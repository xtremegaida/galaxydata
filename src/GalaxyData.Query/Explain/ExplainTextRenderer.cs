using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Results;

namespace GalaxyData.Query.Explain;

/// <summary>Writes an explain as text for a terminal: summary, plan tree, columns, SQL, and the phases when verbose.</summary>
public static class ExplainTextRenderer
{
   public static string Render(QueryExplain explain)
   {
      ArgumentNullException.ThrowIfNull(explain);
      StringBuilder text = new();
      text.AppendLine(explain.Summary);
      foreach (QueryDiagnostic diagnostic in explain.Diagnostics) { Diagnostic(text, explain.Text, diagnostic); }
      if (explain.Plan != null)
      {
         text.AppendLine().AppendLine("Plan");
         bool sites = Sites(explain.Plan).Distinct().Count() > 1;
         Node(text, explain.Plan, 1, sites);
      }
      if (explain.Schema != null)
      {
         text.AppendLine().AppendLine("Columns");
         Columns(text, explain.Schema);
      }
      foreach (ExplainFragment fragment in explain.Fragments)
      {
         text.AppendLine().Append("SQL for ").Append(fragment.Source).Append(" (").Append(fragment.Dialect).Append(", ").Append(fragment.Strategy).AppendLine(")");
         foreach (string line in Lines(fragment.Sql)) { text.Append("  ").AppendLine(line); }
         foreach (ExplainParameter parameter in fragment.Parameters)
         {
            text.Append("  -- ").Append(parameter.Name).Append(' ').Append(parameter.Type).Append(" = ").AppendLine(parameter.Value);
         }
         if (fragment.BindJoinTemplate != null)
         {
            text.AppendLine("  -- for each batch of keys:");
            foreach (string line in Lines(fragment.BindJoinTemplate)) { text.Append("  ").AppendLine(line); }
            foreach (ExplainParameter parameter in fragment.BindJoinParameters)
            {
               text.Append("  -- ").Append(parameter.Name).Append(' ').Append(parameter.Type).Append(" = ").AppendLine(parameter.Value);
            }
         }
      }
      if (explain.MergeSql != null)
      {
         text.AppendLine().AppendLine("Merge SQL");
         foreach (string line in Lines(explain.MergeSql)) { text.Append("  ").AppendLine(line); }
         foreach (ExplainParameter parameter in explain.MergeParameters)
         {
            text.Append("  -- ").Append(parameter.Name).Append(' ').Append(parameter.Type).Append(" = ").AppendLine(parameter.Value);
         }
      }
      foreach (ExplainPhase phase in explain.Phases ?? [])
      {
         text.AppendLine().Append("Phase ").Append(phase.Name);
         if (phase.Rules.Count > 0) { text.Append(": ").AppendJoin(", ", Counted(phase.Rules)); }
         text.AppendLine();
         foreach (string line in Lines(phase.Plan)) { text.Append("  ").AppendLine(line); }
      }
      return text.ToString();
   }

   /// <summary>A diagnostic with the line of the query it is on, underlined.</summary>
   private static void Diagnostic(StringBuilder text, string query, QueryDiagnostic diagnostic)
   {
      text.Append(diagnostic.IsError ? "error " : "warning ").Append(diagnostic.Code).Append(": ").AppendLine(diagnostic.Message);
      int start = Math.Clamp(diagnostic.Start, 0, query.Length);
      int lineStart = start == 0 ? 0 : query.LastIndexOf('\n', start - 1) + 1;
      int lineEnd = query.IndexOf('\n', start);
      string line = query[lineStart..(lineEnd < 0 ? query.Length : lineEnd)].TrimEnd('\r');
      int length = Math.Max(1, Math.Min(diagnostic.End, lineStart + line.Length) - start);
      text.Append("  ").AppendLine(line);
      text.Append("  ").Append(' ', start - lineStart).Append('^', length).AppendLine();
   }

   private static IEnumerable<string?> Sites(ExplainNode node) =>
      new[] { node.Site }.Concat(node.Inputs.SelectMany(Sites)).Concat(node.Subqueries.SelectMany(s => Sites(s.Plan)));

   private static void Node(StringBuilder text, ExplainNode node, int depth, bool sites)
   {
      text.Append(' ', depth * 2).Append(node.Operator);
      if (!string.IsNullOrEmpty(node.Detail)) { text.Append("  ").Append(node.Detail); }
      if (sites && node.Site != null) { text.Append("  @").Append(node.Site); }
      if (node.EstimatedRows is { } rows) { text.Append("  (~").Append(rows.ToString("N0", CultureInfo.InvariantCulture)).Append(rows == 1 ? " row)" : " rows)"); }
      text.AppendLine();
      foreach (ExplainSubquery subquery in node.Subqueries)
      {
         text.Append(' ', depth * 2 + 4).Append("subquery ").Append(subquery.Number.ToString(CultureInfo.InvariantCulture)).Append(" (").Append(subquery.Kind).AppendLine(")");
         Node(text, subquery.Plan, depth + 3, sites);
      }
      foreach (ExplainNode input in node.Inputs) { Node(text, input, depth + 1, sites); }
   }

   /// <summary>A result's columns, one line each: ordinal, name, type, lineage, edit target and link; then the row identity.</summary>
   public static string RenderColumns(ResultSchema schema)
   {
      ArgumentNullException.ThrowIfNull(schema);
      StringBuilder text = new();
      Columns(text, schema);
      return text.ToString();
   }

   private static void Columns(StringBuilder text, ResultSchema schema)
   {
      foreach (ResultColumn column in schema.Columns)
      {
         text.Append("  ").Append(column.Ordinal.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(column.Name).Append(' ').Append(column.Type);
         if (column.IsHidden) { text.Append(" (hidden)"); }
         text.Append(": ").Append(column.Lineage);
         if (column.EditTarget != null) { text.Append("; edits ").Append(column.EditTarget); }
         if (column.Link != null) { text.Append("; links to ").Append(column.Link); }
         text.AppendLine();
      }
      if (schema.RowIdentity is { } identity)
      {
         text.Append("  rows: ").Append(identity);
         if (identity.Related.Count > 0) { text.Append("; related: ").AppendJoin("; ", identity.Related); }
         text.AppendLine();
      }
   }

   /// <summary>Rule names with how often they fired: <c>merge filters x2</c>.</summary>
   private static IEnumerable<string> Counted(IReadOnlyList<string> rules) =>
      rules.GroupBy(r => r, StringComparer.Ordinal).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} x{g.Count()}");

   private static string[] Lines(string text) => text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
}
