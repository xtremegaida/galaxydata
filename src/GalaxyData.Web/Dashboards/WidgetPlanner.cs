using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Language;
using GalaxyData.Query.Types;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Queries;

namespace GalaxyData.Web.Dashboards;

/// <summary>A query to run: its text and its parameters' values.</summary>
public sealed record BuiltQuery(string Text, QueryParameters Parameters);

public enum ColumnRole
{
   Dimension,
   Series,
   Measure,

   /// <summary>A raw table's key column, hidden: what choosing its row sends.</summary>
   Key,

   /// <summary>A raw table's column.</summary>
   Column,
}

/// <summary>A column of a widget's rows: its name in the query (<c>d0</c>, <c>s0</c>, <c>m1</c>, <c>c2</c>, <c>k0</c>), its role and index among its role's, and how it shows.</summary>
public sealed record PlannedColumn(string Name, ColumnRole Role, int Index, string Label, NumberFormat? Format);

/// <summary>The categories a series' bars are limited to (the first column of <see cref="WidgetPlan.Categories"/>' rows), of their type.</summary>
public sealed record CategoryValues(IReadOnlyList<object?> Values, ScalarType Type);

/// <summary>
/// The queries of a widget: its rows (<see cref="Main"/>; for bars by a series, of the categories the first query
/// found), the total "Other" is worked out from, and its underlying rows (its source with every condition that
/// reaches it). <see cref="Limit"/> rows (or categories) are shown, one more fetched to tell there are more; tables
/// are paged by the engine instead.
/// </summary>
public sealed record WidgetPlan(
   IReadOnlyList<PlannedColumn> Columns,
   BuiltQuery? Categories,
   Func<CategoryValues?, BuiltQuery> Main,
   BuiltQuery? Total,
   BuiltQuery Underlying,
   int Limit,
   bool Paged);

/// <summary>How much a state may ask: slices in a selection, values in a condition; rows a chart shows; whether it is a public dashboard's.</summary>
public sealed record PlanLimits(int MaxSelectionKeys, int MaxFilterValues, int MaxChartRows, bool Public);

/// <summary>
/// Writes a widget's queries from its config and the dashboard's state. The rows a widget keeps are its source's
/// rows that its own conditions keep, related along the dashboard's links to at least one row of each other source
/// that every condition there keeps: the filters that apply to it, and the slices chosen in the widgets it listens to
/// (never its own). Conditions are lambdas (<c>where(r0 =&gt; …)</c>), so neither a column nor a lambda's row hides
/// a source's name. What is wrong with the state goes into <c>errors</c> by field (400); what is wrong with the
/// definition as the catalog has it is an issue of the widget's (an error stops it).
/// </summary>
public sealed class WidgetPlanner
{
   private readonly DashboardCatalog dashboard;
   private readonly DashboardState state;
   private readonly PlanLimits limits;
   private readonly DateOnly today;
   private readonly Dictionary<string, string[]> errors;

   public WidgetPlanner(DashboardCatalog dashboard, DashboardState state, PlanLimits limits, DateOnly today, Dictionary<string, string[]> errors)
   {
      this.dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
      this.state = state ?? throw new ArgumentNullException(nameof(state));
      this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
      this.today = today;
      this.errors = errors ?? throw new ArgumentNullException(nameof(errors));
   }

   public DashboardCatalog Dashboard => dashboard;

   public PlanLimits Limits => limits;

   /// <summary>The widget's queries; null when it has none (text), or can't be planned: then its issues, or <c>errors</c>, say why.</summary>
   public WidgetPlan? Plan(string widgetId, WidgetKinds kinds)
   {
      ArgumentNullException.ThrowIfNull(kinds);
      int index = dashboard.Definition.Widgets.FindIndex(w => w.Id == widgetId);
      if (index < 0) { return null; }
      DashboardWidget widget = dashboard.Definition.Widgets[index];
      if (widget.Config is not DataWidgetConfig config || !kinds.TryGet(widget.Config, out IWidgetKind? kind)) { return null; }
      int before = ErrorsOf(widget.Id);
      if (dashboard.Source(config.Source) is not { } source)
      {
         dashboard.Issue(IssueSeverity.Error, $"Its source {config.Source} isn't in the catalog", widget.Id, $"widgets[{index}].config.source");
         return null;
      }
      PlanContext context = new(this, widget, index, source, config);
      WidgetPlan? plan = kind.Plan(widget.Config, context);
      return ErrorsOf(widget.Id) > before || errors.Count > 0 ? null : plan;
   }

