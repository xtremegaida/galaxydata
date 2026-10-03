using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Language;
using GalaxyData.Query.Results;
using GalaxyData.Query.Sqlite;
using GalaxyData.Query.Types;
using GalaxyData.Web.Browse;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Browse;

/// <summary>Grids' filters, where and sort as query text, and navigations from a row as queries of their targets: written, not run.</summary>
public sealed class GridQueryTests
{
   private sealed class NoConnections : IConnectionFactory
   {
      public ValueTask<DbConnection> OpenAsync(SourceInfo source, CancellationToken cancellationToken) => throw new InvalidOperationException("Nothing runs here");
   }

   private static ColumnSchema Column(string name, int ordinal, string type) => new(name, ordinal, "TEXT", ScalarType.Parse(type));

   private static readonly QueryCatalog Catalog = new CatalogBuilder().AddSource(new SourceInfo("shop", "sqlite", "main"), new SourceSchema("sqlite", "3", "main",
   [
      new TableSchema("main", "customers", TableKind.Table,
         [Column("id", 0, "int64"), Column("name", 1, "string"), Column("city", 2, "string?"), Column("credit", 3, "decimal(10,2)?"), Column("created_at", 4, "datetime?"),
          Column("Total Spend", 5, "double?"), Column("vip", 6, "boolean?"), Column("photo", 7, "binary?")])
      {
         PrimaryKey = new KeySchema(null, ["id"]),
      },
      new TableSchema("main", "orders", TableKind.Table, [Column("id", 0, "int64"), Column("customer_id", 1, "int64"), Column("placed_at", 2, "datetimeoffset?")])
      {
         PrimaryKey = new KeySchema(null, ["id"]),
         ForeignKeys = [new ForeignKeySchema(null, ["customer_id"], "main", "customers", ["id"])],
      },
      new TableSchema("main", "lines", TableKind.Table, [Column("order_id", 0, "int64"), Column("line_no", 1, "int32"), Column("qty", 2, "int32")])
      {
         PrimaryKey = new KeySchema(null, ["order_id", "line_no"]),
         ForeignKeys = [new ForeignKeySchema(null, ["order_id"], "main", "orders", ["id"])],
      },
      new TableSchema("main", "notes", TableKind.Table, [Column("text", 0, "string?"), Column("line_order", 1, "int64?"), Column("line_no", 2, "int32?")])
      {
         ForeignKeys = [new ForeignKeySchema(null, ["line_order", "line_no"], "main", "lines", ["order_id", "line_no"])],
      },
      new TableSchema("main", "codes", TableKind.Table, [Column("Code", 0, "string"), Column("code", 1, "string"), Column("CoDe2", 2, "string")]),
   ])).Build();

   private static readonly QueryEngine Engine = new(Catalog, new NoConnections(), [SqliteSourceProvider.Instance]);

   private static IReadOnlyList<ResultColumn> Columns(string text) => Engine.Prepare(text).Schema!.VisibleColumns;

   private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

   private static GridFilterDto Filter(string column, GridOp op, string? value = null, string? to = null) =>
      new(column, [new GridConditionDto(op, value == null ? null : Json(value), to == null ? null : Json(to))]);

   /// <summary>The query a grid of customers runs, and its parameters (name = value type), checked to plan.</summary>
   private static (string Text, string[] Parameters) Composed(GridStateDto grid, string text = "shop.customers")
   {
      Dictionary<string, string[]> errors = [];
      ComposedQuery? composed = GridQueryComposer.Compose(text, new QueryParameters(), Columns(text), grid, errors);
      errors.ShouldBeEmpty();
      PreparedQuery prepared = Engine.Prepare(new QueryRequest(composed!.Text) { Parameters = composed.Parameters });
      prepared.Diagnostics.Where(d => d.IsError).ShouldBeEmpty(composed.Text);
      return (composed.Text, composed.Parameters.All.Select(p => $"{p.Name} = {ValueCodec.Encode(p.Value, p.Type)} {p.Type}").ToArray());
   }

   private static Dictionary<string, string[]> Errors(GridStateDto grid)
   {
      Dictionary<string, string[]> errors = [];
      GridQueryComposer.Compose("shop.customers", new QueryParameters(), Columns("shop.customers"), grid, errors).ShouldBeNull();
      return errors;
   }

