using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using GalaxyData.Query.Sql;

namespace GalaxyData.Query.Dml;

/// <summary>
/// Keeps an edited script to statements that change data: <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> and
/// <c>MERGE</c> (and those the dialect adds, such as SQLite's <c>REPLACE</c>), <c>WITH ...</c> forms of those
/// included. The dialect's <see cref="SqlDialect.ChangeRules"/> say what else keeps a statement from running: in SQL
/// Server, where statements needn't end with <c>;</c>, words that start other statements or run other code
/// (<c>DROP</c>, <c>EXEC</c>, <c>DECLARE</c>, ...) and <c>SELECT ... INTO</c>, anywhere in a statement; functions that
/// reach outside the database (DuckDB's <c>read_csv</c>, PostgreSQL's <c>pg_read_file</c>, SQLite's
/// <c>load_extension</c>), by their names quoted or not. The guard is not a sandbox: what the functions a statement calls do is
/// up to the database, and the login's rights are what keep it safe. A database that runs in the application
/// (<see cref="RunsInTheApplication"/>) has no login of its own. Transactions are the engine's in any case: a script
/// can't begin, commit or roll one back, even with <c>allowAnyStatement</c>.
/// </summary>
public static class DmlGuard
{
   private static readonly FrozenSet<string> Changes = FrozenSet.ToFrozenSet(["INSERT", "UPDATE", "DELETE", "MERGE"], StringComparer.Ordinal);

   private static readonly FrozenSet<string> Transactions = FrozenSet.ToFrozenSet(
      ["BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "END", "ABORT", "START", "SAVE"], StringComparer.Ordinal);

   /// <summary>
   /// What keeps the script's statements from running: statements that don't change data (unless
   /// <paramref name="allowAnyStatement"/>), and statements that control the transaction. Includes the script's own problems.
   /// </summary>
   public static IReadOnlyList<ScriptProblem> Check(SplitScript script, SqlDialect dialect, bool allowAnyStatement = false)
   {
      ArgumentNullException.ThrowIfNull(script);
      ArgumentNullException.ThrowIfNull(dialect);
      List<ScriptProblem> problems = [.. script.Problems];
      foreach (ScriptStatement statement in script.Statements)
      {
         string? keyword = statement.Keyword;
         if (keyword != null && Transactions.Contains(keyword))
         {
            problems.Add(Problem(statement, $"{keyword} can't be run here: the changes of each connection run in a transaction of the engine's, committed when every statement has succeeded"));
            continue;
         }
         if (allowAnyStatement) { continue; }
         if (keyword == null || !(Changes.Contains(keyword) || dialect.ChangeRules.MoreChanges.ContainsKey(keyword)))
         {
            problems.Add(Problem(statement, keyword == null
               ? "This isn't a statement that changes data (INSERT, UPDATE, DELETE or MERGE)"
               : $"{keyword} statements can't run here: only those that change data (INSERT, UPDATE, DELETE and MERGE)"));
            continue;
         }
         if (Forbidden(statement, script.Text, dialect.ChangeRules) is { } problem) { problems.Add(problem); }
      }
      return problems;
   }

   /// <summary>
   /// Whether a script for the dialect runs with the application's own rights, which no guard can keep to the
   /// database (DuckDB runs in the application's process): see <see cref="SqlDialect.RunsInTheApplication"/>. Edited
   /// scripts for it should come from those trusted as the application is.
   /// </summary>
   public static bool RunsInTheApplication(SqlDialect dialect)
   {
      ArgumentNullException.ThrowIfNull(dialect);
      return dialect.RunsInTheApplication;
   }

   /// <summary>The kind of change a statement starting with <paramref name="keyword"/> makes in the dialect.</summary>
   internal static DmlStatementKind KindOf(string? keyword, SqlDialect dialect) => keyword switch
   {
      "INSERT" => DmlStatementKind.Insert,
      "UPDATE" => DmlStatementKind.Update,
      "DELETE" => DmlStatementKind.Delete,
      "MERGE" => DmlStatementKind.Merge,
      not null when dialect.ChangeRules.MoreChanges.TryGetValue(keyword, out DmlStatementKind kind) => kind,
      _ => DmlStatementKind.Other,
   };

   /// <summary>A word or function in the statement that keeps it from running, if any.</summary>
   private static ScriptProblem? Forbidden(ScriptStatement statement, string text, ChangeScriptRules rules)
   {
      IReadOnlyList<ScriptToken> tokens = statement.Tokens;
      for (int i = 0; i < tokens.Count; i++)
      {
         ScriptToken token = tokens[i];
         // A quoted name is a function's too when it is called ("pg_read_file"(...)), but never a reserved word.
         bool quoted = token.Kind == ScriptTokenKind.QuotedName;
         if (token.Kind != ScriptTokenKind.Word && !quoted) { continue; }
         string word = quoted ? Unquoted(text[token.Start..token.End]) : text[token.Start..token.End];
         bool call = i + 1 < tokens.Count && tokens[i + 1].Kind == ScriptTokenKind.Symbol && text[tokens[i + 1].Start] == '(';
         if (!quoted && rules.ForbiddenWords.Contains(word)) { return Problem(token, $"{word.ToUpperInvariant()} can't be part of a statement that changes data"); }
         if (!quoted && rules.ForbidsSelectInto && word.Equals("SELECT", StringComparison.OrdinalIgnoreCase) && SelectsInto(tokens, i, text) is { } into)
         {
            return Problem(into, "SELECT ... INTO makes a table: it can't be part of a statement that changes data");
         }
         if (call && rules.IsForbiddenFunction(word))
         {
            return Problem(token, $"{word}(...) reaches outside the database: it can't be part of a statement that changes data");
         }
      }
      return null;
   }

   /// <summary>A quoted name as it names: <c>"a""b"</c> as <c>a"b</c>, <c>[a]]b]</c> as <c>a]b</c>.</summary>
   private static string Unquoted(string quoted)
   {
      if (quoted.Length < 2) { return quoted; }
      char close = quoted[^1];
      return quoted[1..^1].Replace(new string(close, 2), close.ToString(), StringComparison.Ordinal);
   }

   /// <summary>The INTO of a SELECT at <paramref name="select"/>, at its depth of parentheses before its FROM, if it has one.</summary>
   private static ScriptToken? SelectsInto(IReadOnlyList<ScriptToken> tokens, int select, string text)
   {
      int depth = 0;
      for (int i = select + 1; i < tokens.Count; i++)
      {
         ScriptToken token = tokens[i];
         char first = text[token.Start];
         if (token.Kind == ScriptTokenKind.Symbol && first == '(') { depth++; }
         else if (token.Kind == ScriptTokenKind.Symbol && first == ')' && --depth < 0) { return null; }
         else if (depth == 0 && token.Kind == ScriptTokenKind.Word)
         {
            string word = text[token.Start..token.End];
            if (word.Equals("INTO", StringComparison.OrdinalIgnoreCase)) { return token; }
            // FROM ends the select list; another change starts the next statement (INSERT ... SELECT 1 INSERT INTO ...).
            if (word.Equals("FROM", StringComparison.OrdinalIgnoreCase) || Changes.Contains(word.ToUpperInvariant())) { return null; }
         }
      }
      return null;
   }

   private static ScriptProblem Problem(ScriptStatement statement, string message) =>
      new(message, statement.Start, statement.Text.Length, statement.Line);

   private static ScriptProblem Problem(ScriptToken token, string message) =>
      new(message, token.Start, token.End - token.Start, token.Line);
}
