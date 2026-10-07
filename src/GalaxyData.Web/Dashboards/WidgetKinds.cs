using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Language;
using GalaxyData.Query.Types;
using GalaxyData.Web.Queries;

namespace GalaxyData.Web.Dashboards;

/// <summary>
/// A kind of widget: its config's type (<see cref="WidgetConfig"/>, named by its <c>kind</c>) and the rules of its
/// structure. Kinds of widgets of data build their queries too (from D2). A kind is added with its config type in
/// <see cref="WidgetConfig"/>'s derived types, a kind here, and the client's registry.
/// </summary>
public interface IWidgetKind
{
   Type ConfigType { get; }

   /// <summary>Checks <paramref name="config"/> (of <see cref="ConfigType"/>) of widget <paramref name="widget"/>, its errors by field under <paramref name="path"/>.</summary>
   void Check(WidgetConfig config, string? widget, DefinitionCheck check, string path);

   /// <summary>The widget's queries; null for a kind with none (text), or with issues (in the context's dashboard) saying why not.</summary>
   WidgetPlan? Plan(WidgetConfig config, PlanContext context);
}

/// <summary>The kinds of widgets, by their configs' types.</summary>
public sealed class WidgetKinds(IEnumerable<IWidgetKind> kinds)
{
   private readonly Dictionary<Type, IWidgetKind> byType = kinds.ToDictionary(k => k.ConfigType);

   public bool TryGet(WidgetConfig config, [NotNullWhen(true)] out IWidgetKind? kind)
   {
      ArgumentNullException.ThrowIfNull(config);
      return byType.TryGetValue(config.GetType(), out kind);
   }

   /// <summary>The kinds the application has.</summary>
   public static IEnumerable<IWidgetKind> BuiltIn() => [new TextKind(), new PieKind(), new BarKind(), new LineKind(), new TableKind()];
}

/// <summary>A kind whose configs are <typeparamref name="T"/>.</summary>
public abstract class WidgetKind<T> : IWidgetKind where T : WidgetConfig
{
   public Type ConfigType => typeof(T);

   public void Check(WidgetConfig config, string? widget, DefinitionCheck check, string path) => Check((T)config, widget, check, path);

   public WidgetPlan? Plan(WidgetConfig config, PlanContext context) => Plan((T)config, context);

   protected abstract void Check(T config, string? widget, DefinitionCheck check, string path);

   protected virtual WidgetPlan? Plan(T config, PlanContext context) => null;
}

/// <summary>Grouped queries over a widget's rows: <c>groupBy(d0: …).select(d0, m0: …).orderBy(…)</c>, names by role and index.</summary>
internal static class GroupQueries
{
   /// <summary>The widget's rows grouped by <paramref name="keys"/> (none: one group of all), with <paramref name="measures"/>, sorted, and the first <paramref name="take"/>.</summary>
   public static string Group(string rows, IReadOnlyList<(string Name, string Expression)> keys, IReadOnlyList<(string Name, string Expression)> measures,
                              IReadOnlyList<string> order, int? take)
   {
      string text = rows + ".groupBy(" + string.Join(", ", keys.Select(k => $"{k.Name}: {k.Expression}")) + ")"
         + ".select(" + string.Join(", ", keys.Select(k => k.Name).Concat(measures.Select(m => $"{m.Name}: {m.Expression}"))) + ")";
      if (order.Count > 0) { text += ".orderBy(" + string.Join(", ", order) + ")"; }
      return take is { } n ? text + ".take(" + n.ToString(CultureInfo.InvariantCulture) + ")" : text;
   }

   public static string Key(string name, bool descending) => descending ? $"desc({name})" : name;

   /// <summary>The keys that break ties: those not sorted by already, but guids (which can't be sorted).</summary>
   public static IEnumerable<string> Ties(IEnumerable<(string Name, ScalarType Type)> keys, IReadOnlyCollection<string> sorted) =>
      keys.Where(k => k.Type.Kind != ScalarKind.Guid && !sorted.Contains(k.Name)).Select(k => k.Name);

   public static int Take(int limit, PlanContext context) => Math.Min(limit, context.Limits.MaxChartRows) + 1;

