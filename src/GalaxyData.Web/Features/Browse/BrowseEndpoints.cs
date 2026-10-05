using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GalaxyData.Web.Features.Browse;

/// <summary>
/// Browsing, for anyone who reads data: pages of an entity's rows, or of those a navigation leads to from a row,
/// as a grid shows them (filtered, sorted, paged), the crumbs of a trail of such steps, and where a row is among its
/// entity's rows. POST, for their bodies.
/// </summary>
public static class BrowseEndpoints
{
   public static RouteGroupBuilder MapBrowse(this RouteGroupBuilder api)
   {
      RouteGroupBuilder browse = api.MapGroup("/browse")
         .WithTags("Browse")
         .RequireAuthorization(Policies.CanRead)
         .RequireRateLimiting(RateLimits.Queries)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      browse.MapPost("/page", PageAsync).WithName("BrowsePage")
         .WithSummary("A page of rows to browse, with their columns and count when asked for")
         .Produces<BrowsePageDto>()
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status404NotFound)
         .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
         .ProducesProblem(StatusCodes.Status502BadGateway)
         .ProducesProblem(StatusCodes.Status504GatewayTimeout);
      browse.MapPost("/trail", TrailAsync).WithName("BrowseTrail")
         .WithSummary("The crumbs of a trail as they stand: the entities they reach, and the rows chosen in them")
         .Produces<BrowseTrailDto>()
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
         .ProducesProblem(StatusCodes.Status502BadGateway)
         .ProducesProblem(StatusCodes.Status504GatewayTimeout);
      browse.MapPost("/position", PositionAsync).WithName("BrowsePosition")
         .WithSummary("Where a row is among its entity's rows: its key, and how many rows come before it in the key's order")
         .Produces<BrowsePositionDto>()
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status404NotFound)
         .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
         .ProducesProblem(StatusCodes.Status502BadGateway)
         .ProducesProblem(StatusCodes.Status504GatewayTimeout);
      return api;
   }

   private static async Task<IResult> PageAsync(BrowsePageRequest request, ClaimsPrincipal me, HttpResponse response, BrowseService browse,
                                                CancellationToken cancellationToken)
   {
      try
      {
         return TypedResults.Ok(await browse.PageAsync(request, CanEditData(me), response, cancellationToken));
      }
      catch (ProblemResultException problem)
      {
         return problem.Result;
      }
   }

   private static async Task<IResult> TrailAsync(BrowseTrailRequest request, HttpResponse response, BrowseService browse, CancellationToken cancellationToken)
   {
      try
      {
         return TypedResults.Ok(await browse.TrailAsync(request, response, cancellationToken));
      }
      catch (ProblemResultException problem)
      {
         return problem.Result;
      }
   }

   private static async Task<IResult> PositionAsync(BrowsePositionRequest request, HttpResponse response, BrowseService browse, CancellationToken cancellationToken)
   {
      try
      {
         return TypedResults.Ok(await browse.PositionAsync(request, response, cancellationToken));
      }
      catch (ProblemResultException problem)
      {
         return problem.Result;
      }
   }

   private static bool CanEditData(ClaimsPrincipal user) => user.IsInRole(nameof(UserRole.DataManager)) || user.IsInRole(nameof(UserRole.Admin));
}
