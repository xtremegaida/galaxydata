using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Types;
using GalaxyData.Web.Metadata;
using Microsoft.EntityFrameworkCore;

namespace GalaxyData.Web.Overlay;

/// <summary>A column's settings: hidden, its label, and the type it is read as (<c>date</c> for a SQLite text column).</summary>
public sealed record ColumnSettingDto([Required, StringLength(MetadataDb.NameLength)] string Name, bool Hidden = false, [StringLength(MetadataDb.NameLength)] string? Label = null,
                                      [StringLength(ColumnSettingDto.TypeLength)] string? Type = null)
{
   public const int TypeLength = 100;
}

/// <summary>
/// The overlay as stored, and as the engine takes it (<see cref="Overlay"/>): each list's items in the order of
/// their ids, so the catalog is built the same each time.
/// </summary>
public sealed class StoredOverlay
{
   private static readonly JsonSerializerOptions ColumnsJson = new(JsonSerializerDefaults.Web);

   private StoredOverlay(IReadOnlyList<RelationDefinition> relations, IReadOnlyList<NavigationOverride> navigations,
                         IReadOnlyList<VirtualEntityDefinition> virtualEntities, IReadOnlyList<EntitySettings> entitySettings)
   {
      Relations = relations;
      Navigations = navigations;
      VirtualEntities = virtualEntities;
      EntitySettings = entitySettings;
      Overlay = new CatalogOverlay
      {
         Relations = [.. relations.Select(Spec)],
         Navigations = [.. navigations.Select(Spec)],
         VirtualEntities = [.. virtualEntities.Select(Spec)],
         Entities = [.. entitySettings.Select(Spec)],
      };
   }

   public static StoredOverlay Empty { get; } = new([], [], [], []);

   public IReadOnlyList<RelationDefinition> Relations { get; }

   public IReadOnlyList<NavigationOverride> Navigations { get; }

   public IReadOnlyList<VirtualEntityDefinition> VirtualEntities { get; }

   public IReadOnlyList<EntitySettings> EntitySettings { get; }

   public CatalogOverlay Overlay { get; }

   public int Count => Relations.Count + Navigations.Count + VirtualEntities.Count + EntitySettings.Count;

   public static async Task<StoredOverlay> LoadAsync(MetadataDb db, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(db);
      return new StoredOverlay(
         await db.OverlayRelations.AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken),
         await db.OverlayNavigations.AsNoTracking().OrderBy(n => n.Id).ToListAsync(cancellationToken),
         await db.OverlayVirtualEntities.AsNoTracking().OrderBy(v => v.Id).ToListAsync(cancellationToken),
         await db.OverlayEntitySettings.AsNoTracking().OrderBy(s => s.Id).ToListAsync(cancellationToken));
   }

   /// <summary>Every item, by kind, with its id and version: what the catalog's version is built from.</summary>
   public IEnumerable<(OverlayItemKind Kind, IOverlayItem Item)> All() =>
      Relations.Select(r => (OverlayItemKind.Relation, (IOverlayItem)r))
         .Concat(Navigations.Select(n => (OverlayItemKind.Navigation, (IOverlayItem)n)))
         .Concat(VirtualEntities.Select(v => (OverlayItemKind.VirtualEntity, (IOverlayItem)v)))
         .Concat(EntitySettings.Select(s => (OverlayItemKind.EntitySettings, (IOverlayItem)s)));

   /// <summary>The id of the stored item an item of <see cref="Overlay"/> is.</summary>
   public int IdOf(OverlayItemRef item)
   {
      ArgumentNullException.ThrowIfNull(item);
      return item.Kind switch
      {
         OverlayItemKind.Relation => Relations[item.Index].Id,
         OverlayItemKind.Navigation => Navigations[item.Index].Id,
         OverlayItemKind.VirtualEntity => VirtualEntities[item.Index].Id,
         _ => EntitySettings[item.Index].Id,
      };
   }

   /// <summary>The overlay with <paramref name="spec"/> in place of the relation <paramref name="id"/>, or added when that is null: as it would be with it.</summary>
   public (CatalogOverlay Overlay, OverlayItemRef Item) With(OverlayRelation spec, int? id)
   {
      List<OverlayRelation> specs = [.. Overlay.Relations];
      int index = Put(specs, Relations, id, spec);
      return (Overlay with { Relations = specs }, new OverlayItemRef(OverlayItemKind.Relation, index));
   }

   public (CatalogOverlay Overlay, OverlayItemRef Item) With(OverlayNavigation spec, int? id)
   {
      List<OverlayNavigation> specs = [.. Overlay.Navigations];
      int index = Put(specs, Navigations, id, spec);
      return (Overlay with { Navigations = specs }, new OverlayItemRef(OverlayItemKind.Navigation, index));
   }

   public (CatalogOverlay Overlay, OverlayItemRef Item) With(OverlayVirtualEntity spec, int? id)
   {
      List<OverlayVirtualEntity> specs = [.. Overlay.VirtualEntities];
      int index = Put(specs, VirtualEntities, id, spec);
      return (Overlay with { VirtualEntities = specs }, new OverlayItemRef(OverlayItemKind.VirtualEntity, index));
   }

   public (CatalogOverlay Overlay, OverlayItemRef Item) With(OverlayEntitySettings spec, int? id)
   {
      List<OverlayEntitySettings> specs = [.. Overlay.Entities];
      int index = Put(specs, EntitySettings, id, spec);
      return (Overlay with { Entities = specs }, new OverlayItemRef(OverlayItemKind.EntitySettings, index));
   }

   private static int Put<TSpec>(List<TSpec> specs, IEnumerable<IOverlayItem> rows, int? id, TSpec spec)
   {
      int index = id is { } replaced ? rows.Select(r => r.Id).ToList().IndexOf(replaced) : -1;
      if (index < 0)
      {
         specs.Add(spec);
         return specs.Count - 1;
      }
      specs[index] = spec;
      return index;
   }

   public static OverlayRelation Spec(RelationDefinition row)
   {
      ArgumentNullException.ThrowIfNull(row);
      return new OverlayRelation(row.From, row.FromColumns, row.To, row.ToColumns)
      {
         Name = row.Name,
         InverseName = row.InverseName,
         Description = row.Description,
      };
   }

   public static OverlayNavigation Spec(NavigationOverride row)
   {
      ArgumentNullException.ThrowIfNull(row);
      return new OverlayNavigation(row.Entity, row.Navigation) { RenameTo = row.RenameTo, Hidden = row.Hidden };
   }

   public static OverlayVirtualEntity Spec(VirtualEntityDefinition row)
   {
      ArgumentNullException.ThrowIfNull(row);
      return new OverlayVirtualEntity(row.Name, row.Query) { Key = row.Key, Description = row.Description };
   }

   public static OverlayEntitySettings Spec(EntitySettings row)
   {
      ArgumentNullException.ThrowIfNull(row);
      return new OverlayEntitySettings(row.Entity)
      {
         Key = row.Key,
         DisplayColumn = row.DisplayColumn,
         Hidden = row.Hidden,
         Columns = [.. Columns(row).Select(c => new OverlayColumn(c.Name)
         {
            Hidden = c.Hidden,
            Label = c.Label,
            // Checked when saved; one this version doesn't read is left as the source has it.
            Type = ScalarType.TryParse(c.Type, out ScalarType type) ? type : null,
         })],
      };
   }

   /// <summary>The columns' settings; none when they can't be read, so a damaged row doesn't stop the catalog being built.</summary>
   public static List<ColumnSettingDto> Columns(EntitySettings row)
   {
      ArgumentNullException.ThrowIfNull(row);
      try
      {
         return [.. (JsonSerializer.Deserialize<List<ColumnSettingDto?>>(row.ColumnsJson, ColumnsJson) ?? []).Where(c => c?.Name != null).Select(c => c!)];
      }
      catch (JsonException)
      {
         return [];
      }
   }

   public static string ColumnsText(IReadOnlyList<ColumnSettingDto> columns) => JsonSerializer.Serialize(columns, ColumnsJson);
}

