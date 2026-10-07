using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using GalaxyData.Connectors;
using GalaxyData.Query.Execution;

namespace GalaxyData.Query.DuckDb;

/// <summary>
/// DuckDB database files. On the command line, a <c>.sql</c> script or <c>:memory:</c> is an in-memory database,
/// whose other connections are duplicates of its first.
/// </summary>
public sealed class DuckDbConnector : Connector
{
   public override SourceProvider Provider => DuckDbSourceProvider.Instance;

   public override ConnectionKind Kind { get; } = new DuckDbKind();

   public override CommandLineHelp CommandLine { get; } = new(CommandLineTargets.Files);

   public override ValueTask<OpenedSource> OpenAsync(CommandLineSource source, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      string connectionString = Create(source, () => source switch
      {
         { IsScript: true } or { IsMemory: true } => "Data Source=:memory:",
         { IsConnectionString: true } => source.Target,
         _ => $"Data Source={ExistingFile(source)}{(source.Writable ? string.Empty : ";" + DuckDbKind.AccessMode + "=READ_ONLY")}",
      });
      DuckDBConnection keeper = Create(source, () => new DuckDBConnection(connectionString));
      // Only in-memory databases can be duplicated; files are opened again, and DuckDB shares them within the process.
      Func<DbConnection> open = source.IsScript || source.IsMemory ? keeper.Duplicate : () => new DuckDBConnection(connectionString);
      return OpenDatabaseAsync(source, keeper, open, null, cancellationToken);
   }
}

public sealed class DuckDbKind : ConnectionKind
{
   public const string AccessMode = "ACCESS_MODE";

   public override string Id => DuckDbSourceProvider.Instance.ProviderKind;

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
      return $"Opened the DuckDB {connection.ServerVersion} database: {Things(count, "table or view", "tables and views")}";
   }
}
