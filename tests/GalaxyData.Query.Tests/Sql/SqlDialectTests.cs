using System;
using System.Text;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Tests.Planning;
using GalaxyData.Query.Types;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Sql;

public sealed class SqlDialectTests
{
   [Theory]
   [InlineData("sqlite", "orders", "orders")]
   [InlineData("sqlite", "Line Item", "\"Line Item\"")]
   [InlineData("sqlite", "order", "\"order\"")]
   [InlineData("sqlite", "2nd", "\"2nd\"")]
   [InlineData("sqlite", "Orders", "Orders")]
   [InlineData("postgres", "Orders", "\"Orders\"")]
   [InlineData("postgres", "say \"hi\"", "\"say \"\"hi\"\"\"")]
   [InlineData("sqlserver", "Line Item", "[Line Item]")]
   [InlineData("sqlserver", "a]b", "[a]]b]")]
   [InlineData("duckdb", "at", "\"at\"")]
   [InlineData("clickhouse", "Orders", "Orders")]
   [InlineData("clickhouse", "a\"b\\c", "\"a\"\"b\\\\c\"")]
   public void NamesAreQuotedOnlyWhenNeeded(string dialect, string name, string expected)
   {
      Dialect(dialect).Identifier(name).ShouldBe(expected);
   }

   [Theory]
   [InlineData("sqlite", "@p0", "p0")]
   [InlineData("duckdb", "$p0", "p0")]
   [InlineData("postgres", "@p0", "p0")]
   [InlineData("sqlserver", "@p0", "@p0")]
   [InlineData("clickhouse", "@p0", "p0")]
   public void ParametersFollowTheProvider(string dialect, string placeholder, string parameterName)
   {
      Dialect(dialect).Placeholder("p0").ShouldBe(placeholder);
      Dialect(dialect).ParameterName("p0").ShouldBe(parameterName);
   }

   [Theory]
   [InlineData("sqlite", "'it''s'")]
   [InlineData("sqlserver", "N'it''s'")]
   [InlineData("postgres", "'it''s'")]
   [InlineData("clickhouse", "'it''s'")]
   public void TextLiteralsDoubleTheirQuotes(string dialect, string expected)
   {
      Literal(dialect, "it's", ScalarType.Text()).ShouldBe(expected);
   }

   [Theory]
   [InlineData("sqlite", "('it''s' || char(13) || char(10) || 'a\\b' || char(10))")]
   [InlineData("duckdb", "('it''s' || chr(13) || chr(10) || 'a\\b' || chr(10))")]
   [InlineData("postgres", "E'it''s\\r\\na\\\\b\\n'")]
   [InlineData("sqlserver", "(N'it''s' + NCHAR(13) + NCHAR(10) + N'a\\b' + NCHAR(10))")]
   [InlineData("clickhouse", "('it''s' || char(13) || char(10) || 'a\\\\b' || char(10))")]
   public void TextWithLineBreaksIsWrittenWithoutThem(string dialect, string expected)
   {
      // An editor may make a script's line breaks another kind; its values' stay as they are.
      Literal(dialect, "it's\r\na\\b\n", ScalarType.Text()).ShouldBe(expected);
      Literal(dialect, "\n", ScalarType.Text()).ShouldNotContain("\n");
   }

   [Fact]
   public void AnsiTextWithLineBreaksIsNotUnicodeInSqlServer()
   {
      Literal("sqlserver", "\nx", ScalarType.Text(20, ansi: true)).ShouldBe("(CHAR(10) + 'x')");
   }

   [Fact]
   public void AnsiTextIsNotUnicodeInSqlServer()
   {
      Literal("sqlserver", "open", ScalarType.Text(20, ansi: true)).ShouldBe("'open'");
   }

   [Theory]
   [InlineData("sqlite", "'2026-01-05'")]
   [InlineData("duckdb", "DATE '2026-01-05'")]
   [InlineData("postgres", "DATE '2026-01-05'")]
   [InlineData("sqlserver", "CAST('2026-01-05' AS date)")]
   [InlineData("clickhouse", "toDate32('2026-01-05')")]
   public void DatesAreWrittenForEachDatabase(string dialect, string expected)
   {
      Literal(dialect, new DateOnly(2026, 1, 5), ScalarType.Date).ShouldBe(expected);
   }

