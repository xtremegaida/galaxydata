using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GalaxyData.Web.Hosting;

/// <summary>
/// Set by a public dashboard's page (<c>/embed/{token}</c>) as it answers: the sites that may frame it, as
/// <c>frame-ancestors</c> sources, read and written by the application (never as given).
/// </summary>
public sealed record EmbedFraming(string FrameAncestors);

/// <summary>
/// The headers every answer has: no sniffing of types, no framing, no referrer (the client's addresses name
/// entities, filters and rows), no sharing with other origins, no device features, and a content security policy:
/// the client's (<see cref="SecuritySettings.ContentSecurityPolicy"/>), or for the API one that loads nothing. The
/// API's answers are never cached: they hold data. Over HTTPS, browsers are told to keep to it (HSTS), but for
/// local hosts. They are set as the answer starts, so they hold for answers made over (an error's) too.
/// </summary>
public static class SecurityHeaders
{
   /// <summary>The API's policy: its answers are data, which load nothing and aren't framed.</summary>
   public const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

   public const string PermissionsPolicy = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

   /// <summary>Hosts browsers aren't told to keep to HTTPS for, as ASP.NET Core's HSTS doesn't.</summary>
   private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "[::1]"];

   /// <summary>The client's policy with its <c>frame-ancestors</c> (added, if it has none) as given; that alone without a policy.</summary>
   public static string Framed(string? policy, string ancestors)
   {
      ArgumentNullException.ThrowIfNull(ancestors);
      string directive = "frame-ancestors " + ancestors;
      if (string.IsNullOrWhiteSpace(policy)) { return directive; }
      string[] parts = policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      bool replaced = false;
      for (int i = 0; i < parts.Length; i++)
      {
         if (parts[i].StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase))
         {
            parts[i] = directive;
            replaced = true;
         }
      }
      return string.Join("; ", replaced ? parts : [.. parts, directive]);
   }

   public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, SecuritySettings settings, bool hsts)
   {
      ArgumentNullException.ThrowIfNull(app);
      ArgumentNullException.ThrowIfNull(settings);
      string? client = string.IsNullOrWhiteSpace(settings.ContentSecurityPolicy) ? null : settings.ContentSecurityPolicy.Trim();
      string transport = "max-age=" + ((long)settings.HstsMaxAge.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
      return app.Use((context, next) =>
      {
         HttpResponse response = context.Response;
         bool api = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
         bool secure = hsts && context.Request.IsHttps && !LocalHosts.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase);
         response.OnStarting(() =>
         {
            IHeaderDictionary headers = response.Headers;
            // A public dashboard's page may be framed, by the sites its feature says; nothing else.
            EmbedFraming? framing = context.Features.Get<EmbedFraming>();
            headers.XContentTypeOptions = "nosniff";
            if (framing == null) { headers.XFrameOptions = "DENY"; }
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = framing == null ? "same-origin" : "cross-origin";
            headers["Permissions-Policy"] = PermissionsPolicy;
            if (api)
            {
               headers.ContentSecurityPolicy = ApiContentSecurityPolicy;
               if (headers.CacheControl.Count == 0) { headers.CacheControl = "no-store"; }
            }
            else if (framing != null)
            {
               headers.ContentSecurityPolicy = Framed(client, framing.FrameAncestors);
            }
            else if (client != null)
            {
               headers.ContentSecurityPolicy = client;
            }
            if (secure) { headers.StrictTransportSecurity = transport; }
            return Task.CompletedTask;
         });
         return next(context);
      });
   }
}
