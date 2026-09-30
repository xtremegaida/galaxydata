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
using GalaxyData.Query.Excel;
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
/// reached through duplicates of one connection. The first connection keeps the database alive. Folders of
/// workbooks are registered with an Excel provider over <see cref="Merge"/>, which opens their connections itself.
/// </summary>
internal sealed class TestSources : IConnectionFactory, IAsyncDisposable
{
   private readonly Dictionary<string, (DbConnection Keeper, Func<DbConnection> Open)> sources = new(StringComparer.Ordinal);
   private readonly CatalogBuilder builder = new();
   private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> openedBySource = new(StringComparer.Ordinal);
   private DuckDbMergeEngine? merge;
   private ExcelSourceProvider? excel;
   private int opened;

   public int Opened => Volatile.Read(ref opened);

   /// <summary>The connections opened to one source.</summary>
   public int OpenedTo(string alias) => openedBySource.GetValueOrDefault(alias);

   /// <summary>The merge engine of the engines made here, unless one is given; made when first needed.</summary>
   public DuckDbMergeEngine Merge => merge ??= new DuckDbMergeEngine();

   /// <summary>The Excel provider, over <see cref="Merge"/>; made when first needed.</summary>
   public ExcelSourceProvider Excel => excel ??= new ExcelSourceProvider(Merge);

   /// <summary>Adds a folder of workbooks as a source; its schema, as read, is kept in <see cref="Schemas"/>.</summary>
   public async Task<TestSources> AddExcelAsync(string alias, string path, ExcelFolderOptions? options = null)
   {
      Excel.AddFolder(alias, options ?? new ExcelFolderOptions { Path = path });
      SourceSchema schema = await Excel.IntrospectAsync(alias, IntrospectionOptions.Default, CancellationToken.None);
      builder.AddSource(Excel.Source(alias), schema);
      Schemas[alias] = schema;
      return this;
   }

   /// <summary>The schemas of the folders added, by alias.</summary>
   public Dictionary<string, SourceSchema> Schemas { get; } = new(StringComparer.Ordinal);

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

   /// <summary>An engine over the sources; queries that combine them run in <see cref="Merge"/>, or <paramref name="merge"/>, unless <paramref name="noMerge"/>.</summary>
   public QueryEngine Engine(CatalogOverlay? overlay = null, QueryEngineOptions? options = null, IMergeEngine? merge = null, bool noMerge = false) =>
      new(Catalog(overlay), this, excel == null ? [SqliteSourceProvider.Instance, DuckDbSourceProvider.Instance] : [SqliteSourceProvider.Instance, DuckDbSourceProvider.Instance, excel],
          noMerge ? null : merge ?? Merge, options);

   public QueryCatalog Catalog(CatalogOverlay? overlay = null) => builder.WithOverlay(overlay ?? CatalogOverlay.Empty).Build();

   /// <summary>Runs a script in a source's database, as it is now.</summary>
   public async Task RunAsync(string alias, string script) => await sources[alias].Keeper.ExecuteAsync(script);

   public async ValueTask<DbConnection> OpenAsync(SourceInfo source, CancellationToken cancellationToken)
   {
      DbConnection connection = sources[source.Alias].Open();
      if (connection.State != System.Data.ConnectionState.Open) { await connection.OpenAsync(cancellationToken); }
      Interlocked.Increment(ref opened);
      openedBySource.AddOrUpdate(source.Alias, 1, (_, n) => n + 1);
      return connection;
   }

   public async ValueTask DisposeAsync()
   {
      foreach ((DbConnection keeper, _) in sources.Values) { await keeper.DisposeAsync(); }
      excel?.Dispose();
      merge?.Dispose();
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
