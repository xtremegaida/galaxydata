using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Auth;

/// <summary>Who may call what: every endpoint says, and each role gets what its policies give.</summary>
public sealed class PolicyTests
{
   /// <summary>The endpoints anyone may call; any other must name its policy.</summary>
   private static readonly string[] Anonymous = ["GET api/health", "GET api/auth/session", "POST api/auth/sign-in", "POST api/auth/sign-out"];

   [Fact]
   public async Task EveryEndpointOfTheApiSaysWhoMayCallIt()
   {
      await using WebAppFactory factory = new();
      factory.CreateClient();
      List<string> anonymous = [];
      List<string> unsaid = [];
      foreach (RouteEndpoint endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
      {
         string pattern = endpoint.RoutePattern.RawText!.TrimStart('/');
         if (!pattern.StartsWith("api", System.StringComparison.Ordinal)) { continue; }
         string methods = string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"]);
         string name = $"{methods} {pattern}";
         if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null) { anonymous.Add(name); }
         else if (!endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy != null)) { unsaid.Add(name); }
      }
      unsaid.ShouldBeEmpty();
      anonymous.Order().ShouldBe(Anonymous.Order());
   }

   [Theory]
   [InlineData("read", HttpStatusCode.Forbidden, """{"canRead":true,"canEditData":false,"canAdmin":false}""")]
   [InlineData("dataManager", HttpStatusCode.Forbidden, """{"canRead":true,"canEditData":true,"canAdmin":false}""")]
   [InlineData("admin", HttpStatusCode.OK, """{"canRead":true,"canEditData":true,"canAdmin":true}""")]
   public async Task EachRoleGetsWhatItsPoliciesGive(string role, HttpStatusCode admin, string permissions)
   {
      await using WebAppFactory factory = new();
      TestApi administrator = await TestApi.SignedInAsync(factory);
      await administrator.CreateUserAsync("lee", role, "first-password-of-a-user");
      TestApi lee = await TestApi.SignedInAsync(factory, "lee", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      (await lee.SessionAsync()).GetProperty("user").GetProperty("permissions").GetRawText().ShouldBe(permissions);
      (await lee.GetAsync("/api/openapi/v1.json")).StatusCode.ShouldBe(HttpStatusCode.OK);
      foreach (string path in (string[])["/api/users", "/api/users/1", "/api/audit/admin-events", "/api/connections", "/api/connection-kinds"])
      {
         HttpStatusCode status = (await lee.GetAsync(path)).StatusCode;
         status.ShouldBe(admin, path);
      }
      foreach (string path in (string[])["/api/catalog", "/api/catalog/tree/children", "/api/catalog/tree/search?text=x"])
      {
         (await lee.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.OK, path);
      }
      if (admin == HttpStatusCode.Forbidden)
      {
         await (await lee.PostAsync("/api/users/1/unlock")).ProblemAsync(403, ProblemCodes.Forbidden);
      }
   }

   [Theory]
   [InlineData("/api/users")]
   [InlineData("/api/audit/admin-events")]
   [InlineData("/api/openapi/v1.json")]
   public async Task NoOneSignedInIsUnauthenticated(string path)
   {
      await using WebAppFactory factory = new();
      System.Net.Http.HttpResponseMessage response = await new TestApi(factory).GetAsync(path);
      response.Headers.Location.ShouldBeNull("an API answers with statuses, not redirects");
      await response.ProblemAsync(401, ProblemCodes.Unauthenticated);
   }
}
