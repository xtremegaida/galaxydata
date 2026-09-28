using System;
using System.Collections.Generic;
using System.Text;
using GalaxyData.Query.Diagnostics;

namespace GalaxyData.Testing;

/// <summary>Builds readable golden reports: each case's text, then its output, with diagnostics underlined in the source.</summary>
internal sealed class QueryReport
{
   private readonly StringBuilder text = new();

   public QueryReport Case(string query, string output)
   {
      text.Append("### ").AppendLine(query.ReplaceLineEndings(Environment.NewLine + "    "));
      text.AppendLine(output.TrimEnd());
      text.AppendLine();
      return this;
   }

   public static string Diagnostics(string query, IEnumerable<QueryDiagnostic> diagnostics)
   {
      StringBuilder output = new();
      foreach (QueryDiagnostic diagnostic in diagnostics)
      {
         output.Append(diagnostic.Code).Append(' ').Append(diagnostic.Severity).Append(": ").AppendLine(diagnostic.Message);
         output.Append("  ").AppendLine(query);
         int start = Math.Clamp(diagnostic.Start, 0, query.Length);
         int length = Math.Max(1, Math.Min(diagnostic.End, query.Length) - start);
         output.Append("  ").Append(' ', start).Append('^', length).AppendLine();
      }
      return output.ToString();
   }

   public override string ToString() => text.ToString();
}
