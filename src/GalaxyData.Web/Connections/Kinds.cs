using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using GalaxyData.Query.PostgreSql;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace GalaxyData.Web.Connections;

public sealed class PostgreSqlKind : ConnectionKind
{
   /// <summary>Sessions of read-only connections start read-only, so nothing they run writes.</summary>
   public const string ReadOnlyOption = "-c default_transaction_read_only=on";

   private static readonly string[] SslModes = ["Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull"];

   public override string Id => PostgreSqlSchemaIntrospector.ProviderKind;

   public override string DisplayName => "PostgreSQL";

   public override string? RawExample => "Host=db.example.com;Database=shop;Username=reader;Password=...";

   public override IReadOnlyList<FieldDto> Fields { get; } =
   [
      new("Host", "Host", FieldType.Text) { Required = true, Group = "connection", Placeholder = "db.example.com" },
      new("Port", "Port", FieldType.Number) { Default = "5432", Group = "connection", Min = 1, Max = 65535 },
      new("Database", "Database", FieldType.Text) { Required = true, Group = "connection" },
      new("Username", "User name", FieldType.Text) { Group = "connection" },
      new("Password", "Password", FieldType.Password) { Group = "connection" },
      new("SSL Mode", "SSL", FieldType.Select)
      {
         Default = "Prefer",
         Group = "security",
         Choices = SslModes.Select(m => new ChoiceDto(m, m)).ToList(),
      },
      new("Root Certificate", "Root certificate", FieldType.FilePath)
      {
         Group = "security",
         VisibleWhen = new VisibleWhenDto("SSL Mode", ["VerifyCA", "VerifyFull"]),
         Help = "The certificate (PEM) the server's must be signed by",
      },
      new("Timeout", "Connect timeout (s)", FieldType.Number) { Default = "15", Group = "advanced", Min = 1, Max = 1024 },
      new("Command Timeout", "Command timeout (s)", FieldType.Number) { Default = "30", Group = "advanced", Min = 0 },
      new("Application Name", "Application name", FieldType.Text) { Group = "advanced", Placeholder = "GalaxyData" },
      new("Search Path", "Search path", FieldType.Text) { Group = "advanced" },
      Other,
   ];

   public override IReadOnlyList<string> PathKeywords { get; } = ["Root Certificate", "SSL Certificate", "SSL Key", "Passfile"];

   protected override DbConnectionStringBuilder NewBuilder() => new NpgsqlConnectionStringBuilder();

   protected override void Restrict(DbConnectionStringBuilder builder, bool readOnly)
   {
      if (!readOnly) { return; }
      NpgsqlConnectionStringBuilder npgsql = (NpgsqlConnectionStringBuilder)builder;
      npgsql.Options = string.IsNullOrWhiteSpace(npgsql.Options) ? ReadOnlyOption : npgsql.Options + " " + ReadOnlyOption;
   }

   /// <summary>From a data source of the provider's making, which reads enums and other types queries read.</summary>
   public override SourceConnector Connector(string connectionString) => new DataSourceConnector(PostgreSqlSourceProvider.CreateDataSource(connectionString));

   protected override string Unpooled(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ToString();

   protected override async Task<string> FoundAsync(DbConnection connection, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      await PostgreSqlSourceProvider.Instance.PrepareConnectionAsync(connection, cancellationToken);
      object? database = await ScalarAsync(connection, "SELECT current_database()", cancellationToken);
      return $"Connected to PostgreSQL {connection.ServerVersion}, database {database}";
   }
}

public sealed class SqlServerKind : ConnectionKind
{
   private static readonly string[] EncryptModes = ["Mandatory", "Optional", "Strict"];

   public override string Id => Query.SqlServer.SqlServerSchemaIntrospector.ProviderKind;

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

public sealed class SqliteKind : ConnectionKind
{
   public const string EnforceForeignKeysOption = "enforceForeignKeys";

   public override string Id => Query.Sqlite.SqliteSourceProvider.Instance.ProviderKind;

