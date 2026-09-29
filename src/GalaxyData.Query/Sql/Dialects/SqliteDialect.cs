using System;
using System.Text;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Sql;

/// <summary>
/// SQLite. Values are dynamically typed: booleans are 0/1, dates and times are ISO text, guids are upper-case
/// text (as Microsoft.Data.Sqlite binds them). Nulls already sort smallest. LIKE ignores ASCII case, so the
/// case-sensitive text tests use GLOB.
/// </summary>
internal sealed class SqliteDialect : SqlDialect
{
   public override string Name => "SQLite";

   public override string ProviderKind => "sqlite";

   public override int MaxParameters => 32766;

   internal override PagingStyle Paging => PagingStyle.SqliteLimitOffset;

   internal override string BooleanLiteral(bool value) => value ? "1" : "0";

   internal override string? NullOrdering(bool descending) => null;

   internal override string TypeName(ScalarType type) => type.Kind switch
   {
      ScalarKind.Boolean or ScalarKind.Int16 or ScalarKind.Int32 or ScalarKind.Int64 => "INTEGER",
      ScalarKind.Decimal => "NUMERIC",
      ScalarKind.Single or ScalarKind.Double => "REAL",
      ScalarKind.Binary => "BLOB",
      _ => "TEXT",
   };

   private protected override void WriteTemporal(StringBuilder text, string keyword, string value) => text.Append('\'').Append(value).Append('\'');

   private protected override void WriteGuid(StringBuilder text, Guid value) => text.Append('\'').Append(value.ToString("D").ToUpperInvariant()).Append('\'');

   private protected override void WriteBinary(StringBuilder text, byte[] value) => text.Append("X'").Append(Convert.ToHexString(value)).Append('\'');

   /// <summary>
   /// Only integers and text: SQLite keeps what was stored, so a date may be held with a time or a 'T', a guid in
   /// either case, a decimal with more digits than its scale, and each reads the same as the canonical value it
   /// doesn't equal.
   /// </summary>
   internal override bool ComparesExactly(ScalarType type) => type.IsInteger || type.Kind == ScalarKind.String;

   /// <summary>Whole-valued decimals are stored as integers, and integer division truncates, so division is done in reals.</summary>
   internal override SqlExpr Divide(SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType) =>
      leftType.Kind is ScalarKind.Double or ScalarKind.Single
         ? new SqlBinary(SqlBinaryOp.Divide, left, right)
         : new SqlBinary(SqlBinaryOp.Divide, Cast(left, ScalarType.Double), right);

   /// <summary><c>%</c> works on integers only; <c>mod</c> keeps the fraction.</summary>
   internal override SqlExpr Modulo(SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType) =>
      leftType.IsInteger && rightType.IsInteger ? new SqlBinary(SqlBinaryOp.Modulo, left, right) : Call("mod", left, right);

   /// <summary>Dates are 'yyyy-MM-dd' text and date-times 'yyyy-MM-dd HH:mm:ss', so a date meeting a date-time becomes one first.</summary>
   internal override SqlExpr Compare(SqlBinaryOp op, SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType)
   {
      if (leftType.Kind == ScalarKind.Date && rightType.Kind == ScalarKind.DateTime) { left = Call("datetime", left); }
      if (rightType.Kind == ScalarKind.Date && leftType.Kind == ScalarKind.DateTime) { right = Call("datetime", right); }
      return new SqlBinary(op, left, right);
   }