   [Theory]
   [InlineData("sqlite", "X'0AFF'")]
   [InlineData("duckdb", "CAST('\\x0A\\xFF' AS BLOB)")]
   [InlineData("postgres", "CAST('\\x0AFF' AS bytea)")]
   [InlineData("sqlserver", "0x0AFF")]
   [InlineData("clickhouse", "unhex('0AFF')")]
   public void BinaryIsWrittenForEachDatabase(string dialect, string expected)
   {
      Literal(dialect, new byte[] { 0x0A, 0xFF }, ScalarType.Binary).ShouldBe(expected);
   }

   [Fact]
   public void ClickHouseWritesDateTimesToTheMicrosecondAndInstantsInUtc()
   {
      Literal("clickhouse", new DateTime(2026, 1, 5, 10, 11, 12, 123).AddTicks(4567), ScalarType.DateTime).ShouldBe("toDateTime64('2026-01-05 10:11:12.123456', 6)");
      Literal("clickhouse", new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.FromHours(2)), ScalarType.DateTimeOffset)
         .ShouldBe("toDateTime64('2026-01-05 10:00:00.000000', 6, 'UTC')");
      Literal("clickhouse", Guid.Parse("6f2c4d2e-1f0a-4c1b-9d7e-2b3a4c5d6e7f"), ScalarType.Guid).ShouldBe("toUUID('6f2c4d2e-1f0a-4c1b-9d7e-2b3a4c5d6e7f')");
      Should.Throw<NotSupportedException>(() => Literal("clickhouse", new TimeOnly(10, 0), ScalarType.Time));
   }

   [Fact]
   public void DoublesAlwaysLookFractional()
   {
      Literal("sqlite", 3d, ScalarType.Double).ShouldBe("3.0");
      Literal("sqlite", 0.1d, ScalarType.Double).ShouldBe("0.1");
   }

   [Theory]
   [InlineData(PatternStyle.Like, PatternShape.Prefix, "50%_off\\", "50\\%\\_off\\\\%")]
   [InlineData(PatternStyle.Like, PatternShape.Contains, "[a]", "%[a]%")]
   [InlineData(PatternStyle.LikeWithBrackets, PatternShape.Suffix, "[a]", "%\\[a]")]
   [InlineData(PatternStyle.Glob, PatternShape.Prefix, "a*b?[c]", "a[*]b[?][[]c]*")]
   public void PatternsMatchTheirTextLiterally(PatternStyle style, PatternShape shape, string text, string expected)
   {
      new PatternTransform(style, shape).Apply(text).ShouldBe(expected);
   }

   [Fact]
   public void ConstantsCanBeWrittenInline()
   {
      SqlStatement statement = SqlBuilder.Build(PlanCases.Plan("sales.orders.where(status == 'open' and total > 3.5).take(10)"),
         TestDialects.PostgreSql, new SqlBuildOptions { InlineConstants = true });
      statement.Text.ShouldContain("WHERE o.status = 'open' AND o.total > 3.5");
      statement.Text.ShouldContain("LIMIT 10");
      statement.Parameters.ShouldBeEmpty();
   }

   [Fact]
   public void EqualConstantsShareAParameter()
   {
      SqlStatement statement = SqlBuilder.Build(PlanCases.Plan("sales.orders.where(status == 'open' or status == 'open')"), TestDialects.Sqlite);
      statement.Text.ShouldContain("o.status = @p0 OR o.status = @p0");
      statement.Parameters.ShouldHaveSingleItem();
   }

   private static SqlDialect Dialect(string providerKind) => Array.Find([.. TestDialects.All], d => d.ProviderKind == providerKind)!;

   private static string Literal(string dialect, object value, ScalarType type)
   {
      StringBuilder text = new();
      Dialect(dialect).WriteLiteral(text, value, type);
      return text.ToString();
   }
}
