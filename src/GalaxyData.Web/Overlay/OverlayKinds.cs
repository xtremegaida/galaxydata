using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Types;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.EntityFrameworkCore;

namespace GalaxyData.Web.Overlay;

/// <summary>
/// What the overlay's endpoints do with one kind of item: read a request into a row (and say what is wrong with
/// it, by field), tell what an item is (for the audit), find another it can't be beside, and describe it.
/// </summary>
public abstract class OverlayKind<TRow, TInput, TDto> where TRow : class, IOverlayItem, new()
{
   public abstract OverlayItemKind Kind { get; }

   /// <summary>The kind in the audit's actions and targets: <c>relation</c>, <c>navigation</c>, <c>virtual-entity</c>, <c>entity-settings</c>.</summary>
   public abstract string Slug { get; }

   /// <summary>The kind in the API's paths.</summary>
   public abstract string Path { get; }

   /// <summary>The field of an update's request that has the item: <c>relation</c>, <c>navigation</c>, <c>virtualEntity</c>, <c>settings</c>.</summary>
   public abstract string Field { get; }

   /// <summary>The kind in messages: "relation", "navigation override", ...</summary>
   public abstract string Noun { get; }

   public abstract DbSet<TRow> Set(MetadataDb db);

   public abstract IReadOnlyList<TRow> Rows(StoredOverlay overlay);

   /// <summary>Reads a request into <paramref name="row"/>: names checked, blanks made null; what is wrong goes in <paramref name="errors"/>, by field.</summary>
   public abstract void Fill(TInput input, TRow row, Dictionary<string, string[]> errors);

   /// <summary>What the item is, field by field, as the audit tells it.</summary>
   public abstract Dictionary<string, object?> Fields(TRow row);

   /// <summary>Copies what <see cref="Fill"/> fills.</summary>
   public abstract void Copy(TRow from, TRow to);

   public abstract (CatalogOverlay Overlay, OverlayItemRef Item) With(StoredOverlay overlay, TRow row, int? id);

   /// <summary>What the item is about, for the audit's target and for messages.</summary>
   public abstract string Name(TRow row);

   /// <summary>Whether another item is for the same thing (the same entity's settings); the message that says so.</summary>
   public virtual Task<string?> TakenAsync(MetadataDb db, TRow row, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

   /// <summary>The item, its issues, and what it makes in <paramref name="catalog"/> (where it is <paramref name="item"/>, when it is in it).</summary>
   public abstract TDto Dto(TRow row, IReadOnlyList<OverlayIssueDto> issues, QueryCatalog catalog, OverlayItemRef? item);

   /// <summary>What the item, tried, would make: its issues and more, by kind.</summary>
   public virtual OverlayCheckDto Check(TRow row, IReadOnlyList<OverlayIssueDto> issues, QueryCatalog catalog, OverlayItemRef item, OverlayTrial trial) => new(issues);

   protected static string EntityPath(string? text, string field, Dictionary<string, string[]> errors, bool virtualEntity = false)
   {
      string path = text?.Trim() ?? string.Empty;
      if (!EntityName.TryParse(path, out EntityName? name))
      {
         errors[field] = [$"'{path}' isn't an entity path, as queries write them: shop.orders, wh.sales.orders or xl['Budget 2024']['Sheet 1']"];
      }
      else if (virtualEntity && name.Count < 2)
      {
         errors[field] = [$"A virtual entity's name has a namespace and a name, as in reports.{path}"];
      }
      return path;
   }

   /// <summary>
   /// Names of columns, trimmed: none blank, none twice; at least one when <paramref name="required"/>, else null when
   /// there are none. Names alike but for case may be two columns (in a database that tells them apart): the catalog
   /// says when they are one.
   /// </summary>
   protected static List<string>? Names(List<string>? names, string field, Dictionary<string, string[]> errors, bool required)
   {
      if (names is not { Count: > 0 })
      {
         if (required) { errors[field] = ["Name at least one column"]; }
         return required ? [] : null;
      }
      List<string> trimmed = [.. names.Select(n => n?.Trim() ?? string.Empty)];
      for (int i = 0; i < trimmed.Count; i++)
      {
         if (trimmed[i].Length == 0) { errors[$"{field}[{i}]"] = ["A column's name can't be blank"]; }
         else if (trimmed[i].Length > MetadataDb.NameLength) { errors[$"{field}[{i}]"] = [$"A column's name has {MetadataDb.NameLength} characters at most"]; }
         else if (trimmed.IndexOf(trimmed[i]) < i) { errors[$"{field}[{i}]"] = [$"'{trimmed[i]}' is named twice"]; }
      }
      return trimmed;
   }

   protected static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

   protected static string List(IEnumerable<string> names) => string.Join(", ", names);
}

/// <summary>What trying an item needs beyond the catalog: the providers (for what may be changed) and an engine for the trial catalog.</summary>
public sealed record OverlayTrial(Catalog.SourceProviders Providers, Func<QueryCatalog, QueryEngine> Engine);

public sealed class RelationKind : OverlayKind<RelationDefinition, RelationInput, RelationDto>
{
   public static RelationKind Instance { get; } = new();