   internal override SqlExpr? Function(SqlCall c) => c.Id switch
   {
      FunctionId.Lower => Call("lower", c.Arg(0)),
      FunctionId.Upper => Call("upper", c.Arg(0)),
      FunctionId.Trim => Call("trim", c.Arg(0)),
      FunctionId.LTrim => Call("ltrim", c.Arg(0)),
      FunctionId.RTrim => Call("rtrim", c.Arg(0)),
      FunctionId.Length => Call("length", c.Arg(0)),
      FunctionId.Substring => Call("substr", c.Args()),
      FunctionId.IndexOf => Call("instr", c.Arg(0), c.Arg(1)),
      FunctionId.Replace => Call("replace", c.Args()),
      FunctionId.Left => Call("substr", c.Arg(0), Integer(1), c.Arg(1)),
      FunctionId.Right => Tail(c.Arg(0), c.Arg(1)),
      FunctionId.StartsWith => c.IsFixedText(1)
         ? Binary(SqlBinaryOp.Glob, c.Arg(0), c.Pattern(1, PatternStyle.Glob, PatternShape.Prefix))
         : Binary(SqlBinaryOp.Equal, Call("substr", c.Arg(0), Integer(1), Call("length", c.Arg(1))), c.Arg(1)),
      FunctionId.EndsWith => c.IsFixedText(1)
         ? Binary(SqlBinaryOp.Glob, c.Arg(0), c.Pattern(1, PatternStyle.Glob, PatternShape.Suffix))
         : Binary(SqlBinaryOp.Equal, Tail(c.Arg(0), Call("length", c.Arg(1))), c.Arg(1)),
      FunctionId.Contains => c.IsFixedText(1)
         ? Binary(SqlBinaryOp.Glob, c.Arg(0), c.Pattern(1, PatternStyle.Glob, PatternShape.Contains))
         : Binary(SqlBinaryOp.Greater, Call("instr", c.Arg(0), c.Arg(1)), Integer(0)),
      FunctionId.IContains => c.IsFixedText(1)
         ? Like(c.Arg(0), c.Pattern(1, PatternStyle.Like, PatternShape.Contains))
         : Binary(SqlBinaryOp.Greater, Call("instr", Call("lower", c.Arg(0)), Call("lower", c.Arg(1))), Integer(0)),
      FunctionId.Like or FunctionId.ILike => Like(c.Arg(0), c.Arg(1)),
      FunctionId.Concat => Call("concat", c.Args()),
      FunctionId.Abs => Call("abs", c.Arg(0)),
      FunctionId.Round => IsInteger(c.Type(0)) ? c.Arg(0) : Call("round", c.Args()),
      FunctionId.Floor => IsInteger(c.Type(0)) ? c.Arg(0) : Call("floor", c.Arg(0)),
      FunctionId.Ceiling => IsInteger(c.Type(0)) ? c.Arg(0) : Call("ceiling", c.Arg(0)),
      FunctionId.Power => Call("power", c.Arg(0), c.Arg(1)),
      FunctionId.Sqrt => Call("sqrt", c.Arg(0)),
      FunctionId.Sign => Call("sign", c.Arg(0)),
      FunctionId.Year => Part("%Y", c),
      FunctionId.Month => Part("%m", c),
      FunctionId.Day => Part("%d", c),
      FunctionId.Hour => Part("%H", c),
      FunctionId.Minute => Part("%M", c),
      FunctionId.Second => Part("%S", c),
      FunctionId.Date => Call("date", c.Arg(0)),
      FunctionId.AddDays => Shift(c, " days"),
      FunctionId.AddMonths => Shift(c, " months"),
      FunctionId.DaysBetween => Cast(Binary(SqlBinaryOp.Subtract, Call("julianday", Call("date", c.Arg(1))), Call("julianday", Call("date", c.Arg(0)))), ScalarType.Int64),
      FunctionId.Coalesce => Call("coalesce", c.Args()),
      FunctionId.NullIf => Call("nullif", c.Arg(0), c.Arg(1)),
      FunctionId.Iif => Iif(c.Condition(0), c.Arg(1), c.Arg(2)),
      FunctionId.Between => new SqlBetween(c.Arg(0), c.Arg(1), c.Arg(2)),
      FunctionId.ToInt or FunctionId.ToLong => Cast(c.Arg(0), ScalarType.Int64),
      FunctionId.ToDouble => Cast(c.Arg(0), ScalarType.Double),
      FunctionId.ToDecimal => c.Result.Precision > 0 ? Call("round", Cast(c.Arg(0), c.Result), Integer(c.Result.Scale)) : Cast(c.Arg(0), c.Result),
      FunctionId.ToText => ToText(c),
      FunctionId.ToDate => Call("date", c.Arg(0)),
      FunctionId.ToDateTime => Call("datetime", c.Arg(0)),
      FunctionId.ToBool => ToBool(c),
      _ => null,
   };

   /// <summary>The last <paramref name="count"/> characters of <paramref name="text"/>; the whole text when it is shorter.</summary>
   private static SqlExpr Tail(SqlExpr text, SqlExpr count) =>
      Call("substr", text, Call("max", Binary(SqlBinaryOp.Add, Binary(SqlBinaryOp.Subtract, Call("length", text), count), Integer(1)), Integer(1)));

   private SqlExpr Part(string format, SqlCall c) => Cast(Call("strftime", Text(format), c.Arg(0)), ScalarType.Int64);

   /// <summary><c>date(x, n || ' days')</c>; date-times keep their time of day.</summary>
   private static SqlExpr Shift(SqlCall c, string unit) =>
      Call(c.Type(0).Kind == ScalarKind.Date ? "date" : "datetime", c.Arg(0), Binary(SqlBinaryOp.Concat, c.Arg(1), Text(unit)));

   private SqlExpr ToText(SqlCall c)
   {
      ScalarType type = c.Type(0);
      if (type.Kind == ScalarKind.Boolean) { return BooleanText(c); }
      if (type.Kind == ScalarKind.Decimal && type.Scale > 0)
      {
         // Decimals are stored as integers or reals; printf gives them their scale ('250.00'), and would print null as 0.
         SqlExpr formatted = Call("printf", Text($"%.{type.Scale}f"), c.Arg(0));
         return type.Nullable ? new SqlCase([new SqlWhen(new SqlIsNull(c.Arg(0), true), formatted)], null) : formatted;
      }
      return Cast(c.Arg(0), ScalarType.Text());
   }

   private static SqlExpr ToBool(SqlCall c)
   {
      ScalarType type = c.Type(0);
      if (type.IsNumeric) { return Binary(SqlBinaryOp.NotEqual, c.Arg(0), Integer(0)); }
      if (type.Kind != ScalarKind.String) { return c.Arg(0); }
      SqlExpr lower = Call("lower", c.Arg(0));
      return new SqlCase(
      [
         new SqlWhen(new SqlIn(lower, [Text("true"), Text("t"), Text("yes"), Text("y"), Text("1")], false), Integer(1)),
         new SqlWhen(new SqlIn(lower, [Text("false"), Text("f"), Text("no"), Text("n"), Text("0")], false), Integer(0)),
      ], null);
   }
}
