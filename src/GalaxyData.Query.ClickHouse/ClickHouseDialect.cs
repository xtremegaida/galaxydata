using System;
using System.Globalization;
using System.Linq;
using System.Text;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.ClickHouse;

/// <summary>
/// ClickHouse. Names and functions are case-sensitive, and backslashes escape in quoted names and strings. Types are
/// non-nullable unless declared <c>Nullable(T)</c>, which casts write for values that may be null. Text functions
/// count bytes, so the <c>UTF8</c> forms are used; <c>/</c> always gives a Float64; set operations name whether they
/// keep duplicates. Aggregates of no rows are null, as SQL's are, as they are written (<see cref="Aggregate"/>); the
/// sessions' settings (see <see cref="ClickHouseSourceProvider"/>) make outer joins give nulls. A subquery may read
/// columns of the query it is in only to test whether rows exist: ClickHouse's subqueries that give values read null
/// for no rows, where <c>count()</c> is 0.
/// </summary>
public sealed class ClickHouseDialect : SqlDialect
{
   private ClickHouseDialect() { }

   public static ClickHouseDialect Instance { get; } = new();

   public override string Name => "ClickHouse";

   public override string ProviderKind => "clickhouse";

   /// <summary>Parameters travel in the HTTP request: a bind join's batches stay well inside what a server takes.</summary>
   public override int MaxParameters => 1000;

   /// <summary>Double quotes, with a backslash escaped too, as ClickHouse reads it in names.</summary>
   public override string QuoteIdentifier(string name)
   {
      ArgumentNullException.ThrowIfNull(name);
      return "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
   }

   /// <summary>A type as casts write it: <c>Nullable(T)</c> for one that may be null, since a null can't be cast to <c>T</c>.</summary>
   protected override string TypeName(ScalarType type)
   {
      string name = type.Kind switch
      {
         ScalarKind.Boolean => "Bool",
         ScalarKind.Int16 => "Int16",
         ScalarKind.Int32 => "Int32",
         ScalarKind.Int64 => "Int64",
         ScalarKind.Decimal => type.Precision > 0 ? $"Decimal({type.Precision}, {type.Scale})" : "Decimal(38, 10)",
         ScalarKind.Single => "Float32",
         ScalarKind.Double => "Float64",
         ScalarKind.Guid => "UUID",
         ScalarKind.Date => "Date32",
         ScalarKind.DateTime => "DateTime64(6)",
         ScalarKind.DateTimeOffset => "DateTime64(6, 'UTC')",
         ScalarKind.Time or ScalarKind.Interval => throw new NotSupportedException($"ClickHouse has no {type.Kind.ToString().ToLowerInvariant()} values to cast to"),
         _ => "String",
      };
      return type.Nullable ? $"Nullable({name})" : name;
   }

