using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Language;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Catalog;

/// <summary>
/// Builds a <see cref="QueryCatalog"/> from introspected sources and an overlay. Problems don't stop the build:
/// the offending item, or the part of it at fault (a column's settings), is left out and a
/// <see cref="CatalogDiagnostic"/> says why.
/// </summary>
public sealed class CatalogBuilder
{
   private readonly List<(SourceInfo Source, SourceSchema Schema)> sources = [];
   private CatalogOverlay overlay = CatalogOverlay.Empty;

   public CatalogBuilder AddSource(SourceInfo source, SourceSchema schema)
   {
      ArgumentNullException.ThrowIfNull(source);
      ArgumentNullException.ThrowIfNull(schema);
      sources.Add((source, schema));
      return this;
   }

   public CatalogBuilder WithOverlay(CatalogOverlay overlay)
   {
      this.overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
      return this;
   }

   public QueryCatalog Build() => new Run(sources, overlay).Build();

   private sealed class Run
   {
      private readonly IReadOnlyList<(SourceInfo Source, SourceSchema Schema)> inputs;
      private readonly CatalogOverlay overlay;
      private readonly NavigationNamingOptions naming;
      private readonly CatalogNamespace root = new(string.Empty, null, NamespaceKind.Root, null);
      private readonly List<CatalogDiagnostic> diagnostics = [];
      private readonly List<SourceInfo> accepted = [];
      private readonly List<EntityDef> entities = [];
      private readonly List<RelationDef> relations = [];
      private readonly Dictionary<TableEntity, TableSchema> physical = [];
      private readonly Dictionary<RelationDef, (OverlayRelation Spec, OverlayItemRef Item)> explicitNames = [];
      private readonly Dictionary<VirtualEntity, (OverlayVirtualEntity Spec, OverlayItemRef Item)> virtuals = [];
      private readonly QueryCatalog catalog;

      public Run(IReadOnlyList<(SourceInfo Source, SourceSchema Schema)> inputs, CatalogOverlay overlay)
      {
         this.inputs = inputs;
         this.overlay = overlay;
         naming = overlay.Naming ?? NavigationNamingOptions.Default;
         catalog = new QueryCatalog(root, accepted, entities, relations, diagnostics);
      }

      /// <summary>
      /// Physical entities and virtual placeholders first; then relations between physical entities, so virtual
      /// definitions can navigate them; then the virtual definitions, bound on demand in dependency order; then the
      /// settings and relations that involve virtual entities.
      /// </summary>
      public QueryCatalog Build()
      {
         foreach ((SourceInfo source, SourceSchema schema) in inputs) { AddSource(source, schema); }
         DeclareVirtualEntities();

         List<(OverlayEntitySettings Spec, EntityDef Entity, OverlayItemRef Item)> settings = ResolveSettings();
         ApplyEntitySettings(settings.Where(s => s.Entity is not VirtualEntity));
         AddForeignKeyRelations();
         List<(OverlayRelation Spec, EntityDef From, EntityDef To, OverlayItemRef Item)> overlayRelations = ResolveOverlayRelations();
         List<RelationDef> physicalRelations = [.. relations];
         foreach (var r in overlayRelations.Where(r => r.From is not VirtualEntity && r.To is not VirtualEntity))
         {
            if (AddOverlayRelation(r.Spec, r.From, r.To, r.Item) is { } relation) { physicalRelations.Add(relation); }
         }
         NameNavigations(physicalRelations);
         HashSet<NavigationDef> overridden = [];
         List<(OverlayNavigation Spec, OverlayItemRef Item)> deferred = ApplyNavigationOverrides(
            overlay.Navigations.Select((n, i) => (n, new OverlayItemRef(OverlayItemKind.Navigation, i))), overridden, lastPass: false);

         foreach (VirtualEntity entity in virtuals.Keys) { EnsureBound(entity); }
         ApplyEntitySettings(settings.Where(s => s.Entity is VirtualEntity));
         List<RelationDef> virtualRelations = [];
         foreach (var r in overlayRelations.Where(r => r.From is VirtualEntity || r.To is VirtualEntity))
         {
            if (AddOverlayRelation(r.Spec, r.From, r.To, r.Item) is { } relation) { virtualRelations.Add(relation); }
         }
         NameNavigations(virtualRelations);
         ApplyNavigationOverrides(deferred, overridden, lastPass: true);

         AssignDisplayColumns();
         return catalog;
      }

