using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Features.Audit;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Features.Dashboards;

public sealed record CreateDashboardRequest(
   [Required] string Name,
   [StringLength(MetadataDb.DescriptionLength)] string? Description = null,
   DashboardDefinition? Definition = null);

/// <summary>Saves the working copy: its name, description and definition, to the version read.</summary>
public sealed record UpdateDashboardRequest(
   [Required] string Name,
   [StringLength(MetadataDb.DescriptionLength)] string? Description,
   [Required] DashboardDefinition Definition,
   int Version);

public sealed record DashboardVersionRequest(int Version);

public sealed record PublishDashboardRequest(int Version, [StringLength(200)] string? Note = null);

/// <summary>Who sees the published copy: everyone signed in, or the users chosen (by id).</summary>
public sealed record DashboardSharingRequest(bool Everyone, List<int>? Users, int Version);

/// <summary>Whether anyone with the link sees the published copy, and the sites that may frame it (none: any the application allows).</summary>
public sealed record DashboardPublicRequest(bool Enabled, List<string>? Origins, int Version);

public sealed record CopyDashboardRequest([Required] string Name);

public enum DashboardSharing
{
   Private,
   Chosen,
   Everyone,
}

/// <summary>A dashboard, without its definitions: whose it is, who sees it, and what is published.</summary>
public sealed record DashboardSummaryDto(int Id, string Name, string? Description, string Owner, bool IsMine, DashboardSharing Sharing, bool IsPublic,
                                         int? PublishedNumber, DateTime? PublishedAt, bool HasUnpublishedChanges, DateTime UpdatedAt, int Version);

public sealed record PersonDto(int Id, string UserName, string? DisplayName);

public sealed record DashboardSharingDto(bool Everyone, IReadOnlyList<PersonDto> Users);

/// <summary>
/// The public link (<c>/embed/{token}</c>): who made it public, the sites that may frame it, and whether it works
/// now (public dashboards allowed, something published, and whoever made it public still may).
/// </summary>
public sealed record DashboardPublicDto(string Token, IReadOnlyList<string> Origins, string EnabledBy, DateTime EnabledAt, bool Works);

/// <summary>What the user may do with the dashboard.</summary>
public sealed record DashboardPermissionsDto(bool Edit, bool Publish, bool Share, bool MakePublic, bool RevokePublic, bool Delete, bool Copy);

/// <summary>
/// A dashboard: the working copy (its owner's; for administrators, a deleted owner's), the published copy, who sees
/// it and its public link (for its owner and administrators), what the user may do, and what doesn't fit the catalog
/// in the copy they see (the working one if they may edit it).
/// </summary>
public sealed record DashboardDto(int Id, string Name, string? Description, string Owner, bool IsMine,
                                  DashboardDefinition? Working, string? WorkingHash,
                                  DashboardDefinition? Published, string? PublishedHash, int? PublishedNumber, DateTime? PublishedAt, string? PublishedBy,
                                  bool HasUnpublishedChanges, DashboardSharingDto? Sharing, DashboardPublicDto? Public, DashboardPermissionsDto Can,
                                  IReadOnlyList<DashboardIssue> Issues, DateTime CreatedAt, DateTime UpdatedAt, int Version);

public sealed record DashboardRevisionSummaryDto(int Number, DateTime PublishedAt, string PublishedBy, string? Note, bool IsPublished);

public sealed record DashboardRevisionDto(int Number, DateTime PublishedAt, string PublishedBy, string? Note, bool IsPublished, DashboardDefinition Definition);

/// <summary>
/// Dashboards, for anyone who reads data. Each is its owner's, who edits its working copy and publishes it; others
/// see the published copy when it is shared with them (or with everyone). Data managers and administrators may make
/// theirs public. Administrators see the shared, public and orphaned ones, and may take them back (private, the link
/// revoked) or delete them; orphans they edit too. What someone may not see is a 404; what they may see but not
/// change, a 403.
/// </summary>
public static class DashboardEndpoints
{
   public const int MaxOrigins = 20;

