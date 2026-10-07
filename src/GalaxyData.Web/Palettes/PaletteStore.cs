using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GalaxyData.Web.Palettes;

/// <summary>A label's colour, as an answer names the label (its text there: never an override's, which may say more).</summary>
public sealed record LabelColorDto(string? Label, string Light, string? Dark);

/// <summary>A palette as charts are drawn with it: its definition, and its overrides by the text they match.</summary>
public sealed class StoredPalette
{
   private readonly Dictionary<string, PaletteColor> overrides = new(StringComparer.Ordinal);

   private readonly PaletteColor? none;

   public StoredPalette(int id, string hash, PaletteDefinition definition)
   {
      ArgumentNullException.ThrowIfNull(definition);
      Id = id;
      Hash = hash;
      Definition = definition;
      foreach (PaletteOverride item in definition.Overrides)
      {
         if (item.Label == null) { none ??= item.Color; }
         else { overrides.TryAdd(LabelText.Normalize(item.Label, definition.Matching), item.Color); }
      }
   }

   public int Id { get; }

   public string Hash { get; }

   public PaletteDefinition Definition { get; }

   /// <summary>The colour overriding a label's (null: no value's), if one does.</summary>
   public PaletteColor? Override(string? label) =>
      label == null ? none : overrides.GetValueOrDefault(LabelText.Normalize(label, Definition.Matching));

   /// <summary>The overrides of <paramref name="labels"/> (each once), by the labels as given.</summary>
   public IReadOnlyList<LabelColorDto> Colors(IEnumerable<string?> labels)
   {
      ArgumentNullException.ThrowIfNull(labels);
      List<LabelColorDto> colors = [];
      HashSet<string> seen = new(StringComparer.Ordinal);
      bool sawNone = false;
      foreach (string? label in labels)
      {
         if (label == null ? !sawNone : seen.Add(label))
         {
            sawNone |= label == null;
            if (Override(label) is { } color) { colors.Add(new LabelColorDto(label, color.Light, color.Dark)); }
         }
      }
      return colors;
   }
}

/// <summary>
/// The palettes dashboards name, as charts are drawn with them: read by id, their definitions kept by hash (each
/// request reads only ids and hashes, so an edit shows at the next read).
/// </summary>
public sealed class PaletteStore : IDisposable
{
   private readonly MemoryCache kept = new(new MemoryCacheOptions { SizeLimit = 500 });

   public async Task<IReadOnlyDictionary<int, StoredPalette>> FindAsync(MetadataDb db, IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(db);
      ArgumentNullException.ThrowIfNull(ids);
      Dictionary<int, StoredPalette> found = [];
      if (ids.Count == 0) { return found; }
      var hashes = await db.Palettes.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Hash }).ToListAsync(cancellationToken);
      List<int> unread = [];
      foreach (var row in hashes)
      {
         if (kept.TryGetValue(Key(row.Id, row.Hash), out StoredPalette? palette) && palette != null) { found[row.Id] = palette; }
         else { unread.Add(row.Id); }
      }
      if (unread.Count == 0) { return found; }
      var rows = await db.Palettes.AsNoTracking().Where(p => unread.Contains(p.Id)).Select(p => new { p.Id, p.Hash, p.DefinitionJson }).ToListAsync(cancellationToken);
      foreach (var row in rows)
      {
         StoredPalette palette;
         try
         {
            palette = new StoredPalette(row.Id, row.Hash, PaletteJson.Read(row.DefinitionJson));
         }
         catch (JsonException)
         {
            continue;
         }
         kept.Set(Key(row.Id, row.Hash), palette, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(10) });
         found[row.Id] = palette;
      }
      return found;
   }

   public void Dispose() => kept.Dispose();

   private static string Key(int id, string hash) => $"{id}:{hash}";
}

/// <summary>The palettes a dashboard names: its charts' default, and charts' own.</summary>
public static class PaletteRefs
{
   /// <summary>A chart's own palette; none for widgets that aren't charts.</summary>
   public static int? Own(WidgetConfig? config) => config switch
   {
      PieConfig pie => pie.Palette,
      BarConfig bar => bar.Palette,
      LineConfig line => line.Palette,
      _ => null,
   };

   public static SortedSet<int> Of(DashboardDefinition? definition)
   {
      SortedSet<int> named = [];
      if (definition == null) { return named; }
      if (definition.Palette is { } palette) { named.Add(palette); }
      foreach (DashboardWidget widget in definition.Widgets)
      {
         if (Own(widget.Config) is { } own) { named.Add(own); }
      }
      return named;
   }

   /// <summary>The palettes a chart may be drawn with, in turn: its own, then the dashboard's (one that isn't there gives way to the next).</summary>
   public static List<int> For(DashboardDefinition definition, DashboardWidget widget)
   {
      ArgumentNullException.ThrowIfNull(definition);
      ArgumentNullException.ThrowIfNull(widget);
      if (widget.Config is not (PieConfig or BarConfig or LineConfig)) { return []; }
      List<int> candidates = [];
      if (Own(widget.Config) is { } own) { candidates.Add(own); }
      if (definition.Palette is { } shared && shared != Own(widget.Config)) { candidates.Add(shared); }
      return candidates;
   }

   /// <summary>The palettes named in a definition's JSON, as it is stored (none when it isn't one).</summary>
   public static IEnumerable<int> InJson(string? json)
   {
      if (string.IsNullOrEmpty(json)) { return []; }
      try
      {
         return Of(DefinitionJson.Read(json));
      }
      catch (JsonException)
      {
         return [];
      }
   }
}
