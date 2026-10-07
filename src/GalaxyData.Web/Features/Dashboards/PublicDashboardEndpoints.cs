using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Execution;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Palettes;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;

namespace GalaxyData.Web.Features.Dashboards;

/// <summary>
/// Public dashboards, for anyone with the link: read only, by GET (so there is no anti-forgery to skip), reading no
/// user even when one's cookies come along. Outside the API's group, so neither its anti-forgery check nor the
/// catalog's version applies. Failures are told by their codes alone; there is never a 401 or 403 (an unknown, revoked
/// or unpublished dashboard is a 404, whatever the reason). The page they are embedded with is <c>/embed/{token}</c>.
/// </summary>
public static class PublicDashboardEndpoints
{
   /// <summary>The longest state a request may give (<c>s</c>: base64url JSON).</summary>
   public const int MaxStateLength = 4096;

   public const int MaxSearchLength = 200;

   public static WebApplication MapPublicDashboards(this WebApplication app)
   {
      ArgumentNullException.ThrowIfNull(app);
      RouteGroupBuilder group = app.MapGroup("/api/public/dashboards")
         .WithTags("Public dashboards")
         .AllowAnonymous()
         .ProducesProblem(StatusCodes.Status404NotFound)
         .ProducesProblem(StatusCodes.Status429TooManyRequests)
         .ProducesProblem(StatusCodes.Status500InternalServerError);
      group.MapGet("/{token}", GetAsync).WithName("GetPublicDashboard").WithSummary("A public dashboard: what it shows, without its queries")
         .Produces<PublicDashboardDto>().Produces(StatusCodes.Status304NotModified);
      group.MapGet("/{token}/widgets/{widget}/data", DataAsync).WithName("GetPublicWidgetData").WithSummary("A public dashboard's widget's rows, for the state given (s)")
         .Produces<WidgetDataDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status422UnprocessableEntity)
         .ProducesProblem(StatusCodes.Status502BadGateway).ProducesProblem(StatusCodes.Status504GatewayTimeout);
      group.MapGet("/{token}/filters/{filter}/values", ValuesAsync).WithName("GetPublicFilterValues").WithSummary("The values a public dashboard's filter may take")
         .Produces<FilterValuesDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status422UnprocessableEntity)
         .ProducesProblem(StatusCodes.Status502BadGateway).ProducesProblem(StatusCodes.Status504GatewayTimeout);
      app.MapGet("/embed/{token}", EmbedAsync).AllowAnonymous().ExcludeFromDescription();
      return app;
   }

   private static async Task<IResult> GetAsync(string token, HttpContext context, MetadataDb db, PublicDashboards dashboards, PaletteStore palettes,
      CancellationToken cancellationToken)
   {
      if (await dashboards.FindAsync(db, token, cancellationToken) is not { } dashboard) { return NotFound(); }
      IReadOnlyDictionary<int, StoredPalette> named = await palettes.FindAsync(db, PaletteRefs.Of(dashboard.Definition), cancellationToken);
      // Viewers may keep it, asking each time whether it is still the one published (with its palettes as they were).
      string tag = $"\"{PublicDashboards.Tag(dashboard, named)}\"";
      context.Response.Headers.CacheControl = "private, no-cache";
      context.Response.Headers.ETag = tag;
      if (context.Request.Headers.IfNoneMatch.ToString().Split(',', StringSplitOptions.TrimEntries).AsSpan().Contains(tag))
      {
         return TypedResults.StatusCode(StatusCodes.Status304NotModified);
      }
      return TypedResults.Ok(dashboards.View(dashboard, named));
   }

   private static Task<IResult> DataAsync(string token, string widget, string? s, long? offset, int? limit, bool? count, MetadataDb db, PublicDashboards dashboards,
      WidgetRunner runner, PaletteStore palettes, CancellationToken cancellationToken) =>
      AnswerAsync(async () =>
      {
         PublicDashboard dashboard = await FoundAsync(db, dashboards, token, cancellationToken);
         WidgetPage? page = offset == null && limit == null && count == null ? null : new WidgetPage(offset ?? 0, limit, count ?? false);
         WidgetDataDto data = await runner.DataAsync(dashboard.Definition, widget, State(s), page, refresh: false, RunMode.Public, response: null, cancellationToken,
            ct => dashboards.GateAsync(dashboard.Id, ct));
         // After the rows kept (by their query alone), so a palette's edit shows at the next read.
         IReadOnlyDictionary<int, StoredPalette> named = await palettes.FindAsync(db, PaletteRefs.Of(dashboard.Definition), cancellationToken);
         return PublicDashboards.Colored(dashboard, widget, data, named);
      });

   private static Task<IResult> ValuesAsync(string token, string filter, string? s, string? text, MetadataDb db, PublicDashboards dashboards, WidgetRunner runner,
      CancellationToken cancellationToken) =>
      AnswerAsync(async () =>
      {
         PublicDashboard dashboard = await FoundAsync(db, dashboards, token, cancellationToken);
         if (text is { Length: > MaxSearchLength }) { throw Invalid("text", $"Look for {MaxSearchLength} characters at most"); }
         // Only the filters viewers see are theirs to ask about.
         if (!dashboard.Definition.Filters.Exists(f => f.Id == filter && f.Visible && f.Editable)) { throw new ProblemResultException(NotFound()); }
         return await runner.FilterValuesAsync(dashboard.Definition, filter, State(s), text, RunMode.Public, response: null, cancellationToken,
            ct => dashboards.GateAsync(dashboard.Id, ct));
      });

   /// <summary>The client's page, framed as the dashboard (or the application) allows; an unknown token's too, so the page can say so.</summary>
   private static async Task<IResult> EmbedAsync(string token, HttpContext context, MetadataDb db, PublicDashboards dashboards, IWebHostEnvironment environment,
      CancellationToken cancellationToken)
   {
      IFileInfo page = environment.WebRootFileProvider.GetFileInfo("index.html");
      if (!page.Exists) { return TypedResults.NotFound(); }
      PublicDashboard? dashboard = await dashboards.FindAsync(db, token, cancellationToken);
      context.Features.Set(new EmbedFraming(dashboards.FrameAncestors(dashboard)));
      context.Response.Headers.CacheControl = "no-cache";
      return TypedResults.Stream(page.CreateReadStream(), "text/html; charset=utf-8");
   }

   private static async Task<PublicDashboard> FoundAsync(MetadataDb db, PublicDashboards dashboards, string token, CancellationToken cancellationToken) =>
      await dashboards.FindAsync(db, token, cancellationToken) ?? throw new ProblemResultException(NotFound());

   /// <summary>The state as given: base64url JSON of filters and selections (by id).</summary>
   private static DashboardState? State(string? s)
   {
      if (string.IsNullOrEmpty(s)) { return null; }
      if (s.Length > MaxStateLength) { throw Invalid("s", $"The state is {MaxStateLength:N0} characters at most"); }
      try
      {
         string base64 = s.Replace('-', '+').Replace('_', '/');
         base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
         return JsonSerializer.Deserialize<DashboardState>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)), DefinitionJson.Options);
      }
      catch (Exception e) when (e is FormatException or JsonException or ArgumentException or NotSupportedException)
      {
         throw Invalid("s", "The state is base64url JSON of filters and selections");
      }
   }

   private static ProblemResultException Invalid(string field, string message) =>
      new(ApiProblems.Invalid(new Dictionary<string, string[]> { [field] = [message] }));

   private static IResult NotFound() =>
      ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such dashboard", "It may no longer be public");

   /// <summary>The answer, or its problem told by its code alone; the engine's failures as the widget's (422), the source's (502) or time's (504).</summary>
   private static async Task<IResult> AnswerAsync<T>(Func<Task<T>> answer)
   {
      try
      {
         return TypedResults.Ok(await answer());
      }
      catch (ProblemResultException problem)
      {
         return problem.Result;
      }
      catch (ApiException e)
      {
         return Plain(e.Status, e.Code);
      }
      catch (QueryTimeoutException)
      {
         return Plain(StatusCodes.Status504GatewayTimeout, ProblemCodes.QueryTimeout);
      }
      catch (SourceUnavailableException)
      {
         return Plain(StatusCodes.Status502BadGateway, ProblemCodes.SourceUnavailable);
      }
      catch (Exception e) when (e is QueryException or QueryExecutionException)
      {
         return Plain(StatusCodes.Status422UnprocessableEntity, ProblemCodes.WidgetInvalid);
      }
   }

   private static ProblemHttpResult Plain(int status, string code) => TypedResults.Problem(ApiExceptionHandler.Plain(ApiProblems.Create(status, code, string.Empty)));
}