   public static RouteGroupBuilder MapDashboards(this RouteGroupBuilder api)
   {
      RouteGroupBuilder dashboards = api.MapGroup("/dashboards")
         .WithTags("Dashboards")
         .RequireAuthorization(Policies.CanRead)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      dashboards.MapGet("/", ListAsync).WithName("ListDashboards").WithSummary("The user's dashboards, and those shared with them, by name");
      dashboards.MapPost("/", CreateAsync).WithName("CreateDashboard").ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
      dashboards.MapGet("/people", PeopleAsync).WithName("FindPeople").WithSummary("Users to share dashboards with, found by name");
      dashboards.MapGet("/{id:int}", GetAsync).WithName("GetDashboard").ProducesProblem(StatusCodes.Status404NotFound);
      dashboards.MapPut("/{id:int}", UpdateAsync).WithName("UpdateDashboard").WithSummary("Saves the working copy")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      dashboards.MapDelete("/{id:int}", DeleteAsync).WithName("DeleteDashboard")
         .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      dashboards.MapPost("/{id:int}/publish", PublishAsync).WithName("PublishDashboard").WithSummary("Publishes the working copy as the next revision")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      dashboards.MapPost("/{id:int}/discard", DiscardAsync).WithName("DiscardDashboardChanges").WithSummary("Puts the published copy back as the working copy")
         .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      dashboards.MapGet("/{id:int}/revisions", RevisionsAsync).WithName("ListDashboardRevisions").ProducesProblem(StatusCodes.Status404NotFound);
      dashboards.MapGet("/{id:int}/revisions/{number:int}", RevisionAsync).WithName("GetDashboardRevision").ProducesProblem(StatusCodes.Status404NotFound);
      dashboards.MapPost("/{id:int}/revisions/{number:int}/restore", RestoreAsync).WithName("RestoreDashboardRevision")
         .WithSummary("Copies a revision into the working copy")
         .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      dashboards.MapPut("/{id:int}/sharing", ShareAsync).WithName("ShareDashboard")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      dashboards.MapPut("/{id:int}/public", PublicAsync).WithName("SetDashboardPublic").WithSummary("Makes the dashboard public, or not, and says who may frame it")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      dashboards.MapPost("/{id:int}/public/regenerate", RegenerateAsync).WithName("RegenerateDashboardLink").WithSummary("Gives the public link a new token; the old one stops")
         .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      dashboards.MapPost("/{id:int}/copy", CopyAsync).WithName("CopyDashboard").WithSummary("Saves a copy as a new dashboard of the user's")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
      DashboardDataEndpoints.MapDashboardData(dashboards);
      return api;
   }

