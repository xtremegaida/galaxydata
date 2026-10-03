using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GalaxyData.Web.Features.Query;

/// <summary>
/// Queries, for anyone who reads data: checked as they are written, explained, run a page at a time as a grid shows
/// them, and the rows their values lead to. POST, for their bodies. Each runs within <c>Query:Timeout</c>.
/// </summary>
public static class QueryEndpoints
{
   public static RouteGroupBuilder MapQuery(this RouteGroupBuilder api)
   {
      RouteGroupBuilder query = api.MapGroup("/query")
         .WithTags("Query")
         .RequireAuthorization(Policies.CanRead)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      query.MapPost("/validate", ValidateAsync).WithName("ValidateQuery")
         .WithSummary("What is wrong with a query, placed in its text; the parameters it uses (those without values taken as null), and its columns")
         .Produces<QueryValidationDto>()
         .ProducesValidationProblem();
      query.MapPost("/explain", ExplainAsync).WithName("ExplainQuery")
         .WithSummary("How a query would run, without running it; with a grid, the page the grid would fetch")
         .Produces<QueryExplainDto>()
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
      query.MapPost("/execute", ExecuteAsync).WithName("ExecuteQuery")
         .WithSummary("A page of a query's rows, as a grid shows them, with their columns and count when asked for")
         .Produces<QueryPageDto>()
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
         .ProducesProblem(StatusCodes.Status502BadGateway)
         .ProducesProblem(StatusCodes.Status504GatewayTimeout);
      query.MapPost("/link", LinkAsync).WithName("FollowQueryLink")
         .WithSummary("Where a value of a row leads: the query of those rows, and what a grid can browse for them")
         .Produces<QueryLinkDto>()
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
      return api;
   }

   private static Task<IResult> ValidateAsync(QueryTextRequest request, HttpResponse response, QueryService queries, CancellationToken cancellationToken) =>
      Answer(() => queries.ValidateAsync(request, response, cancellationToken));

   private static Task<IResult> ExplainAsync(QueryExplainRequest request, ClaimsPrincipal me, HttpResponse response, QueryService queries,
                                             CancellationToken cancellationToken) =>
      Answer(() => queries.ExplainAsync(request, CanEditData(me), response, cancellationToken));

   private static Task<IResult> ExecuteAsync(QueryPageRequest request, ClaimsPrincipal me, HttpResponse response, QueryService queries,
                                             CancellationToken cancellationToken) =>
      Answer(() => queries.PageAsync(request, CanEditData(me), response, cancellationToken));

   private static Task<IResult> LinkAsync(QueryLinkRequest request, HttpResponse response, QueryService queries, CancellationToken cancellationToken) =>
      Answer(() => queries.LinkAsync(request, response, cancellationToken));

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

   private static bool CanEditData(ClaimsPrincipal user) => user.IsInRole(nameof(UserRole.DataManager)) || user.IsInRole(nameof(UserRole.Admin));
}