   public override OverlayItemKind Kind => OverlayItemKind.Relation;

   public override string Slug => "relation";

   public override string Path => "relations";

   public override string Field => "relation";

   public override string Noun => "relation";

   public override DbSet<RelationDefinition> Set(MetadataDb db) => db.OverlayRelations;

   public override IReadOnlyList<RelationDefinition> Rows(StoredOverlay overlay) => overlay.Relations;

   public override void Fill(RelationInput input, RelationDefinition row, Dictionary<string, string[]> errors)
   {
      row.From = EntityPath(input.From, "from", errors);
      row.FromColumns = Names(input.FromColumns, "fromColumns", errors, required: true)!;
      row.To = EntityPath(input.To, "to", errors);
      row.ToColumns = Names(input.ToColumns, "toColumns", errors, required: true)!;
      if (row.FromColumns.Count > 0 && row.ToColumns.Count > 0 && row.FromColumns.Count != row.ToColumns.Count)
      {
         errors["toColumns"] = [$"A relation has as many columns on each side: {row.FromColumns.Count} from, {row.ToColumns.Count} to"];
      }
      row.Name = Trimmed(input.Name);
      row.InverseName = Trimmed(input.InverseName);
      row.Description = Trimmed(input.Description);
   }

   public override Dictionary<string, object?> Fields(RelationDefinition row) => new()
   {
      ["from"] = row.From,
      ["fromColumns"] = row.FromColumns,
      ["to"] = row.To,
      ["toColumns"] = row.ToColumns,
      ["name"] = row.Name,
      ["inverseName"] = row.InverseName,
      ["description"] = row.Description,
   };

   public override void Copy(RelationDefinition from, RelationDefinition to)
   {
      to.From = from.From;
      to.FromColumns = from.FromColumns;
      to.To = from.To;
      to.ToColumns = from.ToColumns;
      to.Name = from.Name;
      to.InverseName = from.InverseName;
      to.Description = from.Description;
   }

   public override (CatalogOverlay Overlay, OverlayItemRef Item) With(StoredOverlay overlay, RelationDefinition row, int? id) => overlay.With(StoredOverlay.Spec(row), id);

   public override string Name(RelationDefinition row) => $"{row.From}({List(row.FromColumns)}) -> {row.To}({List(row.ToColumns)})";

   /// <summary>The same relation again, as written; the catalog finds one written otherwise (another path, the columns in another order, a foreign key).</summary>
   public override async Task<string?> TakenAsync(MetadataDb db, RelationDefinition row, CancellationToken cancellationToken)
   {
      List<RelationDefinition> between = await db.OverlayRelations.AsNoTracking().Where(r => r.Id != row.Id && r.From == row.From && r.To == row.To).ToListAsync(cancellationToken);
      return between.Any(r => r.FromColumns.SequenceEqual(row.FromColumns, StringComparer.Ordinal) && r.ToColumns.SequenceEqual(row.ToColumns, StringComparer.Ordinal))
         ? $"The overlay has the relation {Name(row)} already"
         : null;
   }

