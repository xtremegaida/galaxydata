using System;
using System.Data.Common;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Providers;

namespace GalaxyData.Testing;

/// <summary>
/// The fixture's HyperWhirlpool warehouse in a test ClickHouse server: its domains web and crm, each a database of its
/// own, and the catalog whose schema table holds the document describing them (<c>hyperwhirlpool.clickhouse.sql</c>
/// and <c>hyperwhirlpool.schema.json</c>, in the test's output). Dropped when disposed.
/// </summary>
internal sealed partial class HyperWhirlpoolWarehouse : IAsyncDisposable
{
   /// <summary>The model version the fixture's document describes, as a connection reports it.</summary>
   public const string Version = "12 (3f9a0c1d2e4b)";

   private readonly Func<DbConnection> open;

   private HyperWhirlpoolWarehouse(Func<DbConnection> open, string web, string crm, string catalog)
   {
      this.open = open;
      Web = web;
      Crm = crm;
      Catalog = catalog;
   }

   /// <summary>The database of the web domain.</summary>
   public string Web { get; }

   /// <summary>The database of the crm domain.</summary>
   public string Crm { get; }

   /// <summary>The database whose <c>schema</c> table holds the document.</summary>
   public string Catalog { get; }

   /// <summary>The document, naming the databases the domains are in.</summary>
   public string Document => Fill(Fixture("hyperwhirlpool.schema.json"));

   /// <summary>
   /// Makes the warehouse with <paramref name="open"/>'s connections (a login that may create databases), its domains in
   /// databases of their own, and its catalog in <paramref name="catalog"/> (dropped first, if it is there), or one of its own.
   /// </summary>
   public static async Task<HyperWhirlpoolWarehouse> CreateAsync(Func<DbConnection> open, string? catalog = null, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(open);
      string id = "gdq_hw_" + Guid.NewGuid().ToString("N")[..10];
      HyperWhirlpoolWarehouse warehouse = new(open, id + "_web", id + "_crm", catalog ?? id + "_catalog");
      try
      {
         await using DbConnection connection = open();
         await connection.OpenAsync(cancellationToken);
         await connection.ExecuteAsync($"DROP DATABASE IF EXISTS {warehouse.Catalog} SYNC", cancellationToken);
         foreach (string database in (string[])[warehouse.Web, warehouse.Crm, warehouse.Catalog])
         {
            await connection.ExecuteAsync($"CREATE DATABASE {database}", cancellationToken);
         }
         // ClickHouse runs one statement a request.
         foreach (string statement in warehouse.Fill(Fixture("hyperwhirlpool.clickhouse.sql")).Split(';'))
         {
            if (Comments().Replace(statement, string.Empty).Trim().Length > 0) { await connection.ExecuteAsync(statement, cancellationToken); }
         }
         await connection.ExecuteAsync(
            $"INSERT INTO {warehouse.Catalog}.schema SELECT 12, '3f9a0c1d2e4b', toDateTime64('2026-10-12 08:00:00', 3, 'UTC'), {{document:String}}",
            cancellationToken, ("document", warehouse.Document));
         return warehouse;
      }
      catch
      {
         await warehouse.DisposeAsync();
         throw;
      }
   }

   private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

   private string Fill(string text) => text
      .Replace("{web}", Web, StringComparison.Ordinal)
      .Replace("{crm}", Crm, StringComparison.Ordinal)
      .Replace("{catalog}", Catalog, StringComparison.Ordinal);

   [GeneratedRegex(@"^\s*--.*$", RegexOptions.Multiline)]
   private static partial Regex Comments();

   public async ValueTask DisposeAsync()
   {
      try
      {
         await using DbConnection connection = open();
         await connection.OpenAsync();
         foreach (string database in (string[])[Web, Crm, Catalog])
         {
            await connection.ExecuteAsync($"DROP DATABASE IF EXISTS {database} SYNC");
         }
      }
      catch (DbException)
      {
         // A server that has gone keeps them until it is started again.
      }
   }
}
