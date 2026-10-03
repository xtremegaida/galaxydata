using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Explain;
using GalaxyData.Query.Language;
using GalaxyData.Query.Results;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Queries;

/// <summary>
/// Queries as written: checked as they are typed, explained, run a page at a time as a grid shows them (the grid's
/// filters, where and sort composed onto the query, paged by the engine, counted alongside), and the rows their
/// values lead to. What can't be answered is a <see cref="ProblemResultException"/>.
/// </summary>
public sealed class QueryService(CatalogService catalogs, QueryEngines engines, SourceProviders providers, IOptions<GalaxyDataOptions> options)
{
   /// <summary>What is wrong with a query, and what it uses and gives; parameters without values are taken as null.</summary>
   public async Task<QueryValidationDto> ValidateAsync(QueryTextRequest request, HttpResponse response, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(request);
      QueryParameters given = Parameters(request.Parameters);
      // A text too long to run is told so by the engine, without looking for its parameters.
      IReadOnlyList<(string Name, Range Range)> uses = request.Text.Length <= QueryLimits.MaxTextLength ? QueryText.ParameterUses(request.Text) : [];
      List<string> used = [.. uses.Select(u => u.Name).Distinct(StringComparer.Ordinal)];
      QueryParameters all = Copy(given);
      foreach (string name in used.Where(n => !given.TryGet(n, out _))) { all.Add(name, null); }
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      PreparedQuery prepared = engines.For(state).Prepare(new QueryRequest(request.Text) { Parameters = all });

      // A null doesn't fit everywhere a value would (-$n, abs($n), $a == $b): there, the parameter wants a value before more can be said.
      List<Range> unfilled = [.. uses.Where(u => !given.TryGet(u.Name, out _)).Select(u => u.Range)];
      List<DiagnosticDto> diagnostics = [];
      foreach (QueryDiagnostic diagnostic in prepared.Diagnostics)
      {
         Range? at = diagnostic.IsError ? unfilled.Cast<Range?>().FirstOrDefault(r => Overlaps(diagnostic, r!.Value)) : null;
         string? name = at is { } range ? request.Text[range][1..] : null;
         diagnostics.Add(name == null
            ? DiagnosticDto.From(diagnostic)
            : new DiagnosticDto(diagnostic.Code, DiagnosticSeverity.Info, $"${name} has no value, so this can't be checked yet: {diagnostic.Message}", diagnostic.Start, diagnostic.End));
      }
      return new QueryValidationDto(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), prepared.Success, diagnostics,
         [.. used.Select(n => new UsedParameterDto(n, given.TryGet(n, out _), prepared.ParameterTypes.TryGetValue(n, out ScalarType type) && type.Kind != ScalarKind.Unknown
            ? type.AsNonNullable().ToString()
            : null))],
         prepared.Success ? [.. prepared.Schema!.VisibleColumns.Select(c => new QueryColumnDto(c.Name, TypeDto.Of(c.Type)))] : null);
   }

   private static bool Overlaps(QueryDiagnostic diagnostic, Range range) =>
      diagnostic.Start < range.End.Value && Math.Max(diagnostic.End, diagnostic.Start + 1) > range.Start.Value;

   public async Task<QueryPageDto> PageAsync(QueryPageRequest request, bool canEdit, HttpResponse response, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(request);
      QuerySettings settings = options.Value.Query;
      GridStateDto grid = request.Grid ?? new GridStateDto();
      if (grid.Limit > settings.MaxPageSize) { throw Invalid("grid.limit", $"A page has at most {settings.MaxPageSize} rows"); }
      int limit = grid.Limit ?? Math.Min(BrowseService.DefaultPageSize, settings.MaxPageSize);
      QueryParameters parameters = Parameters(request.Parameters);
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      QueryEngine engine = engines.For(state);
      (PreparedQuery page, ComposedQuery composed, PreparedQuery shape) = Prepare(engine, request.Text, parameters, grid, new PageRequest(grid.Offset, limit + 1L));
      (List<object?[]> rows, bool more, long? total, ExecutionStats stats) =
         await PagedRows.FetchAsync(engine, page, composed, limit, grid.Offset, request.IncludeCount, settings.CountTimeout, cancellationToken);

      ResultSchema schema = page.Schema!;
      IReadOnlyList<ResultColumn>? key = schema.RowIdentity?.KeyOrdinals.Select(o => schema.Columns[o]).ToList();
      List<ResultRowDto> dtos = rows.Select(row =>
      {
         string? id = key == null ? null : JsonSerializer.Serialize(key.Select(c => ValueCodec.Encode(row[c.Ordinal], c.Type)));
         return new ResultRowDto(id, [.. schema.Columns.Select(c => ValueCodec.Encode(row[c.Ordinal], c.Type))]);
      }).ToList();
      return new QueryPageDto(composed.Text, PagedRows.Parameters(composed.Parameters), request.IncludeSchema ? Schema(schema, canEdit) : null, dtos, grid.Offset,
         more, total, Stats(stats), Warnings(request.Text, shape, page));
   }

   /// <summary>
   /// The query's warnings, placed in the text as written: binding's from the query itself; planning's (a large fetch)
   /// from the page as it ran, about the whole text.
   /// </summary>
   private static List<DiagnosticDto> Warnings(string text, PreparedQuery shape, PreparedQuery page)
   {
      static bool Planning(QueryDiagnostic d) => d.Code.StartsWith("GDQ3", StringComparison.Ordinal);
      return
      [
         .. shape.Diagnostics.Where(d => !d.IsError && !Planning(d)).Select(DiagnosticDto.From),
         .. page.Diagnostics.Where(d => !d.IsError && Planning(d)).Select(d => DiagnosticDto.From(d with { Start = 0, End = text.Length })),
      ];
   }

   /// <summary>How the query (or, with a grid, the page the grid would fetch) would run, without running it.</summary>
   public async Task<QueryExplainDto> ExplainAsync(QueryExplainRequest request, bool canEdit, HttpResponse response, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(request);
      QuerySettings settings = options.Value.Query;
      QueryParameters parameters = Parameters(request.Parameters);
      PageRequest? paging = null;
      if (request.Grid is { } grid)
      {
         if (grid.Limit > settings.MaxPageSize) { throw Invalid("grid.limit", $"A page has at most {settings.MaxPageSize} rows"); }
         paging = new PageRequest(grid.Offset, (grid.Limit ?? Math.Min(BrowseService.DefaultPageSize, settings.MaxPageSize)) + 1L);
      }
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      // A query that binds is explained even when it can't be planned: the explanation says why.
      (PreparedQuery prepared, ComposedQuery composed, _) = Prepare(engines.For(state), request.Text, parameters, request.Grid, paging, planned: false);
      QueryExplain explain = prepared.Explain(request.Verbose);
      List<ExplainNodeDto> nodes = [];
      int? root = explain.Plan == null ? null : Flatten(explain.Plan, nodes);
      return new QueryExplainDto(composed.Text, PagedRows.Parameters(composed.Parameters), explain.Summary, [.. explain.Diagnostics.Select(DiagnosticDto.From)],
         explain.Schema == null ? null : Schema(explain.Schema, canEdit), root, nodes,
         [.. explain.Fragments.Select(f => new ExplainFragmentDto(f.Source, f.Dialect, f.Sql, f.Parameters, f.Strategy, f.Table, f.EstimatedRows, f.BindJoinTemplate,
            f.BindJoinParameters))],
         explain.MergeSql, explain.MergeParameters, explain.Phases, ExplainTextRenderer.Render(explain));
   }

   /// <summary>Where a value of a page's row leads: the query of its rows, and what to browse when a grid can.</summary>
   public async Task<QueryLinkDto> LinkAsync(QueryLinkRequest request, HttpResponse response, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(request);
      if ((request.Column == null) == (request.Related == null)) { throw Invalid("column", "Give the column whose link to follow, or the related rows: one of them"); }
      QueryParameters parameters = Parameters(request.Parameters);
      CatalogState state = await catalogs.GetAsync(response, cancellationToken);
      if (request.CatalogVersion is { } version && version != state.Version)
      {
         throw new ProblemResultException(ApiProblems.Result(StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict, "The catalog changed since the page was read",
            "Its columns may have moved: run the query again"));
      }
      PreparedQuery prepared = engines.For(state).Prepare(new QueryRequest(request.Text) { Parameters = parameters });
      if (!prepared.Success) { throw new ProblemResultException(PagedRows.QueryProblem(prepared.Diagnostics, request.Text)); }
      ResultSchema schema = prepared.Schema!;
      ColumnLink link;
      if (request.Column is int column)
      {
         if (column < 0 || column >= schema.Columns.Count) { throw Invalid("column", $"The rows have {schema.Columns.Count} columns, from 0"); }
         link = schema.Columns[column].Link ?? throw Invalid("column", $"'{schema.Columns[column].Name}' leads nowhere");
      }
      else
      {
         IReadOnlyList<ColumnLink> related = schema.RowIdentity?.Related ?? [];
         if (request.Related < 0 || request.Related >= related.Count) { throw Invalid("related", $"The rows have {related.Count} kinds of related rows, from 0"); }
         link = related[request.Related!.Value];
      }
      if (request.Row.Count != schema.Columns.Count) { throw Invalid("row", $"The row has {request.Row.Count} values; the query's rows have {schema.Columns.Count}"); }
      object?[] values = new object?[schema.Columns.Count];
      Dictionary<string, string[]> errors = [];
      foreach (int ordinal in link.ValueOrdinals)
      {
         try
         {
            values[ordinal] = ValueCodec.Decode(ValueCodec.Json(request.Row[ordinal]), schema.Columns[ordinal].Type);
         }
         catch (ValueFormatException e)
         {
            errors[$"row[{ordinal}]"] = [e.Message];
         }
      }
      if (errors.Count > 0) { throw new ProblemResultException(ApiProblems.Invalid(errors)); }

      // A drill-down's rows are the query's own before it grouped, which may use its parameters (not the grid's).
      QueryRequest? query = link is DrillDownLink drill ? link.Query(values, Used(parameters, drill)) : link.Query(values);
      if (query == null) { return new QueryLinkDto(null, [], null, null); }
      (BrowseSourceDto? browse, IReadOnlyList<object?>? row) = Browsable(link, values);
      return new QueryLinkDto(query.Text, PagedRows.Parameters(query.Parameters ?? QueryParameters.Empty), browse, row);
   }

   /// <summary>
   /// The query with the grid's state composed onto it, prepared (paged when <paramref name="paging"/> says), and the
   /// query as written; its problems when it can't run, or (unless <paramref name="planned"/>) can't bind.
   /// </summary>
   private static (PreparedQuery Prepared, ComposedQuery Composed, PreparedQuery Shape) Prepare(QueryEngine engine, string text, QueryParameters parameters,
                                                                                               GridStateDto? grid, PageRequest? paging, bool planned = true)
   {
      bool Fine(PreparedQuery query) => query.Success || (!planned && query.Plan != null);
      PreparedQuery shape = engine.Prepare(new QueryRequest(text) { Parameters = parameters });
      if (!Fine(shape)) { throw new ProblemResultException(PagedRows.QueryProblem(shape.Diagnostics, text)); }
      ComposedQuery composed = new(text, parameters, null);
      if (grid != null)
      {
         Dictionary<string, string[]> errors = [];
         composed = GridQueryComposer.Compose(text, parameters, shape.Schema!.VisibleColumns, grid, errors) ?? throw new ProblemResultException(ApiProblems.Invalid(errors));
      }
      if (paging == null && ReferenceEquals(composed.Text, text)) { return (shape, composed, shape); }
      PreparedQuery prepared = engine.Prepare(new QueryRequest(composed.Text) { Parameters = composed.Parameters, Paging = paging });
      if (!Fine(prepared))
      {
         throw new ProblemResultException(PagedRows.QueryProblem(prepared.Diagnostics, composed.Text, composed.WhereStart, grid?.Where?.TrimEnd()));
      }
      return (prepared, composed, shape);
   }

   /// <summary>The parameters a drill-down's query uses.</summary>
   private static QueryParameters Used(QueryParameters parameters, DrillDownLink drill)
   {
      HashSet<string> names = [.. QueryText.Parameters(drill.QueryText + " " + string.Join(" ", drill.Keys.Select(k => k.Expression)))];
      QueryParameters used = new();
      foreach (QueryParameter parameter in parameters.All.Where(p => names.Contains(p.Name))) { used.Add(parameter.Name, parameter.Value, parameter.Type); }
      return used;
   }

   /// <summary>What a grid can browse for the rows a link leads to: one row of an entity, by its key; or a navigation from a row, by its key.</summary>
   private static (BrowseSourceDto?, IReadOnlyList<object?>?) Browsable(ColumnLink link, object?[] values)
   {
      switch (link)
      {
         case RowLink row when row.Target.Key is { } key && SameColumns(key.Columns, row.TargetColumns):
            return (new BrowseSourceDto { Entity = row.Target.DisplayName },
               [.. key.Columns.Select(c => ValueCodec.Encode(values[row.KeyOrdinals[IndexOf(row.TargetColumns, c)]], c.Type))]);
         case CollectionLink collection when collection.Navigation.Owner.Key is { } key && SameColumns(key.Columns, collection.Navigation.OwnerColumns):
            List<object?> owner = [.. key.Columns.Select(c => ValueCodec.Encode(values[collection.ValueOrdinals[IndexOf(collection.Navigation.OwnerColumns, c)]], c.Type))];
            return (new BrowseSourceDto { From = new BrowseFromDto(collection.Navigation.Owner.DisplayName, owner), Navigation = collection.Navigation.Name }, null);
         default:
            return (null, null);
      }
   }

   private static bool SameColumns(IReadOnlyList<ColumnDef> a, IReadOnlyList<ColumnDef> b) => a.Count == b.Count && a.All(b.Contains);

   private static int IndexOf(IReadOnlyList<ColumnDef> columns, ColumnDef column)
   {
      for (int i = 0; i < columns.Count; i++)
      {
         if (columns[i] == column) { return i; }
      }
      return -1;
   }

   private ResultSchemaDto Schema(ResultSchema schema, bool canEdit)
   {
      Dictionary<EntityDef, (SqlDialect? Dialect, CapabilitiesDto Capabilities)> entities = [];
      (SqlDialect?, CapabilitiesDto) Of(EntityDef entity)
      {
         if (!entities.TryGetValue(entity, out var found))
         {
            SqlDialect? dialect = entity is TableEntity table ? providers.For(table.Source.ProviderKind).Dialect : null;
            entities[entity] = found = (dialect, EntityCapabilities.Of(entity, dialect, canEdit));
         }
         return found;
      }

      List<ResultColumnDto> columns = [];
      foreach (ResultColumn column in schema.Columns)
      {
         EditTargetDto? target = null;
         if (column.EditTarget is { } edit)
         {
            (SqlDialect? dialect, CapabilitiesDto capabilities) = Of(edit.Entity);
            ColumnCapabilities can = EntityCapabilities.Of(edit.Column, dialect, capabilities);
            target = new EditTargetDto(edit.Entity.DisplayName, edit.Column.Name, edit.KeyOrdinals, can.CanUpdate, can.Reason);
         }
         columns.Add(new ResultColumnDto(column.Name, column.Ordinal, TypeDto.Of(column.Type), column.IsHidden, PagedRows.Lineage(column.Lineage), Link(column.Link),
            target));
      }
      RowIdentityDto? identity = schema.RowIdentity is { } rows
         ? new RowIdentityDto(rows.Entity.DisplayName, rows.KeyOrdinals, [.. rows.Related.Select(r => Link(r)!)], Of(rows.Entity).Item2)
         : null;
      return new ResultSchemaDto(columns, identity);
   }

   private static ColumnLinkDto? Link(ColumnLink? link) => link switch
   {
      RowLink row => new ColumnLinkDto(LinkKind.Row, row.KeyOrdinals, row.Target.DisplayName, row.Navigation?.Name, row.Navigation?.Multiplicity),
      CollectionLink collection => new ColumnLinkDto(LinkKind.Collection, collection.ValueOrdinals, collection.Target.DisplayName, collection.Navigation.Name,
         collection.Navigation.Multiplicity),
      DrillDownLink drill => new ColumnLinkDto(LinkKind.DrillDown, drill.ValueOrdinals, null, null, null),
      _ => null,
   };

   private static QueryStatsDto Stats(ExecutionStats stats) =>
      new(stats.Elapsed.TotalMilliseconds, stats.FetchedRows, stats.KeysSent,
         [.. stats.Fragments.Select(f => new FragmentStatsDto(f.Source, f.Table, f.Rows, f.Elapsed.TotalMilliseconds, f.Strategy, f.Keys, f.Batches))]);

   /// <summary>Adds a node of a plan and those under it, inputs first; its id.</summary>
   private static int Flatten(ExplainNode node, List<ExplainNodeDto> nodes)
   {
      List<int> inputs = [.. node.Inputs.Select(i => Flatten(i, nodes))];
      List<ExplainSubqueryDto> subqueries = [.. node.Subqueries.Select(s => new ExplainSubqueryDto(s.Number, s.Kind, Flatten(s.Plan, nodes)))];
      nodes.Add(new ExplainNodeDto(nodes.Count, node.Operator, node.Detail, node.Site, node.Columns, node.EstimatedRows, inputs, subqueries));
      return nodes.Count - 1;
   }

   /// <summary>The values given for parameters; what is wrong with them, by field, is a <see cref="ProblemResultException"/>.</summary>
   private static QueryParameters Parameters(IReadOnlyList<QueryParameterInput?>? inputs)
   {
      Dictionary<string, string[]> errors = [];
      QueryParameters parameters = QueryInputs.Parameters(inputs, "parameters", errors);
      return errors.Count == 0 ? parameters : throw new ProblemResultException(ApiProblems.Invalid(errors));
   }

   private static QueryParameters Copy(QueryParameters parameters)
   {
      QueryParameters copy = new();
      foreach (QueryParameter parameter in parameters.All) { copy.Add(parameter.Name, parameter.Value, parameter.Type); }
      return copy;
   }

   private static ProblemResultException Invalid(string field, string message) =>
      new(ApiProblems.Invalid(new Dictionary<string, string[]> { [field] = [message] }));
}

