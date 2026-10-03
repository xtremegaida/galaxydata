using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Problems;

/// <summary>
/// What a request that fails is answered with: the engine's failures as the problems they are, the rest as 500s
/// that say nothing of what failed (but in development), and never a source's own words, which may name its server.
/// </summary>
public sealed class ProblemTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static readonly SourceInfo Shop = new("shop", "postgresql", "public");

   private static readonly QueryDiagnostic[] SyntaxError =
   [
      QueryDiagnostic.Error(DiagnosticCodes.SyntaxError, "Expected ')' to close the call", 22, 23),
      QueryDiagnostic.Warning(DiagnosticCodes.ShadowedName, "'orders' now means this subtree", 0, 6),
   ];

   private static readonly QueryDiagnostic[] BindError = [QueryDiagnostic.Error(DiagnosticCodes.UnknownName, "There is no 'custmers' in shop", 5, 13)];

   /// <summary>The application's host, with endpoints that fail as <paramref name="kind"/> says, under <c>/api/test/{kind}</c>.</summary>
   private static async Task<(WebApplication App, HttpClient Client, string Data)> HostAsync(string environment = "Production")
   {
      string data = Path.Combine(Path.GetTempPath(), "gd-web-tests", Guid.NewGuid().ToString("N")[..12]);
      WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
      builder.WebHost.UseTestServer();
      builder.Logging.ClearProviders();
      builder.Configuration["GalaxyData:DataDirectory"] = data;
      builder.Configuration["GalaxyData:Bootstrap:AdminPassword"] = TestApi.AdminPassword;
      builder.AddGalaxyData();
      WebApplication app = builder.Build();
      app.UseGalaxyData();
      app.MapGet("/api/test/{kind}", (string kind) => kind switch
      {
         "syntax" => throw new QueryException(SyntaxError),
         "bind" => throw new QueryException(BindError),
         "bind-result" => ApiProblems.Diagnostics(BindError),
         "timeout" => throw new QueryTimeoutException(TimeSpan.FromSeconds(30)),
         "down" => throw new SourceUnavailableException(Shop, "Failed to connect to db.internal.example:5432: password authentication failed for user \"gd_app\""),
         "failed" => throw new QueryExecutionException("shop (PostgreSQL) failed to run the query: division by zero"),
         "script" => throw new DmlScriptException(Shop, [new ScriptProblem("This string isn't closed", 30, 12, 3), new ScriptProblem("DROP isn't a change to rows", 50, 4, 4)]),
         "conflict" => throw new ApiException(409, "stale-plan", "The changes were previewed before they changed", "Preview them again"),
         "bug" => throw new InvalidOperationException("the secret of the bug"),
         "concurrency" => throw new DbUpdateConcurrencyException("The database operation was expected to affect 1 row(s), but actually affected 0 row(s)"),
         _ => Results.Ok(),
      }).AllowAnonymous();
      app.MapGalaxyData();
      await app.StartAsync(Token);
      return (app, app.GetTestClient(), data);
   }

   /// <summary>An answer made over, for a failure, keeps the security headers, HSTS too.</summary>
   [Fact]
   public async Task FailuresKeepTheSecurityHeaders()
   {
      (WebApplication app, HttpClient client, string data) = await HostAsync();
      try
      {
         HttpResponseMessage response = await client.GetAsync("https://galaxydata.test/api/test/bug", Token);
         await response.ProblemAsync(500, ProblemCodes.InternalError);
         response.Headers.GetValues("Strict-Transport-Security").Single().ShouldBe("max-age=15552000");
         response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
         response.Headers.CacheControl!.NoStore.ShouldBeTrue();
      }
      finally
      {
         await app.DisposeAsync();
         Directory.Delete(data, recursive: true);
      }
   }

   private static async Task<JsonElement> GetProblemAsync(string kind, int status, string code, string environment = "Production")
   {
      (WebApplication app, HttpClient client, string data) = await HostAsync(environment);
      try
      {
         return await (await client.GetAsync($"/api/test/{kind}", Token)).ProblemAsync(status, code);
      }
      finally
      {
         await app.DisposeAsync();
         Directory.Delete(data, recursive: true);
      }
   }

   private static List<(string Code, string Severity, string Message, int Start, int End)> Diagnostics(JsonElement problem) =>
      problem.GetProperty("diagnostics").EnumerateArray()
         .Select(d => (d.GetProperty("code").GetString()!, d.GetProperty("severity").GetString()!, d.GetProperty("message").GetString()!,
                       d.GetProperty("start").GetInt32(), d.GetProperty("end").GetInt32()))
         .ToList();

   [Fact]
   public async Task AQueryThatDoesntParseIsABadRequestSayingWhere()
   {
      JsonElement problem = await GetProblemAsync("syntax", 400, ProblemCodes.QuerySyntax);
      problem.GetProperty("title").GetString().ShouldBe("The query can't be read");
      problem.GetProperty("detail").GetString().ShouldBe("Expected ')' to close the call");
      Diagnostics(problem).ShouldBe([
         (DiagnosticCodes.SyntaxError, "error", "Expected ')' to close the call", 22, 23),
         (DiagnosticCodes.ShadowedName, "warning", "'orders' now means this subtree", 0, 6),
      ]);
   }

   [Theory]
   [InlineData("bind")]
   [InlineData("bind-result")]
   public async Task AQueryThatDoesntBindIsUnprocessableSayingWhere(string kind)
   {
      JsonElement problem = await GetProblemAsync(kind, 422, ProblemCodes.QueryInvalid);
      problem.GetProperty("title").GetString().ShouldBe("The query can't run as written");
      problem.GetProperty("detail").GetString().ShouldBe("There is no 'custmers' in shop");
      Diagnostics(problem).ShouldBe([(DiagnosticCodes.UnknownName, "error", "There is no 'custmers' in shop", 5, 13)]);
   }

   [Fact]
   public async Task AQueryTooSlowIsAGatewayTimeout()
   {
      JsonElement problem = await GetProblemAsync("timeout", 504, ProblemCodes.QueryTimeout);
      problem.GetProperty("detail").GetString().ShouldBe("The query ran longer than 30 s, the most it may, and was stopped");
   }

   /// <summary>A source that can't be reached is a bad gateway naming the source, not its server, login or the database's words.</summary>
   [Fact]
   public async Task ASourceDownIsABadGatewayThatSaysNothingOfTheServer()
   {
      JsonElement problem = await GetProblemAsync("down", 502, ProblemCodes.SourceUnavailable);
      problem.GetProperty("source").GetString().ShouldBe("shop");
      problem.GetProperty("detail").GetString().ShouldBe("Couldn't connect to shop");
      string text = problem.GetRawText();
      text.ShouldNotContain("db.internal");
      text.ShouldNotContain("gd_app");
      text.ShouldNotContain("password");
   }

   [Fact]
   public async Task AQueryThatFailsAsItRunsIsUnprocessable()
   {
      JsonElement problem = await GetProblemAsync("failed", 422, ProblemCodes.QueryFailed);
      problem.GetProperty("detail").GetString().ShouldBe("shop (PostgreSQL) failed to run the query: division by zero");
   }

   [Fact]
   public async Task AScriptThatCantRunSaysWhere()
   {
      JsonElement problem = await GetProblemAsync("script", 422, ProblemCodes.ScriptInvalid);
      problem.GetProperty("title").GetString().ShouldBe("The script for shop can't run");
      problem.GetProperty("source").GetString().ShouldBe("shop");
      problem.GetProperty("problems").EnumerateArray()
         .Select(p => (p.GetProperty("message").GetString(), p.GetProperty("line").GetInt32(), p.GetProperty("start").GetInt32(), p.GetProperty("length").GetInt32()))
         .ShouldBe([("This string isn't closed", 3, 30, 12), ("DROP isn't a change to rows", 4, 50, 4)]);
   }

   [Fact]
   public async Task AChangeToWhatSomeoneElseChangedFirstIsAConflict()
   {
      JsonElement problem = await GetProblemAsync("concurrency", 409, ProblemCodes.ConcurrencyConflict);
      problem.GetProperty("title").GetString().ShouldBe("It was changed by someone else first");
   }

   [Fact]
   public async Task TheApplicationsOwnProblemsKeepTheirCodes()
   {
      JsonElement problem = await GetProblemAsync("conflict", 409, "stale-plan");
      problem.GetProperty("title").GetString().ShouldBe("The changes were previewed before they changed");
      problem.GetProperty("detail").GetString().ShouldBe("Preview them again");
   }

   [Fact]
   public async Task AnUnexpectedFailureSaysNothingOfItself()
   {
      JsonElement problem = await GetProblemAsync("bug", 500, ProblemCodes.InternalError);
      problem.GetProperty("title").GetString().ShouldBe("Something went wrong");
      problem.TryGetProperty("detail", out _).ShouldBeFalse();
      problem.GetRawText().ShouldNotContain("secret");
   }

   [Fact]
   public async Task InDevelopmentAnUnexpectedFailureSaysWhatItWas()
   {
      JsonElement problem = await GetProblemAsync("bug", 500, ProblemCodes.InternalError, "Development");
      problem.GetProperty("detail").GetString()!.ShouldStartWith("System.InvalidOperationException: the secret of the bug");
   }
}
