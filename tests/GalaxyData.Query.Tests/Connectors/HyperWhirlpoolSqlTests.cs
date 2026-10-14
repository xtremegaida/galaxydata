using System;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.HyperWhirlpool;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Planning.Optimizer;
using GalaxyData.Query.Sql;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Connectors;

/// <summary>
/// The SQL of a grid's pages and a dashboard's charts of a warehouse's views, which are large and read through views
/// ClickHouse can't plan into. A page is found by its key first, the rows sorted reading only the columns the sort
/// needs, and the rows it refers to are joined to it with the page on the side ClickHouse builds the join from: neither
/// reads all of a view whole. A view with a key of two columns is sorted whole. A chart's dates bound the rows its
/// navigations join, where an event falls within the span of the row it refers to: a month's sessions, not all of them.
/// </summary>
public sealed class HyperWhirlpoolSqlTests
{
   private static readonly QueryCatalog Catalog = new CatalogBuilder()
      .AddSource(new SourceInfo("hw", HyperWhirlpoolSourceProvider.Kind, "web"),
         SchemaDocument.Parse(HyperWhirlpoolSchemaTests.Document()).ToSourceSchema("26.8.1.1", "web", IntrospectionOptions.Default))
      .Build();

   private static readonly (string Query, long Offset)[] Pages =
   [
      ("hw.session", 0),
      ("hw.page_view_events.where(url != '').orderBy(desc(ts)).extend(_display0: session.traffic_type, _display1: device.browser, _display2: crm_contact.country)", 50),
      ("hw.link_device_contact.extend(_display0: device.browser)", 0),
   ];

   [Fact]
   public void APageOfAViewIsFoundByItsKeyThenJoinedToTheRowsItRefersTo()
   {
      QueryReport report = new();
      foreach ((string query, long offset) in Pages)
      {
         BoundProgram program = Binder.Bind(query, Catalog);
         program.Diagnostics.ShouldNotContain(d => d.IsError, query);
         LogicalPlan plan = PlanOptimizer.Optimize(Lowerer.Lower(program, offset, 51));
         report.Case($"{query}, rows {offset + 1} to {offset + 51}", SqlBuilder.Build(plan, ClickHouseDialect.Instance).ToString());
      }
      Golden.Match(report.ToString());
   }

   private static readonly string[] Charts =
   [
      // A line chart of a month of events, a series for each kind of visit, as the dashboard editor writes it.
      "hw.page_view_events.where(r0 => (r0.ts >= $f1 and r0.ts < $f2)).groupBy(d0: startOfMonth(ts), s0: session.traffic_type).select(d0, s0, m0: count()).orderBy(d0, s0).take(1001)",
      // Filtered by the session too (an inner join), the time compared the other way round; between both ends.
      "hw.page_view_events.where(r0 => $f1 <= r0.ts and r0.session.traffic_type == 'search').select(event_id, ts)",
      "hw.page_view_events.where(between(ts, $f1, $f2)).select(event_id, t: session.traffic_type, b: session.device.browser)",
      // A device's view is a snapshot, and a contact isn't built from the events: neither is bounded.
      "hw.session.where(first_seen >= $f1).groupBy(b: device.browser).select(b, n: count())",
      "hw.page_view_events.where(ts >= $f1).select(event_id, c: crm_contact.country)",
      // Page views carry their session's traffic type: charted and filtered by it, they join no session; by a column
      // they don't carry, they join the sessions of the days those can have started on.
      "hw.page_view.where(r0 => (r0.first_seen >= $f1 and r0.first_seen < $f2)).groupBy(d0: startOfMonth(first_seen), s0: session.traffic_type).select(d0, s0, m0: count()).orderBy(d0, s0).take(1001)",
      "hw.page_view.where(r0 => r0.first_seen >= $f1 and r0.session.traffic_type == 'search').select(page_view_id, n: session.page_views)",
      "hw.page_view.where(r0 => r0.first_seen >= $f1).groupBy(b: session.device.browser).select(b, n: count())",
   ];

   [Fact]
   public void AChartsDatesBoundTheRowsItsNavigationsJoin()
   {
      QueryParameters parameters = new QueryParameters()
         .Add("f1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
         .Add("f2", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
      QueryReport report = new();
      foreach (string query in Charts)
      {
         BoundProgram program = Binder.Bind(query, Catalog, parameters);
         program.Diagnostics.ShouldNotContain(d => d.IsError, query);
         LogicalPlan plan = PlanOptimizer.Optimize(Lowerer.Lower(program));
         report.Case(query, SqlBuilder.Build(plan, ClickHouseDialect.Instance).ToString());
      }
      Golden.Match(report.ToString());
   }
}
