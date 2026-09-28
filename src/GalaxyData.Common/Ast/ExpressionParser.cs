using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace GalaxyData.Common.Ast;

public sealed class ExpressionParser
{
   private readonly CompiledOperatorTable operators;
   private readonly int maxDepth;
   private readonly bool decimalLiterals;

   public ExpressionParser() : this(OperatorTable.CreateDefault()) { }

   public ExpressionParser(OperatorTable operators, int maxDepth = DefaultMaxDepth)
      : this(operators, new ExpressionParserOptions { MaxDepth = maxDepth }) { }

   public ExpressionParser(OperatorTable operators, ExpressionParserOptions options)
   {
      ArgumentNullException.ThrowIfNull(operators);
      ArgumentNullException.ThrowIfNull(options);
      this.operators = operators.Compile();
      maxDepth = options.MaxDepth;
      decimalLiterals = options.FractionalLiteralsAsDecimal;
   }

   public const int DefaultMaxDepth = 64;

   #region Public API

   public SyntaxNode Parse(string text)
   {
      ArgumentNullException.ThrowIfNull(text);
      ParseState state = new(text);
      Advance(ref state);
      if (state.Current.Kind == TokenKind.End)
      {
         throw new SyntaxErrorException(Empty(text), text, 0);
      }
      int at = state.Current.Start;
      List<SyntaxNode> statements = ParseStatementList(ref state, TokenKind.End);
      if (statements.Count == 0)
      {
         throw new SyntaxErrorException(Empty(text), text, 0);
      }
      return statements.Count == 1 ? statements[0] : Bounded(new BlockSyntax([.. statements], at), at, in state);
   }

   private static string Empty(string text) =>
      !text.Contains('#') && !text.Contains("//", StringComparison.Ordinal) &&
      !text.Contains("/*", StringComparison.Ordinal)
         ? "Expression is empty"
         : "This is only a comment, so there is nothing to run. A folded block scalar — \">-\" — joins " +
           "its lines with spaces, so a \"#\" or \"//\" comment on its own line swallows the lines below " +
           "it; write \"|-\" to keep the line breaks, or comment with \"/* ... */\"";

   public bool TryParse(string text, [NotNullWhen(true)] out SyntaxNode? result)
   {
      return TryParse(text, out result, out _);
   }

   public bool TryParse(
      string text, [NotNullWhen(true)] out SyntaxNode? result, [NotNullWhen(false)] out SyntaxErrorException? error)
   {
      try
      {
         result = Parse(text);
         error = null;
         return true;
      }
      catch (SyntaxErrorException ex)
      {
         result = null;
         error = ex;
         return false;
      }
   }

   public static int SkipTrivia(string text, int at)
   {
      ArgumentNullException.ThrowIfNull(text);
      if (at < 0) { return 0; }
      try
      {
         ExpressionLexer.SkipIgnored(text, ref at);
         return at;
      }
      catch (SyntaxErrorException)
      {
         return text.Length;
      }
   }

   public IReadOnlyList<Range> SplitStatements(string text)
   {
      ArgumentNullException.ThrowIfNull(text);
      List<Range> statements = new(4);
      int position = 0;
      int start = 0;
      int depth = 0;

      try
      {
         while (true)
         {
            Token token = ExpressionLexer.Next(text, ref position, operators);
            switch (token.Kind)
            {
               case TokenKind.LeftParen:
               case TokenKind.LeftBracket:
               case TokenKind.LeftBrace:
                  depth++;
                  break;

               case TokenKind.RightParen:
               case TokenKind.RightBracket:
                  if (depth > 0) { depth--; }
                  break;

               case TokenKind.RightBrace:
                  if (depth == 0) { break; }
                  if (--depth == 0 && StartsStatement(text, Peek(text, position)))
                  {
                     Statement(statements, text, start, position);
                     start = position;
                  }
                  break;

               case TokenKind.Semicolon when depth == 0:
                  if (IsWord(text, Peek(text, position), ElseKeyword)) { break; }
                  Statement(statements, text, start, token.Start);
                  start = position;
                  break;

               case TokenKind.End:
                  Statement(statements, text, start, text.Length);
                  return statements;
            }
         }
      }
      catch (SyntaxErrorException)
      {
         Statement(statements, text, start, text.Length);
         return statements;
      }
   }

