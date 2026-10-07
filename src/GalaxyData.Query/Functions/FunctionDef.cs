using System;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Functions;

public enum FunctionId
{
   Lower,
   Upper,
   Trim,
   LTrim,
   RTrim,
   Length,
   Substring,
   IndexOf,
   Replace,
   Left,
   Right,
   StartsWith,
   EndsWith,
   Contains,
   IContains,
   Like,
   ILike,
   Concat,
   Abs,
   Round,
   Floor,
   Ceiling,
   Power,
   Sqrt,
   Sign,
   Year,
   Month,
   Day,
   Hour,
   Minute,
   Second,
   Date,
   Now,
   Today,
   AddDays,
   AddMonths,
   DaysBetween,
   Coalesce,
   NullIf,
   Iif,
   Between,
   ToInt,
   ToLong,
   ToDecimal,
   ToDouble,
   ToText,
   ToDate,
   ToDateTime,
   ToBool,
   StartOfWeek,
   StartOfMonth,
   StartOfQuarter,
   StartOfYear,
   Quarter,
   DayOfWeek,
}

/// <summary>The arguments of a call being type-checked; failures throw, so a checker just returns the result type.</summary>
public abstract class FunctionArguments
{
   public abstract int Count { get; }

   /// <summary>The scalar type of argument <paramref name="index"/>.</summary>
   public abstract ScalarType this[int index] { get; }

   public abstract BoundExpr Expr(int index);

   /// <summary>Converts a constant or parameter argument to <paramref name="target"/> when that loses nothing.</summary>
   public abstract void Adopt(int index, ScalarType target);

   public abstract Exception Fail(int index, string message);

   public abstract Exception Fail(string message);

   public bool IsNullLiteral(int index) => Expr(index) is BoundLiteral { Value: null };

   public bool AnyNullable
   {
      get
      {
         for (int i = 0; i < Count; i++)
         {
            if (this[i].Nullable) { return true; }
         }
         return false;
      }
   }
}

public sealed class FunctionDef
{
   internal FunctionDef(FunctionId id, string name, string signature, int minArguments, int maxArguments,
                        Func<FunctionArguments, ScalarType> check, bool deterministic = true)
   {
      Id = id;
      Name = name;
      Signature = signature;
      MinArguments = minArguments;
      MaxArguments = maxArguments;
      Check = check;
      IsDeterministic = deterministic;
   }

   public FunctionId Id { get; }

   public string Name { get; }

   /// <summary>How the function is written, for messages: <c>substring(text, start[, length])</c>.</summary>
   public string Signature { get; }

   public int MinArguments { get; }

   /// <summary>The most arguments it takes; -1 for any number.</summary>
   public int MaxArguments { get; }

   public bool IsDeterministic { get; }

   internal Func<FunctionArguments, ScalarType> Check { get; }

   public override string ToString() => Signature;
}
