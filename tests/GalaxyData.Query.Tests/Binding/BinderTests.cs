using System;
using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Types;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Binding;

public sealed class BinderTests
{
   private static readonly QueryCatalog Catalog = TestCatalogs.Sales();

   private static BoundProgram Bind(string query, QueryParameters? parameters = null)
   {
      BoundProgram program = Binder.Bind(query, Catalog, parameters);
      program.Diagnostics.Where(d => d.IsError).ShouldBeEmpty(query);
      return program;
   }

   private static BoundQuery Query(string query) => Bind(query).Query.ShouldNotBeNull();

   private static T Predicate<T>(string query) where T : BoundExpr =>
      Query(query).ShouldBeOfType<BoundWhere>().Predicate.ShouldBeOfType<T>();

   [Fact]
   public void FirstExampleProducesTheExpectedShape()
   {
      BoundQuery query = Query("sales.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city)");
      query.Shape.ToString().ShouldBe("[id int64, total decimal(10,2), who string(100), city string?]");
      query.Shape.Entity.ShouldBeNull();
   }

   [Fact]
   public void FilteringKeepsTheEntityAndItsNavigations()
   {
      BoundQuery query = Query("sales.orders.where(total > 1).orderBy(id).take(5)");
      query.Shape.Entity.ShouldBeSameAs(Catalog.FindEntity("sales.orders"));
      query.Shape.Find("customer").Item.ShouldBeOfType<NavigationMember>();
   }

   [Fact]
   public void StringLiteralsTakeTheColumnsTextType()
   {
      BoundBinary equal = Predicate<BoundBinary>("sales.orders.where(status == 'open')");
      equal.Right.ShouldBeOfType<BoundLiteral>().Scalar.ToString().ShouldBe("string(20,ansi)");
   }

   [Fact]
   public void DateTextBecomesADate()
   {
      BoundBinary compare = Predicate<BoundBinary>("sales.orders.where(order_date >= '2026-01-05')");
      compare.Right.ShouldBeOfType<BoundLiteral>().Value.ShouldBe(new DateOnly(2026, 1, 5));
   }

   [Fact]
   public void IntegerLiteralsNarrowToTheColumn()
   {
      BoundBinary compare = Predicate<BoundBinary>("sales.orders.where(customer_id == 7)");
      BoundLiteral literal = compare.Right.ShouldBeOfType<BoundLiteral>();
      literal.Value.ShouldBe(7);
      literal.Scalar.Kind.ShouldBe(ScalarKind.Int32);
   }

   [Fact]
   public void ConstantsOnTheLeftAdoptToo()
   {
      BoundBinary compare = Predicate<BoundBinary>("sales.orders.where('2026-01-05' <= order_date)");
      compare.Left.ShouldBeOfType<BoundLiteral>().Value.ShouldBe(new DateOnly(2026, 1, 5));
   }

   [Fact]
   public void TextParametersTakeTheColumnType()
   {
      BoundProgram program = Bind("sales.orders.where(order_date >= $since)", new QueryParameters().Add("since", "2026-01-01"));
      BoundBinary compare = ((BoundWhere)program.Query!).Predicate.ShouldBeOfType<BoundBinary>();
      compare.Right.ShouldBeOfType<BoundParameter>().Scalar.Kind.ShouldBe(ScalarKind.Date);
   }

   [Fact]
   public void WholeNumberParametersTakeTheWidthTheyMeet()
   {
      BoundProgram program = Bind("sales.customers.where(id == $id).select(d: addDays(created, $n))", new QueryParameters().Add("n", 3L).Add("id", 7L));
      BoundSelect select = program.Query.ShouldBeOfType<BoundSelect>();
      select.Items[0].Expr.ShouldBeOfType<BoundFunctionCall>().Arguments[1].Scalar.Kind.ShouldBe(ScalarKind.Int32);
      select.Input.ShouldBeOfType<BoundWhere>().Predicate.ShouldBeOfType<BoundBinary>().Right.Scalar.Kind.ShouldBe(ScalarKind.Int32);
   }

   [Fact]
   public void NullParametersTakeTheTypeTheyMeet()
   {
      BoundProgram program = Bind("sales.orders.where(total > $floor)", new QueryParameters().Add("floor", null));
      BoundBinary compare = ((BoundWhere)program.Query!).Predicate.ShouldBeOfType<BoundBinary>();
      compare.Right.ShouldBeOfType<BoundParameter>().Scalar.ToString().ShouldBe("decimal(10,2)?");
   }

