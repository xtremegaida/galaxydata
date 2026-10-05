using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Language;
using GalaxyData.Query.Results;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Browse;

/// <summary>
/// Pages of rows to browse: an entity's, or those a navigation leads to from a row. Each page is one query (the
/// grid's filters, where and sort composed onto the rows' query, and the display values of the rows each refers to
/// added to it), paged by the engine in a stable order; the rows are counted alongside, within
/// <see cref="QuerySettings.CountTimeout"/>.
/// </summary>
public sealed class BrowseService(CatalogService catalogs, QueryEngines engines, SourceProviders providers, IOptions<GalaxyDataOptions> options)
{
   /// <summary>The rows a page has when the grid doesn't say (and <see cref="QuerySettings.MaxPageSize"/> allows).</summary>
   public const int DefaultPageSize = 100;

   /// <summary>The most crumbs a trail may have: each is a query.</summary>
   public const int MaxCrumbs = 50;

   /// <summary>The most columns a row is found by (a reference's).</summary>
   public const int MaxPositionColumns = 32;

   public async Task<BrowsePageDto> PageAsync(BrowsePageRequest request, bool canEdit, HttpResponse response, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(request);
      QuerySettings settings = options.Value.Query;
      GridStateDto grid = request.Grid ?? new GridStateDto();
      if (grid.Limit > settings.MaxPageSize) { throw Invalid("grid.limit", $"A page has at most {settings.MaxPageSize} rows"); }
      int limit = grid.Limit ?? Math.Min(DefaultPageSize, settings.MaxPageSize);
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      QueryEngine engine = engines.For(state);
      EntityQuery source = Source(state.Catalog, request.Source);

      // The rows' columns, to check the grid's against.
      PreparedQuery shape = engine.Prepare(new QueryRequest(source.Text) { Parameters = source.Parameters });
      if (!shape.Success) { throw new ProblemResultException(PagedRows.QueryProblem(shape.Diagnostics, source.Text)); }
      Dictionary<string, string[]> errors = [];
      ComposedQuery composed = GridQueryComposer.Compose(source.Text, source.Parameters, shape.Schema!.VisibleColumns, grid, errors)
         ?? throw new ProblemResultException(ApiProblems.Invalid(errors));

      List<Reference> references = References(source.Entity, shape.Schema);
      PreparedQuery page = engine.Prepare(new QueryRequest(composed.Text + Displays(references))
      {
         Parameters = composed.Parameters,
         Paging = new PageRequest(grid.Offset, limit + 1L),
      });
      if (!page.Success) { throw new ProblemResultException(PagedRows.QueryProblem(page.Diagnostics, composed.Text, composed.WhereStart, grid.Where?.TrimEnd())); }
      (List<object?[]> rows, bool more, long? total, _) =
         await PagedRows.FetchAsync(engine, page, composed, limit, grid.Offset, request.IncludeCount, settings.CountTimeout, cancellationToken);

      ResultSchema schema = page.Schema!;
      HashSet<string> displays = references.Select(r => r.Alias).ToHashSet(StringComparer.Ordinal);
      List<ResultColumn> columns = schema.VisibleColumns.Where(c => !displays.Contains(c.Name)).ToList();
      List<ResultColumn> shown = references.Select(r => schema.Find(r.Alias)!).ToList();
      IReadOnlyList<ResultColumn>? key = schema.RowIdentity is { } identity ? identity.KeyOrdinals.Select(o => schema.Columns[o]).ToList() : null;
      List<GridRowDto> dtos = rows.Select(row =>
      {
         List<object?>? k = key?.Select(c => ValueCodec.Encode(row[c.Ordinal], c.Type)).ToList();
         return new GridRowDto(key == null ? null : ValueCodec.RowId(key.Select(c => (row[c.Ordinal], c.Type))), k,
            columns.Select(c => ValueCodec.Encode(row[c.Ordinal], c.Type)).ToList(),
            shown.Count == 0 ? null : shown.Select(c => ValueCodec.Encode(row[c.Ordinal], c.Type)).ToList());
      }).ToList();
      return new BrowsePageDto(composed.Text, PagedRows.Parameters(composed.Parameters), source.Entity.DisplayName,
         request.IncludeSchema ? Schema(source.Entity, schema, columns, references, canEdit) : null, dtos, grid.Offset, more, total);
   }