   /// <summary>The values a filter's field has, most rows first, under every other condition that reaches its source; those holding <paramref name="search"/> when given.</summary>
   public BuiltQuery? FilterValues(string filterId, string? search, int max)
   {
      int index = dashboard.Definition.Filters.FindIndex(f => f.Id == filterId);
      if (index < 0) { return null; }
      DashboardFilter filter = dashboard.Definition.Filters[index];
      if (dashboard.Source(filter.Field.Source) is not { } source) { return null; }
      string issueOf = "filter:" + filter.Id;
      ResolvedField? field = dashboard.Field(source.Id, new FieldRef(filter.Field.Path, filter.Field.Column), $"filters[{index}].field", issueOf);
      if (field == null) { return null; }
      QueryParameterAllocator parameters = new(new QueryParameters(), source.Path);
      ConditionSet conditions = new(this, issueOf, source.Id, parameters);
      conditions.AddFilters(except: filter.Id);
      conditions.AddSelections(listens: null);
      if (!string.IsNullOrWhiteSpace(search)) { conditions.Add(source.Id, row => Search(field, search.Trim(), row, parameters)); }
      string? where = conditions.Where();
      if (ErrorsOf(issueOf) > 0 || errors.Count > 0) { return null; }
      string text = QueryText.Compose(source.Path, where == null ? [] : [where])
         + $".groupBy(v: {Bucketless(field)}).select(v, n: count()).orderBy(desc(n), v).take({max + 1})";
      return new BuiltQuery(text, parameters.Values);
   }

   internal int ErrorsOf(string? widget) => dashboard.Issues.Count(i => i.Severity == IssueSeverity.Error && i.Widget == widget);

   /// <summary>How many fields of the state are wrong so far.</summary>
   internal int StateErrors => errors.Count;

   internal void Error(string field, string message) => errors[field] = errors.TryGetValue(field, out string[]? existing) ? [.. existing, message] : [message];

   internal DashboardState State => state;

   internal DateOnly Today => today;

   /// <summary>Text searched for in a field: within text, ignoring case; numbers and guids equal to it, when it reads as one; nothing else is searched.</summary>
   private static string? Search(ResolvedField field, string search, string row, QueryParameterAllocator parameters)
   {
      string member = field.Member(row);
      ScalarType type = field.Type;
      if (type.Kind == ScalarKind.String) { return $"icontains({member}, {parameters.Add(search, ScalarType.Text())})"; }
      if (!type.IsNumeric && type.Kind != ScalarKind.Guid) { return null; }
      try
      {
         object? value = ValueCodec.Decode(JsonSerializer.SerializeToElement(search), type);
         return $"{member} == {parameters.Add(value, type)}";
      }
      catch (ValueFormatException)
      {
         return "false";
      }
   }

   private static string Bucketless(ResolvedField field) => field.Member(null);
}

/// <summary>
/// The conditions that reach a widget, by source, written as the links are walked from its source. Each is written
/// once, as its row is known; parameters are its query's.
/// </summary>
internal sealed class ConditionSet(WidgetPlanner planner, string? widget, string source, QueryParameterAllocator parameters)
{
   private readonly Dictionary<string, List<Func<string, string?>>> bySource = new(StringComparer.Ordinal);

   private DashboardCatalog Dashboard => planner.Dashboard;

   public QueryParameterAllocator Parameters => parameters;

   /// <summary>A condition on a source's rows, written (once) for the row it is given.</summary>
   public void Add(string sourceId, Func<string, string?> write)
   {
      if (!bySource.TryGetValue(sourceId, out List<Func<string, string?>>? list)) { bySource[sourceId] = list = []; }
      list.Add(write);
   }