   [Fact]
   public void AGridWithNothingIsTheQuery() => Composed(new GridStateDto()).ShouldBe(("shop.customers", []));

   [Theory]
   [InlineData("name", GridOp.Eq, "\"Acme\"", null, "name == $f1", "f1 = Acme string")]
   [InlineData("name", GridOp.Ne, "\"Acme\"", null, "(name != $f1 or name == null)", "f1 = Acme string")]
   [InlineData("credit", GridOp.Lt, "\"10.5\"", null, "credit < $f1", "f1 = 10.5 decimal(10,2)")]
   [InlineData("credit", GridOp.Le, "10.5", null, "credit <= $f1", "f1 = 10.5 decimal(10,2)")]
   [InlineData("id", GridOp.Gt, "\"7\"", null, "id > $f1", "f1 = 7 int64")]
   [InlineData("id", GridOp.Ge, "7", null, "id >= $f1", "f1 = 7 int64")]
   [InlineData("id", GridOp.Between, "1", "\"9\"", "(id >= $f1 and id <= $f2)", "f1 = 1 int64", "f2 = 9 int64")]
   [InlineData("name", GridOp.Contains, "\"ac\"", null, "icontains(name, $f1)", "f1 = ac string")]
   [InlineData("name", GridOp.NotContains, "\"ac\"", null, "(not icontains(name, $f1) or name == null)", "f1 = ac string")]
   [InlineData("name", GridOp.StartsWith, "\"5%_\\\\\"", null, "ilike(name, $f1)", "f1 = 5\\%\\_\\\\% string")]
   [InlineData("name", GridOp.EndsWith, "\"ltd\"", null, "ilike(name, $f1)", "f1 = %ltd string")]
   [InlineData("city", GridOp.Blank, null, null, "(city == null or city == '')")]
   [InlineData("city", GridOp.NotBlank, null, null, "(city != null and city != '')")]
   [InlineData("credit", GridOp.Blank, null, null, "credit == null")]
   [InlineData("vip", GridOp.Eq, "true", null, "vip == $f1", "f1 = True boolean")]
   [InlineData("Total Spend", GridOp.Gt, "100", null, "it['Total Spend'] > $f1", "f1 = 100 double")]
   [InlineData("created_at", GridOp.Eq, "\"2026-03-01T10:30:00\"", null, "created_at == $f1", "f1 = 2026-03-01T10:30:00 datetime")]
   public void FiltersAreConditionsOverTheRows(string column, GridOp op, string? value, string? to, string condition, params string[] parameters)
   {
      (string text, string[] actual) = Composed(new GridStateDto { Filters = [Filter(column, op, value, to)] });
      text.ShouldBe($"(shop.customers).where({condition})");
      actual.ShouldBe(parameters);
   }

   /// <summary>A date alone compares with the days of date-times: on a day is from its start to the next day's.</summary>
   [Theory]
   [InlineData(GridOp.Eq, "(created_at >= $f1 and created_at < $f2)", "f1 = 2026-03-01T00:00:00 datetime", "f2 = 2026-03-02T00:00:00 datetime")]
   [InlineData(GridOp.Ne, "(created_at < $f1 or created_at >= $f2 or created_at == null)", "f1 = 2026-03-01T00:00:00 datetime", "f2 = 2026-03-02T00:00:00 datetime")]
   [InlineData(GridOp.Lt, "created_at < $f1", "f1 = 2026-03-01T00:00:00 datetime")]
   [InlineData(GridOp.Le, "created_at < $f1", "f1 = 2026-03-02T00:00:00 datetime")]
   [InlineData(GridOp.Gt, "created_at >= $f1", "f1 = 2026-03-02T00:00:00 datetime")]
   [InlineData(GridOp.Ge, "created_at >= $f1", "f1 = 2026-03-01T00:00:00 datetime")]
   [InlineData(GridOp.Between, "(created_at >= $f1 and created_at < $f2)", "f1 = 2026-03-01T00:00:00 datetime", "f2 = 2026-03-05T00:00:00 datetime")]
   public void DatesAreDaysOfDateTimes(GridOp op, string condition, params string[] parameters)
   {
      (string text, string[] actual) = Composed(new GridStateDto { Filters = [Filter("created_at", op, "\"2026-03-01\"", "\"2026-03-04\"")] });
      text.ShouldBe($"(shop.customers).where({condition})");
      actual.ShouldBe(parameters);
   }

