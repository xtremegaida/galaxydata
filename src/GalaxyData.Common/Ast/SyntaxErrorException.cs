using System;
using System.Text;

namespace GalaxyData.Common.Ast;

public sealed class SyntaxErrorException : Exception
{
   private const int SnippetWidth = 60;

   public int Index { get; }

   public string Expression { get; }

   public string Description { get; }

   internal SyntaxErrorException(string description, string expression, int index)
      : base(FormatMessage(description, expression, index))
   {
      Description = description;
      Expression = expression;
      Index = index;
   }

   public static string FormatMessage(string description, string expression, int index)
   {
      StringBuilder text = new(description.Length + SnippetWidth + 48);
      text.Append(description).Append(" at index ").Append(index).Append('.');
      if (expression.Length > 0)
      {
         int start = index > 40 ? index - 30 : 0;
         int end = Math.Min(expression.Length, start + SnippetWidth);
         text.AppendLine().AppendLine();
         text.Append("   ");
         int caretColumn = 3 + (index - start);
         if (start > 0) { text.Append("..."); caretColumn += 3; }
         for (int i = start; i < end; i++)
         {
            char c = expression[i];
            text.Append(c < ' ' ? ' ' : c);
         }
         if (end < expression.Length) { text.Append("..."); }
         text.AppendLine();
         text.Append(' ', caretColumn).Append('^');
      }
      return text.ToString();
   }
}