   /// <summary>The dashboard's filters that apply to the widget (all but <paramref name="except"/> for a filter's values), with the values the viewer may have set.</summary>
   public void AddFilters(string? except = null)
   {
      DashboardDefinition definition = Dashboard.Definition;
      Dictionary<string, ConditionValue?> given = planner.State.Filters ?? [];
      for (int i = 0; i < definition.Filters.Count; i++)
      {
         DashboardFilter filter = definition.Filters[i];
         if (filter.Id == except || (widget != null && filter.Except.Contains(widget))) { continue; }
         ConditionValue? value = filter.Value;
         string field = $"filters[{i}].value";
         if (filter.Editable && given.TryGetValue(filter.Id, out ConditionValue? set))
         {
            value = set;
            field = $"state.filters.{filter.Id}";
            if (set != null && !Allowed(filter, set, field)) { continue; }
         }
         if (value == null) { continue; }
         int index = i;
         ConditionValue condition = value;
         string at = field;
         Add(filter.Field.Source, row =>
         {
            ResolvedField? resolved = Dashboard.Field(filter.Field.Source, new FieldRef(filter.Field.Path, filter.Field.Column), $"filters[{index}].field", widget);
            return resolved == null ? null : Write(resolved, filter.Label, ConditionSpec.Of(condition), at, row);
         });
      }
   }

   /// <summary>The slices chosen in the widgets listened to (all, when <paramref name="listens"/> is null), never the widget's own.</summary>
   public void AddSelections(Listens? listens)
   {
      DashboardDefinition definition = Dashboard.Definition;
      foreach ((string emitter, SelectionState selection) in planner.State.Selections ?? [])
      {
         int index = definition.Widgets.FindIndex(w => w.Id == emitter);
         if (index < 0 || emitter == widget || definition.Widgets[index].Config is not DataWidgetConfig { Emits: true } config) { continue; }
         if (listens != null && (listens.Mode == ListenMode.None || (listens.Mode == ListenMode.Chosen && !listens.Widgets.Contains(emitter)))) { continue; }
         if (selection == null || selection.Keys == null || selection.Keys.Count == 0) { continue; }
         string field = $"state.selections.{emitter}";
         int max = planner.Limits.MaxSelectionKeys;
         if (selection.Keys.Count > max)
         {
            planner.Error(field + ".keys", $"A selection holds {max} slices at most");
            continue;
         }
         if (Slices(config, index) is not { } dimensions) { continue; }
         if (dimensions.Count == 0) { continue; }
         Add(config.Source, row => Selection(dimensions, selection, field, row));
      }
   }

   /// <summary>The widget's own conditions, on its source.</summary>
   public void AddOwn(DataWidgetConfig config, int index)
   {
      for (int i = 0; i < config.Conditions.Count; i++)
      {
         WidgetCondition condition = config.Conditions[i];
         string path = $"widgets[{index}].config.conditions[{i}]";
         Add(config.Source, row =>
         {
            ResolvedField? resolved = Dashboard.Field(config.Source, condition.Field, path + ".field", widget);
            return resolved == null ? null : Write(resolved, resolved.Name, ConditionSpec.Of(condition.Value), path + ".value", row);
         });
      }
   }

   /// <summary>The condition of the widget's rows, as a lambda (<c>r0 =&gt; …</c>); null when nothing reaches it.</summary>
   public string? Where()
   {
      string row = Dashboard.Row(0);
      return Condition(source, row, 0, parent: null) is { } condition ? $"{row} => {condition}" : null;
   }

   /// <summary>Which sources' rows reach the widget's: those linked to its source, through any others.</summary>
   public HashSet<string> Reached()
   {
      HashSet<string> seen = new(StringComparer.Ordinal) { source };
      Queue<string> next = new([source]);
      while (next.TryDequeue(out string? at))
      {
         foreach (LinkHop hop in Dashboard.HopsFrom(at))
         {
            if (seen.Add(hop.To.Id)) { next.Enqueue(hop.To.Id); }
         }
      }
      return seen;
   }