   public override string DisplayName => "SQLite";

   public override string? RawExample => "Data Source=C:\\data\\files\\shop.db";

   public override IReadOnlyList<FieldDto> Fields { get; } =
   [
      new("Data Source", "Database file", FieldType.FilePath) { Required = true, Group = "connection" },
      new("Default Timeout", "Lock timeout (s)", FieldType.Number)
      {
         Default = "30",
         Group = "advanced",
         Min = 0,
         Help = "How long to wait for a database another program is writing",
      },
      Other,
   ];

   public override IReadOnlyList<FieldDto> Options { get; } =
   [
      new(EnforceForeignKeysOption, "Check foreign keys when writing", FieldType.Select)
      {
         Default = "",
         Choices = [new("", "As the connection has it (on)"), new("true", "On"), new("false", "Off")],
         Help = "SQLite's PRAGMA foreign_keys, which also carries out ON DELETE actions",
      },
      TrustForeignKeys,
   ];

   /// <summary>The application opens the file read-only or read-write, never creating it, nor in memory.</summary>
   protected override IReadOnlyList<string> Reserved { get; } = ["Mode"];

   protected override DbConnectionStringBuilder NewBuilder() => new SqliteConnectionStringBuilder();

   protected override void Restrict(DbConnectionStringBuilder builder, bool readOnly) =>
      ((SqliteConnectionStringBuilder)builder).Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite;

   /// <summary>Pooled; letting the pool go closes the file.</summary>
   public override SourceConnector Connector(string connectionString) => new ProviderConnector(() => new SqliteConnection(connectionString), () =>
   {
      using SqliteConnection pooled = new(connectionString);
      SqliteConnection.ClearPool(pooled);
   });

   /// <summary>Unpooled, so a try leaves nothing holding the file.</summary>
   protected override string Unpooled(string connectionString) => new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ToString();

   protected override async Task<string> FoundAsync(DbConnection connection, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      long count = (long)(await ScalarAsync(connection, "SELECT count(*) FROM sqlite_schema WHERE type IN ('table', 'view')", cancellationToken))!;
      return $"Opened the SQLite {connection.ServerVersion} database: {Things(count, "table or view", "tables and views")}";
   }

   internal static string Things(long count, string one, string many) =>
      count == 1 ? $"1 {one}" : $"{count.ToString(CultureInfo.InvariantCulture)} {many}";
}

public sealed class DuckDbKind : ConnectionKind
{
   public const string AccessMode = "ACCESS_MODE";

   public override string Id => Query.DuckDb.DuckDbSourceProvider.Instance.ProviderKind;

   public override string DisplayName => "DuckDB";

   public override string? RawExample => "Data Source=C:\\data\\files\\warehouse.duckdb";

   public override IReadOnlyList<FieldDto> Fields { get; } =
   [
      new("Data Source", "Database file", FieldType.FilePath) { Required = true, Group = "connection" },
      Other with { Help = "DuckDB settings (threads=4, memory_limit=1GB)" },
   ];

   /// <summary>The application opens the file read-only or read-write, never in memory.</summary>
   protected override IReadOnlyList<string> Reserved { get; } = [AccessMode];

   protected override DbConnectionStringBuilder NewBuilder() => new DuckDBConnectionStringBuilder();

   /// <summary>DuckDB's builder keeps keywords as written (lower case, when parsed): its file is <c>Data Source</c>, its settings lower case, as DuckDB names them.</summary>
   public override List<KeyValuePair<string, string>> Normalize(IEnumerable<KeyValuePair<string, string>> pairs) =>
      base.Normalize(pairs.Select(Canonical));

   public override List<KeyValuePair<string, string>> Parse(string connectionString) => Normalize(base.Parse(connectionString));

   private static KeyValuePair<string, string> Canonical(KeyValuePair<string, string> pair) =>
      new(pair.Key.Replace(" ", string.Empty, StringComparison.Ordinal).Equals("datasource", StringComparison.OrdinalIgnoreCase) ? "Data Source" : pair.Key.ToLowerInvariant(),
         pair.Value);