   public int Balance(string text, int opener)
   {
      ArgumentNullException.ThrowIfNull(text);
      if (opener < 0 || opener >= text.Length) { return -1; }

      int position = opener;
      int depth = 0;
      try
      {
         while (true)
         {
            Token token = ExpressionLexer.Next(text, ref position, operators);
            switch (token.Kind)
            {
               case TokenKind.LeftParen:
               case TokenKind.LeftBracket:
               case TokenKind.LeftBrace:
                  if (depth == 0 && token.Start != opener) { return -1; }
                  depth++;
                  break;

               case TokenKind.RightParen:
               case TokenKind.RightBracket:
               case TokenKind.RightBrace:
                  if (depth == 0) { return -1; }
                  if (--depth == 0) { return position; }
                  break;

               case TokenKind.End:
                  return -1;

               default:
                  if (depth == 0) { return -1; }
                  break;
            }
         }
      }
      catch (SyntaxErrorException)
      {
         return -1;
      }
   }

   private static void Statement(List<Range> into, string text, int from, int to)
   {
      from = Math.Min(SkipTrivia(text, from), to);
      while (to > from && char.IsWhiteSpace(text[to - 1])) { to--; }
      if (to > from) { into.Add(new Range(from, to)); }
   }

   #endregion

   #region Pratt engine

   private List<SyntaxNode> ParseStatementList(ref ParseState state, TokenKind closer)
   {
      List<SyntaxNode> statements = new(4);
      while (true)
      {
         while (state.Current.Kind == TokenKind.Semicolon) { Advance(ref state); }
         if (state.Current.Kind == closer) { break; }
         if (closer == TokenKind.RightBrace && state.Current.Kind == TokenKind.End)
         {
            throw new SyntaxErrorException($"Expected '}}' to close the block but found {ExpressionLexer.Describe(state.Source, in state.Current)}", state.Source, state.Current.Start);
         }
         statements.Add(ParseStatement(ref state));
         if (state.Current.Kind == TokenKind.Semicolon) { continue; }
         if (state.Current.Kind == closer) { break; }
         if (state.Previous is TokenKind.Semicolon or TokenKind.RightBrace) { continue; }
         string what = closer == TokenKind.End ? "';' or the end of the expression" : "';' or '}' in the block";
         throw new SyntaxErrorException($"Expected {what} but found {ExpressionLexer.Describe(state.Source, in state.Current)}", state.Source, state.Current.Start);
      }
      return statements;
   }

   private SyntaxNode ParseStatement(ref ParseState state)
   {
      if (IsWord(state.Source, in state.Current, LetKeyword) && IsDeclaration(ref state))
      {
         return ParseLet(ref state);
      }
      if (IsWord(state.Source, in state.Current, BreakKeyword))
      {
         int at = state.Current.Start;
         Advance(ref state);
         SyntaxNode? value = null;
         if (!Ends(state.Current.Kind)) { value = ParseExpression(ref state, 0); }
         return Bounded(new BreakSyntax(value, at), at, in state);
      }
      if (IsWord(state.Source, in state.Current, ContinueKeyword))
      {
         Token keyword = state.Current;
         Advance(ref state);
         return Leaf(new ContinueSyntax(keyword.Start), in keyword);
      }
      return ParseExpression(ref state, 0);
   }

   private bool IsDeclaration(ref ParseState state)
   {
      int position = state.Position;
      if (ExpressionLexer.Next(state.Source, ref position, operators).Kind != TokenKind.Identifier)
      {
         return false;
      }
      Token assign = ExpressionLexer.Next(state.Source, ref position, operators);
      return assign.Kind == TokenKind.Operator && assign.Operator!.Symbol == "=";
   }

