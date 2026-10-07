using System;
using System.Globalization;
using System.Text;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Sql;

/// <summary>
/// SQL Server. Conditions are not values: a bit is tested with <c>= 1</c> and a condition becomes a value through
/// CASE. Paging is TOP, or OFFSET/FETCH (which needs an ORDER BY). Nulls already sort smallest. Text constants
/// are Unicode (<c>N'...'</c>) unless the type is ansi. The parts of a date-time with an offset are those of its
/// UTC time, as in the other databases, which work in UTC.
/// </summary>
internal sealed class SqlServerDialect : SqlDialect
{
   public override string Name => "SQL Server";

   public override string ProviderKind => "sqlserver";

   public override int MaxParameters => 2100;

   internal override int MaxNameLength => 128;

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

   private protected override void WriteQuoted(StringBuilder text, string value, ScalarType type)
   {
      if (!type.IsAnsi) { text.Append('N'); }
      base.WriteQuoted(text, value, type);
   }

   private protected override string Concatenation => " + ";

   private protected override void WriteCharacter(StringBuilder text, char value, ScalarType type) =>
      text.Append(type.IsAnsi ? "CHAR(" : "NCHAR(").Append(((int)value).ToString(CultureInfo.InvariantCulture)).Append(')');

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

   /// <summary>A date-time compared with or stored in a <c>datetime</c> column is one, for the same reason as in <see cref="Compare"/>.</summary>
   internal override void WriteLiteral(StringBuilder text, object? value, ScalarType type, string? columnType = null)
   {
      if (value is DateTime dateTime && columnType is "datetime" or "smalldatetime")
      {
         text.Append("CAST('").Append(dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("' AS ").Append(columnType).Append(')');
         return;
      }
      base.WriteLiteral(text, value, type, columnType);
   }

   /// <summary>nvarchar(n) holds n UTF-16 units: a character outside the BMP takes two (varchar's bytes are no fewer, but in UTF-8 collations).</summary>
   internal override int TextLength(string text) => text.Length;

   internal override ReturningStyle Returning => ReturningStyle.Output;

   internal override bool AcceptsIdentityValues => false;

   internal override SqlExpr? RowCountOfChange => Raw("@@ROWCOUNT");

   internal override SqlExpr? InsertedIdentity => Call("SCOPE_IDENTITY");

   /// <summary>The old large types (<c>text</c>, <c>ntext</c>, <c>image</c>) don't compare with <c>=</c>.</summary>
   internal override bool ComparesOriginal(ScalarType type, string? nativeType) =>
      base.ComparesOriginal(type, nativeType) && nativeType is not ("text" or "ntext" or "image");

   /// <summary>
   /// A <c>datetime</c> column compared with a date-time worked out in the query (<c>toDateTime('...')</c>) is compared
   /// as a <c>datetime</c>: SQL Server compares it with a <c>datetime2</c> in its 1/300 seconds, which few equal.
   /// Parameters are sent as <c>datetime</c> already (see the provider), and columns are compared as they are.
   /// </summary>
   internal override SqlExpr Compare(SqlBinaryOp op, SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType)
   {
      if (IsDatetime(left) && rightType.Kind == ScalarKind.DateTime && right is not (SqlColumn or SqlParameterRef)) { right = new SqlCast(right, "datetime"); }
      else if (IsDatetime(right) && leftType.Kind == ScalarKind.DateTime && left is not (SqlColumn or SqlParameterRef)) { left = new SqlCast(left, "datetime"); }
      return new SqlBinary(op, left, right);
   }

   private static bool IsDatetime(SqlExpr expr) => expr is SqlColumn { NativeType: "datetime" };

   /// <summary>Counts are COUNT_BIG: COUNT gives an int, which overflows past 2^31 rows where the language's counts are 64-bit.</summary>
   internal override string AggregateName(string name) => name == "count" ? "COUNT_BIG" : name.ToUpperInvariant();

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
      FunctionId.Length => Length(c.Arg(0)),
      FunctionId.Substring => Call("SUBSTRING", c.Arg(0), Int(c, 1), c.Count == 3 ? Int(c, 2) : Call("DATALENGTH", c.Arg(0))),
      FunctionId.IndexOf => Position(c.Arg(1), c.Arg(0)),
      FunctionId.Replace => Call("REPLACE", c.Args()),
      FunctionId.Left => Call("LEFT", c.Arg(0), Int(c, 1)),
      FunctionId.Right => Call("RIGHT", c.Arg(0), Int(c, 1)),
      FunctionId.StartsWith => c.IsFixedText(1)
         ? Like(c.Arg(0), c.Pattern(1, PatternStyle.LikeWithBrackets, PatternShape.Prefix))
         : Binary(SqlBinaryOp.And, Binary(SqlBinaryOp.GreaterOrEqual, Length(c.Arg(0)), Length(c.Arg(1))), Binary(SqlBinaryOp.Equal, Call("LEFT", c.Arg(0), Length(c.Arg(1))), c.Arg(1))),
      FunctionId.EndsWith => c.IsFixedText(1)
         ? Like(c.Arg(0), c.Pattern(1, PatternStyle.LikeWithBrackets, PatternShape.Suffix))
         : Binary(SqlBinaryOp.And, Binary(SqlBinaryOp.GreaterOrEqual, Length(c.Arg(0)), Length(c.Arg(1))), Binary(SqlBinaryOp.Equal, Call("RIGHT", c.Arg(0), Length(c.Arg(1))), c.Arg(1))),
      FunctionId.Contains => c.IsFixedText(1)
         ? Like(c.Arg(0), c.Pattern(1, PatternStyle.LikeWithBrackets, PatternShape.Contains))
         : Binary(SqlBinaryOp.Greater, Position(c.Arg(1), c.Arg(0)), Integer(0)),
      FunctionId.IContains => c.IsFixedText(1)
         ? Like(Call("LOWER", c.Arg(0)), Call("LOWER", c.Pattern(1, PatternStyle.LikeWithBrackets, PatternShape.Contains)))
         : Binary(SqlBinaryOp.Greater, Position(Call("LOWER", c.Arg(1)), Call("LOWER", c.Arg(0))), Integer(0)),
      FunctionId.Like => Like(c.Arg(0), UserPattern(c)),
      FunctionId.ILike => Like(Call("LOWER", c.Arg(0)), Call("LOWER", UserPattern(c))),
      FunctionId.Concat => Call("CONCAT", ConcatArguments(c)),
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
      FunctionId.Date => Cast(Utc(c, 0), ScalarType.Date),
      FunctionId.AddDays => Call("DATEADD", Raw("day"), Int(c, 1), Utc(c, 0)),
      FunctionId.AddMonths => Call("DATEADD", Raw("month"), Int(c, 1), Utc(c, 0)),
      FunctionId.DaysBetween => Call("DATEDIFF", Raw("day"), Utc(c, 0), Utc(c, 1)),
      FunctionId.StartOfWeek => Template("DATEADD(day, -((DATEDIFF(day, CAST('1900-01-01' AS date), {0}) % 7 + 7) % 7), {0})", DateOf(c)),
      FunctionId.StartOfMonth => Call("DATEFROMPARTS", DatePart("year", c), DatePart("month", c), Raw("1")),
      FunctionId.StartOfQuarter => Call("DATEFROMPARTS", DatePart("year", c), Template("(DATEPART(quarter, {0}) - 1) * 3 + 1", Utc(c, 0)), Raw("1")),
      FunctionId.StartOfYear => Call("DATEFROMPARTS", DatePart("year", c), Raw("1"), Raw("1")),
      FunctionId.Quarter => DatePart("quarter", c),
      FunctionId.DayOfWeek => Template("((DATEDIFF(day, CAST('1900-01-01' AS date), {0}) % 7 + 7) % 7 + 1)", DateOf(c)),
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
         // Guids in lower case, as the other databases write them; decimals (money too) to their scale.
         ScalarKind.Guid => Call("LOWER", Cast(c.Arg(0), ScalarType.Text(36))),
         ScalarKind.Decimal when c.Type(0).Precision > 0 => Cast(Cast(c.Arg(0), c.Type(0)), ScalarType.Text()),
         _ => Cast(c.Arg(0), ScalarType.Text()),
      },
      FunctionId.ToDate => Cast(Utc(c, 0), ScalarType.Date),
      FunctionId.ToDateTime => Cast(Utc(c, 0), ScalarType.DateTime),
      FunctionId.ToBool => c.Type(0).IsNumeric ? Binary(SqlBinaryOp.NotEqual, c.Arg(0), Integer(0))
         : c.Type(0).Kind == ScalarKind.String ? BooleanOfText(c.Arg(0))
         : Cast(c.Arg(0), ScalarType.Boolean),
      _ => null,
   };

   private static SqlExpr DatePart(string part, SqlCall c) => Call("DATEPART", Raw(part), Utc(c, 0));

   /// <summary>
   /// The characters of text, trailing spaces too (LEN leaves them out): its UTF-16 bytes, halved. As nvarchar(max),
   /// so text of 4,000 characters (or varchar of 8,000) isn't cut short.
   /// </summary>
   private SqlExpr Length(SqlExpr text) => Cast(Binary(SqlBinaryOp.Divide, Call("DATALENGTH", Cast(text, ScalarType.Text())), Integer(2)), ScalarType.Int32);

   /// <summary>Where <paramref name="text"/> is in <paramref name="within"/>, from 1; empty text is at 1, as elsewhere (CHARINDEX says 0).</summary>
   private SqlExpr Position(SqlExpr text, SqlExpr within) =>
      Binary(SqlBinaryOp.Add, Call("CHARINDEX", text, within),
         new SqlCase([new SqlWhen(Binary(SqlBinaryOp.Equal, Call("DATALENGTH", text), Integer(0)), Integer(1))], Integer(0)));

   /// <summary>A pattern of like() or ilike(): as written, with <c>[</c> escaped, which SQL Server reads as a class.</summary>
   private static SqlExpr UserPattern(SqlCall c) => c.IsFixedText(1)
      ? c.Pattern(1, PatternStyle.LikeWithBrackets, PatternShape.AsWritten)
      : Call("REPLACE", c.Arg(1), Text("["), Text("\\["));

   /// <summary>Text as a boolean, as the other databases read it: true, t, yes, y and 1, or false, f, no, n and 0 (any case); null otherwise.</summary>
   private static SqlExpr BooleanOfText(SqlExpr text)
   {
      SqlExpr word = Call("LOWER", Call("LTRIM", Call("RTRIM", text)));
      return new SqlCase(
      [
         new SqlWhen(new SqlIn(word, [Text("true"), Text("t"), Text("yes"), Text("y"), Text("1")], negated: false), new SqlLiteral(true, ScalarType.Boolean)),
         new SqlWhen(new SqlIn(word, [Text("false"), Text("f"), Text("no"), Text("n"), Text("0")], negated: false), new SqlLiteral(false, ScalarType.Boolean)),
      ], null);
   }

   /// <summary>The (UTC) date of a date-time; a date as it is.</summary>
   private SqlExpr DateOf(SqlCall c) => c.Type(0).Kind == ScalarKind.Date ? c.Arg(0) : Cast(Utc(c, 0), ScalarType.Date);

   /// <summary>A date-time with an offset moved to UTC, so its parts are the UTC time's; other values as they are.</summary>
   private static SqlExpr Utc(SqlCall c, int index) =>
      c.Type(index).Kind == ScalarKind.DateTimeOffset ? Call("SWITCHOFFSET", c.Arg(index), Text("+00:00")) : c.Arg(index);
}
