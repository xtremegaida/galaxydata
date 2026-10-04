using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Hosting;

/// <summary>The host: its data directory, health, the API's 404s and 405s, the client's paths, and the OpenAPI document.</summary>
public sealed class HostTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   [Fact]
   public async Task HealthSaysTheApplicationCanWork()
   {
      await using WebAppFactory factory = new();
      HttpResponseMessage response = await factory.CreateClient().GetAsync("/api/health", Token);
      response.StatusCode.ShouldBe(HttpStatusCode.OK);
      response.Headers.CacheControl?.NoStore.ShouldBeTrue();
      (await response.Content.ReadAsStringAsync(Token))
         .ShouldBe("""{"status":"healthy","checks":[{"name":"dataDirectory","status":"healthy"},{"name":"mergeEngine","status":"healthy"}]}""");
   }

   [Fact]
   public async Task HealthSaysWhatFailed()
   {
      await using WebAppFactory factory = new()
      {
         ServiceChanges = services => services.Configure<HealthCheckServiceOptions>(o =>
            o.Registrations.Add(new HealthCheckRegistration("broken", _ => new Broken(), HealthStatus.Unhealthy, null))),
      };
      HttpResponseMessage response = await factory.CreateClient().GetAsync("/api/health", Token);
      response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
      (await response.Content.ReadAsStringAsync(Token)).ShouldBe(
         """{"status":"unhealthy","checks":[{"name":"dataDirectory","status":"healthy"},{"name":"mergeEngine","status":"healthy"},{"name":"broken","status":"unhealthy"}]}""");
   }

   private sealed class Broken : IHealthCheck
   {
      public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
         Task.FromResult(HealthCheckResult.Unhealthy("broken on purpose, at db.internal.example"));
   }

   [Fact]
   public async Task TheDataDirectoryIsMadeAtStartup()
   {
      await using WebAppFactory factory = new();
      Directory.Exists(factory.DataDirectory).ShouldBeFalse();
      factory.CreateClient();
      Directory.Exists(factory.DataDirectory).ShouldBeTrue();
   }

   [Fact]
   public async Task AnApplicationThatCantMakeItsDataDirectoryDoesntStart()
   {
      string root = Path.Combine(Path.GetTempPath(), "gd-web-tests", Guid.NewGuid().ToString("N")[..12]);
      Directory.CreateDirectory(root);
      string file = Path.Combine(root, "taken");
      await File.WriteAllTextAsync(file, "a file, not a directory", Token);
      try
      {
         await using WebAppFactory factory = new() { DataDirectory = Path.Combine(file, "data") };
         InvalidOperationException e = Should.Throw<InvalidOperationException>(() => factory.CreateClient());
         e.Message.ShouldStartWith($"The data directory {Path.Combine(file, "data")} can't be made: ");
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }

   /// <summary>One application at a time may use a data directory; another that tries doesn't start, until the first has stopped.</summary>
   [Fact]
   public async Task TwoApplicationsDontShareADataDirectory()
   {
      string root = Path.Combine(Path.GetTempPath(), "gd-web-tests", Guid.NewGuid().ToString("N")[..12]);
      string data = Path.Combine(root, "data");
      try
      {
         await using (WebAppFactory first = new() { DataDirectory = data })
         {
            first.CreateClient();
            await using WebAppFactory second = new() { DataDirectory = data };
            InvalidOperationException e = Should.Throw<InvalidOperationException>(() => second.CreateClient());
            e.Message.ShouldStartWith($"The data directory {data} can't be held for this application: ");
            e.Message.ShouldEndWith("One instance of the application at a time may use it.");
         }
         await WebAppFactory.ReleasedAsync(data);
         await using WebAppFactory third = new() { DataDirectory = data };
         HttpResponseMessage response = await third.CreateClient().GetAsync("/api/health", Token);
         response.StatusCode.ShouldBe(HttpStatusCode.OK);
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }

   [Theory]
   [InlineData("/api/nothing")]
   [InlineData("/api")]
   [InlineData("/api/nothing.json")]
   [InlineData("/API/Nothing/Here")]
   public async Task PathsOfTheApiWithoutEndpointsAreNotFound(string path)
   {
      await using WebAppFactory factory = new();
      TestApi api = await TestApi.SignedInAsync(factory);
      JsonElement problem = await (await api.GetAsync(path)).ProblemAsync(404, "not-found");
      problem.GetProperty("title").GetString().ShouldBe("Not Found");
   }

   [Fact]
   public async Task AnotherMethodIsNotAllowed()
   {
      await using WebAppFactory factory = new();
      TestApi api = await TestApi.SignedInAsync(factory);
      HttpResponseMessage response = await api.PostAsync("/api/health");
      await response.ProblemAsync(405, "method-not-allowed");
      response.Content.Headers.Allow.ShouldContain("GET");
   }

   /// <summary>To anyone not signed in, a path no endpoint has is unauthenticated: the fallback policy, which shows them nothing of the API's shape.</summary>
   [Theory]
   [InlineData("GET", "/api/nothing")]
   [InlineData("POST", "/api/health")]
   [InlineData("GET", "/assets/missing.js")]
   public async Task ToAnyoneNotSignedInAPathWithoutAnEndpointIsUnauthenticated(string method, string path)
   {
      await using WebAppFactory factory = new();
      HttpResponseMessage response = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Token);
      await response.ProblemAsync(401, "unauthenticated");
   }

   /// <summary>
   /// The client's page is every path but the API's and its files', dots and matrix parameters and all, and isn't
   /// cached; its files are served, and those missing are 404s.
   /// </summary>
   [Fact]
   public async Task OtherPathsAreTheClients()
   {
      string webRoot = Path.Combine(Path.GetTempPath(), "gd-web-tests", Guid.NewGuid().ToString("N")[..12]);
      Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
      await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<html>the client</html>", Token);
      await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "app.js"), "// the client's script", Token);
      try
      {
         await using WebAppFactory factory = new() { WebRoot = webRoot };
         HttpClient client = factory.CreateClient();
         foreach (string path in (string[])["/", "/browse/shop.customers", "/browse/shop.customers;f=country:eq:ZA;row=42/orders;sort=-placed_at?at=1",
                                            "/browse/files.docs;f=name:eq:report.json", "/browse/files.docs;f=path:eq:a%2Fb.json", "/browse/geo.map",
                                            "/browse/shop.orders/lines.json", "/query/12", "/apis"])
         {
            HttpResponseMessage page = await client.GetAsync(path, Token);
            page.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            page.Content.Headers.ContentType?.MediaType.ShouldBe("text/html", path);
            page.Headers.CacheControl?.NoCache.ShouldBeTrue(path);
            (await page.Content.ReadAsStringAsync(Token)).ShouldBe("<html>the client</html>", path);
         }
         (await client.GetStringAsync("/assets/app.js", Token)).ShouldBe("// the client's script");
         // Missing files are 404s to those signed in (and 401s to others, as any path without an endpoint).
         TestApi api = await TestApi.SignedInAsync(factory);
         await (await api.GetAsync("/assets/missing.js")).ProblemAsync(404, "not-found");
         await (await api.GetAsync("/favicon.ico")).ProblemAsync(404, "not-found");
         await (await api.GetAsync("/browser/missing.js")).ProblemAsync(404, "not-found");
      }
      finally
      {
         Directory.Delete(webRoot, recursive: true);
      }
   }

   /// <summary>The OpenAPI document, which the client's types are made from: a change to the API shows here.</summary>
   [Fact]
   public async Task TheOpenApiDocumentDescribesTheApi()
   {
      await using WebAppFactory factory = new();
      TestApi api = await TestApi.SignedInAsync(factory);
      HttpResponseMessage response = await api.GetAsync("/api/openapi/v1.json");
      response.StatusCode.ShouldBe(HttpStatusCode.OK);
      Golden.Match(await response.Content.ReadAsStringAsync(Token), "json");
   }
}
