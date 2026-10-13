using System;
using System.IO;
using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.HyperWhirlpool;
using GalaxyData.Query.Introspection;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Connectors;

/// <summary>
/// A HyperWhirlpool warehouse's schema document becomes the source's schema: each domain a schema, each public view a
/// view with its key, and its references foreign keys the warehouse doesn't enforce, which become navigations, across
/// domains too. A document in another format, or that isn't one, is refused with a message saying so.
/// </summary>
public sealed class HyperWhirlpoolSchemaTests
{
   /// <summary>The fixture's document, its domains in the databases <c>web</c> and <c>crm</c>.</summary>
   internal static string Document(string web = "web", string crm = "crm") =>
      File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "hyperwhirlpool.schema.json"))
         .Replace("{web}", web, StringComparison.Ordinal)
         .Replace("{crm}", crm, StringComparison.Ordinal);

   private static SourceSchema Schema(IntrospectionOptions? options = null) =>
      SchemaDocument.Parse(Document()).ToSourceSchema("26.8.1.1", "web", options ?? IntrospectionOptions.Default);

   [Fact]
   public void TheDocumentBecomesTheSourcesSchema() => Golden.Match(IntrospectionJson.Serialize(Schema(), indented: true), "json");

   [Fact]
   public void ReferencesBecomeNavigationsThatMayFindNoRow()
   {
      SourceInfo source = new HyperWhirlpoolKind().Configure(new SourceInfo("hw", HyperWhirlpoolSourceProvider.Kind, "web"), new System.Collections.Generic.Dictionary<string, string>());
      QueryCatalog catalog = new CatalogBuilder().AddSource(source, Schema()).Build();
      catalog.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).ShouldBeEmpty();
      string navigations = string.Join("\n", catalog.Entities.OrderBy(e => e.DisplayName, StringComparer.Ordinal)
         .SelectMany(e => e.Navigations.OrderBy(n => n.Name, StringComparer.Ordinal))
         .Select(n => n.ToString()));
      Golden.Match(navigations);
      EntityDef events = catalog.FindEntity(EntityName.Parse("hw.page_view_events")).ShouldNotBeNull();
      events.Key.ShouldNotBeNull().Columns.Select(c => c.Name).ShouldBe(["event_id"]);
      events.FindNavigation("crm_contact").Item!.Target.DisplayName.ShouldBe("hw.crm.contact");
   }

   /// <summary>
   /// What the model says of a view's columns is kept for the dashboard editor: the column its rows are dated by,
   /// the labels a classified column takes, and how an attribute counted or summed from events adds up.
   /// </summary>
   [Fact]
   public void TheModelsHintsAreKept()
   {
      SourceInfo source = new("hw", HyperWhirlpoolSourceProvider.Kind, "web");
      QueryCatalog catalog = new CatalogBuilder().AddSource(source, Schema()).Build();
      EntityDef session = catalog.FindEntity(EntityName.Parse("hw.session")).ShouldNotBeNull();
      session.TimeColumn.ShouldNotBeNull().Name.ShouldBe("first_seen");
      session.TimeEndColumn.ShouldNotBeNull().Name.ShouldBe("last_seen");
      EntityDef events = catalog.FindEntity(EntityName.Parse("hw.page_view_events")).ShouldNotBeNull();
      events.TimeColumn!.Name.ShouldBe("ts");
      events.TimeEndColumn.ShouldBeNull();
      // An event falls within its session's span; a device's view is a snapshot, and a contact isn't built from the events.
      events.Navigations.Where(n => n.Relation.IsWithinTimeSpan).Select(n => n.Name).ShouldBe(["session"]);
      session.FindNavigation("device").Item!.Relation.IsWithinTimeSpan.ShouldBeFalse();
      session.FindColumn("traffic_type").Item!.Values.ShouldBe(["paid", "social", "search", "referral", "direct"]);
      session.Columns.Where(c => c.Aggregate != null).Select(c => $"{c.Name} {c.Aggregate}").ShouldBe(["events Sum", "page_views Sum"]);
      // The latest value of an attribute doesn't add up; a derived value says nothing.
      catalog.FindEntity(EntityName.Parse("hw.crm.contact")).ShouldNotBeNull().FindColumn("country").Item!.Aggregate.ShouldBeNull();
      session.FindColumn("duration_ms").Item!.Aggregate.ShouldBeNull();
   }

   [Fact]
   public void OnlyTheSchemasNamedAreRead()
   {
      Schema(new IntrospectionOptions { IncludeSchemas = ["crm"] }).Tables.Select(t => t.Name).ShouldBe(["contact"]);
      Schema(new IntrospectionOptions { ExcludeSchemas = ["crm"] }).Tables.Select(t => t.Schema).Distinct().ShouldBe(["web"]);
   }

   [Theory]
   [InlineData("{\"format\": \"hw-schema/2\", \"objects\": 7}", "is in the format hw-schema/2, which this version doesn't read")]
   [InlineData("{\"objects\": []}", "names no format")]
   [InlineData("[1, 2]", "names no format")]
   [InlineData("{\"format\": \"hw-schema/1\", \"model\": {\"seq\": 1, \"hash\": \"x\"}, \"domains\": []}", "can't be read")]
   [InlineData("not json", "can't be read")]
   public void WhatIsntADocumentOfThisFormatIsRefused(string json, string message) =>
      Should.Throw<InvalidDataException>(() => SchemaDocument.Parse(json)).Message.ShouldContain(message);
}
