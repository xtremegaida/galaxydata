using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Sql;
using GalaxyData.Testing;
using Shouldly;
using Xunit;
using static GalaxyData.Query.Tests.Catalog.Schemas;

namespace GalaxyData.Query.Tests.Dml;

/// <summary>Golden reports: the statements each dialect gets for a set of changes, and the issues of changes that can't be made.</summary>
public sealed class DmlReportTests
{
   public static TheoryData<string> Dialects => [.. SqlDialect.All.Select(d => d.ProviderKind)];

   private static readonly Guid Contact = new("2f1c0000-0000-4000-8000-000000000001");

   /// <summary>
   /// A shop whose columns have what changes care about: an identity, defaults, a computed column, a row version, a
   /// column of SQL Server's <c>datetime</c>, one read as text (a PostgreSQL enum); orders have triggers, notes no key.
   /// </summary>
   internal static SourceSchema ShopSchema() => Source(
      Table("customers", Col("id", "int32") with { IsIdentity = true }, Col("name", "string(100)"), Col("city", "string(40)?"),
            Col("credit_limit", "decimal(10,2)?"), Col("created_at", "datetime?") with { DefaultSql = "now()" }) with { PrimaryKey = Pk("id") },
      Table("orders", Col("id"), Col("customer_id", "int32"), Col("status", "string(20,ansi)") with { DefaultSql = "'open'" }, Col("total", "decimal(10,2)"),
            Col("order_date", "date"), Col("placed_at", "datetimeoffset?"), Col("logged", "datetime?") with { NativeType = "datetime" },
            Col("mood", "string?") with { NativeType = "mood", ReadAs = "text" }, Col("version", "binary") with { IsRowVersion = true }) with
      {
         PrimaryKey = Pk("id"),
         ForeignKeys = [Fk("customer_id", "customers")],
         HasTriggers = true,
      },
      Table("order_lines", Col("order_id"), Col("line_no", "int32"), Col("qty", "int32"), Col("price", "decimal(10,2)"),
            Col("amount", "decimal(12,2)?") with { IsComputed = true }) with
      {
         PrimaryKey = Pk("order_id", "line_no"),
         ForeignKeys = [Fk("order_id", "orders")],
      },
      Table("contacts", Col("id", "guid"), Col("email", "string"), Col("Display Name", "string?")) with { PrimaryKey = Pk("id") },
      Table("notes", Col("at", "datetime?"), Col("text", "string?"), Col("score", "double?")),
      View("open_orders", Col("id"), Col("total", "decimal(10,2)")));

   internal static QueryCatalog Catalog() => new CatalogBuilder().AddSource(Info("shop"), ShopSchema()).Build();

   internal static TableEntity TableOf(ICatalog catalog, string name) => (TableEntity)catalog.Entity("shop." + name);

