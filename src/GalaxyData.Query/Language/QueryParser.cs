using System;
using System.Collections.Generic;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Diagnostics;

namespace GalaxyData.Query.Language;

public sealed record ParseResult(string Text, SyntaxNode? Root, IReadOnlyList<QueryDiagnostic> Diagnostics)
{
   public bool Success => Root != null;
}

/// <summary>Parses query text with the query operator table; syntax errors come back as diagnostics.</summary>
public sealed class QueryParser
{
   public const int MaxDepth = 256;

   public static QueryParser Default { get; } = new();

   private readonly ExpressionParser parser = new(
      QueryOperatorTable.Create(),
      new ExpressionParserOptions { MaxDepth = MaxDepth, FractionalLiteralsAsDecimal = true });

   public ExpressionParser Expressions => parser;

   public ParseResult Parse(string text)
   {
      ArgumentNullException.ThrowIfNull(text);
      if (parser.TryParse(text, out SyntaxNode? root, out SyntaxErrorException? error))
      {
         return new ParseResult(text, root, []);
      }
      int start = Math.Clamp(error.Index, 0, text.Length);
      int end = Math.Min(start + 1, text.Length);
      return new ParseResult(text, null, [QueryDiagnostic.Error(DiagnosticCodes.SyntaxError, error.Description, start, end)]);
   }
}
