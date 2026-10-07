using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Functions;
using GalaxyData.Query.IntegrationTests.Execution;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Docs;

/// <summary>
/// The language reference (docs/language.md) keeps in step with the language: every function, query method and
/// diagnostic code is in it, every example in a <c>gdq</c> block runs, and every one in a <c>gdq-error</c> block is
/// refused. The examples run against the shop in SQLite (<c>shop</c>) and in DuckDB (<c>wh</c>, its orders related
/// to shop's customers as <c>shop_customer</c>), with $min = 50, $since = '2026-01-06' and $n = 3.
/// </summary>
public sealed partial class LanguageDocTests
{
   // The document's line breaks are CRLF where Git checks it out so: $ comes before \n only.
   [GeneratedRegex(@"^```(?<tag>[\w-]*)[ \t]*\r?\n(?<body>.*?)^```[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.Singleline)]
   private static partial Regex Blocks();

   private static string Document()
   {
      DirectoryInfo? directory = new(AppContext.BaseDirectory);
      while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GalaxyData.slnx"))) { directory = directory.Parent; }
      string path = Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("The repository's root isn't above the tests"), "docs", "language.md");
      return File.ReadAllText(path);
   }

   private static List<(string Tag, string Query, int Line)> Examples(string document) =>
      Blocks().Matches(document)
         .Select(m => (Tag: m.Groups["tag"].Value, Query: m.Groups["body"].Value.TrimEnd(), Line: document[..m.Index].Count(c => c == '\n') + 1))
         .Where(e => e.Tag is "gdq" or "gdq-error")
         .ToList();

   private static async Task<TestSources> SourcesAsync() =>
      await (await new TestSources().AddSqliteAsync("shop", Fixtures.Sql("shop.sqlite.sql"))).AddDuckDbAsync("wh", Fixtures.Sql("shop.duckdb.sql"));

   private static readonly CatalogOverlay Overlay = new()
   {
      Relations = [new OverlayRelation("wh.orders", ["customer_id"], "shop.customers", ["id"]) { Name = "shop_customer" }],
   };

   private static readonly QueryParameters Parameters = new QueryParameters().Add("min", 50L).Add("since", "2026-01-06").Add("n", 3L);

   [Fact]
   public void EveryFunctionIsDocumented()
   {
      string document = Document();
      FunctionRegistry.Default.Functions.Select(f => f.Name).Where(name => !document.Contains(name + "(", StringComparison.Ordinal)).ShouldBeEmpty();
   }

   [Fact]
   public void EveryQueryMethodIsDocumented()
   {
      string document = Document();
      BinderRun.QueryMethods.Where(name => name != "orderByDesc" && name != "thenByDesc")
         .Where(name => !document.Contains("." + name + "(", StringComparison.Ordinal)).ShouldBeEmpty();
   }

   [Fact]
   public void EveryDiagnosticCodeIsDocumented()
   {
      string document = Document();
      typeof(DiagnosticCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetValue(null)!)
         .Where(code => !document.Contains(code, StringComparison.Ordinal)).ShouldBeEmpty();
   }

   [Fact]
   public async Task EveryExampleRuns()
   {
      List<(string Tag, string Query, int Line)> examples = Examples(Document());
      examples.Count(e => e.Tag == "gdq").ShouldBeGreaterThan(50);
      await using TestSources sources = await SourcesAsync();
      QueryEngine engine = sources.Engine(Overlay);
      List<string> failures = [];
      foreach ((string tag, string query, int line) in examples.Where(e => e.Tag == "gdq"))
      {
         PreparedQuery prepared = engine.Prepare(new QueryRequest(query) { Parameters = Parameters });
         if (!prepared.Success)
         {
            failures.Add($"line {line}: {query}{Environment.NewLine}  {string.Join("; ", prepared.Diagnostics.Where(d => d.IsError).Select(d => d.Message))}");
            continue;
         }
         try
         {
            await using QueryResult result = await prepared.ExecuteAsync(TestContext.Current.CancellationToken);
            await result.ToListAsync(TestContext.Current.CancellationToken);
         }
         catch (QueryExecutionException e)
         {
            failures.Add($"line {line}: {query}{Environment.NewLine}  {e.Message}");
         }
      }
      failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
   }

   [Fact]
   public async Task EveryErrorExampleIsRefused()
   {
      List<(string Tag, string Query, int Line)> examples = Examples(Document());
      await using TestSources sources = await SourcesAsync();
      QueryEngine engine = sources.Engine(Overlay);
      examples.Where(e => e.Tag == "gdq-error")
         .Where(e => engine.Prepare(new QueryRequest(e.Query) { Parameters = Parameters }).Success)
         .Select(e => $"line {e.Line}: {e.Query}").ShouldBeEmpty();
   }
}