      private void Report(string code, DiagnosticSeverity severity, string message, string? subject, OverlayItemRef? item = null) =>
         diagnostics.Add(new CatalogDiagnostic(code, severity, message, subject) { Item = item });

      private void AddSource(SourceInfo source, SourceSchema schema)
      {
         if (!QueryText.IsBareIdentifier(source.Alias))
         {
            Report(DiagnosticCodes.InvalidSourceAlias, DiagnosticSeverity.Error,
               "A source alias must be a plain name (letters, digits and '_', not starting with a digit) and not a keyword",
               source.Alias);
            return;
         }
         if (accepted.Any(s => string.Equals(s.Alias, source.Alias, StringComparison.OrdinalIgnoreCase)))
         {
            Report(DiagnosticCodes.DuplicateSource, DiagnosticSeverity.Error, "Another source already uses this alias", source.Alias);
            return;
         }
         accepted.Add(source);
         CatalogNamespace sourceNs = root.GetOrAddNamespace(source.Alias, NamespaceKind.Source, source);

         foreach (TableSchema table in schema.Tables)
         {
            CatalogNamespace schemaNs = sourceNs.GetOrAddNamespace(table.Schema, NamespaceKind.Schema, source);
            TableEntity entity = new(new EntityName(source.Alias, table.Schema, table.Name), schemaNs, source, table);
            foreach (ColumnSchema column in table.Columns.OrderBy(c => c.Ordinal))
            {
               entity.AddColumn(new ColumnDef(entity, column));
            }
            if (table.PrimaryKey != null && ResolveColumns(entity, table.PrimaryKey.Columns) is { } keyColumns)
            {
               entity.Key = new KeyDef(table.PrimaryKey.Name, keyColumns);
            }
            foreach (KeySchema unique in table.UniqueKeys)
            {
               if (ResolveColumns(entity, unique.Columns) is { } columns) { entity.AddUniqueKey(new KeyDef(unique.Name, columns)); }
            }
            foreach (IndexSchema index in table.Indexes)
            {
               if (!index.IsUnique || index.IsPrimaryKey || index.Filter != null) { continue; }
               if (ResolveColumns(entity, index.Columns, report: false) is { } columns) { entity.AddUniqueKey(new KeyDef(index.Name, columns)); }
            }
            schemaNs.AddEntity(entity);
            entities.Add(entity);
            physical.Add(entity, table);
         }

         if (string.IsNullOrEmpty(schema.DefaultSchema)) { return; }
         CatalogNamespace? defaultNs = sourceNs.Namespaces.FirstOrDefault(n => string.Equals(n.Name, schema.DefaultSchema, StringComparison.Ordinal));
         if (defaultNs == null) { return; }
         foreach (EntityDef entity in defaultNs.Entities)
         {
            if (sourceNs.Namespaces.Any(n => string.Equals(n.Name, entity.Name, StringComparison.OrdinalIgnoreCase)))
            {
               Report(DiagnosticCodes.ShortcutSuppressed, DiagnosticSeverity.Warning,
                  $"'{source.Alias}.{entity.Name}' names a schema, so this table must be written with its schema: {entity.QualifiedName}",
                  entity.QualifiedName.ToString());
               continue;
            }
            sourceNs.AddShortcut(entity);
            entity.DisplayName = new EntityName(source.Alias, entity.Name).ToString();
         }
      }

