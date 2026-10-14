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

   /// <summary>
   /// The column the rows are dated by (when an event happened, when an entity was first seen), when the source says
   /// so: what a chart of the rows is drawn over unless another is chosen.
   /// </summary>
   public string? TimeColumn { get; init; }

   /// <summary>
   /// The column a row's time span ends at (when an entity was last seen), when its rows span time from
   /// <see cref="TimeColumn"/> and the source says so.
   /// </summary>
   public string? TimeEndColumn { get; init; }

   /// <summary>
   /// The most days a row's time span (<see cref="TimeColumn"/> to <see cref="TimeEndColumn"/>) can last, when the
   /// source bounds it: a row that ends after x starts after x less that many days. Nothing checks it.
   /// </summary>
   public int? TimeSpanDays { get; init; }
}

public sealed record ColumnSchema(string Name, int Ordinal, string NativeType, ScalarType Type)
{
   public bool IsIdentity { get; init; }

   public bool IsComputed { get; init; }

   /// <summary>The default expression in the source's own dialect; null when the column has none.</summary>
   public string? DefaultSql { get; init; }

   public bool IsRowVersion { get; init; }

   public string? Collation { get; init; }

   /// <summary>
   /// The type the source's SQL reads the column as, when the language can't work with it as it is: PostgreSQL enums,
   /// which text functions and text don't take, and types the language has no values for (arrays, composites) are
   /// read as <c>text</c>. Null reads the column as it is.
   /// </summary>
   public string? ReadAs { get; init; }

   public string? Comment { get; init; }

   /// <summary>
   /// The values the column is known to take, in the order the source gives them (the labels its rules choose
   /// between), when the source says so; null when they are whatever the rows hold.
   /// </summary>
   public IReadOnlyList<string>? Values { get; init; }

   /// <summary>
   /// How the column's values are summed up over many rows (a count of events kept for each entity, by adding them),
   /// when the source says so; null when it doesn't.
   /// </summary>
   public AggregateHint? Aggregate { get; init; }

   /// <summary>
   /// The column of the row a foreign key of the table finds that this one holds a copy of, when the source says so
   /// (null when the key finds no row): reading it reads the other row's value without joining it.
   /// </summary>
   public CarriedColumn? CarriedFrom { get; init; }

   [JsonIgnore]
   public bool IsNullable => Type.Nullable;

   [JsonIgnore]
   public bool HasDefault => DefaultSql != null;
}

/// <summary>A column copied from the row a foreign key (<see cref="ForeignKeySchema.Name"/>) finds: its column <paramref name="Column"/>.</summary>
public sealed record CarriedColumn(string ForeignKey, string Column);

/// <summary>How a column's values are best summed up over many rows, as its source says.</summary>
public enum AggregateHint : byte
{
   Sum,
   Avg,
   Min,
   Max,
   CountDistinct,
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

   /// <summary>
   /// Each row's time (its table's <see cref="TableSchema.TimeColumn"/>) falls within the time span of the row it refers
   /// to (the referenced table's <see cref="TableSchema.TimeColumn"/> to its <see cref="TableSchema.TimeEndColumn"/>),
   /// as the source says: an event happens while its session lasts. Nothing checks it.
   /// </summary>
   public bool IsWithinTimeSpan { get; init; }

   public string? OnDelete { get; init; }

   public string? OnUpdate { get; init; }
}
