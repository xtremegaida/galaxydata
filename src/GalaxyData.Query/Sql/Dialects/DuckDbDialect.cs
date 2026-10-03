using System.Text;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Sql;

/// <summary>
/// DuckDB, which is also the merge engine, so every language function must translate here. It sorts nulls last
/// in both directions unless told otherwise, and has native string predicates (<c>starts_with</c>, <c>contains</c>).
/// </summary>
internal sealed class DuckDbDialect : SqlDialect
{
   public override string Name => "DuckDB";

   public override string ProviderKind => "duckdb";

   public override int MaxParameters => 65535;

   public override string Placeholder(string name) => "$" + name;

   internal override bool SharesParameters => false;

   internal override string TypeName(ScalarType type) => type.Kind switch
   {
      ScalarKind.Boolean => "BOOLEAN",
      ScalarKind.Int16 => "SMALLINT",
      ScalarKind.Int32 => "INTEGER",
      ScalarKind.Int64 => "BIGINT",
      ScalarKind.Decimal => type.Precision > 0 ? $"DECIMAL({type.Precision},{type.Scale})" : "DECIMAL(38,10)",
      ScalarKind.Single => "REAL",
      ScalarKind.Double => "DOUBLE",
      ScalarKind.Binary => "BLOB",
      ScalarKind.Guid => "UUID",
      ScalarKind.Date => "DATE",
      ScalarKind.Time => "TIME",
      ScalarKind.DateTime => "TIMESTAMP",
      ScalarKind.DateTimeOffset => "TIMESTAMPTZ",
      ScalarKind.Interval => "INTERVAL",
      ScalarKind.Json => "JSON",
      _ => "VARCHAR",
   };

   /// <summary>
   /// A date-time compared with a date-time with an offset is taken as UTC: DuckDB compares TIMESTAMP with
   /// TIMESTAMPTZ, but not TIMESTAMP_NS (which DuckDB sources may have), without a cast.
   /// </summary>
   internal override SqlExpr Compare(SqlBinaryOp op, SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType)
   {
      if (leftType.Kind == ScalarKind.DateTime && rightType.Kind == ScalarKind.DateTimeOffset) { left = new SqlCast(left, TypeName(rightType)); }
      else if (leftType.Kind == ScalarKind.DateTimeOffset && rightType.Kind == ScalarKind.DateTime) { right = new SqlCast(right, TypeName(leftType)); }
      return new SqlBinary(op, left, right);
   }

