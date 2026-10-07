using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.ADO.Readers;
using ClickHouse.Driver.Numerics;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.ClickHouse;

/// <summary>
/// ClickHouse servers, over HTTP (ClickHouse.Driver). Read-only: ClickHouse has no transactions, and its updates and
/// deletes are mutations that run later. Every statement runs with the settings the language's meaning depends on
/// (<see cref="Settings"/>). Parameters are given their ClickHouse type, so the server parses them as the SQL
/// expects. Decimals are read as <see cref="decimal"/>, dates as dates, and date-times of a column with a time zone
/// of its own as instants. A statement stopped is cancelled on the server too, by its query id.
/// </summary>
public sealed class ClickHouseSourceProvider : SourceProvider
{
   /// <summary>The settings every statement runs with.</summary>
   /// <remarks>
   /// Outer joins give nulls (not the type's default value) for rows that don't match; aggregates of no rows give
   /// null (not 0); times are in UTC, as the other providers' sessions are; nothing is written (<c>readonly=2</c> lets
   /// these settings be sent); and a statement whose client has gone is stopped.
   /// </remarks>
   public static IReadOnlyDictionary<string, object> Settings { get; } = new Dictionary<string, object>(StringComparer.Ordinal)
   {
      ["join_use_nulls"] = 1,
      ["aggregate_functions_null_for_empty"] = 1,
      ["session_timezone"] = "UTC",
      ["readonly"] = 2,
      ["cancel_http_readonly_queries_on_client_close"] = 1,
   };

   private ClickHouseSourceProvider() { }

   public static ClickHouseSourceProvider Instance { get; } = new();

   public override string ProviderKind => ClickHouseSchemaIntrospector.ProviderKind;

   public override SqlDialect Dialect => ClickHouseDialect.Instance;

   public override ISchemaIntrospector Introspector { get; } = new ClickHouseSchemaIntrospector();

