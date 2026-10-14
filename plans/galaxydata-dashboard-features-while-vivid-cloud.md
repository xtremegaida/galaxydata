# Dashboard charts: top N series with "Other", and stepping into "Other"

## Context

Working on a dashboard over a HyperWhirlpool source showed two gaps:

1. **Too many series.** A bar or line chart split by a series shows every series value, up to `Dashboards:MaxChartRows` (10,000 rows); nothing limits them. Pies already have a top N (`limit` + `other`). A chart split by a series should have one too: the N largest series by the measure over the chart's rows (the y-axis total for counts and sums), with the rest as one "Other" series.
2. **"Other" is a dead end.** The pie's Other slice can't be clicked (`key: null` in `chart-options.ts`). "Other" is where every smaller label goes, so clicking it should step the chart into the rest: skip the first N, show the next N, and let "Other" again hold what lies beyond. A small indicator lets the viewer go back one step, or back to the top.

**Decided with the user**
- The step is per chart and transient, like a table's page (`WidgetContext.offset`). It is not in the URL or `DashboardState`, and it resets on reload.
- Scope is the pie's Other slice and the new series Other on bar and line charts. Bar categories stay as they are: top N, no Other bar.

## Approach

The approach extends the existing **two-phase pattern** that bars-by-series use: find the top values in one query, then filter by a literal parameter list. The engine has no window functions, and `x in <subquery>` drops nulls.

- **The step travels as `WidgetPage.Offset`.** Every data endpoint already takes it, including the public GET (`?offset=`). Charts ignore it today, so the request shape doesn't change.
- **Pie at step `o` (limit N):**
  - Main becomes `…orderBy(desc(m0), ties).take(o+N+1)`. There is no `skip`, so the subtraction still works.
  - Rows shown are `[o..o+N]`.
  - Other = total − Σ rows `[0..o+N]`.
  - At `o = 0` the query text is identical to today's.
- **Series (bars and lines) with `seriesLimit` N at step `o`:**
  - **Ranking:** `rows[.where(categories)].groupBy(s0).select(s0, m0).orderBy(desc(m0), ties).take(o+N+1)`. Bars rank over the categories shown, so the order is what's displayed.
  - **Main:** the existing query with one more filter, `s0 in [shown]` (`or s0 == null` when null is shown).
  - **Rest**, when `seriesOther` is on and more series exist: rows whose series is not among the first `o+N`, using `ConditionWriter`'s null-safe not-in semantics. It is grouped by `d0` only and gives `[d0, m0]`.
  - Because the rest is a query rather than a subtraction, the series Other is correct for **every** aggregate (avg, min, max, distinct). Pies keep their count/sum-only rule.
- **The server gives the next step.** `next` is the offset Other steps to, or null when there's nothing more or `Dashboards:MaxOtherDepth` (default 1,000) is reached. The client never computes it.
- **Existing dashboards are unchanged.** The new config fields are optional and written only when set, so canonical JSON and hashes stay the same, and `seriesLimit` null still means every series.

## Server (`src/GalaxyData.Web`)

1. **Definition** (`Dashboards/Definition.cs`): append `[JsonIgnore(WhenWritingNull)] int? SeriesLimit = null` and `[JsonIgnore(WhenWritingDefault)] bool SeriesOther = false` to `BarConfig` (after `ColorBy`) and `LineConfig` (after `Palette`).
2. **Rules** (`DefinitionRules.cs`):
   - `MaxSeries = 100`.
   - `DefinitionCheck.SeriesLimit(series, limit, other, path)`, called from `BarKind.Check` and `LineKind.Check`, refuses:
     - `.seriesLimit` without a series, or outside 1–100;
     - `.seriesOther` without a limit.
3. **Settings:**
   - `DashboardSettings.MaxOtherDepth` (`[Range(1, 10_000)]`, 1,000).
   - `PlanLimits` gains a trailing `int MaxOtherDepth = 1_000`. `WidgetRunner.Limits` passes `min(MaxOtherDepth, MaxChartRows)`, and `DashboardChecks` keeps working through the default.
