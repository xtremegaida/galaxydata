using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace GalaxyData.Query.Providers;

/// <summary>Small ADO.NET helpers shared by the providers.</summary>
public static class DbCommandExtensions
{
   public static DbCommand CreateCommand(this DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
   {
      ArgumentNullException.ThrowIfNull(connection);
      DbCommand command = connection.CreateCommand();
      command.CommandText = sql;
      foreach ((string name, object? value) in parameters)
      {
         DbParameter parameter = command.CreateParameter();
         parameter.ParameterName = name;
         parameter.Value = value ?? DBNull.Value;
         command.Parameters.Add(parameter);
      }
      return command;
   }

   /// <summary>Runs a query and yields the reader once per row; read the row before moving on.</summary>
   public static async IAsyncEnumerable<DbDataReader> QueryAsync(this DbConnection connection, string sql,
      [EnumeratorCancellation] CancellationToken cancellationToken = default, params (string Name, object? Value)[] parameters)
   {
      using DbCommand command = connection.CreateCommand(sql, parameters);
      using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { yield return reader; }
   }

   /// <summary>The first column of the first row as invariant text, or null.</summary>
   public static async Task<string?> ScalarTextAsync(this DbConnection connection, string sql,
      CancellationToken cancellationToken = default, params (string Name, object? Value)[] parameters)
   {
      using DbCommand command = connection.CreateCommand(sql, parameters);
      object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
   }

   public static async Task<int> ExecuteAsync(this DbConnection connection, string sql,
      CancellationToken cancellationToken = default, params (string Name, object? Value)[] parameters)
   {
      using DbCommand command = connection.CreateCommand(sql, parameters);
      return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
   }
}