   private string? Condition(string at, string row, int depth, string? parent)
   {
      List<string> parts = [];
      foreach (Func<string, string?> write in bySource.GetValueOrDefault(at) ?? [])
      {
         if (write(row) is { Length: > 0 } text) { parts.Add(text); }
      }
      foreach (LinkHop hop in Dashboard.HopsFrom(at))
      {
         if (hop.To.Id == parent || !Holds(hop.To.Id, at)) { continue; }
         parts.Add(Hop(hop, 0, row, depth));
      }
      return parts.Count switch
      {
         0 => null,
         1 => parts[0],
         _ => string.Join(" and ", parts.Select(p => p.StartsWith('(') || !p.Contains(" or ", StringComparison.Ordinal) ? p : "(" + p + ")")),
      };
   }

   /// <summary>Whether conditions are on <paramref name="at"/>, or a source beyond it (away from <paramref name="parent"/>).</summary>
   private bool Holds(string at, string parent)
   {
      if (bySource.TryGetValue(at, out List<Func<string, string?>>? here) && here.Count > 0) { return true; }
      return Dashboard.HopsFrom(at).Any(h => h.To.Id != parent && Holds(h.To.Id, at));
   }

   /// <summary>
   /// Along a link, one navigation at a time: to one row, its member (which must be there); to many, <c>any</c> of
   /// them; into a virtual entity from its base, <c>any</c> of its rows matching on the navigation's columns.
   /// </summary>
   private string Hop(LinkHop hop, int step, string row, int depth)
   {
      if (step == hop.Steps.Count) { return Condition(hop.To.Id, row, depth, hop.From.Id) ?? "true"; }
      NavigationDef navigation = hop.Steps[step];
      if (step == hop.Steps.Count - 1 && hop.Correlated)
      {
         string inner = Dashboard.Row(depth + 1);
         IEnumerable<string> match = navigation.TargetColumns.Zip(navigation.OwnerColumns, (target, owner) =>
            $"{QueryText.AppendMember(new System.Text.StringBuilder(inner), target.Name)} == {QueryText.AppendMember(new System.Text.StringBuilder(row), owner.Name)}");
         string rest = Condition(hop.To.Id, inner, depth + 1, hop.From.Id) ?? "true";
         return $"{hop.To.Path}.any({inner} => {string.Join(" and ", match)} and {Parenthesized(rest)})";
      }
      string member = QueryText.AppendMember(new System.Text.StringBuilder(row), navigation.Name).ToString();
      if (!navigation.IsCollection)
      {
         string rest = Hop(hop, step + 1, member, depth);
         return navigation.Multiplicity == Multiplicity.One ? rest : $"({member} != null and {Parenthesized(rest)})";
      }
      string each = Dashboard.Row(depth + 1);
      return $"{member}.any({each} => {Hop(hop, step + 1, each, depth + 1)})";
   }

   private static string Parenthesized(string condition) =>
      condition.StartsWith('(') || !condition.Contains(' ', StringComparison.Ordinal) ? condition : "(" + condition + ")";

   /// <summary>Whether a viewer's value takes the filter's kind of op and its number of values.</summary>
   private bool Allowed(DashboardFilter filter, ConditionValue value, string field)
   {
      ConditionOp[] ops = filter.Kind switch
      {
         FilterKind.Values => [ConditionOp.In, ConditionOp.NotIn],
         FilterKind.Range => [ConditionOp.Between, ConditionOp.Ge, ConditionOp.Gt, ConditionOp.Le, ConditionOp.Lt],
         FilterKind.Relative => [ConditionOp.Relative],
         FilterKind.Text => [ConditionOp.Contains, ConditionOp.StartsWith],
         _ => [ConditionOp.Eq],
      };
      if (!ops.Contains(value.Op))
      {
         planner.Error(field + ".op", $"{filter.Label} takes {string.Join(", ", ops.Select(DefinitionRules.Name))}");
         return false;
      }
      int max = planner.Limits.MaxFilterValues;
      if (value.Values is { } values && (values.Count > max || (!filter.Multiple && values.Count > 1)))
      {
         planner.Error(field + ".values", filter.Multiple ? $"Choose {max} values at most" : $"{filter.Label} takes one value");
         return false;
      }
      return true;
   }