   [Fact]
   public void DaysOfDateTimesWithOffsetsAreDaysInUtc()
   {
      (_, string[] parameters) = Composed(new GridStateDto { Filters = [Filter("placed_at", GridOp.Eq, "\"2026-03-01\"")] }, "shop.orders");
      parameters.ShouldBe(["f1 = 2026-03-01T00:00:00+00:00 datetimeoffset", "f2 = 2026-03-02T00:00:00+00:00 datetimeoffset"]);
   }

   /// <summary>The last day has no day after it: on it is from its start, and nothing is after it.</summary>
   [Theory]
   [InlineData(GridOp.Eq, "created_at >= $f1")]
   [InlineData(GridOp.Ne, "(created_at < $f1 or created_at == null)")]
   [InlineData(GridOp.Le, "created_at != null")]
   [InlineData(GridOp.Gt, "false")]
   [InlineData(GridOp.Between, "created_at >= $f1")]
   public void TheLastDayIsADayToo(GridOp op, string condition)
   {
      (string text, _) = Composed(new GridStateDto { Filters = [Filter("created_at", op, "\"9999-12-31\"", "\"9999-12-31\"")] });
      text.ShouldBe($"(shop.customers).where({condition})");
   }

   [Fact]
   public void FiltersMeetEveryConditionOrAny()
   {
      GridStateDto grid = new()
      {
         Filters =
         [
            new GridFilterDto("id", [new GridConditionDto(GridOp.Lt, Json("3")), new GridConditionDto(GridOp.Gt, Json("8"))], Any: true),
            new GridFilterDto("name", [new GridConditionDto(GridOp.StartsWith, Json("\"a\"")), new GridConditionDto(GridOp.EndsWith, Json("\"z\""))]),
         ],
         Sort = [new GridSortDto("name"), new GridSortDto("Total Spend", Desc: true), new GridSortDto("NAME")],
      };
      Composed(grid).Text.ShouldBe("(shop.customers).where((id < $f1 or id > $f2)).where((ilike(name, $f3) and ilike(name, $f4))).orderBy(name, desc(it['Total Spend']))");
   }

   [Fact]
   public void TheWhereExpressionIsAddedAsWritten()
   {
      GridStateDto grid = new() { Where = "  credit > 100 // the big ones  ", Filters = [Filter("city", GridOp.Eq, "\"Cape Town\"")], Sort = [new GridSortDto("id", true)] };
      Dictionary<string, string[]> errors = [];
      ComposedQuery composed = GridQueryComposer.Compose("shop.customers", new QueryParameters(), Columns("shop.customers"), grid, errors)!;
      composed.Text.ShouldBe("(shop.customers).where(city == $f1).where((  credit > 100 // the big ones\n)).orderBy(desc(id))");
      composed.Text.Substring(composed.WhereStart!.Value, "  credit > 100".Length).ShouldBe("  credit > 100", "the where as the client wrote it");
      Engine.Prepare(new QueryRequest(composed.Text) { Parameters = composed.Parameters }).Success.ShouldBeTrue();
   }

   [Fact]
   public void ParametersAreNamedAsTheWhereExpressionDoesnt()
   {
      GridStateDto grid = new() { Where = "id != $f1", Filters = [Filter("id", GridOp.Gt, "1")] };
      Dictionary<string, string[]> errors = [];
      ComposedQuery composed = GridQueryComposer.Compose("shop.customers", new QueryParameters(), Columns("shop.customers"), grid, errors)!;
      composed.Text.ShouldStartWith("(shop.customers).where(id > $f2)");
   }

