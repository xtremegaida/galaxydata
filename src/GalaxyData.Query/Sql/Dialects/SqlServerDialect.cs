using System;
using System.Text;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Sql;

/// <summary>
/// SQL Server. Conditions are not values: a bit is tested with <c>= 1</c> and a condition becomes a value through
/// CASE. Paging is TOP, or OFFSET/FETCH (which needs an ORDER BY). Nulls already sort smallest. Text constants
/// are Unicode (<c>N'...'</c>) unless the type is ansi.
/// </summary>
internal sealed class SqlServerDialect : SqlDialect
{
   public override string Name => "SQL Server";

   public override string ProviderKind => "sqlserver";

   public override int MaxParameters => 2100;

   internal override bool HasBooleanValues => false;

   internal override PagingStyle Paging => PagingStyle.TopOrOffsetFetch;

   internal override string ConcatOperator => "+";

   public override string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

   public override string ParameterName(string name) => "@" + name;

   internal override string BooleanLiteral(bool value) => value ? "CAST(1 AS bit)" : "CAST(0 AS bit)";

   internal override string? NullOrdering(bool descending) => null;

   internal override string TypeName(ScalarType type) => type.Kind switch
   {
      ScalarKind.Boolean => "bit",
      ScalarKind.Int16 => "smallint",
      ScalarKind.Int32 => "int",
      ScalarKind.Int64 => "bigint",
      ScalarKind.Decimal => type.Precision > 0 ? $"decimal({type.Precision},{type.Scale})" : "decimal(38,10)",
      ScalarKind.Single => "real",
      ScalarKind.Double => "float",
      ScalarKind.Binary => "varbinary(max)",
      ScalarKind.Guid => "uniqueidentifier",
      ScalarKind.Date => "date",
      ScalarKind.Time => "time",
      ScalarKind.DateTime => "datetime2",
      ScalarKind.DateTimeOffset => "datetimeoffset",
      ScalarKind.String when type.IsAnsi => type.Length is > 0 and <= 8000 ? $"varchar({type.Length})" : "varchar(max)",
      ScalarKind.String => type.Length is > 0 and <= 4000 ? $"nvarchar({type.Length})" : "nvarchar(max)",
      _ => "nvarchar(max)",
   };

   private protected override void WriteString(StringBuilder text, string value, ScalarType type)
   {
      if (!type.IsAnsi) { text.Append('N'); }
      base.WriteString(text, value, type);
   }

   private protected override void WriteTemporal(StringBuilder text, string keyword, string value)
   {
      string type = keyword switch
      {
         "DATE" => "date",
         "TIME" => "time",
         "TIMESTAMPTZ" => "datetimeoffset",
         _ => "datetime2",
      };
      text.Append("CAST('").Append(value).Append("' AS ").Append(type).Append(')');
   }

   private protected override void WriteBinary(StringBuilder text, byte[] value) => text.Append("0x").Append(Convert.ToHexString(value));

   internal override string AggregateName(string name) => name.ToUpperInvariant();

   /// <summary>SUM and AVG keep the type of whole numbers, which overflows and truncates; they get bigint and float.</summary>
   internal override SqlExpr Aggregate(AggregateFunction function, SqlExpr? argument, ScalarType? argumentType)
   {
      if (argumentType is { IsInteger: true } && argument != null)
      {
         if (function == AggregateFunction.Sum) { argument = Cast(argument, ScalarType.Int64); }
         if (function == AggregateFunction.Avg) { argument = Cast(argument, ScalarType.Double); }
      }
      return base.Aggregate(function, argument, argumentType);
   }

   /// <summary>There is no <c>%</c> for floats: x - y * (x / y truncated).</summary>
   internal override SqlExpr Modulo(SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType) =>
      leftType.Kind is ScalarKind.Double or ScalarKind.Single || rightType.Kind is ScalarKind.Double or ScalarKind.Single
         ? Binary(SqlBinaryOp.Subtract, left, Binary(SqlBinaryOp.Multiply, right, Call("ROUND", Binary(SqlBinaryOp.Divide, left, right), Integer(0), Integer(1))))
         : new SqlBinary(SqlBinaryOp.Modulo, left, right);

