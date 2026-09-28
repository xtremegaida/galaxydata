using System;
using System.Globalization;
using System.Text;

namespace GalaxyData.Common.Ast;

internal enum TokenKind : byte
{
   End,
   Integer,
   Number,
   String,
   Identifier,
   True,
   False,
   Null,
   Operator,
   LeftParen,
   RightParen,
   LeftBracket,
   RightBracket,
   Comma,
   Question,
   Colon,
   Semicolon,
   LeftBrace,
   RightBrace,
   Arrow,
}

internal struct Token
{
   public TokenKind Kind;
   public int Start;
   public int Length;
   public long IntegerValue;
   public double NumberValue;
   public bool StringHasEscapes;
   public bool IsRawBlock;
   public int StringLength;
   public OpEntry? Operator;
}

internal static class ExpressionLexer
{
   public static Token Next(string source, ref int position, CompiledOperatorTable operators)
   {
      int length = source.Length;
      SkipIgnored(source, ref position);
      if (position >= length)
      {
         return new Token { Kind = TokenKind.End, Start = length };
      }
      char c = source[position];
      if (c >= '0' && c <= '9') { return LexNumber(source, ref position); }
      if (c == '"' || c == '\'') { return LexString(source, ref position); }
      if (c == '`') { return LexRaw(source, ref position); }
      if (char.IsLetter(c) || c == '_' || c == '$') { return LexIdentifier(source, ref position, operators); }
      switch (c)
      {
         case '(': return Structural(TokenKind.LeftParen, ref position);
         case ')': return Structural(TokenKind.RightParen, ref position);
         case '[': return Structural(TokenKind.LeftBracket, ref position);
         case ']': return Structural(TokenKind.RightBracket, ref position);
         case ',': return Structural(TokenKind.Comma, ref position);
         case ';': return Structural(TokenKind.Semicolon, ref position);
         case '{': return Structural(TokenKind.LeftBrace, ref position);
         case '}': return Structural(TokenKind.RightBrace, ref position);
      }
      if (c == '=' && position + 1 < length && source[position + 1] == '>')
      {
         Token arrow = new() { Kind = TokenKind.Arrow, Start = position, Length = 2 };
         position += 2;
         return arrow;
      }
      OpEntry? op = MatchOperator(source, position, operators);
      if (op != null)
      {
         Token token = new() { Kind = TokenKind.Operator, Start = position, Length = op.Symbol.Length, Operator = op };
         position += op.Symbol.Length;
         return token;
      }
      if (c == '?') { return Structural(TokenKind.Question, ref position); }
      if (c == ':') { return Structural(TokenKind.Colon, ref position); }
      throw new SyntaxErrorException($"Unexpected character '{c}'", source, position);
   }

   public static void SkipIgnored(string source, ref int position)
   {
      int length = source.Length;
      while (position < length)
      {
         char c = source[position];
         if (char.IsWhiteSpace(c)) { position++; continue; }

         if (c == '#' || (c == '/' && position + 1 < length && source[position + 1] == '/'))
         {
            while (position < length && source[position] != '\n' && source[position] != '\r') { position++; }
            continue;
         }

         if (c == '/' && position + 1 < length && source[position + 1] == '*')
         {
            int start = position;
            int close = source.IndexOf("*/", position + 2, StringComparison.Ordinal);
            if (close < 0) { throw new SyntaxErrorException("Unterminated comment", source, start); }
            position = close + 2;
            continue;
         }

         return;
      }
   }

   public static string GetStringValue(string source, in Token token)
   {
      int contentStart = token.Start + 1;
      int contentLength = token.Length - 2;
      if (token.IsRawBlock) { return RawBlock(source, contentStart, contentLength); }
      if (!token.StringHasEscapes)
      {
         return contentLength == 0 ? string.Empty : source.Substring(contentStart, contentLength);
      }
      return string.Create(token.StringLength, (source, contentStart, contentLength), static (buffer, state) =>
      {
         (string text, int read, int length) = state;
         int end = read + length;
         int write = 0;
         while (read < end)
         {
            char c = text[read++];
            if (c != '\\') { buffer[write++] = c; continue; }
            char escape = text[read++];
            switch (escape)
            {
               case 'n': buffer[write++] = '\n'; break;
               case 'r': buffer[write++] = '\r'; break;
               case 't': buffer[write++] = '\t'; break;
               case 'b': buffer[write++] = '\b'; break;
               case 'f': buffer[write++] = '\f'; break;
               case '0': buffer[write++] = '\0'; break;
               case 'u':
                  buffer[write++] = (char)((ToHex(text[read]) << 12) | (ToHex(text[read + 1]) << 8) |
                                           (ToHex(text[read + 2]) << 4) | ToHex(text[read + 3]));
                  read += 4;
                  break;
               default: buffer[write++] = escape; break;
            }
         }
      });
   }

   public static string Describe(string source, in Token token)
   {
      return token.Kind == TokenKind.End ? "the end of the expression" : $"'{source.Substring(token.Start, token.Length)}'";
   }

