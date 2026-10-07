using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.PostgreSql;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Sqlite;
using GalaxyData.Query.SqlServer;

namespace GalaxyData.Query.Tests.Sql;

/// <summary>The dialects of the connectors in this repository, which the engine's tests write SQL in.</summary>
internal static class TestDialects
{
   public static SqlDialect Sqlite => SqliteDialect.Instance;

   public static SqlDialect DuckDb => DuckDbDialect.Instance;

   public static SqlDialect PostgreSql => PostgreSqlDialect.Instance;

   public static SqlDialect SqlServer => SqlServerDialect.Instance;

   public static SqlDialect ClickHouse => ClickHouseDialect.Instance;

   public static IReadOnlyList<SqlDialect> All { get; } = [Sqlite, DuckDb, PostgreSql, SqlServer, ClickHouse];

   /// <summary>The dialects of sources that can be changed, which plans of changes are written in: ClickHouse's are read-only.</summary>
   public static IReadOnlyList<SqlDialect> Writable { get; } = [Sqlite, DuckDb, PostgreSql, SqlServer];

   public static SqlDialect Of(string providerKind) => All.Single(d => d.ProviderKind == providerKind);
}
