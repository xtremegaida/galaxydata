using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Results;
using GalaxyData.Query.Types;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Dashboards;

/// <summary>A column of a widget's rows: its name in the query, its role and index among its role's, its label, its type, and how numbers show.</summary>
public sealed record WidgetColumnDto(string Name, ColumnRole Role, int Index, string Label, TypeDto Type, NumberFormat? Format);

/// <summary>
/// A widget's rows, as positional arrays of its columns' values (<c>ValueCodec</c>'s: whole numbers of 64 bits and
/// decimals as text, exact). <see cref="Truncated"/>: there are more than are shown (rows, or a series' categories);
/// tables page instead (<see cref="Offset"/>, <see cref="Total"/> when counted). <see cref="Categories"/>: a series'
/// bars' categories in their order; <see cref="Other"/>: the rest of a pie's total. When the rows were worked out,
/// and whether they were kept from then for whoever asked first; warnings about the widget.
/// </summary>
public sealed record WidgetDataDto(IReadOnlyList<WidgetColumnDto> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated, long Offset, long? Total,
                                   IReadOnlyList<object?>? Categories, object? Other, DateTime RefreshedAt, bool Cached, IReadOnlyList<DashboardIssue> Issues);

/// <summary>A query's text and its parameters' values.</summary>
public sealed record QueryTextDto(string Text, IReadOnlyList<QueryParameterDto> Parameters);

/// <summary>A widget's queries, as they run: the categories found first (bars by a series), its rows, the total for "Other", and its underlying rows.</summary>
public sealed record WidgetQueryDto(QueryTextDto Main, QueryTextDto? Categories, QueryTextDto? Total, QueryTextDto Underlying);

/// <summary>A value a filter's field has, and how many rows have it.</summary>
public sealed record FilterValueDto(object? Value, long Rows);

/// <summary>The values a filter may take, most rows first; more there are.</summary>
public sealed record FilterValuesDto(TypeDto Type, IReadOnlyList<FilterValueDto> Values, bool More);

/// <summary>A page of a table's rows: from <see cref="Offset"/>, <see cref="Limit"/> of them (the table's page size when not given); counted when asked.</summary>
public sealed record WidgetPage(long Offset = 0, int? Limit = null, bool Count = false);

/// <summary>Whom a widget runs for: someone signed in, or anyone with a public link (whose limits are lower, and whose rows are always kept a while).</summary>
public enum RunMode
{
   SignedIn,
   Public,
}

/// <summary>
/// Runs dashboards' widgets: plans their queries for the state given, runs them (the categories first, for bars by
/// a series; the total, for a pie's "Other"), and keeps what they give a while (<see cref="WidgetResults"/>). What is
/// wrong is a problem: the state's by field (400), the widget's issues (422 <c>widget-invalid</c>), no such widget
/// (404); the engine's are the application's.
/// </summary>
public sealed class WidgetRunner(CatalogService catalogs, QueryEngines engines, WidgetKinds kinds, WidgetResults results, TimeProvider clock, IOptions<GalaxyDataOptions> options)
{
   private DashboardSettings Settings => options.Value.Dashboards;

   public PlanLimits Limits(RunMode mode) => mode == RunMode.Public
      ? new PlanLimits(Settings.MaxPublicSelectionKeys, Settings.MaxPublicFilterValues, Settings.MaxChartRows, Public: true)
      : new PlanLimits(Settings.MaxSelectionKeys, Settings.MaxFilterValues, Settings.MaxChartRows, Public: false);

