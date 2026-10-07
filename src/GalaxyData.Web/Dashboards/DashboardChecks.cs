using System;
using System.Collections.Generic;
using GalaxyData.Query.Catalog;

namespace GalaxyData.Web.Dashboards;

/// <summary>What doesn't fit the catalog in a definition: every widget planned with nothing chosen, and every filter's field.</summary>
public static class DashboardChecks
{
   public static IReadOnlyList<DashboardIssue> Issues(DashboardDefinition definition, QueryCatalog catalog, WidgetKinds kinds, DateOnly today)
   {
      ArgumentNullException.ThrowIfNull(definition);
      DashboardCatalog resolved = DashboardCatalog.Resolve(definition, catalog);
      WidgetPlanner planner = new(resolved, DashboardState.Empty, new PlanLimits(int.MaxValue, int.MaxValue, int.MaxValue, Public: false), today, []);
      for (int i = 0; i < definition.Filters.Count; i++)
      {
         DashboardFilter filter = definition.Filters[i];
         resolved.Field(filter.Field.Source, new FieldRef(filter.Field.Path, filter.Field.Column), $"filters[{i}].field", null);
      }
      foreach (DashboardWidget widget in definition.Widgets) { planner.Plan(widget.Id, kinds); }
      return resolved.Issues;
   }
}
