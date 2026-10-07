using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Language;
using GalaxyData.Query.Results;
using GalaxyData.Query.Types;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Queries;

namespace GalaxyData.Web.Browse;

/// <summary>
/// A grid's query: the text, its parameters, and where the user's where expression starts in it (to place what is
/// wrong with it), if there is one.
/// </summary>
public sealed record ComposedQuery(string Text, QueryParameters Parameters, int? WhereStart);

/// <summary>
/// Writes what a grid shows of a query's rows as query text: the filters (each value a parameter of its column's
/// type), the user's where expression, and the sort, added to the query by <see cref="QueryText.Compose"/>. Column
/// names are checked against the query's columns and quoted; the where expression must be one expression, a
/// condition rather than a statement. What is wrong goes into <c>errors</c>, by field (<c>grid.filters[0].column</c>).
/// </summary>
public static class GridQueryComposer
{
   private static readonly SyntaxKind[] Statements =
      [SyntaxKind.Block, SyntaxKind.Let, SyntaxKind.For, SyntaxKind.If, SyntaxKind.Break, SyntaxKind.Continue, SyntaxKind.Lambda, SyntaxKind.NamedArgument];

   /// <summary>The query with the grid's filters, where and sort; null when <paramref name="errors"/> says what is wrong.</summary>
   public static ComposedQuery? Compose(string text, QueryParameters parameters, IReadOnlyList<ResultColumn> columns, GridStateDto grid,
                                        Dictionary<string, string[]> errors)
   {
      ArgumentNullException.ThrowIfNull(text);
      ArgumentNullException.ThrowIfNull(parameters);
      ArgumentNullException.ThrowIfNull(columns);
      ArgumentNullException.ThrowIfNull(grid);
      ArgumentNullException.ThrowIfNull(errors);
      // Its start kept, so positions in it are the client's.
      string where = grid.Where?.TrimEnd() ?? string.Empty;
      // Names the query and the where expression don't use.
      QueryParameterAllocator allocator = new(parameters, text + " " + where);

      List<string> filters = [];
      for (int i = 0; i < (grid.Filters?.Count ?? 0); i++)
      {
         string field = $"grid.filters[{i}]";
         if (grid.Filters![i] is not { } filter)
         {
            errors[field] = ["A filter can't be null"];
            continue;
         }
         if (Column(columns, filter.Column, field + ".column", errors) is not { } column) { continue; }
         List<string> conditions = [];
         for (int j = 0; j < filter.Conditions.Count; j++)
         {
            if (filter.Conditions[j] is not { } given) { errors[$"{field}.conditions[{j}]"] = ["A condition can't be null"]; }
            else if (ConditionWriter.Write(QueryText.QuoteName(column.Name), column.Type, column.Name, Spec(given), $"{field}.conditions[{j}]", allocator, errors) is { } condition)
            {
               conditions.Add(condition);
            }
         }
         if (conditions.Count == 0) { continue; }
         filters.Add(conditions.Count == 1 ? conditions[0] : "(" + string.Join(filter.Any ? " or " : " and ", conditions) + ")");
      }

      List<QuerySortKey> sort = [];
      for (int i = 0; i < (grid.Sort?.Count ?? 0); i++)
      {
         if (grid.Sort![i] is not { } key)
         {
            errors[$"grid.sort[{i}]"] = ["A sort key can't be null"];
            continue;
         }
         if (Column(columns, key.Column, $"grid.sort[{i}].column", errors) is not { } column) { continue; }
         string expression = QueryText.QuoteName(column.Name);
         if (!sort.Any(s => s.Expression == expression)) { sort.Add(new QuerySortKey(expression, key.Desc)); }
      }

      string? wrapped = null;
      if (where.Trim().Length > 0)
      {
         if (WhereProblem(where) is { } problem) { errors["grid.where"] = [problem]; }
         // On a line of its own, so a comment at its end doesn't end the query too.
         else { wrapped = "(" + where + "\n)"; }
      }
      if (errors.Count > 0) { return null; }
      if (wrapped != null) { filters.Add(wrapped); }
      string composed = QueryText.Compose(text, filters, sort);
      int? whereStart = wrapped == null ? null : composed.LastIndexOf(".where(" + wrapped + ")", StringComparison.Ordinal) + ".where((".Length;
      return new ComposedQuery(composed, allocator.Values, whereStart);
   }

   /// <summary>Why the where expression can't be one; null when it can.</summary>
   public static string? WhereProblem(string where)
   {
      ArgumentNullException.ThrowIfNull(where);
      int statements = QueryText.SplitStatements(where).Count;
      if (statements == 0) { return "Write a condition over the rows, such as total > 100"; }
      if (statements > 1) { return "Write one condition, without ';'"; }
      ParseResult parsed = QueryParser.Default.Parse(where);
      if (!parsed.Success) { return $"{parsed.Diagnostics[0].Message} (at {parsed.Diagnostics[0].Start + 1})"; }
      SyntaxNode root = parsed.Root!;
      if (Statements.Contains(root.Kind) || root is BinarySyntax { Op: ":=" or "=" })
      {
         return "Write a condition over the rows, such as total > 100, not a statement";
      }
      return null;
   }

   /// <summary>The column named so, or the one named so but for case.</summary>
   private static ResultColumn? Column(IReadOnlyList<ResultColumn> columns, string name, string field, Dictionary<string, string[]> errors)
   {
      if (columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal)) is { } exact) { return exact; }
      List<ResultColumn> found = columns.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
      if (found.Count == 1) { return found[0]; }
      errors[field] = [found.Count == 0
         ? $"The rows have no column '{name}'"
         : $"'{name}' names more than one column but for case: {string.Join(", ", found.Select(c => c.Name))}"];
      return null;
   }

   /// <summary>A grid's condition as a condition to write: the grid's ops are among the writer's, by name.</summary>
   private static ConditionSpec Spec(GridConditionDto condition) => new(Enum.Parse<ConditionOp>(condition.Op.ToString()), condition.Value, condition.ValueTo);

   /// <summary>Text matched as it is in an <c>ilike</c> pattern: its <c>%</c>, <c>_</c> and <c>\</c> made plain.</summary>
   public static string Escape(string text)
   {
      ArgumentNullException.ThrowIfNull(text);
      return text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
   }
}