   /// <summary>The widget's rows for the state given.</summary>
   public async Task<WidgetDataDto> DataAsync(DashboardDefinition definition, string widget, DashboardState? state, WidgetPage? page, bool refresh, RunMode mode,
                                              HttpResponse? response, CancellationToken cancellationToken, Func<CancellationToken, Task<IAsyncDisposable>>? gate = null)
   {
      ArgumentNullException.ThrowIfNull(definition);
      CatalogState catalog = await CatalogAsync(response, cancellationToken);
      (WidgetPlan plan, IReadOnlyList<DashboardIssue> warnings) = Plan(definition, widget, state, mode, catalog);
      QueryEngine engine = engines.For(catalog);
      TimeSpan timeout = mode == RunMode.Public ? Settings.PublicWidgetTimeout : Settings.WidgetTimeout;
      long offset = plan.Paged ? Math.Max(page?.Offset ?? 0, 0) : 0;
      int limit = plan.Paged ? Math.Clamp(page?.Limit ?? plan.Limit, 1, TableKind.MaxPageSize) : plan.Limit;
      bool count = plan.Paged && page?.Count == true;
      string key = Key(catalog.Version, "data", plan.Categories, plan.Main(null), plan.Total, $"{offset}/{limit}/{count}");
      TimeSpan duration = mode == RunMode.Public ? Settings.PublicCacheDuration : Settings.CacheDuration;
      (Computed computed, bool cached, DateTime at) = await results.GetAsync(key, duration, refresh && mode == RunMode.SignedIn,
         async ct =>
         {
            // Only queries that run wait their turn: answers kept are given at once.
            await using IAsyncDisposable? turn = gate == null ? null : await gate(ct);
            return await ComputeAsync(engine, plan, timeout, offset, limit, count, catalog, ct);
         }, cancellationToken);
      if (mode == RunMode.Public && computed.Columns.Any(c => c.Role == ColumnRole.Key))
      {
         // A raw table's key is for choosing its rows, which a public dashboard doesn't.
         List<int> shown = [.. computed.Columns.Select((c, i) => (c, i)).Where(p => p.c.Role != ColumnRole.Key).Select(p => p.i)];
         computed = computed with
         {
            Columns = [.. shown.Select(i => computed.Columns[i])],
            Rows = [.. computed.Rows.Select(r => (IReadOnlyList<object?>)[.. shown.Select(i => r[i])])],
         };
      }
      return new WidgetDataDto(computed.Columns, computed.Rows, computed.Truncated, offset, computed.Total, computed.Categories, computed.Other, at, cached, warnings);
   }

   /// <summary>The widget's queries for the state given, as they run (the categories found, for bars by a series).</summary>
   public async Task<WidgetQueryDto> QueryAsync(DashboardDefinition definition, string widget, DashboardState? state, HttpResponse? response, CancellationToken cancellationToken)
   {
      CatalogState catalog = await CatalogAsync(response, cancellationToken);
      (WidgetPlan plan, _) = Plan(definition, widget, state, RunMode.SignedIn, catalog);
      CategoryValues? categories = null;
      if (plan.Categories is { } first)
      {
         QueryEngine engine = engines.For(catalog);
         (List<object?[]> rows, ResultSchema schema) = await RunAsync(engine, first, null, Settings.WidgetTimeout, cancellationToken);
         categories = new CategoryValues([.. rows.Take(plan.Limit).Select(r => r[0])], schema.VisibleColumns[0].Type);
      }
      return new WidgetQueryDto(Text(plan.Main(categories)), plan.Categories is { } c ? Text(c) : null, plan.Total is { } t ? Text(t) : null, Text(plan.Underlying));
   }

