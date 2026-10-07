using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GalaxyData.Web.Features.Dashboards;

/// <summary>A widget of a definition sent as it is (the editor's, unsaved; the owner's working copy): a slice of it holding what the widget needs is enough.</summary>
public sealed record InlineWidgetRequest([Required] DashboardDefinition Slice, [Required] string Widget, DashboardState? State = null, WidgetPage? Page = null, bool Refresh = false);

public sealed record InlineFilterRequest([Required] DashboardDefinition Slice, [Required] string Filter, DashboardState? State = null, [StringLength(200)] string? Search = null);

/// <summary>A widget of the published copy, which must still be the one read (<see cref="Hash"/>).</summary>
public sealed record PublishedWidgetRequest([Required] string Hash, DashboardState? State = null, WidgetPage? Page = null, bool Refresh = false);

public sealed record PublishedFilterRequest([Required] string Hash, DashboardState? State = null, [StringLength(200)] string? Search = null);

/// <summary>
/// Widgets' rows, queries and filters' values: of a definition sent (the editor's), or of a dashboard's published
/// copy. They run queries, so they wait their turn as queries do (<see cref="RateLimits.Queries"/>).
/// </summary>
internal static class DashboardDataEndpoints
{
   /// <summary>A definition sent may be as long as one saved, and its state besides.</summary>
   private const long InlineBodyLimit = 2 * 1024 * 1024;

   private const long PublishedBodyLimit = 64 * 1024;

   public static void MapDashboardData(RouteGroupBuilder dashboards)
   {
      static RouteHandlerBuilder Runs(RouteHandlerBuilder endpoint, long limit) => endpoint
         .RequireRateLimiting(RateLimits.Queries)
         .WithMetadata(new RequestSizeLimitAttribute(limit))
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status404NotFound)
         .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
         .ProducesProblem(StatusCodes.Status502BadGateway)
         .ProducesProblem(StatusCodes.Status504GatewayTimeout);

      Runs(dashboards.MapPost("/data", InlineDataAsync).WithName("GetSliceData").WithSummary("A widget's rows, of a definition sent").Produces<WidgetDataDto>(), InlineBodyLimit);
      Runs(dashboards.MapPost("/query", InlineQueryAsync).WithName("GetSliceQuery").WithSummary("A widget's queries, of a definition sent").Produces<WidgetQueryDto>(),
         InlineBodyLimit);
      Runs(dashboards.MapPost("/filter-values", InlineValuesAsync).WithName("GetSliceFilterValues").WithSummary("A filter's values, of a definition sent")
         .Produces<FilterValuesDto>(), InlineBodyLimit);
      Runs(dashboards.MapPost("/{id:int}/widgets/{widget}/data", PublishedDataAsync).WithName("GetWidgetData").WithSummary("A widget's rows, of the published copy")
         .Produces<WidgetDataDto>().ProducesProblem(StatusCodes.Status409Conflict), PublishedBodyLimit);
      Runs(dashboards.MapPost("/{id:int}/widgets/{widget}/query", PublishedQueryAsync).WithName("GetWidgetQuery").WithSummary("A widget's queries, of the published copy")
         .Produces<WidgetQueryDto>().ProducesProblem(StatusCodes.Status409Conflict), PublishedBodyLimit);
      Runs(dashboards.MapPost("/{id:int}/filters/{filter}/values", PublishedValuesAsync).WithName("GetFilterValues").WithSummary("A filter's values, of the published copy")
         .Produces<FilterValuesDto>().ProducesProblem(StatusCodes.Status409Conflict), PublishedBodyLimit);
   }

   private static Task<IResult> InlineDataAsync(InlineWidgetRequest request, HttpResponse response, DefinitionRules rules, WidgetRunner runner, CancellationToken cancellationToken) =>
      Answer(() => runner.DataAsync(Slice(request.Slice, rules), request.Widget, request.State, request.Page, request.Refresh, RunMode.SignedIn, response, cancellationToken));

   private static Task<IResult> InlineQueryAsync(InlineWidgetRequest request, HttpResponse response, DefinitionRules rules, WidgetRunner runner, CancellationToken cancellationToken) =>
      Answer(() => runner.QueryAsync(Slice(request.Slice, rules), request.Widget, request.State, response, cancellationToken));

   private static Task<IResult> InlineValuesAsync(InlineFilterRequest request, HttpResponse response, DefinitionRules rules, WidgetRunner runner, CancellationToken cancellationToken) =>
      Answer(() => runner.FilterValuesAsync(Slice(request.Slice, rules), request.Filter, request.State, request.Search, RunMode.SignedIn, response, cancellationToken));

   private static Task<IResult> PublishedDataAsync(int id, string widget, PublishedWidgetRequest request, ClaimsPrincipal me, MetadataDb db, HttpResponse response,
      WidgetRunner runner, CancellationToken cancellationToken) =>
      Answer(async () => await runner.DataAsync(await PublishedAsync(db, me, id, request.Hash, cancellationToken), widget, request.State, request.Page, request.Refresh,
         RunMode.SignedIn, response, cancellationToken));

   private static Task<IResult> PublishedQueryAsync(int id, string widget, PublishedWidgetRequest request, ClaimsPrincipal me, MetadataDb db, HttpResponse response,
      WidgetRunner runner, CancellationToken cancellationToken) =>
      Answer(async () => await runner.QueryAsync(await PublishedAsync(db, me, id, request.Hash, cancellationToken), widget, request.State, response, cancellationToken));

   private static Task<IResult> PublishedValuesAsync(int id, string filter, PublishedFilterRequest request, ClaimsPrincipal me, MetadataDb db, HttpResponse response,
      WidgetRunner runner, CancellationToken cancellationToken) =>
      Answer(async () => await runner.FilterValuesAsync(await PublishedAsync(db, me, id, request.Hash, cancellationToken), filter, request.State, request.Search,
         RunMode.SignedIn, response, cancellationToken));

   /// <summary>The definition sent, checked as one saved would be (its problems by field under <c>slice.</c>).</summary>
   private static DashboardDefinition Slice(DashboardDefinition slice, DefinitionRules rules)
   {
      Dictionary<string, string[]> errors = [];
      DashboardDefinition? checkedSlice = rules.Check(slice, "slice.", errors);
      return checkedSlice ?? throw new ProblemResultException(ApiProblems.Invalid(errors));
   }

   /// <summary>The published copy of a dashboard the user sees; 409 <c>dashboard-changed</c> when it isn't the one read.</summary>
   private static async Task<DashboardDefinition> PublishedAsync(MetadataDb db, ClaimsPrincipal me, int id, string hash, CancellationToken cancellationToken)
   {
      Dashboard? row = await DashboardEndpoints.FindAsync(db, me, id, cancellationToken);
      if (row?.PublishedJson == null)
      {
         throw new ProblemResultException(ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such dashboard",
            $"There is no published dashboard {id} you can see"));
      }
      if (!string.Equals(row.PublishedHash, hash, StringComparison.Ordinal))
      {
         throw new ProblemResultException(ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.DashboardChanged, $"{row.Name} was published again",
            "Read the dashboard again"));
      }
      return DefinitionJson.Read(row.PublishedJson);
   }

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
