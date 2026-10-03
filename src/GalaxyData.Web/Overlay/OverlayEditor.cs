using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Features.Audit;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GalaxyData.Web.Overlay;

/// <summary>
/// Reads, saves, deletes and tries overlay items, alike for each kind. A saved item is kept whatever the catalog
/// finds wrong with it, and comes back with its issues: a source may not be read yet, and an item that stops
/// working when a schema changes is kept too. Every change is in the admin audit, and builds the catalog again.
/// </summary>
public sealed class OverlayEditor(MetadataDb db, CatalogService catalogs, SourceProviders providers, QueryEngines engines, TimeProvider clock)
{
   public async Task<Results<Ok<TDto>, ProblemHttpResult>> GetAsync<TRow, TInput, TDto>(OverlayKind<TRow, TInput, TDto> kind, int id, HttpResponse response,
                                                                                        CancellationToken cancellationToken)
      where TRow : class, IOverlayItem, new()
   {
      ArgumentNullException.ThrowIfNull(kind);
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      TRow? row = kind.Rows(state.Overlay).FirstOrDefault(r => r.Id == id);
      return row == null ? NoSuchItem(kind, id) : TypedResults.Ok(Dto(kind, state, row));
   }

   public async Task<Results<Created<TDto>, ValidationProblem, ProblemHttpResult>> CreateAsync<TRow, TInput, TDto>(OverlayKind<TRow, TInput, TDto> kind, TInput input,
      ClaimsPrincipal me, HttpResponse response, CancellationToken cancellationToken)
      where TRow : class, IOverlayItem, new()
   {
      ArgumentNullException.ThrowIfNull(kind);
      Dictionary<string, string[]> errors = [];
      TRow row = new();
      kind.Fill(input, row, errors);
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      if (await kind.TakenAsync(db, row, cancellationToken) is { } taken) { return Exists(kind, taken); }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      row.CreatedAt = now;
      row.UpdatedAt = now;
      kind.Set(db).Add(row);
      AdminAudit.Add(db, me, $"overlay.{kind.Slug}.created", Target(kind, row), kind.Fields(row), now);
      if (!await SavedAsync(cancellationToken)) { return Exists(kind, $"The {kind.Noun} {kind.Name(row)} is there already"); }
      catalogs.Invalidate();
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      return TypedResults.Created($"/api/overlay/{kind.Path}/{row.Id}", Dto(kind, state, row));
   }

   public async Task<Results<Ok<TDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync<TRow, TInput, TDto>(OverlayKind<TRow, TInput, TDto> kind, int id, TInput input,
      int version, ClaimsPrincipal me, HttpResponse response, CancellationToken cancellationToken)
      where TRow : class, IOverlayItem, new()
   {
      ArgumentNullException.ThrowIfNull(kind);
      TRow? row = await kind.Set(db).FindAsync([id], cancellationToken);
      if (row == null) { return NoSuchItem(kind, id); }
      if (row.Version != version) { return Changed(kind, row); }
      Dictionary<string, string[]> errors = [];
      TRow edited = new();
      kind.Fill(input, edited, errors);
      // Named in the request's body, where the item is a field of its own.
      if (errors.Count > 0) { return ApiProblems.Invalid(errors.ToDictionary(e => $"{kind.Field}.{e.Key}", e => e.Value)); }
      SetId(edited, id);
      if (await kind.TakenAsync(db, edited, cancellationToken) is { } taken) { return Exists(kind, taken); }

      Dictionary<string, object?> changes = Changes(kind.Fields(row), kind.Fields(edited));
      if (changes.Count > 0)
      {
         DateTime now = clock.GetUtcNow().UtcDateTime;
         string target = Target(kind, row);
         kind.Copy(edited, row);
         row.UpdatedAt = now;
         AdminAudit.Add(db, me, $"overlay.{kind.Slug}.updated", target, changes, now);
         if (!await SavedAsync(cancellationToken)) { return Exists(kind, $"The {kind.Noun} {kind.Name(row)} is there already"); }
         catalogs.Invalidate();
      }
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      return TypedResults.Ok(Dto(kind, state, row));
   }

   public async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync<TRow, TInput, TDto>(OverlayKind<TRow, TInput, TDto> kind, int id, int? version, ClaimsPrincipal me,
                                                                                            CancellationToken cancellationToken)
      where TRow : class, IOverlayItem, new()
   {
      ArgumentNullException.ThrowIfNull(kind);
      TRow? row = await kind.Set(db).FindAsync([id], cancellationToken);
      if (row == null) { return NoSuchItem(kind, id); }
      if (version is { } read && row.Version != read) { return Changed(kind, row); }
      kind.Set(db).Remove(row);
      AdminAudit.Add(db, me, $"overlay.{kind.Slug}.deleted", Target(kind, row), kind.Fields(row), clock.GetUtcNow().UtcDateTime);
      await db.SaveChangesAsync(cancellationToken);
      catalogs.Invalidate();
      return TypedResults.NoContent();
   }

   /// <summary>Tries an item against the catalog without saving it: in place of the item <paramref name="id"/>, or added when that is null.</summary>
   public async Task<Results<Ok<OverlayCheckDto>, ValidationProblem, ProblemHttpResult>> CheckAsync<TRow, TInput, TDto>(OverlayKind<TRow, TInput, TDto> kind,
      TInput input, int? id, HttpResponse response, CancellationToken cancellationToken)
      where TRow : class, IOverlayItem, new()
   {
      ArgumentNullException.ThrowIfNull(kind);
      Dictionary<string, string[]> errors = [];
      TRow row = new();
      kind.Fill(input, row, errors);
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      if (id is { } replaced)
      {
         if (kind.Rows(state.Overlay).All(r => r.Id != replaced)) { return NoSuchItem(kind, replaced); }
         SetId(row, replaced);
      }
      (CatalogOverlay overlay, OverlayItemRef item) = kind.With(state.Overlay, row, id);
      QueryCatalog trial = state.With(overlay);
      List<OverlayIssueDto> issues = [.. OverlayIssues.Of(trial, item, state.Unread).Select(OverlayIssueDto.From)];
      OverlayCheckDto check = kind.Check(row, issues, trial, item, new OverlayTrial(providers, engines.Trial));
      return TypedResults.Ok(check with { Breaks = Breaks(state, trial, item) });
   }