   [Theory]
   [InlineData("// just a comment", "Write a condition over the rows, such as total > 100")]
   [InlineData("x := 1; x > 0", "Write one condition, without ';'")]
   [InlineData("let x = 1", "Write a condition over the rows, such as total > 100, not a statement")]
   [InlineData("c => c.id > 1", "Write a condition over the rows, such as total > 100, not a statement")]
   [InlineData("id = 1", "Write a condition over the rows, such as total > 100, not a statement")]
   [InlineData("true).select(name", "Expected ';' or the end of the expression but found ')' (at 5)")]
   public void TheWhereExpressionIsOneCondition(string where, string message)
   {
      Dictionary<string, string[]> errors = Errors(new GridStateDto { Where = where });
      errors["grid.where"][0].ShouldBe(message);
   }

   [Fact]
   public void TheWhereExpressionsPlacesAreTheClients()
   {
      Dictionary<string, string[]> errors = [];
      ComposedQuery composed = GridQueryComposer.Compose("shop.customers", new QueryParameters(), Columns("shop.customers"), new GridStateDto { Where = "   id > 1  " }, errors)!;
      composed.Text.ShouldBe("(shop.customers).where((   id > 1\n))");
      composed.Text.Substring(composed.WhereStart!.Value + 3, 6).ShouldBe("id > 1", "the where's start, spaces and all");
      Errors(new GridStateDto { Where = "  )" })["grid.where"][0].ShouldEndWith("(at 3)");
   }

   [Fact]
   public void ColumnsNamedButForCaseAreNamedExactly()
   {
      Dictionary<string, string[]> errors = [];
      GridQueryComposer.Compose("shop.codes", new QueryParameters(), Columns("shop.codes"), new GridStateDto { Sort = [new GridSortDto("CODE")] }, errors).ShouldBeNull();
      errors["grid.sort[0].column"][0].ShouldBe("'CODE' names more than one column but for case: Code, code");
      Composed(new GridStateDto { Sort = [new GridSortDto("code"), new GridSortDto("code2")] }, "shop.codes").Text.ShouldBe("(shop.codes).orderBy(code, CoDe2)");
   }

   [Fact]
   public void NullsInListsAreSaidToBe()
   {
      Dictionary<string, string[]> errors = Errors(new GridStateDto
      {
         Filters = [null!, new GridFilterDto("id", [null!])],
         Sort = [null!],
      });
      errors.Keys.ShouldBe(["grid.filters[0]", "grid.filters[1].conditions[0]", "grid.sort[0]"], ignoreOrder: true);
   }

   [Fact]
   public void WhatIsWrongIsSaidByField()
   {
      Dictionary<string, string[]> errors = Errors(new GridStateDto
      {
         Filters =
         [
            Filter("nothing", GridOp.Eq, "1"),
            Filter("id", GridOp.Contains, "\"1\""),
            Filter("id", GridOp.Eq, "\"one\""),
            Filter("id", GridOp.Gt),
            Filter("id", GridOp.Between, "1"),
            Filter("photo", GridOp.Eq, "\"AA==\""),
         ],
         Sort = [new GridSortDto("other")],
      });
      errors.ToDictionary(e => e.Key, e => e.Value.Single()).ShouldBe(new Dictionary<string, string>
      {
         ["grid.filters[0].column"] = "The rows have no column 'nothing'",
         ["grid.filters[1].conditions[0].op"] = "'id' is int64, which can't be filtered by contains",
         ["grid.filters[2].conditions[0].value"] = "\"one\" isn't a whole number",
         ["grid.filters[3].conditions[0].value"] = "gt needs a value",
         ["grid.filters[4].conditions[0].valueTo"] = "between needs a value up to",
         ["grid.filters[5].conditions[0].op"] = "'photo' is binary, which can't be filtered by eq",
         ["grid.sort[0].column"] = "The rows have no column 'other'",
      }, ignoreOrder: true);
   }