   /// <summary>
   /// A data source for a connection string: its connections share one HTTP client (a connection of its own each
   /// would each have one, and run out of sockets), which reports a server it can't reach as a <see cref="DbException"/>,
   /// as other providers do. <c>SkipServerCertificateValidation=true</c> accepts the server's certificate unchecked.
   /// </summary>
   public static ClickHouseDataSource CreateDataSource(string connectionString)
   {
      ClickHouseConnectionStringBuilder builder = new(connectionString);
      bool skipCertificate = builder.TryGetValue(ClickHouseKind.SkipCertificateValidation, out object? skip) &&
                             bool.TryParse(Convert.ToString(skip, CultureInfo.InvariantCulture), out bool flag) && flag;
      SocketsHttpHandler sockets = new() { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
      if (skipCertificate) { sockets.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true; }
      HttpClient client = new(new UnreachableAsDbException(sockets)) { Timeout = builder.Timeout > TimeSpan.Zero ? builder.Timeout : Timeout.InfiniteTimeSpan };
      return new ClickHouseDataSource(connectionString, client, disposeHttpClient: true);
   }

   /// <summary>The settings, on every statement of the connection.</summary>
   public override ValueTask PrepareConnectionAsync(DbConnection connection, CancellationToken cancellationToken)
   {
      if (connection is ClickHouseConnection clickHouse) { Apply(clickHouse.CustomSettings); }
      return ValueTask.CompletedTask;
   }

   /// <summary>The settings, and a query id the statement can be stopped on the server by.</summary>
   public override void PrepareCommand(DbCommand command)
   {
      if (command is not ClickHouseCommand clickHouse) { return; }
      Apply(clickHouse.CustomSettings);
      clickHouse.QueryId = Guid.NewGuid().ToString("N");
   }

   private static void Apply(IDictionary<string, object> settings)
   {
      foreach ((string name, object value) in Settings) { settings[name] = value; }
   }

   /// <summary>
   /// Stops the HTTP request, and the statement on the server: a server goes on with a statement whose client has
   /// gone until it has something to send, so it is killed by its query id, on another request.
   /// </summary>
   public override void CancelCommand(DbCommand command)
   {
      ArgumentNullException.ThrowIfNull(command);
      command.Cancel();
      if (command is ClickHouseCommand { QueryId: { Length: > 0 } id, Connection: ClickHouseConnection connection }) { _ = KillAsync(connection, id); }
   }

   private static async Task KillAsync(ClickHouseConnection connection, string id)
   {
      try
      {
         await using ClickHouseCommand kill = connection.CreateCommand();
         kill.CommandText = "KILL QUERY WHERE query_id = {id:String} ASYNC";
         kill.Parameters.Add(new ClickHouseDbParameter { ParameterName = "id", Value = id, ClickHouseType = "String" });
         await kill.ExecuteNonQueryAsync().ConfigureAwait(false);
      }
      catch (Exception e) when (e is DbException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
      {
         // Finished already, or the login may not kill statements: the request was stopped all the same.
      }
   }

   public override ValueTask PrepareWriteAsync(DbConnection connection, SourceInfo source, CancellationToken cancellationToken) =>
      throw new NotSupportedException("ClickHouse sources are read-only: rows of them can't be changed here");

   /// <summary>The value, with its ClickHouse type, so the server parses it as the SQL expects (and a decimal keeps its digits).</summary>
   public override void BindParameter(DbParameter parameter, object? value, ScalarType type)
   {
      ArgumentNullException.ThrowIfNull(parameter);
      parameter.Value = value switch
      {
         null => DBNull.Value,
         TimeOnly time => time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
         TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
         _ => value,
      };
      if (parameter is ClickHouseDbParameter clickHouse) { clickHouse.ClickHouseType = ParameterType(type, value); }
   }

   /// <summary>A parameter's type: the logical type's, nullable; for an unknown one, its value's.</summary>
   private static string ParameterType(ScalarType type, object? value)
   {
      ScalarKind kind = type.Kind != ScalarKind.Unknown ? type.Kind : value switch
      {
         bool => ScalarKind.Boolean,
         short => ScalarKind.Int16,
         int => ScalarKind.Int32,
         long => ScalarKind.Int64,
         decimal => ScalarKind.Decimal,
         float => ScalarKind.Single,
         double => ScalarKind.Double,
         Guid => ScalarKind.Guid,
         DateOnly => ScalarKind.Date,
         DateTime => ScalarKind.DateTime,
         DateTimeOffset => ScalarKind.DateTimeOffset,
         _ => ScalarKind.String,
      };
      string name = kind switch
      {
         ScalarKind.Boolean => "Bool",
         ScalarKind.Int16 => "Int16",
         ScalarKind.Int32 => "Int32",
         ScalarKind.Int64 => "Int64",
         ScalarKind.Decimal => type.Kind == ScalarKind.Decimal && type.Precision > 0
            ? $"Decimal({type.Precision}, {type.Scale})"
            : $"Decimal(38, {(value is decimal number ? number.Scale : 10).ToString(CultureInfo.InvariantCulture)})",
         ScalarKind.Single => "Float32",
         ScalarKind.Double => "Float64",
         ScalarKind.Guid => "UUID",
         ScalarKind.Date => "Date32",
         ScalarKind.DateTime => "DateTime64(6)",
         ScalarKind.DateTimeOffset => "DateTime64(6, 'UTC')",
         _ => "String",
      };
      return $"Nullable({name})";
   }

   /// <summary>
   /// The raw value: ClickHouse's decimals as decimals, its network addresses as text, and the date-times of a
   /// column with a time zone of its own as instants (the driver reads them as the zone's time of day).
   /// </summary>
   public override object? ReadValue(DbDataReader reader, int ordinal)
   {
      ArgumentNullException.ThrowIfNull(reader);
      if (reader.IsDBNull(ordinal)) { return null; }
      object value = reader.GetValue(ordinal);
      return value switch
      {
         ClickHouseDecimal number => ToDecimal(number),
         // A date-time type with a zone names it, quoted: DateTime('Africa/Johannesburg'), DateTime64(3, 'UTC').
         DateTime when reader is ClickHouseDataReader clickHouse && reader.GetDataTypeName(ordinal).Contains('\'', StringComparison.Ordinal) =>
            clickHouse.GetDateTimeOffset(ordinal),
         IPAddress address => address.ToString(),
         _ => value,
      };
   }

   /// <summary>A decimal as .NET's, which holds 28 digits; a wider one as the nearest double.</summary>
   private static object ToDecimal(ClickHouseDecimal number)
   {
      try
      {
         return number.ToDecimal(CultureInfo.InvariantCulture);
      }
      catch (OverflowException)
      {
         return number.ToDouble(CultureInfo.InvariantCulture);
      }
   }

   /// <summary>A server that can't be reached is a <see cref="DbException"/>, as the engine expects of a source that fails.</summary>
   private sealed class UnreachableAsDbException(HttpMessageHandler inner) : DelegatingHandler(inner)
   {
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         try
         {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
         }
         catch (HttpRequestException e)
         {
            throw new ClickHouseUnreachableException($"The ClickHouse server at {request.RequestUri?.GetLeftPart(UriPartial.Authority)} can't be reached: {e.Message}", e);
         }
      }
   }
}

/// <summary>A ClickHouse server couldn't be reached: the host isn't known, refused the connection, or broke it off.</summary>
public sealed class ClickHouseUnreachableException(string message, Exception inner) : DbException(message, inner);
