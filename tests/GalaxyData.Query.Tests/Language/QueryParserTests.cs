using System.Linq;
using GalaxyData.Common;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Language;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Language;

public sealed class QueryParserTests
{
   private static string Print(string source)
   {
      ParseResult result = QueryParser.Default.Parse(source);
      result.Diagnostics.ShouldBeEmpty();
      return SyntaxPrinter.Print(result.Root!);
   }

   [Theory]
   [InlineData("a and b or not c", "(or (and a b) (pre:not c))")]
   [InlineData("not a == b && c", "(&& (pre:not (== a b)) c)")]
   [InlineData("not a and b", "(and (pre:not a) b)")]
   [InlineData("a <> b", "(<> a b)")]
   [InlineData("a--b", "(- a (pre:- b))")]
   [InlineData("a++b", "(+ a (pre:+ b))")]
   [InlineData("x in [1, 2]", "(in x [1 2])")]
   [InlineData("x in ys.select(id)", "(in x (call (. ys select) id))")]
   [InlineData("not x in [1] or y", "(or (pre:not (in x [1])) y)")]
   [InlineData("a ?? b ?? c", "(?? a (?? b c))")]
   [InlineData("inner.index + outer.inbox", "(+ (. inner index) (. outer inbox))")]
   [InlineData("it[\"in\"] == 1", "(== (index it 'in') 1)")]
   [InlineData("total > 3.5", "(> total 3.5m)")]
   [InlineData("ratio > 1e-3", "(> ratio 0.001d)")]
   public void OperatorsFollowTheQueryTable(string source, string expected)
   {
      Print(source).ShouldBe(expected);
   }

   [Fact]
   public void WordOperatorsCannotBeUsedAsMemberNames()
   {
      QueryParser.Default.Parse("order.in").Success.ShouldBeFalse();
   }

   [Fact]
   public void FilterProjectionAndNavigationExampleParses()
   {
      Print("sales.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city)")
         .ShouldBe("(call (. (call (. (. sales orders) where) (== status 'open')) select) " +
                   "id total (who: (. customer name)) (city: (. customer city)))");
   }

   [Fact]
   public void AggregateExampleParses()
   {
      Print("sales.orders.groupBy(customer_id).select(customer_id: key, spend: sum(total), orders: count())")
         .ShouldBe("(call (. (call (. (. sales orders) groupBy) customer_id) select) " +
                   "(customer_id: key) (spend: (call sum total)) (orders: (call count)))");
   }

   [Fact]
   public void ExplicitJoinExampleParses()
   {
      Print("sales.orders.join(sales.customers, outer.customer_id == inner.id, orders: outer, cust: inner)" +
            ".groupBy(cust.name).select(name: cust.name, count: orders.count(), total: orders.sum(total))")
         .ShouldBe("(call (. (call (. (call (. (. sales orders) join) (. sales customers) " +
                   "(== (. outer customer_id) (. inner id)) (orders: outer) (cust: inner)) groupBy) (. cust name)) select) " +
                   "(name: (. cust name)) (count: (call (. orders count))) (total: (call (. orders sum) total)))");
   }

   [Fact]
   public void NamedSubtreeExampleParses()
   {
      Print("x := sales.orders.where(total > 3); " +
            "sales.customers.where(cust => x.any(order => cust.id == order.customer_id))")
         .ShouldBe("(block (:= x (call (. (. sales orders) where) (> total 3))) " +
                   "(call (. (. sales customers) where) (=> (cust) (call (. x any) " +
                   "(=> (order) (== (. cust id) (. order customer_id)))))))");
   }

   [Fact]
   public void LongMethodChainsFitTheQueryDepth()
   {
      string chain = "t" + string.Concat(Enumerable.Repeat(".where(x)", 100));
      QueryParser.Default.Parse(chain).Success.ShouldBeTrue();
      Should.Throw<SyntaxErrorException>(() => new ExpressionParser().Parse(chain));
   }

   [Fact]
   public void SyntaxErrorsBecomeDiagnostics()
   {
      ParseResult result = QueryParser.Default.Parse("sales.orders.where(status == )");
      result.Success.ShouldBeFalse();
      QueryDiagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
      diagnostic.Code.ShouldBe(DiagnosticCodes.SyntaxError);
      diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
      diagnostic.Start.ShouldBe(29);
      diagnostic.End.ShouldBe(30);
      diagnostic.Message.ShouldContain("Expected an expression");
   }

   [Fact]
   public void ErrorAtTheEndOfTheTextHasAnEmptyRange()
   {
      ParseResult result = QueryParser.Default.Parse("sales.orders.where(");
      QueryDiagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
      diagnostic.Start.ShouldBe(19);
      diagnostic.End.ShouldBe(19);
   }

   [Fact]
   public void FractionalLiteralsAreDecimals()
   {
      ParseResult result = QueryParser.Default.Parse("0.1");
      result.Root.ShouldBeOfType<LiteralSyntax>().Value.Type.ShouldBe(DynamicNodeType.Decimal);
   }
}
