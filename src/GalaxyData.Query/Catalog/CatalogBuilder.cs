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
/// the offending item is left out and a <see cref="CatalogDiagnostic"/> says why.
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
      private readonly Dictionary<RelationDef, OverlayRelation> explicitNames = [];
      private readonly Dictionary<VirtualEntity, OverlayVirtualEntity> virtuals = [];
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

         List<(OverlayEntitySettings Spec, EntityDef Entity)> settings = ResolveSettings();
         ApplyEntitySettings(settings.Where(s => s.Entity is not VirtualEntity));
         AddForeignKeyRelations();
         List<(OverlayRelation Spec, EntityDef From, EntityDef To)> overlayRelations = ResolveOverlayRelations();
         List<RelationDef> physicalRelations = [.. relations];
         foreach (var r in overlayRelations.Where(r => r.From is not VirtualEntity && r.To is not VirtualEntity))
         {
            if (AddOverlayRelation(r.Spec, r.From, r.To) is { } relation) { physicalRelations.Add(relation); }
         }
         NameNavigations(physicalRelations);
         List<OverlayNavigation> deferred = ApplyNavigationOverrides(overlay.Navigations, lastPass: false);

         foreach (VirtualEntity entity in virtuals.Keys) { EnsureBound(entity); }
         ApplyEntitySettings(settings.Where(s => s.Entity is VirtualEntity));
         List<RelationDef> virtualRelations = [];
         foreach (var r in overlayRelations.Where(r => r.From is VirtualEntity || r.To is VirtualEntity))
         {
            if (AddOverlayRelation(r.Spec, r.From, r.To) is { } relation) { virtualRelations.Add(relation); }
         }
         NameNavigations(virtualRelations);
         ApplyNavigationOverrides(deferred, lastPass: true);

         AssignDisplayColumns();
         return catalog;
      }

      private void Report(string code, DiagnosticSeverity severity, string message, string? subject) =>
         diagnostics.Add(new CatalogDiagnostic(code, severity, message, subject));

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

      private List<ColumnDef>? ResolveColumns(EntityDef entity, IReadOnlyList<string> names, bool report = true)
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
                     entity.DisplayName);
               }
               return null;
            }
            columns.Add(match.Item!);
         }
         return columns;
      }

      private EntityDef? ResolveEntity(string path, string context)
      {
         if (!EntityName.TryParse(path, out EntityName? name))
         {
            Report(DiagnosticCodes.InvalidEntityPath, DiagnosticSeverity.Error, $"'{path}' is not an entity path ({context})", path);
            return null;
         }
         NameMatch<CatalogItem> match = QueryCatalog.Walk(root, name.Parts);
         if (match.IsFound && match.Item is EntityDef entity) { return entity; }
         string message = match.Status == MatchStatus.Ambiguous
            ? $"'{path}' matches more than one item: {string.Join(", ", match.Candidates.Select(c => c is EntityDef e ? e.QualifiedName.ToString() : c.Name))} ({context})"
            : $"There is no entity '{path}' ({context})";
         Report(DiagnosticCodes.UnknownEntity, DiagnosticSeverity.Error, message, path);
         return null;
      }

      private List<(OverlayEntitySettings Spec, EntityDef Entity)> ResolveSettings()
      {
         List<(OverlayEntitySettings, EntityDef)> resolved = [];
         foreach (OverlayEntitySettings settings in overlay.Entities)
         {
            if (ResolveEntity(settings.Entity, "entity settings") is { } entity) { resolved.Add((settings, entity)); }
         }
         return resolved;
      }

      private void ApplyEntitySettings(IEnumerable<(OverlayEntitySettings Spec, EntityDef Entity)> items)
      {
         foreach ((OverlayEntitySettings settings, EntityDef entity) in items)
         {
            if (entity is VirtualEntity { State: not VirtualState.Bound }) { continue; }
            entity.Hidden = settings.Hidden;
            foreach (OverlayColumn column in settings.Columns)
            {
               NameMatch<ColumnDef> match = entity.FindColumn(column.Name);
               if (!match.IsFound)
               {
                  Report(DiagnosticCodes.UnknownColumn, DiagnosticSeverity.Error, $"There is no column '{column.Name}'", entity.DisplayName);
                  continue;
               }
               ColumnDef target = match.Item!;
               target.Hidden = column.Hidden;
               target.Label = column.Label;
               if (column.Type is { } type) { target.Type = type.WithNullable(target.Type.Nullable); }
            }
            if (settings.Key is { Count: > 0 } declared && ResolveColumns(entity, declared) is { } keyColumns)
            {
               if (entity.Key is { IsDeclared: false } && entity is not VirtualEntity)
               {
                  Report(DiagnosticCodes.DeclaredKeyIgnored, DiagnosticSeverity.Warning,
                     "The entity has a primary key already, so the declared key is ignored", entity.DisplayName);
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
                     $"There is no column '{settings.DisplayColumn}' to display", entity.DisplayName);
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

      private List<(OverlayRelation Spec, EntityDef From, EntityDef To)> ResolveOverlayRelations()
      {
         List<(OverlayRelation, EntityDef, EntityDef)> resolved = [];
         foreach (OverlayRelation spec in overlay.Relations)
         {
            EntityDef? from = ResolveEntity(spec.From, "relation source");
            EntityDef? to = ResolveEntity(spec.To, "relation target");
            if (from != null && to != null) { resolved.Add((spec, from, to)); }
         }
         return resolved;
      }

      private RelationDef? AddOverlayRelation(OverlayRelation spec, EntityDef from, EntityDef to)
      {
         string subject = $"{spec.From} -> {spec.To}";
         foreach (EntityDef end in (EntityDef[])[from, to])
         {
            if (end is VirtualEntity { State: not VirtualState.Bound })
            {
               Report(DiagnosticCodes.BrokenVirtualEntity, DiagnosticSeverity.Error, $"{end.DisplayName} can't be used, so neither can this relation", subject);
               return null;
            }
         }
         if (spec.FromColumns.Count == 0 || spec.FromColumns.Count != spec.ToColumns.Count)
         {
            Report(DiagnosticCodes.RelationShape, DiagnosticSeverity.Error, "A relation needs the same number of columns, at least one, on each side", subject);
            return null;
         }
         List<ColumnDef>? fromColumns = ResolveColumns(from, spec.FromColumns);
         List<ColumnDef>? toColumns = ResolveColumns(to, spec.ToColumns);
         if (fromColumns == null || toColumns == null) { return null; }
         if (!to.IsUnique(toColumns))
         {
            Report(DiagnosticCodes.RelationNotUnique, DiagnosticSeverity.Error,
               $"The columns on the {to.DisplayName} side must be its key or a unique key, or each row would match several", subject);
            return null;
         }
         for (int i = 0; i < fromColumns.Count; i++)
         {
            if (!Comparable(fromColumns[i].Type, toColumns[i].Type))
            {
               Report(DiagnosticCodes.RelationTypeMismatch, DiagnosticSeverity.Warning,
                  $"{fromColumns[i].Name} ({fromColumns[i].Type}) and {toColumns[i].Name} ({toColumns[i].Type}) may not compare equal", subject);
            }
         }
         RelationDef relation = new(null, RelationOrigin.Overlay, from, fromColumns, to, toColumns, isEnforced: false);
         relations.Add(relation);
         explicitNames[relation] = spec;
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
         List<RelationDef> ordered =
         [
            .. batch.Where(r => explicitNames.ContainsKey(r)),
            .. batch.Where(r => !explicitNames.ContainsKey(r)),
         ];

         foreach (RelationDef relation in ordered)
         {
            explicitNames.TryGetValue(relation, out OverlayRelation? spec);
            string preferred = spec?.Name ?? NavigationNaming.ForwardBase(relation, naming);
            Assign(relation.Forward, preferred, relation.Name, spec?.Name != null);
         }

         ILookup<EntityDef, RelationDef> byPrincipal = relations.ToLookup(r => r.To);
         foreach (RelationDef relation in ordered)
         {
            explicitNames.TryGetValue(relation, out OverlayRelation? spec);
            string preferred = spec?.InverseName ?? NavigationNaming.InverseBase(relation, byPrincipal[relation.To]);
            Assign(relation.Inverse, preferred, relation.Name, spec?.InverseName != null);
         }
      }

      private void Assign(NavigationDef navigation, string preferred, string? fallback, bool isExplicit)
      {
         EntityDef owner = navigation.Owner;
         string name = NavigationNaming.Unique(owner, preferred, fallback);
         if (isExplicit && !string.Equals(name, preferred, StringComparison.Ordinal))
         {
            Report(DiagnosticCodes.NavigationNameTaken, DiagnosticSeverity.Warning,
               $"'{preferred}' is already a member of {owner.DisplayName}, so the navigation is called '{name}'", owner.DisplayName);
         }
         navigation.Name = name;
         navigation.ConventionName = name;
         owner.AddNavigation(navigation);
      }

      /// <summary>Applies renames and hides; before the last pass, ones whose navigation doesn't exist yet come back to retry.</summary>
      private List<OverlayNavigation> ApplyNavigationOverrides(IEnumerable<OverlayNavigation> specs, bool lastPass)
      {
         List<OverlayNavigation> retry = [];
         foreach (OverlayNavigation spec in specs)
         {
            EntityDef? entity = lastPass ? ResolveEntity(spec.Entity, "navigation override") : ResolveQuietly(spec.Entity);
            if (entity == null)
            {
               if (!lastPass) { retry.Add(spec); }
               continue;
            }
            NavigationDef? navigation =
               entity.Navigations.FirstOrDefault(n => string.Equals(n.ConventionName, spec.Name, StringComparison.Ordinal)) ??
               entity.Navigations.FirstOrDefault(n => string.Equals(n.ConventionName, spec.Name, StringComparison.OrdinalIgnoreCase));
            if (navigation == null)
            {
               if (!lastPass)
               {
                  retry.Add(spec);
                  continue;
               }
               Report(DiagnosticCodes.UnknownNavigation, DiagnosticSeverity.Error, $"There is no navigation '{spec.Name}'", entity.DisplayName);
               continue;
            }
            navigation.Hidden = spec.Hidden;
            if (spec.RenameTo == null || string.Equals(spec.RenameTo, navigation.Name, StringComparison.Ordinal)) { continue; }
            if (entity.HasMemberNamed(spec.RenameTo) && !string.Equals(spec.RenameTo, navigation.Name, StringComparison.OrdinalIgnoreCase))
            {
               Report(DiagnosticCodes.NavigationNameTaken, DiagnosticSeverity.Error,
                  $"Cannot rename '{navigation.Name}' to '{spec.RenameTo}', which is already a member", entity.DisplayName);
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
         foreach (OverlayVirtualEntity spec in overlay.VirtualEntities)
         {
            if (!EntityName.TryParse(spec.Name, out EntityName? name) || name.Count < 2)
            {
               Report(DiagnosticCodes.InvalidEntityPath, DiagnosticSeverity.Error,
                  "A virtual entity needs a namespace and a name, as in reports.big_orders", spec.Name);
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
               Report(DiagnosticCodes.InvalidEntityPath, DiagnosticSeverity.Error, $"'{part}' in the path is not a namespace", spec.Name);
               placed = false;
               break;
            }
            if (!placed) { continue; }
            if (ns.Contains(name.Last))
            {
               Report(DiagnosticCodes.InvalidEntityPath, DiagnosticSeverity.Error, $"{PathOf(ns, name.Last)} already exists", spec.Name);
               continue;
            }
            VirtualEntity entity = new(new EntityName([.. ns.Path, name.Last]), ns, spec.Query);
            ns.AddEntity(entity);
            entities.Add(entity);
            virtuals.Add(entity, spec);
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
            Report(DiagnosticCodes.BrokenVirtualEntity, DiagnosticSeverity.Error, $"The definition doesn't work: {problem}", entity.DisplayName);
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
            if (baseEntity.Key != null && ResolveColumns(entity, baseEntity.Key.Columns.Select(c => c.Name).ToList(), report: false) is { } key)
            {
               entity.Key = new KeyDef(null, key);
            }
         }
         if (virtuals[entity].Key is { Count: > 0 } declared)
         {
            if (ResolveColumns(entity, declared) is not { } key) { return "its declared key names a column it doesn't have"; }
            entity.Key = new KeyDef(null, key, isDeclared: true);
         }
         return null;
      }

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