   /// <summary>A condition on a field of the row; its problems the state's (400) or the definition's (an issue).</summary>
   private string? Write(ResolvedField field, string label, ConditionSpec condition, string path, string row)
   {
      Dictionary<string, string[]> found = [];
      string? text = ConditionWriter.Write(field.Member(row), field.Type, label, condition, path, parameters, found, planner.Today);
      foreach ((string at, string[] messages) in found)
      {
         if (at.StartsWith("state.", StringComparison.Ordinal))
         {
            foreach (string message in messages) { planner.Error(at, message); }
         }
         else
         {
            foreach (string message in messages) { Dashboard.Issue(IssueSeverity.Error, message, widget, at); }
         }
      }
      return found.Count > 0 ? null : text;
   }

   /// <summary>The fields a widget's slices are keyed by: its dimensions (and series), a grouped table's dimensions, a raw table's key (not in public dashboards).</summary>
   private List<(ResolvedField Field, Bucket? Bucket)>? Slices(DataWidgetConfig config, int index)
   {
      string path = $"widgets[{index}].config";
      List<(FieldRef Field, Bucket? Bucket, string Path)> wanted = config switch
      {
         PieConfig pie => [(pie.Dimension.Field, pie.Dimension.Bucket, path + ".dimension.field")],
         BarConfig bar => [(bar.Dimension.Field, bar.Dimension.Bucket, path + ".dimension.field"), .. Series(bar.Series, path)],
         LineConfig line => [(line.Dimension.Field, line.Dimension.Bucket, path + ".dimension.field"), .. Series(line.Series, path)],
         TableConfig { Mode: TableMode.Grouped } table => [.. table.Dimensions.Select((d, i) => (d.Field, d.Bucket, $"{path}.dimensions[{i}].field"))],
         TableConfig when !planner.Limits.Public && Dashboard.Source(config.Source) is { Entity.Key: { } key } =>
            [.. key.Columns.Select(c => (new FieldRef([], c.Name), (Bucket?)null, path + ".source"))],
         _ => [],
      };
      List<(ResolvedField, Bucket?)> fields = [];
      foreach ((FieldRef field, Bucket? bucket, string at) in wanted)
      {
         // Another widget's fields: their problems are its own.
         if (Dashboard.Field(config.Source, field, at, Dashboard.Definition.Widgets[index].Id) is not { } resolved) { return null; }
         fields.Add((resolved, bucket));
      }
      return fields;
   }

   private static IEnumerable<(FieldRef, Bucket?, string)> Series(Dimension? series, string path) =>
      series == null ? [] : [(series.Field, series.Bucket, path + ".series.field")];

   /// <summary>
   /// The slices chosen: those keyed so (or, excluding, all but those), as an OR (balanced, as the parser nests 256
   /// deep) of each key's parts. Excluded slices are written so that a row without a value isn't excluded with them.
   /// </summary>
   private string? Selection(List<(ResolvedField Field, Bucket? Bucket)> dimensions, SelectionState selection, string field, string row)
   {
      bool exclude = selection.Mode == SelectionMode.Exclude;
      int before = planner.StateErrors;
      if (dimensions is [(var single, null)]) { return List(single, selection, field, row, exclude, before); }
      List<string> tuples = [];
      for (int k = 0; k < selection.Keys.Count; k++)
      {
         List<object?>? key = selection.Keys[k];
         string at = $"{field}.keys[{k}]";
         if (key == null || key.Count != dimensions.Count)
         {
            planner.Error(at, $"A slice's key has {dimensions.Count} value{(dimensions.Count == 1 ? string.Empty : "s")}");
            continue;
         }
         List<string> parts = [];
         for (int d = 0; d < dimensions.Count; d++)
         {
            if (Part(dimensions[d].Field, dimensions[d].Bucket, key[d], guarded: exclude, $"{at}[{d}]", row) is { } part) { parts.Add(part); }
         }
         if (parts.Count == dimensions.Count) { tuples.Add(parts.Count == 1 ? parts[0] : "(" + string.Join(" and ", parts) + ")"); }
      }
      if (planner.StateErrors > before || tuples.Count == 0) { return null; }
      string any = Or(tuples, 0, tuples.Count);
      return exclude ? $"not {Wrap(any)}" : any;
   }

