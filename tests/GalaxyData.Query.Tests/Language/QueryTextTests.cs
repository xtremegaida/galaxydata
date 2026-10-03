using System.Linq;
using GalaxyData.Query.Language;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Language;

public sealed class QueryTextTests
{
   [Theory]
   [InlineData("orders", true)]
   [InlineData("_private", true)]
   [InlineData("$param", true)]
   [InlineData("Ünïcode", true)]
   [InlineData("a1", true)]
   [InlineData("1a", false)]
   [InlineData("", false)]
   [InlineData("has space", false)]
   [InlineData("dash-ed", false)]
   [InlineData("a::b", false)]
   [InlineData("true", false)]
   [InlineData("null", false)]
   [InlineData("and", false)]
   [InlineData("in", false)]
   [InlineData("not", false)]
   [InlineData("inner", true)]
   [InlineData("True", true)]
   public void BareIdentifiers(string name, bool expected)
   {
      QueryText.IsBareIdentifier(name).ShouldBe(expected);
   }

   [Theory]
   [InlineData("plain", "'plain'")]
   [InlineData("O'Brien", "'O\\'Brien'")]
   [InlineData("back\\slash", "'back\\\\slash'")]
   [InlineData("two\nlines", "'two\\nlines'")]
   public void QuotesStrings(string value, string expected)
   {
      QueryText.QuoteString(value).ShouldBe(expected);
   }

   [Fact]
   public void FormatsPathsWithIndexersForAwkwardNames()
   {
      QueryText.FormatPath(["xl", "Budget 2024", "Sheet 1"]).ShouldBe("xl['Budget 2024']['Sheet 1']");
      QueryText.FormatPath(["shop", "main", "orders"]).ShouldBe("shop.main.orders");
      QueryText.FormatPath(["shop", "in"]).ShouldBe("shop['in']");
   }

   [Theory]
   [InlineData("total", "total")]
   [InlineData("Total Spend", "it['Total Spend']")]
   [InlineData("it", "it['it']")]
   [InlineData("key", "it['key']")]
   [InlineData("in", "it['in']")]
   [InlineData("$x", "it['$x']")]
   [InlineData("o'brien", "it['o\\'brien']")]
   public void NamesForTheImplicitRow(string name, string expected)
   {
      QueryText.QuoteName(name).ShouldBe(expected);
   }

   [Theory]
   [InlineData("shop.orders", "shop.orders")]
   [InlineData("x := shop.orders; x.where(total > 1);", "x := shop.orders|x.where(total > 1)")]
   [InlineData("x := shop.orders.where(s == ';');  // last ; one\n x", "x := shop.orders.where(s == ';')|x")]
   [InlineData("a := f(1; 2); /* ; */ # ;\n b", "a := f(1; 2)|b")]
   [InlineData(";; shop.orders ;", "shop.orders")]
   [InlineData("x := `raw ; text`; x", "x := `raw ; text`|x")]
   [InlineData("x := 'it\\'s; fine'; x", "x := 'it\\'s; fine'|x")]
   [InlineData("", "")]
   public void StatementsSplitOnTopLevelSemicolons(string text, string expected)
   {
      string.Join("|", QueryText.SplitStatements(text).Select(r => text[r])).ShouldBe(expected);
   }

   [Theory]
   [InlineData("shop.orders.where(total > $min and order_date >= $since and total < $min)", "min,since")]
   [InlineData("x := $a; x // $b\n + 'it''s $c' + \"$d\" /* $e */ + `$f` + a$g + it[\"$h\"] + $Z1", "a,Z1")]
   [InlineData("$", "")]
   [InlineData("'unterminated $x", "")]
   public void ParametersAreFoundOutsideStringsAndComments(string text, string expected) =>
      string.Join(",", QueryText.Parameters(text)).ShouldBe(expected);

   [Fact]
   public void ParametersUsesArePlaced()
   {
      const string text = "where(a > $min and b < $min2 and c == $min)";
      QueryText.ParameterUses(text).Select(u => $"{u.Name} {text[u.Range]}").ShouldBe(["min $min", "min2 $min2", "min $min"]);
      // Many parameters are found in time proportional to the text.
      string many = string.Join(" + ", Enumerable.Range(0, 20_000).Select(i => "$p" + i));
      QueryText.Parameters(many).Count.ShouldBe(20_000);
   }

   [Fact]
   public void ComposeWrapsTheLastStatement()
   {
      QueryText.Compose("shop.orders").ShouldBe("shop.orders");
      QueryText.Compose("shop.orders.select(id, total)", ["total > 100"]).ShouldBe("(shop.orders.select(id, total)).where(total > 100)");
      QueryText.Compose("big := shop.orders.where(total > 100);\nbig.select(id) // the ids\n", ["id > 3", " "], [new QuerySortKey("id", Descending: true)])
         .ShouldBe("big := shop.orders.where(total > 100);\n(big.select(id)).where(id > 3).orderBy(desc(id)) // the ids\n");
      QueryText.Compose("shop.orders;", sort: [new QuerySortKey(QueryText.QuoteName("Total Spend"))], tiebreak: ["id", "Total Spend"])
         .ShouldBe("(shop.orders).orderBy(it['Total Spend'], id);");
      QueryText.Compose("shop.orders", tiebreak: ["id"]).ShouldBe("shop.orders");
      Should.Throw<System.ArgumentException>(() => QueryText.Compose("  // nothing", ["a"]));
   }
}
