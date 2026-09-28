using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Results;
using GalaxyData.Query.Sql;

namespace GalaxyData.Query.Cli;

internal enum OutputFormat
{
   Table,
   Csv,
   Json,
}

/// <summary>Writes results, SQL, diagnostics and the catalog for people (tables) or programs (CSV, JSON).</summary>
internal static class Output
{
   private const int MaxWidth = 40;

   public static void Rows(TextWriter writer, ResultSchema schema, IReadOnlyList<object?[]> rows, OutputFormat format)
   {
      switch (format)
      {
         case OutputFormat.Csv:
            Csv(writer, schema, rows);
            break;
         case OutputFormat.Json:
            Json(writer, schema, rows);
            break;
         default:
            Table(writer, schema, rows);
            break;
      }
   }

   private static void Table(TextWriter writer, ResultSchema schema, IReadOnlyList<object?[]> rows)
   {
      IReadOnlyList<ResultColumn> columns = schema.VisibleColumns;
      int count = columns.Count;
      string[][] cells = rows.Select(r => r.Take(count).Select(v => Clip(Text(v) ?? "null")).ToArray()).ToArray();
      int[] widths = new int[count];
      for (int i = 0; i < count; i++)
      {
         widths[i] = Math.Max(Clip(columns[i].Name).Length, cells.Length == 0 ? 0 : cells.Max(c => c[i].Length));
      }
      bool[] right = columns.Select(c => c.Type.IsNumeric).ToArray();
      writer.WriteLine(Line(columns.Select(c => Clip(c.Name)).ToArray(), widths, new bool[count]));
      writer.WriteLine(string.Join("-+-", widths.Select(w => new string('-', w))));
      foreach (string[] row in cells) { writer.WriteLine(Line(row, widths, right)); }
   }

   private static string Line(string[] cells, int[] widths, bool[] right) =>
      string.Join(" | ", cells.Select((c, i) => right[i] ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))).TrimEnd();

   private static string Clip(string text)
   {
      string single = text.ReplaceLineEndings(" ");
      return single.Length <= MaxWidth ? single : single[..(MaxWidth - 1)] + "…";
   }

   private static void Csv(TextWriter writer, ResultSchema schema, IReadOnlyList<object?[]> rows)
   {
      int count = schema.VisibleColumns.Count;
      writer.WriteLine(string.Join(",", schema.VisibleColumns.Select(c => CsvField(c.Name))));
      foreach (object?[] row in rows) { writer.WriteLine(string.Join(",", row.Take(count).Select(v => v == null ? string.Empty : CsvField(Text(v)!)))); }
   }

   private static string CsvField(string text) =>
      text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;

   private static void Json(TextWriter writer, ResultSchema schema, IReadOnlyList<object?[]> rows)
   {
      using MemoryStream stream = new();
      using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
      {
         json.WriteStartArray();
         foreach (object?[] row in rows)
         {
            json.WriteStartObject();
            for (int i = 0; i < schema.VisibleColumns.Count; i++)
            {
               json.WritePropertyName(schema.VisibleColumns[i].Name);
               switch (row[i])
               {
                  case null:
                     json.WriteNullValue();
                     break;
                  case bool flag:
                     json.WriteBooleanValue(flag);
                     break;
                  case long or int or short:
                     json.WriteNumberValue(Convert.ToInt64(row[i], CultureInfo.InvariantCulture));
                     break;
                  case decimal number:
                     json.WriteNumberValue(number);
                     break;
                  case double or float:
                     json.WriteNumberValue(Convert.ToDouble(row[i], CultureInfo.InvariantCulture));
                     break;
                  default:
                     json.WriteStringValue(Text(row[i]));
                     break;
               }
            }
            json.WriteEndObject();
         }
         json.WriteEndArray();
      }
      writer.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
   }

   /// <summary>A value as text: invariant numbers, ISO dates, base64 bytes.</summary>
   public static string? Text(object? value) => value switch
   {
      null => null,
      string text => text,
      bool flag => flag ? "true" : "false",
      double number => number.ToString("R", CultureInfo.InvariantCulture),
      float number => number.ToString("R", CultureInfo.InvariantCulture),
      DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
      TimeOnly time => time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
      DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
      DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
      byte[] bytes => Convert.ToBase64String(bytes),
      _ => Convert.ToString(value, CultureInfo.InvariantCulture),
   };

   /// <summary>The SQL each source runs, then the merge engine's SQL over their rows when the query combines sources.</summary>
   public static void Fragments(TextWriter writer, PreparedQuery prepared)
   {
      foreach (QueryFragment fragment in prepared.Fragments)
      {
         writer.WriteLine($"-- {fragment.Source.Alias} ({fragment.Dialect.Name}){(fragment.Table != null ? ", fetched into " + fragment.Table : string.Empty)}");
         Statement(writer, fragment.Statement, fragment.Dialect);
      }
      if (prepared.Merge != null)
      {
         writer.WriteLine($"-- merge ({prepared.MergeDialect!.Name})");
         Statement(writer, prepared.Merge, prepared.MergeDialect);
      }
   }

   private static void Statement(TextWriter writer, SqlStatement statement, SqlDialect dialect)
   {
      writer.WriteLine(statement.Text);
      foreach (SqlParameterSlot parameter in statement.Parameters)
      {
         writer.WriteLine($"-- {dialect.Placeholder(parameter.Name)} {parameter.Type} = {parameter.Description}");
      }
      writer.WriteLine();
   }

   /// <summary>Each diagnostic with the line of the query it is on, underlined.</summary>
   public static void Diagnostics(TextWriter writer, string text, IEnumerable<QueryDiagnostic> diagnostics)
   {
      foreach (QueryDiagnostic diagnostic in diagnostics)
      {
         writer.WriteLine($"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code}: {diagnostic.Message}");
         int start = Math.Clamp(diagnostic.Start, 0, text.Length);
         int lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
         int lineEnd = text.IndexOf('\n', start);
         string line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)].TrimEnd('\r');
         int length = Math.Max(1, Math.Min(diagnostic.End, lineStart + line.Length) - start);
         writer.WriteLine("  " + line);
         writer.WriteLine("  " + new string(' ', start - lineStart) + new string('^', length));
      }
   }

   /// <summary>The entities whose names contain <paramref name="filter"/>, with their columns and navigations.</summary>
   public static void Catalog(TextWriter writer, ICatalog catalog, string? filter)
   {
      foreach (CatalogDiagnostic diagnostic in catalog.Diagnostics) { writer.WriteLine($"-- {diagnostic}"); }
      foreach (EntityDef entity in catalog.Entities.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
      {
         if (filter != null && !entity.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)) { continue; }
         string key = entity.Key == null ? "no key" : "key " + string.Join(", ", entity.Key.Columns.Select(c => c.Name));
         writer.WriteLine($"{entity.DisplayName} ({entity.Kind.ToString().ToLowerInvariant()}, {key})");
         writer.WriteLine("   " + string.Join(", ", entity.Columns.Select(c => $"{c.Name} {c.Type}")));
         IEnumerable<NavigationDef> navigations = entity.Navigations.Concat(entity.InheritedNavigations).Where(n => !n.Hidden);
         string links = string.Join(", ", navigations.Select(n => $"{n.Name} -> {n.Target.DisplayName}{(n.IsCollection ? "*" : n.Multiplicity == Multiplicity.ZeroOrOne ? "?" : string.Empty)}"));
         if (links.Length > 0) { writer.WriteLine("   " + links); }
      }
   }
}
