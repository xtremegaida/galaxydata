using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GalaxyData.Web.Features.Health;

/// <summary>Whether the application can do its work, and each of its checks: names and statuses only, as anyone may ask.</summary>
public sealed record HealthResponse(HealthStatus Status, IReadOnlyList<HealthCheckDto> Checks);

public sealed record HealthCheckDto(string Name, HealthStatus Status);

public static class HealthEndpoints
{
   public static RouteGroupBuilder MapHealth(this RouteGroupBuilder api)
   {
      api.MapGet("/health", GetAsync)
         .WithName("GetHealth")
         .WithTags("Health")
         .WithSummary("Whether the application can do its work")
         .WithDescription("200 when it can; 503, with the checks that failed, when it can't. Anyone may ask.")
         .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable)
         .AllowAnonymous();
      return api;
   }

   private static async Task<Results<Ok<HealthResponse>, JsonHttpResult<HealthResponse>>> GetAsync(HealthCheckService health, HttpContext context,
                                                                                                CancellationToken cancellationToken)
   {
      HealthReport report = await health.CheckHealthAsync(cancellationToken);
      HealthResponse response = new(report.Status, report.Entries.Select(e => new HealthCheckDto(e.Key, e.Value.Status)).ToList());
      context.Response.Headers.CacheControl = "no-store";
      return report.Status == HealthStatus.Unhealthy
         ? TypedResults.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable)
         : TypedResults.Ok(response);
   }
}