   /// <summary>
   /// The crumbs of a trail as they stand: the entities they reach, and the display values of the rows chosen in them.
   /// A crumb whose rows can't be reached has no entity; one whose row isn't one (a key that can't be read) has its
   /// entity, and the problem.
   /// </summary>
   public async Task<BrowseTrailDto> TrailAsync(BrowseTrailRequest request, HttpResponse response, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(request);
      if (request.Crumbs.Count == 0) { throw Invalid("crumbs", "A trail has at least one crumb"); }
      if (request.Crumbs.Count > MaxCrumbs) { throw Invalid("crumbs", $"A trail has at most {MaxCrumbs} crumbs"); }
      int missing = request.Crumbs.IndexOf(null!);
      if (missing >= 0) { throw Invalid($"crumbs[{missing}]", "A crumb can't be null"); }
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      QueryEngine engine = engines.For(state);
      List<TrailStepDto> steps = [];
      // The rows of the crumb before, and the key of the row chosen in it.
      EntityQuery? rows = null;
      IReadOnlyList<object?>? key = null;
      for (int i = 0; i < request.Crumbs.Count; i++)
      {
         TrailCrumbDto crumb = request.Crumbs[i];
         string label;
         string? problem = null;
         if (i == 0)
         {
            label = crumb.Entity ?? string.Empty;
            EntityDef? entity = crumb.Entity is { } name && EntityName.TryParse(name, out EntityName? path) ? state.Catalog.FindEntity(path) : null;
            if (entity == null) { problem = $"There is no entity {crumb.Entity}"; }
            else
            {
               label = entity.DisplayName;
               rows = new EntityQuery(entity, entity.DisplayName, new QueryParameters());
            }
         }
         else
         {
            label = crumb.Navigation ?? string.Empty;
            NavigationDef? navigation = null;
            if (key == null) { problem = "No row is chosen in the crumb before"; }
            else if (crumb.Navigation == null) { problem = "A crumb after the first follows a navigation"; }
            else { navigation = NavigationResolver.Find(rows!.Entity, crumb.Navigation, out problem); }
            if (navigation != null)
            {
               rows = NavigationResolver.Resolve(rows!.Entity, key!, navigation);
               label = navigation.Name;
            }
         }
         if (problem != null)
         {
            steps.Add(new TrailStepDto(null, label, null, null, problem));
            break;
         }
         key = null;
         if (crumb.Key is { } given)
         {
            key = NavigationResolver.Key(rows!.Entity, given, out string? keyProblem);
            if (key == null)
            {
               // The crumb's rows are there, but the row chosen in them isn't one: no crumb after it can be followed.
               steps.Add(new TrailStepDto(rows.Entity.DisplayName, label, null, false, keyProblem));
               break;
            }
         }
         (bool found, object? title) = key == null ? (false, null) : await TitleAsync(engine, rows!, key, cancellationToken);
         steps.Add(new TrailStepDto(rows!.Entity.DisplayName, label, title, key == null ? null : found, null));
      }
      return new BrowseTrailDto(steps);
   }

