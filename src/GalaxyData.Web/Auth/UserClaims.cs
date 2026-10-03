using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using GalaxyData.Web.Metadata;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace GalaxyData.Web.Auth;

/// <summary>What a session's cookie says of its user: id, name, role, security stamp, and a password to change.</summary>
public static class UserClaims
{
   public const string Stamp = "gd:stamp";

   public const string MustChangePassword = "gd:must-change-password";

   public static ClaimsPrincipal Principal(AppUser user)
   {
      ArgumentNullException.ThrowIfNull(user);
      List<Claim> claims =
      [
         new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
         new(ClaimTypes.Name, user.UserName),
         new(ClaimTypes.Role, user.Role.ToString()),
         new(Stamp, user.SecurityStamp),
      ];
      if (user.MustChangePassword) { claims.Add(new Claim(MustChangePassword, "true")); }
      return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role));
   }

   /// <summary>The signed-in user's id; null when no one is signed in.</summary>
   public static int? UserId(this ClaimsPrincipal principal)
   {
      ArgumentNullException.ThrowIfNull(principal);
      return principal.Identity?.IsAuthenticated == true && int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out int id)
         ? id
         : null;
   }

   /// <summary>The signed-in user's id, on an endpoint only signed-in users reach.</summary>
   public static int RequiredUserId(this ClaimsPrincipal principal) =>
      principal.UserId() ?? throw new InvalidOperationException("No user is signed in, on an endpoint only signed-in users reach");
}