   /// <summary>The values a filter may take, under every other condition that reaches its source; those holding <paramref name="search"/> when given.</summary>
   public async Task<FilterValuesDto> FilterValuesAsync(DashboardDefinition definition, string filter, DashboardState? state, string? search, RunMode mode,
                                                        HttpResponse? response, CancellationToken cancellationToken, Func<CancellationToken, Task<IAsyncDisposable>>? gate = null)
   {
      ArgumentNullException.ThrowIfNull(definition);
      CatalogState catalog = await CatalogAsync(response, cancellationToken);
      DashboardCatalog resolved = DashboardCatalog.Resolve(definition, catalog.Catalog);
      int index = definition.Filters.FindIndex(f => f.Id == filter);
      if (index < 0) { throw Problem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such filter", $"The dashboard has no filter {filter}"); }
      Dictionary<string, string[]> errors = [];
      PlanLimits limits = Limits(mode);
      BuiltQuery? query = new WidgetPlanner(resolved, state ?? DashboardState.Empty, limits, Today, errors).FilterValues(filter, search, FilterValuesMax);
      if (errors.Count > 0) { throw new ProblemResultException(ApiProblems.Invalid(errors)); }
      if (query == null) { throw Invalid(resolved.Issues.Where(i => i.Severity == IssueSeverity.Error), mode); }
      QueryEngine engine = engines.For(catalog);
      TimeSpan timeout = mode == RunMode.Public ? Settings.PublicWidgetTimeout : Settings.WidgetTimeout;
      TimeSpan duration = mode == RunMode.Public ? Settings.PublicCacheDuration : Settings.CacheDuration;
      (FilterValuesDto values, _, _) = await results.GetAsync(Key(catalog.Version, "values", null, query, null, string.Empty), duration, refresh: false, async ct =>
      {
         await using IAsyncDisposable? turn = gate == null ? null : await gate(ct);
         (List<object?[]> rows, ResultSchema schema) = await RunAsync(engine, query, null, timeout, ct);
         ScalarType type = schema.VisibleColumns[0].Type;
         return new FilterValuesDto(TypeDto.Of(type),
            [.. rows.Take(FilterValuesMax).Select(r => new FilterValueDto(ValueCodec.Encode(r[0], type), Convert.ToInt64(r[1], CultureInfo.InvariantCulture)))], rows.Count > FilterValuesMax);
      }, cancellationToken);
      return values;
   }

   public const int FilterValuesMax = 100;

   private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

   private Task<CatalogState> CatalogAsync(HttpResponse? response, CancellationToken cancellationToken) =>
      response == null ? catalogs.GetAsync(cancellationToken) : catalogs.GetAsync(response, cancellationToken);

   /// <summary>The widget's plan, and its warnings; a problem when it has none (no such widget, or no data) or can't be planned.</summary>
   private (WidgetPlan Plan, IReadOnlyList<DashboardIssue> Warnings) Plan(DashboardDefinition definition, string widget, DashboardState? state, RunMode mode, CatalogState catalog)
   {
      DashboardWidget? found = definition.Widgets.FirstOrDefault(w => w.Id == widget);
      if (found == null) { throw Problem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such widget", $"The dashboard has no widget {widget}"); }
      if (found.Config is not DataWidgetConfig) { throw Problem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "The widget has no rows", $"{widget} shows no data"); }
      DashboardCatalog resolved = DashboardCatalog.Resolve(definition, catalog.Catalog);
      Dictionary<string, string[]> errors = [];
      WidgetPlan? plan = new WidgetPlanner(resolved, state ?? DashboardState.Empty, Limits(mode), Today, errors).Plan(widget, kinds);
      if (errors.Count > 0) { throw new ProblemResultException(ApiProblems.Invalid(errors)); }
      List<DashboardIssue> mine = [.. resolved.Issues.Where(i => i.Widget == widget || i.Widget == null)];
      if (plan == null) { throw Invalid(mine.Where(i => i.Severity == IssueSeverity.Error), mode); }
      return (plan, mode == RunMode.Public ? [] : [.. mine.Where(i => i.Severity == IssueSeverity.Warning && i.Widget == widget)]);
   }

   /// <summary>422 <c>widget-invalid</c>, with the issues that stop the widget (signed in: what they are; in public: how many).</summary>
   private static ProblemResultException Invalid(IEnumerable<DashboardIssue> issues, RunMode mode)
   {
      List<DashboardIssue> list = [.. issues];
      ProblemDetails problem = ApiProblems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.WidgetInvalid, "The widget can't be shown",
         mode == RunMode.Public ? null : string.Join("; ", list.Select(i => i.Message)));
      if (mode == RunMode.SignedIn) { problem.Extensions["issues"] = list; }
      return new ProblemResultException(TypedResults.Problem(problem));
   }

   private static ProblemResultException Problem(int status, string code, string title, string detail) =>
      new(ApiProblems.Result(status, code, title, detail));

   private sealed record Computed(IReadOnlyList<WidgetColumnDto> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated, long? Total,
                                  IReadOnlyList<object?>? Categories, object? Other);

   private async Task<Computed> ComputeAsync(QueryEngine engine, WidgetPlan plan, TimeSpan timeout, long offset, int limit, bool count, CatalogState catalog,
                                             CancellationToken cancellationToken)
   {
      bool truncated = false;
      CategoryValues? categories = null;
      IReadOnlyList<object?>? categoryValues = null;
      if (plan.Categories is { } first)
      {
         (List<object?[]> found, ResultSchema schema) = await RunAsync(engine, first, null, timeout, cancellationToken);
         truncated = found.Count > plan.Limit;
         ScalarType type = schema.VisibleColumns[0].Type;
         categories = new CategoryValues([.. found.Take(plan.Limit).Select(r => r[0])], type);
         categoryValues = [.. categories.Values.Select(v => ValueCodec.Encode(v, type))];
      }
      BuiltQuery main = plan.Main(categories);
      List<object?[]> rows;
      ResultSchema mainSchema;
      long? total = null;
      if (plan.Paged)
      {
         PreparedQuery prepared = Prepared(engine, main, new PageRequest(offset, limit + 1), timeout);
         ComposedQuery composed = new(main.Text, main.Parameters, null);
         FetchedPage page = await PagedRows.FetchAsync(engine, prepared, composed, limit, offset, count, options.Value.Query.CountTimeout, cancellationToken);
         rows = page.Rows;
         truncated = page.More;
         total = page.Total;
         mainSchema = prepared.Schema!;
      }
      else
      {
         (rows, mainSchema) = await RunAsync(engine, main, null, timeout, cancellationToken);
         int shown = plan.Categories == null ? plan.Limit : Settings.MaxChartRows;
         if (rows.Count > shown)
         {
            truncated = true;
            rows = rows[..shown];
         }
      }
      List<ResultColumn> visible = [.. mainSchema.VisibleColumns];
      List<WidgetColumnDto> columns = [];
      List<int> ordinals = [];
      foreach (PlannedColumn column in plan.Columns)
      {
         int ordinal = visible.FindIndex(c => string.Equals(c.Name, column.Name, StringComparison.Ordinal));
         if (ordinal < 0) { throw new InvalidOperationException($"The widget's query has no column {column.Name}"); }
         ordinals.Add(ordinal);
         columns.Add(new WidgetColumnDto(column.Name, column.Role, column.Index, column.Label, TypeDto.Of(visible[ordinal].Type), column.Format));
      }
      List<IReadOnlyList<object?>> encoded = [.. rows.Select(r => (IReadOnlyList<object?>)[.. ordinals.Select(o => ValueCodec.Encode(r[o], visible[o].Type))])];
      object? other = null;
      if (plan.Total is { } totalQuery)
      {
         (List<object?[]> all, ResultSchema schema) = await RunAsync(engine, totalQuery, null, timeout, cancellationToken);
         int measure = ordinals[plan.Columns.ToList().FindIndex(c => c.Role == ColumnRole.Measure)];
         other = Rest(all.Count == 1 ? all[0][0] : null, rows.Select(r => r[measure]), schema.VisibleColumns[0].Type);
      }
      return new Computed(columns, encoded, truncated, total, categoryValues, other);
   }