   internal static Dictionary<string, object?> Row(params (string Column, object? Value)[] values) =>
      values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal);

   /// <summary>Inserts (lines given before their order), updates and deletes of each kind the planner writes differently.</summary>
   private static ChangeSet Changes(ICatalog catalog) => new(
   [
      new InsertRow(TableOf(catalog, "customers"), Row(("name", "Delta Ltd"), ("city", "Durban"))),
      new InsertRow(TableOf(catalog, "order_lines"), Row(("order_id", 2001L), ("line_no", 1), ("qty", 2), ("price", 6.25m))),
      new InsertRow(TableOf(catalog, "orders"), Row(("id", 2001L), ("customer_id", 4), ("total", "12.50"), ("order_date", "2026-03-01"),
                                                  ("placed_at", new DateTimeOffset(2026, 3, 1, 10, 30, 0, TimeSpan.FromHours(2))), ("mood", "happy"))),
      new InsertRow(TableOf(catalog, "notes"), Row()),
      new UpdateRow(TableOf(catalog, "orders"), Row(("id", 1001L)), Row(("status", "shipped"), ("logged", new DateTime(2026, 3, 2, 9, 0, 0, 123))))
      {
         Original = Row(("status", "open"), ("total", 250.00m), ("placed_at", new DateTimeOffset(2026, 1, 5, 8, 30, 0, TimeSpan.Zero)),
                        ("logged", new DateTime(2026, 1, 5, 8, 30, 0, 333)), ("mood", "calm"), ("customer_id", null)),
      },
      new UpdateRow(TableOf(catalog, "orders"), Row(("id", 1002L)), Row(("total", 99m))) { Original = Row(("total", 99.50m), ("version", new byte[] { 0, 0, 0, 0, 0, 0, 7, 209 })) },
      new UpdateRow(TableOf(catalog, "customers"), Row(("id", 3)), Row(("city", "Cape Town"))) { Original = Row(("city", null), ("credit_limit", null), ("name", "Gamma Inc")) },
      new UpdateRow(TableOf(catalog, "contacts"), Row(("id", Contact)), Row(("Display Name", "Ann"))),
      new DeleteRow(TableOf(catalog, "order_lines"), Row(("order_id", 1001L), ("line_no", 2))) { Original = Row(("qty", 1), ("price", 50.00m), ("amount", 50.00m)) },
      new DeleteRow(TableOf(catalog, "orders"), Row(("id", 1001L))) { Original = Row(("status", "shipped")) },
      new DeleteRow(TableOf(catalog, "contacts"), Row(("id", Contact.ToString()))) { Original = Row(("email", "ann@acme.test"), ("Display Name", "Ann")) },
   ]);

   [Theory]
   [MemberData(nameof(Dialects))]
   public void Statements(string providerKind)
   {
      SqlDialect dialect = SqlDialect.All.Single(d => d.ProviderKind == providerKind);
      DmlPlan plan = DmlPlanner.Plan(Changes(Catalog()), _ => dialect);
      plan.Issues.ShouldBeEmpty();
      plan.Scripts.Count.ShouldBe(1);
      QueryReport report = new();
      foreach (DmlStatement statement in plan.Scripts[0].Statements)
      {
         StringBuilder text = new();
         text.AppendLine("-- shown").AppendLine(statement.ToDisplayText());
         text.AppendLine("-- with parameters").AppendLine(statement.ToDisplayText(inlineParameters: false));
         text.Append("-- run (").Append(statement.Counting).Append(", ").Append(statement.ExpectedRows).Append(" row");
         if (statement.ReturnedColumns.Count > 0) { text.Append(", gives back ").AppendJoin(", ", statement.ReturnedColumns.Select(c => c.Name)); }
         text.AppendLine(")").AppendLine(statement.ToString());
         report.Case($"{statement.ChangeIndex}: {statement.Description}", text.ToString());
      }
      Golden.Match(report.ToString(), suffix: providerKind);
   }

   /// <summary>The plan as a person reads and edits it: every script, its values written in.</summary>
   [Fact]
   public void ScriptAsShown() => Golden.Match(DmlPlanner.Plan(Changes(Catalog()), _ => SqlDialect.SqlServer).ToDisplayText());

   [Fact]
   public void Issues()
   {
      QueryCatalog catalog = new CatalogBuilder()
         .AddSource(Info("shop"), ShopSchema())
         .AddSource(Info("ro") with { IsReadOnly = true }, Source(Table("things", Col("id")) with { PrimaryKey = Pk("id") }))
         .AddSource(Info("xl") with { SupportsDml = false }, Source(Table("sheet", Col("a", "string?"))))
         .AddSource(Info("other") with { ProviderKind = "mysql" }, Source(Table("things", Col("id")) with { PrimaryKey = Pk("id") }))
         .WithOverlay(new CatalogOverlay { VirtualEntities = [new OverlayVirtualEntity("reports.big_orders", "shop.orders.where(total > 100)")] })
         .Build();
      TableEntity customers = TableOf(catalog, "customers");
      TableEntity orders = TableOf(catalog, "orders");
      TableEntity lines = TableOf(catalog, "order_lines");
      RowChange[] changes =
      [
         new InsertRow(catalog.Entity("shop.open_orders"), Row(("id", 1L))),
         new InsertRow(catalog.Entity("reports.big_orders"), Row(("id", 1L))),
         new InsertRow(catalog.Entity("ro.things"), Row(("id", 1L))),
         new InsertRow(catalog.Entity("xl.sheet"), Row(("a", "x"))),
         new InsertRow(catalog.Entity("other.things"), Row(("id", 1L))),
         new InsertRow(customers, Row(("id", 9), ("name", "Epsilon"))),
         new InsertRow(customers, Row(("Name", "Zeta"), ("nmae", "x"), ("city", "A city whose name is longer than forty characters"))),
         new InsertRow(customers, Row(("city", "Durban"))),
         new InsertRow(lines, Row(("order_id", 1001L), ("line_no", 3), ("qty", 70000L * 70000), ("price", 1.005m), ("amount", 5m))),
         new InsertRow(lines, Row(("order_id", 1001L), ("line_no", 4), ("qty", "two"), ("price", 123456789m))),
         new InsertRow(orders, Row(("id", 3001L), ("customer_id", 1), ("total", 1m), ("order_date", new DateTime(2026, 3, 1, 10, 0, 0)), ("version", new byte[8]))),
         new InsertRow(orders, Row(("id", 3002L), ("customer_id", null), ("total", 1m), ("order_date", "1 March"))),
         new UpdateRow(orders, Row(("id", 1001L)), Row(("id", 1009L), ("version", new byte[8]))),
         new UpdateRow(orders, Row(("id", 1001L), ("status", "open")), Row()),
         new UpdateRow(lines, Row(("order_id", 1001L)), Row(("qty", 3))),
         new UpdateRow(lines, Row(("order_id", 1001L), ("line_no", null)), Row(("amount", 3m))),
         new UpdateRow(customers, Row(("id", 1)), Row(("id", 2))) { Original = Row(("credit_limit", "lots")) },
         new DeleteRow(TableOf(catalog, "notes"), Row(("at", DateTime.MinValue))),
      ];
      StringBuilder text = new();
      foreach (SqlDialect dialect in new[] { SqlDialect.Sqlite, SqlDialect.SqlServer })
      {
         text.Append("## ").AppendLine(dialect.Name);
         DmlPlan plan = DmlPlanner.Plan(new ChangeSet(changes), s => s.ProviderKind == "mysql" ? null : dialect);
         text.Append("planned: ").AppendJoin(", ", plan.Scripts.SelectMany(s => s.Statements).Select(s => s.ChangeIndex)).AppendLine();
         foreach (DmlIssue issue in plan.Issues)
         {
            text.Append(issue.ChangeIndex).Append(issue.Column == null ? string.Empty : $" [{issue.Column}]").Append(": ").AppendLine(issue.Message);
         }
         text.AppendLine();
      }
      Golden.Match(text.ToString());
   }

   /// <summary>Statements of the changes that can be made are planned all the same, to show; the plan doesn't run.</summary>
   [Fact]
   public void ChangesThatCanBeMadeArePlannedBesideIssues()
   {
      QueryCatalog catalog = Catalog();
      DmlPlan plan = DmlPlanner.Plan(new ChangeSet(
      [
         new InsertRow(TableOf(catalog, "customers"), Row(("name", "Delta Ltd"))),
         new InsertRow(TableOf(catalog, "customers"), Row(("city", "Durban"))),
      ]), _ => SqlDialect.Sqlite);
      plan.Success.ShouldBeFalse();
      plan.Issues.Single().ChangeIndex.ShouldBe(1);
      plan.Scripts.Single().Statements.Single().ChangeIndex.ShouldBe(0);
   }
}
