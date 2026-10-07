using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Types;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Dashboards;

namespace GalaxyData.Web.Queries;

/// <summary>
/// Names for the values of conditions written into a query: <c>$f1</c>, <c>$f2</c>, … but those the query has, or
/// its text (and anything else given) uses. The values are kept with the query's own.
/// </summary>
public sealed class QueryParameterAllocator
{
   private readonly string used;
   private int next;

   public QueryParameterAllocator(QueryParameters existing, string used)
   {
      ArgumentNullException.ThrowIfNull(existing);
      this.used = used ?? string.Empty;
      foreach (QueryParameter parameter in existing.All) { Values.Add(parameter.Name, parameter.Value, parameter.Type); }
   }

   /// <summary>The query's parameters and those added.</summary>
   public QueryParameters Values { get; } = new();

   public int Count => Values.All.Count();

   /// <summary>A new parameter of <paramref name="type"/> (not nullable), its name as written: <c>$f3</c>.</summary>
   public string Add(object? value, ScalarType type)
   {
      string name;
      do { name = "f" + (++next).ToString(CultureInfo.InvariantCulture); }
      while (Values.TryGet(name, out _) || used.Contains("$" + name, StringComparison.OrdinalIgnoreCase));
      Values.Add(name, value, type.AsNonNullable());
      return "$" + name;
   }
}

/// <summary>A condition to write: its op and values, as <c>ValueCodec</c> writes values of the column's type.</summary>
public sealed record ConditionSpec(ConditionOp Op, object? Value = null, object? ValueTo = null, IReadOnlyList<object?>? Values = null, RelativeRange? Relative = null)
{
   public static ConditionSpec Of(ConditionValue value)
   {
      ArgumentNullException.ThrowIfNull(value);
      return new(value.Op, value.Value, value.ValueTo, value.Values, value.Relative);
   }
}

