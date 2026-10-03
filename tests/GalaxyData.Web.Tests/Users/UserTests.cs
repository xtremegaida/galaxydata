using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Users;

/// <summary>Users, for administrators: making, changing and deleting them, the guards, and the sessions changes end.</summary>
public sealed class UserTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static async Task<JsonElement[]> EventsAsync(TestApi admin) =>
      (await (await admin.GetAsync("/api/audit/admin-events")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().ToArray();

   [Fact]
   public async Task UsersAreMadeChangedAndDeletedInTheAudit()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement created = await (await admin.PostAsync("/api/users", new { userName = "erin", displayName = " Erin E ", role = "read", password = "first-password-of-a-user" }))
         .JsonAsync(HttpStatusCode.Created);
      int id = created.GetProperty("id").GetInt32();
      created.GetProperty("displayName").GetString().ShouldBe("Erin E");
      created.GetProperty("role").GetString().ShouldBe("read");
      created.GetProperty("mustChangePassword").GetBoolean().ShouldBeTrue();
      created.GetProperty("version").GetInt32().ShouldBe(0);
      created.GetProperty("createdAt").GetString()!.ShouldEndWith("Z");

      JsonElement[] users = (await (await admin.GetAsync("/api/users")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().ToArray();
      users.Select(u => u.GetProperty("userName").GetString()).ShouldBe(["admin", "erin"]);

      JsonElement updated = await (await admin.PutAsync($"/api/users/{id}", new { displayName = "Erin", role = "dataManager", isDisabled = false, version = 0 }))
         .JsonAsync(HttpStatusCode.OK);
      updated.GetProperty("role").GetString().ShouldBe("dataManager");
      updated.GetProperty("version").GetInt32().ShouldBe(1);
      // Nothing changed, nothing saved.
      (await (await admin.PutAsync($"/api/users/{id}", new { displayName = "Erin", role = "dataManager", isDisabled = false, version = 1 }))
         .JsonAsync(HttpStatusCode.OK)).GetProperty("version").GetInt32().ShouldBe(1);

      (await admin.DeleteAsync($"/api/users/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
      await (await admin.GetAsync($"/api/users/{id}")).ProblemAsync(404, ProblemCodes.NotFound);

      JsonElement[] events = await EventsAsync(admin);
      events.Select(e => (e.GetProperty("actor").GetString(), e.GetProperty("action").GetString(), e.GetProperty("target").GetString())).ShouldBe([
         ("admin", "user.deleted", "user:erin"),
         ("admin", "user.updated", "user:erin"),
         ("admin", "user.created", "user:erin"),
         ("(system)", "user.created", "user:admin"),
      ]);
      events[1].GetProperty("details").GetString()
         .ShouldBe("""{"displayName":{"from":"Erin E","to":"Erin"},"role":{"from":"read","to":"dataManager"}}""");
      events[3].GetProperty("details").GetString().ShouldBe("""{"role":"admin","bootstrap":true}""");
   }

   [Fact]
   public async Task AUserNameIsOneIgnoringCase()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      await admin.CreateUserAsync("frank", "read", "first-password-of-a-user");
      JsonElement problem = await (await admin.PostAsync("/api/users", new { userName = "FRANK", role = "read", password = "other-password-of-a-user" }))
         .ProblemAsync(409, ProblemCodes.UserNameTaken);
      problem.GetProperty("detail").GetString().ShouldBe("There is a user FRANK already (user names ignore case)");
   }

   [Theory]
   [InlineData("")]
   [InlineData("has space")]
   [InlineData(".dot")]
   [InlineData("x<script>")]
   public async Task AUserNameIsLettersDigitsAndAFewMarks(string name)
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement problem = await (await admin.PostAsync("/api/users", new { userName = name, role = "read", password = "a-good-long-password" }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest);
      problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["UserName"]);
   }

   /// <summary>A role is one of the roles, by name: a number isn't read as one.</summary>
   [Theory]
   [InlineData("7")]
   [InlineData("2")]
   [InlineData("\"superuser\"")]
   public async Task ARoleIsOneOfTheRolesByName(string role)
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      using System.Net.Http.StringContent body = new($$"""{"userName":"pat","role":{{role}},"password":"a-good-long-password"}""",
         System.Text.Encoding.UTF8, "application/json");
      await admin.SessionAsync();
      using System.Net.Http.HttpRequestMessage request = new(System.Net.Http.HttpMethod.Post, "/api/users") { Content = body };
      request.Headers.Add(GalaxyData.Web.Auth.Xsrf.HeaderName, admin.Cookie(GalaxyData.Web.Auth.Xsrf.CookieName)!.Value);
      await (await admin.Client.SendAsync(request, Token)).ProblemAsync(400, ProblemCodes.BadRequest);
      (await (await admin.GetAsync("/api/users")).JsonAsync(HttpStatusCode.OK)).GetArrayLength().ShouldBe(1);
   }

   [Fact]
   public async Task AChangeToAnotherVersionIsAConflict()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = await admin.CreateUserAsync("gina", "read", "first-password-of-a-user");
      await (await admin.PutAsync($"/api/users/{id}", new { displayName = "G", role = "read", isDisabled = false, version = 0 })).JsonAsync(HttpStatusCode.OK);
      JsonElement problem = await (await admin.PutAsync($"/api/users/{id}", new { displayName = "Gina", role = "read", isDisabled = false, version = 0 }))
         .ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      problem.GetProperty("title").GetString().ShouldBe("gina was changed since it was read");
      await (await admin.DeleteAsync($"/api/users/{id}?version=0")).ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      (await admin.DeleteAsync($"/api/users/{id}?version=1")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
   }

   [Fact]
   public async Task AnAdministratorCantDemoteDisableDeleteOrResetThemselves()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      await (await admin.PutAsync("/api/users/1", new { role = "dataManager", isDisabled = false, version = 0 })).ProblemAsync(409, ProblemCodes.OwnAccount);
      await (await admin.PutAsync("/api/users/1", new { role = "admin", isDisabled = true, version = 0 })).ProblemAsync(409, ProblemCodes.OwnAccount);
      await (await admin.DeleteAsync("/api/users/1")).ProblemAsync(409, ProblemCodes.OwnAccount);
      await (await admin.PostAsync("/api/users/1/reset-password", new { password = "another-long-password" })).ProblemAsync(409, ProblemCodes.OwnAccount);
      // A display name is fine.
      (await admin.PutAsync("/api/users/1", new { displayName = "The Admin", role = "admin", isDisabled = false, version = 0 })).StatusCode.ShouldBe(HttpStatusCode.OK);
   }

   /// <summary>
   /// The last enabled administrator stays one. Through the API that takes a race: here, an administrator demoted
   /// in the database (with a session that still says admin) tries to demote the only other.
   /// </summary>
   [Fact]
   public async Task TheLastEnabledAdministratorStaysOne()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int other = await admin.CreateUserAsync("hal", "admin", "first-password-of-a-user");
      using (IServiceScope scope = factory.Services.CreateScope())
      {
         await scope.ServiceProvider.GetRequiredService<MetadataDb>().Users.Where(u => u.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Read), Token);
      }
      JsonElement problem = await (await admin.PutAsync($"/api/users/{other}", new { role = "read", isDisabled = false, version = 0 }))
         .ProblemAsync(409, ProblemCodes.LastAdmin);
      problem.GetProperty("title").GetString().ShouldBe("hal is the last enabled administrator");
      await (await admin.DeleteAsync($"/api/users/{other}")).ProblemAsync(409, ProblemCodes.LastAdmin);
   }

   /// <summary>A new role, disabling, deleting and resetting the password each end the user's sessions at once.</summary>
   [Theory]
   [InlineData("role")]
   [InlineData("disable")]
   [InlineData("delete")]
   [InlineData("reset")]
   public async Task ChangesThatEndAUsersSessions(string change)
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = await admin.CreateUserAsync("ivy", "admin", "first-password-of-a-user");
      TestApi ivy = await TestApi.SignedInAsync(factory, "ivy", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      (await ivy.GetAsync("/api/users")).StatusCode.ShouldBe(HttpStatusCode.OK);
      int version = (await (await admin.GetAsync($"/api/users/{id}")).JsonAsync(HttpStatusCode.OK)).GetProperty("version").GetInt32();
      HttpStatusCode done = change switch
      {
         "role" => (await admin.PutAsync($"/api/users/{id}", new { role = "read", isDisabled = false, version })).StatusCode,
         "disable" => (await admin.PutAsync($"/api/users/{id}", new { role = "admin", isDisabled = true, version })).StatusCode,
         "delete" => (await admin.DeleteAsync($"/api/users/{id}")).StatusCode,
         _ => (await admin.PostAsync($"/api/users/{id}/reset-password", new { password = "third-password-of-a-user" })).StatusCode,
      };
      done.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
      await (await ivy.GetAsync("/api/users")).ProblemAsync(401, ProblemCodes.Unauthenticated);
   }

   /// <summary>A display name alone doesn't end the user's sessions.</summary>
   [Fact]
   public async Task ADisplayNameDoesntEndAUsersSessions()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = await admin.CreateUserAsync("jo", "read", "first-password-of-a-user");
      TestApi jo = await TestApi.SignedInAsync(factory, "jo", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      int version = (await (await admin.GetAsync($"/api/users/{id}")).JsonAsync(HttpStatusCode.OK)).GetProperty("version").GetInt32();
      (await admin.PutAsync($"/api/users/{id}", new { displayName = "Jo", role = "read", isDisabled = false, version })).StatusCode.ShouldBe(HttpStatusCode.OK);
      (await jo.SessionAsync()).GetProperty("user").GetProperty("displayName").GetString().ShouldBe("Jo");
   }

   [Fact]
   public async Task AResetPasswordMustBeChangedAtTheNextSignIn()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = await admin.CreateUserAsync("kim", "read", "first-password-of-a-user");
      await TestApi.SignedInAsync(factory, "kim", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      await (await admin.PostAsync($"/api/users/{id}/reset-password", new { password = "kim" })).ProblemAsync(422, ProblemCodes.WeakPassword);
      JsonElement user = await (await admin.PostAsync($"/api/users/{id}/reset-password", new { password = "reset-by-the-admin" })).JsonAsync(HttpStatusCode.OK);
      user.GetProperty("mustChangePassword").GetBoolean().ShouldBeTrue();
      TestApi kim = new(factory);
      await (await kim.SignInAsync("kim", "second-password-of-a-user")).ProblemAsync(401, ProblemCodes.InvalidCredentials);
      (await (await kim.SignInAsync("kim", "reset-by-the-admin")).JsonAsync(HttpStatusCode.OK))
         .GetProperty("user").GetProperty("mustChangePassword").GetBoolean().ShouldBeTrue();
      (await EventsAsync(admin))[0].GetProperty("action").GetString().ShouldBe("user.password-reset");
   }

   [Fact]
   public async Task TheAuditPagesNewestFirst()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      for (int i = 0; i < 5; i++) { await admin.CreateUserAsync($"u{i}", "read", "a-long-enough-password"); }
      JsonElement[] first = (await (await admin.GetAsync("/api/audit/admin-events?take=4")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().ToArray();
      first.Select(e => e.GetProperty("target").GetString()).ShouldBe(["user:u4", "user:u3", "user:u2", "user:u1"]);
      long last = first[^1].GetProperty("id").GetInt64();
      JsonElement[] next = (await (await admin.GetAsync($"/api/audit/admin-events?take=4&before={last}")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().ToArray();
      next.Select(e => e.GetProperty("target").GetString()).ShouldBe(["user:u0", "user:admin"]);
      await (await admin.GetAsync("/api/audit/admin-events?take=501")).ProblemAsync(400, ProblemCodes.InvalidRequest);
   }
}