      private List<ColumnDef>? ResolveColumns(EntityDef entity, IReadOnlyList<string> names, bool report = true, OverlayItemRef? item = null)
      {
         if (names.Count == 0) { return null; }
         List<ColumnDef> columns = new(names.Count);
         foreach (string name in names)
         {
            NameMatch<ColumnDef> match = entity.FindColumn(name);
            if (!match.IsFound)
            {
               if (report)
               {
                  Report(DiagnosticCodes.UnknownColumn, DiagnosticSeverity.Error,
                     match.Status == MatchStatus.Ambiguous ? $"Column '{name}' is ambiguous" : $"There is no column '{name}'",
                     entity.DisplayName, item);
               }
               return null;
            }
            if (columns.Contains(match.Item!))
            {
               if (report)
               {
                  Report(DiagnosticCodes.DuplicateOverlayItem, DiagnosticSeverity.Error, $"Column {match.Item!.Name} is named twice", entity.DisplayName, item);
               }
               return null;
            }
            columns.Add(match.Item!);
         }
         return columns;
      }

      private EntityDef? ResolveEntity(string path, string context, OverlayItemRef item)
      {
         if (!EntityName.TryParse(path, out EntityName? name))
         {
            Report(DiagnosticCodes.InvalidEntityPath, DiagnosticSeverity.Error, $"'{path}' is not an entity path ({context})", path, item);
            return null;
         }
         NameMatch<CatalogItem> match = QueryCatalog.Walk(root, name.Parts);
         if (match.IsFound && match.Item is EntityDef entity) { return entity; }
         string message = match.Status == MatchStatus.Ambiguous
            ? $"'{path}' matches more than one item: {string.Join(", ", match.Candidates.Select(c => c is EntityDef e ? e.QualifiedName.ToString() : c.Name))} ({context})"
            : $"There is no entity '{path}' ({context})";
         Report(DiagnosticCodes.UnknownEntity, DiagnosticSeverity.Error, message, path, item);
         return null;
      }

      /// <summary>The entities settings are for; a second setting for an entity (by another path to it) is left out.</summary>
      private List<(OverlayEntitySettings Spec, EntityDef Entity, OverlayItemRef Item)> ResolveSettings()
      {
         List<(OverlayEntitySettings Spec, EntityDef Entity, OverlayItemRef Item)> resolved = [];
         for (int i = 0; i < overlay.Entities.Count; i++)
         {
            OverlayEntitySettings settings = overlay.Entities[i];
            OverlayItemRef item = new(OverlayItemKind.EntitySettings, i);
            if (ResolveEntity(settings.Entity, "entity settings", item) is not { } entity) { continue; }
            if (resolved.FirstOrDefault(r => r.Entity == entity) is { Spec: not null } earlier)
            {
               Report(DiagnosticCodes.DuplicateOverlayItem, DiagnosticSeverity.Error,
                  $"'{settings.Entity}' is {entity.DisplayName}, which has settings already (as '{earlier.Spec.Entity}'), so these are left out", entity.DisplayName, item);
               continue;
            }
            resolved.Add((settings, entity, item));
            entity.SettingsItem = item;
         }
         return resolved;
      }

      private void ApplyEntitySettings(IEnumerable<(OverlayEntitySettings Spec, EntityDef Entity, OverlayItemRef Item)> items)
      {
         foreach ((OverlayEntitySettings settings, EntityDef entity, OverlayItemRef item) in items)
         {
            if (entity is VirtualEntity { State: not VirtualState.Bound })
            {
               Report(DiagnosticCodes.BrokenVirtualEntity, DiagnosticSeverity.Error, $"{entity.DisplayName} can't be used, so neither can its settings", entity.DisplayName, item);
               continue;
            }
            entity.Hidden = settings.Hidden;
            Dictionary<ColumnDef, string> configured = [];
            foreach (OverlayColumn column in settings.Columns)
            {
               NameMatch<ColumnDef> match = entity.FindColumn(column.Name);
               if (!match.IsFound)
               {
                  Report(DiagnosticCodes.UnknownColumn, DiagnosticSeverity.Error,
                     match.Status == MatchStatus.Ambiguous ? $"Column '{column.Name}' is ambiguous" : $"There is no column '{column.Name}'", entity.DisplayName, item);
                  continue;
               }
               ColumnDef target = match.Item!;
               if (!configured.TryAdd(target, column.Name))
               {
                  Report(DiagnosticCodes.DuplicateOverlayItem, DiagnosticSeverity.Error,
                     $"'{column.Name}' is column {target.Name}, which has settings already (as '{configured[target]}'), so these are left out", entity.DisplayName, item);
                  continue;
               }
               target.Hidden = column.Hidden;
               target.Label = column.Label;
               if (column.Type is { } type) { target.Type = type.WithNullable(target.Type.Nullable); }
            }
            if (settings.Key is { Count: > 0 } declared && ResolveColumns(entity, declared, item: item) is { } keyColumns)
            {
               if (entity.Key is { IsDeclared: false } && entity is not VirtualEntity)
               {
                  Report(DiagnosticCodes.DeclaredKeyIgnored, DiagnosticSeverity.Warning,
                     "The entity has a primary key already, so the declared key is ignored", entity.DisplayName, item);
               }
               else
               {
                  entity.Key = new KeyDef(null, keyColumns, isDeclared: true);
               }
            }
            if (settings.DisplayColumn != null)
            {
               NameMatch<ColumnDef> match = entity.FindColumn(settings.DisplayColumn);
               if (match.IsFound) { entity.DisplayColumn = match.Item; }
               else
               {
                  Report(DiagnosticCodes.UnknownColumn, DiagnosticSeverity.Error,
                     $"There is no column '{settings.DisplayColumn}' to display", entity.DisplayName, item);
               }
            }
         }
      }