   private SyntaxNode ParseLet(ref ParseState state)
   {
      int at = state.Current.Start;
      Advance(ref state);
      Token name = state.Current;
      Advance(ref state);
      Advance(ref state);
      SyntaxNode value = ParseExpression(ref state, 0);
      return Bounded(new LetSyntax(state.Source.Substring(name.Start, name.Length), value, at), at, in state);
   }

   private SyntaxNode ParseFor(ref ParseState state)
   {
      int at = state.Current.Start;
      Advance(ref state);
      Advance(ref state);

      SyntaxNode? init = null;
      if (state.Current.Kind != TokenKind.Semicolon) { init = ParseStatement(ref state); }
      Expect(ref state, TokenKind.Semicolon, "';' after the first part of \"for\"");
      if (state.Current.Kind == TokenKind.Semicolon)
      {
         throw new SyntaxErrorException("A \"for\" needs a condition; one without it never stops on its own", state.Source, state.Current.Start);
      }
      SyntaxNode condition = ParseExpression(ref state, 0);
      Expect(ref state, TokenKind.Semicolon, "';' after the condition of \"for\"");
      SyntaxNode? step = null;
      if (state.Current.Kind != TokenKind.RightParen) { step = ParseExpression(ref state, 0); }
      Expect(ref state, TokenKind.RightParen, "')' to close the head of \"for\"");

      SyntaxNode body = ParseStatement(ref state);
      return Bounded(new ForSyntax(init, condition, step, body, at), at, in state);
   }

   private SyntaxNode ParseIf(ref ParseState state)
   {
      int at = state.Current.Start;
      Advance(ref state);
      Advance(ref state);
      SyntaxNode condition = ParseExpression(ref state, 0);
      Expect(ref state, TokenKind.RightParen, "')' to close the condition of \"if\"");

      SyntaxNode then = ParseStatement(ref state);
      int end = state.LastEnd;
      SyntaxNode? otherwise = null;
      if (state.Current.Kind == TokenKind.Semicolon) { Advance(ref state); }
      if (IsWord(state.Source, in state.Current, ElseKeyword))
      {
         Advance(ref state);
         otherwise = ParseStatement(ref state);
         end = state.LastEnd;
      }

      return Bounded(new IfSyntax(condition, then, otherwise, at), at, end, in state);
   }

