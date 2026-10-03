using System;

namespace GalaxyData.Testing;

/// <summary>
/// The database servers tests may connect to: the PostgreSQL and SQL Server that
/// <c>tests/GalaxyData.Query.ContainerTests/servers.sh up</c> starts, or those <c>GDQ_TEST_POSTGRES</c> and
/// <c>GDQ_TEST_SQLSERVER</c> name (connection strings of logins that may create databases).
/// </summary>
internal static class TestServers
{
   public const string DefaultPostgres = "Host=127.0.0.1;Port=55432;Username=postgres;Password=GdqTest2026;Timeout=5";

   public const string DefaultSqlServer = "Server=127.0.0.1,51433;User ID=sa;Password=GdqTest2026;TrustServerCertificate=True;Connect Timeout=5";

   /// <summary>The server's connection string when the environment names one; null for the default.</summary>
   public static string? Named(bool postgres) =>
      Environment.GetEnvironmentVariable(postgres ? "GDQ_TEST_POSTGRES" : "GDQ_TEST_SQLSERVER") is { Length: > 0 } named ? named : null;

   public static string Postgres => Named(postgres: true) ?? DefaultPostgres;

   public static string SqlServer => Named(postgres: false) ?? DefaultSqlServer;
}
