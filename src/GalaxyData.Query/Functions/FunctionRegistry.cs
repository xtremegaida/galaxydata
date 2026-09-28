using System;
using System.Collections.Generic;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Functions;

/// <summary>
/// The scalar functions of the language. Every one of them must be implementable in DuckDB, the merge engine,
/// so an operator whose function a source can't run can always move there. Names match case-insensitively.
/// </summary>
public sealed class FunctionRegistry
{
   private readonly Dictionary<string, FunctionDef> byName = new(StringComparer.OrdinalIgnoreCase);

   public static FunctionRegistry Default { get; } = CreateDefault();

   public IEnumerable<FunctionDef> Functions => byName.Values;

   public bool TryGet(string name, out FunctionDef function) => byName.TryGetValue(name, out function!);

   private void Add(FunctionDef function) => byName.Add(function.Name, function);

   private static FunctionRegistry CreateDefault()
   {
      FunctionRegistry r = new();

      // Text
      foreach ((FunctionId id, string name) in new[] { (FunctionId.Lower, "lower"), (FunctionId.Upper, "upper"), (FunctionId.Trim, "trim"), (FunctionId.LTrim, "ltrim"), (FunctionId.RTrim, "rtrim") })
      {
         r.Add(new(id, name, $"{name}(text)", 1, 1, a => { Text(a, 0); return Nullable(ScalarType.Text(), a); }));
      }
      r.Add(new(FunctionId.Length, "length", "length(text)", 1, 1, a => { Text(a, 0); return Nullable(ScalarType.Int32, a); }));
      r.Add(new(FunctionId.Substring, "substring", "substring(text, start[, length]) — start counts from 1", 2, 3, a =>
      {
         Text(a, 0);
         for (int i = 1; i < a.Count; i++) { Integer(a, i); }
         return Nullable(ScalarType.Text(), a);
      }));
      r.Add(new(FunctionId.IndexOf, "indexOf", "indexOf(text, find) — 1-based, 0 when absent", 2, 2, a => { Text(a, 0); Text(a, 1); return Nullable(ScalarType.Int32, a); }));
      r.Add(new(FunctionId.Replace, "replace", "replace(text, find, replacement)", 3, 3, a => { Text(a, 0); Text(a, 1); Text(a, 2); return Nullable(ScalarType.Text(), a); }));
      r.Add(new(FunctionId.Left, "left", "left(text, count)", 2, 2, a => { Text(a, 0); Integer(a, 1); return Nullable(ScalarType.Text(), a); }));
      r.Add(new(FunctionId.Right, "right", "right(text, count)", 2, 2, a => { Text(a, 0); Integer(a, 1); return Nullable(ScalarType.Text(), a); }));
      foreach ((FunctionId id, string name) in new[]
               {
                  (FunctionId.StartsWith, "startsWith"), (FunctionId.EndsWith, "endsWith"), (FunctionId.Contains, "contains"),
                  (FunctionId.IContains, "icontains"), (FunctionId.Like, "like"), (FunctionId.ILike, "ilike"),
               })
      {
         string second = id is FunctionId.Like or FunctionId.ILike ? "pattern" : "find";
         r.Add(new(id, name, $"{name}(text, {second})", 2, 2, a => { Text(a, 0); Text(a, 1); return Nullable(ScalarType.Boolean, a); }));
      }
      r.Add(new(FunctionId.Concat, "concat", "concat(value, value, ...) — nulls count as empty text", 2, -1, a =>
      {
         for (int i = 0; i < a.Count; i++)
         {
            if (a[i].Kind is ScalarKind.Binary or ScalarKind.Unknown && !a.IsNullLiteral(i)) { throw a.Fail(i, "concat joins text, numbers, dates and the like; convert this first with toString(...)"); }
         }
         return ScalarType.Text().AsNonNullable();
      }));

      // Numbers
      r.Add(new(FunctionId.Abs, "abs", "abs(number)", 1, 1, a => { Numeric(a, 0); return a[0]; }));
      r.Add(new(FunctionId.Round, "round", "round(number[, digits])", 1, 2, a =>
      {
         Numeric(a, 0);
         if (a.Count == 2) { Integer(a, 1); }
         return a[0].WithNullable(a.AnyNullable);
      }));
      r.Add(new(FunctionId.Floor, "floor", "floor(number)", 1, 1, a => { Numeric(a, 0); return a[0]; }));
      r.Add(new(FunctionId.Ceiling, "ceiling", "ceiling(number)", 1, 1, a => { Numeric(a, 0); return a[0]; }));
      r.Add(new(FunctionId.Power, "power", "power(base, exponent)", 2, 2, a => { Numeric(a, 0); Numeric(a, 1); return Nullable(ScalarType.Double, a); }));
      r.Add(new(FunctionId.Sqrt, "sqrt", "sqrt(number)", 1, 1, a => { Numeric(a, 0); return Nullable(ScalarType.Double, a); }));
      r.Add(new(FunctionId.Sign, "sign", "sign(number) — -1, 0 or 1", 1, 1, a => { Numeric(a, 0); return Nullable(ScalarType.Int32, a); }));

      // Dates and times
      foreach ((FunctionId id, string name) in new[] { (FunctionId.Year, "year"), (FunctionId.Month, "month"), (FunctionId.Day, "day") })
      {
         r.Add(new(id, name, $"{name}(date)", 1, 1, a => { DateLike(a, 0); return Nullable(ScalarType.Int32, a); }));
      }
      foreach ((FunctionId id, string name) in new[] { (FunctionId.Hour, "hour"), (FunctionId.Minute, "minute"), (FunctionId.Second, "second") })
      {
         r.Add(new(id, name, $"{name}(time)", 1, 1, a =>
         {
            Expect(a, 0, t => TypeRules.HasTimeOfDay(t.Kind), "a time or date-time");
            return Nullable(ScalarType.Int32, a);
         }));
      }
      r.Add(new(FunctionId.Date, "date", "date(dateTime) — the date part", 1, 1, a => { DateLike(a, 0); return Nullable(ScalarType.Date, a); }));
      r.Add(new(FunctionId.Now, "now", "now() — the current UTC date-time, the same for the whole query", 0, 0, _ => ScalarType.DateTime.AsNonNullable(), deterministic: false));
      r.Add(new(FunctionId.Today, "today", "today() — the current UTC date", 0, 0, _ => ScalarType.Date.AsNonNullable(), deterministic: false));
      r.Add(new(FunctionId.AddDays, "addDays", "addDays(date, days)", 2, 2, a => { DateLike(a, 0); Integer(a, 1); return a[0].WithNullable(a.AnyNullable); }));
      r.Add(new(FunctionId.AddMonths, "addMonths", "addMonths(date, months)", 2, 2, a => { DateLike(a, 0); Integer(a, 1); return a[0].WithNullable(a.AnyNullable); }));
      r.Add(new(FunctionId.DaysBetween, "daysBetween", "daysBetween(from, to)", 2, 2, a => { DateLike(a, 0); DateLike(a, 1); return Nullable(ScalarType.Int32, a); }));

      // Nulls and conditions
      r.Add(new(FunctionId.Coalesce, "coalesce", "coalesce(value, fallback, ...)", 2, -1, a =>
      {
         ScalarType result = Common(a, 0, a.Count);
         bool nullable = true;
         for (int i = 0; i < a.Count; i++) { nullable &= a[i].Nullable; }
         return result.WithNullable(nullable);
      }));
      r.Add(new(FunctionId.NullIf, "nullif", "nullif(value, sameAs) — null when the two are equal", 2, 2, a =>
      {
         a.Adopt(1, a[0]);
         Comparable(a, 0, 1, ordering: false);
         return a[0].AsNullable();
      }));
      r.Add(new(FunctionId.Iif, "iif", "iif(condition, whenTrue, whenFalse)", 3, 3, a =>
      {
         Boolean(a, 0);
         return Common(a, 1, 3);
      }));
      r.Add(new(FunctionId.Between, "between", "between(value, low, high) — both ends included", 3, 3, a =>
      {
         a.Adopt(1, a[0]);
         a.Adopt(2, a[0]);
         Comparable(a, 0, 1, ordering: true);
         Comparable(a, 0, 2, ordering: true);
         return Nullable(ScalarType.Boolean, a);
      }));

      // Conversions
      r.Add(new(FunctionId.ToInt, "toInt", "toInt(value)", 1, 1, a => { Convertible(a, 0, numberish: true); return Nullable(ScalarType.Int32, a); }));
      r.Add(new(FunctionId.ToLong, "toLong", "toLong(value)", 1, 1, a => { Convertible(a, 0, numberish: true); return Nullable(ScalarType.Int64, a); }));
      r.Add(new(FunctionId.ToDouble, "toDouble", "toDouble(value)", 1, 1, a => { Convertible(a, 0, numberish: true); return Nullable(ScalarType.Double, a); }));
      r.Add(new(FunctionId.ToDecimal, "toDecimal", "toDecimal(value[, precision, scale])", 1, 3, a =>
      {
         Convertible(a, 0, numberish: true);
         if (a.Count == 2) { throw a.Fail("give both precision and scale, e.g. toDecimal(x, 10, 2)"); }
         if (a.Count == 1) { return Nullable(ScalarType.Decimal(), a); }
         int precision = ConstantInteger(a, 1, 1, 38);
         int scale = ConstantInteger(a, 2, 0, precision);
         return ScalarType.Decimal(precision, scale).WithNullable(a[0].Nullable);
      }));
      r.Add(new(FunctionId.ToText, "toString", "toString(value)", 1, 1, a => ScalarType.Text().WithNullable(a[0].Nullable)));
      r.Add(new(FunctionId.ToDate, "toDate", "toDate(value)", 1, 1, a =>
      {
         Expect(a, 0, t => t.Kind == ScalarKind.String || TypeRules.IsDateLike(t.Kind), "text or a date");
         return Nullable(ScalarType.Date, a);
      }));
      r.Add(new(FunctionId.ToDateTime, "toDateTime", "toDateTime(value)", 1, 1, a =>
      {
         Expect(a, 0, t => t.Kind == ScalarKind.String || TypeRules.IsDateLike(t.Kind), "text or a date");
         return Nullable(ScalarType.DateTime, a);
      }));
      r.Add(new(FunctionId.ToBool, "toBool", "toBool(value)", 1, 1, a =>
      {
         Expect(a, 0, t => t.Kind is ScalarKind.Boolean or ScalarKind.String || t.IsNumeric, "a true/false value, a number or text");
         return Nullable(ScalarType.Boolean, a);
      }));
      return r;
   }

