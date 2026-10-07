using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GalaxyData.Query.Catalog;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Hosting;

namespace GalaxyData.Web.Dashboards;

/// <summary>Definitions as JSON: as the API reads and writes them, and as they are stored (canonical, so equal hashes mean equal definitions).</summary>
public static class DefinitionJson
{
   public static readonly JsonSerializerOptions Options = Create();

   public static string Write(DashboardDefinition definition) => JsonSerializer.Serialize(definition, Options);

   public static DashboardDefinition Read(string json) =>
      JsonSerializer.Deserialize<DashboardDefinition>(json, Options) ?? throw new JsonException("A definition can't be null");

   /// <summary>SHA-256 of the JSON, in lower-case hex.</summary>
   public static string Hash(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

   /// <summary>As the API's JSON options (see <c>WebApp</c>): enums by name only, numbers as numbers, a widget's kind anywhere in its config.</summary>
   public static void Configure(JsonSerializerOptions options)
   {
      ArgumentNullException.ThrowIfNull(options);
      options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
      options.NumberHandling = JsonNumberHandling.Strict;
      options.AllowOutOfOrderMetadataProperties = true;
   }

   private static JsonSerializerOptions Create()
   {
      JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
      Configure(options);
      return options;
   }
}

/// <summary>
/// The structure of definitions, checked as they are saved: ids and the references between lists, kinds, sizes and
/// limits, breakpoints, and links that make a forest. What is wrong is given by field, as the JSON names it
/// (<c>definition.widgets[2].config.measures[0].aggregate</c>). Whether entities, columns and navigations exist, and
/// fit, is for when the dashboard is shown: the catalog changes after a save.
/// </summary>
public sealed partial class DefinitionRules(WidgetKinds kinds, DashboardSettings settings)
{
   /// <summary>The ids of breakpoints, sources, filters and widgets.</summary>
   [GeneratedRegex("^[a-z0-9-]{1,32}$")]
   public static partial Regex IdPattern();

   public const int LabelLength = 200;

   public const int MaxBreakpoints = 6;

   public const int MaxColumns = 24;

   public const int MaxPathLength = 5;

   public const int MaxConditions = 20;

   public const int MaxMeasures = 10;

   /// <summary>
   /// The definition, checked, in its canonical form (cells by widget id, numbers as JSON writes them shortest); null
   /// when something is wrong, in <paramref name="errors"/> by field under <paramref name="prefix"/>.
   /// </summary>
   public DashboardDefinition? Check(DashboardDefinition? definition, string prefix, Dictionary<string, string[]> errors)
   {
      ArgumentNullException.ThrowIfNull(errors);
      DefinitionCheck check = new(settings, errors, prefix);
      if (definition == null)
      {
         check.Error(string.Empty, "Give the definition");
         return null;
      }
      int before = errors.Count;
      if (definition.Schema != DashboardDefinition.CurrentSchema) { check.Error("schema", $"The definition's schema is {DashboardDefinition.CurrentSchema}"); }
      // Lists too long are said once; their items are checked all the same, so nothing else is wrong for want of them.
      check.List(definition.Sources, "sources", settings.MaxSources, "sources");
      for (int i = 0; i < (definition.Sources?.Count ?? 0); i++) { Source(check, definition.Sources![i], $"sources[{i}]"); }
      check.List(definition.Widgets, "widgets", settings.MaxWidgets, "widgets");
      for (int i = 0; i < (definition.Widgets?.Count ?? 0); i++) { check.NewId(definition.Widgets![i]?.Id, $"widgets[{i}].id", check.WidgetIds, "widget"); }
      check.List(definition.Filters, "filters", settings.MaxFilters, "filters");
      for (int i = 0; i < (definition.Filters?.Count ?? 0); i++) { Filter(check, definition.Filters![i], $"filters[{i}]"); }
      check.List(definition.Links, "links", settings.MaxSources, "links");
      if (definition.Links != null) { Links(check, definition.Links); }
      for (int i = 0; i < (definition.Widgets?.Count ?? 0); i++) { Widget(check, definition.Widgets![i], $"widgets[{i}]"); }
      Layout(check, definition.Layout, "layout");
      Refresh(check, definition.Refresh, "refresh");
      if (definition.Public == null) { check.Error("public", "Say what a public dashboard's viewers may see"); }
      if (errors.Count > before) { return null; }

      DashboardDefinition canonical = Canonical(definition);
      if (DefinitionJson.Write(canonical).Length > settings.MaxDefinitionLength)
      {
         check.Error(string.Empty, $"A definition has {settings.MaxDefinitionLength:N0} characters at most, as JSON");
         return null;
      }
      return canonical;
   }

