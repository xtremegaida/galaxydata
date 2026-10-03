using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Features.Health;
using GalaxyData.Web.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Features;

public sealed class HealthCheckTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static DataDirectory Directory(string path) =>
      new(new GalaxyDataOptions { DataDirectory = path }, new HostingEnvironment { ContentRootPath = Path.GetTempPath() });

   [Fact]
   public async Task ADataDirectoryThatCanBeWrittenToIsHealthyAndKeepsNothingOfTheCheck()
   {
      string root = Path.Combine(Path.GetTempPath(), "gd-web-tests", Guid.NewGuid().ToString("N")[..12]);
      using DataDirectory directory = Directory(root);
      directory.Open();
      try
      {
         HealthCheckResult result = await new DataDirectoryHealthCheck(directory).CheckHealthAsync(new HealthCheckContext(), Token);
         result.Status.ShouldBe(HealthStatus.Healthy);
         System.IO.Directory.GetFiles(root).ShouldBe([Path.Combine(root, DataDirectory.LockFileName)]);
      }
      finally
      {
         directory.Dispose();
         System.IO.Directory.Delete(root, recursive: true);
      }
   }

   [Fact]
   public async Task ADataDirectoryThatCantBeWrittenToIsUnhealthy()
   {
      string missing = Path.Combine(Path.GetTempPath(), "gd-web-tests", Guid.NewGuid().ToString("N")[..12], "data");
      using DataDirectory directory = Directory(missing);
      HealthCheckResult result = await new DataDirectoryHealthCheck(directory).CheckHealthAsync(new HealthCheckContext(), Token);
      result.Status.ShouldBe(HealthStatus.Unhealthy);
      result.Description.ShouldBe($"The data directory {missing} can't be written to");
      result.Exception.ShouldBeOfType<DirectoryNotFoundException>();
   }
}
