using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Providers;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;
using Npgsql;
using NpgsqlTypes;

namespace GalaxyData.Query.PostgreSql;

/// <summary>
/// PostgreSQL sources, through Npgsql. Each connection works in UTC, so date-times with and without an offset
/// compare the same way on every server. Parameters are sent as the type the SQL compares them with, so a null has
/// a type too: date-times without an offset as <c>timestamp</c>, with one as <c>timestamptz</c> (in UTC, as Npgsql
/// requires), text as <c>text</c> but untyped when it is compared with a column, so PostgreSQL compares it as the
/// column's type: an enum (which has no comparison with text), a char(n) (which text compares with without its
/// padding) or citext (which text compares with case-sensitively). Connections should come from a data source made
/// by <see cref="CreateDataSource"/>: Npgsql reads enums, ranges and multiranges only when that is set up.
/// </summary>
public sealed class PostgreSqlSourceProvider : SourceProvider
{
   public static PostgreSqlSourceProvider Instance { get; } = new();

   public override string ProviderKind => PostgreSqlSchemaIntrospector.ProviderKind;

   public override SqlDialect Dialect => PostgreSqlDialect.Instance;

   public override ISchemaIntrospector Introspector { get; } = new PostgreSqlSchemaIntrospector();

   /// <summary>A data source for connections the engine can read every column of: one that reads enums (as text), ranges and multiranges.</summary>
   public static NpgsqlDataSource CreateDataSource(string connectionString)
   {
      NpgsqlDataSourceBuilder builder = new(connectionString);
      builder.EnableUnmappedTypes();
      return builder.Build();
   }

   public override async ValueTask PrepareConnectionAsync(DbConnection connection, CancellationToken cancellationToken) =>
      await connection.ExecuteAsync("SET TIME ZONE 'UTC'", cancellationToken).ConfigureAwait(false);

   /// <summary>Deferred constraints are checked now, so a change that breaks one stops before any connection commits.</summary>
   public override async ValueTask PrepareCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      await using DbCommand command = connection.CreateCommand();
      command.CommandText = "SET CONSTRAINTS ALL IMMEDIATE";
      command.Transaction = transaction;
      await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
   }

   public override void BindParameter(DbParameter parameter, object? value, ScalarType type)
   {
      ArgumentNullException.ThrowIfNull(parameter);
      if (parameter is not NpgsqlParameter npgsql)
      {
         base.BindParameter(parameter, value, type);
         return;
      }
      NpgsqlDbType? dbType = type.Kind switch
      {
         ScalarKind.Boolean => NpgsqlDbType.Boolean,
         ScalarKind.Int16 => NpgsqlDbType.Smallint,
         ScalarKind.Int32 => NpgsqlDbType.Integer,
         ScalarKind.Int64 => NpgsqlDbType.Bigint,
         ScalarKind.Decimal => NpgsqlDbType.Numeric,
         ScalarKind.Single => NpgsqlDbType.Real,
         ScalarKind.Double => NpgsqlDbType.Double,
         ScalarKind.String => NpgsqlDbType.Text,
         ScalarKind.Binary => NpgsqlDbType.Bytea,
         ScalarKind.Guid => NpgsqlDbType.Uuid,
         ScalarKind.Date => NpgsqlDbType.Date,
         ScalarKind.Time => NpgsqlDbType.Time,
         ScalarKind.DateTime => NpgsqlDbType.Timestamp,
         ScalarKind.DateTimeOffset => NpgsqlDbType.TimestampTz,
         ScalarKind.Interval => NpgsqlDbType.Interval,
         ScalarKind.Json => NpgsqlDbType.Jsonb,
         _ => null,
      };
      if (dbType is { } known) { npgsql.NpgsqlDbType = known; }
      // A null of no type (a $param given as null) is text: PostgreSQL needs a type for $1 IS NULL.
      else if (value == null) { npgsql.NpgsqlDbType = NpgsqlDbType.Text; }
      npgsql.Value = (value, type.Kind) switch
      {
         (null, _) => DBNull.Value,
         (DateTime dateTime, ScalarKind.DateTime) => DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified),
         (DateTime dateTime, ScalarKind.DateTimeOffset) => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
         (DateTimeOffset offset, ScalarKind.DateTimeOffset) => offset.ToUniversalTime(),
         (DateTimeOffset offset, ScalarKind.DateTime) => DateTime.SpecifyKind(offset.UtcDateTime, DateTimeKind.Unspecified),
         (DateOnly date, ScalarKind.DateTime) => date.ToDateTime(TimeOnly.MinValue),
         (TimeSpan span, ScalarKind.Time) => TimeOnly.FromTimeSpan(span),
         _ => value,
      };
   }

   /// <summary>
   /// Text compared with or stored in a column is sent untyped, so PostgreSQL takes it as the column's type (an enum,
   /// char(n), citext); so is JSON stored in a column, which may be <c>json</c> or <c>jsonb</c>.
   /// </summary>
   public override void BindParameter(DbParameter parameter, object? value, SqlParameterSlot slot)
   {
      ArgumentNullException.ThrowIfNull(slot);
      BindParameter(parameter, value, slot.Type);
      if (slot.ComparedWithColumn && slot.Type.Kind is ScalarKind.String or ScalarKind.Json && parameter is NpgsqlParameter npgsql)
      {
         npgsql.NpgsqlDbType = NpgsqlDbType.Unknown;
      }
   }

   /// <summary>
   /// Npgsql's value for a column; an interval with months, which a <see cref="TimeSpan"/> can't hold, counts them as
   /// 30 days (as PostgreSQL's <c>EXTRACT(EPOCH ...)</c> does). A numeric a decimal can't hold (NaN, infinity, beyond
   /// 28 digits) fails as a value that doesn't convert, naming its row and column (null when conversion is lenient).
   /// </summary>
   public override object? ReadValue(DbDataReader reader, int ordinal)
   {
      ArgumentNullException.ThrowIfNull(reader);
      if (reader.IsDBNull(ordinal)) { return null; }
      try
      {
         return reader.GetValue(ordinal);
      }
      catch (Exception e) when (e is InvalidCastException or OverflowException)
      {
         switch (reader.GetDataTypeName(ordinal))
         {
            case "interval":
               NpgsqlInterval interval = reader.GetFieldValue<NpgsqlInterval>(ordinal);
               return TimeSpan.FromDays(interval.Months * 30L + interval.Days) + TimeSpan.FromTicks(interval.Time * 10);
            case "-.-":
               throw new InvalidCastException(
                  $"Npgsql can't read the column '{reader.GetName(ordinal)}': an enum, range or multirange is read only on connections of a data source made by {nameof(PostgreSqlSourceProvider)}.{nameof(CreateDataSource)}", e);
            default:
               throw;
         }
      }
   }
}
