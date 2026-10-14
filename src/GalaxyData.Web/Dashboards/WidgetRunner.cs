using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Results;
using GalaxyData.Query.Types;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Palettes;
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
/// bars' categories in their order, or a line's x values when its "Other" is drawn (some may be its alone);
/// <see cref="Other"/>: the rest of a pie's total. When the rows were worked out, and whether they were kept from then
/// for whoever asked first; warnings about the widget. <see cref="Colors"/> (public dashboards' charts): the colours
/// their palette's overrides give the labels in these rows, by the labels as they are here (signed in, the palette
/// comes whole with the dashboard instead).
/// <para>
/// A chart limited to its largest series: those shown, the largest first (<see cref="Series"/>), whether there are
/// more (<see cref="MoreSeries"/>), and their rest by the dimension (<see cref="OtherRows"/>: the dimension's value
/// and the measure's, as "Other" is drawn). A chart stepped into its "Other" has <see cref="Offset"/> slices or
/// series before those shown; <see cref="Next"/> is where its "Other" steps to (none: no further).
/// </para>
/// </summary>
public sealed record WidgetDataDto(IReadOnlyList<WidgetColumnDto> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated, long Offset, long? Total,
                                   IReadOnlyList<object?>? Categories, object? Other, DateTime RefreshedAt, bool Cached, IReadOnlyList<DashboardIssue> Issues,
                                   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<LabelColorDto>? Colors = null,
                                   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<object?>? Series = null,
                                   bool MoreSeries = false,
                                   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<IReadOnlyList<object?>>? OtherRows = null,
                                   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Next = null);

/// <summary>A query's text and its parameters' values.</summary>
public sealed record QueryTextDto(string Text, IReadOnlyList<QueryParameterDto> Parameters);

/// <summary>
/// A widget's queries, as they run: the categories found first (bars by a series), its rows, the total for "Other",
/// its underlying rows; and for a chart limited to its largest series, the query ranking them and their rest
/// ("Other", when there is one).
/// </summary>
public sealed record WidgetQueryDto(QueryTextDto Main, QueryTextDto? Categories, QueryTextDto? Total, QueryTextDto Underlying,
                                    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QueryTextDto? Series = null,
                                    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QueryTextDto? Rest = null);

/// <summary>A value a filter's field has, and how many rows have it.</summary>
public sealed record FilterValueDto(object? Value, long Rows);

/// <summary>The values a filter may take, most rows first; more there are.</summary>
public sealed record FilterValuesDto(TypeDto Type, IReadOnlyList<FilterValueDto> Values, bool More);