   private static (string Text, string[] Parameters) Navigated(string from, object[] key, string navigation)
   {
      EntityDef entity = Catalog.FindEntity(from)!;
      IReadOnlyList<object?> values = NavigationResolver.Key(entity, key.Select(k => (object?)JsonSerializer.SerializeToElement(k)).ToList(), out string? problem)!;
      problem.ShouldBeNull();
      EntityQuery query = NavigationResolver.Resolve(entity, values, NavigationResolver.Find(entity, navigation, out problem)!);
      problem.ShouldBeNull();
      PreparedQuery prepared = Engine.Prepare(new QueryRequest(query.Text) { Parameters = query.Parameters });
      prepared.Diagnostics.Where(d => d.IsError).ShouldBeEmpty(query.Text);
      prepared.Schema!.Entity.ShouldBe(query.Entity, "the target's own rows");
      return (query.Text, query.Parameters.All.Select(p => $"{p.Name} = {ValueCodec.Encode(p.Value, p.Type)} {p.Type}").ToArray());
   }

   [Fact]
   public void ANavigationFromARowIsAQueryOfItsTarget()
   {
      static void Leads((string Text, string[] Parameters) actual, string text, params string[] parameters)
      {
         actual.Text.ShouldBe(text);
         actual.Parameters.ShouldBe(parameters);
      }
      Leads(Navigated("shop.customers", ["42"], "orders"), "(shop.orders).where(customer_id == $key1)", "key1 = 42 int64");
      // The key has no value of the foreign key: the target's rows are those the row matches.
      Leads(Navigated("shop.orders", [1001], "customer"), "(shop.customers).where(t => shop.orders.any(o => o.id == $key1 and o.customer_id == t.id))", "key1 = 1001 int64");
      Leads(Navigated("shop.lines", ["1001", 2], "notes"), "(shop.notes).where(line_order == $key1 and line_no == $key2)", "key1 = 1001 int64", "key2 = 2 int32");
      // The key has the foreign key's value.
      Leads(Navigated("shop.lines", ["1001", 2], "order"), "(shop.orders).where(id == $key1)", "key1 = 1001 int64");
   }

   /// <summary>A source named as the target's row is in the query: the row is named apart from it.</summary>
   [Fact]
   public void TheTargetsRowIsNamedApartFromTheSource()
   {
      QueryCatalog catalog = new CatalogBuilder().AddSource(new SourceInfo("t", "sqlite", "main"), new SourceSchema("sqlite", "3", "main",
      [
         new TableSchema("main", "emp", TableKind.Table, [Column("id", 0, "int64"), Column("boss_id", 1, "int64?")])
         {
            PrimaryKey = new KeySchema(null, ["id"]),
            ForeignKeys = [new ForeignKeySchema(null, ["boss_id"], "main", "emp", ["id"])],
         },
      ])).Build();
      EntityDef emp = catalog.FindEntity("t.emp")!;
      EntityQuery query = NavigationResolver.Resolve(emp, [2L], NavigationResolver.Find(emp, "boss", out _)!);
      query.Text.ShouldBe("(t.emp).where(t_ => t.emp.any(o => o.id == $key1 and o.boss_id == t_.id))");
      new QueryEngine(catalog, new NoConnections(), [SqliteSourceProvider.Instance]).Prepare(new QueryRequest(query.Text) { Parameters = query.Parameters })
         .Diagnostics.Where(d => d.IsError).ShouldBeEmpty();
   }

   [Fact]
   public void KeysAndNavigationsThatArentAreSaidToBe()
   {
      EntityDef lines = Catalog.FindEntity("shop.lines")!;
      NavigationResolver.Key(lines, [Json("1")], out string? problem).ShouldBeNull();
      problem.ShouldBe("The key of shop.lines is 2 values (order_id, line_no)");
      NavigationResolver.Key(lines, [Json("1"), Json("\"x\"")], out problem).ShouldBeNull();
      problem.ShouldBe("'line_no': \"x\" isn't a whole number");
      NavigationResolver.Key(lines, [Json("1"), Json("null")], out problem).ShouldBeNull();
      problem.ShouldBe("'line_no': null isn't a value of 'line_no', a key");
      NavigationResolver.Key(Catalog.FindEntity("shop.notes")!, [Json("1")], out problem).ShouldBeNull();
      problem.ShouldBe("shop.notes has no key, so its rows can't be told apart");
      NavigationResolver.Find(lines, "nothing", out problem).ShouldBeNull();
      problem.ShouldBe("shop.lines has no navigation 'nothing'");
      NavigationResolver.Find(lines, "ORDER", out problem)!.Name.ShouldBe("order", "names ignore case where they are unique");
   }
}
