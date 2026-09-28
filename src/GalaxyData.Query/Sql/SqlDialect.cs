using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Sql;

internal enum PagingStyle : byte
{
   /// <summary><c>LIMIT n OFFSET m</c>; an offset alone is written <c>OFFSET m</c>.</summary>
   LimitOffset,

   /// <summary><c>LIMIT n OFFSET m</c>; an offset alone needs <c>LIMIT -1</c>.</summary>
   SqliteLimitOffset,

   /// <summary><c>TOP (n)</c>, or <c>OFFSET m ROWS FETCH NEXT n ROWS ONLY</c>, which needs an ORDER BY.</summary>
   TopOrOffsetFetch,
}

/// <summary>
/// How one database spells SQL: names, literals, parameters, paging, null ordering, and each language function.
/// A function a dialect has no translation for can't run in that database. All dialects live in this assembly.
/// </summary>
public abstract class SqlDialect
{
   private protected SqlDialect() { }

   public static SqlDialect Sqlite { get; } = new SqliteDialect();

   public static SqlDialect DuckDb { get; } = new DuckDbDialect();

   public static SqlDialect PostgreSql { get; } = new PostgreSqlDialect();

   public static SqlDialect SqlServer { get; } = new SqlServerDialect();

   public static IReadOnlyList<SqlDialect> All { get; } = [Sqlite, DuckDb, PostgreSql, SqlServer];

   /// <summary>The name for people: <c>SQLite</c>.</summary>
   public abstract string Name { get; }

   /// <summary>The provider kind of sources that speak this dialect: <c>sqlite</c>.</summary>
   public abstract string ProviderKind { get; }

   /// <summary>The most parameters one statement may take.</summary>
   public abstract int MaxParameters { get; }

   /// <summary>Whether a condition is also a value; SQL Server needs CASE to select one and <c>= 1</c> to test a bit.</summary>
   internal virtual bool HasBooleanValues => true;

   internal virtual PagingStyle Paging => PagingStyle.LimitOffset;

   internal virtual string ConcatOperator => "||";

   /// <summary>
   /// Whether a value used in several places can be one parameter. DuckDB types a parameter from its first use, so
   /// a shared one can be wrong for the others (<c>left(name, $n)</c> makes it BIGINT, then <c>date + $n</c> fails).
   /// </summary>
   internal virtual bool SharesParameters => true;

   /// <summary>The name as written in SQL: bare when that is safe, quoted otherwise.</summary>
   public string Identifier(string name) => IsBare(name) ? name : QuoteIdentifier(name);

   public virtual string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

   /// <summary>A name that needs no quotes: letters, digits and underscores, not starting with a digit, and not a keyword.</summary>
   private protected virtual bool IsBare(string name)
   {
      if (name.Length == 0 || char.IsAsciiDigit(name[0])) { return false; }
      foreach (char c in name)
      {
         if (!char.IsAsciiLetterOrDigit(c) && c != '_') { return false; }
      }
      return !SqlKeywords.IsKeyword(name);
   }

   /// <summary>A parameter as written in the SQL text.</summary>
   public virtual string Placeholder(string name) => "@" + name;

   /// <summary>The name to give the ADO.NET parameter object.</summary>
   public virtual string ParameterName(string name) => name;

   /// <summary>The type to CAST to for a logical type.</summary>
   internal abstract string TypeName(ScalarType type);

   internal virtual string BooleanLiteral(bool value) => value ? "TRUE" : "FALSE";

   /// <summary>How to write ORDER BY null placement so nulls sort smallest; null when the default already does.</summary>
   internal virtual string? NullOrdering(bool descending) => descending ? "NULLS LAST" : "NULLS FIRST";