   /// <summary>Slices of one field, as they are: one list (<c>x in [$f1, $f2]</c>), and <c>x == null</c> for the slice without a value.</summary>
   private string? List(ResolvedField field, SelectionState selection, string at, string row, bool exclude, int before)
   {
      string member = field.Member(row);
      List<string> values = [];
      bool nulls = false;
      for (int k = 0; k < selection.Keys.Count; k++)
      {
         List<object?>? key = selection.Keys[k];
         if (key is not [var value])
         {
            planner.Error($"{at}.keys[{k}]", "A slice's key has 1 value");
            continue;
         }
         JsonElement json = value == null ? default : ValueCodec.Json(value);
         if (value == null || json.ValueKind == JsonValueKind.Null)
         {
            nulls = true;
            continue;
         }
         try
         {
            values.Add(parameters.Add(ValueCodec.Decode(json, field.Type), field.Type));
         }
         catch (ValueFormatException e)
         {
            planner.Error($"{at}.keys[{k}][0]", e.Message);
         }
      }
      if (planner.StateErrors > before) { return null; }
      List<string> parts = [];
      if (values.Count > 0)
      {
         string list = values.Count == 1 ? $"{member} == {values[0]}" : $"{member} in [{string.Join(", ", values)}]";
         parts.Add(exclude ? $"({member} != null and {list})" : list);
      }
      if (nulls) { parts.Add($"{member} == null"); }
      string any = parts.Count == 1 ? parts[0] : "(" + string.Join(" or ", parts) + ")";
      return exclude ? $"not {Wrap(any)}" : any;
   }

   private static string Wrap(string condition) => condition.StartsWith('(') && condition.EndsWith(')') ? condition : "(" + condition + ")";

   private static string Or(List<string> parts, int from, int to)
   {
      if (to - from == 1) { return parts[from]; }
      int middle = (from + to) / 2;
      return "(" + Or(parts, from, middle) + " or " + Or(parts, middle, to) + ")";
   }

   /// <summary>One part of a key: the field's value (a period's: from its first day to the next's; a part that repeats: the function's value), or none.</summary>
   private string? Part(ResolvedField field, Bucket? bucket, object? value, bool guarded, string at, string row)
   {
      string member = field.Member(row);
      JsonElement json = value == null ? default : ValueCodec.Json(value);
      if (value == null || json.ValueKind == JsonValueKind.Null) { return $"{member} == null"; }
      string guard = guarded ? $"{member} != null and " : string.Empty;
      try
      {
         switch (bucket)
         {
            case null:
            {
               string parameter = parameters.Add(ValueCodec.Decode(json, field.Type), field.Type);
               return guarded ? $"({guard}{member} == {parameter})" : $"{member} == {parameter}";
            }
            case Bucket.Day or Bucket.Week or Bucket.Month or Bucket.Quarter or Bucket.Year:
            {
               if (!ValueCodec.IsDateOnly(json, out DateOnly start) || !Aligned(start, bucket.Value))
               {
                  planner.Error(at, $"A {DefinitionRules.Name(bucket.Value)}'s slice is its first day (yyyy-MM-dd)");
                  return null;
               }
               DateOnly? end = Next(start, bucket.Value);
               string from = parameters.Add(ConditionWriter.DayStart(start, field.Type), field.Type);
               return end is { } until
                  ? $"({guard}{member} >= {from} and {member} < {parameters.Add(ConditionWriter.DayStart(until, field.Type), field.Type)})"
                  : $"({guard}{member} >= {from})";
            }
            default:
            {
               string function = bucket switch { Bucket.QuarterOfYear => "quarter", Bucket.MonthOfYear => "month", Bucket.DayOfWeek => "dayOfWeek", _ => "hour" };
               string parameter = parameters.Add(ValueCodec.Decode(json, ScalarType.Int32), ScalarType.Int32);
               return $"({guard}{function}({member}) == {parameter})";
            }
         }
      }
      catch (ValueFormatException e)
      {
         planner.Error(at, e.Message);
         return null;
      }
   }