   private static void Source(DefinitionCheck check, DashboardSource? source, string path)
   {
      if (source == null)
      {
         check.Error(path, "A source can't be null");
         return;
      }
      check.NewId(source.Id, path + ".id", check.SourceIds, "source");
      if (string.IsNullOrWhiteSpace(source.Entity)) { check.Error(path + ".entity", "Name the source's entity"); }
      else if (source.Entity.Length > Metadata.MetadataDb.PathLength || !EntityName.TryParse(source.Entity, out _))
      {
         check.Error(path + ".entity", $"'{source.Entity}' isn't an entity's path, as queries write it (shop.orders)");
      }
      check.Label(source.Label, path + ".label", required: true);
   }

   private static void Filter(DefinitionCheck check, DashboardFilter? filter, string path)
   {
      if (filter == null)
      {
         check.Error(path, "A filter can't be null");
         return;
      }
      check.NewId(filter.Id, path + ".id", check.FilterIds, "filter");
      check.Label(filter.Label, path + ".label", required: true);
      if (filter.Field == null) { check.Error(path + ".field", "Give the filter's field"); }
      else
      {
         check.SourceRef(filter.Field.Source, path + ".field.source");
         check.Field(new FieldRef(filter.Field.Path, filter.Field.Column), path + ".field");
      }
      if (!filter.Visible && filter.Editable) { check.Error(path + ".editable", "A hidden filter can't be changed by viewers"); }
      if (filter.Value is { } value)
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
            check.Error(path + ".value.op", $"A {Name(filter.Kind)} filter's op is {string.Join(", ", ops.Select(Name))}");
         }
         check.Condition(value, path + ".value");
         if (!filter.Multiple && value.Values is { Count: > 1 }) { check.Error(path + ".value.values", "The filter takes one value"); }
      }
      if (check.List(filter.Except, path + ".except", int.MaxValue, "widgets") is { } except)
      {
         for (int i = 0; i < except.Count; i++) { check.WidgetRef(except[i], $"{path}.except[{i}]"); }
      }
   }

   /// <summary>Links between sources that exist, each pair once, making no cycle: then every two sources are related one way at most.</summary>
   private static void Links(DefinitionCheck check, List<DashboardLink> links)
   {
      Dictionary<string, string> roots = new(StringComparer.Ordinal);
      string Root(string id)
      {
         while (roots.TryGetValue(id, out string? up) && up != id) { id = up; }
         return id;
      }
      for (int i = 0; i < links.Count; i++)
      {
         string path = $"links[{i}]";
         DashboardLink? link = links[i];
         if (link == null)
         {
            check.Error(path, "A link can't be null");
            continue;
         }
         bool known = check.SourceRef(link.From, path + ".from") & check.SourceRef(link.To, path + ".to");
         if (check.List(link.Path, path + ".path", MaxPathLength, "steps") is { } steps)
         {
            for (int s = 0; s < steps.Count; s++) { check.Name(steps[s], $"{path}.path[{s}]", "a navigation"); }
         }
         if (!known) { continue; }
         if (link.From == link.To)
         {
            check.Error(path + ".to", "A link is between two sources");
            continue;
         }
         string from = Root(link.From), to = Root(link.To);
         if (from == to)
         {
            check.Error(path, $"{link.From} and {link.To} are linked already (through other links, or this one twice); a link more would make a cycle");
            continue;
         }
         roots[from] = to;
      }
   }

   private void Widget(DefinitionCheck check, DashboardWidget? widget, string path)
   {
      if (widget == null)
      {
         check.Error(path, "A widget can't be null");
         return;
      }
      if (widget.Title is { } title) { check.Label(title, path + ".title", required: false); }
      if (widget.Config == null)
      {
         check.Error(path + ".config", "Say what the widget shows");
         return;
      }
      if (!kinds.TryGet(widget.Config, out IWidgetKind? kind))
      {
         check.Error(path + ".config", "This kind of widget isn't one the application has");
         return;
      }
      kind.Check(widget.Config, widget.Id, check, path + ".config");
   }

   private static void Layout(DefinitionCheck check, DashboardLayout? layout, string path)
   {
      if (layout == null)
      {
         check.Error(path, "Give the layout");
         return;
      }
      if (layout.RowHeight is < 16 or > 400) { check.Error(path + ".rowHeight", "A row is 16 to 400 pixels high"); }
      if (layout.Gap is < 0 or > 64) { check.Error(path + ".gap", "The gap is 0 to 64 pixels"); }
      List<Breakpoint>? breakpoints = check.List(layout.Breakpoints, path + ".breakpoints", MaxBreakpoints, "breakpoints");
      HashSet<string> ids = new(StringComparer.Ordinal);
      int designed = MaxColumns;
      if (breakpoints is { Count: 0 }) { check.Error(path + ".breakpoints", "Give at least one breakpoint"); }
      else if (breakpoints != null)
      {
         for (int i = 0; i < breakpoints.Count; i++)
         {
            string at = $"{path}.breakpoints[{i}]";
            Breakpoint? breakpoint = breakpoints[i];
            if (breakpoint == null)
            {
               check.Error(at, "A breakpoint can't be null");
               continue;
            }
            check.NewId(breakpoint.Id, at + ".id", ids, "breakpoint");
            check.Label(breakpoint.Label, at + ".label", required: true, max: 40);
            if (i == 0 && breakpoint.MinWidth != 0) { check.Error(at + ".minWidth", "The first breakpoint is from 0 pixels"); }
            else if (i > 0 && breakpoints[i - 1] is { } before && breakpoint.MinWidth <= before.MinWidth)
            {
               check.Error(at + ".minWidth", "Breakpoints go from the narrowest to the widest");
            }
            if (breakpoint.MinWidth is < 0 or > 10_000) { check.Error(at + ".minWidth", "A breakpoint is from 0 to 10,000 pixels"); }
            if (breakpoint.Columns is < 1 or > MaxColumns) { check.Error(at + ".columns", $"A breakpoint has 1 to {MaxColumns} columns"); }
         }
         designed = breakpoints[^1]?.Columns ?? MaxColumns;
      }
      if (layout.Items == null) { check.Error(path + ".items", "Give the widgets' cells"); }
      else
      {
         foreach (string widget in check.WidgetIds.Where(w => !layout.Items.ContainsKey(w))) { check.Error($"{path}.items.{widget}", "Give the widget's cell"); }
         foreach ((string widget, Placement? cell) in layout.Items) { Cell(check, cell, widget, Math.Clamp(designed, 1, MaxColumns), $"{path}.items.{widget}"); }
      }
      if (layout.Overrides == null) { check.Error(path + ".overrides", "Give the breakpoints laid out by hand (or none)"); }
      else
      {
         foreach ((string id, LayoutOverride? custom) in layout.Overrides)
         {
            string at = $"{path}.overrides.{id}";
            if (!ids.Contains(id)) { check.Error(at, $"There is no breakpoint {id}"); }
            else if (breakpoints?[^1]?.Id == id) { check.Error(at, "The widest breakpoint is the one designed; it isn't overridden"); }
            if (custom == null)
            {
               check.Error(at, "An override can't be null");
               continue;
            }
            if (custom.Columns is < 1 or > MaxColumns) { check.Error(at + ".columns", $"A breakpoint has 1 to {MaxColumns} columns"); }
            if (custom.Items == null) { check.Error(at + ".items", "Give the widgets' cells"); }
            else
            {
               foreach ((string widget, Placement? cell) in custom.Items) { Cell(check, cell, widget, Math.Clamp(custom.Columns, 1, MaxColumns), $"{at}.items.{widget}"); }
            }
            if (check.List(custom.Hidden, at + ".hidden", int.MaxValue, "widgets") is { } hidden)
            {
               for (int i = 0; i < hidden.Count; i++) { check.WidgetRef(hidden[i], $"{at}.hidden[{i}]"); }
            }
         }
      }
   }

   private static void Cell(DefinitionCheck check, Placement? cell, string widget, int columns, string path)
   {
      if (!check.WidgetIds.Contains(widget)) { check.Error(path, $"There is no widget {widget}"); }
      if (cell == null)
      {
         check.Error(path, "A cell can't be null");
         return;
      }
      if (cell.W < 1 || cell.W > columns) { check.Error(path + ".w", $"A widget is 1 to {columns} columns wide here"); }
      if (cell.X < 0 || cell.X + Math.Max(cell.W, 1) > columns) { check.Error(path + ".x", $"The widget is within the {columns} columns"); }
      if (cell.Y is < 0 or > 10_000) { check.Error(path + ".y", "A widget's row is 0 to 10,000"); }
      if (cell.H is < 1 or > 200) { check.Error(path + ".h", "A widget is 1 to 200 rows high"); }
   }

   private static void Refresh(DefinitionCheck check, RefreshPolicy? refresh, string path)
   {
      if (refresh == null)
      {
         check.Error(path, "Give the refresh policy");
         return;
      }
      if (refresh.Mode == RefreshMode.Interval)
      {
         if (refresh.Seconds is not { } seconds || seconds < check.Settings.MinRefreshSeconds || seconds > 86_400)
         {
            check.Error(path + ".seconds", $"Refresh every {check.Settings.MinRefreshSeconds} to 86,400 seconds");
         }
      }
      else if (refresh.Seconds != null) { check.Error(path + ".seconds", "A dashboard refreshed by hand has no interval"); }
   }

   /// <summary>Cells in the order of their widgets' ids, and numbers as JSON writes them shortest.</summary>
   private static DashboardDefinition Canonical(DashboardDefinition definition)
   {
      static Dictionary<string, Placement> Cells(Dictionary<string, Placement> cells) =>
         cells.OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);

      DashboardLayout layout = definition.Layout with
      {
         Items = Cells(definition.Layout.Items),
         Overrides = definition.Layout.Overrides.OrderBy(o => o.Key, StringComparer.Ordinal)
            .ToDictionary(o => o.Key, o => o.Value with { Items = Cells(o.Value.Items) }, StringComparer.Ordinal),
      };
      return definition with
      {
         Layout = layout,
         Filters = [.. definition.Filters.Select(f => f with { Value = Canonical(f.Value) })],
         Widgets = [.. definition.Widgets.Select(w => w with { Config = w.Config is DataWidgetConfig data ? Canonical(data) : w.Config })],
      };
   }

   private static WidgetConfig Canonical(DataWidgetConfig config) =>
      config with { Conditions = [.. config.Conditions.Select(c => c with { Value = Canonical(c.Value)! })] };

   private static ConditionValue? Canonical(ConditionValue? value) => value == null ? null : value with
   {
      Value = Canonical(value.Value),
      ValueTo = Canonical(value.ValueTo),
      Values = value.Values?.Select(Canonical).ToList(),
   };

   private static object? Canonical(object? value)
   {
      if (value == null) { return null; }
      JsonElement json = ValueCodec.ShortestNumber(ValueCodec.Json(value));
      return json.ValueKind == JsonValueKind.Null ? null : json;
   }

   internal static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}

