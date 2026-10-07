using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Tests.Sql;
using GalaxyData.Query.Types;
using Shouldly;
using Xunit;
using static GalaxyData.Query.Tests.Catalog.Schemas;

namespace GalaxyData.Query.Tests.Dml;

/// <summary>
/// Edges of changes that need no database: value conversion, the planner's issues and ordering, the script splitter
/// and the guard, against attempts to get other statements past it.
/// </summary>
public sealed class DmlEdgeTests
{
   #region Catalogs and helpers

   /// <summary>A table with a column of (nearly) every logical type, each nullable, to convert values against.</summary>
   private static QueryCatalog KindsCatalog()
   {
      TableSchema t = Table("t",
         Col("id", "int32") with { IsIdentity = true },
         Col("i16", "int16?"), Col("i32", "int32?"), Col("i64", "int64?"),
         Col("dec", "decimal(10,2)?"), Col("dec0", "decimal(5,0)?"), Col("decw", "decimal(38,10)?"),
         Col("f32", "single?"), Col("f64", "double?"),
         Col("s5", "string(5)?"), Col("sa5", "string(5,ansi)?"), Col("smax", "string?"),
         Col("b", "boolean?"),
         Col("d", "date?"), Col("tm", "time?"), Col("dt", "datetime?"), Col("dto", "datetimeoffset?"),
         Col("g", "guid?"), Col("bin", "binary(4)?"), Col("iv", "interval?"), Col("js", "json?"),
         Col("un", "unknown?") with { NativeType = "xml" }) with { PrimaryKey = Pk("id") };
      return Build(("k", Source(t)));
   }

   private static TableEntity Kinds() => (TableEntity)KindsCatalog().Entity("k.t");

   private static ColumnDef Column(string name) => Kinds().FindColumn(name).Item ?? throw new KeyNotFoundException(name);

   private static (bool Ok, object? Value, string? Problem) Convert(string column, object? value)
   {
      bool ok = DmlValues.TryConvert(Column(column), value, TestDialects.PostgreSql, out object? converted, out string? problem);
      return (ok, converted, problem);
   }

   private static readonly Guid Guid1 = new("2f1c0000-0000-4000-8000-000000000001");