   private static Token Structural(TokenKind kind, ref int position)
   {
      Token token = new() { Kind = kind, Start = position, Length = 1 };
      position++;
      return token;
   }

   private static Token LexNumber(string source, ref int position)
   {
      int start = position;
      int length = source.Length;
      if (source[position] == '0' && position + 1 < length && (source[position + 1] | 0x20) == 'x')
      {
         return LexHex(source, ref position);
      }
      bool isFloat = false;
      while (position < length && source[position] >= '0' && source[position] <= '9') { position++; }
      if (position + 1 < length && source[position] == '.' && source[position + 1] >= '0' && source[position + 1] <= '9')
      {
         isFloat = true;
         position += 2;
         while (position < length && source[position] >= '0' && source[position] <= '9') { position++; }
      }
      if (position < length && (source[position] == 'e' || source[position] == 'E'))
      {
         int exponent = position + 1;
         if (exponent < length && (source[exponent] == '+' || source[exponent] == '-')) { exponent++; }
         if (exponent < length && source[exponent] >= '0' && source[exponent] <= '9')
         {
            isFloat = true;
            position = exponent + 1;
            while (position < length && source[position] >= '0' && source[position] <= '9') { position++; }
         }
      }
      if (position < length && (char.IsLetter(source[position]) || source[position] == '_' || source[position] == '$'))
      {
         throw new SyntaxErrorException("Invalid numeric literal", source, start);
      }
      ReadOnlySpan<char> span = source.AsSpan(start, position - start);
      if (!isFloat && long.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out long integer))
      {
         return new Token { Kind = TokenKind.Integer, Start = start, Length = position - start, IntegerValue = integer };
      }
      double number = double.Parse(span, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
      return new Token { Kind = TokenKind.Number, Start = start, Length = position - start, NumberValue = number };
   }

   private static Token LexHex(string source, ref int position)
   {
      int start = position;
      int length = source.Length;
      position += 2;
      int firstDigit = position;
      uint value = 0;
      while (position < length && TryHexDigit(source[position], out int digit))
      {
         value = (value << 4) | (uint)digit;
         position++;
         if (position - firstDigit > 8)
         {
            throw new SyntaxErrorException("A hexadecimal literal holds at most 8 digits", source, start);
         }
      }
      if (position == firstDigit)
      {
         throw new SyntaxErrorException("A hexadecimal literal needs at least one digit", source, start);
      }
      if (position < length && (char.IsLetter(source[position]) || source[position] == '_' || source[position] == '$'))
      {
         throw new SyntaxErrorException("Invalid numeric literal", source, start);
      }
      return new Token
      {
         Kind = TokenKind.Integer,
         Start = start,
         Length = position - start,
         IntegerValue = unchecked((int)value)
      };
   }

   private static Token LexRaw(string source, ref int position)
   {
      int start = position;
      int length = source.Length;
      position++;
      bool block = false;
      while (true)
      {
         if (position >= length) { throw new SyntaxErrorException("Unterminated raw string literal", source, start); }
         char c = source[position];
         if (c == '`') { position++; break; }
         if (c == '\n') { block = true; }
         position++;
      }
      int contentLength = position - start - 2;
      if (block) { CheckRawBlock(source, start + 1, contentLength, start); }
      return new Token
      {
         Kind = TokenKind.String,
         Start = start,
         Length = position - start,
         StringHasEscapes = false,
         IsRawBlock = block,
         StringLength = contentLength,
      };
   }

