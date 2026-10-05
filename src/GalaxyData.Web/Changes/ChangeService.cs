using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Sql;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Changes;

/// <summary>
/// Each user's pending changes: kept in the metadata database (<see cref="UserChangeSet"/>), changed by operations
/// (<see cref="ChangeSetEditor"/>), previewed as the statements each connection would run (a plan, kept for
/// <see cref="ChangeSettings.PlanLifetime"/>), and committed. A commit is recorded in the audit before anything runs,
/// and clears the changes it wrote.
/// </summary>
public sealed partial class ChangeService(MetadataDb db, CatalogService catalogs, QueryEngines engines, SourceProviders providers, ChangePlans plans,
                                          IOptions<GalaxyDataOptions> options, TimeProvider clock, ILogger<ChangeService> logger)
{
   /// <summary>How often a change to the set is tried again when another request changed it first.</summary>
   private const int Attempts = 5;

   public async Task<ChangeSetDto> GetAsync(int userId, CancellationToken cancellationToken)
   {
      UserChangeSet? set = await db.ChangeSets.AsNoTracking().Include(s => s.Changes).SingleOrDefaultAsync(s => s.UserId == userId, cancellationToken);
      return Dto(set);
   }

   /// <summary>Applies the operations together, or none of them (400, by field); with a version, only to the changes at that version (409).</summary>
   public async Task<ChangeSetDto> ApplyAsync(int userId, ChangeOpsRequest request, HttpResponse response, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(request);
      ChangeSettings settings = options.Value.Changes;
      if (request.Ops.Count > settings.MaxChanges) { throw Invalid("ops", $"At most {settings.MaxChanges} operations are applied at once"); }
      int missing = request.Ops.IndexOf(null!);
      if (missing >= 0) { throw Invalid($"ops[{missing.ToString(CultureInfo.InvariantCulture)}]", "An operation can't be null"); }
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      for (int attempt = 1; ; attempt++)
      {
         db.ChangeTracker.Clear();
         UserChangeSet set = await db.ChangeSets.Include(s => s.Changes).SingleOrDefaultAsync(s => s.UserId == userId, cancellationToken)
            ?? new UserChangeSet { UserId = userId };
         if (request.Version is { } read && set.Version != read) { throw Changed(); }
         List<ChangeState> changes = [.. set.Changes.OrderBy(c => c.Id).Select(ChangeState.Read)];
         Dictionary<string, string[]> errors = [];
         ChangeSetEditor editor = new(state.Catalog, providers, changes, errors);
         for (int i = 0; i < request.Ops.Count; i++) { editor.Apply(i, request.Ops[i]); }
         if (errors.Count == 0 && changes.Count(c => !c.Removed) > settings.MaxChanges)
         {
            errors["ops"] = [$"A user may have at most {settings.MaxChanges} changes pending: commit or revert some first"];
         }
         if (errors.Count > 0) { throw new ProblemResultException(ApiProblems.Invalid(errors)); }
         if (!Write(set, changes)) { return Dto(set.Id == 0 ? null : set); }
         try
         {
            await db.SaveChangesAsync(cancellationToken);
            return await GetAsync(userId, cancellationToken);
         }
         catch (DbUpdateException e) when (attempt < Attempts && Raced(e))
         {
            // Another request changed the set first: apply the operations to it as it is now.
         }
      }
   }

   /// <summary>Drops the pending changes, or those of a source (by alias) or an entity (as the changes name it, or as the catalog finds it).</summary>
   public async Task<ChangeSetDto> ClearAsync(int userId, string? source, string? entity, int? version, CancellationToken cancellationToken)
   {
      string? named = entity == null ? null : ChangeRows.Entity((await catalogs.GetAsync(cancellationToken)).Catalog, entity, out _)?.DisplayName;
      for (int attempt = 1; ; attempt++)
      {
         db.ChangeTracker.Clear();
         UserChangeSet? set = await db.ChangeSets.Include(s => s.Changes).SingleOrDefaultAsync(s => s.UserId == userId, cancellationToken);
         if (version is { } read && (set?.Version ?? 0) != read) { throw Changed(); }
         List<PendingChange> gone = set?.Changes
            .Where(c => (source == null || string.Equals(c.Source, source, StringComparison.OrdinalIgnoreCase)) &&
                        (entity == null || c.Entity == entity || c.Entity == named))
            .ToList() ?? [];
         if (gone.Count == 0) { return Dto(set); }
         db.PendingChanges.RemoveRange(gone);
         Touch(set!);
         try
         {
            await db.SaveChangesAsync(cancellationToken);
            return await GetAsync(userId, cancellationToken);
         }
         catch (DbUpdateException e) when (attempt < Attempts && Raced(e))
         {
         }
      }
   }

   /// <summary>
   /// What committing the changes would run, checked against the catalog as it stands; a plan to commit, when no
   /// change has an issue. It replaces the user's plan before. Which scripts the user may edit: an administrator
   /// (<paramref name="admin"/>) all, others those of databases that don't run in the application.
   /// </summary>
   public async Task<ChangePreviewDto> PreviewAsync(int userId, bool admin, HttpResponse response, CancellationToken cancellationToken)
   {
      if (plans.IsCommitting(userId)) { throw Running(); }
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      UserChangeSet? set = await db.ChangeSets.AsNoTracking().Include(s => s.Changes).SingleOrDefaultAsync(s => s.UserId == userId, cancellationToken);
      List<PendingChange> rows = set?.Changes.OrderBy(c => c.Id).ToList() ?? [];
      (ChangeSet changes, List<PendingChange> planned, List<ChangeIssueDto> issues) = Build(state.Catalog, rows);
      DmlPlan plan = engines.For(state).PlanChanges(changes);
      issues.AddRange(plan.Issues.Select(i => new ChangeIssueDto(planned[i.ChangeIndex].Id, i.Column, i.Message)));
      // In the order of the changes.
      Dictionary<long, int> order = rows.Select((r, i) => (r.Id, i)).ToDictionary(r => r.Id, r => r.i);
      issues = [.. issues.OrderBy(i => order[i.Change])];

      ChangePlan? kept = null;
      if (issues.Count == 0 && plan.Scripts.Count > 0)
      {
         kept = new ChangePlan(Guid.NewGuid().ToString("N"), userId, set!.Version, state.Version, plan,
            [.. planned.Select(c => new PlannedChange(c.Id, c.Version, c.TempId))], clock.GetUtcNow().UtcDateTime + options.Value.Changes.PlanLifetime);
         plans.Put(kept);
      }
      else
      {
         plans.Drop(userId);
      }
      List<PreviewScriptDto> scripts = [.. plan.Scripts.Select(s => new PreviewScriptDto(s.Source.Alias, s.Source.ProviderKind, s.Dialect.Name, s.ToDisplayText(),
         admin || !DmlGuard.RunsInTheApplication(s.Dialect),
         [.. s.Statements.Select(t => new PreviewStatementDto(t.ChangeIndex is int i ? planned[i].Id : null, t.Kind, t.Description, t.ToDisplayText()))]))];
      return new ChangePreviewDto(kept?.Id, set?.Version ?? 0, state.Version, kept?.ExpiresAt, plan.IsMultiConnection, scripts, issues);
   }

   /// <summary>
   /// Commits a preview's plan, with the scripts the user edited as they edited them. The commit is in the audit
   /// before anything runs; it runs to its end even if the user goes away (within <c>Query:Timeout</c>), and clears
   /// the changes each connection that committed wrote.
   /// </summary>
   public async Task<CommitResultDto> CommitAsync(ClaimsPrincipal me, CommitChangesRequest request, HttpResponse response, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(me);
      ArgumentNullException.ThrowIfNull(request);
      int userId = me.RequiredUserId();
      bool admin = me.IsInRole(nameof(UserRole.Admin));
      if (request.AllowAnyStatement && !admin)
      {
         throw new ProblemResultException(ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden,
            "Only administrators may run statements that don't change data", "Leave allowAnyStatement off: edited scripts may insert, update, delete and merge"));
      }
      ChangePlan plan = plans.Find(userId, request.PlanId) ?? throw Stale("The preview expired, or another replaced it");
      int version = await db.ChangeSets.Where(s => s.UserId == userId).Select(s => (int?)s.Version).SingleOrDefaultAsync(cancellationToken) ?? 0;
      if (request.Version != plan.Version || version != plan.Version) { throw Stale("The changes were changed since they were previewed"); }
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      if (state.Version != plan.CatalogVersion) { throw Stale("The catalog changed since the changes were previewed"); }
      QueryEngine engine = engines.For(state);
      List<(DmlScript Script, string Text)> scripts = Scripts(engine, plan, request, admin);
      if (!plans.BeginCommit(userId)) { throw Running(); }
      try
      {
         if (!plans.TryTake(plan)) { throw Stale("The preview expired, or another replaced it"); }
         return await RunAsync(me, plan, engine, scripts, request.AllowAnyStatement);
      }
      finally
      {
         plans.EndCommit(userId);
      }
   }

   /// <summary>Runs a plan taken to commit, to its end: in the audit first, so a commit the application doesn't live to finish is known.</summary>
   private async Task<CommitResultDto> RunAsync(ClaimsPrincipal me, ChangePlan plan, QueryEngine engine, List<(DmlScript Script, string Text)> scripts,
                                                bool allowAnyStatement)
   {
      int userId = plan.UserId;
      CommitAudit audit = new()
      {
         StartedAt = clock.GetUtcNow().UtcDateTime,
         UserId = userId,
         UserName = me.Identity?.Name ?? string.Empty,
         Status = CommitStatus.InProgress,
         ChangeCount = plan.Changes.Count,
         IsEdited = scripts.Any(s => s.Script.IsEdited),
         AnyStatement = allowAnyStatement && scripts.Any(s => s.Script.IsEdited),
         CatalogVersion = plan.CatalogVersion,
         Scripts = [.. scripts.Select((s, i) => new CommitAuditScript
         {
            Ordinal = i,
            Source = s.Script.Source.Alias,
            Kind = s.Script.Source.ProviderKind,
            Dialect = s.Script.Dialect.Name,
            IsEdited = s.Script.IsEdited,
            Text = s.Text,
            Statements = s.Script.Statements.Count,
            Status = CommitScriptStatus.Pending,
         })],
      };
      db.CommitAudits.Add(audit);
      await db.SaveChangesAsync(CancellationToken.None);
      string sources = string.Join(", ", audit.Scripts.Select(s => s.Source));

      DmlResult result;
      try
      {
         result = await engine.CommitAsync(scripts.Select(s => s.Script), CancellationToken.None);
      }
      catch (Exception e)
      {
         LogUnexpected(logger, UserNames.ForLog(audit.UserName), sources, e);
         audit.Status = CommitStatus.Unknown;
         audit.FinishedAt = clock.GetUtcNow().UtcDateTime;
         audit.Failure = "The commit failed unexpectedly; the log says why";
         foreach (CommitAuditScript script in audit.Scripts) { script.Status = CommitScriptStatus.Unknown; }
         try
         {
            await db.SaveChangesAsync(CancellationToken.None);
         }
         catch (Exception saving) when (saving is DbUpdateException or SqliteException or InvalidOperationException)
         {
            LogAuditFailed(logger, audit.Id, saving);
         }
         throw;
      }
      // What was written is told, whatever else fails from here on.
      List<string> warnings = [];
      Record(audit, result);
      try
      {
         await db.SaveChangesAsync(CancellationToken.None);
      }
      catch (Exception e) when (e is DbUpdateException or SqliteException or InvalidOperationException)
      {
         LogAuditFailed(logger, audit.Id, e);
         warnings.Add("What came of the commit couldn't be written to the audit, which says it is in progress; the log has it");
      }
      switch (result.Outcome)
      {
         case DmlOutcome.Committed:
            LogCommitted(logger, UserNames.ForLog(audit.UserName), audit.ChangeCount, sources, audit.Id);
            break;
         case DmlOutcome.RolledBack:
            LogRolledBack(logger, UserNames.ForLog(audit.UserName), audit.ChangeCount, sources, audit.Id, audit.Failure);
            break;
         default:
            LogPartial(logger, UserNames.ForLog(audit.UserName), audit.ChangeCount, sources, audit.Id, audit.Failure);
            break;
      }
      if (!await ClearWrittenAsync(userId, plan, result))
      {
         warnings.Add("The changes written couldn't be cleared: revert them before committing again, or they would be written twice");
      }
      foreach (DmlScriptResult script in result.Scripts.Where(s => s.Script.IsEdited && s.Status == DmlScriptStatus.Committed))
      {
         foreach (DmlStatementResult ran in script.Statements.Where(s => s.RowsChanged == 0))
         {
            warnings.Add($"{ran.Statement.Description} of {script.Script.Source.Alias} changed no rows");
         }
      }
      return new CommitResultDto(audit.Id, result.Outcome,
         [.. result.Scripts.Select(s => new CommitScriptDto(s.Script.Source.Alias, s.Script.IsEdited, s.Status, s.Error,
            [.. s.Statements.Select(t => new CommitStatementDto(Change(plan, t.Statement), t.Statement.Description, t.RowsChanged))]))],
         result.Failure is { } failure ? new CommitFailureDto(failure.Kind, failure.Source.Alias, audit.Failure!, Change(plan, failure.Statement), failure.Statement?.Description) : null,
         Inserted(plan, result), await GetAsync(userId, CancellationToken.None), warnings);
   }

   /// <summary>The scripts to run: each of the plan's, or as edited when its text was (checked, and refused with its problems).</summary>
   private static List<(DmlScript Script, string Text)> Scripts(QueryEngine engine, ChangePlan plan, CommitChangesRequest request, bool admin)
   {
      Dictionary<string, string> edited = new(StringComparer.Ordinal);
      Dictionary<string, string[]> errors = [];
      List<EditedScriptDto> given = request.Scripts ?? [];
      for (int i = 0; i < given.Count; i++)
      {
         string field = $"scripts[{i.ToString(CultureInfo.InvariantCulture)}]";
         if (given[i] is not { } script)
         {
            errors[field] = ["A script can't be null"];
            continue;
         }
         DmlScript? planned = plan.Plan.Scripts.FirstOrDefault(s => string.Equals(s.Source.Alias, script.Source, StringComparison.OrdinalIgnoreCase));
         if (planned == null) { errors[field + ".source"] = [$"The changes write nothing to {script.Source}: a script is for a connection they write to"]; }
         else if (!edited.TryAdd(planned.Source.Alias, script.Text)) { errors[field + ".source"] = [$"There are two scripts for {planned.Source.Alias}"]; }
      }
      if (errors.Count > 0) { throw new ProblemResultException(ApiProblems.Invalid(errors)); }
      List<(DmlScript, string)> scripts = [];
      foreach (DmlScript planned in plan.Plan.Scripts)
      {
         string text = planned.ToDisplayText();
         if (edited.TryGetValue(planned.Source.Alias, out string? written) && Normalized(written) != Normalized(text))
         {
            if (DmlGuard.RunsInTheApplication(planned.Dialect) && !admin)
            {
               throw new ProblemResultException(ApiProblems.Result(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden,
                  $"Only administrators may edit the script for {planned.Source.Alias}",
                  $"{planned.Source.Alias} is a {planned.Dialect.Name} database, which runs in the application and reads any file a statement names: commit the changes as planned"));
            }
            try
            {
               scripts.Add((engine.ParseScript(planned.Source, written, request.AllowAnyStatement), written));
            }
            catch (DmlScriptException e)
            {
               throw new ProblemResultException(TypedResults.Problem(ApiProblems.ForScript(e)));
            }
         }
         else
         {
            scripts.Add((planned, text));
         }
      }
      return scripts;
   }

   /// <summary>A script's text as edited, whatever its line breaks, and the white space after it.</summary>
   private static string Normalized(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

   /// <summary>What came of the commit, in its audit.</summary>
   private void Record(CommitAudit audit, DmlResult result)
   {
      audit.FinishedAt = clock.GetUtcNow().UtcDateTime;
      audit.Status = result.Outcome switch
      {
         DmlOutcome.Committed => CommitStatus.Committed,
         DmlOutcome.RolledBack => CommitStatus.RolledBack,
         _ => CommitStatus.PartiallyCommitted,
      };
      if (result.Failure is { } failure)
      {
         audit.FailureKind = JsonNamingPolicy.CamelCase.ConvertName(failure.Kind.ToString());
         audit.Failure = Message(failure);
      }
      List<CommitAuditScript> scripts = [.. audit.Scripts.OrderBy(s => s.Ordinal)];
      for (int i = 0; i < result.Scripts.Count; i++)
      {
         DmlScriptResult ran = result.Scripts[i];
         CommitAuditScript script = scripts[i];
         script.Status = ran.Status switch
         {
            DmlScriptStatus.Committed => CommitScriptStatus.Committed,
            DmlScriptStatus.RolledBack => CommitScriptStatus.RolledBack,
            _ => CommitScriptStatus.CommitFailed,
         };
         script.Error = ran.Error;
         // Of what was written: a script rolled back changed nothing.
         List<long> counted = [.. ran.Statements.Where(s => s.RowsChanged >= 0).Select(s => s.RowsChanged)];
         script.RowsChanged = ran.Status == DmlScriptStatus.Committed && counted.Count > 0 ? counted.Sum() : null;
      }
   }

   /// <summary>Why the changes stopped; a connection's own words (which may name its server or login) are logged, not told.</summary>
   private string Message(DmlFailure failure)
   {
      if (failure.Kind != DmlFailureKind.Connection) { return failure.Message; }
      LogUnavailable(logger, failure.Source.Alias, failure.Message, failure.Exception);
      return $"Couldn't connect to {failure.Source.Alias} to write the changes, so nothing was";
   }

   /// <summary>
   /// Clears the changes the connections that committed wrote, unless they were changed again since they were
   /// previewed; false when they couldn't be (which is logged).
   /// </summary>
   private async Task<bool> ClearWrittenAsync(int userId, ChangePlan plan, DmlResult result)
   {
      try
      {
         await ClearAsync(userId, plan, result);
         return true;
      }
      catch (Exception e) when (e is DbUpdateException or SqliteException or InvalidOperationException)
      {
         LogClearFailed(logger, userId, e);
         return false;
      }
   }

   private async Task ClearAsync(int userId, ChangePlan plan, DmlResult result)
   {
      Dictionary<long, int> written = [];
      for (int i = 0; i < result.Scripts.Count; i++)
      {
         if (result.Scripts[i].Status != DmlScriptStatus.Committed) { continue; }
         // An edited script stands for every change of its connection.
         foreach (DmlStatement statement in plan.Plan.Scripts[i].Statements)
         {
            if (statement.ChangeIndex is int index) { written[plan.Changes[index].Id] = plan.Changes[index].Version; }
         }
      }
      if (written.Count == 0) { return; }
      for (int attempt = 1; ; attempt++)
      {
         db.ChangeTracker.Clear();
         UserChangeSet? set = await db.ChangeSets.Include(s => s.Changes).SingleOrDefaultAsync(s => s.UserId == userId, CancellationToken.None);
         List<PendingChange> done = set?.Changes.Where(c => written.TryGetValue(c.Id, out int version) && c.Version == version).ToList() ?? [];
         if (done.Count == 0) { return; }
         db.PendingChanges.RemoveRange(done);
         Touch(set!);
         try
         {
            await db.SaveChangesAsync(CancellationToken.None);
            return;
         }
         catch (DbUpdateException e) when (attempt < Attempts && Raced(e))
         {
         }
      }
   }

   /// <summary>The new rows as the database made them, of the connections that committed.</summary>
   private static List<InsertedRowDto> Inserted(ChangePlan plan, DmlResult result)
   {
      List<InsertedRowDto> rows = [];
      foreach (DmlScriptResult script in result.Scripts.Where(s => s.Status == DmlScriptStatus.Committed))
      {
         foreach (DmlStatementResult ran in script.Statements)
         {
            if (ran.Statement.Change is not InsertRow insert || ran.Statement.ChangeIndex is not int index || ran.Row == null) { continue; }
            IReadOnlyList<ColumnDef> columns = ran.Statement.ReturnedColumns;
            Dictionary<string, object?> read = new(StringComparer.Ordinal);
            for (int i = 0; i < columns.Count; i++) { read[columns[i].Name] = ran.Row[i]; }
            Dictionary<string, object?> values = columns.ToDictionary(c => c.Name, c => ValueCodec.Encode(read[c.Name], c.Type), StringComparer.Ordinal);
            // Its key, when the row given back has it all.
            IReadOnlyList<ColumnDef>? keyColumns = insert.Entity.Key?.Columns;
            bool keyed = keyColumns != null && keyColumns.All(c => read.TryGetValue(c.Name, out object? value) && value != null);
            rows.Add(new InsertedRowDto(plan.Changes[index].TempId ?? string.Empty, insert.Entity.DisplayName,
               keyed ? [.. keyColumns!.Select(c => values[c.Name])] : null,
               keyed ? ValueCodec.RowId(keyColumns!.Select(c => (read[c.Name], c.Type))) : null, values));
         }
      }
      return rows;
   }

   private static long? Change(ChangePlan plan, DmlStatement? statement) => statement?.ChangeIndex is int index ? plan.Changes[index].Id : null;

   /// <summary>
   /// The engine's changes, read from the pending ones as the catalog stands; what can't be read (an entity or column
   /// gone, a value no longer of its column's type) or done in the application (a binary value) is an issue of the change.
   /// </summary>
   private (ChangeSet Changes, List<PendingChange> Planned, List<ChangeIssueDto> Issues) Build(QueryCatalog catalog, List<PendingChange> rows)
   {
      ChangeSet changes = new();
      List<PendingChange> planned = [];
      List<ChangeIssueDto> issues = [];
      foreach (PendingChange row in rows)
      {
         int before = issues.Count;
         void Issue(string message, string? column = null) => issues.Add(new ChangeIssueDto(row.Id, column, message));
         EntityDef? entity = ChangeRows.Entity(catalog, row.Entity, out string? problem);
         if (entity == null)
         {
            Issue(problem!);
            continue;
         }
         SqlDialect? dialect = entity is TableEntity table ? providers.For(table.Source.ProviderKind).Dialect : null;
         CapabilitiesDto can = EntityCapabilities.Of(entity, dialect, canEditData: true);
         string? reason = row.Kind == PendingChangeKind.Insert ? can.InsertReason : can.ChangeReason;
         if (reason != null)
         {
            Issue(reason);
            continue;
         }
         ChangeState change = ChangeState.Read(row);
         Dictionary<string, object?> values = Values(entity, dialect, can, change.Values, row.Kind == PendingChangeKind.Insert ? ValueUse.Insert : ValueUse.Update, Issue);
         Dictionary<string, object?> original = Values(entity, dialect, can, change.Original, ValueUse.Original, Issue);
         Dictionary<string, object?>? key = row.RowKey == null ? null : Key(entity, row.RowKey, Issue);
         if (issues.Count > before) { continue; }
         changes.Add(row.Kind switch
         {
            PendingChangeKind.Insert => new InsertRow(entity, values),
            PendingChangeKind.Update => new UpdateRow(entity, key!, values) { Original = original },
            _ => new DeleteRow(entity, key!) { Original = original },
         });
         planned.Add(row);
      }
      return (changes, planned, issues);
   }

   private enum ValueUse
   {
      Insert,
      Update,
      Original,
   }

   /// <summary>Values as the engine takes them, by column: a new row's, an update's, or the values a row had (which needn't be ones the user may give).</summary>
   private static Dictionary<string, object?> Values(EntityDef entity, SqlDialect? dialect, CapabilitiesDto can, SortedDictionary<string, JsonElement> kept,
                                                     ValueUse use, Action<string, string?> issue)
   {
      Dictionary<string, object?> values = new(StringComparer.Ordinal);
      foreach ((string name, JsonElement value) in kept)
      {
         if (ChangeRows.Column(entity, name, out string? problem) is not { } column)
         {
            issue(problem!, name);
            continue;
         }
         if (use != ValueUse.Original)
         {
            ColumnCapabilities allowed = EntityCapabilities.Of(column, dialect, can);
            if (use == ValueUse.Insert ? allowed.Insert == InsertMode.Never : !allowed.CanUpdate)
            {
               issue(allowed.Reason ?? $"'{column.Name}' can't be given a value", column.Name);
               continue;
            }
         }
         if (!ChangeRows.TryRead(column, value, out object? read, out _, out problem))
         {
            issue(problem!, column.Name);
            continue;
         }
         values[column.Name] = read;
      }
      return values;
   }

   private static Dictionary<string, object?>? Key(EntityDef entity, string rowKey, Action<string, string?> issue)
   {
      List<JsonElement> given = ChangeRows.KeyValues(rowKey);
      IReadOnlyList<ColumnDef> columns = entity.Key?.Columns ?? [];
      if (given.Count != columns.Count)
      {
         issue($"The key of {entity.DisplayName} isn't what it was when the row was changed: revert the change", null);
         return null;
      }
      Dictionary<string, object?> key = new(StringComparer.Ordinal);
      for (int i = 0; i < columns.Count; i++)
      {
         if (!ChangeRows.TryRead(columns[i], given[i], out object? value, out _, out string? problem))
         {
            issue(problem!, columns[i].Name);
            return null;
         }
         key[columns[i].Name] = value;
      }
      return key;
   }

   /// <summary>Records the changes into their rows; whether any changed.</summary>
   private bool Write(UserChangeSet set, List<ChangeState> changes)
   {
      DateTime now = clock.GetUtcNow().UtcDateTime;
      bool changed = false;
      foreach (ChangeState change in changes)
      {
         if (change.Removed)
         {
            if (change.Row != null)
            {
               db.PendingChanges.Remove(change.Row);
               changed = true;
            }
            continue;
         }
         PendingChange row = change.Row ?? new PendingChange();
         if (change.WriteTo(row))
         {
            row.UpdatedAt = now;
            changed = true;
         }
         if (change.Row == null) { set.Changes.Add(row); }
      }
      if (!changed) { return false; }
      if (set.Id == 0)
      {
         // Version 0 is no set at all.
         set.Version = 1;
         db.ChangeSets.Add(set);
      }
      Touch(set);
      return true;
   }

   /// <summary>Marks the set changed: its version goes up, and is checked as it is saved.</summary>
   private void Touch(UserChangeSet set)
   {
      set.UpdatedAt = clock.GetUtcNow().UtcDateTime;
      if (set.Id != 0) { db.Entry(set).Property(s => s.UpdatedAt).IsModified = true; }
   }

   /// <summary>Another request changed the set (its version), or made it, first.</summary>
   private static bool Raced(DbUpdateException e) => e is DbUpdateConcurrencyException || e.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };

   private static ChangeSetDto Dto(UserChangeSet? set) =>
      new(set?.Version ?? 0, set?.UpdatedAt, [.. (set?.Changes ?? []).OrderBy(c => c.Id).Select(Dto)]);

   private static PendingChangeDto Dto(PendingChange row)
   {
      ChangeState change = ChangeState.Read(row);
      return new PendingChangeDto(row.Id, row.Kind, row.Entity, row.Source, row.RowKey == null ? null : [.. ChangeRows.KeyValues(row.RowKey).Select(k => (object?)k)],
         row.RowKey, row.TempId, ChangeRows.ForDto(change.Values), ChangeRows.ForDto(change.Original), ChangeRows.ForDto(change.Display), row.UpdatedAt);
   }

   private static ProblemResultException Invalid(string field, string message) =>
      new(ApiProblems.Invalid(new Dictionary<string, string[]> { [field] = [message] }));

   private static ProblemResultException Changed() =>
      new(ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict, "The pending changes were changed since they were read",
         "Read them again, and make the change again"));

   private static ProblemResultException Running() =>
      new(ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.CommitInProgress, "The changes are being committed",
         "Preview them again once the commit has finished"));

   private static ProblemResultException Stale(string why) =>
      new(ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.PlanStale, "The preview can't be committed", $"{why}: preview the changes again"));

   [LoggerMessage(Level = LogLevel.Information, Message = "{UserName} committed {Changes} changes to {Sources} (commit {AuditId})")]
   private static partial void LogCommitted(ILogger logger, string userName, int changes, string sources, long auditId);

   [LoggerMessage(Level = LogLevel.Information, Message = "{UserName}'s commit of {Changes} changes to {Sources} was rolled back (commit {AuditId}): {Failure}")]
   private static partial void LogRolledBack(ILogger logger, string userName, int changes, string sources, long auditId, string? failure);

   [LoggerMessage(Level = LogLevel.Error, Message = "{UserName}'s commit of {Changes} changes to {Sources} was partly written (commit {AuditId}): {Failure}")]
   private static partial void LogPartial(ILogger logger, string userName, int changes, string sources, long auditId, string? failure);

   [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't connect to {Source} to write changes: {Reason}")]
   private static partial void LogUnavailable(ILogger logger, string source, string reason, Exception? exception);

   [LoggerMessage(Level = LogLevel.Error, Message = "What came of commit {AuditId} couldn't be written to the audit")]
   private static partial void LogAuditFailed(ILogger logger, long auditId, Exception exception);

   [LoggerMessage(Level = LogLevel.Error, Message = "The changes user {UserId} committed couldn't be cleared: committed again, they would be written twice")]
   private static partial void LogClearFailed(ILogger logger, int userId, Exception exception);

   [LoggerMessage(Level = LogLevel.Error, Message = "{UserName}'s commit of changes to {Sources} failed unexpectedly; what was written is unknown")]
   private static partial void LogUnexpected(ILogger logger, string userName, string sources, Exception exception);
}
