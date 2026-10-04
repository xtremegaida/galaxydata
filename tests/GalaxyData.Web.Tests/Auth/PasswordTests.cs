using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using GalaxyData.Web.Problems;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Auth;

/// <summary>Changing one's password: first, when it must be; what a password must be; and the other sessions it ends.</summary>
public sealed class PasswordTests
{
   /// <summary>A user who must change their password may do nothing else, and is told so, until they have.</summary>
   [Fact]
   public async Task APasswordToChangeComesFirst()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      await admin.CreateUserAsync("carol", "dataManager", "first-password-of-a-user", "Carol");
      TestApi carol = new(factory);
      JsonElement session = await (await carol.SignInAsync("carol", "first-password-of-a-user")).JsonAsync(HttpStatusCode.OK);
      session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean().ShouldBeTrue();
      session.GetProperty("user").GetProperty("permissions").GetRawText().ShouldBe("""{"canRead":false,"canEditData":false,"canAdmin":false}""");
      (await (await carol.GetAsync("/api/auth/password-policy")).JsonAsync(HttpStatusCode.OK)).GetRawText()
         .ShouldBe("""{"minimumLength":12,"maximumLength":256}""", "the form says what a password must be");
      JsonElement refused = await (await carol.GetAsync("/api/openapi/v1.json")).ProblemAsync(403, ProblemCodes.PasswordChangeRequired);
      refused.GetProperty("detail").GetString()!.ShouldContain("/api/auth/change-password");

      await (await carol.PostAsync("/api/auth/change-password", new { currentPassword = "wrong", newPassword = "second-password-of-a-user" }))
         .ProblemAsync(422, ProblemCodes.WrongPassword);
      JsonElement shortOne = await (await carol.PostAsync("/api/auth/change-password", new { currentPassword = "first-password-of-a-user", newPassword = "short" }))
         .ProblemAsync(422, ProblemCodes.WeakPassword);
      shortOne.GetProperty("detail").GetString().ShouldBe("A password needs at least 12 characters");
      JsonElement named = await (await carol.PostAsync("/api/auth/change-password", new { currentPassword = "first-password-of-a-user", newPassword = "my name is CAROL!" }))
         .ProblemAsync(422, ProblemCodes.WeakPassword);
      named.GetProperty("detail").GetString().ShouldBe("A password may not hold the user name");
      JsonElement same = await (await carol.PostAsync("/api/auth/change-password", new { currentPassword = "first-password-of-a-user", newPassword = "first-password-of-a-user" }))
         .ProblemAsync(422, ProblemCodes.WeakPassword);
      same.GetProperty("detail").GetString().ShouldBe("It must differ from the current one");

      session = await (await carol.PostAsync("/api/auth/change-password", new { currentPassword = "first-password-of-a-user", newPassword = "a-better-password-2" }))
         .JsonAsync(HttpStatusCode.OK);
      session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean().ShouldBeFalse();
      session.GetProperty("user").GetProperty("permissions").GetRawText().ShouldBe("""{"canRead":true,"canEditData":true,"canAdmin":false}""");
      (await carol.GetAsync("/api/openapi/v1.json")).StatusCode.ShouldBe(HttpStatusCode.OK);
      (await new TestApi(factory).SignInAsync("carol", "a-better-password-2")).StatusCode.ShouldBe(HttpStatusCode.OK);
   }

   /// <summary>Changing the password keeps the session it was changed in, and ends the user's others at once.</summary>
   [Fact]
   public async Task ChangingThePasswordEndsTheOtherSessions()
   {
      await using WebAppFactory factory = new();
      TestApi here = await TestApi.SignedInAsync(factory);
      TestApi there = await TestApi.SignedInAsync(factory);
      (await there.GetAsync("/api/users")).StatusCode.ShouldBe(HttpStatusCode.OK);
      await (await here.PostAsync("/api/auth/change-password", new { currentPassword = TestApi.AdminPassword, newPassword = "a-new-password-here" }))
         .JsonAsync(HttpStatusCode.OK);
      (await here.GetAsync("/api/users")).StatusCode.ShouldBe(HttpStatusCode.OK);
      (await here.PostAsync("/api/users/1/unlock")).StatusCode.ShouldBe(HttpStatusCode.OK, "the token is the new session's");
      await (await there.GetAsync("/api/users")).ProblemAsync(401, ProblemCodes.Unauthenticated);
   }

   [Fact]
   public async Task TheFirstAdministratorMayHaveToChangeThePassword()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Bootstrap:RequirePasswordChange"] = "true" } };
      TestApi admin = new(factory);
      JsonElement session = await (await admin.SignInAsync(TestApi.AdminName, TestApi.AdminPassword)).JsonAsync(HttpStatusCode.OK);
      session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean().ShouldBeTrue();
      await (await admin.GetAsync("/api/users")).ProblemAsync(403, ProblemCodes.PasswordChangeRequired);
   }

   [Fact]
   public async Task TheMinimumLengthIsASetting()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Auth:MinimumPasswordLength"] = "20" } };
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement problem = await (await admin.PostAsync("/api/users", new { userName = "dan", role = "read", password = "only-17-characters" }))
         .ProblemAsync(422, ProblemCodes.WeakPassword);
      problem.GetProperty("detail").GetString().ShouldBe("A password needs at least 20 characters");
   }
}