      private void AddForeignKeyRelations()
      {
         Dictionary<(string, string, string), TableEntity> exact = [];
         Dictionary<(string, string, string), List<TableEntity>> folded = [];
         foreach (TableEntity entity in physical.Keys)
         {
            exact[(entity.Source.Alias, entity.Schema, entity.Table)] = entity;
            (string, string, string) key = (entity.Source.Alias, entity.Schema.ToUpperInvariant(), entity.Table.ToUpperInvariant());
            if (!folded.TryGetValue(key, out List<TableEntity>? list)) { folded[key] = list = []; }
            list.Add(entity);
         }

         foreach ((TableEntity dependent, TableSchema table) in physical)
         {
            foreach (ForeignKeySchema fk in table.ForeignKeys)
            {
               string subject = fk.Name ?? $"{dependent.DisplayName}({string.Join(", ", fk.Columns)})";
               if (!exact.TryGetValue((dependent.Source.Alias, fk.RefSchema, fk.RefTable), out TableEntity? principal))
               {
                  folded.TryGetValue((dependent.Source.Alias, fk.RefSchema.ToUpperInvariant(), fk.RefTable.ToUpperInvariant()), out List<TableEntity>? candidates);
                  if (candidates is not [TableEntity single])
                  {
                     Report(DiagnosticCodes.ForeignKeyTargetMissing, DiagnosticSeverity.Warning,
                        $"The foreign key refers to {fk.RefSchema}.{fk.RefTable}, which is not in the catalog", subject);
                     continue;
                  }
                  principal = single;
               }
               List<ColumnDef>? fromColumns = ResolveColumns(dependent, fk.Columns);
               List<ColumnDef>? toColumns;
               if (fk.RefColumns.Count == 0)
               {
                  toColumns = principal.Key?.Columns.ToList();
                  if (toColumns == null)
                  {
                     Report(DiagnosticCodes.MissingKey, DiagnosticSeverity.Warning,
                        $"The foreign key refers to the primary key of {principal.DisplayName}, which has none", subject);
                     continue;
                  }
               }
               else
               {
                  toColumns = ResolveColumns(principal, fk.RefColumns);
               }
               if (fromColumns == null || toColumns == null) { continue; }
               if (fromColumns.Count != toColumns.Count)
               {
                  Report(DiagnosticCodes.RelationShape, DiagnosticSeverity.Warning, "The foreign key has a different number of columns on each side", subject);
                  continue;
               }
               bool enforced = fk.IsEnforced || dependent.Source.TrustForeignKeys;
               relations.Add(new RelationDef(fk.Name, RelationOrigin.ForeignKey, dependent, fromColumns, principal, toColumns, enforced));
            }
         }
      }

