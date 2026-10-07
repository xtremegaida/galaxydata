using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Hosting;

namespace GalaxyData.Web.Palettes;

/// <summary>Palettes' definitions as JSON: as dashboards' are written (canonical, so equal hashes mean equal palettes).</summary>
public static class PaletteJson
{
   public static string Write(PaletteDefinition definition) => JsonSerializer.Serialize(definition, DefinitionJson.Options);

   public static PaletteDefinition Read(string json) =>
      JsonSerializer.Deserialize<PaletteDefinition>(json, DefinitionJson.Options) ?? throw new JsonException("A palette can't be null");
}

/// <summary>
/// A palette's definition, checked as it is saved: its colours (<c>#rrggbb</c>), and overrides of labels that are
/// something once normalised, each matching what no other does. What is wrong is given by field, as the JSON names it
/// (<c>definition.overrides[3].label</c>).
/// </summary>
public sealed partial class PaletteRules(PaletteSettings settings)
{
   public const int LabelLength = DefinitionRules.LabelLength;

   [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
   private static partial Regex ColorPattern();

   /// <summary>The definition, checked, in its canonical form (colours in lower case); null when something is wrong, in <paramref name="errors"/> by field under <paramref name="prefix"/>.</summary>
   public PaletteDefinition? Check(PaletteDefinition? definition, string prefix, Dictionary<string, string[]> errors)
   {
      ArgumentNullException.ThrowIfNull(errors);
      int before = errors.Count;
      void Error(string path, string message)
      {
         string key = path.Length == 0 ? prefix.TrimEnd('.') : prefix + path;
         errors[key] = errors.TryGetValue(key, out string[]? existing) ? [.. existing, message] : [message];
      }
      void Color(PaletteColor? color, string path)
      {
         if (color == null) { Error(path, "Give the colour"); }
         else
         {
            if (color.Light == null || !ColorPattern().IsMatch(color.Light)) { Error(path + ".light", "A colour is written #rrggbb"); }
            if (color.Dark != null && !ColorPattern().IsMatch(color.Dark)) { Error(path + ".dark", "A colour is written #rrggbb (or none, for the same in dark schemes)"); }
         }
      }
      if (definition == null)
      {
         Error(string.Empty, "Give the palette's colours");
         return null;
      }
      if (definition.Colors == null) { Error("colors", "Give the colours"); }
      else if (definition.Colors.Count == 0) { Error("colors", "Give a colour at least"); }
      else if (definition.Colors.Count > settings.MaxColors) { Error("colors", $"A palette has {settings.MaxColors} colours at most"); }
      for (int i = 0; i < (definition.Colors?.Count ?? 0); i++) { Color(definition.Colors![i], $"colors[{i}]"); }
      if (definition.Matching == null) { Error("matching", "Say how labels are matched"); }
      if (definition.Overrides == null) { Error("overrides", "Give the overrides (or none)"); }
      else
      {
         if (definition.Overrides.Count > settings.MaxOverrides) { Error("overrides", $"A palette has {settings.MaxOverrides:N0} overrides at most"); }
         Dictionary<string, int> keys = new(StringComparer.Ordinal);
         int? none = null;
         for (int i = 0; i < definition.Overrides.Count; i++)
         {
            string path = $"overrides[{i}]";
            PaletteOverride? item = definition.Overrides[i];
            if (item == null)
            {
               Error(path, "An override can't be null");
               continue;
            }
            Color(item.Color, path + ".color");
            if (item.Label == null)
            {
               if (none is { } first) { Error(path + ".label", $"overrides[{first}] is no value's already"); }
               none ??= i;
               continue;
            }
            if (item.Label.Length > LabelLength)
            {
               Error(path + ".label", $"A label has {LabelLength} characters at most");
               continue;
            }
            if (definition.Matching == null) { continue; }
            string key = LabelText.Normalize(item.Label, definition.Matching);
            if (key.Length == 0) { Error(path + ".label", "Nothing of this label is left to match, as the palette matches labels"); }
            else if (keys.TryGetValue(key, out int earlier)) { Error(path + ".label", $"It matches what overrides[{earlier}] matches"); }
            else { keys[key] = i; }
         }
      }
      if (errors.Count > before) { return null; }

      PaletteDefinition canonical = definition with
      {
         Colors = [.. definition.Colors!.Select(Canonical)],
         Overrides = [.. definition.Overrides!.Select(o => o with { Color = Canonical(o.Color) })],
      };
      if (PaletteJson.Write(canonical).Length > settings.MaxDefinitionLength)
      {
         Error(string.Empty, $"A palette has {settings.MaxDefinitionLength:N0} characters at most, as JSON");
         return null;
      }
      return canonical;
   }

   private static PaletteColor Canonical(PaletteColor color) =>
      new(color.Light.ToLowerInvariant(), color.Dark?.ToLowerInvariant());
}