   private SyntaxNode ParseExpression(ref ParseState state, int minBindingPower)
   {
      if (++state.Depth > maxDepth)
      {
         throw new SyntaxErrorException($"Expression is nested too deeply (maximum depth is {maxDepth})", state.Source, state.Current.Start);
      }
      int start = state.Current.Start;
      SyntaxNode left = ParsePrimary(ref state);
      while (true)
      {
         TokenKind kind = state.Current.Kind;
         if (kind == TokenKind.Question)
         {
            if (operators.TernaryPrecedence < minBindingPower) { break; }
            int at = state.Current.Start;
            Advance(ref state);
            SyntaxNode whenTrue = ParseExpression(ref state, 0);
            Expect(ref state, TokenKind.Colon, "':' for the conditional operator");
            SyntaxNode whenFalse = ParseExpression(ref state, operators.TernaryPrecedence);
            left = Bounded(new TernarySyntax(left, whenTrue, whenFalse, at), start, in state);
            continue;
         }
         if (kind == TokenKind.Arrow)
         {
            if (operators.LambdaPrecedence < minBindingPower) { break; }
            if (left is not IdentifierSyntax parameter)
            {
               throw new SyntaxErrorException("Expected a parameter list on the left side of '=>'", state.Source, state.Current.Start);
            }
            int arrowAt = state.Current.Start;
            Advance(ref state);
            SyntaxNode lambdaBody = ParseExpression(ref state, operators.LambdaPrecedence);
            left = Bounded(new LambdaSyntax([parameter], lambdaBody, arrowAt), start, in state);
            continue;
         }
         if (kind == TokenKind.LeftParen)
         {
            if (operators.CallPrecedence < minBindingPower) { break; }
            int at = state.Current.Start;
            SyntaxNode[] args = ParseArguments(ref state, TokenKind.RightParen);
            left = Bounded(new CallSyntax(SyntaxKind.Call, left, args, at), start, in state);
            continue;
         }
         if (kind == TokenKind.LeftBracket)
         {
            if (operators.IndexPrecedence < minBindingPower) { break; }
            int at = state.Current.Start;
            SyntaxNode[] args = ParseArguments(ref state, TokenKind.RightBracket);
            left = Bounded(new CallSyntax(SyntaxKind.Index, left, args, at), start, in state);
            continue;
         }
         if (kind != TokenKind.Operator) { break; }
         OpEntry op = state.Current.Operator!;
         bool hasBinary = op.BinaryPrecedence >= 0;
         bool hasPostfix = op.PostfixPrecedence >= 0;
         if (!hasBinary && !hasPostfix) { break; }
         bool useBinary = hasBinary;
         if (hasBinary && hasPostfix)
         {
            int peekPosition = state.Position;
            Token peek = ExpressionLexer.Next(state.Source, ref peekPosition, operators);
            useBinary = StartsOperand(in peek);
         }
         if (useBinary)
         {
            if (op.BinaryPrecedence < minBindingPower) { break; }
            int at = state.Current.Start;
            Advance(ref state);
            int rhsBindingPower = op.BinaryRightAssociative ? op.BinaryPrecedence : op.BinaryPrecedence + 1;
            SyntaxNode right = ParseExpression(ref state, rhsBindingPower);
            left = Bounded(new BinarySyntax(op.Symbol, left, right, at), start, in state);
         }
         else
         {
            if (op.PostfixPrecedence < minBindingPower) { break; }
            int at = state.Current.Start;
            Advance(ref state);
            left = Bounded(new UnarySyntax(SyntaxKind.Postfix, op.Symbol, left, at), start, in state);
         }
      }
      state.Depth--;
      return left;
   }

   private SyntaxNode ParsePrimary(ref ParseState state)
   {
      Token token = state.Current;
      switch (token.Kind)
      {
         case TokenKind.Integer:
            Advance(ref state);
            return Leaf(new LiteralSyntax(new DynamicNode(token.IntegerValue), token.Start), in token);
         case TokenKind.Number:
            Advance(ref state);
            return Leaf(new LiteralSyntax(NumberValue(state.Source, in token), token.Start), in token);
         case TokenKind.String:
            Advance(ref state);
            return Leaf(new LiteralSyntax(new DynamicNode(ExpressionLexer.GetStringValue(state.Source, in token)), token.Start), in token);
         case TokenKind.True:
            Advance(ref state);
            return Leaf(new LiteralSyntax(DynamicNode.True, token.Start), in token);
         case TokenKind.False:
            Advance(ref state);
            return Leaf(new LiteralSyntax(DynamicNode.False, token.Start), in token);
         case TokenKind.Null:
            Advance(ref state);
            return Leaf(new LiteralSyntax(DynamicNode.Null, token.Start), in token);
         case TokenKind.Identifier:
            if (IsWord(state.Source, in token, ForKeyword) && Peek(state.Source, state.Position).Kind == TokenKind.LeftParen)
            {
               return ParseFor(ref state);
            }
            if (IsWord(state.Source, in token, IfKeyword) && Peek(state.Source, state.Position).Kind == TokenKind.LeftParen)
            {
               return ParseIf(ref state);
            }
            Advance(ref state);
            return Leaf(new IdentifierSyntax(state.Source.Substring(token.Start, token.Length), token.Start), in token);
         case TokenKind.LeftParen:
            {
               if (IsParenthesizedLambda(ref state))
               {
                  return ParseParenthesizedLambda(ref state);
               }
               Advance(ref state);
               SyntaxNode inner = ParseExpression(ref state, 0);
               Expect(ref state, TokenKind.RightParen, "')' to close the group");
               return inner;
            }
         case TokenKind.LeftBracket:
            return ParseArrayLiteral(ref state);
         case TokenKind.LeftBrace:
            {
               if (IsObjectLiteral(ref state))
               {
                  return ParseObjectLiteral(ref state);
               }
               Advance(ref state);
               List<SyntaxNode> statements = ParseStatementList(ref state, TokenKind.RightBrace);
               Advance(ref state);
               return Bounded(new BlockSyntax([.. statements], token.Start), token.Start, in state);
            }
         case TokenKind.Operator:
            {
               OpEntry op = token.Operator!;
               if (op.PrefixPrecedence >= 0)
               {
                  Advance(ref state);
                  SyntaxNode operand = ParseExpression(ref state, op.PrefixPrecedence);
                  return Bounded(new UnarySyntax(SyntaxKind.Prefix, op.Symbol, operand, token.Start), token.Start, in state);
               }
               break;
            }
      }
      throw new SyntaxErrorException($"Expected an expression but found {ExpressionLexer.Describe(state.Source, in state.Current)}", state.Source, state.Current.Start);
   }