      private List<(OverlayRelation Spec, EntityDef From, EntityDef To, OverlayItemRef Item)> ResolveOverlayRelations()
      {
         List<(OverlayRelation, EntityDef, EntityDef, OverlayItemRef)> resolved = [];
         for (int i = 0; i < overlay.Relations.Count; i++)
         {
            OverlayRelation spec = overlay.Relations[i];
            OverlayItemRef item = new(OverlayItemKind.Relation, i);
            EntityDef? from = ResolveEntity(spec.From, "relation source", item);
            EntityDef? to = ResolveEntity(spec.To, "relation target", item);
            if (from != null && to != null) { resolved.Add((spec, from, to, item)); }
         }
         return resolved;
      }

      private RelationDef? AddOverlayRelation(OverlayRelation spec, EntityDef from, EntityDef to, OverlayItemRef item)
      {
         string subject = $"{spec.From} -> {spec.To}";
         foreach (EntityDef end in (EntityDef[])[from, to])
         {
            if (end is VirtualEntity { State: not VirtualState.Bound })
            {
               Report(DiagnosticCodes.BrokenVirtualEntity, DiagnosticSeverity.Error, $"{end.DisplayName} can't be used, so neither can this relation", subject, item);
               return null;
            }
         }
         if (spec.FromColumns.Count == 0 || spec.FromColumns.Count != spec.ToColumns.Count)
         {
            Report(DiagnosticCodes.RelationShape, DiagnosticSeverity.Error, "A relation needs the same number of columns, at least one, on each side", subject, item);
            return null;
         }
         List<ColumnDef>? fromColumns = ResolveColumns(from, spec.FromColumns, item: item);
         List<ColumnDef>? toColumns = ResolveColumns(to, spec.ToColumns, item: item);
         if (fromColumns == null || toColumns == null) { return null; }
         HashSet<(ColumnDef, ColumnDef)> pairs = [.. fromColumns.Zip(toColumns)];
         if (relations.FirstOrDefault(r => r.From == from && r.To == to && pairs.SetEquals(r.FromColumns.Zip(r.ToColumns))) is { } same)
         {
            Report(DiagnosticCodes.DuplicateOverlayItem, DiagnosticSeverity.Error,
               same.Origin == RelationOrigin.ForeignKey
                  ? $"The database declares this relation already ({same.Name ?? "a foreign key"}): rename its navigations instead"
                  : "The overlay has this relation already, so this one is left out",
               subject, item);
            return null;
         }
         if (!to.IsUnique(toColumns))
         {
            Report(DiagnosticCodes.RelationNotUnique, DiagnosticSeverity.Error,
               $"The columns on the {to.DisplayName} side must be its key or a unique key, or each row would match several", subject, item);
            return null;
         }
         for (int i = 0; i < fromColumns.Count; i++)
         {
            if (!Comparable(fromColumns[i].Type, toColumns[i].Type))
            {
               Report(DiagnosticCodes.RelationTypeMismatch, DiagnosticSeverity.Warning,
                  $"{fromColumns[i].Name} ({fromColumns[i].Type}) and {toColumns[i].Name} ({toColumns[i].Type}) may not compare equal", subject, item);
            }
         }
         RelationDef relation = new(null, RelationOrigin.Overlay, from, fromColumns, to, toColumns, isEnforced: false) { OverlayItem = item };
         relations.Add(relation);
         explicitNames[relation] = (spec, item);
         return relation;
      }

      private static bool Comparable(ScalarType a, ScalarType b)
      {
         if (a.Kind == b.Kind || a.Kind == ScalarKind.Unknown || b.Kind == ScalarKind.Unknown) { return true; }
         if (a.IsNumeric && b.IsNumeric) { return true; }
         return a.Kind is ScalarKind.Date or ScalarKind.DateTime or ScalarKind.DateTimeOffset &&
                b.Kind is ScalarKind.Date or ScalarKind.DateTime or ScalarKind.DateTimeOffset;
      }

