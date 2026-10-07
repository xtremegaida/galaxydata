using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace GalaxyData.Query.Dml;

/// <summary>
/// What a dialect's edited scripts may hold beyond the standard data changes, and what keeps a statement from
/// running (see <see cref="DmlGuard"/>): words that start other statements or run code, and functions that reach
/// outside the database. Words and functions compare ignoring case.
/// </summary>
public sealed record ChangeScriptRules
{
   /// <summary>No more changes, and nothing forbidden but what every dialect forbids.</summary>
   public static ChangeScriptRules Standard { get; } = new();

   /// <summary>
   /// Words, in upper case, other than <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> and <c>MERGE</c> that start a
   /// statement changing data, and the change each makes: SQLite's <c>REPLACE</c> inserts.
   /// </summary>
   public IReadOnlyDictionary<string, DmlStatementKind> MoreChanges { get; init; } = FrozenDictionary<string, DmlStatementKind>.Empty;

   /// <summary>
   /// Words that, unquoted anywhere in a statement, keep it from running: where statements needn't end with
   /// <c>;</c> (SQL Server), those that start other statements or run code.
   /// </summary>
   public IReadOnlySet<string> ForbiddenWords { get; init; } = FrozenSet<string>.Empty;

   /// <summary>Whether <c>SELECT ... INTO</c>, which makes a table, keeps a statement from running (SQL Server).</summary>
   public bool ForbidsSelectInto { get; init; }

   /// <summary>Functions that reach outside the database, by their names quoted or not: PostgreSQL's <c>pg_read_file</c>.</summary>
   public IReadOnlySet<string> ForbiddenFunctions { get; init; } = FrozenSet<string>.Empty;

   /// <summary>Beginnings of the names of such functions: DuckDB's <c>read_</c>.</summary>
   public IReadOnlyList<string> ForbiddenFunctionPrefixes { get; init; } = [];

   /// <summary>Whether a call of <paramref name="name"/> reaches outside the database.</summary>
   internal bool IsForbiddenFunction(string name)
   {
      if (ForbiddenFunctions.Contains(name)) { return true; }
      foreach (string prefix in ForbiddenFunctionPrefixes)
      {
         if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { return true; }
      }
      return false;
   }

   /// <summary>A set of words that compares ignoring case, as the rules' sets do.</summary>
   public static IReadOnlySet<string> Words(params string[] words) => words.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// How a dialect writes the parts of a script that <see cref="SqlScriptSplitter"/> must step over to find where its
/// statements end: quoted names and strings, comments, and batch separators. Every dialect has <c>'strings'</c>,
/// <c>"names"</c>, <c>--</c> and <c>/* */</c> comments.
/// </summary>
public sealed record ScriptSyntax
{
   /// <summary>Only what every dialect has, and nested block comments.</summary>
   public static ScriptSyntax Standard { get; } = new();

   /// <summary>Names in brackets: <c>[name]</c> (SQL Server, SQLite).</summary>
   public bool BracketNames { get; init; }

   /// <summary>Names in backticks: <c>`name`</c> (SQLite).</summary>
   public bool BacktickNames { get; init; }

   /// <summary>Dollar-quoted strings: <c>$tag$...$tag$</c> (PostgreSQL, DuckDB).</summary>
   public bool DollarQuotes { get; init; }

   /// <summary>Strings with backslash escapes, written <c>E'...'</c> (PostgreSQL, DuckDB).</summary>
   public bool EscapeStrings { get; init; }

   /// <summary>Whether a block comment may hold others (all but SQLite's).</summary>
   public bool NestedComments { get; init; } = true;

   /// <summary><c>GO</c> on a line of its own ends a batch (SQL Server).</summary>
   public bool BatchSeparator { get; init; }

   /// <summary>A <c>CREATE TRIGGER</c>'s body (<c>BEGIN ... END</c>) keeps the statements in it (SQLite).</summary>
   public bool TriggerBodies { get; init; }
}