   public static List<PlannedColumn> Columns(Dimension dimension, Dimension? series, IReadOnlyList<Measure> measures)
   {
      List<PlannedColumn> columns = [new PlannedColumn("d0", ColumnRole.Dimension, 0, dimension.Label, null)];
      if (series != null) { columns.Add(new PlannedColumn("s0", ColumnRole.Series, 0, series.Label, null)); }
      columns.AddRange(measures.Select((m, i) => new PlannedColumn(Name("m", i), ColumnRole.Measure, i, m.Label, m.Format)));
      return columns;
   }

   public static string Name(string role, int index) => role + index.ToString(CultureInfo.InvariantCulture);

   /// <summary>Measures by name, <c>m0</c>, <c>m1</c>, …; null when one can't be written.</summary>
   public static List<(string Name, string Expression)>? Measures(IReadOnlyList<Measure> measures, PlanContext context, string path)
   {
      List<(string, string)> written = [];
      bool ok = true;
      for (int i = 0; i < measures.Count; i++)
      {
         if (context.Measure(measures[i], $"{path}[{i}]") is { } expression) { written.Add((Name("m", i), expression)); }
         else { ok = false; }
      }
      return ok ? written : null;
   }

   /// <summary>The order of a chart's rows: its sort (a dimension or a measure), then its dimensions, as ties.</summary>
   public static List<string> Order(WidgetSort? sort, IReadOnlyList<(string Name, ScalarType Type)> keys, PlanContext context, string path)
   {
      List<string> order = [];
      List<string> sorted = [];
      if (sort != null)
      {
         string name = Name(sort.By == SortTarget.Measure ? "m" : "d", sort.Index);
         if (sort.By == SortTarget.Dimension && keys.FirstOrDefault(k => k.Name == name) is { Type.Kind: ScalarKind.Guid })
         {
            context.Issue(IssueSeverity.Error, "Guids can't be sorted: sort by a measure", path + ".by");
         }
         order.Add(Key(name, sort.Descending));
         sorted.Add(name);
      }
      order.AddRange(Ties(keys, sorted));
      return order;
   }
}

public sealed class TextKind : WidgetKind<TextConfig>
{
   public const int MaxLength = 20_000;

   protected override void Check(TextConfig config, string? widget, DefinitionCheck check, string path)
   {
      if (config.Markdown == null) { check.Error(path + ".markdown", "Give the text (or none)"); }
      else if (config.Markdown.Length > MaxLength) { check.Error(path + ".markdown", $"A text widget has {MaxLength:N0} characters at most"); }
   }
}

public sealed class PieKind : WidgetKind<PieConfig>
{
   public const int MaxSlices = 100;

   protected override void Check(PieConfig config, string? widget, DefinitionCheck check, string path)
   {
      check.Data(config, widget, path);
      check.Dimension(config.Dimension, path + ".dimension");
      check.Measure(config.Measure, path + ".measure");
      check.Limit(config.Limit, path + ".limit", MaxSlices);
      if (config.Other && config.Measure is { Aggregate: not (Aggregate.Count or Aggregate.CountValues or Aggregate.Sum) })
      {
         check.Error(path + ".other", "The rest adds up as one slice only for counts and sums");
      }
      check.Palette(config.Palette, path + ".palette");
   }

   /// <summary>The largest slices, largest first (ties by the dimension); the total of every row's for "Other".</summary>
   protected override WidgetPlan? Plan(PieConfig config, PlanContext context)
   {
      DimensionExpression? dimension = context.Dimension(config.Dimension, context.Path + ".dimension");
      string? measure = context.Measure(config.Measure, context.Path + ".measure");
      if (dimension == null || measure == null) { return null; }
      string rows = context.Base();
      List<(string, ScalarType)> keys = [("d0", dimension.Type)];
      BuiltQuery main = new(GroupQueries.Group(rows, [("d0", dimension.Expression)], [("m0", measure)], ["desc(m0)", .. GroupQueries.Ties(keys, [])],
         GroupQueries.Take(config.Limit, context)), context.Parameters.Values);
      BuiltQuery? total = config.Other ? new BuiltQuery(GroupQueries.Group(rows, [], [("m0", measure)], [], null), context.Parameters.Values) : null;
      return new WidgetPlan(GroupQueries.Columns(config.Dimension, null, [config.Measure]), null, _ => main, total, new BuiltQuery(rows, context.Parameters.Values),
         Math.Min(config.Limit, context.Limits.MaxChartRows), Paged: false);
   }
}

