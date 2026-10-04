using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Language;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Connections;
using GalaxyData.Web.Features.Audit;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Schemas;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace GalaxyData.Web.Features.Connections;

/// <summary>
/// A connection as administrators see it: its settings without secrets, which secrets have values (never the
/// values), and, for kinds that have one, its connection string with secrets masked. <see cref="SecretsUnreadable"/>
/// when the keys that protected its secrets are gone, and they must be entered again. How reading its schema stands:
/// <see cref="SchemaStatus"/>, and why the last read failed (<see cref="SchemaError"/>).
/// </summary>
public sealed record ConnectionDto(int Id, string Alias, string Kind, string? DisplayName, ConnectionMode Mode, Dictionary<string, string> Settings,
                                   Dictionary<string, SecretStateDto> Secrets, string? ConnectionString, Dictionary<string, string> Options, bool IsReadOnly,
                                   bool SecretsUnreadable, SchemaStatus SchemaStatus, string? SchemaError, DateTime? SchemaRefreshedAt, DateTime CreatedAt,
                                   DateTime UpdatedAt, int Version);

public sealed record CreateConnectionRequest([Required] string Alias, [Required] string Kind, [Required] ConnectionInput Connection,
                                             [StringLength(200)] string? DisplayName = null);

public sealed record UpdateConnectionRequest([Required] ConnectionInput Connection, int Version, [StringLength(200)] string? DisplayName = null);

/// <summary>Settings to try before saving them; with <see cref="ConnectionId"/>, the secrets they keep are that connection's.</summary>
public sealed record TestConnectionRequest([Required] string Kind, [Required] ConnectionInput Connection, int? ConnectionId = null);

public sealed record ConvertConnectionRequest([Required] ConnectionInput Connection, ConnectionMode To);

/// <summary>
/// Connections, for administrators: the kinds and their forms, converting between a form and a connection string,
/// and the connections, made, changed, deleted and tried. An alias is a plain name, unique ignoring case, and never
/// changes. Every change is in the admin audit, secrets by name only. A connection's schema is read when it is made,
/// when its settings, secrets or options change, and when asked (<c>refresh</c>); its snapshots say what changed.
/// </summary>
public static partial class ConnectionEndpoints
{
   private static readonly IReadOnlyDictionary<string, string> NoSecrets = new Dictionary<string, string>();