   private static async Task<Ok<List<DashboardSummaryDto>>> ListAsync(ClaimsPrincipal me, MetadataDb db, CancellationToken cancellationToken)
   {
      int id = me.RequiredUserId();
      var rows = await Visible(db, me)
         .Select(d => new
         {
            d.Id, d.Name, d.Description, d.OwnerId, d.OwnerName, d.SharedWithEveryone, Shared = d.Shares.Any(), Public = d.PublicToken != null,
            d.PublishedNumber, d.PublishedAt, Changed = d.PublishedHash == null || d.PublishedHash != d.WorkingHash, d.UpdatedAt, d.Version,
         })
         .ToListAsync(cancellationToken);
      return TypedResults.Ok(rows
         .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.OwnerName, StringComparer.OrdinalIgnoreCase)
         .Select(d =>
         {
            bool mine = d.OwnerId == id;
            DashboardSharing sharing = d.SharedWithEveryone ? DashboardSharing.Everyone : d.Shared ? DashboardSharing.Chosen : DashboardSharing.Private;
            // Others see what is published; whether the working copy differs is the owner's (and for orphans, the administrators').
            bool changed = (mine || d.OwnerId == null) && d.Changed;
            return new DashboardSummaryDto(d.Id, d.Name, d.Description, d.OwnerName, mine, sharing, d.Public, d.PublishedNumber, d.PublishedAt, changed, d.UpdatedAt, d.Version);
         })
         .ToList());
   }

   private static async Task<Results<Created<DashboardDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(CreateDashboardRequest request, ClaimsPrincipal me,
      MetadataDb db, DefinitionRules rules, TimeProvider clock, DashboardViews views, CancellationToken cancellationToken)
   {
      Dictionary<string, string[]> errors = [];
      Dashboard row = new() { OwnerId = me.RequiredUserId(), OwnerName = me.Identity?.Name ?? string.Empty };
      Fill(row, request.Name, request.Description, request.Definition ?? DashboardDefinition.Blank(), rules, errors);
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      if (await NameTakenAsync(db, row, cancellationToken)) { return NameTaken(row.Name); }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      row.CreatedAt = now;
      row.UpdatedAt = now;
      db.Dashboards.Add(row);
      if (!await SavedAsync(db, cancellationToken)) { return NameTaken(row.Name); }
      return TypedResults.Created($"/api/dashboards/{row.Id}", await views.DtoAsync(db, row, me, cancellationToken));
   }

   private static async Task<Results<Ok<DashboardDto>, ProblemHttpResult>> GetAsync(int id, ClaimsPrincipal me, MetadataDb db, DashboardViews views,
      CancellationToken cancellationToken) =>
      await FindAsync(db, me, id, cancellationToken) is { } row ? TypedResults.Ok(await views.DtoAsync(db, row, me, cancellationToken)) : NoSuchDashboard(id);

   private static async Task<Results<Ok<DashboardDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(int id, UpdateDashboardRequest request, ClaimsPrincipal me,
      MetadataDb db, DefinitionRules rules, TimeProvider clock, DashboardViews views, CancellationToken cancellationToken)
   {
      Dashboard? row = await FindAsync(db, me, id, cancellationToken);
      if (row == null) { return NoSuchDashboard(id); }
      if (!SeesWorking(row, me)) { return NotYours(row); }
      if (row.Version != request.Version) { return Changed(row); }
      Dictionary<string, string[]> errors = [];
      Fill(row, request.Name, request.Description, request.Definition, rules, errors);
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      if (await NameTakenAsync(db, row, cancellationToken)) { return NameTaken(row.Name); }
      if (IsMine(row, me)) { row.OwnerName = me.Identity?.Name ?? row.OwnerName; }
      db.ChangeTracker.DetectChanges();
      if (db.Entry(row).State != EntityState.Unchanged)
      {
         row.UpdatedAt = clock.GetUtcNow().UtcDateTime;
         if (!await SavedAsync(db, cancellationToken)) { return NameTaken(row.Name); }
      }
      return TypedResults.Ok(await views.DtoAsync(db, row, me, cancellationToken));
   }

   private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(int id, int? version, ClaimsPrincipal me, MetadataDb db, TimeProvider clock,
      CancellationToken cancellationToken)
   {
      Dashboard? row = await FindAsync(db, me, id, cancellationToken);
      if (row == null) { return NoSuchDashboard(id); }
      if (!IsMine(row, me) && !IsAdmin(me)) { return NotYours(row); }
      if (version is { } read && row.Version != read) { return Changed(row); }
      // Public ones, and others' taken away, are an administrator's business.
      if (row.PublicToken != null || !IsMine(row, me))
      {
         AdminAudit.Add(db, me, "dashboard.deleted", Target(row), new { row.Name, Owner = row.OwnerName, Public = row.PublicToken != null }, clock.GetUtcNow().UtcDateTime);
      }
      db.Dashboards.Remove(row);
      await db.SaveChangesAsync(cancellationToken);
      return TypedResults.NoContent();
   }

   private static async Task<Results<Ok<DashboardDto>, ProblemHttpResult>> PublishAsync(int id, PublishDashboardRequest request, ClaimsPrincipal me, MetadataDb db,
      TimeProvider clock, DashboardViews views, CancellationToken cancellationToken)
   {
      DashboardSettings settings = views.Settings;
      Dashboard? row = await FindAsync(db, me, id, cancellationToken);
      if (row == null) { return NoSuchDashboard(id); }
      if (!SeesWorking(row, me)) { return NotYours(row); }
      if (row.Version != request.Version) { return Changed(row); }
      // Nothing changed since it was last published: nothing to number.
      if (row.WorkingHash != row.PublishedHash)
      {
         DateTime now = clock.GetUtcNow().UtcDateTime;
         int number = (row.PublishedNumber ?? 0) + 1;
         string by = me.Identity?.Name ?? string.Empty;
         db.DashboardRevisions.Add(new DashboardRevision
         {
            DashboardId = row.Id,
            Number = number,
            DefinitionJson = row.WorkingJson,
            Hash = row.WorkingHash,
            PublishedAt = now,
            PublishedById = me.RequiredUserId(),
            PublishedByName = by,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
         });
         row.PublishedJson = row.WorkingJson;
         row.PublishedHash = row.WorkingHash;
         row.PublishedNumber = number;
         row.PublishedAt = now;
         row.PublishedByName = by;
         row.UpdatedAt = now;
         // The newest are kept; the one published now is among them.
         List<DashboardRevision> old = await db.DashboardRevisions.Where(r => r.DashboardId == row.Id).OrderByDescending(r => r.Number)
            .Skip(settings.KeepRevisions - 1).ToListAsync(cancellationToken);
         db.DashboardRevisions.RemoveRange(old);
         await db.SaveChangesAsync(cancellationToken);
      }
      return TypedResults.Ok(await views.DtoAsync(db, row, me, cancellationToken));
   }

   private static async Task<Results<Ok<DashboardDto>, ProblemHttpResult>> DiscardAsync(int id, DashboardVersionRequest request, ClaimsPrincipal me, MetadataDb db,
      TimeProvider clock, DashboardViews views, CancellationToken cancellationToken)
   {
      Dashboard? row = await FindAsync(db, me, id, cancellationToken);
      if (row == null) { return NoSuchDashboard(id); }
      if (!SeesWorking(row, me)) { return NotYours(row); }
      if (row.Version != request.Version) { return Changed(row); }
      if (row.PublishedJson == null || row.PublishedHash == null)
      {
         return ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.Conflict, $"{row.Name} isn't published", "There is no published copy to go back to");
      }
      return await WorkingCopyAsync(db, row, row.PublishedJson, row.PublishedHash, me, clock, views, cancellationToken);
   }

   private static async Task<Results<Ok<List<DashboardRevisionSummaryDto>>, ProblemHttpResult>> RevisionsAsync(int id, ClaimsPrincipal me, MetadataDb db,
      CancellationToken cancellationToken)
   {
      Dashboard? row = await FindAsync(db, me, id, cancellationToken);
      if (row == null) { return NoSuchDashboard(id); }
      if (!SeesWorking(row, me)) { return NotYours(row); }
      List<DashboardRevisionSummaryDto> revisions = await db.DashboardRevisions.Where(r => r.DashboardId == id).OrderByDescending(r => r.Number)
         .Select(r => new DashboardRevisionSummaryDto(r.Number, r.PublishedAt, r.PublishedByName, r.Note, r.Number == row.PublishedNumber))
         .ToListAsync(cancellationToken);
      return TypedResults.Ok(revisions);
   }

   private static async Task<Results<Ok<DashboardRevisionDto>, ProblemHttpResult>> RevisionAsync(int id, int number, ClaimsPrincipal me, MetadataDb db,
      CancellationToken cancellationToken)
   {
      Dashboard? row = await FindAsync(db, me, id, cancellationToken);
      if (row == null) { return NoSuchDashboard(id); }
      if (!SeesWorking(row, me)) { return NotYours(row); }
      DashboardRevision? revision = await db.DashboardRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.DashboardId == id && r.Number == number, cancellationToken);
      if (revision == null) { return NoSuchRevision(row, number); }
      return TypedResults.Ok(new DashboardRevisionDto(revision.Number, revision.PublishedAt, revision.PublishedByName, revision.Note,
         revision.Number == row.PublishedNumber, DefinitionJson.Read(revision.DefinitionJson)));
   }

   private static async Task<Results<Ok<DashboardDto>, ProblemHttpResult>> RestoreAsync(int id, int number, DashboardVersionRequest request, ClaimsPrincipal me,
      MetadataDb db, TimeProvider clock, DashboardViews views, CancellationToken cancellationToken)
   {
      Dashboard? row = await FindAsync(db, me, id, cancellationToken);
      if (row == null) { return NoSuchDashboard(id); }
      if (!SeesWorking(row, me)) { return NotYours(row); }
      if (row.Version != request.Version) { return Changed(row); }
      DashboardRevision? revision = await db.DashboardRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.DashboardId == id && r.Number == number, cancellationToken);
      if (revision == null) { return NoSuchRevision(row, number); }
      return await WorkingCopyAsync(db, row, revision.DefinitionJson, revision.Hash, me, clock, views, cancellationToken);
   }

   private static async Task<Results<Ok<DashboardDto>, ValidationProblem, ProblemHttpResult>> ShareAsync(int id, DashboardSharingRequest request, ClaimsPrincipal me,
      MetadataDb db, TimeProvider clock, DashboardViews views, CancellationToken cancellationToken)
   {
      Dashboard? row = await FindAsync(db, me, id, cancellationToken);
      if (row == null) { return NoSuchDashboard(id); }
      if (!IsMine(row, me) && !IsAdmin(me)) { return NotYours(row); }
      if (row.Version != request.Version) { return Changed(row); }
      List<int> users = request.Users ?? [];
      Dictionary<string, string[]> errors = [];
      HashSet<int> known = [.. await db.Users.Where(u => users.Contains(u.Id)).Select(u => u.Id).ToListAsync(cancellationToken)];
      HashSet<int> chosen = [];
      for (int i = 0; i < users.Count; i++)
      {
         if (!known.Contains(users[i])) { errors[$"users[{i}]"] = [$"There is no user {users[i]}"]; }
         else if (users[i] == row.OwnerId) { errors[$"users[{i}]"] = ["The owner sees it already"]; }
         else if (!chosen.Add(users[i])) { errors[$"users[{i}]"] = ["The user is chosen twice"]; }
      }
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      // Administrators take dashboards back from people; only the owner shows them to more.
      HashSet<int> before = [.. row.Shares.Select(s => s.UserId)];
      if (!IsMine(row, me) && ((request.Everyone && !row.SharedWithEveryone) || !chosen.IsSubsetOf(before)))
      {
         return ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, $"Only {row.OwnerName} shares {row.Name} with more people",
            "Administrators may share it with fewer, or make it private");
      }
      row.SharedWithEveryone = request.Everyone;
      row.Shares.RemoveAll(s => !chosen.Contains(s.UserId));
      row.Shares.AddRange(chosen.Where(u => !before.Contains(u)).Select(u => new DashboardShare { DashboardId = row.Id, UserId = u }));
      db.ChangeTracker.DetectChanges();
      if (db.ChangeTracker.HasChanges())
      {
         // A change of who sees it is a change of the dashboard's: its version goes up.
         row.UpdatedAt = clock.GetUtcNow().UtcDateTime;
         await db.SaveChangesAsync(cancellationToken);
      }
      return TypedResults.Ok(await views.DtoAsync(db, row, me, cancellationToken));
   }

   private static async Task<Results<Ok<DashboardDto>, ValidationProblem, ProblemHttpResult>> PublicAsync(int id, DashboardPublicRequest request, ClaimsPrincipal me,
      MetadataDb db, TimeProvider clock, DashboardViews views, CancellationToken cancellationToken)
   {
      DashboardSettings settings = views.Settings;
      Dashboard? row = await FindAsync(db, me, id, cancellationToken);
      if (row == null) { return NoSuchDashboard(id); }
      if (!IsMine(row, me) && !IsAdmin(me)) { return NotYours(row); }
      if (row.Version != request.Version) { return Changed(row); }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      if (!request.Enabled)
      {
         if (row.PublicToken != null)
         {
            AdminAudit.Add(db, me, "dashboard.public.revoked", Target(row), new { row.Name, Owner = row.OwnerName }, now);
            row.PublicToken = null;
            row.PublicEnabledById = null;
            row.PublicEnabledByName = null;
            row.PublicEnabledAt = null;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
         }
         return TypedResults.Ok(await views.DtoAsync(db, row, me, cancellationToken));
      }
      if (MakePublicProblem(row, me, settings) is { } refused) { return refused; }
      Dictionary<string, string[]> errors = [];
      List<string> origins = Origins(request.Origins, errors);
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      if (row.PublicToken == null)
      {
         row.PublicToken = NewToken();
         row.PublicEnabledById = me.RequiredUserId();
         row.PublicEnabledByName = me.Identity?.Name ?? string.Empty;
         row.PublicEnabledAt = now;
         AdminAudit.Add(db, me, "dashboard.public.enabled", Target(row), new { row.Name, Owner = row.OwnerName, Origins = origins }, now);
      }
      else if (!origins.SequenceEqual(row.EmbedOrigins, StringComparer.Ordinal))
      {
         AdminAudit.Add(db, me, "dashboard.public.origins-changed", Target(row), new { row.Name, From = row.EmbedOrigins, To = origins }, now);
      }
      if (!origins.SequenceEqual(row.EmbedOrigins, StringComparer.Ordinal)) { row.EmbedOrigins = origins; }
      db.ChangeTracker.DetectChanges();
      if (db.Entry(row).State != EntityState.Unchanged)
      {
         row.UpdatedAt = now;
         await db.SaveChangesAsync(cancellationToken);
      }
      return TypedResults.Ok(await views.DtoAsync(db, row, me, cancellationToken));
   }

   private static async Task<Results<Ok<DashboardDto>, ProblemHttpResult>> RegenerateAsync(int id, DashboardVersionRequest request, ClaimsPrincipal me, MetadataDb db,
      TimeProvider clock, DashboardViews views, CancellationToken cancellationToken)
   {
      Dashboard? row = await FindAsync(db, me, id, cancellationToken);
      if (row == null) { return NoSuchDashboard(id); }
      if (!IsMine(row, me) && !IsAdmin(me)) { return NotYours(row); }
      if (row.Version != request.Version) { return Changed(row); }
      if (row.PublicToken == null)
      {
         return ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.Conflict, $"{row.Name} isn't public", "There is no link to give a new token");
      }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      row.PublicToken = NewToken();
      row.UpdatedAt = now;
      AdminAudit.Add(db, me, "dashboard.public.regenerated", Target(row), new { row.Name, Owner = row.OwnerName }, now);
      await db.SaveChangesAsync(cancellationToken);
      return TypedResults.Ok(await views.DtoAsync(db, row, me, cancellationToken));
   }

   private static async Task<Results<Created<DashboardDto>, ValidationProblem, ProblemHttpResult>> CopyAsync(int id, CopyDashboardRequest request, ClaimsPrincipal me,
      MetadataDb db, TimeProvider clock, DashboardViews views, CancellationToken cancellationToken)
   {
      Dashboard? from = await FindAsync(db, me, id, cancellationToken);
      string? json = from == null ? null : SeesWorking(from, me) ? from.WorkingJson : from.PublishedJson;
      string? hash = from == null ? null : SeesWorking(from, me) ? from.WorkingHash : from.PublishedHash;
      if (from == null || json == null || hash == null) { return NoSuchDashboard(id); }
      Dictionary<string, string[]> errors = [];
      Dashboard row = new() { OwnerId = me.RequiredUserId(), OwnerName = me.Identity?.Name ?? string.Empty, Description = from.Description, WorkingJson = json, WorkingHash = hash };
      row.Name = Name(request.Name, "name", errors) ?? string.Empty;
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      if (await NameTakenAsync(db, row, cancellationToken)) { return NameTaken(row.Name); }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      row.CreatedAt = now;
      row.UpdatedAt = now;
      db.Dashboards.Add(row);
      if (!await SavedAsync(db, cancellationToken)) { return NameTaken(row.Name); }
      return TypedResults.Created($"/api/dashboards/{row.Id}", await views.DtoAsync(db, row, me, cancellationToken));
   }

   /// <summary>Enabled users whose user name or display name holds <paramref name="text"/> (ignoring case), by user name.</summary>
   private static async Task<Ok<List<PersonDto>>> PeopleAsync(string? text, [Range(1, 50)] int? take, MetadataDb db, CancellationToken cancellationToken)
   {
      IQueryable<AppUser> users = db.Users.Where(u => !u.IsDisabled);
      if (!string.IsNullOrWhiteSpace(text))
      {
         string pattern = "%" + text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
         users = users.Where(u => EF.Functions.Like(u.UserName, pattern, "\\") || (u.DisplayName != null && EF.Functions.Like(u.DisplayName, pattern, "\\")));
      }
      List<PersonDto> people = await users.OrderBy(u => u.UserName).Take(take ?? 20).Select(u => new PersonDto(u.Id, u.UserName, u.DisplayName)).ToListAsync(cancellationToken);
      return TypedResults.Ok(people);
   }

   /// <summary>
   /// The dashboards the user sees: theirs; those published and shared with them or with everyone; and for
   /// administrators those shared, public, or whose owner was deleted.
   /// </summary>
   private static IQueryable<Dashboard> Visible(MetadataDb db, ClaimsPrincipal me)
   {
      int id = me.RequiredUserId();
      bool admin = IsAdmin(me);
      return db.Dashboards.Where(d => d.OwnerId == id
         || (d.PublishedJson != null && (d.SharedWithEveryone || d.Shares.Any(s => s.UserId == id)))
         || (admin && (d.SharedWithEveryone || d.Shares.Any() || d.PublicToken != null || d.OwnerId == null)));
   }

   internal static Task<Dashboard?> FindAsync(MetadataDb db, ClaimsPrincipal me, int id, CancellationToken cancellationToken) =>
      Visible(db, me).Include(d => d.Shares).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

   internal static bool IsMine(Dashboard row, ClaimsPrincipal me) => row.OwnerId == me.RequiredUserId();

   internal static bool IsAdmin(ClaimsPrincipal me) => me.IsInRole(nameof(UserRole.Admin));

   internal static bool EditsData(ClaimsPrincipal me) => me.IsInRole(nameof(UserRole.DataManager)) || me.IsInRole(nameof(UserRole.Admin));

   /// <summary>The working copy is its owner's; a deleted owner's is the administrators'.</summary>
   internal static bool SeesWorking(Dashboard row, ClaimsPrincipal me) => IsMine(row, me) || (IsAdmin(me) && row.OwnerId == null);

   /// <summary>Why the user may not make the dashboard public (or give its link other sites), if they may not.</summary>
   private static ProblemHttpResult? MakePublicProblem(Dashboard row, ClaimsPrincipal me, DashboardSettings settings)
   {
      if (!settings.AllowPublic)
      {
         return ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.PublicDashboardsDisabled, "Dashboards can't be public here",
            "The application's settings turn public dashboards off");
      }
      if (!IsMine(row, me))
      {
         return ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, $"Only {row.OwnerName} makes {row.Name} public",
            "Administrators may revoke its link, or give it a new one");
      }
      return EditsData(me) ? null : ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Only data managers and administrators make dashboards public",
         "Share it with people instead");
   }

   /// <summary>Reads a request into the row; what is wrong goes in <paramref name="errors"/>, by field.</summary>
   private static void Fill(Dashboard row, string name, string? description, DashboardDefinition? definition, DefinitionRules rules, Dictionary<string, string[]> errors)
   {
      string? trimmed = Name(name, "name", errors);
      DashboardDefinition? checkedDefinition = rules.Check(definition, "definition.", errors);
      if (errors.Count > 0) { return; }
      // EF changes only what is set to another value: a save of nothing new changes nothing, not even the version.
      string json = DefinitionJson.Write(checkedDefinition!);
      row.Name = trimmed!;
      row.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
      row.WorkingJson = json;
      row.WorkingHash = DefinitionJson.Hash(json);
   }

   private static string? Name(string? name, string field, Dictionary<string, string[]> errors)
   {
      string trimmed = (name ?? string.Empty).Trim();
      if (trimmed.Length == 0) { errors[field] = ["Name the dashboard"]; }
      else if (trimmed.Length > MetadataDb.NameLength) { errors[field] = [$"A name has {MetadataDb.NameLength} characters at most"]; }
      else { return trimmed; }
      return null;
   }

   /// <summary>Origins as browsers write them (<c>https://example.com:8443</c>, or <c>https://*.example.com</c>), lower case, each once.</summary>
   internal static List<string> Origins(List<string>? origins, Dictionary<string, string[]> errors)
   {
      List<string> kept = [];
      origins ??= [];
      if (origins.Count > MaxOrigins)
      {
         errors["origins"] = [$"Name {MaxOrigins} sites at most"];
         return kept;
      }
      for (int i = 0; i < origins.Count; i++)
      {
         if (EmbedOrigin.Normalize(origins[i]) is not { } origin) { errors[$"origins[{i}]"] = ["A site is written as https://example.com (a port, or *. for its subdomains, if need be)"]; }
         else if (!kept.Contains(origin, StringComparer.Ordinal)) { kept.Add(origin); }
      }
      return kept;
   }

   private static async Task<Results<Ok<DashboardDto>, ProblemHttpResult>> WorkingCopyAsync(MetadataDb db, Dashboard row, string json, string hash, ClaimsPrincipal me,
      TimeProvider clock, DashboardViews views, CancellationToken cancellationToken)
   {
      row.WorkingJson = json;
      row.WorkingHash = hash;
      db.ChangeTracker.DetectChanges();
      if (db.Entry(row).State != EntityState.Unchanged)
      {
         row.UpdatedAt = clock.GetUtcNow().UtcDateTime;
         await db.SaveChangesAsync(cancellationToken);
      }
      return TypedResults.Ok(await views.DtoAsync(db, row, me, cancellationToken));
   }

   /// <summary>Whether the public link works: public dashboards allowed, something published, and whoever made it public still a data manager or administrator, and enabled.</summary>
   internal static async Task<bool> PublicLinkWorksAsync(MetadataDb db, Dashboard row, DashboardSettings settings, CancellationToken cancellationToken) =>
      settings.AllowPublic && row.PublicToken != null && row.PublishedJson != null && row.PublicEnabledById is { } by
      && await db.Users.AnyAsync(u => u.Id == by && !u.IsDisabled && (u.Role == UserRole.DataManager || u.Role == UserRole.Admin), cancellationToken);

   /// <summary>128 random bits as base64url: 22 letters, digits, <c>-</c> and <c>_</c>, never a dot (which the client's paths would read as a file's).</summary>
   private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

   /// <summary>The audit's name for the dashboard; never its token.</summary>
   private static string Target(Dashboard row) => $"dashboard:{row.Id}";

   /// <summary>Whether the owner has another dashboard of the name; dashboards of deleted owners are no one's, so share names freely.</summary>
   private static async Task<bool> NameTakenAsync(MetadataDb db, Dashboard row, CancellationToken cancellationToken) =>
      row.OwnerId != null && await db.Dashboards.AnyAsync(d => d.Id != row.Id && d.OwnerId == row.OwnerId && d.Name == row.Name, cancellationToken);

   /// <summary>Saves; false when a dashboard of the owner's took the name first (the unique index).</summary>
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

   private static ProblemHttpResult NoSuchDashboard(int id) =>
      ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such dashboard", $"There is no dashboard {id} you can see");

   private static ProblemHttpResult NoSuchRevision(Dashboard row, int number) =>
      ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such revision", $"{row.Name} has no revision {number} (the newest are kept)");

   private static ProblemHttpResult NotYours(Dashboard row) =>
      ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, $"{row.Name} is {row.OwnerName}'s",
         "Only its owner changes it (and administrators, once its owner is gone); save a copy of your own instead");

   private static ProblemHttpResult NameTaken(string name) =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.DashboardNameTaken, "The name is taken", $"There is a dashboard {name} already (names ignore case)");

   private static ProblemHttpResult Changed(Dashboard row) =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict, $"{row.Name} was changed since it was read",
         "Read it again, and make the change again");
}