   [Fact]
   public void ParametersTypesAreThoseTheyTake()
   {
      BoundProgram program = Bind("sales.orders.where(total > $floor and order_date >= $since and status == $status).take($n)",
         new QueryParameters().Add("floor", null).Add("since", "2026-01-01").Add("status", null).Add("n", 5L));
      program.ParameterTypes.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}")
         .ShouldBe(["floor decimal(10,2)?", "n int64", "since date", "status string(20,ansi)?"]);
   }

   [Fact]
   public void IntegerDivisionGivesADouble()
   {
      BoundQuery query = Query("sales.order_lines.select(a: qty / 2, b: price / 2, c: qty % 2)");
      query.Shape.ToString().ShouldBe("[a double, b decimal, c int32]");
   }

   [Fact]
   public void OptionalNavigationsMakeTheirColumnsNullable()
   {
      Query("sales.orders.select(a: customer.name, b: ship_customer.name)").Shape.ToString()
         .ShouldBe("[a string(100), b string(100)?]");
   }

   [Fact]
   public void CrossSourceNavigationsBind()
   {
      Query("crm.contacts.select(email, who: customer.name)").Shape.ToString().ShouldBe("[email string, who string(100)?]");
   }

   [Fact]
   public void NullComparisonsBecomeNullTests()
   {
      BoundIsNull test = Predicate<BoundIsNull>("sales.orders.where(null != shipped_at)");
      test.Negated.ShouldBeTrue();
      test.Operand.ShouldBeOfType<BoundMemberAccess>().Member.Name.ShouldBe("shipped_at");
   }

   [Fact]
   public void ThenByAddsToThePreviousSort()
   {
      BoundOrderBy order = Query("sales.orders.orderBy(total).thenBy(desc(id))").ShouldBeOfType<BoundOrderBy>();
      order.Keys.Select(k => (BoundTreePrinter.Expr(k.Expr), k.Descending)).ShouldBe([("it.total", false), ("it.id", true)]);
      order.Input.ShouldBeOfType<BoundEntityScan>();
   }

   [Fact]
   public void LambdasNameTheRow()
   {
      BoundWhere where = Query("sales.orders.where(o => o.total > 1)").ShouldBeOfType<BoundWhere>();
      where.Row.Name.ShouldBe("o");
      BoundTreePrinter.Expr(where.Predicate).ShouldBe("o.total > 1");
   }

   [Fact]
   public void MethodCallSyntaxOnValuesCallsFunctions()
   {
      string a = BoundTreePrinter.Print(Query("sales.customers.select(x: name.lower())"));
      string b = BoundTreePrinter.Print(Query("sales.customers.select(x: lower(name))"));
      a.ShouldBe(b);
   }

   [Fact]
   public void ScalarLetsOfConstantsAreInlined()
   {
      BoundBinary compare = Predicate<BoundBinary>("limit := 1000; sales.customers.where(credit_limit > limit)");
      BoundLiteral literal = compare.Right.ShouldBeOfType<BoundLiteral>();
      literal.Value.ShouldBe(1000m);
   }

   [Fact]
   public void QueryLetsAreReferencedNotCopied()
   {
      BoundProgram program = Bind("big := sales.orders.where(total > 100); big.take(1)");
      program.Lets.ShouldHaveSingleItem().IsQuery.ShouldBeTrue();
      program.Query.ShouldBeOfType<BoundTake>().Input.ShouldBeOfType<BoundLetQuery>().Let.Name.ShouldBe("big");
   }

   [Fact]
   public void ColumnsHidingASubtreeGiveAWarning()
   {
      BoundProgram program = Binder.Bind("status := 5; sales.orders.where(status == 'open')", Catalog);
      program.Success.ShouldBeTrue();
      program.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.ShadowedName);
   }

   [Fact]
   public void ScalarResultsAreAllowed()
   {
      Bind("1 + 2").Result.ShouldBeOfType<BoundBinary>().Scalar.Kind.ShouldBe(ScalarKind.Int64);
   }

   [Fact]
   public void SyntaxErrorsPassThrough()
   {
      BoundProgram program = Binder.Bind("sales.orders.where(", Catalog);
      program.Success.ShouldBeFalse();
      program.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.SyntaxError);
   }

   [Fact]
   public void ErrorsPointAtTheOffendingText()
   {
      const string query = "sales.orders.where(statuss == 'open')";
      QueryDiagnostic error = Binder.Bind(query, Catalog).Diagnostics.ShouldHaveSingleItem();
      query[error.Start..error.End].ShouldBe("statuss");
      error.Message.ShouldContain("Did you mean 'status'?");
   }
}