/// <summary>Values for a query's parameters, as requests give them.</summary>
public static class QueryInputs
{
   public static QueryParameters Parameters(IReadOnlyList<QueryParameterInput?>? inputs, string field, Dictionary<string, string[]> errors)
   {
      ArgumentNullException.ThrowIfNull(errors);
      QueryParameters parameters = new();
      if (inputs == null) { return parameters; }
      HashSet<string> seen = new(StringComparer.Ordinal);
      for (int i = 0; i < inputs.Count; i++)
      {
         string at = $"{field}[{i}]";
         if (inputs[i] is not { } input)
         {
            errors[at] = ["A parameter can't be null"];
            continue;
         }
         string name = input.Name.StartsWith('$') ? input.Name[1..] : input.Name;
         if (!IsName(name))
         {
            errors[at + ".name"] = [$"'{input.Name}' isn't a parameter's name, such as $min"];
            continue;
         }
         if (!seen.Add(name))
         {
            errors[at + ".name"] = [$"${name} is given twice"];
            continue;
         }
         JsonElement value = ValueCodec.Json(input.Value);
         if (input.Type is { } typeText)
         {
            if (!ScalarType.TryParse(typeText.Trim(), out ScalarType type))
            {
               errors[at + ".type"] = [$"'{typeText}' isn't a type: use int32, int64, decimal(12,2), double, string, boolean, date, datetime, datetimeoffset, time, guid, ..."];
               continue;
            }
            try
            {
               object? decoded = ValueCodec.Decode(value, type);
               if (decoded != null && type.Kind == ScalarKind.Unknown)
               {
                  errors[at + ".value"] = ["A value of no type can't be given: leave the type out, or give one"];
                  continue;
               }
               parameters.Add(name, decoded, decoded == null ? type.WithNullable(true) : type);
            }
            catch (ValueFormatException e)
            {
               errors[at + ".value"] = [e.Message];
            }
            continue;
         }
         if (Untyped(value, out object? untyped)) { parameters.Add(name, untyped); }
         else { errors[at + ".value"] = ["A parameter's value is one value: text, a number, true, false or null"]; }
      }
      return parameters;
   }

   /// <summary>A JSON value as the command line types one: a boolean, a whole number (int64), another number (decimal, or double past one), text, or null.</summary>
   private static bool Untyped(JsonElement value, out object? clr)
   {
      clr = null;
      switch (value.ValueKind)
      {
         case JsonValueKind.Null or JsonValueKind.Undefined:
            return true;
         case JsonValueKind.True or JsonValueKind.False:
            clr = value.GetBoolean();
            return true;
         case JsonValueKind.String:
            clr = value.GetString();
            return true;
         case JsonValueKind.Number when value.TryGetInt64(out long whole):
            clr = whole;
            return true;
         case JsonValueKind.Number when decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number):
            clr = number;
            return true;
         case JsonValueKind.Number when value.TryGetDouble(out double real) && double.IsFinite(real):
            clr = real;
            return true;
         default:
            return false;
      }
   }

   /// <summary>A name as the language reads one after a <c>$</c>: letters, digits, <c>_</c> and <c>$</c> (<c>$1</c> too).</summary>
   private static bool IsName(string name) => name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '$');
}
