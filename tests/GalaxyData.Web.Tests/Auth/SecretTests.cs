using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Auth;

/// <summary>No answer gives a password back, nor what is kept of one.</summary>
public sealed class SecretTests
{
   [Fact]
   public async Task NoAnswerHasAPasswordOrItsHash()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = await admin.CreateUserAsync("nia", "dataManager", "first-password-of-a-user");
      TestApi nia = await TestApi.SignedInAsync(factory, "nia", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      await nia.SignInAsync("nia", "wrong-password-of-a-user");
      await nia.PostAsync("/api/auth/change-password", new { currentPassword = "wrong-password-of-a-user", newPassword = "third-password-of-a-user" });
      await admin.PostAsync($"/api/users/{id}/reset-password", new { password = "reset-password-of-a-user" });
      await admin.PostAsync("/api/users", new { userName = "oz", role = "read", password = "short-oz" });
      await admin.GetAsync("/api/users");
      await admin.GetAsync($"/api/users/{id}");
      await admin.GetAsync("/api/audit/admin-events");
      (await admin.GetAsync("/api/openapi/v1.json")).StatusCode.ShouldBe(HttpStatusCode.OK);

      List<string> responses = [.. admin.Responses, .. nia.Responses];
      responses.Count.ShouldBeGreaterThan(10);
      string[] secrets = [TestApi.AdminPassword, "first-password-of-a-user", "second-password-of-a-user", "wrong-password-of-a-user", "third-password-of-a-user", "reset-password-of-a-user",
                          "short-oz", "passwordHash", "securityStamp", "AQAAAA"];
      foreach (string secret in secrets)
      {
         responses.Where(r => r.Contains(secret, System.StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty(secret);
      }
   }
}
