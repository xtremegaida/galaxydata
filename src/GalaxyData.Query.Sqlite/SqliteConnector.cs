using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Connectors;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using Microsoft.Data.Sqlite;

namespace GalaxyData.Query.Sqlite;

/// <summary>
/// SQLite database files. On the command line, a <c>.sql</c> script or <c>:memory:</c> is a shared-cache in-memory
/// database, which lasts while its first connection is open.
/// </summary>
public sealed class SqliteConnector : Connector
{
   public override SourceProvider Provider => SqliteSourceProvider.Instance;

   public override ConnectionKind Kind { get; } = new SqliteKind();

   public override CommandLineHelp CommandLine { get; } = new(CommandLineTargets.Files);

   public override ValueTask<OpenedSource> OpenAsync(CommandLineSource source, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      string connectionString = Create(source, () => source switch
      {
         { IsScript: true } or { IsMemory: true } => $"Data Source=gdq_{source.Alias}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
         { IsConnectionString: true } => source.Target,
         _ => new SqliteConnectionStringBuilder { DataSource = ExistingFile(source), Mode = source.Writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly }.ToString(),
      });
      SqliteConnection keeper = Create(source, () => new SqliteConnection(connectionString));
      return OpenDatabaseAsync(source, keeper, () => new SqliteConnection(connectionString), null, cancellationToken);
   }
}

public sealed class SqliteKind : ConnectionKind
{
   public const string EnforceForeignKeysOption = "enforceForeignKeys";

   public override string Id => SqliteSourceProvider.Instance.ProviderKind;

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

   /// <summary>Whether the connections changes are written on check foreign keys, as the option sets it.</summary>
   public override SourceInfo Configure(SourceInfo source, IReadOnlyDictionary<string, string> options) =>
      base.Configure(source, options) with { EnforceForeignKeys = Flag(options, EnforceForeignKeysOption) };

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
}
