using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Execution;
using GalaxyData.Query.IntegrationTests.Execution;
using GalaxyData.Query.Language;
using GalaxyData.Query.Results;
using GalaxyData.Query.Types;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.ContainerTests;

/// <summary>A value of each type the servers have: read as their logical types, alone and through the merge engine, and sent back as parameters.</summary>
public sealed class ValueTests(Servers servers)
{
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task ReadsAValueOfEachType(ServerKind server)
   {
      await using TestSources sources = await (await servers.KindsAsync(server)).SourcesAsync("k");
      Golden.Match(await ReadAsync(sources.Engine()), suffix: server.ToString());
   }

   /// <summary>Through the merge engine the values are the same, but for those of unknown types, which it holds as text.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task ReadsTheSameValuesThroughTheMergeEngine(ServerKind server)
   {
      await using TestSources sources = await (await servers.KindsAsync(server)).SourcesAsync("k");
      string direct = await ReadAsync(sources.Engine());
      string merged = await ReadAsync(sources.Engine(options: new QueryEngineOptions { PushDown = false }));
      Golden.Match(Differences(direct, merged), suffix: server.ToString());
   }

   /// <summary>Each value read from row 1, sent back as a parameter of its type, finds the row.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task ParametersOfEachTypeFindTheirRow(ServerKind server)
   {
      await using TestSources sources = await (await servers.KindsAsync(server)).SourcesAsync("k");
      QueryEngine engine = sources.Engine();
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest("k.kinds.where(id == 1)"), TestContext.Current.CancellationToken);
      object?[] row = (await result.ToListAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
      List<string> found = [];
      foreach (ResultColumn column in result.Schema.VisibleColumns)
      {
         if (column.Type.Kind is ScalarKind.Unknown or ScalarKind.Json or ScalarKind.Binary || column.Name == "id") { continue; }
         object? value = row[column.Ordinal];
         string query = QueryText.FormatPath(["k", "kinds"]) + $".where({QueryText.QuoteName(column.Name)} == $v).select(id)";
         QueryParameters parameters = new QueryParameters().Add("v", value, column.Type);
         try
         {
            await using QueryResult match = await engine.ExecuteAsync(new QueryRequest(query) { Parameters = parameters }, TestContext.Current.CancellationToken);
            found.Add($"{column.Name}: {TestSources.Format(await match.ToListAsync(TestContext.Current.CancellationToken)).TrimEnd()}");
         }
         catch (QueryExecutionException e)
         {
            found.Add($"{column.Name}: {e.Message}");
         }
      }
      Golden.Match(string.Join("\n", found), suffix: server.ToString());
   }

   /// <summary>Row 1 and row 2 (nulls) of kinds: each column's type, then each value with its CLR type.</summary>
   private static async Task<string> ReadAsync(QueryEngine engine)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest("k.kinds.orderBy(id)"), TestContext.Current.CancellationToken);
      IReadOnlyList<object?[]> rows = await result.ToListAsync(TestContext.Current.CancellationToken);
      StringBuilder text = new();
      for (int i = 0; i < result.Schema.VisibleColumns.Count; i++)
      {
         ResultColumn column = result.Schema.VisibleColumns[i];
         text.Append(column.Name).Append(": ").Append(column.Type).Append(" = ").AppendJoin(" / ", rows.Select(r => Describe(r[i]))).Append('\n');
      }
      return text.ToString();
   }

   private static string Describe(object? value) => value switch
   {
      null => "null",
      byte[] bytes => "byte[] " + Convert.ToHexString(bytes),
      string text => "string '" + text + "'",
      DateTime dateTime => $"DateTime {dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)} ({dateTime.Kind})",
      DateTimeOffset offset => "DateTimeOffset " + offset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
      DateOnly date => "DateOnly " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
      TimeOnly time => "TimeOnly " + time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
      IEnumerable items and not string => value.GetType().Name + " [" + string.Join(", ", items.Cast<object?>().Select(o => Convert.ToString(o, CultureInfo.InvariantCulture))) + "]",
      _ => value.GetType().Name + " " + Convert.ToString(value, CultureInfo.InvariantCulture),
   };

   private static string Differences(string direct, string merged)
   {
      string[] a = direct.Split('\n');
      string[] b = merged.Split('\n');
      StringBuilder text = new();
      for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
      {
         string left = i < a.Length ? a[i] : string.Empty;
         string right = i < b.Length ? b[i] : string.Empty;
         if (left != right) { text.Append("- ").Append(left).Append('\n').Append("+ ").Append(right).Append('\n'); }
      }
      return text.Length == 0 ? "(the same)" : text.ToString();
   }
}
