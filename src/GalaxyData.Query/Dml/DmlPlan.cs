using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Sql;

namespace GalaxyData.Query.Dml;

/// <summary>
/// The statements that carry out a <see cref="ChangeSet"/>: one script for each connection, and the changes that
/// can't be made (<see cref="Issues"/>), which stop it from running. Nothing has run yet.
/// </summary>
public sealed class DmlPlan
{
   internal DmlPlan(IReadOnlyList<DmlScript> scripts, IReadOnlyList<DmlIssue> issues)
   {
      Scripts = scripts;
      Issues = issues;
   }

   /// <summary>One script for each connection, in the order their changes first appear.</summary>
   public IReadOnlyList<DmlScript> Scripts { get; }

   /// <summary>Why changes can't be made; the plan runs only when there are none.</summary>
   public IReadOnlyList<DmlIssue> Issues { get; }

   public bool Success => Issues.Count == 0;

   /// <summary>
   /// Whether the changes are written on more than one connection. Each commits on its own, one after another:
   /// should one fail to commit after another has, the changes are left partly written.
   /// </summary>
   public bool IsMultiConnection => Scripts.Count > 1;

   /// <summary>Every script, each headed by its source.</summary>
   public string ToDisplayText(bool inlineParameters = true)
   {
      StringBuilder text = new();
      foreach (DmlScript script in Scripts)
      {
         if (text.Length > 0) { text.AppendLine(); }
         text.Append("-- ").Append(script.Source.Alias).Append(" (").Append(script.Dialect.Name).AppendLine(")");
         text.Append(script.ToDisplayText(inlineParameters));
      }
      return text.ToString();
   }

   public override string ToString() => ToDisplayText();
}

/// <summary>Why the change at <see cref="ChangeIndex"/> of the change set can't be made; <see cref="Column"/> names the column at fault, if one is.</summary>
public sealed record DmlIssue(int ChangeIndex, RowChange Change, string Message)
{
   public string? Column { get; init; }

   public override string ToString() => $"Change {ChangeIndex}: {Message}";
}

/// <summary>The statements one connection runs, in one transaction.</summary>
public sealed class DmlScript
{
   internal DmlScript(SourceInfo source, SqlDialect dialect, IReadOnlyList<DmlStatement> statements, bool isEdited)
   {
      Source = source;
      Dialect = dialect;
      Statements = statements;
      IsEdited = isEdited;
   }

   public SourceInfo Source { get; }

   public SqlDialect Dialect { get; }

   public IReadOnlyList<DmlStatement> Statements { get; }

   /// <summary>
   /// Made from text a person wrote or edited (<see cref="Execution.QueryEngine.ParseScript"/>), not planned from
   /// changes: its statements change whatever rows they find, and how many isn't checked.
   /// </summary>
   public bool IsEdited { get; }

   /// <summary>
   /// The statements as people read them, each ended by <c>;</c>: the changes alone, without the rows they give back.
   /// With <paramref name="inlineParameters"/>, values are written in, and the text can be edited and run as a script.
   /// </summary>
   public string ToDisplayText(bool inlineParameters = true)
   {
      StringBuilder text = new();
      foreach (DmlStatement statement in Statements)
      {
         if (text.Length > 0) { text.AppendLine(); }
         text.AppendLine(statement.ToDisplayText(inlineParameters) + ";");
      }
      return text.ToString();
   }

   public override string ToString() => ToDisplayText();
}

public enum DmlStatementKind : byte
{
   Insert,
   Update,
   Delete,
   Merge,

   /// <summary>A statement of an edited script that changes no data, run by an administrator's leave.</summary>
   Other,
}

/// <summary>How a statement's count of rows changed is learned.</summary>
internal enum DmlRowCount : byte
{
   /// <summary>The count the database reports for the statement.</summary>
   Affected,

   /// <summary>The rows the statement gives back.</summary>
   Rows,

   /// <summary>A count the statement selects after the change: SQL Server's <c>@@ROWCOUNT</c>, which triggers don't add to.</summary>
   Selected,
}

/// <summary>One statement of a script: its SQL as it runs, and the change it makes.</summary>
public sealed class DmlStatement
{
   private readonly string displayText;
   private readonly string parameterizedText;

   internal DmlStatement(DmlStatementKind kind, string sql, IReadOnlyList<SqlParameterSlot> parameters, string displayText, string parameterizedText)
   {
      Kind = kind;
      Sql = sql;
      Parameters = parameters;
      this.displayText = displayText;
      this.parameterizedText = parameterizedText;
   }

   public DmlStatementKind Kind { get; }

   /// <summary>What the statement does, for messages: <c>the update of shop.orders (id = 1001)</c>, <c>statement 2 (line 4)</c>.</summary>
   public string Description { get; internal init; } = string.Empty;

   /// <summary>The SQL as it runs: with the rows it gives back, and what SQL Server selects after it.</summary>
   public string Sql { get; }

   /// <summary>The values of <see cref="Sql"/>'s parameters, as constants.</summary>
   public IReadOnlyList<SqlParameterSlot> Parameters { get; }

   /// <summary>The change the statement makes, and its index in the change set; null for a statement of an edited script.</summary>
   public RowChange? Change { get; internal init; }

   public int? ChangeIndex { get; internal init; }

   /// <summary>How many rows the statement must change (one); if it changes another number, nothing is committed anywhere. Null when not checked.</summary>
   public int? ExpectedRows { get; internal init; }

   /// <summary>The columns of the row the statement gives back (an inserted row's, identity and defaults included); empty when it gives none.</summary>
   public IReadOnlyList<ColumnDef> ReturnedColumns { get; internal init; } = [];

   internal DmlRowCount Counting { get; init; }

   /// <summary>The statement as people read it, without the rows it gives back; with its values written in, or its parameters listed after it.</summary>
   public string ToDisplayText(bool inlineParameters = true) => inlineParameters ? displayText : parameterizedText;

   public override string ToString() => WithParameters(Sql, Parameters);

   /// <summary>A statement's text with its parameters listed after it, as <see cref="SqlStatement"/> shows them.</summary>
   internal static string WithParameters(string text, IReadOnlyList<SqlParameterSlot> parameters) =>
      parameters.Count == 0
         ? text
         : text + Environment.NewLine + string.Join(Environment.NewLine, parameters.Select(p => $"-- {p.Name} {p.Type} = {p.Description}"));
}

/// <summary>A script that can't run: the problems found in its text, with where they are.</summary>
public sealed class DmlScriptException(SourceInfo source, IReadOnlyList<ScriptProblem> problems)
   : Exception($"The script for {source.Alias} can't run: {string.Join("; ", problems.Select(p => p.ToString()))}")
{
   /// <summary>The source the script was for.</summary>
   public SourceInfo Target { get; } = source;

   public IReadOnlyList<ScriptProblem> Problems { get; } = problems;
}
