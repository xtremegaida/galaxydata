using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Changes;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace GalaxyData.Web.Features.Audit;

/// <summary>Something an administrator did; <see cref="Details"/> is JSON.</summary>
public sealed record AdminEventDto(long Id, DateTime At, string Actor, string Action, string Target, string? Details);

/// <summary>The audit, for administrators: what administrators did, and the commits of changes, newest first.</summary>
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
      audit.MapGet("/commits", ListCommitsAsync)
         .WithName("ListCommits")
         .WithSummary("Commits of changes, newest first; the next page is before the last id given");
      audit.MapGet("/commits/{id:long}", GetCommitAsync)
         .WithName("GetCommit")
         .WithSummary("A commit of changes, with the scripts it ran")
         .ProducesProblem(StatusCodes.Status404NotFound);
      return api;
   }

   private static async Task<Ok<List<CommitAuditSummaryDto>>> ListCommitsAsync(MetadataDb db, long? before, [Range(1, 500)] int? take,
                                                                                CancellationToken cancellationToken)
   {
      IQueryable<CommitAudit> commits = db.CommitAudits.AsNoTracking();
      if (before is { } id) { commits = commits.Where(c => c.Id < id); }
      List<CommitAuditSummaryDto> page = await commits.OrderByDescending(c => c.Id).Take(take ?? 50)
         .Select(c => new CommitAuditSummaryDto(c.Id, c.StartedAt, c.FinishedAt, c.UserName, c.Status, c.ChangeCount,
            c.Scripts.OrderBy(s => s.Ordinal).Select(s => s.Source).ToList(), c.IsEdited, c.AnyStatement, c.Failure))
         .ToListAsync(cancellationToken);
      return TypedResults.Ok(page);
   }

   private static async Task<Results<Ok<CommitAuditDto>, ProblemHttpResult>> GetCommitAsync(long id, MetadataDb db, CancellationToken cancellationToken)
   {
      CommitAudit? commit = await db.CommitAudits.AsNoTracking().Include(c => c.Scripts).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
      if (commit == null)
      {
         return ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such commit", $"There is no commit {id} in the audit");
      }
      return TypedResults.Ok(new CommitAuditDto(commit.Id, commit.StartedAt, commit.FinishedAt, commit.UserName, commit.Status, commit.ChangeCount, commit.IsEdited,
         commit.AnyStatement, commit.CatalogVersion, commit.FailureKind, commit.Failure,
         [.. commit.Scripts.OrderBy(s => s.Ordinal).Select(s => new CommitAuditScriptDto(s.Source, s.Kind, s.Dialect, s.IsEdited, s.Status, s.Statements,
            s.RowsChanged, s.Error, s.Text))]));
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
