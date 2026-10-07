using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Features.Audit;
using GalaxyData.Web.Features.Dashboards;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Palettes;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace GalaxyData.Web.Features.Palettes;

public sealed record CreatePaletteRequest(
   [Required] string Name,
   [StringLength(MetadataDb.DescriptionLength)] string? Description,
   [Required] PaletteDefinition Definition);

/// <summary>Saves a palette: its name, description and definition, to the version read.</summary>
public sealed record UpdatePaletteRequest(
   [Required] string Name,
   [StringLength(MetadataDb.DescriptionLength)] string? Description,
   [Required] PaletteDefinition Definition,
   int Version);

/// <summary>A palette, without its overrides: whose it is, its colours (to show), how it assigns them, and how many dashboards use it.</summary>
public sealed record PaletteSummaryDto(int Id, string Name, string? Description, string Owner, bool IsMine, bool CanEdit, IReadOnlyList<PaletteColor> Colors,
                                       PaletteAssign Assign, int Overrides, int UsedBy, DateTime UpdatedAt, int Version);

/// <summary>A dashboard that uses a palette.</summary>
public sealed record PaletteDashboardDto(int Id, string Name, string Owner);

/// <summary>
/// A palette: its definition, how many dashboards use it (<see cref="UsedBy"/>: all of them; <see cref="Dashboards"/>:
/// those the user sees, by name), and whether the user may change it.
/// </summary>
public sealed record PaletteDto(int Id, string Name, string? Description, string Owner, bool IsMine, bool CanEdit, PaletteDefinition Definition, string Hash,
                                int UsedBy, IReadOnlyList<PaletteDashboardDto> Dashboards, DateTime CreatedAt, DateTime UpdatedAt, int Version);

/// <summary>A palette a dashboard's charts are drawn with: its definition and its hash (which changes with it).</summary>
public sealed record ChartPaletteDto(int Id, string Name, string Owner, PaletteDefinition Definition, string Hash);

/// <summary>
/// Palettes of charts' colours, for anyone who reads data: everyone sees and uses every one, so labels keep their
/// colours across people's dashboards. Each is its owner's, who changes and deletes it; administrators change and
/// delete others' too (written to the audit), but don't rename them. Dashboards using a deleted palette are drawn
/// with their default colours.
/// </summary>
public static class PaletteEndpoints
{
   public static RouteGroupBuilder MapPalettes(this RouteGroupBuilder api)
   {
      RouteGroupBuilder palettes = api.MapGroup("/palettes")
         .WithTags("Palettes")
         .RequireAuthorization(Policies.CanRead)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      palettes.MapGet("/", ListAsync).WithName("ListPalettes").WithSummary("Every palette, by name");
      palettes.MapGet("/{id:int}", GetAsync).WithName("GetPalette").ProducesProblem(StatusCodes.Status404NotFound);
      palettes.MapPost("/", CreateAsync).WithName("CreatePalette").ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
      palettes.MapPut("/{id:int}", UpdateAsync).WithName("UpdatePalette")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      palettes.MapDelete("/{id:int}", DeleteAsync).WithName("DeletePalette")
         .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      return api;
   }

