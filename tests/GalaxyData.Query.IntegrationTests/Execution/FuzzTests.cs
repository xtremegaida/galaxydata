using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>
/// Queries mangled at random from the conformance set: whatever the text, preparing, explaining, counting and
/// running it gives diagnostics or a query's failure, never another exception. The seeds are fixed, so a failure
/// repeats.
/// </summary>
public sealed partial class FuzzTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   /// <summary>Names, words, numbers, text and operators of query text; a method call starts at its dot.</summary>
   [GeneratedRegex(@"'(?:[^']|'')*'|\d+(?:\.\d+)?|[A-Za-z_][A-Za-z0-9_]*|==|!=|<=|>=|\?\?|=>|:=|[-+*/%<>(),.;:\[\]?!]")]
   private static partial Regex Tokens();

   private static readonly string[] Names =
   [
      "id", "name", "city", "total", "status", "order_date", "placed_at", "credit_limit", "customer_id", "qty", "price", "line_no", "customer",
      "orders", "order", "order_lines", "addresses", "ship_address", "key", "it", "outer", "inner", "nope",
   ];

   private static readonly string[] Values = ["0", "1", "-1", "2.5", "1e300", "9223372036854775807", "null", "true", "''", "'x'", "'2026-01-01'", "$min", "$since", "$n", "$nope"];

   private static readonly string[][] Operators =
   [
      ["==", "!=", "<", "<=", ">", ">="],
      ["+", "-", "*", "/", "%", "??"],
      ["and", "or"],
   ];

   private static readonly string[] Methods =
   [
      ".take(0)", ".take(1)", ".skip(2)", ".distinct()", ".count()", ".first()", ".firstOrDefault()", ".orderBy(id)", ".orderBy(desc(1))",
      ".where(true)", ".where(null)", ".where(id > 1)", ".select(.*)", ".select(x: 1)", ".select(id)", ".groupBy(status)", ".groupBy()", ".any()",
      ".extend(z: id + 1)", ".union(shop.orders.select(id))", ".concat(shop.orders)", ".selectMany(orders)", ".select(n: count())",
   ];

   private static readonly string[] Wrappers = ["toString({0})", "coalesce({0}, null)", "-{0}", "not {0}", "({0} ?? 0)", "iif({0}, 1, 2)", "{0}.count()", "{0} == null", "[{0}]"];

   /// <summary>
   /// A query with a change or a few, most of which leave it meaningful: a name, value or operator swapped for
   /// another, a method call dropped, repeated or added, an expression wrapped; now and then a character cut.
   /// </summary>
   private static string Mangle(string query, Random random)
   {
      string text = query;
      int changes = random.Next(1, 4);
      for (int c = 0; c < changes; c++)
      {
         List<Match> tokens = Tokens().Matches(text).ToList();
         if (tokens.Count == 0) { break; }
         Match token = tokens[random.Next(tokens.Count)];
         string Replace(string with) => text[..token.Index] + with + text[(token.Index + token.Length)..];
         switch (random.Next(8))
         {
            case 0 when char.IsLetter(token.Value[0]) && token.Value is not ("and" or "or" or "not" or "in" or "null" or "true" or "false"):
               text = Replace(Names[random.Next(Names.Length)]);
               break;
            case 1 when char.IsDigit(token.Value[0]) || token.Value[0] is '\'' or '$' || token.Value is "null" or "true" or "false":
               text = Replace(Values[random.Next(Values.Length)]);
               break;
            case 2 when Operators.FirstOrDefault(o => o.Contains(token.Value)) is { } kind:
               text = Replace(kind[random.Next(kind.Length)]);
               break;
            case 3 or 4 when Call(text, random) is (int from, int to):
               // A method call: dropped, or repeated.
               text = random.Next(2) == 0 ? text[..from] + text[to..] : text[..to] + text[from..to] + text[to..];
               break;
            case 5:
               int end = text.LastIndexOf(';') is var semicolon and >= 0 && random.Next(3) == 0 ? semicolon : text.Length;
               text = text[..end] + Methods[random.Next(Methods.Length)] + text[end..];
               break;
            case 6 when char.IsLetter(token.Value[0]):
               text = Replace(string.Format(System.Globalization.CultureInfo.InvariantCulture, Wrappers[random.Next(Wrappers.Length)], token.Value));
               break;
            case 7:
               int at = random.Next(text.Length);
               text = text.Remove(at, Math.Min(random.Next(1, 3), text.Length - at));
               break;
            default:
               text = Replace(Values[random.Next(Values.Length)]);
               break;
         }
      }
      return text;
   }

   /// <summary>The span of a method call (<c>.where(...)</c>) chosen at random: from its dot to past its closing parenthesis.</summary>
   private static (int From, int To)? Call(string text, Random random)
   {
      List<int> dots = [];
      for (int i = 0; i < text.Length - 1; i++)
      {
         if (text[i] == '.' && char.IsLetter(text[i + 1])) { dots.Add(i); }
      }
      while (dots.Count > 0)
      {
         int dot = dots[random.Next(dots.Count)];
         dots.Remove(dot);
         int open = dot + 1;
         while (open < text.Length && (char.IsLetterOrDigit(text[open]) || text[open] == '_')) { open++; }
         if (open >= text.Length || text[open] != '(') { continue; }
         int depth = 0;
         for (int i = open; i < text.Length; i++)
         {
            if (text[i] == '(') { depth++; }
            else if (text[i] == ')' && --depth == 0) { return (dot, i + 1); }
         }
      }
      return null;
   }

   /// <summary>Prepares, explains, counts and runs the query; what went wrong in a way it shouldn't, if anything.</summary>
   private static async Task<string?> TryAsync(QueryEngine engine, string text)
   {
      try
      {
         PreparedQuery prepared = engine.Prepare(new QueryRequest(text) { Parameters = Conformance.Parameters, Timeout = TimeSpan.FromSeconds(5) });
         _ = prepared.Explain(verbose: true);
         if (!prepared.Success)
         {
            return prepared.Diagnostics.Any(d => d.IsError) ? null : "it failed without an error";
         }
         PreparedQuery count = prepared.ForCount();
         _ = count.Explain();
         foreach (PreparedQuery query in new[] { prepared, count }.Where(q => q.Success))
         {
            try
            {
               await using QueryResult result = await query.ExecuteAsync(Token);
               for (int i = 0; i < 100 && await result.ReadAsync(Token); i++) { }
            }
            catch (QueryExecutionException)
            {
               // A query may fail as it runs: first() of no rows, a value that doesn't convert, a division by zero.
            }
         }
         return null;
      }
      catch (Exception e) when (e is not OperationCanceledException || !Token.IsCancellationRequested)
      {
         return $"{e.GetType().Name}: {e.Message}{Environment.NewLine}{e.StackTrace?.Split(Environment.NewLine).FirstOrDefault()?.Trim()}";
      }
   }

   private static async Task FuzzAsync(QueryEngine engine, IReadOnlyList<string> queries, int seed, int count)
   {
      Random random = new(seed);
      List<string> failures = [];
      int planned = 0;
      for (int i = 0; i < count; i++)
      {
         string text = Mangle(queries[random.Next(queries.Count)], random);
         if (engine.Prepare(new QueryRequest(text) { Parameters = Conformance.Parameters }).Success) { planned++; }
         if (await TryAsync(engine, text) is { } failure) { failures.Add($"{text}{Environment.NewLine}  {failure}"); }
      }
      failures.ShouldBeEmpty(string.Join(Environment.NewLine + Environment.NewLine, failures.Distinct().Take(20)));
      // Enough of them still mean something to be planned and run, not only parsed.
      planned.ShouldBeGreaterThan(count / 10);
   }

   [Theory]
   [InlineData(1)]
   [InlineData(2)]
   [InlineData(3)]
   public async Task MangledQueriesOfOneSourceFailCleanly(int seed)
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      await FuzzAsync(sources.Engine(Conformance.Overlay), Conformance.Queries, seed, 3000);
   }

   [Theory]
   [InlineData(4)]
   [InlineData(5)]
   public async Task MangledQueriesAcrossSourcesFailCleanly(int seed)
   {
      await using TestSources sources = await Conformance.SplitShopAsync();
      await FuzzAsync(sources.Engine(Conformance.SplitOverlay, new QueryEngineOptions { BindJoins = BindJoinMode.Always }),
                      Conformance.Queries.Select(Conformance.Split).ToList(), seed, 1500);
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   /// <summary>Text that isn't a query at all, or is one only just.</summary>
   [Theory]
   [InlineData("")]
   [InlineData(" ")]
   [InlineData(";")]
   [InlineData(";;;")]
   [InlineData("a := shop.orders;")]
   [InlineData("a := a; a")]
   [InlineData("a := b; b := a; a")]
   [InlineData("shop")]
   [InlineData("shop.")]
   [InlineData("shop.orders.")]
   [InlineData("shop.orders.select()")]
   [InlineData("shop.orders.select(.*)")]
   [InlineData("shop.orders.where()")]
   [InlineData("shop.orders.groupBy().select()")]
   [InlineData("shop.orders.take(-1)")]
   [InlineData("shop.orders.take(9223372036854775807).skip(9223372036854775807)")]
   [InlineData("shop.orders.select(x: 9223372036854775807 + 1)")]
   [InlineData("shop.orders.select(x: 1 / 0, y: 1 % 0, z: 1.0 / 0)")]
   [InlineData("shop.orders.select(x: substring(name, -5, -5))")]
   [InlineData("shop.orders.first().first()")]
   [InlineData("shop.orders.select(customer).select(customer)")]
   [InlineData("shop.orders.select(o: it).select(p: o).select(q: p.customer.orders.count())")]
   [InlineData("(((((((((((shop.orders)))))))))))")]
   [InlineData("shop.orders.where(id in ())")]
   [InlineData("shop.orders.union(shop.customers)")]
   [InlineData("shop.orders.join(shop.orders, outer.id == inner.id).join(shop.orders, outer.id == inner.id)")]
   [InlineData("\u0000")]
   [InlineData("shop.orders.where(status == '\u0000')")]
   [InlineData("shop.orders.select(\"\": id)")]
   [InlineData("shop.orders.select(x: $)")]
   [InlineData("shop.orders.groupBy(status).select(status, a: customer_id.any())")]
   [InlineData("shop.orders.groupBy(status).select(status, a: total.any(id > 1))")]
   [InlineData("shop.orders.groupBy(status).select(status, a: total.all(it > 1), b: total.any(it > 100))")]
   [InlineData("shop.orders.groupBy(status).select(status, a: total.count(id > 1))")]
   public async Task OddTextFailsCleanly(string text)
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      (await TryAsync(sources.Engine(Conformance.Overlay), text)).ShouldBeNull();
   }

   /// <summary>A query too long, or whose plan would be too large, is refused before it is planned.</summary>
   [Fact]
   public async Task QueriesTooLargeAreRefused()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { MaxQueryLength = 1_000 });
      PreparedQuery tooLong = engine.Prepare("shop.orders.where(" + string.Join(" or ", Enumerable.Range(1, 200).Select(i => $"id == {i}")) + ")");
      tooLong.Diagnostics.Single().Code.ShouldBe(Diagnostics.DiagnosticCodes.QueryTooLong);
      tooLong.Diagnostics.Single().Message.ShouldStartWith("The query is 2,");

      StringBuilder doubling = new("a0 := shop.orders.select(id);\n");
      for (int i = 1; i <= 30; i++) { doubling.Append($"a{i} := a{i - 1}.concat(a{i - 1});\n"); }
      doubling.Append("a30.count()");
      PreparedQuery tooLarge = sources.Engine().Prepare(doubling.ToString());
      tooLarge.Diagnostics.Single().Code.ShouldBe(Diagnostics.DiagnosticCodes.PlanTooLarge);
      tooLarge.Diagnostics.Single().Message.ShouldStartWith("This query is too large to plan: it would make more than 100,000 columns.");
   }
}
