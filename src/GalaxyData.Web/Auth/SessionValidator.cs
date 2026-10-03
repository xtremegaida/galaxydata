using System;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using GalaxyData.Web.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace GalaxyData.Web.Auth;

/// <summary>
/// Ends sessions whose user has since been deleted, disabled, or had their password or role changed (which changes
/// their security stamp). Each user's state is read at most every <see cref="CacheTime"/>, and at once after a
/// change here (<see cref="Forget"/>).
/// </summary>
public sealed class SessionValidator(IMemoryCache cache, IServiceScopeFactory scopes)
{
   public static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(30);

   private sealed record UserState(string Stamp, bool IsDisabled);

   public async Task ValidateAsync(CookieValidatePrincipalContext context)
   {
      ArgumentNullException.ThrowIfNull(context);
      if (context.Principal?.UserId() is not int id || context.Principal.FindFirstValue(UserClaims.Stamp) is not { } stamp)
      {
         await RejectAsync(context);
         return;
      }
      UserState? state = await cache.GetOrCreateAsync(Key(id), async entry =>
      {
         entry.AbsoluteExpirationRelativeToNow = CacheTime;
         await using AsyncServiceScope scope = scopes.CreateAsyncScope();
         MetadataDb db = scope.ServiceProvider.GetRequiredService<MetadataDb>();
         return await db.Users.Where(u => u.Id == id).Select(u => new UserState(u.SecurityStamp, u.IsDisabled)).SingleOrDefaultAsync();
      });
      if (state == null || state.IsDisabled || !string.Equals(state.Stamp, stamp, StringComparison.Ordinal)) { await RejectAsync(context); }
   }

   /// <summary>Reads the user's state afresh at their next request, after a change to it.</summary>
   public void Forget(int userId) => cache.Remove(Key(userId));

   private static async Task RejectAsync(CookieValidatePrincipalContext context)
   {
      context.RejectPrincipal();
      await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
   }

   private static string Key(int userId) => "gd:user-state:" + userId.ToString(CultureInfo.InvariantCulture);
}