4. **Plan types** (`WidgetPlanner.cs`):
   - `SeriesValues(Ranked, Type, Skip, Limit)` with `Shown`, `Before` and `More`.
   - `PlanFound(CategoryValues? Categories, SeriesValues? Series)` with `None`.
   - `SeriesPlan(Func<PlanFound, BuiltQuery> Ranking, int Limit, Func<PlanFound, BuiltQuery>? Rest)`.
   - `OtherStep(Skip, Size, Depth)` with `Next(bool more)`.
   - `WidgetPlan.Main` becomes `Func<PlanFound, BuiltQuery>`, with init-only `Series` and `Step`.
   - `WidgetPlanner.Plan(widgetId, kinds, int skip = 0)` passes the step to `PlanContext.Skip`.
   - `PlanContext.Step(size, steps)` returns null for charts that don't step. It reports `page.offset` as a 400 by field when the step is past the depth.
5. **Helpers** (`GroupQueries` in `WidgetKinds.cs`):
   - `Among(member, values, type, parameters, among: bool)` writes in-or-null and the null-safe not-in. Refactor `BarKind.Main` onto it; existing goldens must not change.
   - `RankTies(keys)` is `Ties`, with guid keys tied by `toString(name)` so steps are stable. Verify that `toString(uuid)` pushes down on each dialect; if it doesn't, keep today's `Ties` and note the instability.