/// <summary>What a definition's check knows as it goes: the ids met, and the errors found, by field.</summary>
public sealed class DefinitionCheck(DashboardSettings settings, Dictionary<string, string[]> errors, string prefix)
{
   public DashboardSettings Settings => settings;

   public HashSet<string> SourceIds { get; } = new(StringComparer.Ordinal);

   public HashSet<string> WidgetIds { get; } = new(StringComparer.Ordinal);

   public HashSet<string> FilterIds { get; } = new(StringComparer.Ordinal);

   public void Error(string path, string message)
   {
      string key = path.Length == 0 ? prefix.TrimEnd('.') : prefix + path;
      errors[key] = errors.TryGetValue(key, out string[]? existing) ? [.. existing, message] : [message];
   }

   /// <summary>The list, unless it is null or longer than <paramref name="max"/> (which are errors).</summary>
   public List<T>? List<T>(List<T>? list, string path, int max, string what)
   {
      if (list == null)
      {
         Error(path, $"Give the {what} (or none)");
         return null;
      }
      if (list.Count > max)
      {
         Error(path, $"There are {max:N0} {what} at most");
         return null;
      }
      return list;
   }

   /// <summary>An id of the pattern, new among <paramref name="ids"/>.</summary>
   public void NewId(string? id, string path, HashSet<string> ids, string what)
   {
      if (id == null || !DefinitionRules.IdPattern().IsMatch(id)) { Error(path, $"A {what}'s id is 1 to 32 lower-case letters, digits and hyphens"); }
      else if (!ids.Add(id)) { Error(path, $"There is another {what} {id}"); }
   }