   private static ScalarType Nullable(ScalarType type, FunctionArguments a) => type.WithNullable(a.AnyNullable);

   private static void Expect(FunctionArguments a, int i, Func<ScalarType, bool> accepts, string what)
   {
      if (a.IsNullLiteral(i) || accepts(a[i])) { return; }
      throw a.Fail(i, $"expected {what} here, but this is {TypeRules.Describe(a[i])}");
   }

   private static void Text(FunctionArguments a, int i)
   {
      a.Adopt(i, ScalarType.Text());
      Expect(a, i, t => t.Kind == ScalarKind.String, "text");
   }

   private static void Numeric(FunctionArguments a, int i) => Expect(a, i, t => t.IsNumeric, "a number");

   private static void Integer(FunctionArguments a, int i)
   {
      a.Adopt(i, ScalarType.Int32);
      Expect(a, i, t => t.IsInteger, "a whole number");
   }

   private static void Boolean(FunctionArguments a, int i) => Expect(a, i, t => t.Kind == ScalarKind.Boolean, "a true/false condition");

   private static void DateLike(FunctionArguments a, int i) => Expect(a, i, t => TypeRules.IsDateLike(t.Kind), "a date or date-time");

   private static void Convertible(FunctionArguments a, int i, bool numberish) =>
      Expect(a, i, t => t.IsNumeric || t.Kind is ScalarKind.String or ScalarKind.Boolean || (!numberish && TypeRules.IsDateLike(t.Kind)),
         numberish ? "a number, text or true/false value" : "a number, text or date");

   private static void Comparable(FunctionArguments a, int left, int right, bool ordering)
   {
      if (a.IsNullLiteral(left) || a.IsNullLiteral(right)) { return; }
      if (!TypeRules.AreComparable(a[left], a[right], ordering, out string? reason)) { throw a.Fail(right, reason!); }
   }

   private static int ConstantInteger(FunctionArguments a, int i, int min, int max)
   {
      if (a.Expr(i) is BoundLiteral { Value: long value } && value >= min && value <= max) { return (int)value; }
      throw a.Fail(i, $"expected a whole number from {min} to {max} written in the query");
   }

   /// <summary>The common type of arguments [from, to); constants are converted to it.</summary>
   private static ScalarType Common(FunctionArguments a, int from, int to)
   {
      ScalarType common = a[from];
      for (int i = from + 1; i < to; i++)
      {
         ScalarType? unified = TypeRules.Unify(common, a[i]);
         if (unified == null) { throw a.Fail(i, $"{TypeRules.Describe(a[i])} doesn't mix with {TypeRules.Describe(common)} here"); }
         common = unified.Value;
      }
      if (common.Kind != ScalarKind.Unknown)
      {
         for (int i = from; i < to; i++) { a.Adopt(i, common); }
      }
      return common;
   }
}