      private void NameNavigations(IReadOnlyList<RelationDef> batch)
      {
         foreach (RelationDef relation in batch)
         {
            relation.Forward = new NavigationDef(relation, isInverse: false);
            relation.Inverse = new NavigationDef(relation, isInverse: true);
         }
         // Names the overlay gives come first, then the conventions' names of the sources' own foreign keys, so a
         // relation the overlay adds never takes a name the database's schema gives.
         int Rank(RelationDef relation, Func<OverlayRelation, string?> given) =>
            explicitNames.TryGetValue(relation, out var spec) && given(spec.Spec) != null ? 0 : relation.Origin == RelationOrigin.ForeignKey ? 1 : 2;

         foreach (RelationDef relation in batch.OrderBy(r => Rank(r, s => s.Name)))
         {
            OverlayRelation? spec = explicitNames.TryGetValue(relation, out var given) ? given.Spec : null;
            string preferred = spec?.Name ?? NavigationNaming.ForwardBase(relation, naming);
            Assign(relation.Forward, preferred, relation.Name ?? AcrossSources(relation.To, preferred, relation), spec?.Name != null ? given.Item : null);
         }

         ILookup<EntityDef, RelationDef> byPrincipal = relations.ToLookup(r => r.To);
         foreach (RelationDef relation in batch.OrderBy(r => Rank(r, s => s.InverseName)))
         {
            OverlayRelation? spec = explicitNames.TryGetValue(relation, out var given) ? given.Spec : null;
            string preferred = spec?.InverseName ?? NavigationNaming.InverseBase(relation, byPrincipal[relation.To]);
            Assign(relation.Inverse, preferred, relation.Name ?? AcrossSources(relation.From, preferred, relation), spec?.InverseName != null ? given.Item : null);
         }
      }

      /// <summary>For a navigation to another source whose name is taken: the name after that source's alias (<c>wh_orders</c>).</summary>
      private static string? AcrossSources(EntityDef target, string preferred, RelationDef relation) =>
         relation.IsCrossSource && target is TableEntity table ? table.Source.Alias + "_" + preferred : null;

      /// <summary>Names a navigation; <paramref name="givenBy"/> is the relation that gave it its name, if one did.</summary>
      private void Assign(NavigationDef navigation, string preferred, string? fallback, OverlayItemRef? givenBy)
      {
         EntityDef owner = navigation.Owner;
         string name = NavigationNaming.Unique(owner, preferred, fallback);
         if (givenBy != null && !string.Equals(name, preferred, StringComparison.Ordinal))
         {
            Report(DiagnosticCodes.NavigationNameTaken, DiagnosticSeverity.Warning,
               $"'{preferred}' is already a member of {owner.DisplayName}, so the navigation is called '{name}'", owner.DisplayName, givenBy);
         }
         navigation.Name = name;
         navigation.ConventionName = name;
         owner.AddNavigation(navigation);
      }