public sealed class BarKind : WidgetKind<BarConfig>
{
   protected override void Check(BarConfig config, string? widget, DefinitionCheck check, string path)
   {
      check.Data(config, widget, path);
      check.Dimension(config.Dimension, path + ".dimension");
      if (config.Series != null) { check.Dimension(config.Series, path + ".series"); }
      check.Measures(config.Measures, path + ".measures", min: 1);
      if (config.Series != null && config.Measures is { Count: > 1 }) { check.Error(path + ".measures", "Bars by a series show one measure"); }
      check.Sort(config.Sort, path + ".sort", [SortTarget.Dimension, SortTarget.Measure], by => by == SortTarget.Dimension ? 1 : config.Measures?.Count ?? 0);
      if (config.Sort == null) { check.Error(path + ".sort", "Say what the bars are sorted by"); }
      check.Limit(config.Limit, path + ".limit", check.Settings.MaxChartRows);
      check.Label(config.XTitle, path + ".xTitle", required: false);
      check.Label(config.YTitle, path + ".yTitle", required: false);
      check.Palette(config.Palette, path + ".palette");
      if (config.ColorBy == BarColorBy.Category && (config.Series != null || config.Measures is { Count: > 1 }))
      {
         check.Error(path + ".colorBy", "Bars take their categories' colours with one measure and no series");
      }
   }

   /// <summary>
   /// The first categories by the sort; by a series, those categories are found first (with the measure over all
   /// series), and the bars are then of those (the null category too, when it is among them).
   /// </summary>
   protected override WidgetPlan? Plan(BarConfig config, PlanContext context)
   {
      DimensionExpression? dimension = context.Dimension(config.Dimension, context.Path + ".dimension");
      DimensionExpression? series = config.Series == null ? null : context.Dimension(config.Series, context.Path + ".series");
      List<(string Name, string Expression)>? measures = GroupQueries.Measures(config.Measures, context, context.Path + ".measures");
      if (dimension == null || (config.Series != null && series == null) || measures == null) { return null; }
      string rows = context.Base();
      int take = GroupQueries.Take(config.Limit, context);
      List<PlannedColumn> columns = GroupQueries.Columns(config.Dimension, config.Series, config.Measures);
      BuiltQuery underlying = new(rows, context.Parameters.Values);
      int limit = Math.Min(config.Limit, context.Limits.MaxChartRows);
      List<string> order = GroupQueries.Order(config.Sort, [("d0", dimension.Type)], context, context.Path + ".sort");
      if (series == null)
      {
         BuiltQuery main = new(GroupQueries.Group(rows, [("d0", dimension.Expression)], measures, order, take), context.Parameters.Values);
         return new WidgetPlan(columns, null, _ => main, null, underlying, limit, Paged: false);
      }
      BuiltQuery categories = new(GroupQueries.Group(rows, [("d0", dimension.Expression)], measures, order, take), context.Parameters.Values);
      QueryParameters values = context.Parameters.Values;
      BuiltQuery Main(CategoryValues? found)
      {
         QueryParameterAllocator parameters = new(values, rows);
         string text = rows;
         if (found != null)
         {
            string row = context.Row(0);
            string member = dimension.On(row);
            List<string> listed = [.. found.Values.Where(v => v != null).Select(v => parameters.Add(v, found.Type))];
            List<string> parts = [];
            if (listed.Count > 0) { parts.Add($"{member} in [{string.Join(", ", listed)}]"); }
            if (found.Values.Any(v => v == null)) { parts.Add($"{member} == null"); }
            text = QueryText.Compose(rows, [$"{row} => " + (parts.Count == 0 ? "false" : string.Join(" or ", parts))]);
         }
         List<(string, ScalarType)> keys = [("d0", dimension.Type), ("s0", series.Type)];
         return new BuiltQuery(GroupQueries.Group(text, [("d0", dimension.Expression), ("s0", series.Expression)], measures, [.. GroupQueries.Ties(keys, [])],
            context.Limits.MaxChartRows + 1), parameters.Values);
      }
      return new WidgetPlan(columns, categories, Main, null, underlying, limit, Paged: false);
   }
}