   /// <summary>The total less what the slices shown hold, of the total's type; null when there is nothing more.</summary>
   private static object? Rest(object? total, IEnumerable<object?> shown, ScalarType type)
   {
      if (total == null) { return null; }
      try
      {
         decimal rest = Convert.ToDecimal(total, CultureInfo.InvariantCulture) - shown.Where(v => v != null).Sum(v => Convert.ToDecimal(v, CultureInfo.InvariantCulture));
         if (rest <= 0) { return null; }
         return type.IsInteger ? ValueCodec.Encode((long)rest, ScalarType.Int64) : ValueCodec.Encode(rest, ScalarType.Decimal(38, Math.Min((int)type.Scale, 10)));
      }
      catch (OverflowException)
      {
         double rest = Convert.ToDouble(total, CultureInfo.InvariantCulture) - shown.Where(v => v != null).Sum(v => Convert.ToDouble(v, CultureInfo.InvariantCulture));
         return rest <= 0 ? null : ValueCodec.Encode(rest, ScalarType.Double);
      }
   }

   private static PreparedQuery Prepared(QueryEngine engine, BuiltQuery query, PageRequest? paging, TimeSpan timeout)
   {
      PreparedQuery prepared = engine.Prepare(new QueryRequest(query.Text) { Parameters = query.Parameters, Paging = paging, Timeout = timeout });
      if (!prepared.Success) { throw new QueryException(prepared.Diagnostics); }
      return prepared;
   }

   private static async Task<(List<object?[]> Rows, ResultSchema Schema)> RunAsync(QueryEngine engine, BuiltQuery query, PageRequest? paging, TimeSpan timeout,
                                                                                   CancellationToken cancellationToken)
   {
      PreparedQuery prepared = Prepared(engine, query, paging, timeout);
      await using QueryResult result = await prepared.ExecuteAsync(cancellationToken);
      return ([.. await result.ToListAsync(cancellationToken)], result.Schema);
   }

   private static QueryTextDto Text(BuiltQuery query) => new(query.Text, PagedRows.Parameters(query.Parameters));

   /// <summary>What the answer depends on: the catalog, and the queries with their parameters (by name, type and value, offsets in UTC), and the page.</summary>
   private static string Key(string catalog, string kind, BuiltQuery? first, BuiltQuery main, BuiltQuery? total, string page)
   {
      StringBuilder text = new();
      text.Append(catalog).Append('\u0001').Append(kind).Append('\u0001').Append(page);
      foreach (BuiltQuery? query in (BuiltQuery?[])[first, main, total])
      {
         text.Append('\u0002');
         if (query == null) { continue; }
         text.Append(query.Text);
         foreach (QueryParameter parameter in query.Parameters.All.OrderBy(p => p.Name, StringComparer.Ordinal))
         {
            object? value = parameter.Value is DateTimeOffset at ? at.ToUniversalTime() : parameter.Value;
            text.Append('\u0003').Append(parameter.Name).Append(':').Append(parameter.Type).Append('=').Append(ValueCodec.Json(ValueCodec.Encode(value, parameter.Type)).GetRawText());
         }
      }
      return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
   }
}