   public override RelationDto Dto(RelationDefinition row, IReadOnlyList<OverlayIssueDto> issues, QueryCatalog catalog, OverlayItemRef? item) =>
      new(row.Id, row.From, row.FromColumns, row.To, row.ToColumns, row.Name, row.InverseName, row.Description, Navigations(catalog, item), issues, row.CreatedAt,
         row.UpdatedAt, row.Version);

   public override OverlayCheckDto Check(RelationDefinition row, IReadOnlyList<OverlayIssueDto> issues, QueryCatalog catalog, OverlayItemRef item, OverlayTrial trial) =>
      new(issues) { Navigations = Navigations(catalog, item) };

   private static RelationNavigationsDto? Navigations(QueryCatalog catalog, OverlayItemRef? item) =>
      item != null && catalog.Relations.FirstOrDefault(r => r.OverlayItem == item) is { } relation ? new(relation.Forward.Name, relation.Inverse.Name) : null;
}

public sealed class NavigationOverrideKind : OverlayKind<NavigationOverride, NavigationOverrideInput, NavigationOverrideDto>
{
   public static NavigationOverrideKind Instance { get; } = new();

   public override OverlayItemKind Kind => OverlayItemKind.Navigation;

   public override string Slug => "navigation";

   public override string Path => "navigations";

   public override string Field => "navigation";

   public override string Noun => "navigation override";

   public override DbSet<NavigationOverride> Set(MetadataDb db) => db.OverlayNavigations;

   public override IReadOnlyList<NavigationOverride> Rows(StoredOverlay overlay) => overlay.Navigations;

   public override void Fill(NavigationOverrideInput input, NavigationOverride row, Dictionary<string, string[]> errors)
   {
      row.Entity = EntityPath(input.Entity, "entity", errors);
      row.Navigation = input.Navigation?.Trim() ?? string.Empty;
      if (row.Navigation.Length == 0) { errors["navigation"] = ["Name the navigation, as the convention names it"]; }
      row.RenameTo = Trimmed(input.RenameTo);
      row.Hidden = input.Hidden;
      if (row.RenameTo == null && !row.Hidden) { errors["renameTo"] = ["Rename the navigation, or hide it"]; }
   }

   public override Dictionary<string, object?> Fields(NavigationOverride row) => new()
   {
      ["entity"] = row.Entity,
      ["navigation"] = row.Navigation,
      ["renameTo"] = row.RenameTo,
      ["hidden"] = row.Hidden,
   };

   public override void Copy(NavigationOverride from, NavigationOverride to)
   {
      to.Entity = from.Entity;
      to.Navigation = from.Navigation;
      to.RenameTo = from.RenameTo;
      to.Hidden = from.Hidden;
   }

   public override (CatalogOverlay Overlay, OverlayItemRef Item) With(StoredOverlay overlay, NavigationOverride row, int? id) => overlay.With(StoredOverlay.Spec(row), id);

   public override string Name(NavigationOverride row) => $"{row.Entity}.{row.Navigation}";

   public override async Task<string?> TakenAsync(MetadataDb db, NavigationOverride row, CancellationToken cancellationToken) =>
      await db.OverlayNavigations.AnyAsync(n => n.Id != row.Id && n.Entity == row.Entity && n.Navigation == row.Navigation, cancellationToken)
         ? $"{row.Entity}'s navigation '{row.Navigation}' is renamed or hidden already: change that override"
         : null;

   public override NavigationOverrideDto Dto(NavigationOverride row, IReadOnlyList<OverlayIssueDto> issues, QueryCatalog catalog, OverlayItemRef? item) =>
      new(row.Id, row.Entity, row.Navigation, row.RenameTo, row.Hidden, issues, row.CreatedAt, row.UpdatedAt, row.Version);

   public override OverlayCheckDto Check(NavigationOverride row, IReadOnlyList<OverlayIssueDto> issues, QueryCatalog catalog, OverlayItemRef item, OverlayTrial trial) =>
      new(issues) { Entity = Describe(catalog, row.Entity, trial) };

