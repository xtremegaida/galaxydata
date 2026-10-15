# Charts limited in memory, from one query of their whole rows

## Context

On the live HyperWhirlpool service a chart's time goes on round trips to the database, not on the rows it reads:

| Chart | Today | Fewer queries |
|---|---|---|
| Page views by hour × device, top 4 + Other | 6.3 s, 4 queries | 3.7 s with 2; about 1.6 s a query, whatever it returns |
| Browsers pie, top 8 + Other | 0.45 s, 2 queries | 0.24 s with 1; 50 slices cost what 8 do |
| A new step into Other | 0.4 s, 1 s, 6.2 s: the whole chain again | a step seen before: about 0.02 s (kept) |
| Sessions per day by entry page | 0.93 s, 3 queries | one query of every row overflows 10,000 rows |

The queries that limit a chart run one after another, because each needs what the one before found:

- bars by a series: categories → series ranked → bars → rest;
- lines limited to their largest series: series ranked → points → rest;
- pies with Other: slices → total.

**Decided with the user:**
- Read a chart's rows whole in one query, and limit them **on the server, in memory**: the same answer as today, and
  public dashboards still receive only the series shown.
- Fall back to today's queries when the rows overflow, or when the measure can't be combined from its groups.

## Approach

**What is read whole.** The chart's rows grouped by its dimension (and series), every group:

```
rows.groupBy(d0: …[, s0: …]).select(d0, [s0,] m0: …).orderBy(d0[, s0]).take(MaxWholeRows + 1)
```

Ordered by the keys (guids left out, as `Ties` does), so the database's order of the categories is kept for ties
and sorts by the dimension.

**When.** Only where it saves queries:
- a pie with Other (2 queries → 1);
- bars by a series, limited or not (2–4 → 1);
- lines limited to their largest series (3 → 1).

Bars and lines without a series, pies without Other, and tables are one query already, and stay so.

**Measures that combine.** Each group's measure combines into a coarser group's: count, count of values and sum add up;
min and max of numbers and dates take the least or greatest. Not average, distinct counts, or min/max of text (whose
order is the database's collation). A chart of those runs today's queries.

**In memory, each of today's queries is worked out from the whole rows**, so what follows them is unchanged:

| Query | From the whole rows |
|---|---|
| categories (bars) | by `d0`, the measure combined; by the chart's sort (ties in the rows' order); the first limit + 1 |
| series ranked | of the categories found; by `s0`, combined; largest first, ties by `s0` (guids as text); skip + size + 1 |
| main | bars: of the categories and the series shown, up to `MaxChartRows + 1`; lines: of the series shown, the take; pie: largest first, ties in the rows' order |
| rest | of the categories, the series not ranked before; by `d0`, combined; lines: the take |
| total (pie) | every slice combined |

**Kept once for every step.** The whole rows are kept in `WidgetResults` under their own key, which has no page, so
each step into Other and each step back reads them from memory. An entry's size grows with its rows (1 + rows / 1,000),
so large ones count against `CacheSize`.

**Overflow.** More than `MaxWholeRows` rows: the chart runs today's queries, and the overflow is remembered for an
hour, so the next load goes straight to them.

**Ties.** Slices and categories are ranked with ties broken in the database's order of `d0`; series with ties broken by
their characters' codes. On SQLite, DuckDB and ClickHouse that is the ranked plan's order. Elsewhere, only equal
measures at the edge of what is shown may break another way.

## Server (`src/GalaxyData.Web`)

1. **Settings** (`Hosting/GalaxyDataOptions.cs`):
   - `DashboardSettings.MaxWholeRows` (`[Range(0, 1_000_000)]`, 20,000; 0 turns it off).
   - `PlanLimits` gains a trailing `int MaxWholeRows = 0`, so `DashboardChecks` and the query goldens plan as now.
   - `WidgetRunner.Limits` passes the setting.
2. **Plan** (`Dashboards/WidgetPlanner.cs`):
   - `enum Combine { Sum, Min, Max }`.
   - `WholePlan(BuiltQuery Query, Combine Combine, WidgetSort? Categories, int MainTake, int? RestTake)`, and
     `WidgetPlan.Whole` (init-only).
   - `PlanContext.Combines(measure, type)`: the combine of a measure, or null.
3. **Kinds** (`Dashboards/WidgetKinds.cs`): `PieKind`, `BarKind` and `LineKind` set `Whole` as above, through
   `GroupQueries.Whole(...)`.
4. **Runner** (`Dashboards/WidgetRunner.cs`):
   - `RunAsync` gives `Fetched(Rows, Columns)`: names and types, not the engine's `ResultSchema`.
   - `IChartRows` (`CategoriesAsync`, `RankingAsync`, `MainAsync`, `RestAsync`, `TotalAsync`), with two
     implementations: `EngineRows` (today's queries) and `WholeRows` (from the whole rows, new file
     `Dashboards/WholeRows.cs`).
   - `FindAsync` and `ComputeAsync` read through it; nothing after them changes.
   - `QueryAsync` shows the whole query as the main one when it would run (`Categories`, `Series`, `Rest` and
     `Total` none). The answers' shapes don't change, so there is no OpenAPI change.
5. **Results** (`Dashboards/WidgetResults.cs`):
   - `GetAsync` takes an optional size of the value.
   - `Remember(key, duration)` and `Remembers(key)` for the overflow.
6. **Combining** (in `WholeRows`):
   - Sums in `decimal` for whole numbers and decimals, in `double` for floats, as the column's type.
   - A value of another type, or a sum that overflows, falls back to today's queries.

## Tests

- **`Dashboards/WholeRowsTests.cs`**, through the API over the shop fixture: every case is run with
  `MaxWholeRows = 0` and with the default, and the answers must be equal (all but `at` and `cached`). Cases:
  - bars by a series: unlimited; limited with and without Other; at steps 0, 1 and 2; sorted by the measure and by
    the dimension, ascending and descending; with null categories and series; with min and max;
  - lines limited, with Other, stepped;
  - pies with Other, stepped.
- **One query.** `/query` shows the whole query alone; with avg, today's queries.
- **Overflow.** With `MaxWholeRows = 2`, the same answers; after the first, `/query` shows today's queries.
- **Steps read the rows kept.** Load step 0, add rows to the SQLite file that would change the ranking, then step to
  1: the answer is of the rows read first. After the cache's time, or with `refresh`, it is of the new rows.
- **Goldens:** a `WidgetQueryTests` case prints the whole query of a bar, line and pie (with limits enabling it);
  existing snapshots don't change.
- **`HostTests`:** the OpenAPI snapshot doesn't change.

## Verification

- Server tests:
  ```
  MSBUILDCOPYTASKPARALLELISM=1 dotnet test --solution GalaxyData.slnx
  ```
  Client tests are unchanged, as the API is.
- On the live service (`server-hw-live`): rerun `time_variants.py` and `time_steps.py`. Expect one query for the
  limited charts, steps in tens of milliseconds, and the high-cardinality chart falling back, then remembered. Check
  in the browser that the dashboard's charts look as they did.
- `docs/server.md`: the "Running" paragraph (whole rows, combining, fallback, ties) and the settings table.
