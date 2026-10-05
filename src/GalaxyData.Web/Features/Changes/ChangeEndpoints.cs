using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Changes;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GalaxyData.Web.Features.Changes;

/// <summary>
/// The user's pending changes to rows, for those who change data: kept on the server until committed or reverted,
/// changed by batches of operations, previewed as the statements each connection would run, and committed.
/// </summary>
public static class ChangeEndpoints
{
   public static RouteGroupBuilder MapChanges(this RouteGroupBuilder api)
   {
      RouteGroupBuilder changes = api.MapGroup("/changes")
         .WithTags("Changes")
         .RequireAuthorization(Policies.CanEditData)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      changes.MapGet("/", GetAsync).WithName("GetChanges").WithSummary("The user's pending changes, in the order they were first made");
      changes.MapPost("/ops", ApplyAsync).WithName("ApplyChangeOps")
         .WithSummary("Applies operations to the pending changes: all of them, or none")
         .Produces<ChangeSetDto>()
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status409Conflict);
      changes.MapDelete("/", ClearAsync).WithName("ClearChanges")
         .WithSummary("Drops the pending changes, or those of a source or an entity")
         .Produces<ChangeSetDto>()
         .ProducesProblem(StatusCodes.Status409Conflict);
      changes.MapPost("/preview", PreviewAsync).WithName("PreviewChanges")
         .WithSummary("The statements committing the changes would run on each connection, and why changes can't be made; a plan to commit when none")
         .Produces<ChangePreviewDto>()
         .ProducesProblem(StatusCodes.Status409Conflict);
      changes.MapPost("/commit", CommitAsync).WithName("CommitChanges")
         .RequireRateLimiting(RateLimits.Queries)
         .WithSummary("Commits a preview's plan, with scripts as edited; what came of it on each connection")
         .Produces<CommitResultDto>()
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status409Conflict)
         .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
      return api;
   }

   private static async Task<ChangeSetDto> GetAsync(ClaimsPrincipal me, ChangeService changes, CancellationToken cancellationToken) =>
      await changes.GetAsync(me.RequiredUserId(), cancellationToken);

   private static Task<IResult> ApplyAsync(ChangeOpsRequest request, ClaimsPrincipal me, HttpResponse response, ChangeService changes,
                                           CancellationToken cancellationToken) =>
      Answer(() => changes.ApplyAsync(me.RequiredUserId(), request, response, cancellationToken));

   private static Task<IResult> ClearAsync(string? source, string? entity, int? version, ClaimsPrincipal me, ChangeService changes,
                                           CancellationToken cancellationToken) =>
      Answer(() => changes.ClearAsync(me.RequiredUserId(), source, entity, version, cancellationToken));

   private static Task<IResult> PreviewAsync(ClaimsPrincipal me, HttpResponse response, ChangeService changes, CancellationToken cancellationToken) =>
      Answer(() => changes.PreviewAsync(me.RequiredUserId(), me.IsInRole(nameof(UserRole.Admin)), response, cancellationToken));

   private static Task<IResult> CommitAsync(CommitChangesRequest request, ClaimsPrincipal me, HttpResponse response, ChangeService changes,
                                            CancellationToken cancellationToken) =>
      Answer(() => changes.CommitAsync(me, request, response, cancellationToken));

   private static async Task<IResult> Answer<T>(Func<Task<T>> answer)
   {
      try
      {
         return TypedResults.Ok(await answer());
      }
      catch (ProblemResultException problem)
      {
         return problem.Result;
      }
   }
}
