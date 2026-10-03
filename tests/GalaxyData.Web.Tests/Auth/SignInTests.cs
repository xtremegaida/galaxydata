using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Problems;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Auth;

/// <summary>Signing in and out: the session, the anti-forgery token, wrong passwords, lockout, disabled users and the rate limit.</summary>
public sealed class SignInTests
{
   [Fact]
   public async Task TheSessionSaysNoOneIsSignedInAndGivesAToken()
   {
      await using WebAppFactory factory = new();
      TestApi api = new(factory);
      HttpResponseMessage response = await api.GetAsync("/api/auth/session");
      (await response.JsonAsync(HttpStatusCode.OK)).GetRawText().ShouldBe("""{"signedIn":false,"user":null}""");
      response.Headers.CacheControl?.NoStore.ShouldBeTrue();
      api.Cookie(Xsrf.CookieName)!.HttpOnly.ShouldBeFalse();
      api.Cookie(Xsrf.AntiforgeryCookieName)!.HttpOnly.ShouldBeTrue();
   }

   [Fact]
   public async Task SigningInGivesASessionCookieAndANewToken()
   {
      await using WebAppFactory factory = new();
      TestApi api = new(factory);
      await api.SessionAsync();
      string anonymousToken = api.Cookie(Xsrf.CookieName)!.Value;
      HttpResponseMessage response = await api.SignInAsync(TestApi.AdminName, TestApi.AdminPassword);
      JsonElement session = await response.JsonAsync(HttpStatusCode.OK);
      session.GetProperty("signedIn").GetBoolean().ShouldBeTrue();
      JsonElement user = session.GetProperty("user");
      user.GetProperty("userName").GetString().ShouldBe("admin");
      user.GetProperty("role").GetString().ShouldBe("admin");
      user.GetProperty("mustChangePassword").GetBoolean().ShouldBeFalse();
      user.GetProperty("permissions").GetRawText().ShouldBe("""{"canRead":true,"canEditData":true,"canAdmin":true}""");

      string cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthSetup.CookieName + "=", StringComparison.Ordinal));
      cookie.ShouldContain("httponly", Case.Insensitive);
      cookie.ShouldContain("samesite=strict", Case.Insensitive);
      cookie.ShouldNotContain("expires", Case.Insensitive);
      api.Cookie(Xsrf.CookieName)!.Value.ShouldNotBe(anonymousToken, "a token is for the user it was made for");
      (await api.GetAsync("/api/users")).StatusCode.ShouldBe(HttpStatusCode.OK);
      (await api.SessionAsync()).GetProperty("user").GetProperty("userName").GetString().ShouldBe("admin");
   }

   [Fact]
   public async Task RequestsThatChangeAnythingNeedTheToken()
   {
      await using WebAppFactory factory = new();
      TestApi api = new(factory);
      await api.SessionAsync();
      HttpResponseMessage without = await api.SendAsync(HttpMethod.Post, "/api/auth/sign-in", new { userName = "admin", password = TestApi.AdminPassword }, withToken: false);
      JsonElement problem = await without.ProblemAsync(400, ProblemCodes.XsrfTokenInvalid);
      problem.GetProperty("detail").GetString()!.ShouldContain("X-XSRF-TOKEN");

      // The anonymous token doesn't serve once signed in.
      string anonymousToken = api.Cookie(Xsrf.CookieName)!.Value;
      await (await api.SignInAsync("admin", TestApi.AdminPassword)).JsonAsync(HttpStatusCode.OK);
      using HttpRequestMessage stale = new(HttpMethod.Post, "/api/users/1/unlock");
      stale.Headers.Add(Xsrf.HeaderName, anonymousToken);
      await (await api.Client.SendAsync(stale, TestContext.Current.CancellationToken)).ProblemAsync(400, ProblemCodes.XsrfTokenInvalid);
      (await api.PostAsync("/api/users/1/unlock")).StatusCode.ShouldBe(HttpStatusCode.OK);
   }

   /// <summary>A wrong password and a user who isn't one get the same answer.</summary>
   [Fact]
   public async Task AWrongPasswordAndAnUnknownUserAreTheSame()
   {
      await using WebAppFactory factory = new();
      TestApi api = new(factory);
      JsonElement wrong = await (await api.SignInAsync("admin", "not-the-password")).ProblemAsync(401, ProblemCodes.InvalidCredentials);
      JsonElement unknown = await (await api.SignInAsync("nobody", "not-the-password")).ProblemAsync(401, ProblemCodes.InvalidCredentials);
      wrong.GetProperty("title").GetString().ShouldBe("The user name or password isn't right");
      unknown.GetProperty("title").GetString().ShouldBe(wrong.GetProperty("title").GetString());
      (await api.SessionAsync()).GetProperty("signedIn").GetBoolean().ShouldBeFalse();
   }

   [Fact]
   public async Task UserNamesIgnoreCase()
   {
      await using WebAppFactory factory = new();
      TestApi api = new(factory);
      (await api.SignInAsync(" ADMIN ", TestApi.AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
   }

   /// <summary>Failed sign-ins in a row lock the user out for a while, even with the right password; the right one then signs in, and the count starts again.</summary>
   [Fact]
   public async Task TooManyFailedSignInsLockTheUserOut()
   {
      ManualClock clock = new();
      await using WebAppFactory factory = new()
      {
         Settings = new Dictionary<string, string?> { ["GalaxyData:Auth:MaxFailedSignIns"] = "3", ["GalaxyData:Auth:LockoutDuration"] = "00:10:00" },
         ServiceChanges = services => services.AddSingleton<TimeProvider>(clock),
      };
      TestApi api = new(factory);
      for (int i = 0; i < 3; i++) { await (await api.SignInAsync("admin", "wrong")).ProblemAsync(401, ProblemCodes.InvalidCredentials); }
      JsonElement locked = await (await api.SignInAsync("admin", TestApi.AdminPassword)).ProblemAsync(401, ProblemCodes.LockedOut);
      locked.GetProperty("detail").GetString()!.ShouldStartWith("Try again in 10 minutes");

      clock.Advance(TimeSpan.FromMinutes(11));
      (await api.SignInAsync("admin", TestApi.AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
      await api.PostAsync("/api/auth/sign-out");
      // The count started again: two more failures don't lock.
      for (int i = 0; i < 2; i++) { await (await api.SignInAsync("admin", "wrong")).ProblemAsync(401, ProblemCodes.InvalidCredentials); }
      (await api.SignInAsync("admin", TestApi.AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
   }

   [Fact]
   public async Task AnAdministratorUnlocksAUser()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Auth:MaxFailedSignIns"] = "2" } };
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = await admin.CreateUserAsync("ann", "read", "first-password-of-a-user");
      TestApi ann = new(factory);
      for (int i = 0; i < 2; i++) { await ann.SignInAsync("ann", "wrong"); }
      await (await ann.SignInAsync("ann", "first-password-of-a-user")).ProblemAsync(401, ProblemCodes.LockedOut);
      JsonElement user = await (await admin.GetAsync($"/api/users/{id}")).JsonAsync(HttpStatusCode.OK);
      user.GetProperty("lockedOutUntil").ValueKind.ShouldBe(JsonValueKind.String);

      user = await (await admin.PostAsync($"/api/users/{id}/unlock")).JsonAsync(HttpStatusCode.OK);
      user.GetProperty("lockedOutUntil").ValueKind.ShouldBe(JsonValueKind.Null);
      (await ann.SignInAsync("ann", "first-password-of-a-user")).StatusCode.ShouldBe(HttpStatusCode.OK);
   }

   /// <summary>A disabled user is told so only with their password; without it, they are as anyone else.</summary>
   [Fact]
   public async Task ADisabledUserIsToldSoOnlyWithTheirPassword()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = await admin.CreateUserAsync("bob", "read", "first-password-of-a-user");
      await (await admin.PutAsync($"/api/users/{id}", new { role = "read", isDisabled = true, version = 0 })).JsonAsync(HttpStatusCode.OK);
      TestApi bob = new(factory);
      await (await bob.SignInAsync("bob", "wrong")).ProblemAsync(401, ProblemCodes.InvalidCredentials);
      await (await bob.SignInAsync("bob", "first-password-of-a-user")).ProblemAsync(403, ProblemCodes.AccountDisabled);
   }

   [Fact]
   public async Task SignInsFromAnAddressAreLimited()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Auth:SignInsPerMinute"] = "3" } };
      TestApi api = new(factory);
      for (int i = 0; i < 3; i++) { await (await api.SignInAsync("admin", "wrong")).ProblemAsync(401, ProblemCodes.InvalidCredentials); }
      HttpResponseMessage limited = await api.SignInAsync("admin", TestApi.AdminPassword);
      await limited.ProblemAsync(429, ProblemCodes.TooManyRequests);
      limited.Headers.RetryAfter.ShouldNotBeNull();
   }

   [Fact]
   public async Task SigningOutEndsTheSession()
   {
      await using WebAppFactory factory = new();
      TestApi api = await TestApi.SignedInAsync(factory);
      (await (await api.PostAsync("/api/auth/sign-out")).JsonAsync(HttpStatusCode.OK)).GetRawText().ShouldBe("""{"signedIn":false,"user":null}""");
      api.Cookie(AuthSetup.CookieName).ShouldBeNull();
      await (await api.GetAsync("/api/users")).ProblemAsync(401, ProblemCodes.Unauthenticated);
      (await api.SessionAsync()).GetProperty("signedIn").GetBoolean().ShouldBeFalse();
   }

   [Fact]
   public async Task ARequestWithoutTheValuesItNeedsSaysWhich()
   {
      await using WebAppFactory factory = new();
      TestApi api = new(factory);
      JsonElement problem = await (await api.PostAsync("/api/auth/sign-in", new { userName = "admin" })).ProblemAsync(400, ProblemCodes.InvalidRequest);
      problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["password"]);
   }
}
