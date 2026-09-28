using GalaxyData.Common.Ast;

namespace GalaxyData.Query.Language;

/// <summary>
/// The operator table of the query language: the default table without increment and decrement (so
/// <c>a--b</c> is <c>a - (-b)</c>), plus <c>&lt;&gt;</c> and the word operators <c>and</c>, <c>or</c>,
/// <c>not</c> and <c>in</c>. <c>=</c> stays registered because <c>let x = ...</c> needs it; the binder rejects
/// it inside expressions.
/// </summary>
public static class QueryOperatorTable
{
   public const int NotPrecedence = 55;

   public static OperatorTable Create()
   {
      OperatorTable table = OperatorTable.CreateDefault();
      table.RemovePrefix("++");
      table.RemovePrefix("--");
      table.RemovePostfix("++");
      table.RemovePostfix("--");
      table.AddBinary("<>", 90);
      table.AddBinary("and", 50);
      table.AddBinary("or", 40);
      table.AddPrefix("not", NotPrecedence);
      table.AddBinary("in", 100);
      return table;
   }

   /// <summary>Words the lexer reads as operators, so they cannot be used as bare names.</summary>
   public static readonly string[] WordOperators = ["and", "or", "not", "in"];
}
