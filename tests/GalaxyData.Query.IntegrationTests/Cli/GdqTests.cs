using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Cli;
using GalaxyData.Query.IntegrationTests.Excel;
using GalaxyData.Testing;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Cli;

/// <summary>The gdq command line, run in process against the fixture scripts; one golden transcript.</summary>
public sealed partial class GdqTests
{
   private static readonly string Sqlite = "shop=sqlite:" + Path.Combine(AppContext.BaseDirectory, "fixtures", "shop.sqlite.sql");
   private static readonly string DuckDb = "shop=duckdb:" + Path.Combine(AppContext.BaseDirectory, "fixtures", "shop.duckdb.sql");
   private static readonly string Warehouse = "wh=duckdb:" + Path.Combine(AppContext.BaseDirectory, "fixtures", "shop.duckdb.sql");

   [GeneratedRegex(@"; \d+ ms\)")]
   private static partial Regex Timing();

   private static async Task<string> RunAsync(string title, string[] args, string input = "")
   {
      using StringWriter output = new();
      using StringWriter error = new();
      int exit = await GdqApp.RunAsync(args, new StringReader(input), output, error, TestContext.Current.CancellationToken);
      StringBuilder text = new();
      text.Append("### ").AppendLine(title).Append("exit ").Append(exit).AppendLine();
      if (output.ToString().Length > 0) { text.AppendLine("--- out").Append(output.ToString().TrimEnd()).AppendLine(); }
      if (error.ToString().Length > 0) { text.AppendLine("--- err").Append(error.ToString().TrimEnd()).AppendLine(); }
      return Timing().Replace(text.ToString(), "; (time))").ReplaceLineEndings(Environment.NewLine) + Environment.NewLine;
   }

   [Fact]
   public async Task Transcript()
   {
      StringBuilder transcript = new();
      transcript.Append(await RunAsync("run example 1 with its SQL",
         ["run", "-s", Sqlite, "--sql", "shop.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city).orderBy(id)"]));
      transcript.Append(await RunAsync("run with a parameter, as JSON",
         ["run", "-s", DuckDb, "-p", "since=2026-01-06", "--format", "json", "shop.orders.where(order_date >= $since).orderBy(id).select(id, total, paid: total > 50)"]));
      transcript.Append(await RunAsync("run as CSV with a row limit",
         ["run", "-s", Sqlite, "--format", "csv", "--max-rows", "2", "shop.customers.orderBy(name).select(name, city)"]));
      transcript.Append(await RunAsync("sql: the SQL only", ["sql", "-s", DuckDb, "shop.order_lines.select(line_no, city: order.customer.city).take(3)"]));
      transcript.Append(await RunAsync("explain: plan, columns with lineage and links, SQL",
         ["explain", "-s", Sqlite, "shop.orders.where(status == 'open').select(id, total, who: customer.name, n: order_lines.count())"]));
      transcript.Append(await RunAsync("explain, verbose", ["explain", "-s", DuckDb, "--verbose", "shop.orders.groupBy(customer_id).select(customer_id: key, spend: sum(total))"]));
      transcript.Append(await RunAsync("explain a query that doesn't bind", ["explain", "-s", Sqlite, "shop.orders.where(totl > 5)"]));
      string overlay = Path.Combine(Path.GetTempPath(), $"gdq-overlay-{Guid.NewGuid():N}.json");
      await File.WriteAllTextAsync(overlay, new CatalogOverlay
      {
         Relations = [new OverlayRelation("wh.orders", ["customer_id"], "shop.customers", ["id"]) { Name = "shop_customer" }],
      }.ToJson(), TestContext.Current.CancellationToken);
      try
      {
         const string across = "wh.orders.where(status == 'open').select(id, total, who: shop_customer.name).orderBy(id)";
         transcript.Append(await RunAsync("run across sources, with the SQL", ["run", "-s", Sqlite, "-s", Warehouse, "--overlay", overlay, "--sql", across]));
         transcript.Append(await RunAsync("explain across sources", ["explain", "-s", Sqlite, "-s", Warehouse, "--overlay", overlay, across]));
      }
      finally
      {
         File.Delete(overlay);
      }
      using (TempFolder folder = new())
      {
         new XlsxBuilder().Sheet("lines", ["customer_id", "amount"], [1, 10.5], [2, 20], [1, 4.5]).Save(folder.File("budget.xlsx"));
         await File.WriteAllTextAsync(folder.File("broken.xlsx"), "not a workbook", TestContext.Current.CancellationToken);
         string excel = "xl=excel:" + folder.Path;
         transcript.Append(await RunAsync("run across a folder of workbooks and SQLite, with the SQL", ["run", "-s", Sqlite, "-s", excel, "--sql",
            "xl.budget.lines.join(shop.customers, outer.customer_id == inner.id, b: outer, c: inner).groupBy(c.name).select(name: c.name, total: b.sum(amount)).orderBy(name)"]));
         transcript.Append(await RunAsync("schema of a folder of workbooks", ["schema", "-s", excel]));
      }
      transcript.Append(await RunAsync("a folder that doesn't exist", ["run", "-s", "xl=excel:missing-folder", "xl.budget.lines"]));
      transcript.Append(await RunAsync("first() of no rows", ["run", "-s", Sqlite, "shop.orders.where(total > 1000000).first()"]));
      transcript.Append(await RunAsync("schema, filtered", ["schema", "-s", Sqlite, "order"]));
      transcript.Append(await RunAsync("a query that doesn't bind", ["run", "-s", Sqlite, "shop.orders\n  .where(totl > 5)"]));
      transcript.Append(await RunAsync("a source that isn't one", ["run", "-s", "shop=oracle:x", "shop.orders"]));
      transcript.Append(await RunAsync("a database that doesn't exist", ["run", "-s", "shop=sqlite:missing.db", "shop.orders"]));
      transcript.Append(await RunAsync("no query", ["run", "-s", Sqlite]));
      transcript.Append(await RunAsync("repl", ["repl", "-s", Sqlite],
         "shop.customers.select(name, city)\n" +
         "  .where(city != null)\n" +
         ".orderBy(desc(name))\n" +
         ":param min=50\n" +
         "shop.orders\n" +
         "  .where(total > $min)\n" +
         "  .select(id,\n" +
         "          total)\n" +
         "big := shop.orders.where(total > 100);\n" +
         "big.select(id)\n" +
         ":explain\n" +
         ":explain shop.customers.select(name, first_order: orders.orderBy(order_date).first())\n" +
         ":nope\n" +
         ":quit\n"));
      Golden.Match(transcript.ToString());
   }
}