   public bool SourceRef(string? id, string path)
   {
      if (id != null && SourceIds.Contains(id)) { return true; }
      Error(path, $"There is no source {id}");
      return false;
   }

   public void WidgetRef(string? id, string path)
   {
      if (id == null || !WidgetIds.Contains(id)) { Error(path, $"There is no widget {id}"); }
   }

   public void Label(string? text, string path, bool required, int max = DefinitionRules.LabelLength)
   {
      if (string.IsNullOrWhiteSpace(text))
      {
         if (required || text != null) { Error(path, "Give it a label"); }
      }
      else if (text.Length > max) { Error(path, $"A label has {max} characters at most"); }
   }

   /// <summary>A column's or navigation's name: as the catalog has it, so any text, but not empty or long.</summary>
   public void Name(string? name, string path, string what)
   {
      if (string.IsNullOrWhiteSpace(name)) { Error(path, $"Name {what}"); }
      else if (name.Length > Metadata.MetadataDb.NameLength) { Error(path, $"A name has {Metadata.MetadataDb.NameLength} characters at most"); }
   }

   public void Field(FieldRef? field, string path)
   {
      if (field == null)
      {
         Error(path, "Give the field");
         return;
      }
      if (List(field.Path, path + ".path", DefinitionRules.MaxPathLength, "navigations") is { } steps)
      {
         for (int i = 0; i < steps.Count; i++) { Name(steps[i], $"{path}.path[{i}]", "the navigation"); }
      }
      Name(field.Column, path + ".column", "the column");
   }