   public static Dictionary<string, object?> Row(params (string Column, object? Value)[] values) =>
      values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal);

   /// <summary>The shop fixture's shape with the usual keys, triggers and a keyless table.</summary>
   private static QueryCatalog ShopCatalog(CatalogOverlay? overlay = null)
   {
      SourceSchema shop = Source(
         Table("customers", Col("id", "int32") with { IsIdentity = true }, Col("name", "string(100)"), Col("city", "string(40)?")) with { PrimaryKey = Pk("id") },
         Table("orders", Col("id"), Col("customer_id", "int32"), Col("status", "string(20)"), Col("total", "decimal(10,2)")) with
         {
            PrimaryKey = Pk("id"),
            ForeignKeys = [Fk("customer_id", "customers")],
            HasTriggers = true,
         },
         Table("order_lines", Col("order_id"), Col("line_no", "int32"), Col("qty", "int32")) with
         {
            PrimaryKey = Pk("order_id", "line_no"),
            ForeignKeys = [Fk("order_id", "orders")],
         },
         Table("employees", Col("id"), Col("name", "string"), Col("manager_id", "int64?")) with
         {
            PrimaryKey = Pk("id"),
            ForeignKeys = [Fk("manager_id", "employees")],
         },
         Table("notes", Col("at", "datetime?"), Col("text", "string?"), Col("score", "double?")),
         View("open_orders", Col("id"), Col("total", "decimal(10,2)")));
      return Build(overlay ?? CatalogOverlay.Empty, ("shop", shop));
   }

   private static TableEntity ShopTable(QueryCatalog catalog, string name) => (TableEntity)catalog.Entity("shop." + name);

   private static DmlPlan Plan(QueryCatalog catalog, SqlDialect dialect, params RowChange[] changes) =>
      DmlPlanner.Plan(new ChangeSet(changes), _ => dialect);

   private static SqlDialect DialectOf(string kind) => TestDialects.All.Single(d => d.ProviderKind == kind);

   private static string[] Split(string kind, string script)
   {
      SplitScript split = SqlScriptSplitter.Split(script, DialectOf(kind));
      split.Problems.ShouldBeEmpty();
      return split.Statements.Select(s => s.Text).ToArray();
   }

   private static IReadOnlyList<ScriptProblem> Guard(string kind, string script, bool any = false) =>
      DmlGuard.Check(SqlScriptSplitter.Split(script, DialectOf(kind)), DialectOf(kind), any);

   #endregion

   #region Value conversion: numbers

   [Fact]
   public void IntegerStringConverts() => Convert("i32", "123").ShouldBe((true, 123, null));

   [Fact]
   public void IntegerStringWithSpacesConverts() => Convert("i32", " 123 ").Value.ShouldBe(123);

   [Fact]
   public void IntegerStringThatIsntFails()
   {
      (bool ok, _, string? problem) = Convert("i32", "two");
      ok.ShouldBeFalse();
      problem.ShouldBe("'i32' takes a whole number; 'two' isn't one");
   }

   [Fact]
   public void IntegerOutOfRangeFails()
   {
      (bool ok, _, string? problem) = Convert("i32", 70000L * 70000);
      ok.ShouldBeFalse();
      problem!.ShouldContain("out of its range");
   }

   [Fact]
   public void Int16BoundariesConvert()
   {
      Convert("i16", (int)short.MaxValue).ShouldBe((true, (short)32767, null));
      Convert("i16", (int)short.MinValue).Value.ShouldBe((short)-32768);
   }

   [Fact]
   public void Int16OverflowFails() => Convert("i16", 40000).Ok.ShouldBeFalse();

   [Fact]
   public void Int64MaxConverts() => Convert("i64", long.MaxValue).ShouldBe((true, long.MaxValue, null));

   [Fact]
   public void DecimalStringConverts() => Convert("dec", "12.50").Value.ShouldBe(12.50m);

   [Fact]
   public void DecimalKeepsScale() => Convert("dec", 12.5m).Value!.ToString().ShouldBe("12.50");

   [Fact]
   public void DecimalTooManyPlacesFails()
   {
      (bool ok, _, string? problem) = Convert("dec", 1.005m);
      ok.ShouldBeFalse();
      problem!.ShouldContain("has more");
   }

   [Fact]
   public void DecimalTooLargeFails()
   {
      (bool ok, _, string? problem) = Convert("dec", 123456789m);
      ok.ShouldBeFalse();
      problem!.ShouldContain("is too large");
   }

   [Fact]
   public void DecimalAtPrecisionBoundaryFits() => Convert("dec", 99999999.99m).Ok.ShouldBeTrue();

   [Fact]
   public void DecimalJustOverPrecisionFails() => Convert("dec", 100000000.00m).Ok.ShouldBeFalse();

   [Fact]
   public void DecimalFromDoubleConverts() => Convert("dec", 0.1).Value.ShouldBe(0.10m);

   [Fact]
   public void DecimalTrailingZerosThatNormalizeAwayFit() => Convert("dec", 12.3400m).Value.ShouldBe(12.34m);

   [Fact]
   public void DecimalZeroScaleRejectsFraction() => Convert("dec0", 1.5m).Ok.ShouldBeFalse();

   [Fact]
   public void DecimalZeroScaleAcceptsWhole() => Convert("dec0", 12345m).Ok.ShouldBeTrue();

   [Fact]
   public void NegativeDecimalCountsTheSameAsPositive() => Convert("dec", -99999999.99m).Ok.ShouldBeTrue();

   [Fact]
   public void WideDecimalAtPrecisionBoundaryFits()
   {
      // decimal(38,10): 28 integer digits allowed; 28 nines should fit.
      (bool ok, _, string? problem) = Convert("decw", 9999999999999999999999999999m);
      ok.ShouldBeTrue(problem);
   }

   [Fact]
   public void WideDecimalWithFractionFits() => Convert("decw", 1234.1234567890m).Ok.ShouldBeTrue();

   [Fact]
   public void DecimalExponentStringConverts() => Convert("dec", "1E2").Value.ShouldBe(100.00m);

   [Fact]
   public void DecimalZeroConvertsWithScale() => Convert("dec", 0m).Value.ShouldBe(0.00m);

   [Fact]
   public void SingleConvertsFromDouble() => Convert("f32", 1.5).Value.ShouldBe(1.5f);

   [Fact]
   public void DoubleConverts() => Convert("f64", 2.25).Value.ShouldBe(2.25);

   [Fact]
   public void StringBooleanWordsConvert()
   {
      Convert("b", "true").Value.ShouldBe(true);
      Convert("b", "false").Value.ShouldBe(false);
      Convert("b", "yes").Value.ShouldBe(true);
      Convert("b", "t").Value.ShouldBe(true);
      Convert("b", "0").Value.ShouldBe(false);
   }

   [Fact]
   public void StringBooleanGibberishFails() => Convert("b", "maybe").Ok.ShouldBeFalse();

   #endregion

   #region Value conversion: text, dates, guids, binary, intervals

   [Fact]
   public void TextWithinLengthConverts() => Convert("s5", "abcde").ShouldBe((true, "abcde", null));

   [Fact]
   public void TextOverLengthFails()
   {
      (bool ok, _, string? problem) = Convert("s5", "abcdef");
      ok.ShouldBeFalse();
      problem.ShouldBe("'s5' takes text of at most 5 characters; 'abcdef' has 6");
   }

   [Fact]
   public void EmptyStringConverts() => Convert("s5", "").ShouldBe((true, "", null));

   [Fact]
   public void TextWithQuotesConvertsVerbatim() => Convert("smax", "it's a \"test\"").Value.ShouldBe("it's a \"test\"");

   [Fact]
   public void TextWithBackslashConvertsVerbatim() => Convert("smax", "a\\b").Value.ShouldBe("a\\b");

   /// <summary>A surrogate pair is one character (rune): "AB" with an emoji is 2, so it fits a string(5) but a 4-emoji string of length 8 UTF-16 units still counts as 4.</summary>
   [Fact]
   public void SurrogatePairsCountAsOneCharacterEach()
   {
      Convert("s5", "ab\U0001F600cd").Ok.ShouldBeTrue();          // 5 runes
      Convert("s5", "\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600\U0001F600").Ok.ShouldBeFalse();  // 6 runes
   }

   [Fact]
   public void DateStringConverts() => Convert("d", "2026-03-01").Value.ShouldBe(new DateOnly(2026, 3, 1));

   [Fact]
   public void DateWithTimeOfDayFails()
   {
      (bool ok, _, string? problem) = Convert("d", new DateTime(2026, 3, 1, 10, 0, 0));
      ok.ShouldBeFalse();
      problem!.ShouldContain("has a time of day");
   }

   [Fact]
   public void DateAtMidnightConverts() => Convert("d", new DateTime(2026, 3, 1, 0, 0, 0)).Value.ShouldBe(new DateOnly(2026, 3, 1));

   [Fact]
   public void TimeOfDayConverts() => Convert("tm", new TimeSpan(13, 45, 30)).Value.ShouldBe(new TimeOnly(13, 45, 30));

   [Fact]
   public void TimeStringConverts() => Convert("tm", "13:45:30").Value.ShouldBe(new TimeOnly(13, 45, 30));

   [Fact]
   public void TimeOfADayOrMoreFails()
   {
      Convert("tm", TimeSpan.FromDays(1)).Ok.ShouldBeFalse();
      Convert("tm", TimeSpan.FromHours(-1)).Ok.ShouldBeFalse();
   }

   [Fact]
   public void DateTimeConverts() => Convert("dt", "2026-03-01 13:45:30").Value.ShouldBe(new DateTime(2026, 3, 1, 13, 45, 30));

   [Fact]
   public void DateTimeOffsetStringKeepsItsOffset()
   {
      object? value = Convert("dto", "2026-03-01 10:30:00+02:00").Value;
      value.ShouldBeOfType<DateTimeOffset>().Offset.ShouldBe(TimeSpan.FromHours(2));
   }

   [Fact]
   public void DateTimeOffsetWithoutOffsetAssumedUtc()
   {
      object? value = Convert("dto", "2026-03-01 10:30:00").Value;
      value.ShouldBeOfType<DateTimeOffset>().Offset.ShouldBe(TimeSpan.Zero);
   }

   [Fact]
   public void GuidUpperCaseConverts() => Convert("g", "2F1C0000-0000-4000-8000-000000000001").Value.ShouldBe(Guid1);

   [Fact]
   public void GuidValueConverts() => Convert("g", Guid1).Value.ShouldBe(Guid1);

   [Fact]
   public void GuidGibberishFails() => Convert("g", "not-a-guid").Ok.ShouldBeFalse();

   [Fact]
   public void IntervalConverts() => Convert("iv", TimeSpan.FromMinutes(90)).Value.ShouldBe(TimeSpan.FromMinutes(90));

   [Fact]
   public void IntervalStringConverts() => Convert("iv", "02:03:04").Value.ShouldBe(new TimeSpan(2, 3, 4));

   [Fact]
   public void JsonTextConverts() => Convert("js", "{\"a\": 1}").Value.ShouldBe("{\"a\": 1}");

   [Fact]
   public void NullConvertsToNull() => Convert("i32", null).ShouldBe((true, (object?)null, null));

   [Fact]
   public void BinaryValueConverts() => Convert("bin", new byte[] { 1, 2, 3, 4 }).Value.ShouldBe(new byte[] { 1, 2, 3, 4 });

   #endregion

   #region Planner: issues

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   [InlineData("postgres")]
   [InlineData("sqlserver")]
   public void CantInsertIntoAView(string kind)
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, DialectOf(kind), new InsertRow(ShopTable(catalog, "open_orders"), Row(("id", 1L))));
      plan.Issues.ShouldContain(i => i.Message.Contains("is a view: only tables can be changed"));
   }

   [Fact]
   public void CantUpdateAKeylessTable()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new UpdateRow(ShopTable(catalog, "notes"), Row(("at", DateTime.Now)), Row(("text", "x"))));
      plan.Issues.ShouldContain(i => i.Message.Contains("has no primary key", StringComparison.Ordinal));
   }

   /// <summary>A key with a column of a type the language has no values for can't find its row: such a table's rows are only inserted.</summary>
   [Fact]
   public void CantChangeRowsWhoseKeyHasNoValues()
   {
      QueryCatalog catalog = Build(("k", Source(Table("t", Col("id", "unknown") with { NativeType = "hierarchyid" }, Col("name", "string?")) with { PrimaryKey = Pk("id") })));
      TableEntity table = (TableEntity)catalog.Entity("k.t");
      const string Why = "k.t's key has a column of a type the language has no values for ('id', hierarchyid), so its rows can't be told apart: " +
                         "they can be inserted, but not changed or deleted";
      DmlRules.WhyNoChanges(table).ShouldBe(Why);
      DmlRules.WhyNoInserts(table).ShouldBeNull();
      DmlPlan plan = Plan(catalog, TestDialects.SqlServer, new UpdateRow(table, Row(("id", "/1/")), Row(("name", "x"))), new DeleteRow(table, Row(("id", "/1/"))));
      plan.Issues.Select(i => i.Message).ShouldBe([Why, Why]);
   }

   [Fact]
   public void CanInsertIntoAKeylessTable()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new InsertRow(ShopTable(catalog, "notes"), Row(("text", "hi"))));
      plan.Issues.ShouldBeEmpty();
   }

   [Fact]
   public void UpdateChangingNoColumnIsAnIssue()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new UpdateRow(ShopTable(catalog, "orders"), Row(("id", 1L)), Row()));
      plan.Issues.ShouldContain(i => i.Message == "The update changes no column");
   }

   [Fact]
   public void UpdateOfKeyColumnIsAnIssue()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new UpdateRow(ShopTable(catalog, "orders"), Row(("id", 1L)), Row(("id", 2L))));
      plan.Issues.ShouldContain(i => i.Message.Contains("is part of the key, which can't change"));
   }

   [Fact]
   public void NullKeyValueIsAnIssue()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new UpdateRow(ShopTable(catalog, "order_lines"), Row(("order_id", 1L), ("line_no", null)), Row(("qty", 1))));
      plan.Issues.ShouldContain(i => i.Message.Contains("is null: no row has it"));
   }

   [Fact]
   public void MissingKeyColumnIsAnIssue()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new UpdateRow(ShopTable(catalog, "order_lines"), Row(("order_id", 1L)), Row(("qty", 1))));
      plan.Issues.ShouldContain(i => i.Message.Contains("The key needs a value for 'line_no'"));
   }

   [Fact]
   public void NonKeyColumnInKeyIsAnIssue()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new UpdateRow(ShopTable(catalog, "orders"), Row(("id", 1L), ("status", "open")), Row(("total", 1m))));
      plan.Issues.ShouldContain(i => i.Message.Contains("isn't part of the key"));
   }

   [Fact]
   public void UnknownColumnIsAnIssue()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new InsertRow(ShopTable(catalog, "customers"), Row(("name", "x"), ("nmae", "y"))));
      plan.Issues.ShouldContain(i => i.Message.Contains("has no column 'nmae'"));
   }

   [Fact]
   public void MissingRequiredColumnIsAnIssue()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new InsertRow(ShopTable(catalog, "customers"), Row(("city", "Durban"))));
      plan.Issues.ShouldContain(i => i.Message.Contains("'name' needs a value"));
   }

   [Fact]
   public void IdentityNeedsNoValue()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new InsertRow(ShopTable(catalog, "customers"), Row(("name", "x"))));
      plan.Issues.ShouldBeEmpty();
   }

   [Fact]
   public void SqlServerIdentityTakesNoValue()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.SqlServer, new InsertRow(ShopTable(catalog, "customers"), Row(("id", 9), ("name", "x"))));
      plan.Issues.ShouldContain(i => i.Message.Contains("is an identity column"));
   }

   [Fact]
   public void OtherDatabasesAcceptIdentityValues()
   {
      foreach (string kind in new[] { "sqlite", "duckdb", "postgres" })
      {
         QueryCatalog catalog = ShopCatalog();
         DmlPlan plan = Plan(catalog, DialectOf(kind), new InsertRow(ShopTable(catalog, "customers"), Row(("id", 9), ("name", "x"))));
         plan.Issues.ShouldBeEmpty($"{kind} should accept identity values");
      }
   }

   [Fact]
   public void AmbiguousColumnNameIsAnIssue()
   {
      // customers has 'name'; asking for 'NAME' is a case-insensitive match, not ambiguous, so use two real columns.
      QueryCatalog catalog = Build(("shop", Source(Table("t", Col("Value", "int32?"), Col("value", "int32?")) with { PrimaryKey = Pk("Value") })));
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new InsertRow((TableEntity)catalog.Entity("shop.t"), Row(("vAlUe", 1))));
      plan.Issues.ShouldContain(i => i.Message.Contains("could be"));
   }

   [Fact]
   public void ColumnGivenTwiceByDifferentCaseIsAnIssue()
   {
      // Not possible via a dictionary with the same key; give the exact name and a differently-cased alias that resolves to the same column.
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new InsertRow(ShopTable(catalog, "customers"), Row(("name", "x"), ("NAME", "y"))));
      plan.Issues.ShouldContain(i => i.Message.Contains("is given twice"));
   }

   #endregion

   #region Planner: ordering

   [Fact]
   public void InsertsAreOrderedParentsFirst()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite,
         new InsertRow(ShopTable(catalog, "order_lines"), Row(("order_id", 1L), ("line_no", 1), ("qty", 1))),
         new InsertRow(ShopTable(catalog, "orders"), Row(("id", 1L), ("customer_id", 1), ("status", "open"), ("total", 1m))),
         new InsertRow(ShopTable(catalog, "customers"), Row(("name", "x"))));
      plan.Issues.ShouldBeEmpty();
      plan.Scripts.Single().Statements.Select(s => s.ChangeIndex).ShouldBe([2, 1, 0]);
   }

   [Fact]
   public void DeletesAreOrderedChildrenFirst()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite,
         new DeleteRow(ShopTable(catalog, "customers"), Row(("id", 1))),
         new DeleteRow(ShopTable(catalog, "orders"), Row(("id", 1L))),
         new DeleteRow(ShopTable(catalog, "order_lines"), Row(("order_id", 1L), ("line_no", 1))));
      plan.Issues.ShouldBeEmpty();
      // order_lines (child) first, then orders, then customers (parent).
      plan.Scripts.Single().Statements.Select(s => s.ChangeIndex).ShouldBe([2, 1, 0]);
   }

   [Fact]
   public void InsertsThenUpdatesThenDeletes()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite,
         new DeleteRow(ShopTable(catalog, "order_lines"), Row(("order_id", 1L), ("line_no", 1))),
         new UpdateRow(ShopTable(catalog, "orders"), Row(("id", 1L)), Row(("total", 1m))),
         new InsertRow(ShopTable(catalog, "customers"), Row(("name", "x"))));
      plan.Issues.ShouldBeEmpty();
      plan.Scripts.Single().Statements.Select(s => s.Kind).ShouldBe([DmlStatementKind.Insert, DmlStatementKind.Update, DmlStatementKind.Delete]);
   }

   [Fact]
   public void SelfReferenceKeepsOrderGiven()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite,
         new InsertRow(ShopTable(catalog, "employees"), Row(("id", 1L), ("name", "Ann"))),
         new InsertRow(ShopTable(catalog, "employees"), Row(("id", 2L), ("name", "Ben"), ("manager_id", 1L))));
      plan.Issues.ShouldBeEmpty();
      plan.Scripts.Single().Statements.Select(s => s.ChangeIndex).ShouldBe([0, 1]);
   }

   #endregion

   #region Planner: declared keys (overlay)

   /// <summary>
   /// The overlay doc says a declared key "enables navigation, never editing". A table whose only key is declared by
   /// the overlay should therefore not be updatable or deletable; the planner should raise the "no key" issue.
   /// </summary>
   [Fact]
   public void ADeclaredKeyDoesNotAllowUpdating()
   {
      CatalogOverlay overlay = new() { Entities = [new OverlayEntitySettings("shop.notes") { Key = ["at"] }] };
      QueryCatalog catalog = ShopCatalog(overlay);
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new UpdateRow(ShopTable(catalog, "notes"), Row(("at", new DateTime(2026, 1, 1))), Row(("text", "x"))));
      plan.Success.ShouldBeFalse("a declared key enables navigation, never editing");
   }

   [Fact]
   public void ADeclaredKeyDoesNotAllowDeleting()
   {
      CatalogOverlay overlay = new() { Entities = [new OverlayEntitySettings("shop.notes") { Key = ["at"] }] };
      QueryCatalog catalog = ShopCatalog(overlay);
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new DeleteRow(ShopTable(catalog, "notes"), Row(("at", new DateTime(2026, 1, 1)))));
      plan.Success.ShouldBeFalse("a declared key enables navigation, never editing");
   }

   #endregion

   #region Planner: where clause and original comparison

   [Fact]
   public void SqliteSkipsDecimalAndDateOriginals()
   {
      QueryCatalog catalog = Build(("shop", Source(Table("t", Col("id"), Col("price", "decimal(10,2)?"), Col("when", "date?"), Col("note", "string?")) with { PrimaryKey = Pk("id") })));
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new UpdateRow((TableEntity)catalog.Entity("shop.t"), Row(("id", 1L)), Row(("note", "x")))
      {
         Original = Row(("price", 5m), ("when", new DateOnly(2026, 1, 1))),
      });
      plan.Issues.ShouldBeEmpty();
      string where = plan.Scripts.Single().Statements.Single().ToDisplayText();
      where.ShouldNotContain("price =");
      where.ShouldNotContain("when =");
   }

   [Fact]
   public void DuckDbComparesDecimalOriginals()
   {
      QueryCatalog catalog = Build(("shop", Source(Table("t", Col("id"), Col("price", "decimal(10,2)?"), Col("note", "string?")) with { PrimaryKey = Pk("id") })));
      DmlPlan plan = Plan(catalog, TestDialects.DuckDb, new UpdateRow((TableEntity)catalog.Entity("shop.t"), Row(("id", 1L)), Row(("note", "x"))) { Original = Row(("price", 5m)) });
      plan.Scripts.Single().Statements.Single().ToDisplayText().ShouldContain("price =");
   }

   [Fact]
   public void NullOriginalBecomesIsNull()
   {
      QueryCatalog catalog = ShopCatalog();
      DmlPlan plan = Plan(catalog, TestDialects.Sqlite, new UpdateRow(ShopTable(catalog, "customers"), Row(("id", 1)), Row(("name", "x"))) { Original = Row(("city", null)) });
      plan.Scripts.Single().Statements.Single().ToDisplayText().ShouldContain("city IS NULL");
   }

   [Fact]
   public void FloatOriginalsAreNeverCompared()
   {
      QueryCatalog catalog = Build(("shop", Source(Table("t", Col("id"), Col("ratio", "double?"), Col("note", "string?")) with { PrimaryKey = Pk("id") })));
      DmlPlan plan = Plan(catalog, TestDialects.DuckDb, new UpdateRow((TableEntity)catalog.Entity("shop.t"), Row(("id", 1L)), Row(("note", "x"))) { Original = Row(("ratio", 1.5)) });
      plan.Scripts.Single().Statements.Single().ToDisplayText().ShouldNotContain("ratio =");
   }

   #endregion

   #region Splitter: well-formed scripts

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   [InlineData("postgres")]
   [InlineData("sqlserver")]
   public void SemicolonInsideStringDoesntSplit(string kind) =>
      Split(kind, "UPDATE t SET a = 'x;y'; DELETE FROM t").ShouldBe(["UPDATE t SET a = 'x;y'", "DELETE FROM t"]);

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   [InlineData("postgres")]
   [InlineData("sqlserver")]
   public void EmptyStatementsAreDropped(string kind) =>
      Split(kind, ";;; UPDATE t SET a = 1 ;;").ShouldBe(["UPDATE t SET a = 1"]);

   [Fact]
   public void TrailingContentWithoutSemicolonIsAStatement() =>
      Split("postgres", "UPDATE a SET x = 1;\nUPDATE b SET y = 2").ShouldBe(["UPDATE a SET x = 1", "UPDATE b SET y = 2"]);

   [Fact]
   public void GoInsideStringIsNotABatchEnd() =>
      Split("sqlserver", "UPDATE t SET note = 'line\nGO\nmore' WHERE id = 1").Length.ShouldBe(1);

   [Fact]
   public void GoLowercaseAloneEndsABatch() =>
      Split("sqlserver", "UPDATE a SET x = 1\ngo\nDELETE FROM b").ShouldBe(["UPDATE a SET x = 1", "DELETE FROM b"]);

   [Fact]
   public void TrailingGoWithNoNewlineEndsABatch() =>
      Split("sqlserver", "UPDATE a SET x = 1\nGO").ShouldBe(["UPDATE a SET x = 1"]);

   [Fact]
   public void DollarQuotesProtectSemicolons() =>
      Split("postgres", "UPDATE t SET a = $tag$x;y$tag$; DELETE FROM t").ShouldBe(["UPDATE t SET a = $tag$x;y$tag$", "DELETE FROM t"]);

   [Fact]
   public void EStringBackslashQuoteProtectsSemicolon() =>
      Split("postgres", @"UPDATE t SET a = E'x\';y'; DELETE FROM t").ShouldBe([@"UPDATE t SET a = E'x\';y'", "DELETE FROM t"]);

   [Fact]
   public void SqlServerDollarIsNotADollarQuote() =>
      Split("sqlserver", "UPDATE t SET a = $x$y;z").ShouldBe(["UPDATE t SET a = $x$y", "z"]);

   [Fact]
   public void BracketNamesProtectSemicolons()
   {
      Split("sqlserver", "UPDATE [a;b] SET x = 1").Length.ShouldBe(1);
      Split("sqlite", "UPDATE [a;b] SET x = 1").Length.ShouldBe(1);
   }

   [Fact]
   public void BacktickNamesProtectSemicolonsInSqlite() =>
      Split("sqlite", "UPDATE `a;b` SET x = 1").Length.ShouldBe(1);

   [Fact]
   public void DoubleQuoteNamesProtectSemicolons() =>
      Split("postgres", "UPDATE \"a;b\" SET x = 1").Length.ShouldBe(1);

   #endregion

   #region Splitter: problems and positions

   [Fact]
   public void UnclosedStringIsReportedAtItsPosition()
   {
      SplitScript script = SqlScriptSplitter.Split("UPDATE t SET a = 1;\nUPDATE t SET b = 'x;\n", TestDialects.Sqlite);
      script.Problems.Single().ShouldBe(new ScriptProblem("This string isn't closed", 37, 4, 2));
   }

   [Fact]
   public void UnclosedBlockCommentIsReported() =>
      SqlScriptSplitter.Split("UPDATE t SET a = 1 /* x", TestDialects.SqlServer).Problems.Single().Message.ShouldBe("This comment isn't closed");

   [Fact]
   public void UnclosedDollarQuoteIsReported() =>
      SqlScriptSplitter.Split("UPDATE t SET a = $q$ x", TestDialects.PostgreSql).Problems.Single().Message.ShouldBe("This $q$ quote isn't closed");

   [Fact]
   public void GoWithACountIsAProblem()
   {
      SplitScript twice = SqlScriptSplitter.Split("UPDATE a SET x = x + 1\nGO 2\n", TestDialects.SqlServer);
      twice.Problems.Single().Message.ShouldBe("GO with a count would run its batch more than once");
   }

   [Fact]
   public void KeywordAfterCteIsFound()
   {
      SplitScript script = SqlScriptSplitter.Split("WITH x AS (SELECT 1) DELETE FROM t WHERE id IN (SELECT a FROM x)", TestDialects.PostgreSql);
      script.Statements.Single().Keyword.ShouldBe("DELETE");
   }

   [Fact]
   public void KeywordAfterCteWithColumnListIsFound()
   {
      SplitScript script = SqlScriptSplitter.Split("WITH x (a, b) AS (SELECT 1, 2) INSERT INTO t SELECT * FROM x", TestDialects.PostgreSql);
      script.Statements.Single().Keyword.ShouldBe("INSERT");
   }

   [Fact]
   public void LineNumbersAreTracked()
   {
      SplitScript script = SqlScriptSplitter.Split("UPDATE a SET x = 1;\n\nDELETE FROM b;\nUPDATE c SET y = 2", TestDialects.Sqlite);
      script.Statements.Select(s => s.Line).ShouldBe([1, 3, 4]);
   }

   #endregion

   #region Guard: allows real data changes

   [Theory]
   [InlineData("sqlite", "INSERT INTO t (a) VALUES (1)")]
   [InlineData("sqlite", "REPLACE INTO t (a) VALUES (1)")]
   [InlineData("sqlite", "UPDATE t SET a = 1 WHERE id = 2")]
   [InlineData("sqlite", "DELETE FROM t WHERE id = 2")]
   [InlineData("sqlite", "WITH x AS (SELECT 1 AS a) UPDATE t SET a = (SELECT a FROM x)")]
   [InlineData("postgres", "INSERT INTO t (a) VALUES (1) ON CONFLICT (a) DO UPDATE SET b = 2 RETURNING *")]
   [InlineData("postgres", "WITH gone AS (DELETE FROM t RETURNING *) INSERT INTO log SELECT * FROM gone")]
   [InlineData("duckdb", "INSERT OR REPLACE INTO t SELECT * FROM u")]
   [InlineData("sqlserver", "MERGE INTO t USING (SELECT 1 AS a) AS s ON t.a = s.a WHEN NOT MATCHED THEN INSERT (a) VALUES (s.a);")]
   [InlineData("sqlserver", "UPDATE t SET a = 1 OUTPUT inserted.a INTO log (a) WHERE b = 2")]
   public void GuardAllowsDataChanges(string kind, string script) => Guard(kind, script).ShouldBeEmpty();

   #endregion

   #region Guard: rejects everything else (security)

   [Theory]
   [InlineData("sqlite", "DROP TABLE t")]
   [InlineData("sqlite", "PRAGMA foreign_keys = OFF")]
   [InlineData("sqlite", "ATTACH 'other.db' AS o")]
   [InlineData("sqlite", "SELECT * FROM t")]
   [InlineData("sqlite", "VACUUM")]
   [InlineData("sqlite", "ALTER TABLE t ADD COLUMN c int")]
   [InlineData("postgres", "TRUNCATE t")]
   [InlineData("postgres", "COPY t FROM '/etc/passwd'")]
   [InlineData("postgres", "CREATE TABLE t (a int)")]
   [InlineData("postgres", "DO $$ BEGIN PERFORM 1; END $$")]
   [InlineData("postgres", "SET search_path = x")]
   [InlineData("postgres", "GRANT ALL ON t TO public")]
   [InlineData("duckdb", "COPY t TO 'out.csv'")]
   [InlineData("duckdb", "INSTALL httpfs")]
   [InlineData("duckdb", "LOAD httpfs")]
   [InlineData("duckdb", "ATTACH 'x.db'")]
   [InlineData("duckdb", "EXPORT DATABASE 'dir'")]
   [InlineData("duckdb", "CALL pragma_version()")]
   [InlineData("sqlserver", "SELECT * FROM t")]
   [InlineData("sqlserver", "EXEC sp_who")]
   [InlineData("sqlserver", "CREATE TABLE t (a int)")]
   [InlineData("sqlserver", "DBCC CHECKDB")]
   public void GuardRejectsNonDataChanges(string kind, string script) => Guard(kind, script).ShouldNotBeEmpty($"{kind}: {script}");

   [Theory]
   [InlineData("sqlserver", "UPDATE t SET a = 1 DROP TABLE t")]
   [InlineData("sqlserver", "UPDATE t SET a = 1 SELECT * INTO u FROM t")]
   [InlineData("sqlserver", "INSERT INTO t EXEC ('DROP TABLE t')")]
   [InlineData("sqlserver", "DELETE FROM t WAITFOR DELAY '00:10'")]
   [InlineData("sqlserver", "UPDATE t SET a = 1 EXEC sp_who")]
   [InlineData("sqlserver", "UPDATE t SET a = 1; SHUTDOWN")]
   [InlineData("sqlserver", "UPDATE t SET a = 1 BACKUP DATABASE x TO DISK = 'y'")]
   [InlineData("sqlserver", "UPDATE t SET a = 1 GRANT CONTROL TO sa")]
   public void GuardRejectsSmuggledTSqlStatements(string kind, string script) => Guard(kind, script).ShouldNotBeEmpty($"{kind}: {script}");

   [Theory]
   [InlineData("postgres", "UPDATE t SET a = pg_read_file('/etc/passwd')")]
   [InlineData("postgres", "INSERT INTO t SELECT * FROM dblink('x', 'y')")]
   [InlineData("postgres", "UPDATE t SET a = lo_import('/etc/passwd')")]
   [InlineData("postgres", "UPDATE t SET a = pg_ls_dir('/')")]
   [InlineData("duckdb", "INSERT INTO t SELECT * FROM read_csv('secret.csv')")]
   [InlineData("duckdb", "INSERT INTO t SELECT * FROM read_parquet('x.parquet')")]
   [InlineData("duckdb", "INSERT INTO t SELECT * FROM glob('*.csv')")]
   [InlineData("sqlite", "UPDATE t SET a = load_extension('x')")]
   public void GuardRejectsFunctionsThatReachOutside(string kind, string script) => Guard(kind, script).ShouldNotBeEmpty($"{kind}: {script}");

   [Theory]
   [InlineData("sqlite", "BEGIN")]
   [InlineData("sqlite", "COMMIT")]
   [InlineData("sqlite", "ROLLBACK")]
   [InlineData("sqlite", "SAVEPOINT s")]
   [InlineData("postgres", "START TRANSACTION")]
   [InlineData("sqlserver", "BEGIN TRANSACTION")]
   [InlineData("sqlserver", "COMMIT")]
   public void GuardAlwaysRejectsTransactionControl(string kind, string script)
   {
      Guard(kind, script).ShouldNotBeEmpty();
      Guard(kind, script, any: true).ShouldNotBeEmpty("transaction control is rejected even for an administrator");
   }

   [Fact]
   public void AdministratorMayRunOtherStatementsButNotTransactions()
   {
      Guard("sqlite", "CREATE TABLE x (a int)", any: true).ShouldBeEmpty();
      Guard("sqlite", "DROP TABLE x", any: true).ShouldBeEmpty();
      Guard("sqlite", "ROLLBACK", any: true).ShouldNotBeEmpty();
   }

   /// <summary>A comment before a forbidden statement doesn't hide it from the guard.</summary>
   [Fact]
   public void CommentsDontHideForbiddenStatements()
   {
      Guard("sqlserver", "/* harmless */ DROP TABLE t").ShouldNotBeEmpty();
      Guard("postgres", "-- ok\nTRUNCATE t").ShouldNotBeEmpty();
   }

   /// <summary>A problem's line points at the offending statement.</summary>
   [Fact]
   public void RejectionLinesAreRight()
   {
      IReadOnlyList<ScriptProblem> problems = Guard("sqlite", "UPDATE t SET a = 1;\nDROP TABLE t");
      problems.Single().Line.ShouldBe(2);
   }

   #endregion
}
