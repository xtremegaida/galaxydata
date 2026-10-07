using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace GalaxyData.Web.Features.Query;

/// <summary>A query to save: its name (unique for its owner, ignoring case), its text, its parameters' values, and whether everyone may see it.</summary>
public sealed record SavedQueryInput(
   [Required] string Name,
   [Required, StringLength(QueryLimits.MaxTextLength)] string Text,
   List<QueryParameterInput>? Parameters = null,
   [StringLength(MetadataDb.DescriptionLength)] string? Description = null,
   bool IsShared = false);

public sealed record UpdateSavedQueryRequest([Required] SavedQueryInput Query, int Version);

/// <summary>A saved query, without its text: whose it is, whether it is shared, and whether the user may change it.</summary>
public sealed record SavedQuerySummaryDto(int Id, string Name, string? Description, string Owner, bool IsShared, bool IsMine, bool CanEdit, DateTime UpdatedAt,
                                          int Version);

/// <summary>A saved query, with its text and its parameters' values (as <see cref="QueryParameterInput"/>s, to run it with).</summary>
public sealed record SavedQueryDto(int Id, string Name, string? Description, string Text, IReadOnlyList<QueryParameterInput> Parameters, string Owner, bool IsShared,
                                   bool IsMine, bool CanEdit, DateTime CreatedAt, DateTime UpdatedAt, int Version);

/// <summary>
/// Saved queries, for anyone who reads data. Each is its owner's; shared ones everyone sees. The owner changes and
/// deletes theirs; administrators also change and delete shared ones, and those whose owner was deleted (which only
/// they see, unless shared). A query is saved as written, whether or not it runs.
/// </summary>
public static class SavedQueryEndpoints
{
   private static readonly JsonSerializerOptions ParametersJson = new(JsonSerializerDefaults.Web);