   [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
   private static partial Regex AliasPattern();

   public static RouteGroupBuilder MapConnections(this RouteGroupBuilder api)
   {
      RouteGroupBuilder kinds = api.MapGroup("/connection-kinds")
         .WithTags("Connections")
         .RequireAuthorization(Policies.CanAdmin)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      kinds.MapGet("/", (ConnectionKinds all) => TypedResults.Ok(all.All.Select(k => k.Describe()).ToList()))
         .WithName("ListConnectionKinds")
         .WithSummary("The kinds of connection, and their forms");
      kinds.MapPost("/{kind}/convert", Convert)
         .WithName("ConvertConnection")
         .WithSummary("The same settings as a connection string, or as the form's; secrets come back only as given")
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status404NotFound);

      RouteGroupBuilder connections = api.MapGroup("/connections")
         .WithTags("Connections")
         .RequireAuthorization(Policies.CanAdmin)
         .ProducesProblem(StatusCodes.Status401Unauthorized)
         .ProducesProblem(StatusCodes.Status403Forbidden);
      connections.MapGet("/", ListAsync).WithName("ListConnections");
      connections.MapGet("/{id:int}", GetAsync).WithName("GetConnection").ProducesProblem(StatusCodes.Status404NotFound);
      connections.MapPost("/", CreateAsync).WithName("CreateConnection")
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status409Conflict);
      connections.MapPut("/{id:int}", UpdateAsync).WithName("UpdateConnection")
         .ProducesValidationProblem()
         .ProducesProblem(StatusCodes.Status404NotFound)
         .ProducesProblem(StatusCodes.Status409Conflict);
      connections.MapDelete("/{id:int}", DeleteAsync).WithName("DeleteConnection")
         .ProducesProblem(StatusCodes.Status404NotFound)
         .ProducesProblem(StatusCodes.Status409Conflict);
      connections.MapPost("/{id:int}/test", TestStoredAsync).WithName("TestConnection")
         .RequireRateLimiting(RateLimits.Queries)
         .WithSummary("Tries a connection as it is saved")
         .ProducesProblem(StatusCodes.Status404NotFound);
      connections.MapPost("/test", TestAsync).WithName("TestConnectionSettings")
         .RequireRateLimiting(RateLimits.Queries)
         .WithSummary("Tries settings before they are saved")
         .ProducesValidationProblem();
      connections.MapPost("/{id:int}/refresh", RefreshAsync).WithName("RefreshConnectionSchema")
         .WithSummary("Reads the connection's schema again, in the background; its schemaStatus says how it goes")
         .ProducesProblem(StatusCodes.Status404NotFound);
      connections.MapGet("/{id:int}/snapshots", SnapshotsAsync).WithName("ListSchemaSnapshots")
         .WithSummary("The schemas kept of the connection, newest first, and how much changed in each")
         .ProducesProblem(StatusCodes.Status404NotFound);
      connections.MapGet("/{id:int}/snapshots/{snapshotId:long}", SnapshotAsync).WithName("GetSchemaSnapshot")
         .WithSummary("A schema kept of the connection, and what changed since the one before it")
         .ProducesProblem(StatusCodes.Status404NotFound);
      return api;
   }

   private static Results<Ok<ConnectionInput>, ValidationProblem, ProblemHttpResult> Convert(string kind, ConvertConnectionRequest request, ConnectionKinds kinds)
   {
      ConnectionKind? found = kinds.Find(kind);
      if (found == null) { return NoSuchKind(kind, kinds); }
      if (!found.SupportsRaw) { return ApiProblems.Invalid(Errors("mode", $"{found.DisplayName} connections have no connection string")); }
      try
      {
         return TypedResults.Ok(ConnectionInputs.Convert(found, request.Connection, request.To));
      }
      catch (Exception e) when (e is ArgumentException or FormatException or InvalidCastException or OverflowException or KeyNotFoundException)
      {
         return ApiProblems.Invalid(Errors(request.Connection.Mode == ConnectionMode.Raw ? "connectionString" : "settings", e.Message));
      }
   }

   private static async Task<Ok<List<ConnectionDto>>> ListAsync(MetadataDb db, ConnectionKinds kinds, ConnectionSecrets secrets, CancellationToken cancellationToken)
   {
      List<SourceConnection> all = await db.Connections.AsNoTracking().OrderBy(c => c.Alias).ToListAsync(cancellationToken);
      return TypedResults.Ok(all.Select(c => Dto(c, kinds, secrets)).ToList());
   }

   private static async Task<Results<Ok<ConnectionDto>, ProblemHttpResult>> GetAsync(int id, MetadataDb db, ConnectionKinds kinds, ConnectionSecrets secrets,
                                                                                     CancellationToken cancellationToken) =>
      await db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken) is { } connection
         ? TypedResults.Ok(Dto(connection, kinds, secrets))
         : NoSuchConnection(id);

   private static async Task<Results<Created<ConnectionDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(CreateConnectionRequest request, ClaimsPrincipal me,
      MetadataDb db, ConnectionKinds kinds, FileRoots roots, ConnectionSecrets secrets, SchemaRefreshQueue refreshes, CatalogService catalog, TimeProvider clock,
      CancellationToken cancellationToken)
   {
      Dictionary<string, string[]> errors = [];
      ConnectionKind? kind = kinds.Find(request.Kind);
      if (kind == null) { errors["kind"] = [$"There is no kind '{request.Kind}': use {string.Join(", ", kinds.All.Select(k => k.Id))}"]; }
      if (!AliasPattern().IsMatch(request.Alias) || !QueryText.IsBareIdentifier(request.Alias))
      {
         errors["alias"] = ["An alias is a plain name: a letter or _, then letters, digits and _ (64 at most), and not a word the language has (and, or, not, in, true, false, null)"];
      }
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      if (await db.Connections.AnyAsync(c => c.Alias == request.Alias, cancellationToken)) { return AliasTaken(request.Alias); }
      ConnectionResolution resolved = ConnectionInputs.Resolve(kind!, request.Connection, NoSecrets, roots);
      if (!resolved.IsValid) { return ApiProblems.Invalid(resolved.Errors); }

      DateTime now = clock.GetUtcNow().UtcDateTime;
      SourceConnection connection = new()
      {
         Alias = request.Alias,
         Kind = kind!.Id,
         DisplayName = Blank(request.DisplayName),
         Mode = kind.SupportsRaw ? request.Connection.Mode : ConnectionMode.Form,
         SettingsJson = JsonSerializer.Serialize(resolved.Settings),
         ProtectedSecrets = secrets.Protect(request.Alias, resolved.Secrets),
         OptionsJson = JsonSerializer.Serialize(resolved.Options),
         IsReadOnly = resolved.IsReadOnly,
         SchemaStatus = SchemaStatus.Loading,
         CreatedAt = now,
         UpdatedAt = now,
      };
      db.Connections.Add(connection);
      AdminAudit.Add(db, me, "connection.created", Target(connection), new
      {
         kind = connection.Kind,
         settings = resolved.Settings,
         secrets = resolved.Secrets.Keys.ToList(),
         options = resolved.Options,
         isReadOnly = connection.IsReadOnly,
      }, now);
      try
      {
         await db.SaveChangesAsync(cancellationToken);
      }
      catch (DbUpdateException e) when (e.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
      {
         return AliasTaken(request.Alias);
      }
      catalog.Invalidate();
      refreshes.Enqueue(connection.Id);
      return TypedResults.Created($"/api/connections/{connection.Id}", Dto(connection, kind, resolved.Secrets));
   }

   private static async Task<Results<Ok<ConnectionDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(int id, UpdateConnectionRequest request, ClaimsPrincipal me,
      MetadataDb db, ConnectionKinds kinds, FileRoots roots, ConnectionSecrets secrets, SchemaRefreshQueue refreshes, CatalogService catalog, TimeProvider clock,
      CancellationToken cancellationToken)
   {
      SourceConnection? connection = await db.Connections.FindAsync([id], cancellationToken);
      if (connection == null) { return NoSuchConnection(id); }
      if (connection.Version != request.Version) { return Changed(connection); }
      ConnectionKind kind = KindOf(connection, kinds);
      Dictionary<string, string>? stored = secrets.Unprotect(connection.Alias, connection.ProtectedSecrets);
      ConnectionResolution resolved = ConnectionInputs.Resolve(kind, request.Connection, stored ?? NoSecrets.ToDictionary(), roots);
      if (stored == null) { Unreadable(kind, request.Connection, resolved); }
      if (!resolved.IsValid) { return ApiProblems.Invalid(resolved.Errors); }

      Dictionary<string, object?> changes = [];
      Changes("settings", connection.Settings(), resolved.Settings, changes);
      Changes("options", connection.Options(), resolved.Options, changes);
      Dictionary<string, string> secretChanges = SecretChanges(stored, resolved.Secrets);
      if (secretChanges.Count > 0) { changes["secrets"] = secretChanges; }
      // What it reads changed, or how it reads (a folder's sheets): its schema is read again.
      bool reread = changes.Count > 0;
      string? displayName = Blank(request.DisplayName);
      if (!string.Equals(displayName, connection.DisplayName, StringComparison.Ordinal)) { changes["displayName"] = new { from = connection.DisplayName, to = displayName }; }
      if (resolved.IsReadOnly != connection.IsReadOnly) { changes["isReadOnly"] = new { from = connection.IsReadOnly, to = resolved.IsReadOnly }; }
      ConnectionMode mode = kind.SupportsRaw ? request.Connection.Mode : ConnectionMode.Form;
      if (changes.Count == 0 && mode == connection.Mode) { return TypedResults.Ok(Dto(connection, kinds, secrets)); }

      DateTime now = clock.GetUtcNow().UtcDateTime;
      connection.DisplayName = displayName;
      connection.Mode = mode;
      connection.SettingsJson = JsonSerializer.Serialize(resolved.Settings);
      connection.ProtectedSecrets = secrets.Protect(connection.Alias, resolved.Secrets);
      connection.OptionsJson = JsonSerializer.Serialize(resolved.Options);
      connection.IsReadOnly = resolved.IsReadOnly;
      connection.UpdatedAt = now;
      if (reread) { connection.SchemaStatus = SchemaStatus.Loading; }
      if (changes.Count > 0) { AdminAudit.Add(db, me, "connection.updated", Target(connection), changes, now); }
      await db.SaveChangesAsync(cancellationToken);
      catalog.Invalidate();
      if (reread) { refreshes.Enqueue(connection.Id); }
      return TypedResults.Ok(Dto(connection, kind, resolved.Secrets));
   }

   private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(int id, int? version, ClaimsPrincipal me, MetadataDb db, CatalogService catalog,
                                                                              TimeProvider clock, CancellationToken cancellationToken)
   {
      SourceConnection? connection = await db.Connections.FindAsync([id], cancellationToken);
      if (connection == null) { return NoSuchConnection(id); }
      if (version is { } read && connection.Version != read) { return Changed(connection); }
      DateTime now = clock.GetUtcNow().UtcDateTime;
      // Users' changes to its rows go with it: a connection given its alias later would take them.
      List<PendingChange> pending = await db.PendingChanges.Where(c => c.Source == connection.Alias).ToListAsync(cancellationToken);
      if (pending.Count > 0)
      {
         HashSet<int> sets = [.. pending.Select(c => c.ChangeSetId)];
         db.PendingChanges.RemoveRange(pending);
         foreach (UserChangeSet set in await db.ChangeSets.Where(s => sets.Contains(s.Id)).ToListAsync(cancellationToken))
         {
            set.UpdatedAt = now;
            db.Entry(set).Property(s => s.UpdatedAt).IsModified = true;
         }
      }
      db.Connections.Remove(connection);
      AdminAudit.Add(db, me, "connection.deleted", Target(connection), new { kind = connection.Kind, pendingChanges = pending.Count }, now);
      await db.SaveChangesAsync(cancellationToken);
      catalog.Invalidate();
      return TypedResults.NoContent();
   }

   private static async Task<Results<Accepted<ConnectionDto>, ProblemHttpResult>> RefreshAsync(int id, MetadataDb db, ConnectionKinds kinds, ConnectionSecrets secrets,
      SchemaRefreshQueue refreshes, CatalogService catalog, CancellationToken cancellationToken)
   {
      if (await SchemaRefresher.MarkLoadingAsync(db, id, cancellationToken) == 0) { return NoSuchConnection(id); }
      // Read before it is queued, which may be read at once; it may have been deleted since.
      SourceConnection? connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
      if (connection == null) { return NoSuchConnection(id); }
      catalog.Invalidate();
      refreshes.Enqueue(id);
      return TypedResults.Accepted($"/api/connections/{id}", Dto(connection, kinds, secrets));
   }

   private static async Task<Results<Ok<List<SchemaSnapshotDto>>, ProblemHttpResult>> SnapshotsAsync(int id, MetadataDb db, CancellationToken cancellationToken)
   {
      if (!await db.Connections.AnyAsync(c => c.Id == id, cancellationToken)) { return NoSuchConnection(id); }
      var snapshots = await db.SchemaSnapshots.AsNoTracking().Where(s => s.ConnectionId == id).OrderByDescending(s => s.Id)
         .Select(s => new { s.Id, s.Hash, s.TableCount, s.TakenAt, s.CheckedAt, s.Changes })
         .ToListAsync(cancellationToken);
      return TypedResults.Ok(snapshots.Select(s => SchemaSnapshots.Dto(s.Id, s.Hash, s.TableCount, s.TakenAt, s.CheckedAt, s.Changes)).ToList());
   }

   private static async Task<Results<Ok<SchemaSnapshotDetailDto>, ProblemHttpResult>> SnapshotAsync(int id, long snapshotId, MetadataDb db,
                                                                                                   CancellationToken cancellationToken)
   {
      SchemaSnapshot? snapshot = await db.SchemaSnapshots.AsNoTracking().SingleOrDefaultAsync(s => s.Id == snapshotId && s.ConnectionId == id, cancellationToken);
      if (snapshot == null)
      {
         return ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such snapshot",
            $"Connection {id} has no schema snapshot {snapshotId} (the newest {SchemaSnapshots.Kept} are kept)");
      }
      return TypedResults.Ok(new SchemaSnapshotDetailDto(
         SchemaSnapshots.Dto(snapshot.Id, snapshot.Hash, snapshot.TableCount, snapshot.TakenAt, snapshot.CheckedAt, snapshot.Changes),
         SchemaSnapshots.Read(snapshot.Data), SchemaSnapshots.Changes(snapshot.Changes)));
   }

   private static async Task<Results<Ok<ConnectionTestDto>, ProblemHttpResult>> TestStoredAsync(int id, MetadataDb db, ConnectionKinds kinds,
      ConnectionSecrets secrets, ConnectionTester tester, CancellationToken cancellationToken)
   {
      SourceConnection? connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
      if (connection == null) { return NoSuchConnection(id); }
      Dictionary<string, string>? stored = secrets.Unprotect(connection.Alias, connection.ProtectedSecrets);
      if (stored == null) { return TypedResults.Ok(new ConnectionTestDto(false, StoredConnections.UnreadableSecrets, 0)); }
      ConnectionResolution resolved = new() { IsReadOnly = connection.IsReadOnly };
      foreach ((string key, string value) in connection.Settings()) { resolved.Settings[key] = value; }
      foreach ((string key, string value) in stored) { resolved.Secrets[key] = value; }
      return TypedResults.Ok(await tester.TestAsync(connection.Alias, KindOf(connection, kinds), resolved, cancellationToken));
   }

   private static async Task<Results<Ok<ConnectionTestDto>, ValidationProblem>> TestAsync(TestConnectionRequest request, MetadataDb db, ConnectionKinds kinds,
      FileRoots roots, ConnectionSecrets secrets, ConnectionTester tester, CancellationToken cancellationToken)
   {
      ConnectionKind? kind = kinds.Find(request.Kind);
      if (kind == null) { return ApiProblems.Invalid(Errors("kind", $"There is no kind '{request.Kind}'")); }
      Dictionary<string, string>? stored = new(StringComparer.OrdinalIgnoreCase);
      string alias = "(new)";
      if (request.ConnectionId is { } id)
      {
         SourceConnection? connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
         if (connection == null || connection.Kind != kind.Id) { return ApiProblems.Invalid(Errors("connectionId", $"There is no {kind.DisplayName} connection {id}")); }
         stored = secrets.Unprotect(connection.Alias, connection.ProtectedSecrets);
         alias = connection.Alias;
      }
      ConnectionResolution resolved = ConnectionInputs.Resolve(kind, request.Connection, stored ?? NoSecrets.ToDictionary(), roots);
      if (stored == null) { Unreadable(kind, request.Connection, resolved); }
      if (!resolved.IsValid) { return ApiProblems.Invalid(resolved.Errors); }
      return TypedResults.Ok(await tester.TestAsync(alias, kind, resolved, cancellationToken));
   }

   /// <summary>Secrets kept when the stored ones can't be read are problems: they must be entered again.</summary>
   private static void Unreadable(ConnectionKind kind, ConnectionInput input, ConnectionResolution resolved)
   {
      bool keeps = input.Mode == ConnectionMode.Raw
         ? MasksKept(kind, input)
         : input.Secrets?.Values.Any(s => s.Action == SecretAction.Keep) == true;
      if (keeps) { resolved.Error(input.Mode == ConnectionMode.Raw ? "connectionString" : "secrets", StoredConnections.UnreadableSecrets); }
   }

   /// <summary>
   /// Whether a connection string keeps a stored secret: writes <c>********</c> for one that its secrets don't set
   /// (as the form does when it was converted into the string).
   /// </summary>
   private static bool MasksKept(ConnectionKind kind, ConnectionInput input)
   {
      if (input.ConnectionString?.Contains(ConnectionStrings.Mask, StringComparison.Ordinal) != true) { return false; }
      Dictionary<string, SecretInput> given = new(input.Secrets ?? [], StringComparer.OrdinalIgnoreCase);
      try
      {
         return kind.Parse(input.ConnectionString).Any(p => kind.IsSecret(p.Key) && p.Value == ConnectionStrings.Mask
            && !(given.TryGetValue(p.Key, out SecretInput? action) && action.Action == SecretAction.Set && !string.IsNullOrEmpty(action.Value)));
      }
      catch (Exception e) when (e is ArgumentException or FormatException or InvalidCastException or OverflowException or KeyNotFoundException)
      {
         return true;
      }
   }

   internal static ConnectionDto Dto(SourceConnection connection, ConnectionKinds kinds, ConnectionSecrets secrets) =>
      Dto(connection, KindOf(connection, kinds), secrets.Unprotect(connection.Alias, connection.ProtectedSecrets));

   private static ConnectionDto Dto(SourceConnection connection, ConnectionKind kind, IReadOnlyDictionary<string, string>? secrets)
   {
      Dictionary<string, string> settings = connection.Settings();
      Dictionary<string, SecretStateDto> states = new(StringComparer.OrdinalIgnoreCase);
      foreach (FieldDto field in kind.Fields.Where(f => f.Type == FieldType.Password)) { states[field.Key] = new SecretStateDto(false); }
      foreach (string key in secrets?.Keys ?? []) { states[key] = new SecretStateDto(true); }
      string? text = kind.SupportsRaw ? kind.Display(settings, secrets?.Keys ?? []) : null;
      return new ConnectionDto(connection.Id, connection.Alias, connection.Kind, connection.DisplayName, connection.Mode, settings, states, text,
         connection.Options(), connection.IsReadOnly, secrets == null, connection.SchemaStatus, connection.SchemaError, connection.SchemaRefreshedAt,
         connection.CreatedAt, connection.UpdatedAt, connection.Version);
   }

   private static ConnectionKind KindOf(SourceConnection connection, ConnectionKinds kinds) =>
      kinds.Find(connection.Kind) ?? throw new InvalidOperationException($"The connection {connection.Alias} is of a kind the application hasn't: {connection.Kind}");

   private static void Changes(string what, Dictionary<string, string> before, Dictionary<string, string> after, Dictionary<string, object?> changes)
   {
      Dictionary<string, object> changed = [];
      foreach (string key in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
      {
         string? from = before.GetValueOrDefault(key);
         string? to = after.GetValueOrDefault(key);
         if (!string.Equals(from, to, StringComparison.Ordinal)) { changed[key] = new { from, to }; }
      }
      if (changed.Count > 0) { changes[what] = changed; }
   }

   /// <summary>Which secrets were set, changed or cleared: names only.</summary>
   private static Dictionary<string, string> SecretChanges(Dictionary<string, string>? before, Dictionary<string, string> after)
   {
      Dictionary<string, string> changed = [];
      foreach (string key in (before?.Keys ?? Enumerable.Empty<string>()).Union(after.Keys, StringComparer.OrdinalIgnoreCase))
      {
         bool had = before?.ContainsKey(key) == true;
         bool has = after.ContainsKey(key);
         if (had && !has) { changed[key] = "cleared"; }
         else if (!had && has) { changed[key] = "set"; }
         else if (had && has && !string.Equals(before![key], after[key], StringComparison.Ordinal)) { changed[key] = "changed"; }
      }
      return changed;
   }

   private static string Target(SourceConnection connection) => "connection:" + connection.Alias;

   private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

   private static Dictionary<string, string[]> Errors(string field, string message) => new() { [field] = [message] };

   private static ProblemHttpResult NoSuchConnection(int id) =>
      ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such connection", $"There is no connection {id}");

   private static ProblemHttpResult NoSuchKind(string kind, ConnectionKinds kinds) =>
      ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such kind of connection",
         $"There is no kind '{kind}': use {string.Join(", ", kinds.All.Select(k => k.Id))}");

   private static ProblemHttpResult AliasTaken(string alias) =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.AliasTaken, "The alias is taken", $"There is a connection {alias} already (aliases ignore case)");

   private static ProblemHttpResult Changed(SourceConnection connection) =>
      ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict, $"{connection.Alias} was changed since it was read",
         "Read it again, and make the change again");
}
