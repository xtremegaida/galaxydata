using System;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.HyperWhirlpool;

/// <summary>
/// HyperWhirlpool warehouses, read through their query service, which speaks ClickHouse's HTTP protocol and runs each
/// statement in ClickHouse, on the views the warehouse's model makes public. Statements are written, sent and read
/// as a ClickHouse server's are (<see cref="ClickHouseSourceProvider"/>); the schema is the model's
/// (<see cref="HyperWhirlpoolSchemaIntrospector"/>). Read-only: the warehouse is built from events, which don't change.
/// </summary>
public sealed class HyperWhirlpoolSourceProvider : SourceProvider
{
   public const string Kind = "hyperwhirlpool";

   /// <summary>What the query service is called when it can't be reached.</summary>
   internal const string ServiceName = "HyperWhirlpool query service";

   private static readonly ClickHouseSourceProvider ClickHouse = ClickHouseSourceProvider.Instance;

   private HyperWhirlpoolSourceProvider() { }

   public static HyperWhirlpoolSourceProvider Instance { get; } = new();

   public override string ProviderKind => Kind;

   public override SqlDialect Dialect => ClickHouseDialect.Instance;

   public override ISchemaIntrospector Introspector { get; } = new HyperWhirlpoolSchemaIntrospector();

   /// <summary>A data source for a connection string, whose connections share one HTTP client to the query service.</summary>
   public static ClickHouseDataSource CreateDataSource(string connectionString) =>
      ClickHouseSourceProvider.CreateDataSource(WithDefaults(connectionString), ServiceName);

   /// <summary>
   /// The connection string with what a query service is reached by unless it says otherwise: HTTPS, on 443 (80 over
   /// HTTP), as the form says, where ClickHouse.Driver's own defaults are a ClickHouse server's (HTTP, on 8123).
   /// </summary>
   internal static string WithDefaults(string connectionString)
   {
      ClickHouseConnectionStringBuilder builder = new(connectionString);
      if (!builder.ContainsKey("Protocol")) { builder["Protocol"] = "https"; }
      bool https = string.Equals(Convert.ToString(builder["Protocol"], CultureInfo.InvariantCulture), "https", StringComparison.OrdinalIgnoreCase);
      if (!builder.ContainsKey("Port")) { builder["Port"] = https ? "443" : "80"; }
      return builder.ConnectionString;
   }

   /// <summary>The settings the language's meaning depends on, on every statement of the connection, as ClickHouse's are.</summary>
   public override ValueTask PrepareConnectionAsync(DbConnection connection, CancellationToken cancellationToken) =>
      ClickHouse.PrepareConnectionAsync(connection, cancellationToken);

   public override void PrepareCommand(DbCommand command) => ClickHouse.PrepareCommand(command);

   /// <summary>
   /// Stops the HTTP request. The query service runs nothing but reads (a <c>KILL QUERY</c> would stop others' statements
   /// too), so the statement isn't killed by its id: the service drops its request to ClickHouse when the client goes,
   /// and ClickHouse stops a read whose client has gone.
   /// </summary>
   public override void CancelCommand(DbCommand command)
   {
      ArgumentNullException.ThrowIfNull(command);
      command.Cancel();
   }

   public override ValueTask PrepareWriteAsync(DbConnection connection, SourceInfo source, CancellationToken cancellationToken) =>
      throw new NotSupportedException("HyperWhirlpool sources are read-only: rows of them can't be changed here");

   public override void BindParameter(DbParameter parameter, object? value, ScalarType type) => ClickHouse.BindParameter(parameter, value, type);

   public override object? ReadValue(DbDataReader reader, int ordinal) => ClickHouse.ReadValue(reader, ordinal);
}