/// <summary>
/// A page of a table's rows: from <see cref="Offset"/>, <see cref="Limit"/> of them (the table's page size when not
/// given); counted when asked. Of a chart that steps into its "Other": <see cref="Offset"/> slices or series stepped
/// over (others ignore it).
/// </summary>
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
      ? new PlanLimits(Settings.MaxPublicSelectionKeys, Settings.MaxPublicFilterValues, Settings.MaxChartRows, Public: true, OtherDepth)
      : new PlanLimits(Settings.MaxSelectionKeys, Settings.MaxFilterValues, Settings.MaxChartRows, Public: false, OtherDepth);

   /// <summary>How deep "Other" may be stepped into: no deeper than a chart's rows.</summary>
   private int OtherDepth => Math.Min(Settings.MaxOtherDepth, Settings.MaxChartRows);

   /// <summary>The widget's rows for the state given.</summary>
   public async Task<WidgetDataDto> DataAsync(DashboardDefinition definition, string widget, DashboardState? state, WidgetPage? page, bool refresh, RunMode mode,
                                              HttpResponse? response, CancellationToken cancellationToken, Func<CancellationToken, Task<IAsyncDisposable>>? gate = null)
   {
      ArgumentNullException.ThrowIfNull(definition);
      CatalogState catalog = await CatalogAsync(response, cancellationToken);
      (WidgetPlan plan, IReadOnlyList<DashboardIssue> warnings) = Plan(definition, widget, state, mode, catalog, Skip(page));
      QueryEngine engine = engines.For(catalog);
      TimeSpan timeout = mode == RunMode.Public ? Settings.PublicWidgetTimeout : Settings.WidgetTimeout;
      // A chart that doesn't step into "Other" is at its first, whatever was asked.
      long offset = plan.Paged ? Math.Max(page?.Offset ?? 0, 0) : plan.Step?.Skip ?? 0;
      int limit = plan.Paged ? Math.Clamp(page?.Limit ?? plan.Limit, 1, TableKind.MaxPageSize) : plan.Limit;
      bool count = plan.Paged && page?.Count == true;
      string key = Key(catalog.Version, "data", $"{offset}/{limit}/{count}",
         plan.Categories, plan.Main(PlanFound.None), plan.Total, plan.Series?.Ranking(PlanFound.None), plan.Series?.Rest?.Invoke(PlanFound.None));
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
      return new WidgetDataDto(computed.Columns, computed.Rows, computed.Truncated, offset, computed.Total, computed.Categories, computed.Other, at, cached, warnings,
         Series: computed.Series, MoreSeries: computed.MoreSeries, OtherRows: computed.OtherRows, Next: computed.Next);
   }

   /// <summary>
   /// The widget's queries for the state given (a chart stepped into its "Other" as <paramref name="page"/> says), as
   /// they run: with the categories and series they find (bars by a series, the largest series).
   /// </summary>
   public async Task<WidgetQueryDto> QueryAsync(DashboardDefinition definition, string widget, DashboardState? state, WidgetPage? page, HttpResponse? response,
                                                CancellationToken cancellationToken)
   {
      CatalogState catalog = await CatalogAsync(response, cancellationToken);
      (WidgetPlan plan, _) = Plan(definition, widget, state, RunMode.SignedIn, catalog, Skip(page));
      QueryEngine engine = engines.For(catalog);
      (PlanFound found, _) = await FindAsync(engine, plan, Settings.WidgetTimeout, cancellationToken);
      PlanFound categories = new(found.Categories);
      BuiltQuery? rest = found.Series is { More: true } && plan.Series?.Rest is { } of ? of(found) : null;
      return new WidgetQueryDto(Text(plan.Main(found)), plan.Categories is { } c ? Text(c) : null, plan.Total is { } t ? Text(t) : null, Text(plan.Underlying),
         plan.Series is { } series ? Text(series.Ranking(categories)) : null, rest is { } r ? Text(r) : null);
   }

   /// <summary>The slices or series a page asks a chart to step over into its "Other".</summary>
   private static int Skip(WidgetPage? page) => (int)Math.Clamp(page?.Offset ?? 0, 0, int.MaxValue);

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
      (FilterValuesDto values, _, _) = await results.GetAsync(Key(catalog.Version, "values", string.Empty, null, query, null), duration, refresh: false, async ct =>
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
   private (WidgetPlan Plan, IReadOnlyList<DashboardIssue> Warnings) Plan(DashboardDefinition definition, string widget, DashboardState? state, RunMode mode, CatalogState catalog,
                                                                         int skip)
   {
      DashboardWidget? found = definition.Widgets.FirstOrDefault(w => w.Id == widget);
      if (found == null) { throw Problem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such widget", $"The dashboard has no widget {widget}"); }
      if (found.Config is not DataWidgetConfig) { throw Problem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "The widget has no rows", $"{widget} shows no data"); }
      DashboardCatalog resolved = DashboardCatalog.Resolve(definition, catalog.Catalog);
      Dictionary<string, string[]> errors = [];
      WidgetPlan? plan = new WidgetPlanner(resolved, state ?? DashboardState.Empty, Limits(mode), Today, errors).Plan(widget, kinds, skip);
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
                                  IReadOnlyList<object?>? Categories, object? Other, IReadOnlyList<object?>? Series = null, bool MoreSeries = false,
                                  IReadOnlyList<IReadOnlyList<object?>>? OtherRows = null, long? Next = null);

   /// <summary>What a plan's first queries find: the categories a series' bars are limited to (and whether there are more), then the series ranked.</summary>
   private static async Task<(PlanFound Found, bool MoreCategories)> FindAsync(QueryEngine engine, WidgetPlan plan, TimeSpan timeout, CancellationToken cancellationToken)
   {
      PlanFound found = PlanFound.None;
      bool more = false;
      if (plan.Categories is { } first)
      {
         (List<object?[]> rows, ResultSchema schema) = await RunAsync(engine, first, null, timeout, cancellationToken);
         more = rows.Count > plan.Limit;
         found = new PlanFound(new CategoryValues([.. rows.Take(plan.Limit).Select(r => r[0])], schema.VisibleColumns[0].Type));
      }
      if (plan.Series is { } limited)
      {
         (List<object?[]> rows, ResultSchema schema) = await RunAsync(engine, limited.Ranking(found), null, timeout, cancellationToken);
         found = found with { Series = new SeriesValues([.. rows.Select(r => r[0])], schema.VisibleColumns[0].Type, plan.Step?.Skip ?? 0, limited.Limit) };
      }
      return (found, more);
   }

   private async Task<Computed> ComputeAsync(QueryEngine engine, WidgetPlan plan, TimeSpan timeout, long offset, int limit, bool count, CatalogState catalog,
                                             CancellationToken cancellationToken)
   {
      (PlanFound found, bool truncated) = await FindAsync(engine, plan, timeout, cancellationToken);
      IReadOnlyList<object?>? categoryValues = found.Categories is { } categories ? [.. categories.Values.Select(v => ValueCodec.Encode(v, categories.Type))] : null;
      BuiltQuery main = plan.Main(found);
      List<object?[]> rows;
      List<object?[]> before = [];
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
         if (plan.Series == null && plan.Step is { Skip: > 0 } step)
         {
            // A pie stepped into its "Other": the slices before those shown are read for what "Other" holds, not shown.
            before = rows[..Math.Min(step.Skip, rows.Count)];
            rows = rows[before.Count..];
         }
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
      object? other = null;
      long? next = null;
      if (plan.Total is { } totalQuery)
      {
         (List<object?[]> all, ResultSchema schema) = await RunAsync(engine, totalQuery, null, timeout, cancellationToken);
         int measure = ordinals[plan.Columns.ToList().FindIndex(c => c.Role == ColumnRole.Measure)];
         other = Rest(all.Count == 1 ? all[0][0] : null, before.Concat(rows).Select(r => r[measure]), schema.VisibleColumns[0].Type);
         next = plan.Step?.Next(truncated && other != null);
      }
      IReadOnlyList<IReadOnlyList<object?>>? otherRows = null;
      if (found.Series is { } series)
      {
         next = plan.Step?.Next(series.More);
         if (series.More && plan.Series?.Rest is { } rest)
         {
            (List<object?[]> restRows, ResultSchema restSchema) = await RunAsync(engine, rest(found), null, timeout, cancellationToken);
            List<ResultColumn> restColumns = [.. restSchema.VisibleColumns];
            int d = restColumns.FindIndex(c => c.Name == "d0");
            int m = restColumns.FindIndex(c => c.Name == "m0");
            if (plan.Categories == null)
            {
               // A line's x values: its points' and its rest's, some of which may be the rest's alone.
               int x = ordinals[plan.Columns.ToList().FindIndex(c => c.Role == ColumnRole.Dimension)];
               bool restCut = restRows.Count > plan.Limit;
               if (restCut) { restRows = restRows[..plan.Limit]; }
               (List<object?> axis, object? cut, bool isCut) = Axis(rows.Select(r => r[x]), truncated, restRows.Select(r => r[d]), restCut);
               if (isCut)
               {
                  truncated = true;
                  rows = [.. rows.Where(r => Compare(r[x], cut) <= 0)];
                  restRows = [.. restRows.Where(r => Compare(r[d], cut) <= 0)];
               }
               categoryValues = [.. axis.Select(v => ValueCodec.Encode(v, visible[x].Type))];
            }
            otherRows = [.. restRows.Select(r => (IReadOnlyList<object?>)[ValueCodec.Encode(r[d], restColumns[d].Type), ValueCodec.Encode(r[m], restColumns[m].Type)])];
         }
      }
      List<IReadOnlyList<object?>> encoded = [.. rows.Select(r => (IReadOnlyList<object?>)[.. ordinals.Select(o => ValueCodec.Encode(r[o], visible[o].Type))])];
      IReadOnlyList<object?>? seriesValues = found.Series is { } ranked ? [.. ranked.Shown.Select(v => ValueCodec.Encode(v, ranked.Type))] : null;
      return new Computed(columns, encoded, truncated, total, categoryValues, other, seriesValues, found.Series?.More ?? false, otherRows, next);
   }

   /// <summary>
   /// A line's x values, its points' and its rest's, in order (each list is in the query's order); cut at the
   /// first last value of a list that was cut short, as what follows it may be missing from that list.
   /// </summary>
   private static (List<object?> Axis, object? Cut, bool IsCut) Axis(IEnumerable<object?> points, bool pointsCut, IEnumerable<object?> rest, bool restCut)
   {
      List<object?> mine = Distinct(points);
      List<object?> others = Distinct(rest);
      List<object?> axis = [];
      int i = 0;
      int j = 0;
      while (i < mine.Count || j < others.Count)
      {
         int order = i == mine.Count ? 1 : j == others.Count ? -1 : Compare(mine[i], others[j]);
         axis.Add(order <= 0 ? mine[i] : others[j]);
         if (order <= 0) { i++; }
         if (order >= 0) { j++; }
      }
      List<object?> lasts = [];
      if (pointsCut && mine.Count > 0) { lasts.Add(mine[^1]); }
      if (restCut && others.Count > 0) { lasts.Add(others[^1]); }
      if (lasts.Count == 0) { return (axis, null, false); }
      object? cut = lasts.Aggregate((a, b) => Compare(a, b) <= 0 ? a : b);
      return ([.. axis.Where(v => Compare(v, cut) <= 0)], cut, true);
   }

   private static List<object?> Distinct(IEnumerable<object?> values)
   {
      List<object?> distinct = [];
      foreach (object? value in values)
      {
         if (distinct.Count == 0 || Compare(distinct[^1], value) != 0) { distinct.Add(value); }
      }
      return distinct;
   }

   /// <summary>Two values of a column in the order the engine sorts them: nulls first, text by its characters' codes, others as they compare.</summary>
   private static int Compare(object? a, object? b)
   {
      if (a == null || b == null) { return a == null ? (b == null ? 0 : -1) : 1; }
      if (a is string x && b is string y) { return string.CompareOrdinal(x, y); }
      if (a.GetType() == b.GetType() && a is IComparable comparable) { return comparable.CompareTo(b); }
      return string.CompareOrdinal(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
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
   private static string Key(string catalog, string kind, string page, params BuiltQuery?[] queries)
   {
      StringBuilder text = new();
      text.Append(catalog).Append('\u0001').Append(kind).Append('\u0001').Append(page);
      foreach (BuiltQuery? query in queries)
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
