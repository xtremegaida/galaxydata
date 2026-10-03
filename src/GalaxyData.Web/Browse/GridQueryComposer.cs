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
      QueryParameters values = new();
      foreach (QueryParameter parameter in parameters.All) { values.Add(parameter.Name, parameter.Value, parameter.Type); }
      // Its start kept, so positions in it are the client's.
      string where = grid.Where?.TrimEnd() ?? string.Empty;
      // Names the query and the where expression don't use.
      string used = text + " " + where;
      int next = 0;
      string Parameter(object? value, ScalarType type)
      {
         string name;
         do { name = "f" + (++next).ToString(CultureInfo.InvariantCulture); }
         while (values.TryGet(name, out _) || used.Contains("$" + name, StringComparison.OrdinalIgnoreCase));
         values.Add(name, value, type.AsNonNullable());
         return "$" + name;
      }

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
            else if (Condition(column, given, $"{field}.conditions[{j}]", Parameter, errors) is { } condition) { conditions.Add(condition); }
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
      return new ComposedQuery(composed, values, whereStart);
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

   private static string? Condition(ResultColumn column, GridConditionDto condition, string field, Func<object?, ScalarType, string> parameter,
                                    Dictionary<string, string[]> errors)
   {
      ScalarType type = column.Type;
      string name = QueryText.QuoteName(column.Name);
      bool text = type.Kind == ScalarKind.String;
      bool ordered = type.IsNumeric || type.IsTemporal || type.Kind is ScalarKind.String or ScalarKind.Interval;
      bool equatable = ordered || type.Kind is ScalarKind.Boolean or ScalarKind.Guid;
      bool allowed = condition.Op switch
      {
         GridOp.Blank or GridOp.NotBlank => true,
         GridOp.Eq or GridOp.Ne => equatable,
         GridOp.Contains or GridOp.NotContains or GridOp.StartsWith or GridOp.EndsWith => text,
         _ => ordered,
      };
      if (!allowed)
      {
         errors[field + ".op"] = [$"'{column.Name}' is {TypeName(type)}, which can't be filtered by {Camel(condition.Op)}"];
         return null;
      }
      switch (condition.Op)
      {
         case GridOp.Blank:
            return text ? $"({name} == null or {name} == '')" : $"{name} == null";
         case GridOp.NotBlank:
            return text ? $"({name} != null and {name} != '')" : $"{name} != null";
         case GridOp.Contains or GridOp.NotContains or GridOp.StartsWith or GridOp.EndsWith:
         {
            if (Value(condition.Value, type, field + ".value", errors) is not string find) { return null; }
            return condition.Op switch
            {
               GridOp.Contains => $"icontains({name}, {parameter(find, ScalarType.Text())})",
               GridOp.NotContains => $"(not icontains({name}, {parameter(find, ScalarType.Text())}) or {name} == null)",
               GridOp.StartsWith => $"ilike({name}, {parameter(Escape(find) + "%", ScalarType.Text())})",
               _ => $"ilike({name}, {parameter("%" + Escape(find), ScalarType.Text())})",
            };
         }
      }
      if (condition.Value is null || ValueCodec.Json(condition.Value) is not { ValueKind: not JsonValueKind.Null } given)
      {
         errors[field + ".value"] = [$"{Camel(condition.Op)} needs a value"];
         return null;
      }
      // A date alone compares with the days of date-times: 'on' a day is from its start to the next day's (when
      // there is one: the last day has none, and nothing is after it).
      if (type.Kind is ScalarKind.DateTime or ScalarKind.DateTimeOffset && ValueCodec.IsDateOnly(given, out DateOnly day))
      {
         string From(DateOnly date) => parameter(DayStart(date, type), type);
         string? After() => day == DateOnly.MaxValue ? null : From(day.AddDays(1));
         switch (condition.Op)
         {
            case GridOp.Eq:
            {
               string start = From(day);
               return After() is { } end ? $"({name} >= {start} and {name} < {end})" : $"{name} >= {start}";
            }
            case GridOp.Ne:
            {
               string start = From(day);
               return After() is { } end ? $"({name} < {start} or {name} >= {end} or {name} == null)" : $"({name} < {start} or {name} == null)";
            }
            case GridOp.Lt: return $"{name} < {From(day)}";
            case GridOp.Le: return After() is { } leEnd ? $"{name} < {leEnd}" : $"{name} != null";
            case GridOp.Gt: return After() is { } gtStart ? $"{name} >= {gtStart}" : "false";
            case GridOp.Ge: return $"{name} >= {From(day)}";
         }
      }
      if (!Decoded(given, type, field + ".value", errors, out object? value)) { return null; }
      switch (condition.Op)
      {
         case GridOp.Eq: return $"{name} == {parameter(value, type)}";
         case GridOp.Ne: return $"({name} != {parameter(value, type)} or {name} == null)";
         case GridOp.Lt: return $"{name} < {parameter(value, type)}";
         case GridOp.Le: return $"{name} <= {parameter(value, type)}";
         case GridOp.Gt: return $"{name} > {parameter(value, type)}";
         case GridOp.Ge: return $"{name} >= {parameter(value, type)}";
      }
      // Between, from the value to the value up to, both included; days of date-times whole.
      if (condition.ValueTo is null || ValueCodec.Json(condition.ValueTo) is not { ValueKind: not JsonValueKind.Null } to)
      {
         errors[field + ".valueTo"] = ["between needs a value up to"];
         return null;
      }
      bool days = type.Kind is ScalarKind.DateTime or ScalarKind.DateTimeOffset;
      string lower = days && ValueCodec.IsDateOnly(given, out DateOnly first) ? parameter(DayStart(first, type), type) : parameter(value, type);
      if (days && ValueCodec.IsDateOnly(to, out DateOnly last))
      {
         return last == DateOnly.MaxValue ? $"{name} >= {lower}" : $"({name} >= {lower} and {name} < {parameter(DayStart(last.AddDays(1), type), type)})";
      }
      return Decoded(to, type, field + ".valueTo", errors, out object? upper) ? $"({name} >= {lower} and {name} <= {parameter(upper, type)})" : null;
   }

   private static object? Value(object? value, ScalarType type, string field, Dictionary<string, string[]> errors)
   {
      if (value is null || ValueCodec.Json(value) is not { ValueKind: not JsonValueKind.Null } given)
      {
         errors[field] = ["The condition needs a value"];
         return null;
      }
      return Decoded(given, type, field, errors, out object? decoded) ? decoded : null;
   }

   private static bool Decoded(JsonElement value, ScalarType type, string field, Dictionary<string, string[]> errors, out object? decoded)
   {
      try
      {
         decoded = ValueCodec.Decode(value, type);
         return true;
      }
      catch (ValueFormatException e)
      {
         errors[field] = [e.Message];
         decoded = null;
         return false;
      }
   }

   /// <summary>The start of a day as a value of the column's type: a date-time, or (days in UTC) a date-time with an offset.</summary>
   /// <remarks>Each boxed apart: a conditional of the two would make the date-time one with the machine's offset.</remarks>
   private static object DayStart(DateOnly day, ScalarType type) =>
      type.Kind == ScalarKind.DateTimeOffset
         ? (object)new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
         : (object)day.ToDateTime(TimeOnly.MinValue);

   /// <summary>Text matched as it is in an <c>ilike</c> pattern: its <c>%</c>, <c>_</c> and <c>\</c> made plain.</summary>
   public static string Escape(string text)
   {
      ArgumentNullException.ThrowIfNull(text);
      return text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
   }

   private static string TypeName(ScalarType type) => type.WithNullable(false).ToString();

   private static string Camel(GridOp op) => JsonNamingPolicy.CamelCase.ConvertName(op.ToString());
}