   internal override SqlExpr? Function(SqlCall c) => c.Id switch
   {
      FunctionId.Lower => Call("LOWER", c.Arg(0)),
      FunctionId.Upper => Call("UPPER", c.Arg(0)),
      FunctionId.Trim => Call("LTRIM", Call("RTRIM", c.Arg(0))),
      FunctionId.LTrim => Call("LTRIM", c.Arg(0)),
      FunctionId.RTrim => Call("RTRIM", c.Arg(0)),
      // LEN ignores trailing spaces; one more character after them makes it count them.
      FunctionId.Length => Binary(SqlBinaryOp.Subtract, Call("LEN", Binary(SqlBinaryOp.Concat, c.Arg(0), Text("."))), Integer(1)),
      FunctionId.Substring => Call("SUBSTRING", c.Arg(0), Int(c, 1), c.Count == 3 ? Int(c, 2) : Call("DATALENGTH", c.Arg(0))),
      FunctionId.IndexOf => Call("CHARINDEX", c.Arg(1), c.Arg(0)),
      FunctionId.Replace => Call("REPLACE", c.Args()),
      FunctionId.Left => Call("LEFT", c.Arg(0), Int(c, 1)),
      FunctionId.Right => Call("RIGHT", c.Arg(0), Int(c, 1)),
      FunctionId.StartsWith => c.IsFixedText(1)
         ? Like(c.Arg(0), c.Pattern(1, PatternStyle.LikeWithBrackets, PatternShape.Prefix))
         : Binary(SqlBinaryOp.Equal, Call("LEFT", c.Arg(0), Call("LEN", c.Arg(1))), c.Arg(1)),
      FunctionId.EndsWith => c.IsFixedText(1)
         ? Like(c.Arg(0), c.Pattern(1, PatternStyle.LikeWithBrackets, PatternShape.Suffix))
         : Binary(SqlBinaryOp.Equal, Call("RIGHT", c.Arg(0), Call("LEN", c.Arg(1))), c.Arg(1)),
      FunctionId.Contains => c.IsFixedText(1)
         ? Like(c.Arg(0), c.Pattern(1, PatternStyle.LikeWithBrackets, PatternShape.Contains))
         : Binary(SqlBinaryOp.Greater, Call("CHARINDEX", c.Arg(1), c.Arg(0)), Integer(0)),
      FunctionId.IContains => c.IsFixedText(1)
         ? Like(Call("LOWER", c.Arg(0)), Call("LOWER", c.Pattern(1, PatternStyle.LikeWithBrackets, PatternShape.Contains)))
         : Binary(SqlBinaryOp.Greater, Call("CHARINDEX", Call("LOWER", c.Arg(1)), Call("LOWER", c.Arg(0))), Integer(0)),
      FunctionId.Like => Like(c.Arg(0), c.Arg(1)),
      FunctionId.ILike => Like(Call("LOWER", c.Arg(0)), Call("LOWER", c.Arg(1))),
      FunctionId.Concat => Call("CONCAT", c.Args()),
      FunctionId.Abs => Call("ABS", c.Arg(0)),
      FunctionId.Round => IsInteger(c.Type(0)) ? c.Arg(0) : Call("ROUND", c.Arg(0), c.Count == 2 ? Int(c, 1) : Integer(0)),
      FunctionId.Floor => IsInteger(c.Type(0)) ? c.Arg(0) : Call("FLOOR", c.Arg(0)),
      FunctionId.Ceiling => IsInteger(c.Type(0)) ? c.Arg(0) : Call("CEILING", c.Arg(0)),
      FunctionId.Power => Call("POWER", Cast(c.Arg(0), ScalarType.Double), c.Arg(1)),
      FunctionId.Sqrt => Call("SQRT", c.Arg(0)),
      FunctionId.Sign => Cast(Call("SIGN", c.Arg(0)), ScalarType.Int32),
      FunctionId.Year => DatePart("year", c),
      FunctionId.Month => DatePart("month", c),
      FunctionId.Day => DatePart("day", c),
      FunctionId.Hour => DatePart("hour", c),
      FunctionId.Minute => DatePart("minute", c),
      FunctionId.Second => DatePart("second", c),
      FunctionId.Date => Cast(c.Arg(0), ScalarType.Date),
      FunctionId.AddDays => Call("DATEADD", Raw("day"), Int(c, 1), c.Arg(0)),
      FunctionId.AddMonths => Call("DATEADD", Raw("month"), Int(c, 1), c.Arg(0)),
      FunctionId.DaysBetween => Call("DATEDIFF", Raw("day"), c.Arg(0), c.Arg(1)),
      FunctionId.Coalesce => Call("COALESCE", c.Args()),
      FunctionId.NullIf => Call("NULLIF", c.Arg(0), c.Arg(1)),
      FunctionId.Iif => Iif(c.Condition(0), c.Arg(1), c.Arg(2)),
      FunctionId.Between => new SqlBetween(c.Arg(0), c.Arg(1), c.Arg(2)),
      FunctionId.ToInt => Cast(c.Arg(0), ScalarType.Int32),
      FunctionId.ToLong => Cast(c.Arg(0), ScalarType.Int64),
      FunctionId.ToDouble => Cast(c.Arg(0), ScalarType.Double),
      FunctionId.ToDecimal => Cast(c.Arg(0), c.Result),
      FunctionId.ToText => c.Type(0).Kind switch
      {
         ScalarKind.Boolean => BooleanText(c),
         ScalarKind.DateTime => Call("CONVERT", Raw("nvarchar(30)"), c.Arg(0), Integer(120)),
         _ => Cast(c.Arg(0), ScalarType.Text()),
      },
      FunctionId.ToDate => Cast(c.Arg(0), ScalarType.Date),
      FunctionId.ToDateTime => Cast(c.Arg(0), ScalarType.DateTime),
      FunctionId.ToBool => c.Type(0).IsNumeric ? Binary(SqlBinaryOp.NotEqual, c.Arg(0), Integer(0)) : Cast(c.Arg(0), ScalarType.Boolean),
      _ => null,
   };

   private static SqlExpr DatePart(string part, SqlCall c) => Call("DATEPART", Raw(part), c.Arg(0));
}