/// <summary>
/// Writes a condition on a value (a column, or a member through navigations) as query text, each value a parameter
/// of the value's type. Text is matched ignoring case (<c>icontains</c>, <c>ilike</c> with its marks made plain);
/// "not" keeps rows without a value; a date alone against date-times is its whole day (in UTC for date-times with
/// offsets); lists are <c>in</c>; periods relative to today are ranges from the day they start to the day after they end.
/// What is wrong goes into <c>errors</c>, by field.
/// </summary>
public static class ConditionWriter
{
   /// <summary>
   /// The condition on <paramref name="target"/> (its text in the query) of <paramref name="type"/>, named
   /// <paramref name="displayName"/> in messages; empty when it adds nothing (not in an empty list); null when
   /// <paramref name="errors"/> says what is wrong. <paramref name="today"/> (UTC) is needed for relative periods.
   /// </summary>
   public static string? Write(string target, ScalarType type, string displayName, ConditionSpec condition, string field, QueryParameterAllocator parameters,
                               Dictionary<string, string[]> errors, DateOnly? today = null)
   {
      ArgumentNullException.ThrowIfNull(target);
      ArgumentNullException.ThrowIfNull(condition);
      ArgumentNullException.ThrowIfNull(parameters);
      ArgumentNullException.ThrowIfNull(errors);
      string name = target;
      bool text = type.Kind == ScalarKind.String;
      bool ordered = type.IsNumeric || type.IsTemporal || type.Kind is ScalarKind.String or ScalarKind.Interval;
      bool equatable = ordered || type.Kind is ScalarKind.Boolean or ScalarKind.Guid;
      bool days = type.Kind is ScalarKind.DateTime or ScalarKind.DateTimeOffset;
      bool allowed = condition.Op switch
      {
         ConditionOp.Blank or ConditionOp.NotBlank => true,
         ConditionOp.Eq or ConditionOp.Ne or ConditionOp.In or ConditionOp.NotIn => equatable,
         ConditionOp.Contains or ConditionOp.NotContains or ConditionOp.StartsWith or ConditionOp.EndsWith => text,
         ConditionOp.Relative => type.Kind is ScalarKind.Date or ScalarKind.DateTime or ScalarKind.DateTimeOffset,
         _ => ordered,
      };
      if (!allowed)
      {
         errors[field + ".op"] = [$"'{displayName}' is {TypeName(type)}, which can't be filtered by {Camel(condition.Op)}"];
         return null;
      }
      string Parameter(object? value) => parameters.Add(value, type);
      string From(DateOnly date) => parameters.Add(DayStart(date, type), type);
      switch (condition.Op)
      {
         case ConditionOp.Blank:
            return text ? $"({name} == null or {name} == '')" : $"{name} == null";
         case ConditionOp.NotBlank:
            return text ? $"({name} != null and {name} != '')" : $"{name} != null";
         case ConditionOp.Contains or ConditionOp.NotContains or ConditionOp.StartsWith or ConditionOp.EndsWith:
         {
            if (Value(condition.Value, type, field + ".value", errors) is not string find) { return null; }
            return condition.Op switch
            {
               ConditionOp.Contains => $"icontains({name}, {parameters.Add(find, ScalarType.Text())})",
               ConditionOp.NotContains => $"(not icontains({name}, {parameters.Add(find, ScalarType.Text())}) or {name} == null)",
               ConditionOp.StartsWith => $"ilike({name}, {parameters.Add(GridQueryComposer.Escape(find) + "%", ScalarType.Text())})",
               _ => $"ilike({name}, {parameters.Add("%" + GridQueryComposer.Escape(find), ScalarType.Text())})",
            };
         }
         case ConditionOp.In or ConditionOp.NotIn:
            return List(name, type, condition, field, parameters, errors);
         case ConditionOp.Relative:
         {
            if (condition.Relative is not { } period)
            {
               errors[field + ".relative"] = ["Give the period"];
               return null;
            }
            if (today is not { } day) { throw new InvalidOperationException("Relative periods need today's date"); }
            (DateOnly start, DateOnly? end) = Period(period, day);
            return end is { } until ? $"({name} >= {From(start)} and {name} < {From(until)})" : $"{name} >= {From(start)}";
         }
      }
      if (condition.Value is null || ValueCodec.Json(condition.Value) is not { ValueKind: not JsonValueKind.Null } given)
      {
         errors[field + ".value"] = [$"{Camel(condition.Op)} needs a value"];
         return null;
      }
      // A date alone compares with the days of date-times: 'on' a day is from its start to the next day's (when
      // there is one: the last day has none, and nothing is after it).
      if (days && ValueCodec.IsDateOnly(given, out DateOnly onDay))
      {
         string? After() => onDay == DateOnly.MaxValue ? null : From(onDay.AddDays(1));
         switch (condition.Op)
         {
            case ConditionOp.Eq:
            {
               string start = From(onDay);
               return After() is { } end ? $"({name} >= {start} and {name} < {end})" : $"{name} >= {start}";
            }
            case ConditionOp.Ne:
            {
               string start = From(onDay);
               return After() is { } end ? $"({name} < {start} or {name} >= {end} or {name} == null)" : $"({name} < {start} or {name} == null)";
            }
            case ConditionOp.Lt: return $"{name} < {From(onDay)}";
            case ConditionOp.Le: return After() is { } leEnd ? $"{name} < {leEnd}" : $"{name} != null";
            case ConditionOp.Gt: return After() is { } gtStart ? $"{name} >= {gtStart}" : "false";
            case ConditionOp.Ge: return $"{name} >= {From(onDay)}";
         }
      }
      if (!Decoded(given, type, field + ".value", errors, out object? value)) { return null; }
      switch (condition.Op)
      {
         case ConditionOp.Eq: return $"{name} == {Parameter(value)}";
         case ConditionOp.Ne: return $"({name} != {Parameter(value)} or {name} == null)";
         case ConditionOp.Lt: return $"{name} < {Parameter(value)}";
         case ConditionOp.Le: return $"{name} <= {Parameter(value)}";
         case ConditionOp.Gt: return $"{name} > {Parameter(value)}";
         case ConditionOp.Ge: return $"{name} >= {Parameter(value)}";
      }
      // Between, from the value to the value up to, both included; days of date-times whole.
      if (condition.ValueTo is null || ValueCodec.Json(condition.ValueTo) is not { ValueKind: not JsonValueKind.Null } to)
      {
         errors[field + ".valueTo"] = ["between needs a value up to"];
         return null;
      }
      string lower = days && ValueCodec.IsDateOnly(given, out DateOnly first) ? From(first) : Parameter(value);
      if (days && ValueCodec.IsDateOnly(to, out DateOnly last))
      {
         return last == DateOnly.MaxValue ? $"{name} >= {lower}" : $"({name} >= {lower} and {name} < {From(last.AddDays(1))})";
      }
      return Decoded(to, type, field + ".valueTo", errors, out object? upper) ? $"({name} >= {lower} and {name} <= {Parameter(upper)})" : null;
   }

