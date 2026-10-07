using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GalaxyData.Web.Dashboards;

/// <summary>
/// What a dashboard shows: its layout, its data sources and the links between them, its filters, its widgets and its
/// refresh policy. A working copy holds one, and so does each revision published. Only its structure is checked when
/// it is saved (<see cref="DefinitionRules"/>); entities, columns and navigations are checked when it is shown, as the
/// catalog changes after a save. <see cref="Palette"/>: the palette its charts are drawn with (none: the built-in
/// colours), which a chart may name its own of; left out when there is none, so definitions saved before palettes
/// are written as they were.
/// </summary>
public sealed record DashboardDefinition(
   int Schema,
   DashboardLayout Layout,
   List<DashboardSource> Sources,
   List<DashboardLink> Links,
   List<DashboardFilter> Filters,
   List<DashboardWidget> Widgets,
   RefreshPolicy Refresh,
   PublicSettings Public,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Palette = null)
{
   public const int CurrentSchema = 1;

   /// <summary>A dashboard with nothing on it: a blank page.</summary>
   public static DashboardDefinition Blank() =>
      new(CurrentSchema, DashboardLayout.Default(), [], [], [], [], new RefreshPolicy(RefreshMode.Manual, null), new PublicSettings(ShowData: true));
}

/// <summary>
/// The grid: rows of <see cref="RowHeight"/> pixels with <see cref="Gap"/> between cells, and width ranges
/// (<see cref="Breakpoints"/>, ascending, the first from 0) with their numbers of columns. The widest is the one
/// designed (<see cref="Items"/>, by widget id); narrower ones are derived from it, unless overridden.
/// </summary>
public sealed record DashboardLayout(
   int RowHeight,
   int Gap,
   List<Breakpoint> Breakpoints,
   Dictionary<string, Placement> Items,
   Dictionary<string, LayoutOverride> Overrides)
{
   public static DashboardLayout Default() => new(48, 12,
   [
      new Breakpoint("narrow", "Narrow", 0, 1),
      new Breakpoint("medium", "Medium", 720, 6),
      new Breakpoint("wide", "Wide", 1200, 12),
   ], [], []);
}

/// <summary>From <see cref="MinWidth"/> pixels (until the next breakpoint's), the grid has <see cref="Columns"/> columns.</summary>
public sealed record Breakpoint(string Id, string Label, int MinWidth, int Columns);

/// <summary>A widget's cell: from column <see cref="X"/> and row <see cref="Y"/> (from 0), <see cref="W"/> columns wide and <see cref="H"/> rows high.</summary>
public sealed record Placement(int X, int Y, int W, int H);

/// <summary>A narrower breakpoint laid out by hand: its widgets' cells when it had <see cref="Columns"/> columns, and those it hides.</summary>
public sealed record LayoutOverride(int Columns, Dictionary<string, Placement> Items, List<string> Hidden);

/// <summary>An entity of the catalog (a table, a view or a virtual entity), as queries write its path, under a label.</summary>
public sealed record DashboardSource(string Id, string Entity, string Label);

/// <summary>
/// How two sources' rows relate: the navigations from <see cref="From"/>'s entity to <see cref="To"/>'s, through
/// which filters and selections on one reach widgets on the other. None for two sources of one entity. The links
/// make a forest.
/// </summary>
public sealed record DashboardLink(string From, string To, List<string> Path);

/// <summary>A column of a source's rows, through forward (one or zero-or-one) navigations: <c>customer.country</c> on orders.</summary>
public sealed record FieldRef(List<string> Path, string Column);

/// <summary>A field of a named source.</summary>
public sealed record SourceField(string Source, List<string> Path, string Column);

public enum FilterKind
{
   /// <summary>Values chosen from those the field has (<see cref="ConditionOp.In"/>, <see cref="ConditionOp.NotIn"/>).</summary>
   Values,

   /// <summary>From and to (<see cref="ConditionOp.Between"/>, or one side: <see cref="ConditionOp.Ge"/>, <see cref="ConditionOp.Le"/>); date-times by day.</summary>
   Range,

   /// <summary>A period relative to today (<see cref="ConditionOp.Relative"/>).</summary>
   Relative,

   /// <summary>Text the field contains or starts with.</summary>
   Text,

   /// <summary>True or false (<see cref="ConditionOp.Eq"/>).</summary>
   Boolean,
}

