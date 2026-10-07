using System.Collections.Generic;

namespace GalaxyData.Web.Dashboards;

public enum SelectionMode
{
   /// <summary>The slices chosen: linked widgets keep the rows they relate to.</summary>
   Include,

   /// <summary>The slices left out: linked widgets keep the rows they don't relate to.</summary>
   Exclude,
}

/// <summary>
/// The slices chosen in a widget: each key is the values of the widget's dimensions (one, or two with a series; a raw
/// table's are its entity's key), as <c>ValueCodec</c> writes them; a period's value is its first day.
/// </summary>
public sealed record SelectionState(SelectionMode Mode, List<List<object?>> Keys);

/// <summary>
/// What a viewer has set: the values of the filters they may change (by id; null for none), and the slices chosen
/// in each widget (by id). Anything else is the definition's.
/// </summary>
public sealed record DashboardState(Dictionary<string, ConditionValue?>? Filters = null, Dictionary<string, SelectionState>? Selections = null)
{
   /// <summary>Nothing set. A field, not a property: request validation walks properties, this one into itself.</summary>
   public static readonly DashboardState Empty = new();
}

public enum IssueSeverity
{
   /// <summary>The widget can't be shown.</summary>
   Error,

   /// <summary>It is shown, but perhaps not as meant.</summary>
   Warning,
}

/// <summary>Something wrong with a dashboard as the catalog has it now: of a widget (or the dashboard), and the field it is about (as the definition's JSON names it).</summary>
public sealed record DashboardIssue(IssueSeverity Severity, string Message, string? Widget, string? Field);
