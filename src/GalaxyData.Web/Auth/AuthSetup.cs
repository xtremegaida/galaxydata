using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Threading.RateLimiting;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Hosting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Auth;

/// <summary>
/// Signing in: a cookie session (<c>gd.auth</c>: HttpOnly, SameSite=Strict, sliding), checked against the user's
/// security stamp; data protection keys in the data directory (DPAPI-protected on Windows), which the cookie and
/// stored secrets depend on; anti-forgery tokens; the policies; and a limit on sign-in attempts per address.
/// </summary>
public static class AuthSetup
{
   public const string CookieName = "gd.auth";

   public const string KeysDirectoryName = "keys";

   /// <summary>The rate limiter policy of the sign-in endpoint.</summary>
   public const string SignInLimit = "sign-in";

   public static IServiceCollection AddGalaxyDataAuth(this IServiceCollection services)
   {
      services.AddMemoryCache();
      services.AddSingleton<SessionValidator>();
      services.AddSingleton<PasswordPolicy>();
      services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();

      services.AddDataProtection().SetApplicationName("GalaxyData");
      services.AddOptions<KeyManagementOptions>().Configure<DataDirectory, ILoggerFactory>((o, data, logging) =>
      {
         o.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(Path.Combine(data.Path, KeysDirectoryName)), logging);
         if (OperatingSystem.IsWindows()) { o.XmlEncryptor = new DpapiXmlEncryptor(protectToLocalMachine: false, logging); }
      });

      services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
      services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme).Configure<IOptions<GalaxyDataOptions>>((o, settings) =>
      {
         o.Cookie.Name = CookieName;
         o.Cookie.HttpOnly = true;
         o.Cookie.SameSite = SameSiteMode.Strict;
         o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
         o.ExpireTimeSpan = settings.Value.Auth.SessionIdleTimeout;
         o.SlidingExpiration = true;
         // An API answers with statuses, not redirects to pages.
         o.Events.OnRedirectToLogin = context => Status(context.Response, StatusCodes.Status401Unauthorized);
         o.Events.OnRedirectToAccessDenied = context => Status(context.Response, StatusCodes.Status403Forbidden);
         o.Events.OnValidatePrincipal = context => context.HttpContext.RequestServices.GetRequiredService<SessionValidator>().ValidateAsync(context);
      });

      services.AddAntiforgery(o =>
      {
         o.HeaderName = Xsrf.HeaderName;
         o.Cookie.Name = Xsrf.AntiforgeryCookieName;
         o.Cookie.SameSite = SameSiteMode.Strict;
         o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
      });

      services.AddAuthorization(Policies.Add);
      services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuthorizationProblems>();

      services.AddRateLimiter(o =>
      {
         o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
         o.AddPolicy(SignInLimit, context =>
         {
            int permits = context.RequestServices.GetRequiredService<IOptions<GalaxyDataOptions>>().Value.Auth.SignInsPerMinute;
            return RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
               _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
         });
         o.OnRejected = (context, _) =>
         {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry))
            {
               context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retry.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            }
            return ValueTask.CompletedTask;
         };
      });
      return services;
   }

   private static Task Status(HttpResponse response, int status)
   {
      response.StatusCode = status;
      return Task.CompletedTask;
   }
}
