using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Overlay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace GalaxyData.Web.Features.Overlay;

/// <summary>
/// The overlay, for administrators: relations the databases don't declare (across sources too), navigations
/// renamed or hidden, virtual entities and entities' settings. Each item is kept whatever the catalog finds wrong
/// with it, and comes with its issues, as the catalog finds them now: after a schema changes, an item that names a
/// column that is gone says so. Items can be tried before they are saved (<c>validate</c>).
/// </summary>
public static class OverlayEndpoints
{
   public static RouteGroupBuilder MapOverlay(this RouteGroupBuilder api)
   {
      RouteGroupBuilder overlay = api.MapGroup("/overlay")
         .WithTags("Overlay")
         .RequireAuthorization(Policies.CanAdmin)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      overlay.MapGet("/", GetAsync).WithName("GetOverlay").WithSummary("Every item of the overlay, with what the catalog finds wrong with it");
      overlay.MapGet("/export", ExportAsync).WithName("ExportOverlay")
         .WithSummary("The overlay as a JSON file, as gdq reads it (--overlay)")
         .Produces(StatusCodes.Status200OK, typeof(CatalogOverlay), "application/json");

      RouteGroupBuilder relations = overlay.MapGroup("/relations");
      relations.MapGet("/{id:int}", (int id, OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
            editor.GetAsync(RelationKind.Instance, id, response, cancellationToken))
         .WithName("GetRelation").ProducesProblem(StatusCodes.Status404NotFound);
      relations.MapPost("/", CreateRelationAsync).WithName("CreateRelation").ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
      relations.MapPut("/{id:int}", UpdateRelationAsync).WithName("UpdateRelation")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      relations.MapDelete("/{id:int}", (int id, int? version, ClaimsPrincipal me, OverlayEditor editor, CancellationToken cancellationToken) =>
            editor.DeleteAsync(RelationKind.Instance, id, version, me, cancellationToken))
         .WithName("DeleteRelation").ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      relations.MapPost("/validate", ValidateRelationAsync).WithName("ValidateRelation").RequireRateLimiting(RateLimits.Queries)
         .WithSummary("Tries a relation, in place of the relation id when given, without saving it")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);

      RouteGroupBuilder navigations = overlay.MapGroup("/navigations");
      navigations.MapGet("/{id:int}", (int id, OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
            editor.GetAsync(NavigationOverrideKind.Instance, id, response, cancellationToken))
         .WithName("GetNavigationOverride").ProducesProblem(StatusCodes.Status404NotFound);
      navigations.MapPost("/", CreateNavigationAsync).WithName("CreateNavigationOverride").ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
      navigations.MapPut("/{id:int}", UpdateNavigationAsync).WithName("UpdateNavigationOverride")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      navigations.MapDelete("/{id:int}", (int id, int? version, ClaimsPrincipal me, OverlayEditor editor, CancellationToken cancellationToken) =>
            editor.DeleteAsync(NavigationOverrideKind.Instance, id, version, me, cancellationToken))
         .WithName("DeleteNavigationOverride").ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      navigations.MapPost("/validate", ValidateNavigationAsync).WithName("ValidateNavigationOverride").RequireRateLimiting(RateLimits.Queries)
         .WithSummary("Tries a navigation override, in place of the override id when given, without saving it")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);

      RouteGroupBuilder virtualEntities = overlay.MapGroup("/virtual-entities");
      virtualEntities.MapGet("/{id:int}", (int id, OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
            editor.GetAsync(VirtualEntityKind.Instance, id, response, cancellationToken))
         .WithName("GetVirtualEntity").ProducesProblem(StatusCodes.Status404NotFound);
      virtualEntities.MapPost("/", CreateVirtualEntityAsync).WithName("CreateVirtualEntity").ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
      virtualEntities.MapPut("/{id:int}", UpdateVirtualEntityAsync).WithName("UpdateVirtualEntity")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      virtualEntities.MapDelete("/{id:int}", (int id, int? version, ClaimsPrincipal me, OverlayEditor editor, CancellationToken cancellationToken) =>
            editor.DeleteAsync(VirtualEntityKind.Instance, id, version, me, cancellationToken))
         .WithName("DeleteVirtualEntity").ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      virtualEntities.MapPost("/validate", ValidateVirtualEntityAsync).WithName("ValidateVirtualEntity").RequireRateLimiting(RateLimits.Queries)
         .WithSummary("Tries a virtual entity, in place of the virtual entity id when given, without saving it: its issues, the entity it makes, and its query's diagnostics")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);

      RouteGroupBuilder settings = overlay.MapGroup("/entity-settings");
      settings.MapGet("/{id:int}", (int id, OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
            editor.GetAsync(EntitySettingsKind.Instance, id, response, cancellationToken))
         .WithName("GetEntitySettings").ProducesProblem(StatusCodes.Status404NotFound);
      settings.MapPost("/", CreateEntitySettingsAsync).WithName("CreateEntitySettings").ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
      settings.MapPut("/{id:int}", UpdateEntitySettingsAsync).WithName("UpdateEntitySettings")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      settings.MapDelete("/{id:int}", (int id, int? version, ClaimsPrincipal me, OverlayEditor editor, CancellationToken cancellationToken) =>
            editor.DeleteAsync(EntitySettingsKind.Instance, id, version, me, cancellationToken))
         .WithName("DeleteEntitySettings").ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      settings.MapPost("/validate", ValidateEntitySettingsAsync).WithName("ValidateEntitySettings").RequireRateLimiting(RateLimits.Queries)
         .WithSummary("Tries an entity's settings, in place of the settings id when given, without saving them: their issues, and the entity as they make it")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
      return api;
   }

   private static async Task<Ok<OverlayDto>> GetAsync(OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      TypedResults.Ok(await editor.AllAsync(response, cancellationToken));

   private static async Task<ContentHttpResult> ExportAsync(CatalogService catalogs, HttpResponse response, CancellationToken cancellationToken)
   {
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      return TypedResults.Text(state.Overlay.Overlay.ToJson(), "application/json");
   }

   private static Task<Results<Created<RelationDto>, ValidationProblem, ProblemHttpResult>> CreateRelationAsync(RelationInput relation, ClaimsPrincipal me,
      OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      editor.CreateAsync(RelationKind.Instance, relation, me, response, cancellationToken);

   private static Task<Results<Ok<RelationDto>, ValidationProblem, ProblemHttpResult>> UpdateRelationAsync(int id, UpdateRelationRequest request, ClaimsPrincipal me,
      OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      editor.UpdateAsync(RelationKind.Instance, id, request.Relation, request.Version, me, response, cancellationToken);

   private static Task<Results<Ok<OverlayCheckDto>, ValidationProblem, ProblemHttpResult>> ValidateRelationAsync(RelationInput relation, int? id, OverlayEditor editor,
      HttpResponse response, CancellationToken cancellationToken) =>
      editor.CheckAsync(RelationKind.Instance, relation, id, response, cancellationToken);

   private static Task<Results<Created<NavigationOverrideDto>, ValidationProblem, ProblemHttpResult>> CreateNavigationAsync(NavigationOverrideInput navigation,
      ClaimsPrincipal me, OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      editor.CreateAsync(NavigationOverrideKind.Instance, navigation, me, response, cancellationToken);

   private static Task<Results<Ok<NavigationOverrideDto>, ValidationProblem, ProblemHttpResult>> UpdateNavigationAsync(int id, UpdateNavigationOverrideRequest request,
      ClaimsPrincipal me, OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      editor.UpdateAsync(NavigationOverrideKind.Instance, id, request.Navigation, request.Version, me, response, cancellationToken);

   private static Task<Results<Ok<OverlayCheckDto>, ValidationProblem, ProblemHttpResult>> ValidateNavigationAsync(NavigationOverrideInput navigation, int? id,
      OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      editor.CheckAsync(NavigationOverrideKind.Instance, navigation, id, response, cancellationToken);

   private static Task<Results<Created<VirtualEntityDto>, ValidationProblem, ProblemHttpResult>> CreateVirtualEntityAsync(VirtualEntityInput virtualEntity,
      ClaimsPrincipal me, OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      editor.CreateAsync(VirtualEntityKind.Instance, virtualEntity, me, response, cancellationToken);

   private static Task<Results<Ok<VirtualEntityDto>, ValidationProblem, ProblemHttpResult>> UpdateVirtualEntityAsync(int id, UpdateVirtualEntityRequest request,
      ClaimsPrincipal me, OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      editor.UpdateAsync(VirtualEntityKind.Instance, id, request.VirtualEntity, request.Version, me, response, cancellationToken);

   private static Task<Results<Ok<OverlayCheckDto>, ValidationProblem, ProblemHttpResult>> ValidateVirtualEntityAsync(VirtualEntityInput virtualEntity, int? id,
      OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      editor.CheckAsync(VirtualEntityKind.Instance, virtualEntity, id, response, cancellationToken);

   private static Task<Results<Created<EntitySettingsDto>, ValidationProblem, ProblemHttpResult>> CreateEntitySettingsAsync(EntitySettingsInput settings,
      ClaimsPrincipal me, OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      editor.CreateAsync(EntitySettingsKind.Instance, settings, me, response, cancellationToken);

   private static Task<Results<Ok<EntitySettingsDto>, ValidationProblem, ProblemHttpResult>> UpdateEntitySettingsAsync(int id, UpdateEntitySettingsRequest request,
      ClaimsPrincipal me, OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      editor.UpdateAsync(EntitySettingsKind.Instance, id, request.Settings, request.Version, me, response, cancellationToken);

   private static Task<Results<Ok<OverlayCheckDto>, ValidationProblem, ProblemHttpResult>> ValidateEntitySettingsAsync(EntitySettingsInput settings, int? id,
      OverlayEditor editor, HttpResponse response, CancellationToken cancellationToken) =>
      editor.CheckAsync(EntitySettingsKind.Instance, settings, id, response, cancellationToken);
}
