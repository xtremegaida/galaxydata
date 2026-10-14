# Dashboards: a KPI (scorecard) widget

## Context

The Web analytics dashboard over HyperWhirlpool (`docs/examples/dashboards/web-analytics.dashboard.json`) has no way
to show a headline number: a grouped table needs a dimension, so "Sessions: 748K, +12% on the 30 days before" can't
be drawn. Its gap list put the KPI tile first (and period-over-period comparison, and "active now", which need it).

A KPI widget shows:
- **one big number:** a measure over the widget's rows, after the filters, the choices it listens to, and its own
  conditions;
- **a comparison, optional:** the same measure over the period before the one a relative filter of the dashboard's
  holds (the absolute and the % change), coloured good or bad by a direction the widget says;
- **a sparkline, optional:** the measure by a period (day, week, month, quarter, year) of a date field.

**Decided**
- The comparison names its filter (`compare.filter`): a dashboard may have more than one relative filter, on
  different sources. Only a `relative` filter may be named (a 400 otherwise). The period before is worked out from the
  value that holds for the viewer (theirs, when the filter is editable), so it follows the filter bar.
- No period set (the viewer cleared it) means nothing to compare with: no comparison, quietly. A filter that never
  reaches the widget (in its `except`, or on a source not linked to it) is a warning of the widget's.
- A KPI has no slices: `emits` must be false.
- The sparkline is always by a period (`bucket` one of `day`…`year`), so its points are dates; the latest 500 are
  drawn (`truncated` says there were more).

## Approach

- **The period before** (`ConditionWriter.Before`), from the relative period's days `[start, end)`:
  - `last n` and `previous n`: the n periods just before, `[start − n units, start)`;
  - `this`: the one before, `[start − 1 unit, start)`;
  - `to date`: the period before, up to the same day of it (`Period(toDate, today − 1 unit)`: this month to the 14th
    is last month to its 14th, or its last day when it has fewer).
- **Written as the period is.** `ConditionSpec.Before` makes `ConditionWriter` write a relative condition's period
  before; `ConditionSet.AddFilters(before: id)` writes that filter so and records the days it wrote
  (`ConditionSet.Moved`). The previous query is therefore the main query's text with other dates.
- **Queries.** Main is `rows.groupBy().select(m0: …)` (one row). The previous is the same over the rows the moved
  filter keeps. The sparkline is `rows.groupBy(d0: startOfX(f)).select(d0, m0).orderBy(desc(d0)).take(501)`,
  reversed by the runner.

## Server (`src/GalaxyData.Web`)

1. **Definition** (`Dashboards/Definition.cs`): `[JsonDerivedType(typeof(KpiConfig), "kpi")]`;
   `KpiConfig(Source, Conditions, Emits, Listens, Measure Measure, KpiCompare? Compare, Dimension? Sparkline)`;
   `KpiCompare(string Filter, KpiGood Good)`; `enum KpiGood { Up, Down, Neither }`.
2. **Rules:**
   - `DefinitionCheck.FilterKinds` (filled as filters are checked, before widgets).
   - `KpiKind.Check`: `Data`; `.emits` true; `Measure`; `.compare.filter` not a filter, or not a relative one;
     `.sparkline` a dimension, `.sparkline.bucket` a period.
3. **Planning:**
   - `ConditionSpec.Before`, `ConditionWriter.Before(period, today)`.
   - `ConditionSet.AddFilters(except, before)` and `Moved`.
   - `PlanContext.Before(filter, path)` gives `PeriodRows(Rows, Parameters, From, To)`, or null (with the warning when
     the filter can't reach the widget). Called only when nothing is wrong yet, so no error is said twice.
   - `WidgetPlan` gains init-only `Previous` (`PreviousPlan(Query, From, To)`) and `Sparkline`
     (`SparklinePlan(Query, Limit)`).
   - `KpiKind.Plan`; `WidgetKinds.BuiltIn` lists it.
4. **Runner** (`WidgetRunner.cs`):
   - `WidgetDataDto` appends `Previous` (`{value, from, to}`, the last day included) and `Points` (`[[day, value]]`,
     oldest first), each written only when set.
   - `WidgetQueryDto` appends `Previous` and `Sparkline`.
   - `ComputeAsync` runs them after the main; the cache key adds them.
5. **Public dashboards** (`PublicDashboards.Strip`): a KPI loses its source, conditions and fields; `compare.filter`
   is blanked when it names a hidden filter (which a public copy hasn't got).

## Client (`src/client/src/app/features/dashboards`)

1. **API types:** accept the OpenAPI snapshot, `npm run api`.
2. **Model:** `KpiConfig` in `definition.ts` (`DataConfig`, not a chart); `kpiDefaults` (emits false);
   `overTime` gives a new KPI a sparkline over its source's time column, by day; the editor's add gives it a
   comparison with the first relative filter. `complete.ts`, `describe.ts`, `slice.ts`, `rows-table.ts` handle it.
3. **Registry:** `kpi`, "KPI", `speed` (icon), 3 × 3, at least 160 px.
4. **`widgets/kpi-widget.ts`:**
   - the label (the measure's, when the widget has no title), the value (`formatNumber`, the measure's format);
   - the change: an arrow icon, the % change (none when the period before is 0), the absolute change in the
     measure's format, "vs 15 Aug – 13 Sep"; `--gd-good` / `--gd-bad` by `compare.good` (neither: plain text), and
     an `aria-label` that says it in words ("up 12.3%, 81.2K more than 15 Aug – 13 Sep: good");
   - the sparkline: an SVG path (`vector-effect: non-scaling-stroke`, so it stretches to its box without a
     ResizeObserver), the latest point marked; counts and sums fill missing periods with zero; a hover and
     keyboard (arrows, Home, End) readout of a point's period and value; hidden by a container query when the tile
     is too short.
5. **Editor** (`editor/kinds/kind-settings.ts`): `KpiSettings` with the measure, its number format (a new
   `controls/number-format-editor.ts`: decimals, prefix, suffix, shortened), the comparison (a relative filter of
   the draft's, and which way is good), and the sparkline (a date field and its period).
6. **Data dialog:** a KPI's points are listed under its value.
7. **Query dialog:** tabs "The period before" and "Its sparkline".
8. **Styles:** `--gd-good`, `--gd-bad` (text contrast ≥ 4.5:1 on both surfaces).

## Tests

- **`DefinitionRulesTests`:** a KPI passes and round-trips; `.emits`, `.compare.filter` (none, not relative),
  `.sparkline.bucket` (a part that repeats), `.measure` refused.
- **`WidgetQueryTests.AKpiIsOneNumberComparedWithThePeriodBefore`** (own snapshot): the number alone; compared over
  the last 30 days, the previous month, the month to date; a viewer's period; no period; excepted; not linked;
  through a link; a sparkline by day and by month; a sparkline over text (an issue).
- **`DashboardDataApiTests`:** the KPI's value, `previous` and `points`; `/query` texts; cached.
- **`PublicDashboardApiTests`:** the stripped KPI.
- **Client:** the widget (value, change and its colour by direction, no % against zero, sparkline points and gaps,
  readout); `KpiSettings` (format, comparison, sparkline); the editor adds a KPI with its defaults.

## Verification

- `MSBUILDCOPYTASKPARALLELISM=1 dotnet test --solution GalaxyData.slnx`; new goldens accepted after reading them.
- `npm --prefix src/client test`, `npm --prefix src/client run lint`.
- The Overview tab of the Web analytics dashboard gains a row of four (sessions, visitors, page views, forms
  started), checked in the browser against `server-hw-live`.