/// <summary>
/// What the catalog finds wrong with each stored overlay item: an error leaves the item, or the part of it at fault,
/// out of the catalog; a warning doesn't. Items that name an entity of a source whose schema isn't read say so.
/// </summary>
public sealed class OverlayIssues
{
   private readonly Dictionary<(OverlayItemKind Kind, int Id), List<CatalogDiagnostic>> issues;

   private OverlayIssues(Dictionary<(OverlayItemKind, int), List<CatalogDiagnostic>> issues)
   {
      this.issues = issues;
   }

   public static OverlayIssues None { get; } = new([]);

   public int Errors => issues.Values.Count(list => list.Any(d => d.Severity == DiagnosticSeverity.Error));

   public int Warnings => issues.Values.Count(list => list.All(d => d.Severity != DiagnosticSeverity.Error));

   /// <summary>The items with an error, by kind and id.</summary>
   public IEnumerable<(OverlayItemKind Kind, int Id)> Broken =>
      issues.Where(i => i.Value.Any(d => d.Severity == DiagnosticSeverity.Error)).Select(i => i.Key);

   public IReadOnlyList<CatalogDiagnostic> For(OverlayItemKind kind, int id) =>
      issues.TryGetValue((kind, id), out List<CatalogDiagnostic>? found) ? found : [];

   /// <summary>The catalog's diagnostics about <paramref name="overlay"/>'s items, by item; <paramref name="unread"/> are the aliases of sources without a schema.</summary>
   public static OverlayIssues Of(QueryCatalog catalog, StoredOverlay overlay, IReadOnlySet<string> unread)
   {
      ArgumentNullException.ThrowIfNull(catalog);
      ArgumentNullException.ThrowIfNull(overlay);
      ArgumentNullException.ThrowIfNull(unread);
      Dictionary<(OverlayItemKind, int), List<CatalogDiagnostic>> found = [];
      foreach (CatalogDiagnostic diagnostic in catalog.Diagnostics)
      {
         if (diagnostic.Item is not { } item) { continue; }
         (OverlayItemKind, int) key = (item.Kind, overlay.IdOf(item));
         if (!found.TryGetValue(key, out List<CatalogDiagnostic>? list)) { found[key] = list = []; }
         list.Add(Explained(diagnostic, unread));
      }
      return new OverlayIssues(found);
   }

   /// <summary>The issues of the item <paramref name="item"/> of a catalog built for trying an item.</summary>
   public static IReadOnlyList<CatalogDiagnostic> Of(QueryCatalog catalog, OverlayItemRef item, IReadOnlySet<string> unread)
   {
      ArgumentNullException.ThrowIfNull(catalog);
      return [.. catalog.Diagnostics.Where(d => d.Item == item).Select(d => Explained(d, unread))];
   }

   /// <summary>An entity that isn't there because its source's schema isn't read is said to be so.</summary>
   private static CatalogDiagnostic Explained(CatalogDiagnostic diagnostic, IReadOnlySet<string> unread)
   {
      if (diagnostic.Code != DiagnosticCodes.UnknownEntity || diagnostic.Subject is not { } path || !EntityName.TryParse(path, out EntityName? name) ||
          !unread.Contains(name.Parts[0]))
      {
         return diagnostic;
      }
      return diagnostic with { Message = $"{diagnostic.Message}: {name.Parts[0]}'s schema isn't read" };
   }
}
