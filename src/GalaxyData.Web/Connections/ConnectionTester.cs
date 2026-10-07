using System;
using System.Data.Common;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Connectors;
using GalaxyData.Web.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Connections;

/// <summary>What trying a connection found: whether it connected, and what it found, or why not.</summary>
public sealed record ConnectionTestDto(bool Ok, string Message, double ElapsedMs);

/// <summary>
/// Tries a connection: its file or folder is there, it connects within <see cref="ConnectionSettings.TestTimeout"/>,
/// and runs a statement. Its secrets are masked in whatever the database answers.
/// </summary>
public sealed partial class ConnectionTester(IOptions<GalaxyDataOptions> options, SecretRedactor redactor, TimeProvider clock, ILogger<ConnectionTester> logger)
{
   public async Task<ConnectionTestDto> TestAsync(string alias, ConnectionKind kind, ConnectionResolution connection, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(kind);
      ArgumentNullException.ThrowIfNull(connection);
      long started = clock.GetTimestamp();
      // Secrets tried before they are saved are masked in the log too.
      foreach (string secret in connection.Secrets.Values) { redactor.Add(secret); }
      double Elapsed() => Math.Round(clock.GetElapsedTime(started).TotalMilliseconds, 1);
      if (kind.Missing(connection.Settings) is { } missing) { return new ConnectionTestDto(false, missing, Elapsed()); }
      TimeSpan limit = options.Value.Connections.TestTimeout;
      using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeout.CancelAfter(limit);
      try
      {
         string connectionString = kind.ConnectionString(connection.Settings, connection.Secrets, connection.IsReadOnly);
         // A provider that doesn't heed the token is left to finish alone.
         string found = await kind.ProbeAsync(connectionString, timeout.Token).WaitAsync(timeout.Token);
         return new ConnectionTestDto(true, found, Elapsed());
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
         return new ConnectionTestDto(false, $"It didn't answer within {limit.TotalSeconds:0.#} seconds", Elapsed());
      }
      catch (Exception e) when (e is DbException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                  or TimeoutException or SocketException or NotSupportedException or FormatException)
      {
         string message = ConnectionStrings.Scrub(e.Message, connection.Secrets.Values);
         LogFailed(logger, alias, message);
         return new ConnectionTestDto(false, message, Elapsed());
      }
   }

   [LoggerMessage(Level = LogLevel.Information, Message = "Trying the connection {Alias} failed: {Reason}")]
   private static partial void LogFailed(ILogger logger, string alias, string reason);
}