   private SyntaxNode[] ParseArguments(ref ParseState state, TokenKind closer)
   {
      Advance(ref state);
      List<SyntaxNode> args = new(4);
      if (!(closer == TokenKind.RightParen && state.Current.Kind == TokenKind.RightParen))
      {
         while (true)
         {
            args.Add(ParseArgument(ref state, closer));
            if (state.Current.Kind != TokenKind.Comma) { break; }
            Advance(ref state);
            if (state.Current.Kind == closer) { break; }
         }
      }
      if (state.Current.Kind != closer)
      {
         string what = closer == TokenKind.RightParen ? "')' or ',' in the argument list" : "']' or ',' in the indexer";
         throw new SyntaxErrorException($"Expected {what} but found {ExpressionLexer.Describe(state.Source, in state.Current)}", state.Source, state.Current.Start);
      }
      Advance(ref state);
      return [.. args];
   }

   private SyntaxNode ParseArgument(ref ParseState state, TokenKind closer)
   {
      if (closer == TokenKind.RightParen && state.Current.Kind is TokenKind.Identifier or TokenKind.String)
      {
         int peekPosition = state.Position;
         Token peek = ExpressionLexer.Next(state.Source, ref peekPosition, operators);
         if (peek.Kind == TokenKind.Colon)
         {
            Token name = state.Current;
            string text = name.Kind == TokenKind.String
               ? ExpressionLexer.GetStringValue(state.Source, in name)
               : state.Source.Substring(name.Start, name.Length);
            Advance(ref state);
            Advance(ref state);
            SyntaxNode value = ParseExpression(ref state, 0);
            return Bounded(new NamedArgumentSyntax(text, value, name.Start), name.Start, in state);
         }
      }
      return ParseExpression(ref state, 0);
   }

   private SyntaxNode ParseArrayLiteral(ref ParseState state)
   {
      int at = state.Current.Start;
      Advance(ref state);
      List<SyntaxNode> elements = new(4);
      if (state.Current.Kind != TokenKind.RightBracket)
      {
         while (true)
         {
            elements.Add(ParseExpression(ref state, 0));
            if (state.Current.Kind != TokenKind.Comma) { break; }
            Advance(ref state);
            if (state.Current.Kind == TokenKind.RightBracket) { break; }
         }
      }
      if (state.Current.Kind != TokenKind.RightBracket)
      {
         throw new SyntaxErrorException($"Expected ']' or ',' in the list but found {ExpressionLexer.Describe(state.Source, in state.Current)}", state.Source, state.Current.Start);
      }
      Advance(ref state);
      return Bounded(new ArraySyntax([.. elements], at), at, in state);
   }

   private bool IsObjectLiteral(ref ParseState state)
   {
      int position = state.Position;
      Token first = ExpressionLexer.Next(state.Source, ref position, operators);

      if (first.Kind == TokenKind.RightBrace) { return true; }

      if (first.Kind != TokenKind.Identifier && first.Kind != TokenKind.String) { return false; }
      return ExpressionLexer.Next(state.Source, ref position, operators).Kind == TokenKind.Colon;
   }