   /// <summary>Writes a constant; used for literals the SQL must contain and for display.</summary>
   internal virtual void WriteLiteral(StringBuilder text, object? value, ScalarType type)
   {
      switch (value)
      {
         case null:
            text.Append("NULL");
            break;
         case bool flag:
            text.Append(BooleanLiteral(flag));
            break;
         case string s:
            WriteString(text, s, type);
            break;
         case long or int or short or byte or sbyte:
            text.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            break;
         case decimal number:
            text.Append(number.ToString(CultureInfo.InvariantCulture));
            break;
         case double or float:
            string written = Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            text.Append(written);
            if (written.AsSpan().IndexOfAny(".E") < 0) { text.Append(".0"); }
            break;
         case DateOnly date:
            WriteTemporal(text, "DATE", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            break;
         case TimeOnly time:
            WriteTemporal(text, "TIME", time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
            break;
         case DateTime dateTime:
            WriteTemporal(text, "TIMESTAMP", dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
            break;
         case DateTimeOffset offset:
            WriteTemporal(text, "TIMESTAMPTZ", offset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
            break;
         case Guid guid:
            WriteGuid(text, guid);
            break;
         case byte[] bytes:
            WriteBinary(text, bytes);
            break;
         default:
            WriteString(text, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, type);
            break;
      }
   }

   private protected virtual void WriteString(StringBuilder text, string value, ScalarType type) =>
      text.Append('\'').Append(value.Replace("'", "''", StringComparison.Ordinal)).Append('\'');

   private protected virtual void WriteTemporal(StringBuilder text, string keyword, string value) =>
      text.Append(keyword).Append(" '").Append(value).Append('\'');

   private protected virtual void WriteGuid(StringBuilder text, Guid value) => text.Append("CAST('").Append(value.ToString("D")).Append("' AS ").Append(TypeName(ScalarType.Guid)).Append(')');

   private protected abstract void WriteBinary(StringBuilder text, byte[] value);

   /// <summary>Division; two whole numbers give a double in the language, not a truncated whole number.</summary>
   internal virtual SqlExpr Divide(SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType) =>
      leftType.IsInteger && rightType.IsInteger
         ? new SqlBinary(SqlBinaryOp.Divide, new SqlCast(left, TypeName(ScalarType.Double)), right)
         : new SqlBinary(SqlBinaryOp.Divide, left, right);

   /// <summary>The remainder, with the sign of the dividend.</summary>
   internal virtual SqlExpr Modulo(SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType) =>
      new SqlBinary(SqlBinaryOp.Modulo, left, right);

   /// <summary>A comparison; dialects adjust operands whose storage doesn't compare the way the language does.</summary>
   internal virtual SqlExpr Compare(SqlBinaryOp op, SqlExpr left, SqlExpr right, ScalarType leftType, ScalarType rightType) =>
      new SqlBinary(op, left, right);

   /// <summary>
   /// An aggregate function. A sum is 0 when there are no values, as the language defines, where SQL gives null.
   /// </summary>
   internal virtual SqlExpr Aggregate(AggregateFunction function, SqlExpr? argument, ScalarType? argumentType) => function switch
   {
      AggregateFunction.CountRows => new SqlAggregate(AggregateName("count"), null),
      AggregateFunction.Count => new SqlAggregate(AggregateName("count"), argument),
      AggregateFunction.CountDistinct => new SqlAggregate(AggregateName("count"), argument, distinct: true),
      AggregateFunction.Sum => new SqlFunctionCall(AggregateName("coalesce"), new SqlAggregate(AggregateName("sum"), argument), new SqlLiteral(0L, ScalarType.Int64)),
      AggregateFunction.Avg => new SqlAggregate(AggregateName("avg"), argument),
      AggregateFunction.Min => new SqlAggregate(AggregateName("min"), argument),
      _ => new SqlAggregate(AggregateName("max"), argument),
   };

   /// <summary>The spelling of a function name in this dialect.</summary>
   internal virtual string AggregateName(string name) => name;

   /// <summary>A call of a language function; null when this database can't run it.</summary>
   internal abstract SqlExpr? Function(SqlCall call);

   public override string ToString() => Name;

   #region Helpers for translations

   private protected static SqlExpr Call(string name, params SqlExpr[] arguments) => new SqlFunctionCall(name, arguments);

   private protected SqlExpr Cast(SqlExpr operand, ScalarType type) => new SqlCast(operand, TypeName(type));

   private protected static SqlExpr Template(string format, params SqlExpr[] arguments) => new SqlTemplate(format, arguments);

   private protected static SqlExpr Binary(SqlBinaryOp op, SqlExpr left, SqlExpr right) => new SqlBinary(op, left, right);

   private protected static SqlExpr Text(string value) => new SqlLiteral(value, ScalarType.Text().AsNonNullable());

   private protected static SqlExpr Integer(long value) => new SqlLiteral(value, ScalarType.Int64.AsNonNullable());

   private protected static SqlExpr Raw(string text) => new SqlRaw(text);

   private protected static SqlExpr Iif(SqlExpr condition, SqlExpr whenTrue, SqlExpr whenFalse) =>
      new SqlCase([new SqlWhen(condition, whenTrue)], whenFalse);

   private protected static SqlExpr Like(SqlExpr operand, SqlExpr pattern, bool caseInsensitive = false) =>
      new SqlLike(operand, pattern, caseInsensitive, '\\');

   /// <summary><c>'true'</c> or <c>'false'</c> for a condition, null for null.</summary>
   private protected static SqlExpr BooleanText(SqlCall c) => c.Type(0).Nullable
      ? new SqlCase([new SqlWhen(c.Condition(0), Text("true")), new SqlWhen(new SqlUnary(SqlUnaryOp.Not, c.Condition(0)), Text("false"))], null)
      : new SqlCase([new SqlWhen(c.Condition(0), Text("true"))], Text("false"));

   private protected static bool IsInteger(ScalarType type) => type.IsInteger;

   /// <summary>
   /// Argument <paramref name="index"/> as an int for functions that take one (a count of characters, days or digits):
   /// a 64-bit value, such as a computed column, is cast, since <c>date + bigint</c> and <c>left(text, bigint)</c> don't exist everywhere.
   /// </summary>
   private protected SqlExpr Int(SqlCall c, int index) => c.Type(index).Kind == ScalarKind.Int64 ? Cast(c.Arg(index), ScalarType.Int32) : c.Arg(index);

   /// <summary>The text argument, then the rest as ints: substring(text, start[, length]).</summary>
   private protected SqlExpr[] TextThenInts(SqlCall c)
   {
      SqlExpr[] arguments = new SqlExpr[c.Count];
      arguments[0] = c.Arg(0);
      for (int i = 1; i < arguments.Length; i++) { arguments[i] = Int(c, i); }
      return arguments;
   }

   private protected static bool IsFractional(ScalarType type) => type.Kind is ScalarKind.Decimal or ScalarKind.Single or ScalarKind.Double;

   #endregion
}

/// <summary>A function call being translated: its arguments as SQL, their logical types, and the result type.</summary>
internal sealed class SqlCall
{
   private readonly SqlBuilder builder;
   private readonly PlanFunction plan;
   private readonly IReadOnlyDictionary<PlanColumn, SqlExpr> columns;

   public SqlCall(SqlBuilder builder, PlanFunction plan, IReadOnlyDictionary<PlanColumn, SqlExpr> columns)
   {
      this.builder = builder;
      this.plan = plan;
      this.columns = columns;
   }

   public FunctionId Id => plan.Function.Id;

   public int Count => plan.Arguments.Count;

   public ScalarType Result => plan.Type;

   public ScalarType Type(int index) => plan.Arguments[index].Type;

   /// <summary>Argument <paramref name="index"/> as a value.</summary>
   public SqlExpr Arg(int index) => builder.Value(plan.Arguments[index], columns);

   /// <summary>Argument <paramref name="index"/> as a condition.</summary>
   public SqlExpr Condition(int index) => builder.Condition(plan.Arguments[index], columns);

   public SqlExpr[] Args()
   {
      SqlExpr[] arguments = new SqlExpr[Count];
      for (int i = 0; i < arguments.Length; i++) { arguments[i] = Arg(i); }
      return arguments;
   }

   /// <summary>Whether the argument is text known when the query runs (a constant or a parameter), so it can become a pattern.</summary>
   public bool IsFixedText(int index) =>
      plan.Arguments[index] is PlanLiteral { Value: string } or PlanParameter { Source: ParameterSource.User };

   /// <summary>A parameter holding argument <paramref name="index"/> turned into a pattern; only for <see cref="IsFixedText"/> arguments.</summary>
   public SqlExpr Pattern(int index, PatternStyle style, PatternShape shape) =>
      builder.PatternParameter(plan.Arguments[index], new PatternTransform(style, shape));
}