/// <summary>
/// A filter of the dashboard's: a condition on a field, with its value (none: no condition). A visible filter shows,
/// read-only or for viewers to change when <see cref="Editable"/>; a hidden one always applies, and never leaves the
/// server. It applies to every widget linked to its source, but those in <see cref="Except"/>.
/// </summary>
public sealed record DashboardFilter(
   string Id,
   string Label,
   SourceField Field,
   FilterKind Kind,
   ConditionValue? Value,
   bool Visible,
   bool Editable,
   bool Multiple,
   List<string> Except);

/// <summary>The operations a condition may have: the grids', and lists and relative periods.</summary>
public enum ConditionOp
{
   Eq,
   Ne,
   Lt,
   Le,
   Gt,
   Ge,
   Between,
   Contains,
   NotContains,
   StartsWith,
   EndsWith,
   Blank,
   NotBlank,
   In,
   NotIn,
   Relative,
}

/// <summary>
/// A condition's operation and values, as <c>ValueCodec</c> writes values of the field's type: <see cref="Value"/>
/// (and <see cref="ValueTo"/> for between), <see cref="Values"/> for lists, <see cref="Relative"/> for periods.
/// Values are declared <c>object</c>, as JSON of any kind.
/// </summary>
public sealed record ConditionValue(ConditionOp Op, object? Value = null, object? ValueTo = null, List<object?>? Values = null, RelativeRange? Relative = null);

public enum RelativeUnit
{
   Day,
   Week,
   Month,
   Quarter,
   Year,
}

public enum RelativeMode
{
   /// <summary>The last <see cref="RelativeRange.Count"/> periods up to today.</summary>
   Last,

   /// <summary>The period today is in.</summary>
   This,

   /// <summary>The period before it.</summary>
   Previous,

   /// <summary>The period today is in, up to today.</summary>
   ToDate,
}

public sealed record RelativeRange(RelativeUnit Unit, int Count, RelativeMode Mode);

/// <summary>A condition of a widget's own, on a field of its source.</summary>
public sealed record WidgetCondition(FieldRef Field, ConditionValue Value);

/// <summary>A widget: its id (unique in the dashboard), its title (none: no title bar), and what it shows (<see cref="WidgetConfig"/>, by kind).</summary>
public sealed record DashboardWidget(string Id, string? Title, WidgetConfig Config);

/// <summary>What a widget shows, by its kind. A kind is added here, with its rules and query (<c>IWidgetKind</c>), and in the client's registry.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextConfig), "text")]
[JsonDerivedType(typeof(PieConfig), "pie")]
[JsonDerivedType(typeof(BarConfig), "bar")]
[JsonDerivedType(typeof(LineConfig), "line")]
[JsonDerivedType(typeof(TableConfig), "table")]
public abstract record WidgetConfig;

/// <summary>Markdown: headings and notes are widgets, as a dashboard has no title of its own.</summary>
public sealed record TextConfig(string Markdown) : WidgetConfig;

/// <summary>
/// A widget of a source's rows: its own conditions, whether choosing its slices filters the widgets that listen
/// (<see cref="Emits"/>), and whose choices filter it (<see cref="Listens"/>).
/// </summary>
public abstract record DataWidgetConfig(string Source, List<WidgetCondition> Conditions, bool Emits, Listens Listens) : WidgetConfig;

public enum ListenMode
{
   /// <summary>Every widget's that emits.</summary>
   All,

   None,

   /// <summary>Those in <see cref="Listens.Widgets"/>.</summary>
   Chosen,
}

public sealed record Listens(ListenMode Mode, List<string> Widgets);

public enum Aggregate
{
   /// <summary>The rows: <c>count()</c>.</summary>
   Count,

   /// <summary>The rows with a value: <c>count(r =&gt; r.x != null)</c> (<c>count(x)</c> of a true/false value counts its trues).</summary>
   CountValues,

   CountDistinct,
   Sum,
   Avg,
   Min,
   Max,
}

/// <summary>A date's or date-time's period, or a part of it that repeats (the quarter, the month, the day of the week, the hour).</summary>
public enum Bucket
{
   Day,
   Week,
   Month,
   Quarter,
   Year,
   QuarterOfYear,
   MonthOfYear,
   DayOfWeek,
   HourOfDay,
}

/// <summary>A field the rows are grouped by, in periods for dates (<see cref="Bucket"/>; date-times must have one), under a label.</summary>
public sealed record Dimension(FieldRef Field, Bucket? Bucket, string Label);

/// <summary>An aggregate of the rows of each group: of a field, but for <see cref="Aggregate.Count"/>.</summary>
public sealed record Measure(Aggregate Aggregate, FieldRef? Field, string Label, NumberFormat? Format);

