using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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
/// that is removed when the factory is disposed; with <see cref="WebRoot"/>, the client is served from there.
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

   /// <summary>Changes to the application's services, made after its own.</summary>
   public Action<IServiceCollection>? ServiceChanges { get; init; }

   protected override void ConfigureWebHost(IWebHostBuilder builder)
   {
      builder.UseEnvironment(Environment);
      builder.ConfigureLogging(logging => logging.ClearProviders());
      builder.UseSetting("GalaxyData:DataDirectory", DataDirectory);
      if (WebRoot != null) { builder.UseSetting(WebHostDefaults.WebRootKey, WebRoot); }
      if (ServiceChanges != null) { builder.ConfigureTestServices(ServiceChanges); }
   }

   protected override IHost CreateHost(IHostBuilder builder)
   {
      IHost host = base.CreateHost(builder);
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
