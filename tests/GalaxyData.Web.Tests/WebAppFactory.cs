using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GalaxyData.Web.Tests;

/// <summary>
/// The application in memory, in production unless told otherwise, keeping its data in a directory of its own
/// that is removed when the factory is disposed; with <see cref="WebRoot"/>, the client is served from there. Its
/// first administrator is <see cref="TestApi.AdminName"/>, with <see cref="TestApi.AdminPassword"/>, who needn't
/// change it; <see cref="Settings"/> changes that, and any other setting (a null removes one).
/// </summary>
internal sealed class WebAppFactory : WebApplicationFactory<Program>
{
   private bool started;

   public WebAppFactory()
   {
      Root = Path.Combine(Path.GetTempPath(), "gd-web-tests", Guid.NewGuid().ToString("N")[..12]);
      DataDirectory = Path.Combine(Root, "data");
   }

   /// <summary>A directory of the factory's own, removed with it.</summary>
   public string Root { get; }

   public string DataDirectory { get; init; }

   public string? WebRoot { get; init; }

   public string Environment { get; init; } = "Production";

   /// <summary>Settings over the factory's own, by their configuration keys (<c>GalaxyData:Auth:MaxFailedSignIns</c>).</summary>
   public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

   /// <summary>Changes to the application's services, made after its own.</summary>
   public Action<IServiceCollection>? ServiceChanges { get; init; }

   protected override void ConfigureWebHost(IWebHostBuilder builder)
   {
      builder.UseEnvironment(Environment);
      builder.ConfigureLogging(logging => logging.ClearProviders());
      Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
      {
         ["GalaxyData:DataDirectory"] = DataDirectory,
         ["GalaxyData:Bootstrap:AdminUserName"] = TestApi.AdminName,
         ["GalaxyData:Bootstrap:AdminPassword"] = TestApi.AdminPassword,
         ["GalaxyData:Bootstrap:RequirePasswordChange"] = "false",
         // Tests poll as they wait; the limits' own tests set them.
         ["GalaxyData:RateLimits:RequestsPerMinute"] = "0",
      };
      foreach ((string key, string? value) in Settings) { settings[key] = value; }
      foreach ((string key, string? value) in settings)
      {
         if (value != null) { builder.UseSetting(key, value); }
      }
      if (WebRoot != null) { builder.UseSetting(WebHostDefaults.WebRootKey, WebRoot); }
      if (ServiceChanges != null) { builder.ConfigureTestServices(ServiceChanges); }
   }

   /// <summary>
   /// Starts the application as the base does (which also finds Kestrel's port, unused here), and waits until it has
   /// started or failed. Its entry point runs on a thread of its own, which goes on once the host is built: an
   /// application that fails to start disposes its host, and if it does before the factory waits for it, the factory
   /// finds a disposed <see cref="IServiceProvider"/> (an <see cref="ObjectDisposedException"/>) rather than why it
   /// failed. So the host's start is held until the factory waits.
   /// </summary>
   protected override IHost CreateHost(IHostBuilder builder)
   {
      TaskCompletionSource waiting = new();
      builder.ConfigureServices(services => HeldLifetime.Replace(services, waiting.Task));
      IHost host = builder.Build();
      Task start;
      try
      {
         // Returns as it waits for the application to start, with the host's services in hand.
         start = host.StartAsync();
      }
      finally
      {
         waiting.SetResult();
      }
      start.GetAwaiter().GetResult();
      started = true;
      return host;
   }

   public override async ValueTask DisposeAsync()
   {
      await base.DisposeAsync();
      if (started) { await ReleasedAsync(DataDirectory); }
      if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
   }

   /// <summary>
   /// Waits until no application holds a data directory. A factory's application has stopped when the factory is
   /// disposed, but its services may still be being disposed (on the thread that ran it), the data directory last.
   /// </summary>
   public static async Task ReleasedAsync(string dataDirectory)
   {
      string path = Path.Combine(dataDirectory, GalaxyData.Web.Hosting.DataDirectory.LockFileName);
      for (int attempt = 0; ; attempt++)
      {
         try
         {
            if (!File.Exists(path)) { return; }
            await using FileStream probe = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return;
         }
         catch (IOException) when (attempt < 400)
         {
            await Task.Delay(25);
         }
      }
   }

   /// <summary>
   /// The host's own lifetime, with the start held until the factory waits for it. The host waits for its lifetime
   /// before anything else it does to start (the options' validation, the hosted services), so before anything fails.
   /// </summary>
   private sealed class HeldLifetime(IHostLifetime lifetime, Task waiting) : IHostLifetime, IDisposable
   {
      public static void Replace(IServiceCollection services, Task waiting)
      {
         ServiceDescriptor own = services.Last(d => d.ServiceType == typeof(IHostLifetime));
         services.Remove(own);
         services.AddSingleton<IHostLifetime>(sp => new HeldLifetime(
            (IHostLifetime)(own.ImplementationInstance ?? own.ImplementationFactory?.Invoke(sp) ?? ActivatorUtilities.CreateInstance(sp, own.ImplementationType!)),
            waiting));
      }

      public Task WaitForStartAsync(CancellationToken cancellationToken)
      {
         // Held on the entry point's thread (the factory doesn't wait for it to let go), which then goes on to start
         // the application where it would have.
         waiting.Wait(cancellationToken);
         return lifetime.WaitForStartAsync(cancellationToken);
      }

      public Task StopAsync(CancellationToken cancellationToken) => lifetime.StopAsync(cancellationToken);

      public void Dispose() => (lifetime as IDisposable)?.Dispose();
   }
}

internal static class Responses
{
   /// <summary>The body of a problem the API answered with, checked to be one, with its status and code.</summary>
   public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response, int status, string code)
   {
      response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
      JsonElement problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
      ((int)response.StatusCode).ShouldBe(status);
      problem.GetProperty("status").GetInt32().ShouldBe(status);
      problem.GetProperty("code").GetString().ShouldBe(code);
      problem.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
      return problem;
   }
}