public sealed class LineKind : WidgetKind<LineConfig>
{
   protected override void Check(LineConfig config, string? widget, DefinitionCheck check, string path)
   {
      check.Data(config, widget, path);
      check.Dimension(config.Dimension, path + ".dimension");
      if (config.Series != null) { check.Dimension(config.Series, path + ".series"); }
      check.Measures(config.Measures, path + ".measures", min: 1);
      if (config.Series != null && config.Measures is { Count: > 1 }) { check.Error(path + ".measures", "Lines by a series show one measure"); }
      check.Limit(config.Limit, path + ".limit", check.Settings.MaxChartRows);
      check.Label(config.XTitle, path + ".xTitle", required: false);
      check.Label(config.YTitle, path + ".yTitle", required: false);
      check.Palette(config.Palette, path + ".palette");
   }

   /// <summary>Points in the order of x (then the series).</summary>
   protected override WidgetPlan? Plan(LineConfig config, PlanContext context)
   {
      DimensionExpression? dimension = context.Dimension(config.Dimension, context.Path + ".dimension");
      DimensionExpression? series = config.Series == null ? null : context.Dimension(config.Series, context.Path + ".series");
      List<(string Name, string Expression)>? measures = GroupQueries.Measures(config.Measures, context, context.Path + ".measures");
      if (dimension == null || (config.Series != null && series == null) || measures == null) { return null; }
      string rows = context.Base();
      List<(string Name, string Expression)> keys = [("d0", dimension.Expression)];
      List<(string, ScalarType)> typed = [("d0", dimension.Type)];
      if (series != null)
      {
         keys.Add(("s0", series.Expression));
         typed.Add(("s0", series.Type));
      }
      BuiltQuery main = new(GroupQueries.Group(rows, keys, measures, [.. GroupQueries.Ties(typed, [])], GroupQueries.Take(config.Limit, context)), context.Parameters.Values);
      return new WidgetPlan(GroupQueries.Columns(config.Dimension, config.Series, config.Measures), null, _ => main, null, new BuiltQuery(rows, context.Parameters.Values),
         Math.Min(config.Limit, context.Limits.MaxChartRows), Paged: false);
   }
}

public sealed class TableKind : WidgetKind<TableConfig>
{
   public const int MaxColumns = 50;

   public const int MaxPageSize = 1000;

   protected override void Check(TableConfig config, string? widget, DefinitionCheck check, string path)
   {
      check.Data(config, widget, path);
      List<Dimension>? dimensions = check.List(config.Dimensions, path + ".dimensions", MaxColumns, "dimensions");
      List<TableColumn>? columns = check.List(config.Columns, path + ".columns", MaxColumns, "columns");
      if (config.Mode == TableMode.Grouped)
      {
         if (dimensions is { Count: 0 }) { check.Error(path + ".dimensions", "Give a dimension"); }
         for (int i = 0; i < (dimensions?.Count ?? 0); i++) { check.Dimension(dimensions![i], $"{path}.dimensions[{i}]"); }
         check.Measures(config.Measures, path + ".measures", min: 0);
         if (columns is { Count: > 0 }) { check.Error(path + ".columns", "A grouped table has dimensions and measures, not columns"); }
         check.Sort(config.Sort, path + ".sort", [SortTarget.Dimension, SortTarget.Measure],
            by => by == SortTarget.Dimension ? dimensions?.Count ?? 0 : config.Measures?.Count ?? 0);
      }
      else
      {
         if (columns is { Count: 0 }) { check.Error(path + ".columns", "Give a column"); }
         for (int i = 0; i < (columns?.Count ?? 0); i++)
         {
            string at = $"{path}.columns[{i}]";
            TableColumn? column = columns![i];
            if (column == null)
            {
               check.Error(at, "A column can't be null");
               continue;
            }
            check.Field(column.Field, at + ".field");
            check.Label(column.Label, at + ".label", required: true);
            check.Format(column.Format, at + ".format");
         }
         if (dimensions is { Count: > 0 }) { check.Error(path + ".dimensions", "A raw table has columns, not dimensions"); }
         if (config.Measures is { Count: > 0 }) { check.Error(path + ".measures", "A raw table has columns, not measures"); }
         else if (config.Measures == null) { check.Error(path + ".measures", "Give the measures (or none)"); }
         check.Sort(config.Sort, path + ".sort", [SortTarget.Column], _ => columns?.Count ?? 0);
      }
      check.Limit(config.PageSize, path + ".pageSize", MaxPageSize);
   }

