using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Tests.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Hosting;

/// <summary>What answers tell browsers, who the client is behind a proxy, and how much each may ask.</summary>
public sealed class SecurityTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static string? Header(HttpResponseMessage response, string name) =>
      response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(", ", values)
         : response.Content.Headers.TryGetValues(name, out values) ? string.Join(", ", values)
         : null;

   private static void HasSecurityHeaders(HttpResponseMessage response, string what)
   {
      Header(response, "X-Content-Type-Options").ShouldBe("nosniff", what);
      Header(response, "X-Frame-Options").ShouldBe("DENY", what);
      Header(response, "Referrer-Policy").ShouldBe("no-referrer", what);
      Header(response, "Cross-Origin-Opener-Policy").ShouldBe("same-origin", what);
      Header(response, "Cross-Origin-Resource-Policy").ShouldBe("same-origin", what);
      Header(response, "Permissions-Policy").ShouldBe(SecurityHeaders.PermissionsPolicy, what);
   }

   /// <summary>A web root with the client's page, a file named by its content's hash, and one that isn't.</summary>
   private static async Task<string> ClientAsync()
   {
      string webRoot = Path.Combine(Path.GetTempPath(), "gd-web-tests", Guid.NewGuid().ToString("N")[..12]);
      Directory.CreateDirectory(Path.Combine(webRoot, "media"));
      await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<html>the client</html>", Token);
      await File.WriteAllTextAsync(Path.Combine(webRoot, "main-LKPGWKWT.js"), "// built", Token);
      await File.WriteAllTextAsync(Path.Combine(webRoot, "media", "logo.svg"), "<svg/>", Token);
      await File.WriteAllTextAsync(Path.Combine(webRoot, "media", "notes-20250101.txt"), "dated, not hashed", Token);
      return webRoot;
   }

   /// <summary>
   /// Every answer says not to sniff, frame or refer, and has a content security policy: the client's for its pages
   /// and files, the API's own (which loads nothing) for the API, whose answers aren't cached. Files named by their
   /// content's hash are kept for a year; the page and other files are checked each time.
   /// </summary>
   [Fact]
   public async Task EveryAnswerHasTheSecurityHeaders()
   {
      string webRoot = await ClientAsync();
      try
      {
         await using WebAppFactory factory = new() { WebRoot = webRoot };
         HttpClient client = factory.CreateClient();
         foreach (string path in (string[])["/", "/browse/shop.orders", "/main-LKPGWKWT.js", "/media/logo.svg", "/media/notes-20250101.txt"])
         {
            HttpResponseMessage response = await client.GetAsync(path, Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            HasSecurityHeaders(response, path);
            Header(response, "Content-Security-Policy").ShouldBe(SecuritySettings.DefaultContentSecurityPolicy, path);
            Header(response, "Strict-Transport-Security").ShouldBeNull("not over HTTP");
            response.Headers.CacheControl!.ToString().ShouldBe(path == "/main-LKPGWKWT.js" ? "public, max-age=31536000, immutable" : "no-cache", path);
         }
         TestApi api = await TestApi.SignedInAsync(factory);
         foreach ((HttpResponseMessage response, string what) in (IEnumerable<(HttpResponseMessage, string)>)[
                     (await client.GetAsync("/api/health", Token), "health"),
                     (await client.GetAsync("/api/users", Token), "a 401"),
                     (await api.GetAsync("/api/nothing"), "a 404"),
                     (await api.GetAsync("/api/catalog"), "the catalog")])
         {
            HasSecurityHeaders(response, what);
            Header(response, "Content-Security-Policy").ShouldBe(SecurityHeaders.ApiContentSecurityPolicy, what);
            response.Headers.CacheControl!.NoStore.ShouldBeTrue(what);
         }
      }
      finally
      {
         Directory.Delete(webRoot, recursive: true);
      }
   }

   /// <summary>The client's policy is a setting; empty, its pages have none (the API's answers keep theirs).</summary>
   [Theory]
   [InlineData("default-src 'self'; script-src 'self' 'unsafe-eval'")]
   [InlineData("")]
   public async Task TheClientsPolicyIsASetting(string policy)
   {
      string webRoot = await ClientAsync();
      try
      {
         await using WebAppFactory factory = new() { WebRoot = webRoot, Settings = new Dictionary<string, string?> { ["GalaxyData:Security:ContentSecurityPolicy"] = policy } };
         HttpClient client = factory.CreateClient();
         Header(await client.GetAsync("/", Token), "Content-Security-Policy").ShouldBe(policy.Length == 0 ? null : policy);
         Header(await client.GetAsync("/api/health", Token), "Content-Security-Policy").ShouldBe(SecurityHeaders.ApiContentSecurityPolicy);
      }
      finally
      {
         Directory.Delete(webRoot, recursive: true);
      }
   }

   /// <summary>Answers over HTTPS tell browsers to keep to it (HSTS), unless turned off, or in development.</summary>
   [Theory]
   [InlineData("Production", true, "max-age=15552000")]
   [InlineData("Production", false, null)]
   [InlineData("Development", true, null)]
   public async Task HttpsAnswersTellBrowsersToKeepToIt(string environment, bool hsts, string? expected)
   {
      await using WebAppFactory factory = new()
      {
         Environment = environment,
         Settings = new Dictionary<string, string?> { ["GalaxyData:Security:Hsts"] = hsts ? "true" : "false" },
      };
      HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://galaxydata.test") });
      Header(await client.GetAsync("/api/health", Token), "Strict-Transport-Security").ShouldBe(expected);
   }

   /// <summary>With HTTPS required, requests over HTTP are sent to it.</summary>
   [Fact]
   public async Task HttpIsRedirectedWhenHttpsIsRequired()
   {
      await using WebAppFactory factory = new()
      {
         Settings = new Dictionary<string, string?> { ["GalaxyData:Security:RequireHttps"] = "true", ["GalaxyData:Security:HttpsPort"] = "8443" },
      };
      HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://galaxydata.test") });
      HttpResponseMessage response = await client.GetAsync("/api/health", Token);
      response.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
      response.Headers.Location.ShouldBe(new Uri("https://galaxydata.test:8443/api/health"));
   }

   /// <summary>Sets the address a request comes from, as a test server's have none.</summary>
   private sealed class RemoteAddress : IStartupFilter
   {
      public const string Header = "X-Test-Remote";

      public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
      {
         app.Use((context, rest) =>
         {
            if (context.Request.Headers.TryGetValue(Header, out Microsoft.Extensions.Primitives.StringValues address))
            {
               context.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
            }
            return rest(context);
         });
         next(app);
      };
   }

   /// <summary>A request signed out (the session's), from an address, maybe through a proxy.</summary>
   private static async Task<HttpResponseMessage> FromAsync(HttpClient client, string remote, string? forwardedFor = null, string? forwardedProto = null)
   {
      using HttpRequestMessage request = new(HttpMethod.Get, "/api/auth/session");
      request.Headers.Add(RemoteAddress.Header, remote);
      if (forwardedFor != null) { request.Headers.Add("X-Forwarded-For", forwardedFor); }
      if (forwardedProto != null) { request.Headers.Add("X-Forwarded-Proto", forwardedProto); }
      return await client.SendAsync(request, Token);
   }

   /// <summary>
   /// Behind a proxy, the client is who a trusted proxy says it is, and connected as it says: its own requests are
   /// counted as its own, and its answers over HTTPS say so. Others are believed of nothing.
   /// </summary>
   [Fact]
   public async Task TrustedProxiesSayWhoTheClientIs()
   {
      await using WebAppFactory factory = new()
      {
         Settings = new Dictionary<string, string?>
         {
            ["GalaxyData:Proxy:Enabled"] = "true",
            ["GalaxyData:Proxy:KnownProxies:0"] = "10.0.0.1",
            ["GalaxyData:RateLimits:RequestsPerMinute"] = "2",
         },
         ServiceChanges = services => services.AddSingleton<IStartupFilter, RemoteAddress>(),
      };
      HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://galaxydata.test") });
      for (int i = 0; i < 2; i++) { (await FromAsync(client, "10.0.0.1", "203.0.113.5")).StatusCode.ShouldBe(HttpStatusCode.OK); }
      await (await FromAsync(client, "10.0.0.1", "203.0.113.5")).ProblemAsync(429, ProblemCodes.TooManyRequests);
      (await FromAsync(client, "10.0.0.1", "203.0.113.6")).StatusCode.ShouldBe(HttpStatusCode.OK, "another client of the proxy is counted as itself");

      for (int i = 0; i < 2; i++) { (await FromAsync(client, "10.0.0.9", "203.0.113.7")).StatusCode.ShouldBe(HttpStatusCode.OK); }
      await (await FromAsync(client, "10.0.0.9", "203.0.113.8")).ProblemAsync(429, ProblemCodes.TooManyRequests);

      Header(await FromAsync(client, "10.0.0.1", "203.0.113.20", "https"), "Strict-Transport-Security").ShouldNotBeNull("the proxy says the client connected over HTTPS");
      Header(await FromAsync(client, "10.0.0.2", "203.0.113.21", "https"), "Strict-Transport-Security").ShouldBeNull("one not trusted isn't believed");
      // The proxy as IPv6 gives it is the proxy.
      (await FromAsync(client, "::ffff:10.0.0.1", "203.0.113.22")).StatusCode.ShouldBe(HttpStatusCode.OK);
      (await FromAsync(client, "::ffff:10.0.0.1", "203.0.113.22")).StatusCode.ShouldBe(HttpStatusCode.OK);
      await (await FromAsync(client, "10.0.0.1", "203.0.113.22")).ProblemAsync(429, ProblemCodes.TooManyRequests);
   }

   /// <summary>ASP.NET Core's own switch for forwarded headers believes any client: it stops the application, which has its own.</summary>
   [Fact]
   public async Task AspNetCoresForwardedHeadersAreRefused()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["FORWARDEDHEADERS_ENABLED"] = "true" } };
      Should.Throw<Exception>(() => factory.CreateClient()).ToString().ShouldContain("ASPNETCORE_FORWARDEDHEADERS_ENABLED believes any client");
   }

   /// <summary>
   /// Signed out, an address's requests are counted, those refused for want of a session included; an IPv6 client is
   /// its /64. Health checks aren't counted.
   /// </summary>
   [Fact]
   public async Task AddressesRequestsAreLimited()
   {
      await using WebAppFactory factory = new()
      {
         Settings = new Dictionary<string, string?> { ["GalaxyData:RateLimits:RequestsPerMinute"] = "2" },
         ServiceChanges = services => services.AddSingleton<IStartupFilter, RemoteAddress>(),
      };
      HttpClient client = factory.CreateClient();
      async Task<HttpResponseMessage> GetAsync(string path, string remote)
      {
         using HttpRequestMessage request = new(HttpMethod.Get, path);
         request.Headers.Add(RemoteAddress.Header, remote);
         return await client.SendAsync(request, Token);
      }
      for (int i = 0; i < 2; i++) { (await GetAsync("/api/users", "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); }
      await (await GetAsync("/api/users", "198.51.100.1")).ProblemAsync(429, ProblemCodes.TooManyRequests);
      for (int i = 0; i < 5; i++) { (await GetAsync("/api/health", "198.51.100.1")).StatusCode.ShouldBe(HttpStatusCode.OK, "health checks aren't counted"); }

      (await GetAsync("/api/auth/session", "2001:db8::1")).StatusCode.ShouldBe(HttpStatusCode.OK);
      (await GetAsync("/api/auth/session", "2001:db8::2")).StatusCode.ShouldBe(HttpStatusCode.OK);
      await (await GetAsync("/api/auth/session", "2001:db8::ffff:3")).ProblemAsync(429, ProblemCodes.TooManyRequests);
      (await GetAsync("/api/auth/session", "2001:db8:0:1::1")).StatusCode.ShouldBe(HttpStatusCode.OK, "another /64 is another client");
   }

   /// <summary>Without a proxy, what a request says of its client is ignored.</summary>
   [Fact]
   public async Task WithoutAProxyForwardedHeadersAreIgnored()
   {
      await using WebAppFactory factory = new()
      {
         Settings = new Dictionary<string, string?> { ["GalaxyData:RateLimits:RequestsPerMinute"] = "2" },
         ServiceChanges = services => services.AddSingleton<IStartupFilter, RemoteAddress>(),
      };
      HttpClient client = factory.CreateClient();
      (await FromAsync(client, "127.0.0.1", "203.0.113.5")).StatusCode.ShouldBe(HttpStatusCode.OK);
      (await FromAsync(client, "127.0.0.1", "203.0.113.6")).StatusCode.ShouldBe(HttpStatusCode.OK);
      await (await FromAsync(client, "127.0.0.1", "203.0.113.7")).ProblemAsync(429, ProblemCodes.TooManyRequests);
   }

   /// <summary>Settings that don't name addresses and networks in full, or a policy that isn't a header's value, stop the application.</summary>
   [Theory]
   [InlineData("Proxy:KnownProxies:0", "10.0.0.256", "KnownProxies: '10.0.0.256' isn't an IP address")]
   [InlineData("Proxy:KnownProxies:0", "10.1", "KnownProxies: '10.1' isn't an IP address")]
   [InlineData("Proxy:KnownNetworks:0", "10.0.0.0", "KnownNetworks: '10.0.0.0' isn't a network in CIDR form, such as 10.0.0.0/8")]
   [InlineData("Proxy:KnownNetworks:0", "10/8", "KnownNetworks: '10/8' isn't a network in CIDR form, such as 10.0.0.0/8")]
   [InlineData("Security:ContentSecurityPolicy", "default-src 'self'\nscript-src 'none'", "it has no line breaks or other control characters")]
   public async Task SettingsAreChecked(string setting, string value, string problem)
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { [$"GalaxyData:{setting}"] = value } };
      Should.Throw<Exception>(() => factory.CreateClient()).ToString().ShouldContain(problem);
   }

   [Fact]
   public async Task ProxiesAndNetworksInFullAreTaken()
   {
      await using WebAppFactory factory = new()
      {
         Settings = new Dictionary<string, string?>
         {
            ["GalaxyData:Proxy:Enabled"] = "true",
            ["GalaxyData:Proxy:KnownProxies:0"] = "2001:db8::1",
            ["GalaxyData:Proxy:KnownNetworks:0"] = "10.0.0.0/8",
         },
      };
      (await factory.CreateClient().GetAsync("/api/health", Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
   }

   /// <summary>A user's requests a minute are limited, each user's apart; refusals say when to try again.</summary>
   [Fact]
   public async Task EachUsersRequestsAreLimited()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:RateLimits:RequestsPerMinute"] = "30" } };
      TestApi admin = await TestApi.SignedInAsync(factory);
      await admin.CreateUserAsync("lee", "read", "first-password-of-a-user");
      TestApi lee = await TestApi.SignedInAsync(factory, "lee", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      HttpResponseMessage? refused = null;
      for (int i = 0; i < 60 && refused == null; i++)
      {
         HttpResponseMessage response = await admin.GetAsync("/api/auth/session");
         if (response.StatusCode == HttpStatusCode.TooManyRequests) { refused = response; }
      }
      refused.ShouldNotBeNull("the admin's requests run out");
      await refused.ProblemAsync(429, ProblemCodes.TooManyRequests);
      refused.Headers.RetryAfter.ShouldNotBeNull();
      (await lee.GetAsync("/api/auth/session")).StatusCode.ShouldBe(HttpStatusCode.OK, "lee's are lee's own");
   }

   /// <summary>
   /// A user's queries run a few at a time: more, past those waiting, are refused, while others' run. Checking a
   /// query, which runs nothing, isn't one.
   /// </summary>
   [Fact]
   public async Task EachUsersQueriesRunAFewAtATime()
   {
      await using WebAppFactory factory = new()
      {
         Settings = new Dictionary<string, string?> { ["GalaxyData:RateLimits:ConcurrentQueries"] = "1", ["GalaxyData:RateLimits:QueuedQueries"] = "0" },
      };
      TestApi admin = await TestApi.SignedInAsync(factory);
      string shop = await TestSources.ShopAsync(factory);
      await TestSources.AddSqliteAsync(admin, "shop", shop, readOnly: false);
      await TestSources.AddSqliteAsync(admin, "other", await TestSources.ShopAsync(factory, "other.db"));
      await admin.CreateUserAsync("lee", "read", "first-password-of-a-user");
      TestApi lee = await TestApi.SignedInAsync(factory, "lee", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      await (await admin.PostAsync("/api/changes/ops", new { ops = new[] { new { op = "insert", entity = "shop.addresses", tempId = "n1", values = new { customer_id = 3, line1 = "3 Long St" } } } }))
         .JsonAsync(HttpStatusCode.OK);
      JsonElement preview = await (await admin.PostAsync("/api/changes/preview")).JsonAsync(HttpStatusCode.OK);
      object validate = new { text = "shop.orders.count()" };
      object execute = new { text = "other.orders.count()" };
      Task<HttpResponseMessage> commit;
      await using (SqliteConnection holder = new($"Data Source={shop};Pooling=False"))
      {
         // The commit waits for the database, which another holds, and holds the admin's one query meanwhile.
         await holder.OpenAsync(Token);
         await using (SqliteCommand exclusive = new("BEGIN EXCLUSIVE", holder)) { await exclusive.ExecuteNonQueryAsync(Token); }
         commit = admin.PostAsync("/api/changes/commit", new { planId = preview.GetProperty("planId").GetString(), version = preview.GetProperty("version").GetInt32() });
         Stopwatch waited = Stopwatch.StartNew();
         while (true)
         {
            JsonElement commits = await (await admin.GetAsync("/api/audit/commits")).JsonAsync(HttpStatusCode.OK);
            if (commits.GetArrayLength() > 0 && commits[0].GetProperty("status").GetString() == "inProgress") { break; }
            if (waited.Elapsed > TimeSpan.FromSeconds(20)) { Assert.Fail("The commit didn't start"); }
            await Task.Delay(20, Token);
         }
         await (await admin.PostAsync("/api/query/execute", execute)).ProblemAsync(429, ProblemCodes.TooManyRequests);
         (await admin.PostAsync("/api/query/validate", validate)).StatusCode.ShouldBe(HttpStatusCode.OK, "checking a query runs nothing");
         (await lee.PostAsync("/api/query/execute", execute)).StatusCode.ShouldBe(HttpStatusCode.OK, "another user's queries run");
         await using (SqliteCommand rollback = new("ROLLBACK", holder)) { await rollback.ExecuteNonQueryAsync(Token); }
      }
      (await (await commit).JsonAsync(HttpStatusCode.OK)).GetProperty("outcome").GetString().ShouldBe("committed");
      (await admin.PostAsync("/api/query/execute", execute)).StatusCode.ShouldBe(HttpStatusCode.OK);
   }
}