   protected override void Restrict(DbConnectionStringBuilder builder, bool readOnly)
   {
      if (readOnly) { builder[AccessMode] = "READ_ONLY"; }
   }

   /// <summary>DuckDB keeps one database for each file in the process, closed with its last connection.</summary>
   public override SourceConnector Connector(string connectionString) => new ProviderConnector(() => new DuckDBConnection(connectionString));

   protected override async Task<string> FoundAsync(DbConnection connection, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      long count = Convert.ToInt64(await ScalarAsync(connection,
         "SELECT (SELECT count(*) FROM duckdb_tables() WHERE NOT internal) + (SELECT count(*) FROM duckdb_views() WHERE NOT internal)", cancellationToken),
         CultureInfo.InvariantCulture);
      return $"Opened the DuckDB {connection.ServerVersion} database: {SqliteKind.Things(count, "table or view", "tables and views")}";
   }
}

/// <summary>A folder of workbooks: no connection string, just the folder, and always read-only.</summary>
public sealed class ExcelKind : ConnectionKind
{
   public const string Folder = "Folder";

   public const string HeaderRowOption = "headerRow";

   public const string AllTextOption = "allText";

   public const string IncludeHiddenSheetsOption = "includeHiddenSheets";

   public override string Id => Query.Excel.ExcelSourceProvider.Kind;

   public override string DisplayName => "Excel folder";

   public override bool SupportsRaw => false;

   public override bool AlwaysReadOnly => true;

   public override IReadOnlyList<GroupDto> Groups { get; } = [new("connection", "Folder", false)];

   public override IReadOnlyList<FieldDto> Fields { get; } =
   [
      new(Folder, "Folder of workbooks", FieldType.FolderPath)
      {
         Required = true,
         Group = "connection",
         Help = "Its own .xlsx workbooks (not those of folders in it) are the source's schemas, their sheets its tables",
      },
   ];

   public override IReadOnlyList<FieldDto> Options { get; } =
   [
      new(HeaderRowOption, "First row names the columns", FieldType.Bool) { Default = "true" },
      new(AllTextOption, "Read every column as text", FieldType.Bool) { Default = "false" },
      new(IncludeHiddenSheetsOption, "Include hidden sheets", FieldType.Bool) { Default = "false" },
   ];

   public override bool IsSecret(string keyword) => false;

   protected override DbConnectionStringBuilder NewBuilder() => throw new NotSupportedException("A folder of workbooks has no connection string");

   public override List<KeyValuePair<string, string>> Normalize(IEnumerable<KeyValuePair<string, string>> pairs)
   {
      ArgumentNullException.ThrowIfNull(pairs);
      List<KeyValuePair<string, string>> normalized = [];
      foreach ((string key, string value) in pairs)
      {
         if (!string.Equals(key, Folder, StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException($"A folder of workbooks has no setting '{key}'; its one setting is {Folder}"); }
         normalized.Add(new KeyValuePair<string, string>(Folder, value));
      }
      return normalized;
   }

   public override List<KeyValuePair<string, string>> Parse(string connectionString) => throw new NotSupportedException("A folder of workbooks has no connection string");

   public override string Display(IReadOnlyDictionary<string, string> settings, IEnumerable<string> secretKeywords) =>
      throw new NotSupportedException("A folder of workbooks has no connection string");

   /// <summary>The folder's path.</summary>
   public override string ConnectionString(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> secrets, bool readOnly)
   {
      ArgumentNullException.ThrowIfNull(settings);
      return settings[Folder];
   }

   public override Task<string> ProbeAsync(string connectionString, CancellationToken cancellationToken)
   {
      int count = Directory.EnumerateFiles(connectionString, "*.xlsx")
         .Count(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal) && !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal));
      return Task.FromResult($"Found {SqliteKind.Things(count, "workbook", "workbooks")} in the folder");
   }
}