      /// <summary>
      /// Applies renames and hides; before the last pass, ones whose navigation doesn't exist yet come back to retry.
      /// A second override of a navigation (by another path to its entity) is left out.
      /// </summary>
      private List<(OverlayNavigation Spec, OverlayItemRef Item)> ApplyNavigationOverrides(IEnumerable<(OverlayNavigation Spec, OverlayItemRef Item)> specs,
                                                                                          HashSet<NavigationDef> overridden, bool lastPass)
      {
         List<(OverlayNavigation, OverlayItemRef)> retry = [];
         foreach ((OverlayNavigation spec, OverlayItemRef item) in specs)
         {
            EntityDef? entity = lastPass ? ResolveEntity(spec.Entity, "navigation override", item) : ResolveQuietly(spec.Entity);
            if (entity == null)
            {
               if (!lastPass) { retry.Add((spec, item)); }
               continue;
            }
            if (lastPass && entity is VirtualEntity { State: not VirtualState.Bound })
            {
               Report(DiagnosticCodes.BrokenVirtualEntity, DiagnosticSeverity.Error, $"{entity.DisplayName} can't be used, so neither can this override", entity.DisplayName, item);
               continue;
            }
            NavigationDef? navigation =
               entity.Navigations.FirstOrDefault(n => string.Equals(n.ConventionName, spec.Name, StringComparison.Ordinal)) ??
               entity.Navigations.FirstOrDefault(n => string.Equals(n.ConventionName, spec.Name, StringComparison.OrdinalIgnoreCase));
            if (navigation == null)
            {
               if (!lastPass)
               {
                  retry.Add((spec, item));
                  continue;
               }
               Report(DiagnosticCodes.UnknownNavigation, DiagnosticSeverity.Error, $"There is no navigation '{spec.Name}'", entity.DisplayName, item);
               continue;
            }
            if (!overridden.Add(navigation))
            {
               Report(DiagnosticCodes.DuplicateOverlayItem, DiagnosticSeverity.Error,
                  $"{entity.DisplayName}'s navigation '{navigation.ConventionName}' is renamed or hidden already, so this is left out", entity.DisplayName, item);
               continue;
            }
            navigation.OverrideItem = item;
            navigation.Hidden = spec.Hidden;
            if (spec.RenameTo == null || string.Equals(spec.RenameTo, navigation.Name, StringComparison.Ordinal)) { continue; }
            if (entity.HasMemberNamed(spec.RenameTo) && !string.Equals(spec.RenameTo, navigation.Name, StringComparison.OrdinalIgnoreCase))
            {
               Report(DiagnosticCodes.NavigationNameTaken, DiagnosticSeverity.Error,
                  $"Cannot rename '{navigation.Name}' to '{spec.RenameTo}', which is already a member", entity.DisplayName, item);
               continue;
            }
            navigation.Name = spec.RenameTo;
            entity.ReindexNavigations();
         }
         return retry;
      }

      private EntityDef? ResolveQuietly(string path)
      {
         if (!EntityName.TryParse(path, out EntityName? name)) { return null; }
         NameMatch<CatalogItem> match = QueryCatalog.Walk(root, name.Parts);
         return match.IsFound ? match.Item as EntityDef : null;
      }

      private void DeclareVirtualEntities()
      {
         for (int i = 0; i < overlay.VirtualEntities.Count; i++)
         {
            OverlayVirtualEntity spec = overlay.VirtualEntities[i];
            OverlayItemRef item = new(OverlayItemKind.VirtualEntity, i);
            if (!EntityName.TryParse(spec.Name, out EntityName? name) || name.Count < 2)
            {
               Report(DiagnosticCodes.InvalidEntityPath, DiagnosticSeverity.Error,
                  "A virtual entity needs a namespace and a name, as in reports.big_orders", spec.Name, item);
               continue;
            }
            CatalogNamespace ns = root;
            bool placed = true;
            foreach (string part in name.Parts.Take(name.Count - 1))
            {
               NameMatch<CatalogItem> match = ns.Lookup(part);
               if (match.IsFound && match.Item is CatalogNamespace child)
               {
                  ns = child;
                  continue;
               }
               if (match.Status == MatchStatus.NotFound)
               {
                  ns = ns.GetOrAddNamespace(part, NamespaceKind.Virtual, null);
                  continue;
               }
               Report(DiagnosticCodes.InvalidEntityPath, DiagnosticSeverity.Error, $"'{part}' in the path is not a namespace", spec.Name, item);
               placed = false;
               break;
            }
            if (!placed) { continue; }
            if (ns.Contains(name.Last))
            {
               Report(DiagnosticCodes.InvalidEntityPath, DiagnosticSeverity.Error, $"{PathOf(ns, name.Last)} already exists", spec.Name, item);
               continue;
            }
            VirtualEntity entity = new(new EntityName([.. ns.Path, name.Last]), ns, spec.Query) { OverlayItem = item };
            ns.AddEntity(entity);
            entities.Add(entity);
            virtuals.Add(entity, (spec, item));
         }
      }

      private static string PathOf(CatalogNamespace ns, string name) => new EntityName([.. ns.Path, name]).ToString();

