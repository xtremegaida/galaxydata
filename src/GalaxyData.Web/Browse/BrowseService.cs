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
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Browse;

/// <summary>A browse request that can't be answered as asked: the problem to answer with instead.</summary>
public sealed class BrowseProblemException(IResult result) : Exception
{
   public IResult Result { get; } = result;
}

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
      if (!shape.Success) { throw new BrowseProblemException(QueryProblem(shape.Diagnostics, source.Text)); }
      Dictionary<string, string[]> errors = [];
      ComposedQuery composed = GridQueryComposer.Compose(source.Text, source.Parameters, shape.Schema!.VisibleColumns, grid, errors)
         ?? throw new BrowseProblemException(TypedResults.ValidationProblem(errors));

      List<Reference> references = References(source.Entity, shape.Schema);
      PreparedQuery page = engine.Prepare(new QueryRequest(composed.Text + Displays(references))
      {
         Parameters = composed.Parameters,
         Paging = new PageRequest(grid.Offset, limit + 1L),
      });
      if (!page.Success) { throw new BrowseProblemException(QueryProblem(page.Diagnostics, composed.Text, composed.WhereStart, grid.Where?.TrimEnd())); }

      using CancellationTokenSource counting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      Task<long?> count = request.IncludeCount ? CountAsync(engine, composed, settings.CountTimeout, counting.Token) : Task.FromResult<long?>(null);
      List<object?[]> rows;
      try
      {
         await using QueryResult result = await page.ExecuteAsync(cancellationToken);
         rows = [.. await result.ToListAsync(cancellationToken)];
      }
      catch
      {
         // The page's problem is the one to tell; the count's, if it has one, goes with it.
         await counting.CancelAsync();
         await Quietly(count);
         throw;
      }
      bool more = rows.Count > limit;
      if (more) { rows.RemoveAt(rows.Count - 1); }
      long? total;
      if (request.IncludeCount && !more && (rows.Count > 0 || grid.Offset == 0))
      {
         // The last page tells the count: there is no need to wait for it.
         total = grid.Offset + rows.Count;
         await counting.CancelAsync();
         await Quietly(count);
      }
      else
      {
         total = await count;
      }

      ResultSchema schema = page.Schema!;
      HashSet<string> displays = references.Select(r => r.Alias).ToHashSet(StringComparer.Ordinal);
      List<ResultColumn> columns = schema.VisibleColumns.Where(c => !displays.Contains(c.Name)).ToList();
      List<ResultColumn> shown = references.Select(r => schema.Find(r.Alias)!).ToList();
      IReadOnlyList<ResultColumn>? key = schema.RowIdentity is { } identity ? identity.KeyOrdinals.Select(o => schema.Columns[o]).ToList() : null;
      List<GridRowDto> dtos = rows.Select(row =>
      {
         List<object?>? k = key?.Select(c => ValueCodec.Encode(row[c.Ordinal], c.Type)).ToList();
         return new GridRowDto(k == null ? null : JsonSerializer.Serialize(k), k, columns.Select(c => ValueCodec.Encode(row[c.Ordinal], c.Type)).ToList(),
            shown.Count == 0 ? null : shown.Select(c => ValueCodec.Encode(row[c.Ordinal], c.Type)).ToList());
      }).ToList();
      return new BrowsePageDto(composed.Text, Parameters(composed.Parameters), source.Entity.DisplayName,
         request.IncludeSchema ? Schema(source.Entity, schema, columns, references, canEdit) : null, dtos, grid.Offset, more, total);
   }

   /// <summary>The crumbs of a trail as they stand: the entities they reach, and the display values of the rows chosen in them.</summary>
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
         key = null;
         if (problem == null && crumb.Key is { } given) { key = NavigationResolver.Key(rows!.Entity, given, out problem); }
         if (problem != null)
         {
            steps.Add(new TrailStepDto(null, label, null, null, problem));
            break;
         }
         (bool found, object? title) = key == null ? (false, null) : await TitleAsync(engine, rows!, key, cancellationToken);
         steps.Add(new TrailStepDto(rows!.Entity.DisplayName, label, title, key == null ? null : found, null));
      }
      return new BrowseTrailDto(steps);
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
         ?? throw new BrowseProblemException(ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such entity", $"There is no entity {name} in the catalog"));
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
      for (int i = 0; i < columns.Count; i++)
      {
         ResultColumn column = columns[i];
         // The entity's own column the value is, when it is one.
         ColumnDef? own = column.Lineage is { Kind: LineageKind.Direct } lineage && lineage.Sources[0].Path.Count == 0 && lineage.Sources[0].Column.Owner == entity
            ? lineage.Sources[0].Column
            : null;
         ColumnCapabilities can = own != null
            ? EntityCapabilities.Of(own, dialect, capabilities)
            : new ColumnCapabilities(false, InsertMode.Never, $"'{column.Name}' isn't a column of {entity.DisplayName}");
         int reference = column.Link is RowLink { Navigation: { } navigation } ? references.FindIndex(r => r.Navigation == navigation) : -1;
         dtos.Add(new GridColumnDto(column.Name, TypeDto.Of(column.Type), own?.IsKey ?? false, can.CanUpdate, can.Insert, can.Reason, Lineage(column.Lineage),
            reference < 0 ? null : reference));
      }
      List<GridReferenceDto> referenceDtos = references.Select(r => new GridReferenceDto(r.Navigation.Name, r.Navigation.Target.DisplayName,
         Enumerable.Range(0, columns.Count).Where(i => columns[i].Link is RowLink { Navigation: { } n } && n == r.Navigation).ToList(),
         r.Navigation.Multiplicity)).ToList();
      List<GridCollectionDto> collections = entity.Navigations.Concat(entity.InheritedNavigations)
         .Where(n => n.IsInverse && !n.Hidden)
         .Select(n => new GridCollectionDto(n.Name, n.Target.DisplayName, n.Multiplicity))
         .ToList();
      return new BrowseSchemaDto(entity.DisplayName, schema.RowIdentity == null ? null : entity.Key?.Columns.Select(c => c.Name).ToList(), capabilities, dtos,
         referenceDtos, collections);
   }

   private static LineageDto Lineage(ColumnLineage lineage) =>
      new(lineage.Kind, lineage.Sources.Select(s => new LineageSourceDto(s.Column.ToString(), s.Path.Count == 0 ? null : s.PathText)).ToList(), lineage.ExpressionText);

   private static List<QueryParameterDto> Parameters(QueryParameters parameters) =>
      parameters.All.Select(p => new QueryParameterDto(p.Name, p.Type.ToString(), ValueCodec.Encode(p.Value, p.Type))).ToList();

   /// <summary>How many rows the grid's query gives; null when counting them takes longer than <paramref name="limit"/>.</summary>
   private static async Task<long?> CountAsync(QueryEngine engine, ComposedQuery composed, TimeSpan limit, CancellationToken cancellationToken)
   {
      PreparedQuery count = engine.Prepare(new QueryRequest(composed.Text) { Parameters = composed.Parameters, Timeout = limit }).ForCount();
      if (!count.Success) { return null; }
      try
      {
         await using QueryResult result = await count.ExecuteAsync(cancellationToken);
         IReadOnlyList<object?[]> rows = await result.ToListAsync(cancellationToken);
         return rows.Count == 1 ? Convert.ToInt64(rows[0][0], System.Globalization.CultureInfo.InvariantCulture) : null;
      }
      catch (Exception e) when (e is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
      {
         // Out of time, or failed where the page didn't (rows fetched past the limit, a source busy): the rows come
         // all the same, without the count.
         return null;
      }
   }

   /// <summary>Waits for a count that is no longer wanted, whatever became of it.</summary>
   private static async Task Quietly(Task<long?> count)
   {
      try
      {
         await count;
      }
      catch (Exception e) when (e is not OutOfMemoryException)
      {
         // Cancelled, or failed with the page.
      }
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

   /// <summary>A query that can't run: its diagnostics, placed in the user's where expression when they are about it.</summary>
   private static IResult QueryProblem(IReadOnlyList<QueryDiagnostic> diagnostics, string text, int? whereStart = null, string? where = null)
   {
      if (whereStart is int start && where != null)
      {
         int end = start + where.Length;
         List<QueryDiagnostic> inWhere = diagnostics.Where(d => d.IsError && d.Start >= start && d.Start <= end).ToList();
         if (inWhere.Count > 0)
         {
            ProblemDetails problem = ApiProblems.ForDiagnostics(inWhere.Select(d => d with { Start = d.Start - start, End = Math.Min(d.End, end) - start }).ToList());
            problem.Extensions["field"] = "grid.where";
            return TypedResults.Problem(problem);
         }
      }
      ProblemDetails other = ApiProblems.ForDiagnostics(diagnostics);
      other.Extensions["queryText"] = text;
      return TypedResults.Problem(other);
   }

   private static BrowseProblemException Invalid(string field, string message) =>
      new(TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }));
}
