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
/// The SQL of a grid's pages of a warehouse's views, which are large and read through views ClickHouse can't plan into.
/// A page is found by its key first, the rows sorted reading only the columns the sort needs, and the rows it refers to
/// are joined to it with the page on the side ClickHouse builds the join from: neither reads all of a view whole. A
/// view with a key of two columns is sorted whole.
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
}
