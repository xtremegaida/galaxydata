using System;
using System.Collections.Generic;
using System.Text;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Sql;

/// <summary>Generated SQL and the parameters it takes, in the order they were first used.</summary>
public sealed class SqlStatement
{
   internal SqlStatement(string text, IReadOnlyList<SqlParameterSlot> parameters)
   {
      Text = text;
      Parameters = parameters;
   }

   public string Text { get; }

   public IReadOnlyList<SqlParameterSlot> Parameters { get; }

   public override string ToString()
   {
      StringBuilder text = new(Text);
      foreach (SqlParameterSlot parameter in Parameters)
      {
         text.AppendLine().Append("-- ").Append(parameter.Name).Append(' ').Append(parameter.Type).Append(" = ").Append(parameter.Description);
      }
      return text.ToString();
   }
}

public enum PatternStyle : byte
{
   /// <summary>LIKE with <c>\</c> as the escape character: <c>%</c>, <c>_</c> and <c>\</c> are escaped.</summary>
   Like,

   /// <summary>SQL Server LIKE: <c>[</c> is escaped too, since it opens a character class.</summary>
   LikeWithBrackets,

   /// <summary>SQLite GLOB: <c>*</c>, <c>?</c> and <c>[</c> become one-character classes.</summary>
   Glob,
}

public enum PatternShape : byte
{
   Prefix,
   Suffix,
   Contains,

   /// <summary>
   /// The text is a LIKE pattern already, as the language writes them (<c>%</c>, <c>_</c>, and <c>\</c> escaping the
   /// next character): written in the style's syntax, SQL Server's <c>[</c> escaped, or as a GLOB pattern.
   /// </summary>
   AsWritten,
}

/// <summary>Turns text into a pattern that matches it literally: startsWith(name, 'a_b') becomes <c>LIKE 'a\_b%'</c>.</summary>
public sealed record PatternTransform(PatternStyle Style, PatternShape Shape)
{
   public string Apply(string text)
   {
      ArgumentNullException.ThrowIfNull(text);
      if (Shape == PatternShape.AsWritten) { return Translate(text); }
      StringBuilder pattern = new(text.Length + 4);
      string any = Style == PatternStyle.Glob ? "*" : "%";
      if (Shape != PatternShape.Prefix) { pattern.Append(any); }
      foreach (char c in text)
      {
         switch (Style)
         {
            case PatternStyle.Glob when c is '*' or '?' or '[':
               pattern.Append('[').Append(c).Append(']');
               break;
            case PatternStyle.Like or PatternStyle.LikeWithBrackets when c is '%' or '_' or '\\':
            case PatternStyle.LikeWithBrackets when c == '[':
               pattern.Append('\\').Append(c);
               break;
            default:
               pattern.Append(c);
               break;
         }
      }
      if (Shape != PatternShape.Suffix) { pattern.Append(any); }
      return pattern.ToString();
   }

   /// <summary>A LIKE pattern of the language in the style's syntax.</summary>
   private string Translate(string like)
   {
      StringBuilder pattern = new(like.Length + 4);
      for (int i = 0; i < like.Length; i++)
      {
         char c = like[i];
         bool escaped = c == '\\' && i + 1 < like.Length;
         if (escaped) { c = like[++i]; }
         if (Style == PatternStyle.Glob)
         {
            if (!escaped && c == '%') { pattern.Append('*'); }
            else if (!escaped && c == '_') { pattern.Append('?'); }
            else if (c is '*' or '?' or '[') { pattern.Append('[').Append(c).Append(']'); }
            else { pattern.Append(c); }
         }
         else if (escaped) { pattern.Append('\\').Append(c); }
         else if (Style == PatternStyle.LikeWithBrackets && c == '[') { pattern.Append("\\["); }
         else { pattern.Append(c); }
      }
      return pattern.ToString();
   }

   public override string ToString() =>
      Shape == PatternShape.AsWritten ? $"{(Style == PatternStyle.Glob ? "glob" : "like")} pattern" : $"{Shape.ToString().ToLowerInvariant()} {(Style == PatternStyle.Glob ? "glob" : "like")} pattern";
}

/// <summary>
/// A parameter of generated SQL: a constant from the query text, a <c>$name</c> parameter, or a value fixed when
/// the query runs (<c>now()</c>). Its value is worked out at execution, converted to <see cref="Type"/>.
/// </summary>
public sealed class SqlParameterSlot
{
   internal SqlParameterSlot(string name, ScalarType type, ParameterSource? source, object? constant, string? parameterName, PatternTransform? pattern)
   {
      Name = name;
      Type = type;
      Source = source;
      Constant = constant;
      ParameterName = parameterName;
      Pattern = pattern;
   }

   /// <summary>The name in the SQL text, without the dialect's prefix: <c>p0</c>.</summary>
   public string Name { get; }

   public ScalarType Type { get; }

   /// <summary>Null for a constant from the query text.</summary>
   public ParameterSource? Source { get; }

   public object? Constant { get; }

   /// <summary>The <c>$name</c> parameter's name, without the <c>$</c>; or the name of a runtime value.</summary>
   public string? ParameterName { get; }

   /// <summary>Set when the value is text that becomes a match pattern.</summary>
   public PatternTransform? Pattern { get; }

   /// <summary>
   /// Whether the SQL compares the value with a column (<c>c.status = @p0</c>, <c>c.id IN (@p0, @p1)</c>), so the
   /// database may take its type from the column's: PostgreSQL compares text sent untyped as an enum, a char(n) or
   /// citext, which text doesn't compare with, or not as they do.
   /// </summary>
   public bool ComparedWithColumn { get; internal set; }

   /// <summary>
   /// The type of the table column the value is compared with, as its database declares it, when known: SQL Server
   /// compares a <c>datetime</c> only with a <c>datetime</c> exactly.
   /// </summary>
   public string? ColumnType { get; internal set; }

   /// <summary>What the value is, in query syntax: <c>'open'</c>, <c>$since</c>, <c>now()</c>.</summary>
   public string Description
   {
      get
      {
         string value = Source switch
         {
            null => BoundTreePrinter.Literal(Constant),
            ParameterSource.User => "$" + ParameterName,
            ParameterSource.Runtime => ParameterName!,
            ParameterSource source => source.ToString().ToLowerInvariant() + "()",
         };
         return Pattern == null ? value : $"{Pattern} of {value}";
      }
   }

   public override string ToString() => $"{Name} = {Description}";
}
