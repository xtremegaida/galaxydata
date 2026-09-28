using System;
using System.IO;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using GalaxyData.Query.Providers;
using Microsoft.Data.Sqlite;

namespace GalaxyData.Query.IntegrationTests;

/// <summary>Opens in-memory copies of the fixture databases in tests/fixtures.</summary>
internal static class Fixtures
{
   public static string Sql(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

   public static async Task<SqliteConnection> OpenSqliteShopAsync()
   {
      SqliteConnection connection = new("Data Source=:memory:");
      await connection.OpenAsync();
      await connection.ExecuteAsync(Sql("shop.sqlite.sql"));
      return connection;
   }

   public static async Task<DuckDBConnection> OpenDuckDbShopAsync()
   {
      DuckDBConnection connection = new("Data Source=:memory:");
      await connection.OpenAsync();
      await connection.ExecuteAsync(Sql("shop.duckdb.sql"));
      return connection;
   }
}