   /// <summary>Grouped rows, or the source's rows' fields and key; sorted, then by the dimensions (or the key) as ties; paged by the engine.</summary>
   protected override WidgetPlan? Plan(TableConfig config, PlanContext context)
   {
      string rows = context.Base();
      BuiltQuery underlying = new(rows, context.Parameters.Values);
      if (config.Mode == TableMode.Grouped)
      {
         List<(string Name, string Expression)> keys = [];
         List<(string, ScalarType)> typed = [];
         bool ok = true;
         for (int i = 0; i < config.Dimensions.Count; i++)
         {
            if (context.Dimension(config.Dimensions[i], $"{context.Path}.dimensions[{i}]") is { } dimension)
            {
               keys.Add((GroupQueries.Name("d", i), dimension.Expression));
               typed.Add((GroupQueries.Name("d", i), dimension.Type));
            }
            else { ok = false; }
         }
         List<(string Name, string Expression)>? measures = GroupQueries.Measures(config.Measures, context, context.Path + ".measures");
         if (!ok || measures == null) { return null; }
         List<string> order = GroupQueries.Order(config.Sort, typed, context, context.Path + ".sort");
         List<PlannedColumn> columns =
         [
            .. config.Dimensions.Select((d, i) => new PlannedColumn(GroupQueries.Name("d", i), ColumnRole.Dimension, i, d.Label, null)),
            .. config.Measures.Select((m, i) => new PlannedColumn(GroupQueries.Name("m", i), ColumnRole.Measure, i, m.Label, m.Format)),
         ];
         BuiltQuery main = new(GroupQueries.Group(rows, keys, measures, order, null), context.Parameters.Values);
         return new WidgetPlan(columns, null, _ => main, null, underlying, config.PageSize, Paged: true);
      }
      List<string> items = [];
      List<PlannedColumn> planned = [];
      List<(string Name, ScalarType Type)> sortable = [];
      for (int i = 0; i < config.Columns.Count; i++)
      {
         string name = GroupQueries.Name("c", i);
         if (context.Field(config.Columns[i].Field, $"{context.Path}.columns[{i}].field") is not { } field) { continue; }
         items.Add($"{name}: {field.Member(null)}");
         planned.Add(new PlannedColumn(name, ColumnRole.Column, i, config.Columns[i].Label, config.Columns[i].Format));
         sortable.Add((name, field.Type));
      }
      if (planned.Count < config.Columns.Count) { return null; }
      List<string> ties = [];
      if (context.Source.Entity.Key is { } key)
      {
         for (int i = 0; i < key.Columns.Count; i++)
         {
            string name = GroupQueries.Name("k", i);
            items.Add($"{name}: {QueryText.QuoteName(key.Columns[i].Name)}");
            planned.Add(new PlannedColumn(name, ColumnRole.Key, i, key.Columns[i].Name, null));
            if (key.Columns[i].Type.Kind != ScalarKind.Guid) { ties.Add(name); }
         }
      }
      else if (config.Emits)
      {
         context.Issue(IssueSeverity.Warning, $"{context.Source.Label}'s rows have no key: choosing them filters nothing", context.Path + ".emits");
      }
      List<string> sortKeys = [];
      if (config.Sort is { By: SortTarget.Column } sort && sort.Index < sortable.Count)
      {
         if (sortable[sort.Index].Type.Kind == ScalarKind.Guid) { context.Issue(IssueSeverity.Error, "Guids can't be sorted", context.Path + ".sort.index"); }
         sortKeys.Add(GroupQueries.Key(sortable[sort.Index].Name, sort.Descending));
      }
      sortKeys.AddRange(ties);
      string text = rows + ".select(" + string.Join(", ", items) + ")" + (sortKeys.Count > 0 ? ".orderBy(" + string.Join(", ", sortKeys) + ")" : string.Empty);
      return new WidgetPlan(planned, null, _ => new BuiltQuery(text, context.Parameters.Values), null, underlying, config.PageSize, Paged: true);
   }
}