   private SyntaxNode ParseObjectLiteral(ref ParseState state)
   {
      int at = state.Current.Start;
      Advance(ref state);
      if (state.Current.Kind == TokenKind.RightBrace)
      {
         Advance(ref state);
         return Bounded(new ObjectSyntax([], at), at, in state);
      }
      List<NamedArgumentSyntax> members = new(4);
      HashSet<string> named = new(StringComparer.OrdinalIgnoreCase);
      while (true)
      {
         Token key = state.Current;
         string name;
         if (key.Kind == TokenKind.Identifier) { name = state.Source.Substring(key.Start, key.Length); }
         else if (key.Kind == TokenKind.String) { name = ExpressionLexer.GetStringValue(state.Source, in key); }
         else
         {
            throw new SyntaxErrorException($"Expected a property name but found {ExpressionLexer.Describe(state.Source, in key)}", state.Source, key.Start);
         }
         if (!named.Add(name))
         {
            throw new SyntaxErrorException($"The property '{name}' is given more than once", state.Source, key.Start);
         }
         Advance(ref state);
         Expect(ref state, TokenKind.Colon, "':' after the property name");
         SyntaxNode value = ParseExpression(ref state, 0);
         members.Add(Bounded(new NamedArgumentSyntax(name, value, key.Start), key.Start, in state));
         if (state.Current.Kind != TokenKind.Comma) { break; }
         Advance(ref state);
         if (state.Current.Kind == TokenKind.RightBrace) { break; }
      }
      if (state.Current.Kind != TokenKind.RightBrace)
      {
         throw new SyntaxErrorException($"Expected '}}' or ',' in the object but found {ExpressionLexer.Describe(state.Source, in state.Current)}", state.Source, state.Current.Start);
      }
      Advance(ref state);
      return Bounded(new ObjectSyntax([.. members], at), at, in state);
   }

   private bool IsParenthesizedLambda(ref ParseState state)
   {
      int position = state.Position;
      Token token = ExpressionLexer.Next(state.Source, ref position, operators);
      if (token.Kind != TokenKind.RightParen)
      {
         while (true)
         {
            if (token.Kind != TokenKind.Identifier) { return false; }
            token = ExpressionLexer.Next(state.Source, ref position, operators);
            if (token.Kind == TokenKind.Comma)
            {
               token = ExpressionLexer.Next(state.Source, ref position, operators);
               continue;
            }
            break;
         }
         if (token.Kind != TokenKind.RightParen) { return false; }
      }
      token = ExpressionLexer.Next(state.Source, ref position, operators);
      return token.Kind == TokenKind.Arrow;
   }

   private SyntaxNode ParseParenthesizedLambda(ref ParseState state)
   {
      int start = state.Current.Start;
      Advance(ref state);
      List<IdentifierSyntax> parameters = new(4);
      if (state.Current.Kind != TokenKind.RightParen)
      {
         while (true)
         {
            Token parameter = state.Current;
            Advance(ref state);
            parameters.Add(Leaf(new IdentifierSyntax(state.Source.Substring(parameter.Start, parameter.Length), parameter.Start), in parameter));
            if (state.Current.Kind == TokenKind.Comma) { Advance(ref state); continue; }
            break;
         }
      }
      Expect(ref state, TokenKind.RightParen, "')' to close the parameter list");
      int arrowAt = state.Current.Start;
      Expect(ref state, TokenKind.Arrow, "'=>' after the parameter list");
      SyntaxNode body = ParseExpression(ref state, operators.LambdaPrecedence);
      return Bounded(new LambdaSyntax([.. parameters], body, arrowAt), start, in state);
   }

