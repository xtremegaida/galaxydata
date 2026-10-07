using System;
using System.Text;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.PostgreSql;

/// <summary>
/// PostgreSQL. Unquoted names fold to lower case, so only lower-case names are written bare. Nulls sort largest
/// by default and are placed explicitly. Parameters are written <c>@p0</c>, which Npgsql rewrites.
/// </summary>
public sealed class PostgreSqlDialect : SqlDialect
{
   private PostgreSqlDialect() { }

   public static PostgreSqlDialect Instance { get; } = new();

   public override string Name => "PostgreSQL";

   public override string ProviderKind => "postgres";

   public override int MaxParameters => 65535;

   /// <summary>PostgreSQL cuts names longer than 63 bytes short, so two long names could become one.</summary>
   protected override int MaxNameLength => 63;

   protected override int NameLength(string name) => Encoding.UTF8.GetByteCount(name);

   protected override bool IsBare(string name)
   {
      foreach (char c in name)
      {
         if (char.IsAsciiLetterUpper(c)) { return false; }
      }
      return base.IsBare(name);
   }

   protected override string TypeName(ScalarType type) => type.Kind switch
   {
      ScalarKind.Boolean => "boolean",
      ScalarKind.Int16 => "smallint",
      ScalarKind.Int32 => "integer",
      ScalarKind.Int64 => "bigint",
      ScalarKind.Decimal => type.Precision > 0 ? $"numeric({type.Precision},{type.Scale})" : "numeric",
      ScalarKind.Single => "real",
      ScalarKind.Double => "double precision",
      ScalarKind.Binary => "bytea",
      ScalarKind.Guid => "uuid",
      ScalarKind.Date => "date",
      ScalarKind.Time => "time",
      ScalarKind.DateTime => "timestamp",
      ScalarKind.DateTimeOffset => "timestamptz",
      ScalarKind.Interval => "interval",
      ScalarKind.Json => "jsonb",
      _ => "text",
   };

   protected override void WriteBinary(StringBuilder text, byte[] value) =>
      text.Append("CAST('\\x").Append(Convert.ToHexString(value)).Append("' AS bytea)");

   /// <summary>
   /// Text with line breaks is an escape string (<c>E'a\nb'</c>), which, as a literal, PostgreSQL takes as the type of
   /// what it meets (an enum, <c>jsonb</c>), as it does other text; text joined to characters would be <c>text</c>.
   /// </summary>
   protected override void WriteString(StringBuilder text, string value, ScalarType type)
   {
      if (value.AsSpan().IndexOfAny('\r', '\n') < 0)
      {
         WriteQuoted(text, value, type);
         return;
      }
      text.Append("E'");
      foreach (char c in value)
      {
         text.Append(c switch
         {
            '\\' => "\\\\",
            '\'' => "''",
            '\r' => "\\r",
            '\n' => "\\n",
            _ => c.ToString(),
         });
      }
      text.Append('\'');
   }

   /// <summary>There is no <c>%</c> for doubles; the remainder is taken in numeric.</summary>
   protected override SqlExpr Modulo(SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType) =>
      IsFloating(leftType) || IsFloating(rightType)
         ? Cast(Call("mod", Cast(left, ScalarType.Decimal()), Cast(right, ScalarType.Decimal())), ScalarType.Double)
         : new SqlBinary(SqlBinaryOp.Modulo, left, right);

   private static bool IsFloating(ScalarType type) => type.Kind is ScalarKind.Double or ScalarKind.Single;

