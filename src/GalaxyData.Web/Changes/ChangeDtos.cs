using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using GalaxyData.Query.Dml;
using GalaxyData.Web.Metadata;

namespace GalaxyData.Web.Changes;

public enum ChangeOpKind
{
   /// <summary>New values for columns of a row that is there (by <c>key</c>), or of a new row (by <c>tempId</c>).</summary>
   Set,

   /// <summary>A new row, named by the client's <c>tempId</c>, with the values given.</summary>
   Insert,

   /// <summary>Deletes a row that is there (by <c>key</c>); a new row (by <c>tempId</c>) is just dropped.</summary>
   Delete,

   /// <summary>Drops a row's change (by <c>key</c>, <c>tempId</c> or <c>change</c>), or only its values for <c>columns</c>.</summary>
   Revert,
}

/// <summary>
/// An operation on the user's pending changes. Rows are named by their entity and either their <see cref="Key"/>
/// (its values in key order, as a grid's rows give it) or, for new rows, the client's <see cref="TempId"/>; a
/// revert may name the change itself (<see cref="Change"/>). Values are by column, as rows' values are sent;
/// <see cref="Original"/> gives the values the row had when it was read (each column set needs one; a deleted row's
/// are checked too). <see cref="Display"/> gives, by navigation, what to show for the row a new foreign key value
/// refers to.
/// </summary>
/// <remarks>Values are declared <c>object</c> (JSON), as request validation would look into a <see cref="System.Text.Json.JsonElement"/> declared so.</remarks>
public sealed record ChangeOpDto(
   [Required] ChangeOpKind? Op,
   string? Entity = null,
   List<object?>? Key = null,
   [StringLength(64)] string? TempId = null,
   long? Change = null,
   Dictionary<string, object?>? Values = null,
   Dictionary<string, object?>? Original = null,
   Dictionary<string, object?>? Display = null,
   List<string>? Columns = null);

/// <summary>Operations applied together, or not at all; with <see cref="Version"/>, only to the changes as they were at that version.</summary>
public sealed record ChangeOpsRequest([Required] List<ChangeOpDto> Ops, int? Version = null);

/// <summary>
/// A change not yet committed: its kind, the entity and its source, the row (by key and id, as a grid's rows have
/// them, or by the client's temporary id for a new row), the new values, the values the row had when first changed,
/// and what to show for the rows new foreign keys refer to.
/// </summary>
public sealed record PendingChangeDto(long Id, PendingChangeKind Kind, string Entity, string Source, IReadOnlyList<object?>? Key, string? RowId, string? TempId,
                                      IReadOnlyDictionary<string, object?> Values, IReadOnlyDictionary<string, object?> Original,
                                      IReadOnlyDictionary<string, object?> Display, DateTime UpdatedAt);

/// <summary>The user's pending changes, in the order they were first made; the version goes up with each change to them.</summary>
public sealed record ChangeSetDto(int Version, DateTime? UpdatedAt, IReadOnlyList<PendingChangeDto> Changes);

/// <summary>Why a change can't be made: the change, the column at fault if one is, and the reason.</summary>
public sealed record ChangeIssueDto(long Change, string? Column, string Message);

/// <summary>A statement of a script, as people read it (values written in), and the change it carries out.</summary>
public sealed record PreviewStatementDto(long? Change, DmlStatementKind Kind, string Description, string Text);

/// <summary>
/// The statements one connection would run, in one transaction: as text with the values written in
/// (<see cref="Text"/>, which may be edited and committed instead), and each with the change it carries out.
/// </summary>
public sealed record PreviewScriptDto(string Source, string Kind, string Dialect, string Text, IReadOnlyList<PreviewStatementDto> Statements);