   private DynamicNode NumberValue(string source, in Token token)
   {
      if (decimalLiterals)
      {
         ReadOnlySpan<char> text = source.AsSpan(token.Start, token.Length);
         if (text.IndexOfAny('e', 'E') < 0 &&
             decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal exact))
         {
            return new DynamicNode(exact);
         }
      }
      return new DynamicNode(token.NumberValue);
   }

   private void Advance(ref ParseState state)
   {
      state.Previous = state.Current.Kind;
      state.LastEnd = state.Current.Start + state.Current.Length;
      state.Current = ExpressionLexer.Next(state.Source, ref state.Position, operators);
   }

   private Token Peek(string source, int position) => ExpressionLexer.Next(source, ref position, operators);

   private static bool Ends(TokenKind kind) => kind
      is TokenKind.End or TokenKind.Semicolon or TokenKind.Comma
      or TokenKind.RightBrace or TokenKind.RightParen or TokenKind.RightBracket;

   private const string LetKeyword = "let";
   private const string ForKeyword = "for";
   private const string IfKeyword = "if";
   private const string ElseKeyword = "else";
   private const string BreakKeyword = "break";
   private const string ContinueKeyword = "continue";

   private static bool IsWord(string source, in Token token, string word) =>
      token.Kind == TokenKind.Identifier &&
      token.Length == word.Length &&
      string.CompareOrdinal(source, token.Start, word, 0, word.Length) == 0;

   private static bool StartsStatement(string source, in Token token)
   {
      if (IsWord(source, in token, ElseKeyword)) { return false; }
      switch (token.Kind)
      {
         case TokenKind.Integer:
         case TokenKind.Number:
         case TokenKind.String:
         case TokenKind.Identifier:
         case TokenKind.True:
         case TokenKind.False:
         case TokenKind.Null:
         case TokenKind.LeftBrace:
            return true;
         case TokenKind.Operator:
            OpEntry op = token.Operator!;
            return op.PrefixPrecedence >= 0 && op.BinaryPrecedence < 0 && op.PostfixPrecedence < 0;
         default:
            return false;
      }
   }

   private T Bounded<T>(T node, int start, in ParseState state) where T : SyntaxNode =>
      Bounded(node, start, state.LastEnd, in state);

   private T Bounded<T>(T node, int start, int end, in ParseState state) where T : SyntaxNode
   {
      if (node.Height > maxDepth)
      {
         throw new SyntaxErrorException(
            $"Expression is nested too deeply (maximum depth is {maxDepth}); every operator in a chain is a level of its own, so a long run of \"||\" or \"+\" counts one for each",
            state.Source, node.At);
      }
      node.SetSpan(start, end);
      return node;
   }

   private static T Leaf<T>(T node, in Token token) where T : SyntaxNode
   {
      node.SetSpan(token.Start, token.Start + token.Length);
      return node;
   }

   private void Expect(ref ParseState state, TokenKind kind, string what)
   {
      if (state.Current.Kind != kind)
      {
         throw new SyntaxErrorException($"Expected {what} but found {ExpressionLexer.Describe(state.Source, in state.Current)}", state.Source, state.Current.Start);
      }
      Advance(ref state);
   }

   private static bool StartsOperand(in Token token)
   {
      switch (token.Kind)
      {
         case TokenKind.Integer:
         case TokenKind.Number:
         case TokenKind.String:
         case TokenKind.Identifier:
         case TokenKind.True:
         case TokenKind.False:
         case TokenKind.Null:
         case TokenKind.LeftParen:
         case TokenKind.LeftBrace:
            return true;
         case TokenKind.Operator:
            OpEntry op = token.Operator!;
            return op.PrefixPrecedence >= 0 && op.BinaryPrecedence < 0 && op.PostfixPrecedence < 0;
         default:
            return false;
      }
   }

   #endregion

   private struct ParseState
   {
      public readonly string Source;
      public int Position;
      public Token Current;
      public int Depth;

      public TokenKind Previous;

      public int LastEnd;

      public ParseState(string source)
      {
         Source = source;
         Position = 0;
         Current = default;
         Depth = 0;
         Previous = TokenKind.End;
         LastEnd = 0;
      }
   }
}
