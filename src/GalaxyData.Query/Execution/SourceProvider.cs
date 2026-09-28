using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Execution;

/// <summary>
/// What the engine needs to know about one kind of database: its SQL dialect, how to read its schema, and how
/// values cross the ADO.NET boundary in each direction.
/// </summary>
public abstract class SourceProvider
{
   /// <summary>The <see cref="SourceInfo.ProviderKind"/> of the sources it serves: <c>sqlite</c>.</summary>
   public abstract string ProviderKind { get; }

   public abstract SqlDialect Dialect { get; }

   public abstract ISchemaIntrospector Introspector { get; }

   /// <summary>
   /// Readies a connection the engine was given before a query runs on it: session settings the language's
   /// semantics depend on, such as working in UTC.
   /// </summary>
   public virtual ValueTask PrepareConnectionAsync(DbConnection connection, CancellationToken cancellationToken) => ValueTask.CompletedTask;

   /// <summary>Readies a command for a query before it runs, such as streaming its rows instead of computing them all first.</summary>
   public virtual void PrepareCommand(DbCommand command) { }

   /// <summary>Sets a parameter to a value of the logical type <paramref name="type"/> (null for SQL NULL).</summary>
   public virtual void BindParameter(DbParameter parameter, object? value, ScalarType type)
   {
      ArgumentNullException.ThrowIfNull(parameter);
      parameter.Value = value ?? DBNull.Value;
   }

   /// <summary>The raw value of a column; <see cref="ValueConverter"/> turns it into the logical type's CLR value.</summary>
   public virtual object? ReadValue(DbDataReader reader, int ordinal)
   {
      ArgumentNullException.ThrowIfNull(reader);
      return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
   }
}

/// <summary>
/// Opens connections to sources. The application implements it, so the engine never sees connection strings or
/// secrets. The engine disposes each connection it is given when it is done with it.
/// </summary>
public interface IConnectionFactory
{
   ValueTask<DbConnection> OpenAsync(SourceInfo source, CancellationToken cancellationToken);
}
