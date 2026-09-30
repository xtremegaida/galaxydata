using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Introspection;

/// <summary>Reads the physical schema of one connection.</summary>
public interface ISchemaIntrospector
{
   Task<SourceSchema> IntrospectAsync(DbConnection connection, IntrospectionOptions options, CancellationToken cancellationToken);
}

public sealed record IntrospectionOptions
{
   public static readonly IntrospectionOptions Default = new();

   /// <summary>Schemas to read; null reads every non-system schema.</summary>
   public IReadOnlyList<string>? IncludeSchemas { get; init; }

   public IReadOnlyList<string>? ExcludeSchemas { get; init; }

   public bool IncludeViews { get; init; } = true;

   public bool IncludeRowCountEstimates { get; init; } = true;

   public bool IncludeSystemObjects { get; init; }
}

/// <summary>
/// The physical schema of one source. Introspectors return tables ordered by schema then name, columns by
/// ordinal, and keys, indexes and foreign keys by name, so the serialized form is stable and can be hashed.
/// </summary>
public sealed record SourceSchema(string ProviderKind, string? ServerVersion, string DefaultSchema, IReadOnlyList<TableSchema> Tables)
{
   /// <summary>What the introspector couldn't read and left out (a workbook of an Excel folder that is damaged); null when nothing.</summary>
   public IReadOnlyList<string>? Warnings { get; init; }
}

public enum TableKind : byte
{
   Table,
   View,
   MaterializedView,
}

public sealed record TableSchema(string Schema, string Name, TableKind Kind, IReadOnlyList<ColumnSchema> Columns)
{
   public KeySchema? PrimaryKey { get; init; }

   public IReadOnlyList<KeySchema> UniqueKeys { get; init; } = [];

   public IReadOnlyList<IndexSchema> Indexes { get; init; } = [];

   public IReadOnlyList<ForeignKeySchema> ForeignKeys { get; init; } = [];

   public long? RowCountEstimate { get; init; }

   public bool HasTriggers { get; init; }

   public string? Comment { get; init; }
}

public sealed record ColumnSchema(string Name, int Ordinal, string NativeType, ScalarType Type)
{
   public bool IsIdentity { get; init; }

   public bool IsComputed { get; init; }

   /// <summary>The default expression in the source's own dialect; null when the column has none.</summary>
   public string? DefaultSql { get; init; }

   public bool IsRowVersion { get; init; }

   public string? Collation { get; init; }

   public string? Comment { get; init; }

   [JsonIgnore]
   public bool IsNullable => Type.Nullable;

   [JsonIgnore]
   public bool HasDefault => DefaultSql != null;
}

public sealed record KeySchema(string? Name, IReadOnlyList<string> Columns);

public sealed record IndexSchema(string Name, IReadOnlyList<string> Columns, bool IsUnique)
{
   public bool IsPrimaryKey { get; init; }

   /// <summary>The predicate of a partial (filtered) index.</summary>
   public string? Filter { get; init; }
}

public sealed record ForeignKeySchema(
   string? Name, IReadOnlyList<string> Columns, string RefSchema, string RefTable, IReadOnlyList<string> RefColumns)
{
   /// <summary>False when the database does not guarantee the constraint holds (not validated, not trusted, or not enforced).</summary>
   /// <remarks>Always written: its default is true, so omitting false would read back as true.</remarks>
   [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
   public bool IsEnforced { get; init; } = true;

   public string? OnDelete { get; init; }

   public string? OnUpdate { get; init; }
}