   protected override SqlExpr? Function(SqlCall c) => c.Id switch
   {
      FunctionId.Lower => Call("lower", c.Arg(0)),
      FunctionId.Upper => Call("upper", c.Arg(0)),
      FunctionId.Trim => Call("trim", c.Arg(0)),
      FunctionId.LTrim => Call("ltrim", c.Arg(0)),
      FunctionId.RTrim => Call("rtrim", c.Arg(0)),
      FunctionId.Length => Call("length", c.Arg(0)),
      FunctionId.Substring => Call("substr", TextThenInts(c)),
      FunctionId.IndexOf => Call("strpos", c.Arg(0), c.Arg(1)),
      FunctionId.Replace => Call("replace", c.Args()),
      FunctionId.Left => Call("left", c.Arg(0), Int(c, 1)),
      FunctionId.Right => Call("right", c.Arg(0), Int(c, 1)),
      FunctionId.StartsWith => c.IsFixedText(1)
         ? Like(c.Arg(0), c.Pattern(1, PatternStyle.Like, PatternShape.Prefix))
         : Call("starts_with", c.Arg(0), c.Arg(1)),
      FunctionId.EndsWith => c.IsFixedText(1)
         ? Like(c.Arg(0), c.Pattern(1, PatternStyle.Like, PatternShape.Suffix))
         : Binary(SqlBinaryOp.Equal, Call("right", c.Arg(0), Call("length", c.Arg(1))), c.Arg(1)),
      FunctionId.Contains => c.IsFixedText(1)
         ? Like(c.Arg(0), c.Pattern(1, PatternStyle.Like, PatternShape.Contains))
         : Binary(SqlBinaryOp.Greater, Call("strpos", c.Arg(0), c.Arg(1)), Integer(0)),
      FunctionId.IContains => c.IsFixedText(1)
         ? Like(c.Arg(0), c.Pattern(1, PatternStyle.Like, PatternShape.Contains), caseInsensitive: true)
         : Binary(SqlBinaryOp.Greater, Call("strpos", Call("lower", c.Arg(0)), Call("lower", c.Arg(1))), Integer(0)),
      FunctionId.Like => Like(c.Arg(0), c.Arg(1)),
      FunctionId.ILike => Like(c.Arg(0), c.Arg(1), caseInsensitive: true),
      FunctionId.Concat => Call("concat", ConcatArguments(c)),
      FunctionId.Abs => Call("abs", c.Arg(0)),
      FunctionId.Round => Round(c),
      FunctionId.Floor => IsInteger(c.Type(0)) ? c.Arg(0) : Call("floor", c.Arg(0)),
      FunctionId.Ceiling => IsInteger(c.Type(0)) ? c.Arg(0) : Call("ceiling", c.Arg(0)),
      FunctionId.Power => Call("power", Cast(c.Arg(0), ScalarType.Double), c.Arg(1)),
      FunctionId.Sqrt => Call("sqrt", Cast(c.Arg(0), ScalarType.Double)),
      FunctionId.Sign => Cast(Call("sign", c.Arg(0)), ScalarType.Int32),
      FunctionId.Year => Extract("YEAR", c),
      FunctionId.Month => Extract("MONTH", c),
      FunctionId.Day => Extract("DAY", c),
      FunctionId.Hour => Extract("HOUR", c),
      FunctionId.Minute => Extract("MINUTE", c),
      FunctionId.Second => Cast(Call("floor", Template("EXTRACT(SECOND FROM {0})", c.Arg(0))), ScalarType.Int32),
      FunctionId.Date => Cast(c.Arg(0), ScalarType.Date),
      FunctionId.AddDays => c.Type(0).Kind == ScalarKind.Date
         ? Binary(SqlBinaryOp.Add, c.Arg(0), Int(c, 1))
         : Binary(SqlBinaryOp.Add, c.Arg(0), Binary(SqlBinaryOp.Multiply, c.Arg(1), Raw("INTERVAL '1 day'"))),
      FunctionId.AddMonths => c.Type(0).Kind == ScalarKind.Date
         ? Cast(Binary(SqlBinaryOp.Add, c.Arg(0), Binary(SqlBinaryOp.Multiply, c.Arg(1), Raw("INTERVAL '1 month'"))), ScalarType.Date)
         : Binary(SqlBinaryOp.Add, c.Arg(0), Binary(SqlBinaryOp.Multiply, c.Arg(1), Raw("INTERVAL '1 month'"))),
      FunctionId.DaysBetween => Binary(SqlBinaryOp.Subtract, Cast(c.Arg(1), ScalarType.Date), Cast(c.Arg(0), ScalarType.Date)),
      FunctionId.StartOfWeek => StartOf("week", c),
      FunctionId.StartOfMonth => StartOf("month", c),
      FunctionId.StartOfQuarter => StartOf("quarter", c),
      FunctionId.StartOfYear => StartOf("year", c),
      FunctionId.Quarter => Extract("QUARTER", c),
      FunctionId.DayOfWeek => Extract("ISODOW", c),
      FunctionId.Coalesce => Call("coalesce", c.Args()),
      FunctionId.NullIf => Call("nullif", c.Arg(0), c.Arg(1)),
      FunctionId.Iif => Iif(c.Condition(0), c.Arg(1), c.Arg(2)),
      FunctionId.Between => new SqlBetween(c.Arg(0), c.Arg(1), c.Arg(2)),
      FunctionId.ToInt => Whole(c, ScalarType.Int32),
      FunctionId.ToLong => Whole(c, ScalarType.Int64),
      FunctionId.ToDouble => Cast(Number(c), ScalarType.Double),
      FunctionId.ToDecimal => Cast(Number(c), c.Result),
      FunctionId.ToText => Cast(c.Arg(0), ScalarType.Text()),
      FunctionId.ToDate => Cast(c.Arg(0), ScalarType.Date),
      FunctionId.ToDateTime => Cast(c.Arg(0), ScalarType.DateTime),
      FunctionId.ToBool => c.Type(0).IsNumeric ? Binary(SqlBinaryOp.NotEqual, c.Arg(0), Integer(0)) : Cast(c.Arg(0), ScalarType.Boolean),
      _ => null,
   };