   /// <summary>A condition's values: as many as its op takes, each a JSON value that isn't a list or an object.</summary>
   public void Condition(ConditionValue? condition, string path)
   {
      if (condition == null)
      {
         Error(path, "Give the condition");
         return;
      }
      void Scalar(object? value, string at, bool required)
      {
         JsonValueKind kind = value == null ? JsonValueKind.Null : Browse.ValueCodec.Json(value).ValueKind;
         if (kind is JsonValueKind.Object or JsonValueKind.Array) { Error(at, "A value is a string, number, true, false or null"); }
         else if (required && kind == JsonValueKind.Null) { Error(at, "Give the value"); }
      }
      switch (condition.Op)
      {
         case ConditionOp.Blank or ConditionOp.NotBlank:
            break;
         case ConditionOp.In or ConditionOp.NotIn:
            if (condition.Values == null) { Error(path + ".values", "Give the values"); }
            else if (condition.Values.Count > settings.MaxFilterValues) { Error(path + ".values", $"A condition lists {settings.MaxFilterValues:N0} values at most"); }
            else
            {
               for (int i = 0; i < condition.Values.Count; i++) { Scalar(condition.Values[i], $"{path}.values[{i}]", required: false); }
            }
            break;
         case ConditionOp.Between:
            Scalar(condition.Value, path + ".value", required: true);
            Scalar(condition.ValueTo, path + ".valueTo", required: true);
            break;
         case ConditionOp.Relative:
            if (condition.Relative == null) { Error(path + ".relative", "Give the period"); }
            else if (condition.Relative.Count is < 1 or > 1000) { Error(path + ".relative.count", "Count 1 to 1,000 periods"); }
            break;
         default:
            Scalar(condition.Value, path + ".value", required: true);
            break;
      }
   }