   /// <summary>
   /// Where the row of an entity whose columns hold the values given is among the entity's rows as a grid shows them
   /// unsorted, in its key's order (the engine's paging orders by it): its id, and how many rows come before it, those
   /// less in the key's first column (NULLs are less: they sort first), or equal in it and less in the next, and so
   /// on. A grid opens at its page. Where the rows aren't paged in the key's order alone (a virtual entity's own
   /// sort), or the count may compare text otherwise than the page's sort (rows put together in the merge engine), the
   /// row is found but not where.
   /// </summary>
   public async Task<BrowsePositionDto> PositionAsync(BrowsePositionRequest request, HttpResponse response, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(request);
      if (request.Columns.Count == 0) { throw Invalid("columns", "Name the columns whose values find the row"); }
      if (request.Columns.Count > MaxPositionColumns) { throw Invalid("columns", $"A row is found by at most {MaxPositionColumns} columns"); }
      if (request.Values.Count != request.Columns.Count) { throw Invalid("values", $"Give a value for each column ({request.Columns.Count})"); }
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      QueryEngine engine = engines.For(state);
      EntityDef entity = Entity(state.Catalog, request.Entity, "entity");
      QueryParameters parameters = new();
      List<string> conditions = [];
      bool none = false;
      for (int i = 0; i < request.Columns.Count; i++)
      {
         string? name = request.Columns[i];
         if (name == null) { throw Invalid($"columns[{i}]", "A column's name can't be null"); }
         NameMatch<ColumnDef> match = entity.FindColumn(name);
         if (!match.IsFound) { throw Invalid($"columns[{i}]", $"{entity.DisplayName} has no column '{name}'"); }
         ColumnDef column = match.Item!;
         object? value;
         try
         {
            value = ValueCodec.Decode(ValueCodec.Json(request.Values[i]), column.Type);
         }
         catch (ValueFormatException e)
         {
            throw Invalid($"values[{i}]", $"'{column.Name}': {e.Message}");
         }
         // A NULL refers to no row.
         none |= value == null;
         string parameter = "at" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
         parameters.Add(parameter, value, column.Type.AsNonNullable());
         conditions.Add($"{QueryText.QuoteName(column.Name)} == ${parameter}");
      }
      if (none || entity.Key is not { } key) { return new BrowsePositionDto(null, null); }

      // The row's key.
      string rows = entity.DisplayName;
      string keys = string.Join(", ", key.Columns.Select((c, k) => $"k{k}: {QueryText.QuoteName(c.Name)}"));
      PreparedQuery find = engine.Prepare(new QueryRequest(QueryText.Compose(rows, [string.Join(" and ", conditions)]) + $".select({keys}).take(1)")
      {
         Parameters = parameters,
      });
      // Columns whose values don't compare (of unknown types) find no row.
      if (!find.Success) { return new BrowsePositionDto(null, null); }
      IReadOnlyList<object?[]> found;
      await using (QueryResult result = await find.ExecuteAsync(cancellationToken))
      {
         found = await result.ToListAsync(cancellationToken);
      }
      if (found.Count == 0) { return new BrowsePositionDto(null, null); }
      object?[] at = found[0];
      string id = ValueCodec.RowId(key.Columns.Select((c, k) => (at[k], c.Type)));
      // A declared key may hold NULLs, which sort before the rest: where among them isn't told.
      if (at.Any(v => v == null)) { return new BrowsePositionDto(id, null); }
      PreparedQuery all = engine.Prepare(new QueryRequest(rows));
      // Rows in an order of their own are paged in it, the key only breaking its ties.
      if (all.IsOrdered) { return new BrowsePositionDto(id, null); }
      // Rows put together in the merge engine are sorted there, as DuckDB compares text, while the count's comparisons
      // may run in their sources, which may compare it otherwise (ignoring case, by a locale).
      if (all.Merge != null && key.Columns.Any(c => c.Type.Kind == ScalarKind.String)) { return new BrowsePositionDto(id, null); }

      // The rows before it.
      QueryParameters before = new();
      List<string> alternatives = [];
      for (int k = 0; k < key.Columns.Count; k++)
      {
         string parameter = "key" + (k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
         before.Add(parameter, at[k], key.Columns[k].Type.AsNonNullable());
         IEnumerable<string> equal = key.Columns.Take(k).Select((c, j) => $"{QueryText.QuoteName(c.Name)} == $key{(j + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
         string column = QueryText.QuoteName(key.Columns[k].Name);
         // A declared key's column may hold NULLs, which sort before every value.
         string less = key.Columns[k].Type.Nullable ? $"({column} < ${parameter} or {column} == null)" : $"{column} < ${parameter}";
         alternatives.Add("(" + string.Join(" and ", equal.Append(less)) + ")");
      }
      PreparedQuery count = engine.Prepare(new QueryRequest(QueryText.Compose(rows, [string.Join(" or ", alternatives)]))
      {
         Parameters = before,
         Timeout = options.Value.Query.CountTimeout,
      }).ForCount();
      // Keys whose values don't compare in order (guids, binary values): the row is found, but not where.
      if (!count.Success) { return new BrowsePositionDto(id, null); }
      try
      {
         await using QueryResult result = await count.ExecuteAsync(cancellationToken);
         IReadOnlyList<object?[]> counted = await result.ToListAsync(cancellationToken);
         return new BrowsePositionDto(id, counted.Count == 1 ? Convert.ToInt64(counted[0][0], System.Globalization.CultureInfo.InvariantCulture) : null);
      }
      catch (QueryTimeoutException)
      {
         return new BrowsePositionDto(id, null);
      }
   }

   /// <summary>The query of the rows to browse; a problem when there is no such entity, row or navigation.</summary>
   private static EntityQuery Source(QueryCatalog catalog, BrowseSourceDto source)
   {
      if (source.Entity is { } name)
      {
         if (source.From != null || source.Navigation != null) { throw Invalid("source", "Browse an entity, or a navigation from a row: not both"); }
         EntityDef entity = Entity(catalog, name, "source.entity");
         return new EntityQuery(entity, entity.DisplayName, new QueryParameters());
      }
      if (source.From is not { } from || source.Navigation is not { } navigationName)
      {
         throw Invalid("source", "Give an entity, or a row (from) and a navigation");
      }
      EntityDef owner = Entity(catalog, from.Entity, "source.from.entity");
      IReadOnlyList<object?> key = NavigationResolver.Key(owner, from.Key, out string? problem) ?? throw Invalid("source.from.key", problem!);
      NavigationDef navigation = NavigationResolver.Find(owner, navigationName, out problem) ?? throw Invalid("source.navigation", problem!);
      return NavigationResolver.Resolve(owner, key, navigation);
   }

   private static EntityDef Entity(QueryCatalog catalog, string name, string field)
   {
      if (!EntityName.TryParse(name, out EntityName? path)) { throw Invalid(field, $"'{name}' isn't an entity's name, such as shop.orders"); }
      NameMatch<CatalogItem> match = catalog.Resolve(path);
      if (match.Status == MatchStatus.Ambiguous)
      {
         throw Invalid(field, $"{name} names more than one entity, whose names differ only in case: " +
            string.Join(", ", match.Candidates.OfType<EntityDef>().Select(e => e.DisplayName)));
      }
      return match.Item as EntityDef
         ?? throw new ProblemResultException(ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such entity", $"There is no entity {name} in the catalog"));
   }

   /// <summary>A navigation to the row a column refers to, and the name its display value has in the page's query.</summary>
   private sealed record Reference(NavigationDef Navigation, string Alias, string? Expression);

   /// <summary>
   /// The rows the columns refer to, each once: by forward navigations the rows have (a virtual entity's columns may
   /// link along its base's that it hasn't), to one row each (a foreign key a database doesn't make unique would
   /// repeat rows). Their display values have names no column or navigation has.
   /// </summary>
   private static List<Reference> References(EntityDef entity, ResultSchema schema)
   {
      List<Reference> references = [];
      HashSet<string> taken = schema.Columns.Select(c => c.Name)
         .Concat(entity.Navigations.Concat(entity.InheritedNavigations).Select(n => n.Name))
         .ToHashSet(StringComparer.OrdinalIgnoreCase);
      foreach (ResultColumn column in schema.VisibleColumns)
      {
         if (column.Link is not RowLink { Navigation: { IsInverse: false } navigation } || references.Any(r => r.Navigation == navigation)) { continue; }
         if (!entity.Navigations.Contains(navigation) && !entity.InheritedNavigations.Contains(navigation)) { continue; }
         if (!navigation.Target.IsUnique(navigation.TargetColumns)) { continue; }
         string alias = "_display" + references.Count;
         while (!taken.Add(alias)) { alias += "_"; }
         string? expression = navigation.Target.DisplayColumn is { } display
            ? QueryText.AppendMember(new StringBuilder(QueryText.QuoteName(navigation.Name)), display.Name).ToString()
            : null;
         references.Add(new Reference(navigation, alias, expression));
      }
      return references;
   }

   /// <summary>The display values of the rows referred to, added to the page's query.</summary>
   private static string Displays(List<Reference> references) =>
      references.Count == 0
         ? string.Empty
         : ".extend(" + string.Join(", ", references.Select(r => $"{r.Alias}: {r.Expression ?? "null"}")) + ")";

   private BrowseSchemaDto Schema(EntityDef entity, ResultSchema schema, List<ResultColumn> columns, List<Reference> references, bool canEdit)
   {
      SqlDialect? dialect = entity is TableEntity table ? providers.For(table.Source.ProviderKind).Dialect : null;
      CapabilitiesDto capabilities = EntityCapabilities.Of(entity, dialect, canEdit);
      List<GridColumnDto> dtos = [];
      // The entity's own column each value is, when it is one.
      List<ColumnDef?> owns = [.. columns.Select(column =>
         column.Lineage is { Kind: LineageKind.Direct } lineage && lineage.Sources[0].Path.Count == 0 && lineage.Sources[0].Column.Owner == entity
            ? lineage.Sources[0].Column
            : null)];
      for (int i = 0; i < columns.Count; i++)
      {
         ResultColumn column = columns[i];
         ColumnDef? own = owns[i];
         ColumnCapabilities can = own != null
            ? EntityCapabilities.Of(own, dialect, capabilities)
            : new ColumnCapabilities(false, InsertMode.Never, $"'{column.Name}' isn't a column of {entity.DisplayName}");
         int reference = column.Link is RowLink { Navigation: { } navigation } ? references.FindIndex(r => r.Navigation == navigation) : -1;
         dtos.Add(new GridColumnDto(column.Name, TypeDto.Of(column.Type), own?.IsKey ?? false, can.CanUpdate, can.Insert, can.Reason, PagedRows.Lineage(column.Lineage),
            reference < 0 ? null : reference));
      }
      List<GridReferenceDto> referenceDtos = references.Select(r =>
      {
         // Each column of the foreign key, and the target's column it matches, as the navigation pairs them: a
         // column of a composite key may show another reference (a key of its own), but setting this one sets it.
         List<int> held = [];
         List<string> targets = [];
         IReadOnlyList<ColumnDef> owners = r.Navigation.OwnerColumns;
         for (int k = 0; k < owners.Count; k++)
         {
            int at = owns.IndexOf(owners[k]);
            // Columns of another entity's (a virtual entity's navigation inherited from its table): by name.
            if (at < 0) { at = columns.FindIndex(c => c.Lineage.Kind == LineageKind.Direct && string.Equals(c.Name, owners[k].Name, StringComparison.OrdinalIgnoreCase)); }
            if (at < 0) { continue; }
            held.Add(at);
            targets.Add(r.Navigation.TargetColumns[k].Name);
         }
         return new GridReferenceDto(r.Navigation.Name, r.Navigation.Target.DisplayName, held, targets, held.Count == owners.Count,
            r.Navigation.Target.DisplayColumn?.Name, r.Navigation.Multiplicity);
      }).ToList();
      List<GridCollectionDto> collections = entity.Navigations.Concat(entity.InheritedNavigations)
         .Where(n => n.IsInverse && !n.Hidden)
         .Select(n => new GridCollectionDto(n.Name, n.Target.DisplayName, n.Multiplicity))
         .ToList();
      return new BrowseSchemaDto(entity.DisplayName, schema.RowIdentity == null ? null : entity.Key?.Columns.Select(c => c.Name).ToList(), capabilities, dtos,
         referenceDtos, collections);
   }

   /// <summary>
   /// The display value of the row among <paramref name="rows"/> whose key is <paramref name="key"/>, and whether it is
   /// one of them (a crumb's row is found along its trail).
   /// </summary>
   private static async Task<(bool Found, object? Title)> TitleAsync(QueryEngine engine, EntityQuery rows, IReadOnlyList<object?> key, CancellationToken cancellationToken)
   {
      EntityDef entity = rows.Entity;
      KeyDef own = entity.Key!;
      QueryParameters parameters = new();
      foreach (QueryParameter given in rows.Parameters.All) { parameters.Add(given.Name, given.Value, given.Type); }
      List<string> conditions = [];
      for (int i = 0; i < own.Columns.Count; i++)
      {
         string name = "row" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
         parameters.Add(name, key[i], own.Columns[i].Type.AsNonNullable());
         conditions.Add($"{QueryText.QuoteName(own.Columns[i].Name)} == ${name}");
      }
      ColumnDef display = entity.DisplayColumn ?? own.Columns[0];
      string text = QueryText.Compose(rows.Text, [string.Join(" and ", conditions)]) + $".select(title: {QueryText.QuoteName(display.Name)}).take(1)";
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(text) { Parameters = parameters }, cancellationToken);
      IReadOnlyList<object?[]> found = await result.ToListAsync(cancellationToken);
      return found.Count == 0 ? (false, null) : (true, ValueCodec.Encode(found[0][0], display.Type));
   }

   private static ProblemResultException Invalid(string field, string message) =>
      new(ApiProblems.Invalid(new Dictionary<string, string[]> { [field] = [message] }));
}
