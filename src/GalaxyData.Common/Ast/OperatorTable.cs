using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace GalaxyData.Common.Ast;

public enum OperatorAssociativity
{
   Left,

   Right,
}

public sealed class OperatorTable
{
   public const int MinPrecedence = 1;

   public const int MaxPrecedence = 1_000_000;

   private const string AllowedSymbolChars = "!#%&*+-./<=>?@^|~:";

   private readonly Dictionary<string, OperatorDef> operators = new(StringComparer.Ordinal);
   private int ternaryPrecedence = 20;
   private int callPrecedence = 190;
   private int indexPrecedence = 190;
   private int lambdaPrecedence = 15;

   public int TernaryPrecedence { get => ternaryPrecedence; set => ternaryPrecedence = ValidatePrecedence(value); }

   public int CallPrecedence { get => callPrecedence; set => callPrecedence = ValidatePrecedence(value); }

   public int IndexPrecedence { get => indexPrecedence; set => indexPrecedence = ValidatePrecedence(value); }

   public int LambdaPrecedence { get => lambdaPrecedence; set => lambdaPrecedence = ValidatePrecedence(value); }

   public static OperatorTable CreateDefault()
   {
      OperatorTable table = new();
      table.AddBinary("=", 15, OperatorAssociativity.Right);
      table.AddBinary(":=", 15, OperatorAssociativity.Right);
      table.AddBinary("??", 30, OperatorAssociativity.Right);
      table.AddBinary("||", 40);
      table.AddBinary("&&", 50);
      table.AddBinary("|", 60);
      table.AddBinary("^", 70);
      table.AddBinary("&", 80);
      table.AddBinary("==", 90).AddBinary("!=", 90);
      table.AddBinary("<", 100).AddBinary("<=", 100).AddBinary(">", 100).AddBinary(">=", 100);
      table.AddBinary("<<", 110).AddBinary(">>", 110);
      table.AddBinary("+", 120).AddBinary("-", 120);
      table.AddBinary("*", 130).AddBinary("/", 130).AddBinary("%", 130);
      table.AddPrefix("!", 150).AddPrefix("~", 150).AddPrefix("+", 150).AddPrefix("-", 150).AddPrefix("++", 150).AddPrefix("--", 150);
      table.AddBinary("**", 160, OperatorAssociativity.Right);
      table.AddPostfix("++", 175).AddPostfix("--", 175);
      table.AddPostfix(".*", 180);
      table.AddBinary(".", 200);
      return table;
   }

   #region Registration

   public OperatorTable AddBinary(string symbol, int precedence, OperatorAssociativity associativity = OperatorAssociativity.Left)
   {
      ValidateSymbol(symbol);
      ValidatePrecedence(precedence);
      OperatorDef def = GetOrNew(symbol);
      def.BinaryPrecedence = precedence;
      def.BinaryRightAssociative = associativity == OperatorAssociativity.Right;
      operators[symbol] = def;
      return this;
   }

   public OperatorTable AddPrefix(string symbol, int precedence)
   {
      ValidateSymbol(symbol);
      ValidatePrecedence(precedence);
      OperatorDef def = GetOrNew(symbol);
      def.PrefixPrecedence = precedence;
      operators[symbol] = def;
      return this;
   }

   public OperatorTable AddPostfix(string symbol, int precedence)
   {
      ValidateSymbol(symbol);
      ValidatePrecedence(precedence);
      OperatorDef def = GetOrNew(symbol);
      def.PostfixPrecedence = precedence;
      operators[symbol] = def;
      return this;
   }

   public bool RemoveBinary(string symbol)
   {
      if (symbol != null && operators.TryGetValue(symbol, out OperatorDef def) && def.BinaryPrecedence >= 0)
      {
         def.BinaryPrecedence = -1;
         def.BinaryRightAssociative = false;
         if (def.IsEmpty) { operators.Remove(symbol); } else { operators[symbol] = def; }
         return true;
      }
      return false;
   }

   public bool RemovePrefix(string symbol)
   {
      if (symbol != null && operators.TryGetValue(symbol, out OperatorDef def) && def.PrefixPrecedence >= 0)
      {
         def.PrefixPrecedence = -1;
         if (def.IsEmpty) { operators.Remove(symbol); } else { operators[symbol] = def; }
         return true;
      }
      return false;
   }

   public bool RemovePostfix(string symbol)
   {
      if (symbol != null && operators.TryGetValue(symbol, out OperatorDef def) && def.PostfixPrecedence >= 0)
      {
         def.PostfixPrecedence = -1;
         if (def.IsEmpty) { operators.Remove(symbol); } else { operators[symbol] = def; }
         return true;
      }
      return false;
   }

   public OperatorTable Clone()
   {
      OperatorTable clone = new();
      foreach (KeyValuePair<string, OperatorDef> pair in operators) { clone.operators[pair.Key] = pair.Value; }
      clone.ternaryPrecedence = ternaryPrecedence;
      clone.callPrecedence = callPrecedence;
      clone.indexPrecedence = indexPrecedence;
      clone.lambdaPrecedence = lambdaPrecedence;
      return clone;
   }

   #endregion

   #region Compilation