   private static bool Aligned(DateOnly day, Bucket bucket) => bucket switch
   {
      Bucket.Week => day.DayOfWeek == DayOfWeek.Monday,
      Bucket.Month => day.Day == 1,
      Bucket.Quarter => day.Day == 1 && day.Month % 3 == 1,
      Bucket.Year => day.Day == 1 && day.Month == 1,
      _ => true,
   };

   private static DateOnly? Next(DateOnly start, Bucket bucket) => ConditionWriter.Plus(start, bucket switch
   {
      Bucket.Day => RelativeUnit.Day,
      Bucket.Week => RelativeUnit.Week,
      Bucket.Month => RelativeUnit.Month,
      Bucket.Quarter => RelativeUnit.Quarter,
      _ => RelativeUnit.Year,
   }, 1);
}

/// <summary>A dimension as the query groups by it: over the implicit row, and over a lambda's (<see cref="On"/>), and the type of its values.</summary>
public sealed record DimensionExpression(string Expression, ScalarType Type, Func<string?, string> Written)
{
   public string On(string row) => Written(row);
}

/// <summary>What a kind plans a widget with: its source, its conditions, the expressions of its dimensions and measures, and its issues.</summary>
public sealed class PlanContext
{
   private readonly WidgetPlanner planner;
   private string? where;
   private bool written;

   internal PlanContext(WidgetPlanner planner, DashboardWidget widget, int index, ResolvedSource source, DataWidgetConfig config)
   {
      this.planner = planner;
      Widget = widget;
      Index = index;
      Source = source;
      Path = $"widgets[{index}].config";
      Parameters = new QueryParameterAllocator(new QueryParameters(), source.Path);
      Conditions = new ConditionSet(planner, widget.Id, source.Id, Parameters);
      Conditions.AddFilters();
      Conditions.AddSelections(config.Listens);
      Conditions.AddOwn(config, index);
      foreach (string emitter in Unreached(config)) { Issue(IssueSeverity.Warning, $"Choices in {emitter} don't reach it: link their sources", Path + ".listens"); }
   }

   public DashboardWidget Widget { get; }

   public int Index { get; }

   public ResolvedSource Source { get; }

   /// <summary>The config's path in the definition: <c>widgets[2].config</c>.</summary>
   public string Path { get; }

   public PlanLimits Limits => planner.Limits;

   public QueryParameterAllocator Parameters { get; }

   internal ConditionSet Conditions { get; }

   /// <summary>The widget's rows: its source, with the conditions that reach it.</summary>
   public string Base()
   {
      if (!written)
      {
         where = Conditions.Where();
         written = true;
      }
      return QueryText.Compose(Source.Path, where == null ? [] : [where]);
   }

   public void Issue(IssueSeverity severity, string message, string field) => planner.Dashboard.Issue(severity, message, Widget.Id, field);

   public ResolvedField? Field(FieldRef field, string path) => planner.Dashboard.Field(Source.Id, field, path, Widget.Id);

   /// <summary>A name for a lambda's row nested <paramref name="depth"/> deep, that hides nothing of the catalog's.</summary>
   public string Row(int depth) => planner.Dashboard.Row(depth);