   private SqlExpr Extract(string field, SqlCall c) => Cast(Template($"EXTRACT({field} FROM {{0}})", c.Arg(0)), ScalarType.Int32);

   /// <summary>
   /// The first day of the date's week (a Monday), month, quarter or year: truncated as a timestamp, a date-time with an
   /// offset at its UTC time (as the sessions are), so neither the date's cast nor the session's zone moves the day.
   /// </summary>
   private SqlExpr StartOf(string unit, SqlCall c)
   {
      SqlExpr value = c.Type(0).Kind switch
      {
         ScalarKind.Date => Cast(c.Arg(0), ScalarType.DateTime),
         ScalarKind.DateTimeOffset => Template("({0} AT TIME ZONE 'UTC')", c.Arg(0)),
         _ => c.Arg(0),
      };
      return Cast(Call("date_trunc", Text(unit), value), ScalarType.Date);
   }

   /// <summary>
   /// Doubles round as numeric and convert back: round(double, digits) doesn't exist, and round(double) rounds halves
   /// to even, where the language (and numeric) rounds them away from zero.
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

   private SqlExpr Whole(SqlCall c, ScalarType type) =>
      Cast(IsFractional(c.Type(0)) ? Call("trunc", c.Arg(0)) : Number(c), type);

   /// <summary>The argument as a number: a boolean as 1 or 0, which only integer casts from.</summary>
   private SqlExpr Number(SqlCall c) => c.Type(0).Kind == ScalarKind.Boolean ? Cast(c.Arg(0), ScalarType.Int32) : c.Arg(0);

   /// <summary>PostgreSQL's functions that read the server's files, reach other servers, or change its settings.</summary>
   protected override ChangeScriptRules ChangeRules { get; } = new()
   {
      ForbiddenFunctions = ChangeScriptRules.Words(
         "pg_read_file", "pg_read_binary_file", "pg_ls_dir", "pg_stat_file", "lo_import", "lo_export", "dblink", "dblink_exec",
         "pg_terminate_backend", "pg_cancel_backend", "pg_reload_conf", "pg_rotate_logfile", "set_config"),
   };

   protected override ScriptSyntax ScriptSyntax { get; } = new() { DollarQuotes = true, EscapeStrings = true };
}