   public void Dimension(Dimension? dimension, string path)
   {
      if (dimension == null)
      {
         Error(path, "Give the dimension");
         return;
      }
      Field(dimension.Field, path + ".field");
      Label(dimension.Label, path + ".label", required: true);
   }

   public void Measure(Measure? measure, string path)
   {
      if (measure == null)
      {
         Error(path, "Give the measure");
         return;
      }
      if (measure.Aggregate == Aggregate.Count)
      {
         if (measure.Field != null) { Error(path + ".field", "Counting rows takes no field"); }
      }
      else { Field(measure.Field, path + ".field"); }
      Label(measure.Label, path + ".label", required: true);
      Format(measure.Format, path + ".format");
   }

   public void Format(NumberFormat? format, string path)
   {
      if (format == null) { return; }
      if (format.Decimals is < 0 or > 10) { Error(path + ".decimals", "Show 0 to 10 decimals"); }
      if (format.Prefix is { Length: > 20 }) { Error(path + ".prefix", "A prefix has 20 characters at most"); }
      if (format.Suffix is { Length: > 20 }) { Error(path + ".suffix", "A suffix has 20 characters at most"); }
   }

   /// <summary>A sort by a dimension, measure or column there are <paramref name="count"/> of.</summary>
   public void Sort(WidgetSort? sort, string path, SortTarget[] targets, Func<SortTarget, int> count)
   {
      if (sort == null) { return; }
      if (!targets.Contains(sort.By)) { Error(path + ".by", $"Sort by {string.Join(" or ", targets.Select(DefinitionRules.Name))}"); }
      else if (sort.Index < 0 || sort.Index >= count(sort.By)) { Error(path + ".index", $"There is no {DefinitionRules.Name(sort.By)} {sort.Index}"); }
   }

   public void Limit(int limit, string path, int max)
   {
      if (limit < 1 || limit > max) { Error(path, $"Show 1 to {max:N0}"); }
   }

   /// <summary>What every widget of a source's rows has: its source, conditions, and the widgets it listens to.</summary>
   public void Data(DataWidgetConfig config, string? widget, string path)
   {
      SourceRef(config.Source, path + ".source");
      if (List(config.Conditions, path + ".conditions", DefinitionRules.MaxConditions, "conditions") is { } conditions)
      {
         for (int i = 0; i < conditions.Count; i++)
         {
            string at = $"{path}.conditions[{i}]";
            if (conditions[i] == null)
            {
               Error(at, "A condition can't be null");
               continue;
            }
            Field(conditions[i].Field, at + ".field");
            Condition(conditions[i].Value, at + ".value");
         }
      }
      if (config.Listens == null) { Error(path + ".listens", "Say which widgets it listens to"); }
      else if (List(config.Listens.Widgets, path + ".listens.widgets", int.MaxValue, "widgets") is { } chosen)
      {
         if (config.Listens.Mode != ListenMode.Chosen && chosen.Count > 0) { Error(path + ".listens.widgets", "Widgets are chosen only when it listens to chosen ones"); }
         for (int i = 0; i < chosen.Count; i++)
         {
            WidgetRef(chosen[i], $"{path}.listens.widgets[{i}]");
            if (chosen[i] == widget) { Error($"{path}.listens.widgets[{i}]", "A widget doesn't listen to itself"); }
         }
      }
   }

   public void Measures(List<Measure>? measures, string path, int min)
   {
      if (List(measures, path, DefinitionRules.MaxMeasures, "measures") is not { } list) { return; }
      if (list.Count < min) { Error(path, min == 1 ? "Give a measure" : $"Give {min} measures at least"); }
      for (int i = 0; i < list.Count; i++) { Measure(list[i], $"{path}[{i}]"); }
   }
}
