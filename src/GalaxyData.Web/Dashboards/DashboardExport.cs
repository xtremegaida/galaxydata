using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GalaxyData.Web.Palettes;

namespace GalaxyData.Web.Dashboards;

/// <summary>
/// A dashboard as a file, to take to another instance or keep: its name, description and one of its copies
/// (<see cref="Definition"/>, as it is stored), and the palettes that copy names, whole, under the ids it names them
/// by. Never whose it is, who sees it, its public link or its revisions. Its sources name entities by the aliases of
/// the connections they were exported from; an import may give them others.
/// </summary>
public sealed record DashboardExport(
   string Format,
   int Version,
   string Name,
   string? Description,
   DateTime ExportedAt,
   DashboardDefinition Definition,
   List<ExportedPalette> Palettes)
{
   /// <summary>What a dashboard's file says it is.</summary>
   public const string FileFormat = "galaxydata.dashboard";

   public const int CurrentVersion = 1;

   /// <summary>Text as it is (<c>'</c>, <c>&amp;</c>, accents) but for what JSON must escape: a file to read, never put in a page.</summary>
   private static readonly JsonSerializerOptions Indented = new(DefinitionJson.Options) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

   /// <summary>The file's JSON: as the API writes definitions (enums by name), indented, so files compare line by line.</summary>
   public static string Write(DashboardExport file) => JsonSerializer.Serialize(file, Indented);

   public static DashboardExport Read(string json) =>
      JsonSerializer.Deserialize<DashboardExport>(json, DefinitionJson.Options) ?? throw new JsonException("A dashboard's file can't be null");

   /// <summary>A name for the file: the dashboard's in lower case, letters and digits, with <c>-</c> between words (<c>sales-2026.dashboard.json</c>).</summary>
   public static string FileName(string name)
   {
      ArgumentNullException.ThrowIfNull(name);
      StringBuilder slug = new();
      foreach (char c in name.Normalize(NormalizationForm.FormKD))
      {
         if (char.IsAsciiLetterOrDigit(c)) { slug.Append(char.ToLowerInvariant(c)); }
         else if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
         {
            if (slug.Length > 0 && slug[^1] != '-') { slug.Append('-'); }
         }
      }
      string text = slug.ToString().Trim('-');
      if (text.Length > 80) { text = text[..80].TrimEnd('-'); }
      return (text.Length == 0 ? "dashboard" : text) + ".dashboard.json";
   }
}

/// <summary>A palette a dashboard's file brings: the id its definition names it by, its name, description and definition.</summary>
public sealed record ExportedPalette(int Id, string Name, string? Description, PaletteDefinition Definition);
