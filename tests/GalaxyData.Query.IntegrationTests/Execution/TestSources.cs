using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Providers;
using GalaxyData.Query.Results;
using GalaxyData.Query.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>
/// In-memory databases the engine can open connections to: a shared-cache SQLite database, or a DuckDB database
/// reached through duplicates of one connection. The first connection keeps the database alive.
/// </summary>
internal sealed class TestSources : IConnectionFactory, IAsyncDisposable
{
   private readonly Dictionary<string, (DbConnection Keeper, Func<DbConnection> Open)> sources = new(StringComparer.Ordinal);
   private readonly CatalogBuilder builder = new();

   public int Opened { get; private set; }

   public async Task<TestSources> AddSqliteAsync(string alias, string script, bool trustForeignKeys = false)
   {
      string connectionString = $"Data Source=gdq_{alias}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
      SqliteConnection keeper = new(connectionString);
      await keeper.OpenAsync();
      await keeper.ExecuteAsync(script);
      await AddAsync(alias, keeper, () => new SqliteConnection(connectionString), SqliteSourceProvider.Instance, trustForeignKeys);
      return this;
   }

   public async Task<TestSources> AddDuckDbAsync(string alias, string script)
   {
      DuckDBConnection keeper = new("Data Source=:memory:");
      await keeper.OpenAsync();
      await keeper.ExecuteAsync(script);
      await AddAsync(alias, keeper, keeper.Duplicate, DuckDbSourceProvider.Instance, trustForeignKeys: false);
      return this;
   }

   private async Task AddAsync(string alias, DbConnection keeper, Func<DbConnection> open, SourceProvider provider, bool trustForeignKeys)
   {
      SourceSchema schema = await provider.Introspector.IntrospectAsync(keeper, IntrospectionOptions.Default, CancellationToken.None);
      builder.AddSource(new SourceInfo(alias, provider.ProviderKind, schema.DefaultSchema) { TrustForeignKeys = trustForeignKeys }, schema);
      sources.Add(alias, (keeper, open));
   }

   public QueryEngine Engine(CatalogOverlay? overlay = null, QueryEngineOptions? options = null) =>
      new(builder.WithOverlay(overlay ?? CatalogOverlay.Empty).Build(), this, [SqliteSourceProvider.Instance, DuckDbSourceProvider.Instance], options);

   public async ValueTask<DbConnection> OpenAsync(SourceInfo source, CancellationToken cancellationToken)
   {
      DbConnection connection = sources[source.Alias].Open();
      if (connection.State != System.Data.ConnectionState.Open) { await connection.OpenAsync(cancellationToken); }
      Opened++;
      return connection;
   }

   public async ValueTask DisposeAsync()
   {
      foreach ((DbConnection keeper, _) in sources.Values) { await keeper.DisposeAsync(); }
   }

   public static Task<TestSources> SqliteShopAsync() => new TestSources().AddSqliteAsync("shop", Fixtures.Sql("shop.sqlite.sql"));

   public static Task<TestSources> DuckDbShopAsync() => new TestSources().AddDuckDbAsync("shop", Fixtures.Sql("shop.duckdb.sql"));

   /// <summary>
   /// Rows as text, one line each: <c>1001 | 250.00 | 'Acme Ltd' | null</c>. With a schema, only the visible values, and
   /// the hidden ones after <c>||</c> when <paramref name="hidden"/> is set.
   /// </summary>
   public static string Format(IEnumerable<object?[]> rows, ResultSchema? schema = null, bool hidden = false)
   {
      StringBuilder text = new();
      int visible = schema?.VisibleColumns.Count ?? int.MaxValue;
      foreach (object?[] row in rows)
      {
         text.AppendJoin(" | ", row.Take(visible).Select(Value));
         if (hidden && row.Length > visible) { text.Append(" || ").AppendJoin(" | ", row.Skip(visible).Select(Value)); }
         text.AppendLine();
      }
      return text.ToString();
   }

   /// <summary>The rest of a result's rows, visible values only.</summary>
   public static async Task<string> RowsAsync(QueryResult result) =>
      Format(await result.ToListAsync(TestContext.Current.CancellationToken), result.Schema);

   public static string Header(ResultSchema schema)
   {
      string text = string.Join(" | ", schema.VisibleColumns.Select(c => c.Name));
      List<ResultColumn> hidden = schema.Columns.Where(c => c.IsHidden).ToList();
      return hidden.Count == 0 ? text : text + " || " + string.Join(" | ", hidden.Select(c => c.Name));
   }

   private static string Value(object? value) => value switch
   {
      null => "null",
      string s => "'" + s + "'",
      bool b => b ? "true" : "false",
      double d => d.ToString("R", CultureInfo.InvariantCulture),
      DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
      DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
      _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?",
   };
}
