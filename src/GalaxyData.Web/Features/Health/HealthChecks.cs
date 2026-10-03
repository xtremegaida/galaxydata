using System;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Execution;
using GalaxyData.Web.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GalaxyData.Web.Features.Health;

/// <summary>The data directory can be written to: a file is made in it, and removed.</summary>
internal sealed class DataDirectoryHealthCheck(DataDirectory directory) : IHealthCheck
{
   public const string Name = "dataDirectory";

   public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
   {
      string probe = Path.Combine(directory.Path, $".health-{Guid.NewGuid():N}");
      try
      {
         using (FileStream file = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
         {
            file.WriteByte(0);
         }
         return Task.FromResult(HealthCheckResult.Healthy());
      }
      catch (Exception e) when (e is IOException or UnauthorizedAccessException)
      {
         return Task.FromResult(HealthCheckResult.Unhealthy($"The data directory {directory.Path} can't be written to", e));
      }
   }
}

/// <summary>The merge engine runs statements: a session is opened, runs one, and is closed.</summary>
internal sealed class MergeEngineHealthCheck(IMergeEngine merge) : IHealthCheck
{
   public const string Name = "mergeEngine";

   public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
   {
      try
      {
         await using IMergeSession session = await merge.OpenSessionAsync(cancellationToken);
         await using DbCommand command = session.CreateCommand();
         command.CommandText = "SELECT 1";
         await command.ExecuteScalarAsync(cancellationToken);
         return HealthCheckResult.Healthy();
      }
      catch (Exception e) when (e is DbException or IOException or InvalidOperationException or ObjectDisposedException)
      {
         return HealthCheckResult.Unhealthy("The merge engine can't run statements", e);
      }
   }
}
