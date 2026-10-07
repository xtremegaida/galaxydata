using System;

namespace GalaxyData.Testing;

/// <summary>
/// The database servers tests may connect to: the PostgreSQL, SQL Server and ClickHouse that
/// <c>tests/GalaxyData.Query.ContainerTests/servers.sh up</c> starts, or those <c>GDQ_TEST_POSTGRES</c>,
/// <c>GDQ_TEST_SQLSERVER</c> and <c>GDQ_TEST_CLICKHOUSE</c> name (connection strings of logins that may create databases).
/// </summary>
internal static class TestServers
{
   public const string DefaultPostgres = "Host=127.0.0.1;Port=55432;Username=postgres;Password=GdqTest2026;Timeout=5";

   public const string DefaultSqlServer = "Server=127.0.0.1,51433;User ID=sa;Password=GdqTest2026;TrustServerCertificate=True;Connect Timeout=5";

   public const string DefaultClickHouse = "Host=127.0.0.1;Port=58123;Username=gdq;Password=GdqTest2026";

   /// <summary>The server's connection string when the environment names one; null for the default.</summary>
   public static string? Named(bool postgres) => Named(postgres ? "GDQ_TEST_POSTGRES" : "GDQ_TEST_SQLSERVER");

   /// <summary>The connection string the environment variable names; null when it names none.</summary>
   public static string? Named(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } named ? named : null;

   public static string Postgres => Named(postgres: true) ?? DefaultPostgres;

   public static string SqlServer => Named(postgres: false) ?? DefaultSqlServer;

   public static string ClickHouse => Named("GDQ_TEST_CLICKHOUSE") ?? DefaultClickHouse;
}
