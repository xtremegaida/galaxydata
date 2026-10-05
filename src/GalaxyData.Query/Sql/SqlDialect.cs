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

/// <summary>How a data change gives back the rows it wrote.</summary>
internal enum ReturningStyle : byte
{
   /// <summary><c>INSERT ... VALUES (...) RETURNING a, b</c>.</summary>
   Returning,

   /// <summary>SQL Server's <c>INSERT ... OUTPUT INSERTED.a, INSERTED.b VALUES (...)</c>, which a table with triggers doesn't take.</summary>
   Output,
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

   /// <summary>
   /// Whether <c>column = value</c> in this database finds the rows whose value, read as <paramref name="type"/>,
   /// equals the value, and no others that differ once read; text may still match more (collations that ignore case
   /// or trailing spaces). Fragments are only fetched by keys that compare exactly.
   /// </summary>
   internal virtual bool ComparesExactly(ScalarType type) =>
      type.Kind is not (ScalarKind.Unknown or ScalarKind.Json or ScalarKind.Binary or ScalarKind.Single or ScalarKind.Double);

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

   /// <summary>
   /// Writes a constant; used for literals the SQL must contain, for display, and in scripts people may edit and run.
   /// <paramref name="columnType"/> is the declared type of the column the value is compared with or stored in, when known.
   /// </summary>
   internal virtual void WriteLiteral(StringBuilder text, object? value, ScalarType type, string? columnType = null)
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
            // A float written as a double would have digits it doesn't (1.1f is 1.100000023841858).
            string written = value is float single ? single.ToString("R", CultureInfo.InvariantCulture) : ((double)value).ToString("R", CultureInfo.InvariantCulture);
            text.Append(written);
            if (written.AsSpan().IndexOfAny(".E") < 0) { text.Append(".0"); }
            break;
         case TimeSpan span:
            text.Append("INTERVAL '").Append(span.TotalSeconds.ToString("R", CultureInfo.InvariantCulture)).Append(" seconds'");
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

   /// <summary>
   /// Text, quoted. Its line breaks (CR, LF) are written as characters by their codes, joined to the rest
   /// (<c>('a' || chr(10) || 'b')</c>), so a script's text has none in its values: an editor that changes a script's
   /// line breaks (to one kind, as Monaco does) changes no value.
   /// </summary>
   private protected virtual void WriteString(StringBuilder text, string value, ScalarType type)
   {
      if (value.AsSpan().IndexOfAny('\r', '\n') < 0)
      {
         WriteQuoted(text, value, type);
         return;
      }
      text.Append('(');
      bool first = true;
      int start = 0;
      for (int i = 0; i <= value.Length; i++)
      {
         if (i < value.Length && value[i] is not ('\r' or '\n')) { continue; }
         if (i > start)
         {
            if (!first) { text.Append(Concatenation); }
            WriteQuoted(text, value[start..i], type);
            first = false;
         }
         if (i < value.Length)
         {
            if (!first) { text.Append(Concatenation); }
            WriteCharacter(text, value[i], type);
            first = false;
         }
         start = i + 1;
      }
      text.Append(')');
   }

   private protected virtual void WriteQuoted(StringBuilder text, string value, ScalarType type) =>
      text.Append('\'').Append(value.Replace("'", "''", StringComparison.Ordinal)).Append('\'');

   /// <summary>How texts are joined.</summary>
   private protected virtual string Concatenation => " || ";

   /// <summary>A character by its code, as text.</summary>
   private protected virtual void WriteCharacter(StringBuilder text, char value, ScalarType type) =>
      text.Append("chr(").Append(((int)value).ToString(CultureInfo.InvariantCulture)).Append(')');

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

   /// <summary>Text's length as the database counts it against a column's: in characters, or UTF-16 units (SQL Server).</summary>
   internal virtual int TextLength(string text)
   {
      int count = 0;
      foreach (Rune _ in text.EnumerateRunes()) { count++; }
      return count;
   }

   /// <summary>The longest name the database takes, as <see cref="NameLength"/> counts: longer aliases are cut short.</summary>
   internal virtual int MaxNameLength => int.MaxValue;

   /// <summary>A name's length as the database limits it: in characters, or bytes.</summary>
   private protected virtual int NameLength(string name) => name.Length;

   /// <summary><paramref name="name"/>, cut short so that with <paramref name="suffix"/> it fits <see cref="MaxNameLength"/>.</summary>
   internal string FitName(string name, string suffix)
   {
      string fitted = name;
      while (fitted.Length > 0 && NameLength(fitted + suffix) > MaxNameLength)
      {
         fitted = fitted[..^(fitted.Length > 1 && char.IsLowSurrogate(fitted[^1]) ? 2 : 1)];
      }
      return fitted + suffix;
   }

   /// <summary>How a data change gives back the row it wrote.</summary>
   internal virtual ReturningStyle Returning => ReturningStyle.Returning;

   /// <summary>Whether an insert may give an identity column its value (SQL Server's take one only with IDENTITY_INSERT on).</summary>
   internal virtual bool AcceptsIdentityValues => true;

   /// <summary>
   /// The count of rows the last statement changed, as a value, where the count the database reports for a statement
   /// isn't that (SQL Server adds the rows its triggers changed, and reports none with NOCOUNT on): changes select
   /// it after them. Null where the reported count is the statement's own.
   /// </summary>
   internal virtual SqlExpr? RowCountOfChange => null;

   /// <summary>The identity value the last insert gave, as a value: to read an inserted row back where it can't be given back.</summary>
   internal virtual SqlExpr? InsertedIdentity => null;

   /// <summary>
   /// Whether a change checks that a column still has the value it was read with, where it compares with
   /// <c>=</c>: the column's value must equal it when read and sent back, as it does for the types that compare exactly.
   /// </summary>
   internal virtual bool ComparesOriginal(ScalarType type, string? nativeType) => ComparesExactly(type);

   /// <summary>
   /// The condition that a key column equals a value, to find the row a change is for: <c>column = value</c>, or, where
   /// the database may keep a key in another form than the value's, both in one form.
   /// </summary>
   internal virtual SqlExpr KeyEquals(SqlColumn column, SqlExpr value, ScalarType type) => new SqlBinary(SqlBinaryOp.Equal, column, value);

   /// <summary>A sort key; dialects adjust values whose storage doesn't sort the way the language does.</summary>
   internal virtual SqlExpr SortKey(SqlExpr key, ScalarType type) => key;

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
   private protected static SqlExpr BooleanText(SqlCall c, int index = 0) => c.Type(index).Nullable
      ? new SqlCase([new SqlWhen(c.Condition(index), Text("true")), new SqlWhen(new SqlUnary(SqlUnaryOp.Not, c.Condition(index)), Text("false"))], null)
      : new SqlCase([new SqlWhen(c.Condition(index), Text("true"))], Text("false"));

   /// <summary>
   /// The arguments of concat(...), true/false values written as <c>true</c> and <c>false</c>, as toString writes
   /// them: databases write them as 1 and 0, t and f, or true and false.
   /// </summary>
   private protected static SqlExpr[] ConcatArguments(SqlCall c)
   {
      SqlExpr[] arguments = new SqlExpr[c.Count];
      for (int i = 0; i < arguments.Length; i++) { arguments[i] = c.Type(i).Kind == ScalarKind.Boolean ? BooleanText(c, i) : c.Arg(i); }
      return arguments;
   }

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