   /// <summary>
   /// In (or not in) a list: <c>x in [$f1, $f2]</c>, with <c>x == null</c> for a null listed (a null in the list
   /// would never match); not in keeps the rows without a value unless null is listed. Dates alone against date-times
   /// are their days. Nothing listed: in is false, and not in adds nothing.
   /// </summary>
   private static string? List(string name, ScalarType type, ConditionSpec condition, string field, QueryParameterAllocator parameters, Dictionary<string, string[]> errors)
   {
      bool not = condition.Op == ConditionOp.NotIn;
      if (condition.Values is not { } values)
      {
         errors[field + ".values"] = ["Give the values"];
         return null;
      }
      bool nulls = false;
      List<string> plain = [];
      List<string> ranges = [];
      bool days = type.Kind is ScalarKind.DateTime or ScalarKind.DateTimeOffset;
      int before = errors.Count;
      for (int i = 0; i < values.Count; i++)
      {
         if (values[i] is null || ValueCodec.Json(values[i]) is not { ValueKind: not JsonValueKind.Null } given)
         {
            nulls = true;
            continue;
         }
         if (days && ValueCodec.IsDateOnly(given, out DateOnly day))
         {
            string start = parameters.Add(DayStart(day, type), type);
            ranges.Add(day == DateOnly.MaxValue ? $"{name} >= {start}" : $"({name} >= {start} and {name} < {parameters.Add(DayStart(day.AddDays(1), type), type)})");
            continue;
         }
         if (Decoded(given, type, $"{field}.values[{i}]", errors, out object? value)) { plain.Add(parameters.Add(value, type)); }
      }
      if (errors.Count > before) { return null; }
      List<string> parts = [];
      if (plain.Count > 0) { parts.Add($"{name} in [{string.Join(", ", plain)}]"); }
      parts.AddRange(ranges);
      if (!not)
      {
         if (nulls) { parts.Add($"{name} == null"); }
         return parts.Count switch
         {
            0 => "false",
            1 => parts[0],
            _ => "(" + string.Join(" or ", parts) + ")",
         };
      }
      if (parts.Count == 0) { return nulls ? $"{name} != null" : string.Empty; }
      string any = parts.Count == 1 ? parts[0] : "(" + string.Join(" or ", parts) + ")";
      return nulls ? $"({name} != null and not {Wrapped(any)})" : $"(not {Wrapped(any)} or {name} == null)";
   }

   /// <summary>The days a period relative to today covers: from its first, to the day after its last (none after the last day there is).</summary>
   public static (DateOnly Start, DateOnly? End) Period(RelativeRange period, DateOnly today)
   {
      ArgumentNullException.ThrowIfNull(period);
      DateOnly startOfThis = StartOf(today, period.Unit);
      DateOnly? next = Plus(startOfThis, period.Unit, 1);
      DateOnly? tomorrow = today == DateOnly.MaxValue ? null : today.AddDays(1);
      return period.Mode switch
      {
         // The last n days, weeks, …: up to today, from the day after today was, n of them ago.
         RelativeMode.Last => (Plus(today, period.Unit, -period.Count)?.AddDays(1) ?? DateOnly.MinValue, tomorrow),
         RelativeMode.This => (startOfThis, next),
         RelativeMode.Previous => (Plus(startOfThis, period.Unit, -period.Count) ?? DateOnly.MinValue, startOfThis),
         _ => (startOfThis, tomorrow),
      };
   }

   /// <summary>The first day of the period <paramref name="day"/> is in; weeks start on Monday.</summary>
   public static DateOnly StartOf(DateOnly day, RelativeUnit unit) => unit switch
   {
      RelativeUnit.Day => day,
      RelativeUnit.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
      RelativeUnit.Month => new DateOnly(day.Year, day.Month, 1),
      RelativeUnit.Quarter => new DateOnly(day.Year, ((day.Month - 1) / 3 * 3) + 1, 1),
      _ => new DateOnly(day.Year, 1, 1),
   };

   /// <summary><paramref name="day"/> moved by periods (a month from 31 January is 28 February); null beyond the days there are.</summary>
   public static DateOnly? Plus(DateOnly day, RelativeUnit unit, int count)
   {
      try
      {
         return unit switch
         {
            RelativeUnit.Day => day.AddDays(count),
            RelativeUnit.Week => day.AddDays(7 * count),
            RelativeUnit.Month => day.AddMonths(count),
            RelativeUnit.Quarter => day.AddMonths(3 * count),
            _ => day.AddYears(count),
         };
      }
      catch (ArgumentOutOfRangeException)
      {
         return null;
      }
   }

   /// <summary>The start of a day as a value of the type: a date, a date-time, or (days in UTC) a date-time with an offset.</summary>
   /// <remarks>Each boxed apart: a conditional of the two would make the date-time one with the machine's offset.</remarks>
   public static object DayStart(DateOnly day, ScalarType type)
   {
      return type.Kind switch
      {
         ScalarKind.DateTimeOffset => (object)new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
         ScalarKind.DateTime => (object)day.ToDateTime(TimeOnly.MinValue),
         _ => (object)day,
      };
   }

   private static string Wrapped(string condition) => condition.StartsWith('(') ? condition : "(" + condition + ")";

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

   private static string TypeName(ScalarType type) => type.WithNullable(false).ToString();

   private static string Camel(ConditionOp op) => JsonNamingPolicy.CamelCase.ConvertName(op.ToString());
}