   /// <summary>A dimension's expression over the implicit row (its period's or part's function for buckets), and the type it gives; null with an issue when it can't group.</summary>
   public DimensionExpression? Dimension(Dimension dimension, string path)
   {
      ArgumentNullException.ThrowIfNull(dimension);
      if (Field(dimension.Field, path + ".field") is not { } field) { return null; }
      ScalarType type = field.Type;
      if (type.Kind is ScalarKind.Json or ScalarKind.Unknown or ScalarKind.Binary)
      {
         Issue(IssueSeverity.Error, $"{field.Name} is {type.WithNullable(false)}, which can't be grouped", path + ".field");
         return null;
      }
      if (dimension.Bucket is not { } bucket)
      {
         if (type.Kind is ScalarKind.DateTime or ScalarKind.DateTimeOffset)
         {
            Issue(IssueSeverity.Error, $"{field.Name} is a date-time: group it by a period (a day, a month)", path + ".bucket");
            return null;
         }
         return new DimensionExpression(field.Member(null), type, field.Member);
      }
      bool hours = bucket == Bucket.HourOfDay;
      if (hours ? !TypeRules.HasTimeOfDay(type.Kind) || type.Kind == ScalarKind.Time : !TypeRules.IsDateLike(type.Kind))
      {
         Issue(IssueSeverity.Error, $"{field.Name} is {type.WithNullable(false)}, which has no {DefinitionRules.Name(bucket)}", path + ".bucket");
         return null;
      }
      (string function, ScalarType result) = bucket switch
      {
         Bucket.Day => ("date", ScalarType.Date),
         Bucket.Week => ("startOfWeek", ScalarType.Date),
         Bucket.Month => ("startOfMonth", ScalarType.Date),
         Bucket.Quarter => ("startOfQuarter", ScalarType.Date),
         Bucket.Year => ("startOfYear", ScalarType.Date),
         Bucket.QuarterOfYear => ("quarter", ScalarType.Int32),
         Bucket.MonthOfYear => ("month", ScalarType.Int32),
         Bucket.DayOfWeek => ("dayOfWeek", ScalarType.Int32),
         _ => ("hour", ScalarType.Int32),
      };
      return new DimensionExpression($"{function}({field.Member(null)})", result, row => $"{function}({field.Member(row)})");
   }

   /// <summary>A measure's aggregate over a group's rows; null with an issue when its field can't be aggregated so.</summary>
   public string? Measure(Measure measure, string path)
   {
      ArgumentNullException.ThrowIfNull(measure);
      if (measure.Aggregate == Aggregate.Count) { return "count()"; }
      if (measure.Field == null || Field(measure.Field, path + ".field") is not { } field) { return null; }
      ScalarType type = field.Type;
      bool fits = measure.Aggregate switch
      {
         Aggregate.Sum or Aggregate.Avg => type.IsNumeric,
         Aggregate.Min or Aggregate.Max => type.IsNumeric || type.IsTemporal || type.Kind is ScalarKind.String or ScalarKind.Interval,
         Aggregate.CountDistinct => type.Kind is not (ScalarKind.Json or ScalarKind.Unknown),
         _ => true,
      };
      if (!fits)
      {
         Issue(IssueSeverity.Error, $"{field.Name} is {type.WithNullable(false)}, which has no {DefinitionRules.Name(measure.Aggregate)}", path + ".aggregate");
         return null;
      }
      if (measure.Aggregate is Aggregate.Sum or Aggregate.Avg && field.Steps.Count > 0)
      {
         Issue(IssueSeverity.Warning, $"Each row of {field.Steps[^1].Target.DisplayName} counts once for each of {Source.Label}'s rows it is reached from", path + ".field");
      }
      string member = field.Member(null);
      return measure.Aggregate switch
      {
         Aggregate.CountValues => $"count({planner.Dashboard.Row(0)} => {field.Member(planner.Dashboard.Row(0))} != null)",
         Aggregate.CountDistinct => $"countDistinct({member})",
         Aggregate.Sum => $"sum({member})",
         Aggregate.Avg => $"avg({member})",
         Aggregate.Min => $"min({member})",
         _ => $"max({member})",
      };
   }

   /// <summary>Emitters the widget listens to by name whose sources aren't linked to its own.</summary>
   private IEnumerable<string> Unreached(DataWidgetConfig config)
   {
      if (config.Listens.Mode != ListenMode.Chosen) { return []; }
      HashSet<string> reached = Conditions.Reached();
      return config.Listens.Widgets.Where(id =>
         planner.Dashboard.Definition.Widgets.FirstOrDefault(w => w.Id == id)?.Config is DataWidgetConfig other && !reached.Contains(other.Source));
   }
}