      /// <summary>Binds a virtual entity's definition, binding the ones it uses first; a cycle fails every entity in it.</summary>
      private void EnsureBound(VirtualEntity entity)
      {
         if (entity.State != VirtualState.Pending) { return; }
         entity.State = VirtualState.Binding;
         BindContext context = new(catalog, QueryParameters.Empty) { EnsureVirtual = EnsureBound };
         BoundProgram program = Binder.Bind(QueryParser.Default.Parse(entity.QueryText), context);
         string? problem = program.Success ? Adopt(entity, program) : program.Diagnostics.First(d => d.IsError).Message;
         if (problem != null)
         {
            entity.State = VirtualState.Failed;
            entity.Problem = problem;
            Report(DiagnosticCodes.BrokenVirtualEntity, DiagnosticSeverity.Error, $"The definition doesn't work: {problem}", entity.DisplayName, virtuals[entity].Item);
            return;
         }
         entity.Definition = program;
         entity.State = VirtualState.Bound;
      }

      /// <summary>Takes the columns, and when the rows are an entity's the key and navigations, from the definition.</summary>
      private string? Adopt(VirtualEntity entity, BoundProgram program)
      {
         if (program.Result is not BoundQuery query)
         {
            return "a virtual entity must be a query, not a single value";
         }
         RowShape shape = query.Shape;
         if (shape.Members.OfType<RecordMember>().FirstOrDefault() is { } record)
         {
            return $"'{record.Name}' is a whole row; select its key columns instead, and add a relation in the overlay to navigate";
         }
         int ordinal = 0;
         foreach (ColumnMember column in shape.Columns)
         {
            entity.AddColumn(new ColumnDef(entity, column.Name, ordinal++, column.Type));
         }
         if (shape.Entity is { } baseEntity)
         {
            entity.BaseEntity = baseEntity;
            foreach (NavigationMember navigation in shape.Members.OfType<NavigationMember>())
            {
               entity.AddInheritedNavigation(navigation.Navigation);
            }
            // Rows that may repeat (concat) have no key, even when they are an entity's.
            if (baseEntity.Key != null && !MayRepeatRows(query) && ResolveColumns(entity, baseEntity.Key.Columns.Select(c => c.Name).ToList(), report: false) is { } key)
            {
               entity.Key = new KeyDef(null, key);
            }
         }
         if (virtuals[entity].Spec.Key is { Count: > 0 } declared)
         {
            if (ResolveColumns(entity, declared, item: virtuals[entity].Item) is not { } key) { return "its declared key names a column it doesn't have"; }
            entity.Key = new KeyDef(null, key, isDeclared: true);
         }
         return null;
      }

      /// <summary>
      /// Whether an entity's rows may come more than once from a query: <c>concat</c> keeps the duplicates, and
      /// <c>selectMany</c> of anything but a navigation repeats rows for each row they're paired with.
      /// </summary>
      private static bool MayRepeatRows(BoundQuery query) => query switch
      {
         BoundSetOperation { Kind: SetOperationKind.UnionAll } => true,
         BoundSetOperation or BoundDistinct or BoundGroupBy => false,
         BoundSelectMany many => many.Collection is not BoundNavigationQuery,
         BoundWhere where => MayRepeatRows(where.Input),
         BoundSelect select => MayRepeatRows(select.Input),
         BoundExtend extend => MayRepeatRows(extend.Input),
         BoundOrderBy order => MayRepeatRows(order.Input),
         BoundTake take => MayRepeatRows(take.Input),
         BoundSkip skip => MayRepeatRows(skip.Input),
         BoundLetQuery let => let.Let.Value is BoundQuery value && MayRepeatRows(value),
         _ => false,
      };

      private void AssignDisplayColumns()
      {
         foreach (EntityDef entity in entities)
         {
            if (entity.DisplayColumn != null) { continue; }
            entity.DisplayColumn = PickDisplayColumn(entity);
         }
      }

      private ColumnDef? PickDisplayColumn(EntityDef entity)
      {
         foreach (string name in naming.DisplayColumnNames)
         {
            NameMatch<ColumnDef> match = entity.FindColumn(name);
            if (match.IsFound && !match.Item!.Hidden) { return match.Item; }
         }
         ColumnDef? text = entity.Columns.FirstOrDefault(c => !c.Hidden && !c.IsKey && c.Type.Kind == ScalarKind.String);
         return text ?? entity.Key?.Columns[0] ?? entity.Columns.FirstOrDefault();
      }
   }
}
