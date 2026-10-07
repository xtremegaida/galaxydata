using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Connectors;
using GalaxyData.Query.Execution;
using Microsoft.Data.SqlClient;

namespace GalaxyData.Query.SqlServer;

/// <summary>SQL Server databases, connected with a connection string, pooled as SqlClient pools.</summary>
public sealed class SqlServerConnector : Connector
{
   public override SourceProvider Provider => SqlServerSourceProvider.Instance;

   public override ConnectionKind Kind { get; } = new SqlServerKind();

   public override CommandLineHelp CommandLine { get; } =
      new(CommandLineTargets.ConnectionString, "a connection string", "Server=localhost;Database=shop;Integrated Security=true");

   public override ValueTask<OpenedSource> OpenAsync(CommandLineSource source, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      SqlConnection keeper = Create(source, () => new SqlConnection(source.Target));
      return OpenDatabaseAsync(source, keeper, () => new SqlConnection(source.Target), null, cancellationToken);
   }
}

public sealed class SqlServerKind : ConnectionKind
{
   private static readonly string[] EncryptModes = ["Mandatory", "Optional", "Strict"];

   public override string Id => SqlServerSchemaIntrospector.ProviderKind;

   public override string DisplayName => "SQL Server";

   public override string? RawExample => "Data Source=db.example.com;Initial Catalog=shop;User ID=reader;Password=...";

   public override IReadOnlyList<FieldDto> Fields { get; } =
   [
      new("Data Source", "Server", FieldType.Text) { Required = true, Group = "connection", Placeholder = "db.example.com,1433 or host\\instance" },
      new("Initial Catalog", "Database", FieldType.Text) { Group = "connection" },
      new("Integrated Security", "Windows authentication", FieldType.Bool)
      {
         Default = "False",
         Group = "connection",
         Help = "Sign in as the account the application runs as",
      },
      new("User ID", "User name", FieldType.Text) { Group = "connection", VisibleWhen = new VisibleWhenDto("Integrated Security", ["False"]) },
      new("Password", "Password", FieldType.Password) { Group = "connection", VisibleWhen = new VisibleWhenDto("Integrated Security", ["False"]) },
      new("Encrypt", "Encryption", FieldType.Select)
      {
         Default = "Mandatory",
         Group = "security",
         Choices = EncryptModes.Select(m => new ChoiceDto(m, m)).ToList(),
      },
      new("Trust Server Certificate", "Trust the server's certificate", FieldType.Bool)
      {
         Default = "False",
         Group = "security",
         Help = "Accept a certificate no trusted authority signed (a server's own); the connection is still encrypted",
      },
      new("Connect Timeout", "Connect timeout (s)", FieldType.Number) { Default = "15", Group = "advanced", Min = 0 },
      new("Command Timeout", "Command timeout (s)", FieldType.Number) { Default = "30", Group = "advanced", Min = 0 },
      new("Application Name", "Application name", FieldType.Text) { Group = "advanced", Placeholder = "GalaxyData" },
      Other,
   ];

   /// <summary>A file the server attaches is the server's business, not a source's.</summary>
   protected override IReadOnlyList<string> Reserved { get; } = ["AttachDBFilename"];

   protected override DbConnectionStringBuilder NewBuilder() => new SqlConnectionStringBuilder();

   public override SourceConnector Connector(string connectionString) => new ProviderConnector(() => new SqlConnection(connectionString), () =>
   {
      using SqlConnection pooled = new(connectionString);
      SqlConnection.ClearPool(pooled);
   });

   protected override string Unpooled(string connectionString) => new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ToString();

   protected override async Task<string> FoundAsync(DbConnection connection, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      object? database = await ScalarAsync(connection, "SELECT DB_NAME()", cancellationToken);
      return $"Connected to SQL Server {connection.ServerVersion}, database {database}";
   }
}
