using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using GalaxyData.Query.Sql;

namespace GalaxyData.Query.Dml;

/// <summary>
/// Keeps an edited script to statements that change data: <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> and
/// <c>MERGE</c> (and SQLite's <c>REPLACE</c>), <c>WITH ...</c> forms of those included. In SQL Server, where statements
/// needn't end with <c>;</c>, words that start other statements or run other code (<c>DROP</c>, <c>EXEC</c>,
/// <c>DECLARE</c>, ...) and <c>SELECT ... INTO</c> are rejected anywhere in a statement. Functions that reach outside
/// the database (DuckDB's <c>read_csv</c>, PostgreSQL's <c>pg_read_file</c>, SQLite's <c>load_extension</c>) are
/// rejected too. The guard is not a sandbox: what the functions a statement calls do is up to the database, and the
/// login's rights are what keep it safe. Transactions are the engine's in any case: a script can't begin, commit or
/// roll one back, even with <c>allowAnyStatement</c>.
/// </summary>
public static class DmlGuard
{
   private static readonly FrozenSet<string> Changes = FrozenSet.ToFrozenSet(["INSERT", "UPDATE", "DELETE", "MERGE"], StringComparer.Ordinal);

   private static readonly FrozenSet<string> Transactions = FrozenSet.ToFrozenSet(
      ["BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "END", "ABORT", "START", "SAVE"], StringComparer.Ordinal);

   /// <summary>SQL Server's reserved words that start statements other than data changes, or run code.</summary>
   private static readonly FrozenSet<string> TSqlStatements = FrozenSet.ToFrozenSet(
   [
      "ALTER", "BACKUP", "BEGIN", "BREAK", "BULK", "CHECKPOINT", "CLOSE", "COMMIT", "CONTINUE", "CREATE", "DBCC", "DEALLOCATE",
      "DECLARE", "DENY", "DISK", "DROP", "DUMP", "EXEC", "EXECUTE", "FETCH", "GOTO", "GRANT", "IF", "KILL", "LINENO", "LOAD", "OPEN",
      "OPENDATASOURCE", "OPENQUERY", "OPENROWSET", "OPENXML", "PRINT", "RAISERROR", "READTEXT", "RECONFIGURE", "RESTORE", "RETURN",
      "REVERT", "REVOKE", "ROLLBACK", "SAVE", "SETUSER", "SHUTDOWN", "TRUNCATE", "UPDATETEXT", "USE", "WAITFOR", "WHILE", "WRITETEXT",
   ], StringComparer.OrdinalIgnoreCase);

   private static readonly FrozenSet<string> PostgresFunctions = FrozenSet.ToFrozenSet(
   [
      "pg_read_file", "pg_read_binary_file", "pg_ls_dir", "pg_stat_file", "lo_import", "lo_export", "dblink", "dblink_exec",
      "pg_terminate_backend", "pg_cancel_backend", "pg_reload_conf", "pg_rotate_logfile", "set_config",
   ], StringComparer.OrdinalIgnoreCase);

   private static readonly FrozenSet<string> DuckDbFunctions = FrozenSet.ToFrozenSet(
      ["glob", "parquet_scan", "parquet_metadata", "parquet_schema", "sniff_csv", "csv_scan", "query", "query_table"], StringComparer.OrdinalIgnoreCase);

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
         if (keyword == null || !(Changes.Contains(keyword) || IsSqliteReplace(keyword, dialect)))
         {
            problems.Add(Problem(statement, keyword == null
               ? "This isn't a statement that changes data (INSERT, UPDATE, DELETE or MERGE)"
               : $"{keyword} statements can't run here: only those that change data (INSERT, UPDATE, DELETE and MERGE)"));
            continue;
         }
         if (Forbidden(statement, script.Text, dialect) is { } problem) { problems.Add(problem); }
      }
      return problems;
   }

   private static bool IsSqliteReplace(string keyword, SqlDialect dialect) => keyword == "REPLACE" && dialect == SqlDialect.Sqlite;

   /// <summary>A word or function in the statement that keeps it from running, if any.</summary>
   private static ScriptProblem? Forbidden(ScriptStatement statement, string text, SqlDialect dialect)
   {
      IReadOnlyList<ScriptToken> tokens = statement.Tokens;
      for (int i = 0; i < tokens.Count; i++)
      {
         ScriptToken token = tokens[i];
         if (token.Kind != ScriptTokenKind.Word) { continue; }
         string word = text[token.Start..token.End];
         bool call = i + 1 < tokens.Count && tokens[i + 1].Kind == ScriptTokenKind.Symbol && text[tokens[i + 1].Start] == '(';
         if (dialect == SqlDialect.SqlServer)
         {
            if (TSqlStatements.Contains(word)) { return Problem(token, $"{word.ToUpperInvariant()} can't be part of a statement that changes data"); }
            if (word.Equals("SELECT", StringComparison.OrdinalIgnoreCase) && SelectsInto(tokens, i, text) is { } into)
            {
               return Problem(into, "SELECT ... INTO makes a table: it can't be part of a statement that changes data");
            }
         }
         else if (call && dialect == SqlDialect.PostgreSql && PostgresFunctions.Contains(word) ||
                  call && dialect == SqlDialect.DuckDb && (word.StartsWith("read_", StringComparison.OrdinalIgnoreCase) || DuckDbFunctions.Contains(word)) ||
                  call && dialect == SqlDialect.Sqlite && word.Equals("load_extension", StringComparison.OrdinalIgnoreCase))
         {
            return Problem(token, $"{word}(...) reaches outside the database: it can't be part of a statement that changes data");
         }
      }
      return null;
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