   private protected override void WriteBinary(StringBuilder text, byte[] value)
   {
      text.Append("CAST('");
      foreach (byte b in value) { text.Append("\\x").Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)); }
      text.Append("' AS BLOB)");
   }

   internal override SqlExpr? Function(SqlCall c) => c.Id switch
   {
      FunctionId.Lower => Call("lower", c.Arg(0)),
      FunctionId.Upper => Call("upper", c.Arg(0)),
      FunctionId.Trim => Call("trim", c.Arg(0)),
      FunctionId.LTrim => Call("ltrim", c.Arg(0)),
      FunctionId.RTrim => Call("rtrim", c.Arg(0)),
      FunctionId.Length => Call("length", c.Arg(0)),
      FunctionId.Substring => Call("substring", TextThenInts(c)),
      FunctionId.IndexOf => Call("strpos", c.Arg(0), c.Arg(1)),
      FunctionId.Replace => Call("replace", c.Args()),
      FunctionId.Left => Call("left", c.Arg(0), Int(c, 1)),
      FunctionId.Right => Call("right", c.Arg(0), Int(c, 1)),
      FunctionId.StartsWith => Call("starts_with", c.Arg(0), c.Arg(1)),
      FunctionId.EndsWith => Call("ends_with", c.Arg(0), c.Arg(1)),
      FunctionId.Contains => Call("contains", c.Arg(0), c.Arg(1)),
      FunctionId.IContains => Call("contains", Call("lower", c.Arg(0)), Call("lower", c.Arg(1))),
      FunctionId.Like => Like(c.Arg(0), c.Arg(1)),
      FunctionId.ILike => Like(c.Arg(0), c.Arg(1), caseInsensitive: true),
      FunctionId.Concat => Call("concat", ConcatArguments(c)),
      FunctionId.Abs => Call("abs", c.Arg(0)),
      FunctionId.Round => IsInteger(c.Type(0)) ? c.Arg(0) : Call("round", TextThenInts(c)),
      FunctionId.Floor => IsInteger(c.Type(0)) ? c.Arg(0) : Call("floor", c.Arg(0)),
      FunctionId.Ceiling => IsInteger(c.Type(0)) ? c.Arg(0) : Call("ceiling", c.Arg(0)),
      FunctionId.Power => Call("power", c.Arg(0), c.Arg(1)),
      FunctionId.Sqrt => Call("sqrt", c.Arg(0)),
      FunctionId.Sign => Cast(Call("sign", c.Arg(0)), ScalarType.Int32),
      FunctionId.Year => Call("year", c.Arg(0)),
      FunctionId.Month => Call("month", c.Arg(0)),
      FunctionId.Day => Call("day", c.Arg(0)),
      FunctionId.Hour => Call("hour", c.Arg(0)),
      FunctionId.Minute => Call("minute", c.Arg(0)),
      FunctionId.Second => Call("second", c.Arg(0)),
      FunctionId.Date => Cast(c.Arg(0), ScalarType.Date),
      FunctionId.AddDays => c.Type(0).Kind == ScalarKind.Date
         ? Binary(SqlBinaryOp.Add, c.Arg(0), Int(c, 1))
         : Binary(SqlBinaryOp.Add, c.Arg(0), Call("to_days", Int(c, 1))),
      FunctionId.AddMonths => c.Type(0).Kind == ScalarKind.Date
         ? Cast(Binary(SqlBinaryOp.Add, c.Arg(0), Call("to_months", Int(c, 1))), ScalarType.Date)
         : Binary(SqlBinaryOp.Add, c.Arg(0), Call("to_months", Int(c, 1))),
      FunctionId.DaysBetween => Call("date_diff", Text("day"), Cast(c.Arg(0), ScalarType.Date), Cast(c.Arg(1), ScalarType.Date)),
      FunctionId.Coalesce => Call("coalesce", c.Args()),
      FunctionId.NullIf => Call("nullif", c.Arg(0), c.Arg(1)),
      FunctionId.Iif => Iif(c.Condition(0), c.Arg(1), c.Arg(2)),
      FunctionId.Between => new SqlBetween(c.Arg(0), c.Arg(1), c.Arg(2)),
      FunctionId.ToInt => Whole(c, ScalarType.Int32),
      FunctionId.ToLong => Whole(c, ScalarType.Int64),
      FunctionId.ToDouble => Cast(c.Arg(0), ScalarType.Double),
      FunctionId.ToDecimal => Cast(c.Arg(0), c.Result),
      FunctionId.ToText => Cast(c.Arg(0), ScalarType.Text()),
      FunctionId.ToDate => Cast(c.Arg(0), ScalarType.Date),
      FunctionId.ToDateTime => Cast(c.Arg(0), ScalarType.DateTime),
      FunctionId.ToBool => c.Type(0).IsNumeric ? Binary(SqlBinaryOp.NotEqual, c.Arg(0), Integer(0)) : Cast(c.Arg(0), ScalarType.Boolean),
      _ => null,
   };

   /// <summary>A whole number; fractions are truncated, as the language defines, where a plain CAST would round.</summary>
   private SqlExpr Whole(SqlCall c, ScalarType type) =>
      Cast(IsFractional(c.Type(0)) ? Call("trunc", c.Arg(0)) : c.Arg(0), type);
}