6. **Kinds:**
   - `PieKind`: the step's take, `Step` set when `Other` is on.
   - `BarKind` and `LineKind`: `SeriesPlan` (ranking, main's extra filter, rest) and `Step`.
   - Text is unchanged when `SeriesLimit` is null or the step is 0.
7. **Runner** (`WidgetRunner.cs`):
   - **`WidgetDataDto`** appends, each written only when set:
     - `Series`: the shown values, in rank order, encoded;
     - `OtherRows`: `[d0, m0]`;
     - `MoreSeries`;
     - `Next`.
     `Offset` echoes the chart's step.
   - **`WidgetQueryDto`** appends `Series` and `Rest`.
   - **`DataAsync`** plans with the page's offset. A chart without `Step` treats any offset as 0, so the cache doesn't split. The cache key adds the ranking and rest queries.
   - **`ComputeAsync`** runs categories → ranking → main → rest (only when `More`) → total.
     - Pie: slices the fetched rows at the step; `Next` is null when Other is null.
     - **Lines with a rest:** merge the main and rest x values into `Categories` (nulls first, ordinal text, otherwise `IComparable`). Cut at the earliest last value of any list that was truncated, and set `Truncated`. This makes the client's axis include x values that only Other has.
   - **`QueryAsync`** takes the page and returns the series and rest texts.
   - **Endpoints** (`Features/Dashboards/DashboardDataEndpoints.cs`): `InlineQueryAsync` and `PublishedQueryAsync` pass `request.Page`.
8. **Public dashboards** (`PublicDashboards.Colored`):
   - Add `data.Series` labels.
   - Send only the series shown, never the skipped ones or the one-ahead ranked value.

## Client (`src/client/src/app`)

1. **API types:** accept the OpenAPI snapshot (`GDQ_ACCEPT_SNAPSHOTS=1`), then run `npm run api` to regenerate `core/api/schema.d.ts`.
2. **`model/widget-context.ts`:**
   - `offset` becomes a `linkedSignal` that returns to 0 when the frame's step basis changes.
   - The basis is the chart's id, inputs and `host.basis`. Filters, selections and edits therefore reset the step; a refresh doesn't, and tables keep their page as now.
   - Update the doc comment: "a table's page, or a chart's step into Other".
3. **`view/widget-frame.ts`:**
   - Charts send `page: {offset, count: false}` only when offset > 0. `dashboard-page.spec.ts` expects `page: null` at 0.
   - `openQuery` passes the page.
   - The `more` text reads "The N largest series of more" when `moreSeries` is set and no Other is drawn, and is dropped once a pie has stepped.
4. **`state/dashboard-host.ts`:** `query(widget, inputs, page)` sends the page on the published and inline hosts.
5. **`charts/chart-options.ts`:**
   - `Item.other?: true`.
   - Series order comes from `data.series ?? unique(…)`.
   - Append an Other series from `data.otherRows`:
     - `theme.neutral`, items `key: null, other: true`, faded like the others while something is chosen;
     - included in percent-stack totals, the legend and the aria `summary` ("series 11–20, the rest as Other").
   - Lines take their axis from `data.categories ?? unique(…)` before filling periods.
   - The pie's Other item gets `other: true`.
   - `otherName()` returns "Other (the rest)" when a real value is named "Other".
   - The tooltip on Other says "Click for the next N" when `next` is set.
6. **`widgets/chart-widget.ts`:**
   - A click on an `other` item sets `context.offset` to `data.next`, when `next` is set and `context.choosing()` (not on the editor's canvas unless Interact is on). It never chooses, whatever the modifiers.
   - **Step bar**, shown when `data.offset > 0`, above the chart or its table:
     - range text, aria-live: "Slices 11–20" or "Series 11–20";
     - **Back** (`chevron_left`, labelled "Back to series 1–10"): `max(0, offset − N)`;
     - **Back to the largest** (`first_page`): 0, shown when two or more steps deep.
     - N is the pie's `limit`, or the chart's `seriesLimit`. Focus moves to the widget when the bar disappears under it.
7. **`widgets/rows-table.ts`** (Show as table):
   - An Other row with a neutral swatch, not choosable.
   - A "Show the next N" button in its actions cell when `next` is set, which is the keyboard path forward.
8. **`charts/series-colors.ts`:**
   - Entities are taken from `data.series` (rank order).
   - The `WidgetColors.paint` basis includes `data.offset`, so each step is coloured from the first slot. Otherwise series past the 8 built-in colours turn grey like Other.
9. **`popups/query-dialog.ts`:** tabs "Its top series" and "Its Other".
10. **Editor** (`editor/kinds/kind-settings.ts`):
    - `SeriesEditor` gains "Series, at most" (1–100; empty means every series) and "The rest as Other" (disabled without a limit).
    - Turning a series on sets `seriesLimit: 8, seriesOther: true`. Eight matches the palette's colours.
    - Turning a series off removes both fields (`undefined`, never null or false), so the JSON matches the server's canonical form.
    - `LineSettings`' inline series handler becomes a method, as `BarSettings.serieschanged` is.
11. **Docs:**
    - `docs/server.md`: the definition paragraph (`seriesLimit`, `seriesOther`), the "Running" paragraph (steps via `page.offset`, `next`, `MaxOtherDepth`), the settings table.
    - `docs/client.md`: Charts and Colours, the step bar.

## Tests

**Server** (xunit.v3 + Shouldly, golden files via `tests/Shared/Golden.cs`)
- **`Dashboards/WidgetQueryTests.cs`:**
  - `Report.CaseAsync(…, skip)` runs the chain and prints `series:` and `rest:`.
  - Switch `Main(null)` to `Main(PlanFound.None)`.
  - New `TheLargestSeriesShowAndOtherStepsIntoTheRest` (own snapshot) covers:
    - bars by customer, limit 1 with Other, at steps 0, 1 and 2 (no rest at the last);
    - an avg Other, showing it is not a subtraction;
    - a null series (ranked, and not-in with null listed);
    - a line by month split by status (axis merge);
    - a pie at step 1.
  - Existing snapshots must not change.
- **`DashboardDataApiTests.cs`:**
  - `series`, `otherRows`, `moreSeries`, `next`, and `offset` echoed.
  - A pie stepping: `next` 1, 2, null; Other shrinking.
  - A bar without a series ignores the offset and echoes 0.
  - A repeated step is `cached`.
  - With `MaxOtherDepth=2`: a 400 on `page.offset`, and `next` null at the depth.
  - `/query` with a page shows the stepped texts.
- **`DefinitionRulesTests.cs`:**
  - `.seriesLimit` with no series, 0 or 101, and `.seriesOther` without a limit, for bar and line.
  - Unset fields are not written, and the hash is unchanged.
- **`PublicDashboardApiTests.cs`:** `offset=1` steps a pie; series past those shown never appear in the body; `colors` cover only the shown series.
- **`Hosting/HostTests`:** the OpenAPI snapshot.

**Client** (Vitest through `ng test`, `FakeECharts`)
- **`charts/charts.spec.ts`:** rank order; the Other series (neutral, `other`, `key` null); percent totals including Other; the line axis from `categories`; the name clash; the tooltip hint; the pie's `other` item.
- **`charts/series-colors.spec.ts`:** a step repaints from the first colour; entities follow `data.series` order.
- **`dashboard-interaction.spec.ts`:**
  - Clicking the pie's Other sends `page {offset: 10}`.
  - The step bar shows "Slices 11–20".
  - Back sends `page: null`.
  - A filter change resets the step.
  - With `next` null, a click does nothing and never chooses.
- **`editor/dashboard-editor.spec.ts`:** the series toggle sets and clears `seriesLimit` and `seriesOther`.
- **Rows table:** the Other row, and its "Show the next N" button.
- **`dashboard-host`:** `query` sends the page.

## Risks noted
- **Line points:** a line with Other can draw up to 2× `limit` points. The axis merge orders x values that only Other has by ordinal text, which may differ from the database's collation; date buckets aren't affected.
- **Bars by series:** still capped at `MaxChartRows + 1` by (d0, s0). If that cap truncates them, the last categories may show Other without their shown series; `truncated` says so, as today.
- **Query count:** a bar step runs 3–4 sequential queries, each cached, inside one public gate turn.
- **Palette clash:** with `whenOut: neutral` and more series than the palette has colours, the extra series look like Other.

## Order of work

1. Rules and settings.
2. Plan types and `Among`/`RankTies`, with the `BarKind` refactor (goldens green).
3. Kinds.
4. Runner and endpoints.
5. Server tests and snapshots.
6. `npm run api`.
7. Context and frame.
8. Chart options, colours and the chart widget with its step bar.
9. Rows table, query dialog and editor.
10. Client tests.
11. Docs.

## Verification

- **Server:**
  ```
  MSBUILDCOPYTASKPARALLELISM=1 dotnet test --solution GalaxyData.slnx
  ```
  `MSBUILDCOPYTASKPARALLELISM=1` avoids the Defender copy crash. Accept new goldens with `GDQ_ACCEPT_SNAPSHOTS=1` only after reading their diffs; existing snapshots must not change.
- **ClickHouse / HyperWhirlpool:** `tests/GalaxyData.Query.ContainerTests` (servers through Docker in WSL) to confirm that the ranking, not-in and `toString` ties run there.
- **Client:**
  ```
  npm --prefix src/client test
  ```
  This also checks that `schema.d.ts` matches. Then:
  ```
  npm --prefix src/client run lint
  ```
- **In the browser:** `preview_start` with `server-hw-e2e` (HyperWhirlpool) or `server`, plus `client`. On a dashboard, check:
  - a bar chart split by a high-cardinality series shows 8 series plus a grey Other;
  - clicking Other shows 9–16 with the step bar, Back returns, and Back to the largest appears two steps deep;
  - a line chart behaves the same, including x values that only Other has;
  - a pie's Other steps;
  - a filter change resets the step;
  - View query shows the series and rest tabs;
  - an embed (`/embed/{token}`) steps through `?offset=`;
  - light and dark, and a narrow width.
  Take screenshots as proof.