   internal static Features.Catalog.EntityDto? Describe(QueryCatalog catalog, string path, OverlayTrial trial) =>
      catalog.FindEntity(path) is { } entity ? Features.Catalog.CatalogEndpoints.Describe(entity, trial.Providers, canEdit: true) : null;
}

public sealed class VirtualEntityKind : OverlayKind<VirtualEntityDefinition, VirtualEntityInput, VirtualEntityDto>
{
   public static VirtualEntityKind Instance { get; } = new();

   public override OverlayItemKind Kind => OverlayItemKind.VirtualEntity;

   public override string Slug => "virtual-entity";

   public override string Path => "virtual-entities";

   public override string Field => "virtualEntity";

   public override string Noun => "virtual entity";

   public override DbSet<VirtualEntityDefinition> Set(MetadataDb db) => db.OverlayVirtualEntities;

   public override IReadOnlyList<VirtualEntityDefinition> Rows(StoredOverlay overlay) => overlay.VirtualEntities;

   public override void Fill(VirtualEntityInput input, VirtualEntityDefinition row, Dictionary<string, string[]> errors)
   {
      row.Name = EntityPath(input.Name, "name", errors, virtualEntity: true);
      row.Query = input.Query ?? string.Empty;
      if (string.IsNullOrWhiteSpace(row.Query)) { errors["query"] = ["Write the query whose rows the entity has, such as shop.orders.where(total > 100)"]; }
      row.Key = Names(input.Key, "key", errors, required: false);
      row.Description = Trimmed(input.Description);
   }

   public override Dictionary<string, object?> Fields(VirtualEntityDefinition row) => new()
   {
      ["name"] = row.Name,
      ["query"] = row.Query,
      ["key"] = row.Key,
      ["description"] = row.Description,
   };

   public override void Copy(VirtualEntityDefinition from, VirtualEntityDefinition to)
   {
      to.Name = from.Name;
      to.Query = from.Query;
      to.Key = from.Key;
      to.Description = from.Description;
   }

   public override (CatalogOverlay Overlay, OverlayItemRef Item) With(StoredOverlay overlay, VirtualEntityDefinition row, int? id) =>
      overlay.With(StoredOverlay.Spec(row), id);

   public override string Name(VirtualEntityDefinition row) => row.Name;

   public override async Task<string?> TakenAsync(MetadataDb db, VirtualEntityDefinition row, CancellationToken cancellationToken) =>
      await db.OverlayVirtualEntities.AnyAsync(v => v.Id != row.Id && v.Name == row.Name, cancellationToken)
         ? $"There is a virtual entity {row.Name} already"
         : null;

   public override VirtualEntityDto Dto(VirtualEntityDefinition row, IReadOnlyList<OverlayIssueDto> issues, QueryCatalog catalog, OverlayItemRef? item) =>
      new(row.Id, row.Name, row.Query, row.Key, row.Description, issues, row.CreatedAt, row.UpdatedAt, row.Version);

   /// <summary>The entity it makes (none when its name is taken), and its query's diagnostics, placed in its text.</summary>
   public override OverlayCheckDto Check(VirtualEntityDefinition row, IReadOnlyList<OverlayIssueDto> issues, QueryCatalog catalog, OverlayItemRef item,
                                         OverlayTrial trial)
   {
      PreparedQuery prepared = trial.Engine(catalog).Prepare(new QueryRequest(row.Query));
      VirtualEntity? made = catalog.Entities.OfType<VirtualEntity>().FirstOrDefault(v => v.OverlayItem == item);
      return new OverlayCheckDto(issues)
      {
         Entity = made == null ? null : Features.Catalog.CatalogEndpoints.Describe(made, trial.Providers, canEdit: true),
         Diagnostics = [.. prepared.Diagnostics.Select(DiagnosticDto.From)],
      };
   }
}

public sealed class EntitySettingsKind : OverlayKind<EntitySettings, EntitySettingsInput, EntitySettingsDto>
{
   public static EntitySettingsKind Instance { get; } = new();