/// <summary>
/// What committing the pending changes would run: a script for each connection, and why changes can't be made.
/// Only a preview without issues can be committed, by its <see cref="PlanId"/>, until <see cref="ExpiresAt"/> and while
/// the changes (<see cref="Version"/>) and the catalog (<see cref="CatalogVersion"/>) are as they were. Changes to more
/// than one connection commit one connection after another: should a commit fail after another succeeded, they are
/// left partly written (<see cref="MultiConnection"/>).
/// </summary>
public sealed record ChangePreviewDto(string? PlanId, int Version, string CatalogVersion, DateTime? ExpiresAt, bool MultiConnection,
                                      IReadOnlyList<PreviewScriptDto> Scripts, IReadOnlyList<ChangeIssueDto> Issues);

/// <summary>A script of a preview as edited, for its connection (<see cref="Source"/>, the connection's alias).</summary>
public sealed record EditedScriptDto([Required] string Source, [Required, StringLength(ChangeLimits.MaxScriptLength)] string Text);

/// <summary>
/// Commits a preview's plan (<see cref="PlanId"/>) of the changes at <see cref="Version"/>. Scripts given whose text
/// differs from the preview's run as edited: split into statements, which may only change data unless an
/// administrator allows any (<see cref="AllowAnyStatement"/>), and whose rows changed aren't counted.
/// </summary>
public sealed record CommitChangesRequest([Required] string PlanId, int Version, List<EditedScriptDto>? Scripts = null, bool AllowAnyStatement = false);

/// <summary>A statement that ran, the change it carried out (none for an edited script's), and the rows it changed (-1 when the database doesn't say).</summary>
public sealed record CommitStatementDto(long? Change, string Description, long RowsChanged);

/// <summary>What one connection did: its status, why its commit failed, and the statements that ran (the one that failed included).</summary>
public sealed record CommitScriptDto(string Source, bool Edited, DmlScriptStatus Status, string? Error, IReadOnlyList<CommitStatementDto> Statements);

/// <summary>What stopped the changes: where, the change whose statement failed, and why.</summary>
public sealed record CommitFailureDto(DmlFailureKind Kind, string Source, string Message, long? Change, string? Statement);

/// <summary>A new row as the database made it: the client's temporary id, its key and id (as a grid's rows have them), and its values by column.</summary>
public sealed record InsertedRowDto(string TempId, string Entity, IReadOnlyList<object?>? Key, string? RowId, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// What a commit came to: committed, rolled back, or (across connections) partly committed; each connection's
/// script; the new rows; and the pending changes left (those committed are cleared). <see cref="AuditId"/> is the
/// commit's record in the audit. <see cref="Warnings"/> tell of what to look at: edited statements that changed no
/// rows, and changes written that couldn't be cleared.
/// </summary>
public sealed record CommitResultDto(long AuditId, DmlOutcome Outcome, IReadOnlyList<CommitScriptDto> Scripts, CommitFailureDto? Failure,
                                     IReadOnlyList<InsertedRowDto> Inserted, ChangeSetDto Changes, IReadOnlyList<string> Warnings);

/// <summary>A commit, for the audit: who, when, what came of it, and the connections it wrote to.</summary>
public sealed record CommitAuditSummaryDto(long Id, DateTime StartedAt, DateTime? FinishedAt, string User, CommitStatus Status, int Changes,
                                           IReadOnlyList<string> Sources, bool Edited, bool AnyStatement, string? Failure);

/// <summary>A script a commit ran, as text, and what came of it.</summary>
public sealed record CommitAuditScriptDto(string Source, string Kind, string Dialect, bool Edited, CommitScriptStatus Status, int Statements, long? RowsChanged,
                                          string? Error, string Text);

public sealed record CommitAuditDto(long Id, DateTime StartedAt, DateTime? FinishedAt, string User, CommitStatus Status, int Changes, bool Edited,
                                    bool AnyStatement, string CatalogVersion, string? FailureKind, string? Failure, IReadOnlyList<CommitAuditScriptDto> Scripts);

public static class ChangeLimits
{
   /// <summary>The longest edited script.</summary>
   public const int MaxScriptLength = 10_000_000;

   /// <summary>The longest display value kept for a navigation.</summary>
   public const int MaxDisplayLength = 1000;

   /// <summary>The longest value a change keeps, as it is sent (text, or binary as base64).</summary>
   public const int MaxValueLength = 1_000_000;
}