/// <summary>How numbers show: their decimals (null: as they are), text before and after, and large ones shortened (1.2K).</summary>
public sealed record NumberFormat(int? Decimals, string? Prefix, string? Suffix, bool Compact);

public enum SortTarget
{
   Dimension,
   Measure,

   /// <summary>A raw table's column.</summary>
   Column,
}

/// <summary>What the rows are sorted by: a dimension, a measure or a column, by its index.</summary>
public sealed record WidgetSort(SortTarget By, int Index, bool Descending);

public enum LegendPosition
{
   None,
   Top,
   Bottom,
   Left,
   Right,
}

/// <summary>
/// A pie (or donut) of a measure by a dimension: the largest <see cref="Limit"/> slices, and the rest as one when
/// <see cref="Other"/>. <see cref="Palette"/>: its own palette (none: the dashboard's).
/// </summary>
public sealed record PieConfig(
   string Source,
   List<WidgetCondition> Conditions,
   bool Emits,
   Listens Listens,
   Dimension Dimension,
   Measure Measure,
   int Limit,
   bool Other,
   bool Donut,
   bool Labels,
   LegendPosition Legend,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Palette = null) : DataWidgetConfig(Source, Conditions, Emits, Listens);

public enum BarOrientation
{
   Vertical,
   Horizontal,
}

public enum BarStack
{
   None,
   Stacked,

   /// <summary>Stacked, each bar to 100%.</summary>
   Percent,
}

/// <summary>What a bar chart's colours stand for: its measures (or series), or each bar's category.</summary>
public enum BarColorBy
{
   Measure,

   /// <summary>Each bar its category's colour (one measure, no series), as a pie's slices have.</summary>
   Category,
}

/// <summary>
/// Bars of measures by a dimension, and by a series too (then one measure): the first <see cref="Limit"/> categories
/// by <see cref="Sort"/>. <see cref="Palette"/>: its own palette (none: the dashboard's).
/// </summary>
public sealed record BarConfig(
   string Source,
   List<WidgetCondition> Conditions,
   bool Emits,
   Listens Listens,
   Dimension Dimension,
   Dimension? Series,
   List<Measure> Measures,
   WidgetSort Sort,
   int Limit,
   BarOrientation Orientation,
   BarStack Stack,
   bool Labels,
   LegendPosition Legend,
   string? XTitle,
   string? YTitle,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Palette = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] BarColorBy ColorBy = BarColorBy.Measure) : DataWidgetConfig(Source, Conditions, Emits, Listens);

public enum LineGaps
{
   /// <summary>A missing period is zero.</summary>
   Zero,

   /// <summary>The line goes on over it.</summary>
   Connect,

   /// <summary>The line breaks.</summary>
   Break,
}

/// <summary>Lines of measures over a dimension (x), by a series too (then one measure), in its order. <see cref="Palette"/>: its own palette (none: the dashboard's).</summary>
public sealed record LineConfig(
   string Source,
   List<WidgetCondition> Conditions,
   bool Emits,
   Listens Listens,
   Dimension Dimension,
   Dimension? Series,
   List<Measure> Measures,
   int Limit,
   bool Area,
   LineGaps Gaps,
   bool Labels,
   LegendPosition Legend,
   string? XTitle,
   string? YTitle,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Palette = null) : DataWidgetConfig(Source, Conditions, Emits, Listens);

public enum TableMode
{
   /// <summary>Rows grouped by dimensions, with measures.</summary>
   Grouped,

   /// <summary>The source's rows, as they are.</summary>
   Raw,
}

/// <summary>A raw table's column: a field, under a label.</summary>
public sealed record TableColumn(FieldRef Field, string Label, NumberFormat? Format);

/// <summary>A table, grouped (<see cref="Dimensions"/> and <see cref="Measures"/>) or raw (<see cref="Columns"/>), in pages.</summary>
public sealed record TableConfig(
   string Source,
   List<WidgetCondition> Conditions,
   bool Emits,
   Listens Listens,
   TableMode Mode,
   List<Dimension> Dimensions,
   List<Measure> Measures,
   List<TableColumn> Columns,
   WidgetSort? Sort,
   int PageSize) : DataWidgetConfig(Source, Conditions, Emits, Listens);

public enum RefreshMode
{
   Manual,
   Interval,
}

/// <summary>Refreshed by hand, or every <see cref="Seconds"/> seconds.</summary>
public sealed record RefreshPolicy(RefreshMode Mode, int? Seconds);

/// <summary>What a public dashboard's viewers may see: its widgets' rows as a table (<see cref="ShowData"/>).</summary>
public sealed record PublicSettings(bool ShowData);
