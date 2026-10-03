using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace GalaxyData.Web.Features.Audit;

/// <summary>Something an administrator did; <see cref="Details"/> is JSON.</summary>
public sealed record AdminEventDto(long Id, DateTime At, string Actor, string Action, string Target, string? Details);

/// <summary>The audit, for administrators: what administrators did, newest first.</summary>
public static class AuditEndpoints
{
   public static RouteGroupBuilder MapAudit(this RouteGroupBuilder api)
   {
      RouteGroupBuilder audit = api.MapGroup("/audit")
         .WithTags("Audit")
         .RequireAuthorization(Policies.CanAdmin)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      audit.MapGet("/admin-events", ListAdminEventsAsync)
         .WithName("ListAdminEvents")
         .WithSummary("What administrators did, newest first; the next page is before the last id given");
      return api;
   }

   private static async Task<Ok<List<AdminEventDto>>> ListAdminEventsAsync(MetadataDb db, long? before, [Range(1, 500)] int? take, CancellationToken cancellationToken)
   {
      IQueryable<AdminAuditEvent> events = db.AdminAuditEvents.AsNoTracking();
      if (before is { } id) { events = events.Where(e => e.Id < id); }
      List<AdminEventDto> page = await events.OrderByDescending(e => e.Id).Take(take ?? 50)
         .Select(e => new AdminEventDto(e.Id, e.At, e.ActorName, e.Action, e.Target, e.Details))
         .ToListAsync(cancellationToken);
      return TypedResults.Ok(page);
   }
}
