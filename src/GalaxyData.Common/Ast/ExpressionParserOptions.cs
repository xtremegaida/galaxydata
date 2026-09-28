using System;

namespace GalaxyData.Common.Ast;

public sealed class ExpressionParserOptions
{
   public static readonly ExpressionParserOptions Default = new();

   private readonly int maxDepth = ExpressionParser.DefaultMaxDepth;

   /// <summary>How deeply nodes may nest; every operator and every call in a chain is a level of its own.</summary>
   public int MaxDepth
   {
      get => maxDepth;
      init
      {
         ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
         maxDepth = value;
      }
   }

   /// <summary>
   /// When set, a literal with a fractional part and no exponent (<c>3.5</c>) and an integer too large for a
   /// long read as <see cref="decimal"/> instead of <see cref="double"/>; a literal with an exponent
   /// (<c>3.5e0</c>) stays a double.
   /// </summary>
   public bool FractionalLiteralsAsDecimal { get; init; }
}