   public static RouteGroupBuilder MapSavedQueries(this RouteGroupBuilder api)
   {
      RouteGroupBuilder saved = api.MapGroup("/saved-queries")
         .WithTags("Saved queries")
         .RequireAuthorization(Policies.CanRead)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      saved.MapGet("/", ListAsync).WithName("ListSavedQueries").WithSummary("The user's saved queries, and those shared, by name");
      saved.MapGet("/{id:int}", GetAsync).WithName("GetSavedQuery").ProducesProblem(StatusCodes.Status404NotFound);
      saved.MapPost("/", CreateAsync).WithName("CreateSavedQuery").ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
      saved.MapPut("/{id:int}", UpdateAsync).WithName("UpdateSavedQuery")
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status404NotFound)
         .ProducesProblem(StatusCodes.Status409Conflict);
      saved.MapDelete("/{id:int}", DeleteAsync).WithName("DeleteSavedQuery")
         .ProducesProblem(StatusCodes.Status404NotFound)
         .ProducesProblem(StatusCodes.Status409Conflict);
      return api;
   }

   private static async Task<Ok<List<SavedQuerySummaryDto>>> ListAsync(ClaimsPrincipal me, MetadataDb db, CancellationToken cancellationToken)
   {
      // Without their texts, which may be long.
      List<SavedQuery> queries = await Visible(db, me)
         .Select(q => new SavedQuery
         {
            Id = q.Id,
            OwnerId = q.OwnerId,
            OwnerName = q.OwnerName,
            Name = q.Name,
            Description = q.Description,
            IsShared = q.IsShared,
            UpdatedAt = q.UpdatedAt,
            Version = q.Version,
         })
         .ToListAsync(cancellationToken);
      return TypedResults.Ok(queries.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase).ThenBy(q => q.OwnerName, StringComparer.OrdinalIgnoreCase)
         .Select(q => new SavedQuerySummaryDto(q.Id, q.Name, q.Description, q.OwnerName, q.IsShared, IsMine(q, me), CanEdit(q, me), q.UpdatedAt, q.Version))
         .ToList());
   }

   private static async Task<Results<Ok<SavedQueryDto>, ProblemHttpResult>> GetAsync(int id, ClaimsPrincipal me, MetadataDb db, CancellationToken cancellationToken) =>
      await Visible(db, me).AsNoTracking().SingleOrDefaultAsync(q => q.Id == id, cancellationToken) is { } query ? TypedResults.Ok(Dto(query, me)) : NoSuchQuery(id);

   private static async Task<Results<Created<SavedQueryDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(SavedQueryInput query, ClaimsPrincipal me,
      MetadataDb db, TimeProvider clock, CancellationToken cancellationToken)
   {
      Dictionary<string, string[]> errors = [];
      SavedQuery row = new() { OwnerId = me.RequiredUserId(), OwnerName = me.Identity?.Name ?? string.Empty };
      Fill(query, row, errors, string.Empty);
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      if (await NameTakenAsync(db, row, cancellationToken)) { return NameTaken(row.Name); }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      row.CreatedAt = now;
      row.UpdatedAt = now;
      db.SavedQueries.Add(row);
      if (!await SavedAsync(db, cancellationToken)) { return NameTaken(row.Name); }
      return TypedResults.Created($"/api/saved-queries/{row.Id}", Dto(row, me));
   }

   private static async Task<Results<Ok<SavedQueryDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(int id, UpdateSavedQueryRequest request, ClaimsPrincipal me,
      MetadataDb db, TimeProvider clock, CancellationToken cancellationToken)
   {
      SavedQuery? row = await Visible(db, me).SingleOrDefaultAsync(q => q.Id == id, cancellationToken);
      if (row == null) { return NoSuchQuery(id); }
      if (!CanEdit(row, me)) { return NotYours(row); }
      if (row.Version != request.Version) { return Changed(row); }
      string name = row.Name;
      Dictionary<string, string[]> errors = [];
      Fill(request.Query, row, errors, "query.");
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      // Names are their owner's: another would learn the names of the owner's other queries by those taken.
      if (!IsMine(row, me) && row.OwnerId != null && !string.Equals(name, row.Name, StringComparison.Ordinal))
      {
         return ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, $"Only {row.OwnerName} renames {name}", "Change it under the name it has");
      }
      if (await NameTakenAsync(db, row, cancellationToken)) { return NameTaken(row.Name); }
      if (IsMine(row, me)) { row.OwnerName = me.Identity?.Name ?? row.OwnerName; }
      db.ChangeTracker.DetectChanges();
      if (db.Entry(row).State == EntityState.Unchanged) { return TypedResults.Ok(Dto(row, me)); }
      row.UpdatedAt = clock.GetUtcNow().UtcDateTime;
      if (!await SavedAsync(db, cancellationToken)) { return NameTaken(row.Name); }
      return TypedResults.Ok(Dto(row, me));
   }

   private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(int id, int? version, ClaimsPrincipal me, MetadataDb db, CancellationToken cancellationToken)
   {
      SavedQuery? row = await Visible(db, me).SingleOrDefaultAsync(q => q.Id == id, cancellationToken);
      if (row == null) { return NoSuchQuery(id); }
      if (!CanEdit(row, me)) { return NotYours(row); }
      if (version is { } read && row.Version != read) { return Changed(row); }
      db.SavedQueries.Remove(row);
      await db.SaveChangesAsync(cancellationToken);
      return TypedResults.NoContent();
   }

   /// <summary>The user's queries, those shared, and for administrators those whose owner was deleted.</summary>
   private static IQueryable<SavedQuery> Visible(MetadataDb db, ClaimsPrincipal me)
   {
      int id = me.RequiredUserId();
      bool admin = IsAdmin(me);
      return db.SavedQueries.Where(q => q.IsShared || q.OwnerId == id || (admin && q.OwnerId == null));
   }

   private static bool IsMine(SavedQuery query, ClaimsPrincipal me) => query.OwnerId == me.RequiredUserId();

   private static bool CanEdit(SavedQuery query, ClaimsPrincipal me) => IsMine(query, me) || (IsAdmin(me) && (query.IsShared || query.OwnerId == null));

   private static bool IsAdmin(ClaimsPrincipal me) => me.IsInRole(nameof(UserRole.Admin));

   /// <summary>Reads a request into the row; what is wrong goes in <paramref name="errors"/>, by field under <paramref name="prefix"/>.</summary>
   private static void Fill(SavedQueryInput input, SavedQuery row, Dictionary<string, string[]> errors, string prefix)
   {
      string name = input.Name.Trim();
      if (name.Length == 0) { errors[prefix + "name"] = ["Name the query"]; }
      else if (name.Length > MetadataDb.NameLength) { errors[prefix + "name"] = [$"A name has {MetadataDb.NameLength} characters at most"]; }
      // Checked as a query would read them, and kept as given (typed values as they were sent), named without their $.
      _ = QueryInputs.Parameters(input.Parameters, prefix + "parameters", errors);
      if (errors.Count > 0) { return; }
      List<QueryParameterInput> kept = [.. (input.Parameters ?? []).Select(p => new QueryParameterInput(p.Name.TrimStart('$'), p.Type?.Trim(), ValueCodec.ShortestNumber(ValueCodec.Json(p.Value))))];
      // EF changes only what is set to another value: a save of nothing new changes nothing, not even the version.
      row.Name = name;
      row.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
      row.Text = input.Text;
      row.ParametersJson = JsonSerializer.Serialize(kept, ParametersJson);
      row.IsShared = input.IsShared;
   }

   private static SavedQueryDto Dto(SavedQuery query, ClaimsPrincipal me) =>
      new(query.Id, query.Name, query.Description, query.Text, JsonSerializer.Deserialize<List<QueryParameterInput>>(query.ParametersJson, ParametersJson) ?? [],
         query.OwnerName, query.IsShared, IsMine(query, me), CanEdit(query, me), query.CreatedAt, query.UpdatedAt, query.Version);

   /// <summary>Whether the owner has another query of the name; queries of deleted owners are no one's, so share names freely.</summary>
   private static async Task<bool> NameTakenAsync(MetadataDb db, SavedQuery row, CancellationToken cancellationToken) =>
      row.OwnerId != null && await db.SavedQueries.AnyAsync(q => q.Id != row.Id && q.OwnerId == row.OwnerId && q.Name == row.Name, cancellationToken);

   /// <summary>Saves; false when a query of the owner's took the name first (the unique index).</summary>
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

   private static ProblemHttpResult NoSuchQuery(int id) =>
      ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such query", $"There is no saved query {id} you can see");

   private static ProblemHttpResult NotYours(SavedQuery query) =>
      ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, $"{query.Name} is {query.OwnerName}'s",
         "Only its owner (and, for a shared query, an administrator) may change or delete it; save a copy of your own instead");

   private static ProblemHttpResult NameTaken(string name) =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.QueryNameTaken, "The name is taken", $"There is a query {name} already (names ignore case)");

   private static ProblemHttpResult Changed(SavedQuery query) =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict, $"{query.Name} was changed since it was read",
         "Read it again, and make the change again");
}
