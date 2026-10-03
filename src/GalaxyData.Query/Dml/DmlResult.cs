using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Catalog;

namespace GalaxyData.Query.Dml;

public enum DmlOutcome : byte
{
   /// <summary>Every connection committed.</summary>
   Committed,

   /// <summary>Nothing was committed: a statement failed or changed the wrong number of rows, or the first commit failed.</summary>
   RolledBack,

   /// <summary>Some connections committed before another failed to: their changes are written, the others' aren't.</summary>
   PartiallyCommitted,
}

public enum DmlScriptStatus : byte
{
   Committed,

   RolledBack,

   /// <summary>The commit failed: the database rolled the changes back, unless the connection was lost while committing, when it can't be known.</summary>
   CommitFailed,
}

public enum DmlFailureKind : byte
{
   /// <summary>A connection couldn't be opened, or its transaction begun.</summary>
   Connection,

   /// <summary>A statement failed in the database.</summary>
   Statement,

   /// <summary>A statement changed another number of rows than it had to: its row was changed or deleted since it was read, or its key isn't unique.</summary>
   Conflict,

   /// <summary>A connection failed to commit.</summary>
   Commit,

   /// <summary>The changes took longer to write than they may (<see cref="Execution.QueryEngineOptions.Timeout"/>), and were rolled back.</summary>
   Timeout,
}

/// <summary>What stopped the changes: where, why, and the database's error if there was one.</summary>
public sealed class DmlFailure
{
   internal DmlFailure(DmlFailureKind kind, SourceInfo source, string message, DmlStatement? statement, Exception? exception)
   {
      Kind = kind;
      Source = source;
      Message = message;
      Statement = statement;
      Exception = exception;
   }

   public DmlFailureKind Kind { get; }

   public SourceInfo Source { get; }

   public string Message { get; }

   /// <summary>The statement that failed, or changed the wrong number of rows; null for connections and commits.</summary>
   public DmlStatement? Statement { get; }

   public Exception? Exception { get; }

   public override string ToString() => Message;
}

/// <summary>What running the scripts of a plan, or edited ones, came to.</summary>
public sealed class DmlResult
{
   internal DmlResult(IReadOnlyList<DmlScriptResult> scripts, DmlFailure? failure)
   {
      Scripts = scripts;
      Failure = failure;
      int committed = scripts.Count(s => s.Status == DmlScriptStatus.Committed);
      Outcome = committed == scripts.Count ? DmlOutcome.Committed : committed == 0 ? DmlOutcome.RolledBack : DmlOutcome.PartiallyCommitted;
   }

   public DmlOutcome Outcome { get; }

   public bool Success => Outcome == DmlOutcome.Committed;

   /// <summary>Each script's status and its statements' results, in the order they ran.</summary>
   public IReadOnlyList<DmlScriptResult> Scripts { get; }

   /// <summary>What stopped the changes; null when every connection committed.</summary>
   public DmlFailure? Failure { get; }

   /// <summary>The results of the statements that carried out each change of the plan, by its index in the change set.</summary>
   public DmlStatementResult? ForChange(int changeIndex) =>
      Scripts.SelectMany(s => s.Statements).FirstOrDefault(s => s.Statement.ChangeIndex == changeIndex);

   public override string ToString() => Failure == null ? Outcome.ToString() : $"{Outcome}: {Failure.Message}";
}

public sealed class DmlScriptResult
{
   internal DmlScriptResult(DmlScript script, DmlScriptStatus status, IReadOnlyList<DmlStatementResult> statements, string? error)
   {
      Script = script;
      Status = status;
      Statements = statements;
      Error = error;
   }

   public DmlScript Script { get; }

   public DmlScriptStatus Status { get; }

   /// <summary>The statements that ran, the one that failed included.</summary>
   public IReadOnlyList<DmlStatementResult> Statements { get; }

   /// <summary>Why the commit failed, for <see cref="DmlScriptStatus.CommitFailed"/>.</summary>
   public string? Error { get; }

   public override string ToString() => $"{Script.Source.Alias}: {Status}";
}

/// <summary>A statement that ran: the rows it changed, and the row it gave back (an inserted row, as <see cref="DmlStatement.ReturnedColumns"/>).</summary>
public sealed class DmlStatementResult
{
   internal DmlStatementResult(DmlStatement statement, long rowsChanged, IReadOnlyList<object?>? row)
   {
      Statement = statement;
      RowsChanged = rowsChanged;
      Row = row;
   }

   public DmlStatement Statement { get; }

   /// <summary>The rows the statement changed; -1 when the database doesn't say (an edited statement that isn't a data change).</summary>
   public long RowsChanged { get; }

   /// <summary>The row the statement gave back, as values of <see cref="DmlStatement.ReturnedColumns"/>; null when it gave none.</summary>
   public IReadOnlyList<object?>? Row { get; }

   /// <summary>A value of the row given back, by column name; null when there is no row or no such column.</summary>
   public object? Value(string column)
   {
      if (Row == null) { return null; }
      for (int i = 0; i < Statement.ReturnedColumns.Count; i++)
      {
         if (string.Equals(Statement.ReturnedColumns[i].Name, column, StringComparison.Ordinal)) { return Row[i]; }
      }
      return null;
   }
}