   private static async Task<Ok<List<PaletteSummaryDto>>> ListAsync(ClaimsPrincipal me, MetadataDb db, CancellationToken cancellationToken)
   {
      List<Palette> rows = await db.Palettes.AsNoTracking().ToListAsync(cancellationToken);
      Dictionary<int, int> used = await db.PaletteUses.GroupBy(u => u.PaletteId).Select(g => new { g.Key, Count = g.Count() })
         .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
      return TypedResults.Ok(rows
         .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.OwnerName, StringComparer.OrdinalIgnoreCase)
         .Select(p =>
         {
            PaletteDefinition definition = PaletteJson.Read(p.DefinitionJson);
            return new PaletteSummaryDto(p.Id, p.Name, p.Description, p.OwnerName, IsMine(p, me), CanEdit(p, me), definition.Colors, definition.Assign,
               definition.Overrides.Count, used.GetValueOrDefault(p.Id), p.UpdatedAt, p.Version);
         })
         .ToList());
   }

   private static async Task<Results<Ok<PaletteDto>, ProblemHttpResult>> GetAsync(int id, ClaimsPrincipal me, MetadataDb db, CancellationToken cancellationToken) =>
      await db.Palettes.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken) is { } row
         ? TypedResults.Ok(await DtoAsync(db, row, me, cancellationToken))
         : NoSuchPalette(id);

   private static async Task<Results<Created<PaletteDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(CreatePaletteRequest request, ClaimsPrincipal me,
      MetadataDb db, PaletteRules rules, TimeProvider clock, CancellationToken cancellationToken)
   {
      Dictionary<string, string[]> errors = [];
      Palette row = new() { OwnerId = me.RequiredUserId(), OwnerName = me.Identity?.Name ?? string.Empty };
      Fill(row, request.Name, request.Description, request.Definition, rules, errors);
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      if (await NameTakenAsync(db, row, cancellationToken)) { return NameTaken(row.Name); }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      row.CreatedAt = now;
      row.UpdatedAt = now;
      db.Palettes.Add(row);
      if (!await SavedAsync(db, cancellationToken)) { return NameTaken(row.Name); }
      return TypedResults.Created($"/api/palettes/{row.Id}", await DtoAsync(db, row, me, cancellationToken));
   }

   private static async Task<Results<Ok<PaletteDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(int id, UpdatePaletteRequest request, ClaimsPrincipal me,
      MetadataDb db, PaletteRules rules, TimeProvider clock, CancellationToken cancellationToken)
   {
      Palette? row = await db.Palettes.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
      if (row == null) { return NoSuchPalette(id); }
      if (!CanEdit(row, me)) { return NotYours(row); }
      if (row.Version != request.Version) { return Changed(row); }
      string name = row.Name;
      Dictionary<string, string[]> errors = [];
      Fill(row, request.Name, request.Description, request.Definition, rules, errors);
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      // Names are their owner's: another would learn the names of the owner's other palettes by those taken.
      if (!IsMine(row, me) && row.OwnerId != null && !string.Equals(name, row.Name, StringComparison.Ordinal))
      {
         return ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, $"Only {row.OwnerName} renames {name}", "Change it under the name it has");
      }
      if (await NameTakenAsync(db, row, cancellationToken)) { return NameTaken(row.Name); }
      if (IsMine(row, me)) { row.OwnerName = me.Identity?.Name ?? row.OwnerName; }
      db.ChangeTracker.DetectChanges();
      if (db.Entry(row).State != EntityState.Unchanged)
      {
         DateTime now = clock.GetUtcNow().UtcDateTime;
         row.UpdatedAt = now;
         if (!IsMine(row, me)) { AdminAudit.Add(db, me, "palette.updated", Target(row), new { row.Name, Owner = row.OwnerName }, now); }
         if (!await SavedAsync(db, cancellationToken)) { return NameTaken(row.Name); }
      }
      return TypedResults.Ok(await DtoAsync(db, row, me, cancellationToken));
   }

   private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(int id, int? version, ClaimsPrincipal me, MetadataDb db, TimeProvider clock,
      CancellationToken cancellationToken)
   {
      Palette? row = await db.Palettes.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
      if (row == null) { return NoSuchPalette(id); }
      if (!CanEdit(row, me)) { return NotYours(row); }
      if (version is { } read && row.Version != read) { return Changed(row); }
      List<PaletteUse> uses = await db.PaletteUses.Where(u => u.PaletteId == id).ToListAsync(cancellationToken);
      if (!IsMine(row, me))
      {
         AdminAudit.Add(db, me, "palette.deleted", Target(row), new { row.Name, Owner = row.OwnerName, UsedBy = uses.Count }, clock.GetUtcNow().UtcDateTime);
      }
      db.PaletteUses.RemoveRange(uses);
      db.Palettes.Remove(row);
      await db.SaveChangesAsync(cancellationToken);
      return TypedResults.NoContent();
   }

   private static async Task<PaletteDto> DtoAsync(MetadataDb db, Palette row, ClaimsPrincipal me, CancellationToken cancellationToken)
   {
      int used = await db.PaletteUses.CountAsync(u => u.PaletteId == row.Id, cancellationToken);
      List<PaletteDashboardDto> dashboards = await DashboardEndpoints.Visible(db, me).Where(d => d.PaletteUses.Any(u => u.PaletteId == row.Id))
         .Select(d => new PaletteDashboardDto(d.Id, d.Name, d.OwnerName)).ToListAsync(cancellationToken);
      return new PaletteDto(row.Id, row.Name, row.Description, row.OwnerName, IsMine(row, me), CanEdit(row, me), PaletteJson.Read(row.DefinitionJson), row.Hash,
         used, [.. dashboards.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Owner, StringComparer.OrdinalIgnoreCase)],
         row.CreatedAt, row.UpdatedAt, row.Version);
   }

   private static bool IsMine(Palette row, ClaimsPrincipal me) => row.OwnerId == me.RequiredUserId();

   private static bool CanEdit(Palette row, ClaimsPrincipal me) => IsMine(row, me) || DashboardEndpoints.IsAdmin(me);

   /// <summary>Reads a request into the row; what is wrong goes in <paramref name="errors"/>, by field.</summary>
   private static void Fill(Palette row, string name, string? description, PaletteDefinition? definition, PaletteRules rules, Dictionary<string, string[]> errors)
   {
      string trimmed = (name ?? string.Empty).Trim();
      if (trimmed.Length == 0) { errors["name"] = ["Name the palette"]; }
      else if (trimmed.Length > MetadataDb.NameLength) { errors["name"] = [$"A name has {MetadataDb.NameLength} characters at most"]; }
      PaletteDefinition? checkedDefinition = rules.Check(definition, "definition.", errors);
      if (errors.Count > 0) { return; }
      // EF changes only what is set to another value: a save of nothing new changes nothing, not even the version.
      string json = PaletteJson.Write(checkedDefinition!);
      row.Name = trimmed;
      row.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
      row.DefinitionJson = json;
      row.Hash = GalaxyData.Web.Dashboards.DefinitionJson.Hash(json);
   }

   /// <summary>The audit's name for the palette.</summary>
   private static string Target(Palette row) => $"palette:{row.Id}";

   /// <summary>Whether the owner has another palette of the name; palettes of deleted owners are no one's, so share names freely.</summary>
   private static async Task<bool> NameTakenAsync(MetadataDb db, Palette row, CancellationToken cancellationToken) =>
      row.OwnerId != null && await db.Palettes.AnyAsync(p => p.Id != row.Id && p.OwnerId == row.OwnerId && p.Name == row.Name, cancellationToken);

   /// <summary>Saves; false when a palette of the owner's took the name first (the unique index).</summary>
   private static async Task<bool> SavedAsync(MetadataDb db, CancellationToken cancellationToken)
   {
      try
      {
         await db.SaveChangesAsync(cancellationToken);
         return true;
      }
      catch (DbUpdateException e) when (e is not DbUpdateConcurrencyException && e.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 })
      {
         return false;
      }
   }

   private static ProblemHttpResult NoSuchPalette(int id) =>
      ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such palette", $"There is no palette {id}");

   private static ProblemHttpResult NotYours(Palette row) =>
      ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, $"{row.Name} is {row.OwnerName}'s",
         "Only its owner (and administrators) may change or delete it; copy it to make one of your own");

   private static ProblemHttpResult NameTaken(string name) =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.PaletteNameTaken, "The name is taken", $"There is a palette {name} already (names ignore case)");

   private static ProblemHttpResult Changed(Palette row) =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict, $"{row.Name} was changed since it was read",
         "Read it again, and make the change again");
}