   private static string RawBlock(string source, int start, int length)
   {
      int end = start + length;
      int first = source.IndexOf('\n', start, length);
      int last = source.LastIndexOf('\n', end - 1, length);
      if (last <= first) { return string.Empty; }

      int indent = Indent(source, last + 1, end);
      StringBuilder result = new(length);
      int at = first + 1;
      while (at <= last)
      {
         int stop = source.IndexOf('\n', at, last - at + 1);
         int lineEnd = stop < 0 ? last : stop;
         int text = lineEnd > at && source[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
         if (result.Length > 0 || at > first + 1) { result.Append('\n'); }
         if (!IsBlank(source, at, text)) { result.Append(source, at + indent, text - at - indent); }
         at = lineEnd + 1;
      }
      return result.ToString();
   }

   private static void CheckRawBlock(string source, int start, int length, int at)
   {
      int end = start + length;
      int first = source.IndexOf('\n', start, length);
      if (!IsBlank(source, start, Trim(source, first)))
      {
         throw new SyntaxErrorException(
            "A raw string that spans lines has nothing on the line it opens on; its text starts on the next one",
            source, at);
      }

      int last = source.LastIndexOf('\n', end - 1, length);
      if (!IsBlank(source, last + 1, end))
      {
         throw new SyntaxErrorException(
            "The backtick closing a raw string that spans lines is on a line of its own, and its indentation is what comes off every line above it",
            source, at);
      }
      if (last <= first) { return; }

      int indent = Indent(source, last + 1, end);
      int line = first + 1;
      while (line <= last)
      {
         int stop = source.IndexOf('\n', line, last - line + 1);
         int lineEnd = stop < 0 ? last : stop;
         int text = Trim(source, lineEnd);
         if (!IsBlank(source, line, text) && (text - line < indent || Indent(source, line, text) < indent))
         {
            throw new SyntaxErrorException(
               "This line is indented less than the backtick that closes the string, so there is no telling what to take off it",
               source, line);
         }
         line = lineEnd + 1;
      }
   }

   private static int Trim(string source, int lineEnd) =>
      lineEnd > 0 && source[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;

   private static bool IsBlank(string source, int from, int to)
   {
      for (int i = from; i < to; i++)
      {
         if (!char.IsWhiteSpace(source[i])) { return false; }
      }
      return true;
   }

   private static int Indent(string source, int from, int to)
   {
      int count = 0;
      while (from + count < to && (source[from + count] == ' ' || source[from + count] == '\t')) { count++; }
      return count;
   }

   private static Token LexString(string source, ref int position)
   {
      int start = position;
      char quote = source[position];
      int length = source.Length;
      bool hasEscapes = false;
      int contentLength = 0;
      position++;
      while (true)
      {
         if (position >= length) { throw new SyntaxErrorException("Unterminated string literal", source, start); }
         char c = source[position];
         if (c == quote) { position++; break; }
         if (c == '\n' || c == '\r') { throw new SyntaxErrorException("Unterminated string literal", source, start); }
         if (c == '\\')
         {
            hasEscapes = true;
            if (position + 1 >= length) { throw new SyntaxErrorException("Unterminated string literal", source, start); }
            char escape = source[position + 1];
            switch (escape)
            {
               case '"':
               case '\'':
               case '\\':
               case '/':
               case 'n':
               case 'r':
               case 't':
               case 'b':
               case 'f':
               case '0':
                  position += 2;
                  break;
               case 'u':
                  if (position + 5 >= length || !IsHex4(source, position + 2))
                  {
                     throw new SyntaxErrorException("Invalid Unicode escape sequence", source, position);
                  }
                  position += 6;
                  break;
               default:
                  throw new SyntaxErrorException($"Invalid escape sequence '\\{escape}'", source, position);
            }
            contentLength++;
         }
         else
         {
            position++;
            contentLength++;
         }
      }
      return new Token { Kind = TokenKind.String, Start = start, Length = position - start, StringHasEscapes = hasEscapes, StringLength = contentLength };
   }

   private static Token LexIdentifier(string source, ref int position, CompiledOperatorTable operators)
   {
      int start = position;
      int length = source.Length;
      position++;
      Body(source, ref position);

      if (position + 2 < length && source[position] == ':' && source[position + 1] == ':' &&
          IsNameStart(source[position + 2]))
      {
         position += 3;
         Body(source, ref position);
      }

      int tokenLength = position - start;
      ReadOnlySpan<char> span = source.AsSpan(start, tokenLength);
      TokenKind kind = TokenKind.Identifier;
      if (tokenLength == 4)
      {
         if (span.SequenceEqual("true")) { kind = TokenKind.True; }
         else if (span.SequenceEqual("null")) { kind = TokenKind.Null; }
      }
      else if (tokenLength == 5 && span.SequenceEqual("false")) { kind = TokenKind.False; }
      if (kind == TokenKind.Identifier && operators.TryGetWordOperator(span, out OpEntry? wordOperator))
      {
         return new Token { Kind = TokenKind.Operator, Start = start, Length = tokenLength, Operator = wordOperator };
      }
      return new Token { Kind = kind, Start = start, Length = tokenLength };
   }

   private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_' || c == '$';

   private static void Body(string source, ref int position)
   {
      int length = source.Length;
      while (position < length)
      {
         char c = source[position];
         if (char.IsLetterOrDigit(c) || c == '_' || c == '$') { position++; } else { break; }
      }
   }

   private static OpEntry? MatchOperator(string source, int position, CompiledOperatorTable operators)
   {
      char c = source[position];
      if (c >= 128) { return null; }
      OpEntry[]? bucket = operators.ByFirstChar[c];
      if (bucket == null) { return null; }
      ReadOnlySpan<char> remaining = source.AsSpan(position);
      for (int i = 0; i < bucket.Length; i++)
      {
         if (remaining.StartsWith(bucket[i].Symbol)) { return bucket[i]; }
      }
      return null;
   }

   private static bool IsHex4(string source, int index)
   {
      for (int i = 0; i < 4; i++)
      {
         if (!TryHexDigit(source[index + i], out _)) { return false; }
      }
      return true;
   }

   private static int ToHex(char c)
   {
      TryHexDigit(c, out int digit);
      return digit;
   }

   private static bool TryHexDigit(char c, out int digit)
   {
      if (c >= '0' && c <= '9') { digit = c - '0'; return true; }
      int lower = c | 0x20;
      if (lower >= 'a' && lower <= 'f') { digit = lower - 'a' + 10; return true; }
      digit = 0;
      return false;
   }
}