/// <summary>Dashboards as their answers give them: with what the user may do, and what doesn't fit the catalog.</summary>
public sealed class DashboardViews(CatalogService catalogs, WidgetKinds kinds, TimeProvider clock, IOptions<GalaxyDataOptions> options)
{
   public DashboardSettings Settings => options.Value.Dashboards;

   public async Task<DashboardDto> DtoAsync(MetadataDb db, Dashboard row, ClaimsPrincipal me, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(db);
      ArgumentNullException.ThrowIfNull(row);
      ArgumentNullException.ThrowIfNull(me);
      DashboardSettings settings = Settings;
      bool mine = DashboardEndpoints.IsMine(row, me);
      bool working = DashboardEndpoints.SeesWorking(row, me);
      bool manages = mine || DashboardEndpoints.IsAdmin(me);
      DashboardSharingDto? sharing = null;
      DashboardPublicDto? link = null;
      if (manages)
      {
         List<int> ids = [.. row.Shares.Select(s => s.UserId)];
         List<PersonDto> people = await db.Users.Where(u => ids.Contains(u.Id)).OrderBy(u => u.UserName)
            .Select(u => new PersonDto(u.Id, u.UserName, u.DisplayName)).ToListAsync(cancellationToken);
         sharing = new DashboardSharingDto(row.SharedWithEveryone, people);
         if (row.PublicToken != null)
         {
            link = new DashboardPublicDto(row.PublicToken, row.EmbedOrigins, row.PublicEnabledByName ?? string.Empty, row.PublicEnabledAt ?? default,
               await DashboardEndpoints.PublicLinkWorksAsync(db, row, settings, cancellationToken));
         }
      }
      DashboardPermissionsDto can = new(
         Edit: working,
         Publish: working,
         Share: manages,
         MakePublic: mine && settings.AllowPublic && DashboardEndpoints.EditsData(me),
         RevokePublic: manages && row.PublicToken != null,
         Delete: manages,
         Copy: working || row.PublishedJson != null);
      DashboardDefinition? workingCopy = working ? DefinitionJson.Read(row.WorkingJson) : null;
      DashboardDefinition? published = row.PublishedJson == null ? null : DefinitionJson.Read(row.PublishedJson);
      IReadOnlyList<DashboardIssue> issues = [];
      if ((workingCopy ?? published) is { } shown)
      {
         CatalogState state = await catalogs.GetAsync(cancellationToken);
         issues = DashboardChecks.Issues(shown, state.Catalog, kinds, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
      }
      return new DashboardDto(row.Id, row.Name, row.Description, row.OwnerName, mine,
         workingCopy, working ? row.WorkingHash : null,
         published, row.PublishedHash, row.PublishedNumber, row.PublishedAt, row.PublishedByName,
         working && row.WorkingHash != row.PublishedHash, sharing, link, can, issues, row.CreatedAt, row.UpdatedAt, row.Version);
   }
}