   internal CompiledOperatorTable Compile()
   {
      List<OpEntry>?[] buckets = new List<OpEntry>?[128];
      Dictionary<string, OpEntry>? wordOperators = null;
      foreach (KeyValuePair<string, OperatorDef> pair in operators)
      {
         OperatorDef def = pair.Value;
         OpEntry entry = new(pair.Key, def.BinaryPrecedence, def.BinaryRightAssociative, def.PrefixPrecedence, def.PostfixPrecedence);
         if (AllowedSymbolChars.Contains(pair.Key[0]))
         {
            (buckets[pair.Key[0]] ??= new List<OpEntry>(4)).Add(entry);
         }
         else
         {
            (wordOperators ??= new Dictionary<string, OpEntry>(StringComparer.Ordinal))[pair.Key] = entry;
         }
      }
      OpEntry[]?[] byFirstChar = new OpEntry[]?[128];
      for (int i = 0; i < buckets.Length; i++)
      {
         List<OpEntry>? bucket = buckets[i];
         if (bucket == null) { continue; }
         bucket.Sort(static (a, b) => b.Symbol.Length - a.Symbol.Length);
         byFirstChar[i] = bucket.ToArray();
      }
      return new CompiledOperatorTable(byFirstChar, wordOperators, ternaryPrecedence, callPrecedence, indexPrecedence, lambdaPrecedence);
   }

   private OperatorDef GetOrNew(string symbol)
   {
      return operators.TryGetValue(symbol, out OperatorDef def) ? def : OperatorDef.Empty;
   }

   private static void ValidateSymbol(string symbol)
   {
      ArgumentException.ThrowIfNullOrEmpty(symbol);
      char first = symbol[0];
      if (AllowedSymbolChars.Contains(first))
      {
         if (symbol == "?" || symbol == ":")
         {
            throw new ArgumentException($"The symbol '{symbol}' by itself is reserved for the ternary conditional operator.", nameof(symbol));
         }
         if (symbol == "::")
         {
            throw new ArgumentException("The symbol '::' is reserved for qualified names, which the lexer reads as one identifier.", nameof(symbol));
         }
         if (symbol.StartsWith("=>", StringComparison.Ordinal))
         {
            throw new ArgumentException("Operator symbols may not start with \"=>\", which is reserved for lambda expressions.", nameof(symbol));
         }
         foreach (char c in symbol)
         {
            if (!AllowedSymbolChars.Contains(c))
            {
               throw new ArgumentException($"Symbolic operators may only use the characters \"{AllowedSymbolChars}\" but found '{c}'.", nameof(symbol));
            }
         }
      }
      else if (char.IsLetter(first) || first == '_' || first == '$')
      {
         if (symbol is "true" or "false" or "null")
         {
            throw new ArgumentException($"The word '{symbol}' is a reserved literal keyword and cannot be an operator.", nameof(symbol));
         }
         foreach (char c in symbol)
         {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$'))
            {
               throw new ArgumentException($"Word operators may only use letters, digits, '_' and '$' but found '{c}'.", nameof(symbol));
            }
         }
      }
      else
      {
         throw new ArgumentException($"Operator symbols must be made of the characters \"{AllowedSymbolChars}\" or be a word of letters, digits, '_' and '$'.", nameof(symbol));
      }
   }

   private static int ValidatePrecedence(int precedence)
   {
      ArgumentOutOfRangeException.ThrowIfLessThan(precedence, MinPrecedence);
      ArgumentOutOfRangeException.ThrowIfGreaterThan(precedence, MaxPrecedence);
      return precedence;
   }

   #endregion

   private struct OperatorDef
   {
      public static readonly OperatorDef Empty = new() { BinaryPrecedence = -1, PrefixPrecedence = -1, PostfixPrecedence = -1 };

      public int BinaryPrecedence;
      public bool BinaryRightAssociative;
      public int PrefixPrecedence;
      public int PostfixPrecedence;

      public readonly bool IsEmpty => BinaryPrecedence < 0 && PrefixPrecedence < 0 && PostfixPrecedence < 0;
   }
}

internal sealed class OpEntry
{
   public readonly string Symbol;
   public readonly int BinaryPrecedence;
   public readonly bool BinaryRightAssociative;
   public readonly int PrefixPrecedence;
   public readonly int PostfixPrecedence;

   public OpEntry(string symbol, int binaryPrecedence, bool binaryRightAssociative, int prefixPrecedence, int postfixPrecedence)
   {
      Symbol = symbol;
      BinaryPrecedence = binaryPrecedence;
      BinaryRightAssociative = binaryRightAssociative;
      PrefixPrecedence = prefixPrecedence;
      PostfixPrecedence = postfixPrecedence;
   }
}

internal sealed class CompiledOperatorTable
{
   public readonly OpEntry[]?[] ByFirstChar;
   public readonly int TernaryPrecedence;
   public readonly int CallPrecedence;
   public readonly int IndexPrecedence;
   public readonly int LambdaPrecedence;

   private readonly Dictionary<string, OpEntry>.AlternateLookup<ReadOnlySpan<char>> wordOperators;
   private readonly bool hasWordOperators;

   public CompiledOperatorTable(OpEntry[]?[] byFirstChar, Dictionary<string, OpEntry>? wordOperators,
                                int ternaryPrecedence, int callPrecedence, int indexPrecedence, int lambdaPrecedence)
   {
      ByFirstChar = byFirstChar;
      TernaryPrecedence = ternaryPrecedence;
      CallPrecedence = callPrecedence;
      IndexPrecedence = indexPrecedence;
      LambdaPrecedence = lambdaPrecedence;
      if (wordOperators != null)
      {
         this.wordOperators = wordOperators.GetAlternateLookup<ReadOnlySpan<char>>();
         hasWordOperators = true;
      }
   }

   public bool TryGetWordOperator(ReadOnlySpan<char> word, [NotNullWhen(true)] out OpEntry? entry)
   {
      if (hasWordOperators) { return wordOperators.TryGetValue(word, out entry!); }
      entry = null;
      return false;
   }
}