   /// <summary>The other items that work now and wouldn't with the item tried: a virtual entity renamed that a relation uses, a key changed.</summary>
   private static List<OverlayItemIssuesDto> Breaks(CatalogState state, QueryCatalog trial, OverlayItemRef tried)
   {
      // The trial's overlay is the state's with one item in place, or one more at the end: the others' indexes are the same.
      StoredOverlay stored = state.Overlay;
      List<OverlayItemIssuesDto> breaks = [];
      foreach (IGrouping<OverlayItemRef, CatalogDiagnostic> other in trial.Diagnostics.Where(d => d.Item is { } i && i != tried).GroupBy(d => d.Item!))
      {
         if (other.All(d => d.Severity != Query.Diagnostics.DiagnosticSeverity.Error)) { continue; }
         int id = stored.IdOf(other.Key);
         if (state.Issues.For(other.Key.Kind, id).Any(d => d.Severity == Query.Diagnostics.DiagnosticSeverity.Error)) { continue; }
         breaks.Add(new OverlayItemIssuesDto(other.Key.Kind, id, [.. OverlayIssues.Of(trial, other.Key, state.Unread).Select(OverlayIssueDto.From)]));
      }
      return breaks;
   }

   /// <summary>The whole overlay, with each item's issues.</summary>
   public async Task<OverlayDto> AllAsync(HttpResponse response, CancellationToken cancellationToken)
   {
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      return new OverlayDto(state.Version,
         [.. state.Overlay.Relations.Select(r => Dto(RelationKind.Instance, state, r))],
         [.. state.Overlay.Navigations.Select(n => Dto(NavigationOverrideKind.Instance, state, n))],
         [.. state.Overlay.VirtualEntities.Select(v => Dto(VirtualEntityKind.Instance, state, v))],
         [.. state.Overlay.EntitySettings.Select(s => Dto(EntitySettingsKind.Instance, state, s))],
         state.Issues.Errors, state.Issues.Warnings);
   }

   private static TDto Dto<TRow, TInput, TDto>(OverlayKind<TRow, TInput, TDto> kind, CatalogState state, TRow row)
      where TRow : class, IOverlayItem, new()
   {
      int index = kind.Rows(state.Overlay).Select(r => r.Id).ToList().IndexOf(row.Id);
      List<OverlayIssueDto> issues = [.. state.Issues.For(kind.Kind, row.Id).Select(OverlayIssueDto.From)];
      return kind.Dto(row, issues, state.Catalog, index < 0 ? null : new OverlayItemRef(kind.Kind, index));
   }

   /// <summary>SQLite's SQLITE_CONSTRAINT_UNIQUE.</summary>
   private const int UniqueConstraint = 2067;

   /// <summary>Saves; false when another item for the same thing was saved first (a unique index).</summary>
   private async Task<bool> SavedAsync(CancellationToken cancellationToken)
   {
      try
      {
         await db.SaveChangesAsync(cancellationToken);
         return true;
      }
      catch (DbUpdateException e) when (e is not DbUpdateConcurrencyException && e.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: UniqueConstraint })
      {
         return false;
      }
   }

   private static void SetId(IOverlayItem row, int id)
   {
      switch (row)
      {
         case RelationDefinition relation: relation.Id = id; break;
         case NavigationOverride navigation: navigation.Id = id; break;
         case VirtualEntityDefinition entity: entity.Id = id; break;
         case EntitySettings settings: settings.Id = id; break;
      }
   }

   private static Dictionary<string, object?> Changes(Dictionary<string, object?> before, Dictionary<string, object?> after)
   {
      Dictionary<string, object?> changes = [];
      foreach ((string field, object? to) in after)
      {
         object? from = before.GetValueOrDefault(field);
         if (JsonSerializer.Serialize(from) != JsonSerializer.Serialize(to)) { changes[field] = new { from, to }; }
      }
      return changes;
   }

   private static string Target<TRow, TInput, TDto>(OverlayKind<TRow, TInput, TDto> kind, TRow row)
      where TRow : class, IOverlayItem, new()
   {
      string target = $"{kind.Slug}:{kind.Name(row)}";
      return target.Length <= 200 ? target : target[..199] + "…";
   }

   private static ProblemHttpResult NoSuchItem<TRow, TInput, TDto>(OverlayKind<TRow, TInput, TDto> kind, int id)
      where TRow : class, IOverlayItem, new() =>
      ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, $"There is no such {kind.Noun}", $"The overlay has no {kind.Noun} {id}");

   private static ProblemHttpResult Exists<TRow, TInput, TDto>(OverlayKind<TRow, TInput, TDto> kind, string detail)
      where TRow : class, IOverlayItem, new() =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.OverlayItemExists, $"The {kind.Noun} is there already", detail);

   private static ProblemHttpResult Changed<TRow, TInput, TDto>(OverlayKind<TRow, TInput, TDto> kind, TRow row)
      where TRow : class, IOverlayItem, new() =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict, $"The {kind.Noun} {kind.Name(row)} was changed since it was read",
         "Read it again, and make the change again");
}
