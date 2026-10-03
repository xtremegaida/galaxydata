using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using GalaxyData.Web.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Hosting;

/// <summary>
/// How much each user may ask (<see cref="RateLimitSettings"/>): requests a minute, for each user (or, signed out,
/// each address; IPv6 addresses by their /64, which one client may have), health checks aside; and requests that
/// run queries or reach sources (<see cref="Queries"/>) at once, a few more waiting. Refusals are
/// <c>429 too-many-requests</c>, with <c>Retry-After</c> when there is a time to say.
/// </summary>
public static class RateLimits
{
   /// <summary>The policy of the endpoints that run queries or reach sources.</summary>
   public const string Queries = "queries";

   public static IServiceCollection AddGalaxyDataRateLimits(this IServiceCollection services)
   {
      services.Configure<RateLimiterOptions>(o =>
      {
         o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
         {
            int perMinute = Settings(context).RequestsPerMinute;
            // Probes check health often, maybe from an address others share.
            if (perMinute == 0 || !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
                context.Request.Path.StartsWithSegments("/api/health", StringComparison.OrdinalIgnoreCase))
            {
               return RateLimitPartition.GetNoLimiter(string.Empty);
            }
            return RateLimitPartition.GetTokenBucketLimiter(Partition(context), _ => Bucket(perMinute));
         });
         o.AddPolicy(Queries, context =>
         {
            RateLimitSettings limits = Settings(context);
            return RateLimitPartition.GetConcurrencyLimiter(Partition(context), _ => new ConcurrencyLimiterOptions
            {
               PermitLimit = limits.ConcurrentQueries,
               QueueLimit = limits.QueuedQueries,
               QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
         });
      });
      return services;
   }

   /// <summary>A minute's requests at once, given back at that rate: about a second's worth at a time (one at a time, for fewer than 60 a minute).</summary>
   private static TokenBucketRateLimiterOptions Bucket(int perMinute)
   {
      int tokens = Math.Max(1, perMinute / 60);
      return new TokenBucketRateLimiterOptions
      {
         TokenLimit = perMinute,
         TokensPerPeriod = tokens,
         ReplenishmentPeriod = TimeSpan.FromMinutes((double)tokens / perMinute),
         QueueLimit = 0,
         AutoReplenishment = true,
      };
   }

   /// <summary>Whose requests they are: a signed-in user's, or an address's.</summary>
   private static string Partition(HttpContext context) =>
      context.User.UserId() is int id ? "user:" + id.ToString(CultureInfo.InvariantCulture) : "address:" + Address(context.Connection.RemoteIpAddress);

   /// <summary>
   /// A client's address, as limits count by it: an IPv4 address, or an IPv6 address's /64, as one client may have
   /// all of one (an IPv4 address written as IPv6 is the IPv4 address).
   /// </summary>
   public static string Address(IPAddress? address)
   {
      if (address == null) { return "unknown"; }
      if (address.IsIPv4MappedToIPv6) { address = address.MapToIPv4(); }
      if (address.AddressFamily != AddressFamily.InterNetworkV6) { return address.ToString(); }
      byte[] bytes = address.GetAddressBytes();
      Array.Clear(bytes, 8, 8);
      return new IPAddress(bytes) + "/64";
   }

   private static RateLimitSettings Settings(HttpContext context) =>
      context.RequestServices.GetRequiredService<IOptions<GalaxyDataOptions>>().Value.RateLimits;
}
