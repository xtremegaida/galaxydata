using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Excel;
using GalaxyData.Query.PostgreSql;
using GalaxyData.Query.Sqlite;
using GalaxyData.Query.SqlServer;

namespace GalaxyData.Connectors.BuiltIn;

/// <summary>
/// The connectors the application and the command line come with: the one list of them. A new connector is a
/// project of its own, a reference to it here, and a line below; the hosts take the list and name no database.
/// </summary>
public static class BuiltInConnectors
{
   /// <summary>The connectors, in the order the application lists their kinds.</summary>
   public static ConnectorSet Create(ConnectorContext context) => new(
   [
      new PostgreSqlConnector(),
      new SqlServerConnector(),
      new SqliteConnector(),
      new DuckDbConnector(),
      new ExcelConnector(context),
      new ClickHouseConnector(),
   ]);
}