   /// <summary>Quotes doubled, and backslashes, which escape in ClickHouse's strings.</summary>
   protected override void WriteQuoted(StringBuilder text, string value, ScalarType type)
   {
      ArgumentNullException.ThrowIfNull(text);
      ArgumentNullException.ThrowIfNull(value);
      text.Append('\'').Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal)).Append('\'');
   }

   protected override void WriteCharacter(StringBuilder text, char value, ScalarType type)
   {
      ArgumentNullException.ThrowIfNull(text);
      text.Append("char(").Append(((int)value).ToString(CultureInfo.InvariantCulture)).Append(')');
   }

   /// <summary>Dates and date-times through ClickHouse's functions, date-times to the microsecond (DateTime64(6)), those with an offset in UTC.</summary>
   protected override void WriteLiteral(StringBuilder text, object? value, ScalarType type, string? columnType = null)
   {
      ArgumentNullException.ThrowIfNull(text);
      switch (value)
      {
         case DateTime dateTime:
            text.Append("toDateTime64('").Append(dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)).Append("', 6)");
            break;
         case DateTimeOffset offset:
            text.Append("toDateTime64('").Append(offset.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)).Append("', 6, 'UTC')");
            break;
         case TimeOnly or TimeSpan:
            throw new NotSupportedException("ClickHouse has no times of day or intervals to write");
         default:
            base.WriteLiteral(text, value, type, columnType);
            break;
      }
   }

   protected override void WriteTemporal(StringBuilder text, string keyword, string value)
   {
      ArgumentNullException.ThrowIfNull(text);
      if (keyword != "DATE") { throw new NotSupportedException($"ClickHouse has no {keyword} literals"); }
      text.Append("toDate32('").Append(value).Append("')");
   }

   protected override void WriteGuid(StringBuilder text, Guid value)
   {
      ArgumentNullException.ThrowIfNull(text);
      text.Append("toUUID('").Append(value.ToString("D")).Append("')");
   }

   protected override void WriteBinary(StringBuilder text, byte[] value)
   {
      ArgumentNullException.ThrowIfNull(text);
      ArgumentNullException.ThrowIfNull(value);
      text.Append("unhex('").Append(Convert.ToHexString(value)).Append("')");
   }

   /// <summary>
   /// <c>/</c> gives a Float64 for whole numbers, with no cast. A decimal quotient has the dividend's scale, so a
   /// decimal is divided with 18 digits after the point, as the other databases give about as many.
   /// </summary>
   protected override SqlExpr Divide(SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType)
   {
      bool exact = (leftType.Kind == ScalarKind.Decimal || rightType.Kind == ScalarKind.Decimal) && !IsFloating(leftType) && !IsFloating(rightType);
      return new SqlBinary(SqlBinaryOp.Divide, exact ? Cast(left, ScalarType.Decimal(38, 18)) : left, right);
   }

   private static bool IsFloating(ScalarType type) => type.Kind is ScalarKind.Single or ScalarKind.Double;

   /// <summary>The remainder; a decimal's, of two decimals of one scale, as ClickHouse's <c>%</c> of a decimal and a whole number is wrong (7 % 2.5 is 0.7).</summary>
   protected override SqlExpr Modulo(SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType)
   {
      bool exact = (leftType.Kind == ScalarKind.Decimal || rightType.Kind == ScalarKind.Decimal) && !IsFloating(leftType) && !IsFloating(rightType);
      return exact
         ? new SqlBinary(SqlBinaryOp.Modulo, Cast(left, ScalarType.Decimal(38, 18)), Cast(right, ScalarType.Decimal(38, 18)))
         : new SqlBinary(SqlBinaryOp.Modulo, left, right);
   }

   /// <summary>
   /// Aggregates of no rows are null, as SQL's are: <c>sumOrNull</c>, <c>avgOrNull</c>, <c>minOrNull</c> and
   /// <c>maxOrNull</c>, where ClickHouse's own give the type's zero (or nan); a sum of none is then 0, as the language
   /// says, and counts are as they are. They are written so rather than made so by the setting that does it
   /// (<c>aggregate_functions_null_for_empty</c>), which would also rewrite the aggregates of the views a query reads:
   /// a view's <c>anyMerge</c> of an <c>any</c> state would be an <c>anyOrNullMerge</c>, which can't read it.
   /// The distinct values are estimated by <c>uniqCombined64</c>, a sketch of a few kilobytes: an exact count
   /// (<c>uniqExact</c>) holds every value in memory at once, and can't spill to disk. Of nulls only it is null, so
   /// 0 then, as a count is.
   /// </summary>
   protected override SqlExpr Aggregate(AggregateFunction function, SqlExpr? argument, ScalarType? argumentType) => function switch
   {
      AggregateFunction.ApproxCountDistinct =>
         new SqlFunctionCall("coalesce", new SqlAggregate("uniqCombined64", argument), new SqlLiteral(0L, ScalarType.Int64)),
      AggregateFunction.Sum => new SqlFunctionCall("coalesce", new SqlAggregate("sumOrNull", argument), new SqlLiteral(0L, ScalarType.Int64)),
      AggregateFunction.Avg => new SqlAggregate("avgOrNull", argument),
      AggregateFunction.Min => new SqlAggregate("minOrNull", argument),
      AggregateFunction.Max => new SqlAggregate("maxOrNull", argument),
      _ => base.Aggregate(function, argument, argumentType),
   };

   /// <summary>
   /// A view is read with the outer joins it was defined with. The sessions' outer joins give nulls by a setting
   /// (<c>join_use_nulls</c>), which ClickHouse applies inside the views a statement reads too: a view whose own outer
   /// join was made without it would give nulls its columns can't hold, and fail. A subquery's settings apply inside it
   /// only, so the view is read in one with ClickHouse's default; the statement's own joins still give nulls.
   /// </summary>
   protected override string ViewReference(string view) => $"(SELECT * FROM {view} SETTINGS join_use_nulls = 0)";

   /// <summary>Bare <c>UNION</c> fails unless a setting says which; <c>INTERSECT</c> and <c>EXCEPT</c> would keep duplicates.</summary>
   protected override string SetOperator(SqlSetOperator op) => op switch
   {
      SqlSetOperator.Union => "UNION DISTINCT",
      SqlSetOperator.UnionAll => "UNION ALL",
      SqlSetOperator.Intersect => "INTERSECT DISTINCT",
      _ => "EXCEPT DISTINCT",
   };

   /// <summary>A backslash is LIKE's escape without an ESCAPE clause, which ClickHouse doesn't take.</summary>
   protected override string? LikeEscape(char escape) =>
      escape == '\\' ? null : throw new NotSupportedException($"ClickHouse's LIKE escapes with a backslash, not '{escape}'");

   /// <summary>A subquery may read columns of the query it is in, and no further out.</summary>
   protected override int MaxCorrelationDepth => 1;

   /// <summary>A subquery that reads the query around it gives null where it finds no rows (a count of none is null), so only EXISTS may.</summary>
   protected override bool CorrelatesValueSubqueries => false;

   /// <summary>
   /// A join's hash table is built from its right side. ClickHouse swaps the sides only where it can estimate both,
   /// which it can't for a view: a page joined to the view it refers to would build the table from the whole view.
   /// </summary>
   protected override bool BuildsJoinsFromRight => true;

   /// <summary><c>SELECT d.id FROM d JOIN s ON …</c>, where <c>s</c> has an <c>id</c> too, names the column <c>d.id</c>.</summary>
   protected override bool NamesJoinedColumnsByTable => true;

   /// <summary>
   /// ClickHouse reads the columns a sort doesn't need for the rows a limit keeps only (lazy materialization), but not
   /// through a view: a page of a view's rows sorted whole would read every column of every row.
   /// </summary>
   protected override bool PagesViewsByKeys => true;

   protected override SqlExpr? Function(SqlCall c) => c.Id switch
   {
      FunctionId.Lower => Call("lowerUTF8", c.Arg(0)),
      FunctionId.Upper => Call("upperUTF8", c.Arg(0)),
      FunctionId.Trim => Call("trimBoth", c.Arg(0)),
      FunctionId.LTrim => Call("trimLeft", c.Arg(0)),
      FunctionId.RTrim => Call("trimRight", c.Arg(0)),
      FunctionId.Length => Cast(Call("lengthUTF8", c.Arg(0)), c.Result),
      FunctionId.Substring => Substring(c),
      FunctionId.IndexOf => Cast(Call("positionUTF8", c.Arg(0), c.Arg(1)), c.Result),
      FunctionId.Replace => Call("replaceAll", c.Args()),
      FunctionId.Left => Call("leftUTF8", c.Arg(0), Int(c, 1)),
      FunctionId.Right => Call("rightUTF8", c.Arg(0), Int(c, 1)),
      FunctionId.StartsWith => Call("startsWith", c.Arg(0), c.Arg(1)),
      FunctionId.EndsWith => Call("endsWith", c.Arg(0), c.Arg(1)),
      FunctionId.Contains => Binary(SqlBinaryOp.Greater, Call("position", c.Arg(0), c.Arg(1)), Integer(0)),
      FunctionId.IContains => Binary(SqlBinaryOp.Greater, Call("positionCaseInsensitiveUTF8", c.Arg(0), c.Arg(1)), Integer(0)),
      FunctionId.Like => Like(c.Arg(0), c.Arg(1)),
      FunctionId.ILike => Like(c.Arg(0), c.Arg(1), caseInsensitive: true),
      FunctionId.Concat => Concat(c),
      FunctionId.Abs => Call("abs", c.Arg(0)),
      FunctionId.Round => Round(c),
      FunctionId.Floor => IsInteger(c.Type(0)) ? c.Arg(0) : Call("floor", c.Arg(0)),
      FunctionId.Ceiling => IsInteger(c.Type(0)) ? c.Arg(0) : Call("ceil", c.Arg(0)),
      FunctionId.Power => Call("pow", c.Arg(0), c.Arg(1)),
      FunctionId.Sqrt => Call("sqrt", c.Arg(0)),
      FunctionId.Sign => Cast(Call("sign", c.Arg(0)), ScalarType.Int32),
      FunctionId.Year => Part("toYear", c),
      FunctionId.Month => Part("toMonth", c),
      FunctionId.Day => Part("toDayOfMonth", c),
      FunctionId.Hour => Part("toHour", c),
      FunctionId.Minute => Part("toMinute", c),
      FunctionId.Second => Part("toSecond", c),
      FunctionId.Date => Cast(c.Arg(0), ScalarType.Date),
      FunctionId.AddDays => Call("addDays", c.Arg(0), Int(c, 1)),
      FunctionId.AddMonths => Call("addMonths", c.Arg(0), Int(c, 1)),
      FunctionId.DaysBetween => Cast(Call("dateDiff", Text("day"), Cast(c.Arg(0), ScalarType.Date), Cast(c.Arg(1), ScalarType.Date)), c.Result),
      FunctionId.StartOfWeek => Cast(Call("toMonday", c.Arg(0)), ScalarType.Date),
      FunctionId.StartOfMonth => Cast(Call("toStartOfMonth", c.Arg(0)), ScalarType.Date),
      FunctionId.StartOfQuarter => Cast(Call("toStartOfQuarter", c.Arg(0)), ScalarType.Date),
      FunctionId.StartOfYear => Cast(Call("toStartOfYear", c.Arg(0)), ScalarType.Date),
      FunctionId.Quarter => Part("toQuarter", c),
      FunctionId.DayOfWeek => Part("toDayOfWeek", c),
      FunctionId.Coalesce => Coalesce(c),
      FunctionId.NullIf => Call("nullIf", c.Arg(0), c.Arg(1)),
      FunctionId.Iif => Iif(c.Condition(0), c.Arg(1), c.Arg(2)),
      FunctionId.Between => new SqlBetween(c.Arg(0), c.Arg(1), c.Arg(2)),
      FunctionId.ToInt => Whole(c, ScalarType.Int32),
      FunctionId.ToLong => Whole(c, ScalarType.Int64),
      FunctionId.ToDouble => Cast(c.Arg(0), ScalarType.Double),
      FunctionId.ToDecimal => Cast(c.Arg(0), c.Result),
      FunctionId.ToText => c.Type(0).Kind == ScalarKind.Boolean ? BooleanText(c) : TextOf(c.Arg(0), c.Type(0)),
      FunctionId.ToDate => Cast(c.Arg(0), ScalarType.Date),
      FunctionId.ToDateTime => Cast(c.Arg(0), ScalarType.DateTime),
      FunctionId.ToBool => c.Type(0).IsNumeric ? Binary(SqlBinaryOp.NotEqual, c.Arg(0), Integer(0)) : Cast(c.Arg(0), ScalarType.Boolean),
      _ => null,
   };

   /// <summary>A part of a date or date-time, as an int (ClickHouse gives unsigned ones, which subtract into wrapped values).</summary>
   private SqlExpr Part(string function, SqlCall c) => Cast(Call(function, c.Arg(0)), ScalarType.Int32);

   /// <summary>concat(...) of text, nulls as no text, as the language joins them (ClickHouse's concat gives null for a null).</summary>
   private SqlExpr Concat(SqlCall c)
   {
      SqlExpr[] arguments = ConcatArguments(c);
      for (int i = 0; i < arguments.Length; i++)
      {
         // ConcatArguments has made true and false text already.
         ScalarType type = c.Type(i).Kind == ScalarKind.Boolean ? ScalarType.Text() : c.Type(i);
         arguments[i] = Call("ifNull", TextOf(arguments[i], type), Text(string.Empty));
      }
      return Call("concat", arguments);
   }

   /// <summary>
   /// A value as text, as the other databases write it: a decimal with its scale's digits (<c>250.00</c>), which
   /// ClickHouse's casts leave out; a date-time without fractions of a second it doesn't have (ClickHouse writes all six).
   /// </summary>
   private SqlExpr TextOf(SqlExpr value, ScalarType type) => type.Kind switch
   {
      ScalarKind.Decimal when type.Scale > 0 => Call("toDecimalString", value, Integer(type.Scale)),
      ScalarKind.DateTime => Call("trimRight", Call("replaceRegexpOne", Call("toString", Cast(value, ScalarType.DateTime)), Text("0+$"), Text(string.Empty)), Text(".")),
      _ => Cast(value, ScalarType.Text()),
   };

   /// <summary>
   /// substring(text, start[, length]), as SQL counts from a start before the first character: ClickHouse refuses a
   /// start of 0, and counts a negative one from the end.
   /// </summary>
   private SqlExpr Substring(SqlCall c)
   {
      SqlExpr start = Int(c, 1);
      SqlExpr first = Call("greatest", start, Integer(1));
      if (c.Count == 2) { return Call("substringUTF8", c.Arg(0), first); }
      SqlExpr length = Binary(SqlBinaryOp.Subtract, Binary(SqlBinaryOp.Add, start, Int(c, 2)), first);
      return Call("substringUTF8", c.Arg(0), first, Call("greatest", length, Integer(0)));
   }

   /// <summary>coalesce(...) of decimals as one decimal type: ClickHouse's takes no decimals of different scales.</summary>
   private SqlExpr Coalesce(SqlCall c)
   {
      SqlExpr[] arguments = c.Args();
      if (c.Result.Kind != ScalarKind.Decimal) { return Call("coalesce", arguments); }
      int scale = Enumerable.Range(0, c.Count).Max(i => c.Type(i).Scale);
      ScalarType common = c.Result.Precision > 0 ? c.Result.AsNullable() : ScalarType.Decimal(38, scale);
      return Call("coalesce", arguments.Select(a => Cast(a, common)).ToArray());
   }

   /// <summary>
   /// Halves round away from zero, as the language rounds them: ClickHouse rounds a float's to even, so floats round
   /// as decimals and convert back.
   /// </summary>
   private SqlExpr Round(SqlCall c)
   {
      ScalarType type = c.Type(0);
      if (IsInteger(type)) { return c.Arg(0); }
      if (type.Kind is ScalarKind.Double or ScalarKind.Single)
      {
         SqlExpr exact = Cast(c.Arg(0), ScalarType.Decimal());
         return Cast(c.Count == 2 ? Call("round", exact, Int(c, 1)) : Call("round", exact), type);
      }
      return Call("round", TextThenInts(c));
   }

   /// <summary>A whole number; fractions are truncated, as the language defines.</summary>
   private SqlExpr Whole(SqlCall c, ScalarType type) => Cast(IsFractional(c.Type(0)) ? Call("trunc", c.Arg(0)) : c.Arg(0), type);
}