   public override OverlayItemKind Kind => OverlayItemKind.EntitySettings;

   public override string Slug => "entity-settings";

   public override string Path => "entity-settings";

   public override string Field => "settings";

   public override string Noun => "entity settings";

   public override DbSet<EntitySettings> Set(MetadataDb db) => db.OverlayEntitySettings;

   public override IReadOnlyList<EntitySettings> Rows(StoredOverlay overlay) => overlay.EntitySettings;

   public override void Fill(EntitySettingsInput input, EntitySettings row, Dictionary<string, string[]> errors)
   {
      row.Entity = EntityPath(input.Entity, "entity", errors);
      row.Key = Names(input.Key, "key", errors, required: false);
      row.DisplayColumn = Trimmed(input.DisplayColumn);
      row.Hidden = input.Hidden;
      List<ColumnSettingDto> columns = [];
      List<ColumnSettingDto?> given = [.. input.Columns ?? []];
      for (int i = 0; i < given.Count; i++)
      {
         string field = $"columns[{i}]";
         if (given[i] is not { } column)
         {
            errors[field] = ["A column's settings can't be null"];
            continue;
         }
         string name = column.Name?.Trim() ?? string.Empty;
         if (name.Length == 0)
         {
            errors[field + ".name"] = ["Name the column"];
            continue;
         }
         if (columns.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal)))
         {
            errors[field + ".name"] = [$"'{name}' has settings already, before"];
            continue;
         }
         string? type = null;
         if (!string.IsNullOrWhiteSpace(column.Type))
         {
            if (ScalarType.TryParse(column.Type.Trim(), out ScalarType parsed)) { type = parsed.WithNullable(false).ToString(); }
            else { errors[field + ".type"] = [$"'{column.Type}' isn't a type: use int32, int64, decimal(12,2), double, string, boolean, date, datetime, datetimeoffset, time, guid, ..."]; }
         }
         columns.Add(new ColumnSettingDto(name, column.Hidden, Trimmed(column.Label), type));
      }
      row.ColumnsJson = StoredOverlay.ColumnsText(columns);
   }

   public override Dictionary<string, object?> Fields(EntitySettings row) => new()
   {
      ["entity"] = row.Entity,
      ["key"] = row.Key,
      ["displayColumn"] = row.DisplayColumn,
      ["hidden"] = row.Hidden,
      ["columns"] = StoredOverlay.Columns(row),
   };

   public override void Copy(EntitySettings from, EntitySettings to)
   {
      to.Entity = from.Entity;
      to.Key = from.Key;
      to.DisplayColumn = from.DisplayColumn;
      to.Hidden = from.Hidden;
      to.ColumnsJson = from.ColumnsJson;
   }

   public override (CatalogOverlay Overlay, OverlayItemRef Item) With(StoredOverlay overlay, EntitySettings row, int? id) => overlay.With(StoredOverlay.Spec(row), id);

   public override string Name(EntitySettings row) => row.Entity;

   public override async Task<string?> TakenAsync(MetadataDb db, EntitySettings row, CancellationToken cancellationToken) =>
      await db.OverlayEntitySettings.AnyAsync(s => s.Id != row.Id && s.Entity == row.Entity, cancellationToken)
         ? $"{row.Entity} has settings already: change those"
         : null;

   public override EntitySettingsDto Dto(EntitySettings row, IReadOnlyList<OverlayIssueDto> issues, QueryCatalog catalog, OverlayItemRef? item) =>
      new(row.Id, row.Entity, row.Key, row.DisplayColumn, row.Hidden, StoredOverlay.Columns(row), issues, row.CreatedAt, row.UpdatedAt, row.Version);

   public override OverlayCheckDto Check(EntitySettings row, IReadOnlyList<OverlayIssueDto> issues, QueryCatalog catalog, OverlayItemRef item, OverlayTrial trial) =>
      new(issues) { Entity = NavigationOverrideKind.Describe(catalog, row.Entity, trial) };
}
