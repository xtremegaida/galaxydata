# Dashboard export/import, and a web analytics dashboard over HyperWhirlpool

## Context

Two needs:

1. **Moving dashboards between instances.** There's no way to take a dashboard out of GalaxyData or bring one in.
   - `POST /api/dashboards` already takes a whole definition and checks it (`DefinitionRules.Check`).
   - But definitions name palettes by database id (`definition.palette` and each chart's `palette`), which mean nothing on
     another instance.
   - Sources name entities by connection alias (`hw.web.session`), and the alias may differ elsewhere.
   - The only export in the server is the overlay's (`GET /api/overlay/export`). The client has no file download or
     upload code.
2. **A full-featured web analytics dashboard** over the live HyperWhirlpool query service
   (`http://156.38.156.242:8100`, model seq 2, ClickHouse 26.6).
   - It should use every dashboard feature: tabs, headings, text, bar/line/pie/table (grouped and raw), filters of
     every kind, cross-filtering, links, series limits with Other, palettes, layout overrides, and refresh.
   - Anything worth showing that has no good visualisation gets flagged.
   - Only the `web` domain is used; CRM events haven't arrived yet.

**What's in `web`:**
- `session`: 35.7M rows since 2022-10.
- `page_view`: 59M rows; carries `session_*` columns.
- `device`: 24M rows.
- Event streams: engagement 25M, interaction 19M, geo 5.6M, clicks 259k, scrolls 192k.
- `form_visit` (8k) and `page_request` (789) only started on 2026-10-13. They have no references to session, so they
  are unlinked.
- Classify columns with known values: `traffic_type`, `visitor_class`, `device_type`, form `outcome`, request `outcome`.
- About 15 sites, mostly ZA traffic; paid traffic dominates.

**Speed:** run directly on the service, 30 days of sessions by day and traffic type take about 0.3 s; all history takes
about 1.7 s. So the default period is **the last 30 days**, and **bots are excluded by default** (Visitors = human).

## Part 1: Export and import

### Server (`src/GalaxyData.Web`)

**Export file** (new `Dashboards/DashboardExport.cs`):

```
DashboardExport {
  format: "galaxydata.dashboard", version: 1,
  name, description?, exportedAt,
  definition,                                   // the canonical definition
  palettes: [{ id, name, description?, definition }]   // those the definition names that exist
}
```

- It never carries the owner, sharing, the public token, embed origins or revisions.
- It's written indented (camelCase, enums as names, as `DefinitionJson` writes), so files diff well.

**`GET /api/dashboards/{id}/export?copy=working|published`**, in `Features/Dashboards/DashboardEndpoints.cs`:
- Who may export follows `CopyAsync`: whoever `FindAsync` finds.
- The default copy is the working one if `SeesWorking`, else the published one. Asking for a copy you can't see is a
  404, the same as Copy.
- The palettes come from `DashboardViews.PalettesAsync` (`PaletteRefs.Of`).
- The response is `application/json` with `Content-Disposition: attachment; filename="<slug>.dashboard.json"`, as in
  the overlay export precedent (`Features/Overlay/OverlayEndpoints.cs:97-101`).

**`POST /api/dashboards/import`** takes `ImportDashboardRequest { dashboard: DashboardExport, name?, aliases?: {from: to} }`
and returns 201 with a `DashboardDto`:
- **Checks:**
  - The format and version must be 1.
  - The name defaults to the file's and goes through the existing `Name()`. A name already taken is a 409
    `dashboard-name-taken`, as now.
  - The definition goes through `rules.Check(def, "dashboard.definition.", errors)`.
  - Each palette definition gets the checks `POST /api/palettes` runs, with errors at `dashboard.palettes[i]…`.
- **Aliases:** rewrite the first part of each `sources[].entity` found in `aliases` (case-insensitive), using
  `EntityName.TryParse` and its parts, before the check. The catalog isn't checked on save, as for any save; problems
  show as issues on the dashboard.
- **Palettes:**
  - Reuse an existing palette with the same hash **and** name.
  - Otherwise create one owned by the importer, under a free name ("Traffic (2)" if the importer has the name already;
    the index is unique on (OwnerId, Name)).
  - Remap the ids in the definition.
  - **References to palettes not in the file are dropped (set to null).** Otherwise they could point at an unrelated
    palette with the same id on this instance.
- **Saving:** one transaction (palettes saved first for their ids, then the dashboard via `Fill`). The result is a
  working copy only, unpublished and private.
- A shared helper `PaletteRefs.Remap(definition, map)` sits next to `PaletteRefs.Of`/`Own` in
  `Palettes/PaletteStore.cs:112-160`.

**Docs** (`docs/server.md`, Dashboards, endpoint table at about line 783): an "Export and import" paragraph covering
the file format, what isn't carried, alias mapping, palette reuse, and dropped references.

### Client (`src/client/src/app/features/dashboards`)

- **List page** (`list/dashboard-list.ts:50-65`):
  - An **Import** button (`upload_file`) next to "New dashboard" opens a hidden `<input type=file accept=".json">`.
  - It reads the file with `file.text()`, refuses files over 1 MB and those whose `format`/`version` aren't known, then
    opens the **Import dialog**. The dialog is new (`dialogs/import-dialog.ts`) and loaded on demand like the others
    (`dashboard-page.ts:253-280`).
  - **Dialog contents:**
    - Name, defaulting to `freeName()` (`list/dashboard-list.ts:366-378`).
    - **Sources:** each alias the file's sources use, with a select of the catalog's sources from `/api/catalog`,
      preselected when the alias exists here.
    - The palettes it brings.
  - Import sends `POST /api/dashboards/import` and then navigates to the new dashboard. A 400 lists the errors in the
    dialog, as `editor/dashboard-editor.ts:547-586` turns them into messages.
  - Each dashboard's ⋮ menu (`:125-158`) gains **Export**.
- **Dashboard page** "More" menu (`dashboard-page.ts:129-151`): **Export** after Copy.
  - It's an anchor to `/api/dashboards/{id}/export?copy=<the copy shown>` with `download`, as
    `features/admin/overlay/overlay-list.html:14-17` does.
  - That avoids blob URLs, which the CSP doesn't cover.
- **API types:** accept the OpenAPI snapshot, then `npm run api`.
- **Docs:** `docs/client.md`, the dashboards list and page actions.

### Tests

**Server**, new `tests/GalaxyData.Web.Tests/Dashboards/DashboardTransferTests.cs`, with `WebAppFactory`/`TestApi` and
the `Definitions.Sales()` fixtures:
- **Export:**
  - It gives the canonical definition and the named palettes.
  - An owner gets the working copy; a shared viewer gets the published one, and a 404 for `copy=working`.
  - An invisible dashboard is a 404.
  - The headers carry the filename.
- **Import:**
  - It creates an unpublished dashboard of the importer's.
  - Aliases are rewritten.
  - An identical palette is reused; a differing one is created under a free name.
  - Missing palette references are dropped.
  - A bad format or version is a 400; a bad definition is a 400 with `dashboard.definition.` paths; a taken name is a
    409.
- **Round trip:** export, import, export again gives the same definition apart from palette ids.
- **The example dashboard** (`docs/examples/dashboards/*.dashboard.json`, from Part 2) is read as a `DashboardExport`
  and passes `DefinitionRules` and the palette checks, so it stays valid as definitions evolve.
- **The OpenAPI snapshot** (`Hosting/HostTests`).

**Client** (Vitest):
- `list/dashboard-list.spec.ts`: Import reads a file, shows the dialog, sends the mapped aliases and navigates; an
  unknown format is refused; 400 errors are shown.
- `dashboard-page.spec.ts`: the Export link's href follows the copy shown.
- A spec for the import dialog's alias mapping.

## Part 2: The "Web analytics" dashboard

### Setup

- **A new local instance**, so the e2e one (local HW on 18101) stays untouched:
  - Launch config `server-hw-live` in `.claude/launch.json`, port 5180.
  - Script `C:/Users/Robert/AppData/Local/Temp/gd/hwlive/gd-server.ps1` and data dir `…/hwlive/gd-data`.
  - Bootstrap admin password in `…/hwlive/gd-admin.txt`.
  - Plus the `client` config.
- **A HyperWhirlpool connection** with alias `hw`:
  - `Host=156.38.156.242; Port=8100; Protocol=http; Database=web; Password=<key>`.
  - It's added through the connections API and checked with Try. The key is stored encrypted by GalaxyData and is never
    written to the repo.
- **Palettes**, through the palettes API:
  - **"Traffic types"**: assigned by label, with an override per `traffic_type`. It's the dashboard's palette, so a
    traffic type keeps its colour across every chart.
  - **"Devices"**: a chart's own palette, on the device pies.
- **Authoring:**
  1. Generate the export file with a scratch Node script (kept in Temp, not the repo).
  2. Bring it in with the new import endpoint, so the feature is used for real.
  3. Fix the issues it shows and tune it in the editor.
  4. Publish revision 1.
  5. Export it to **`docs/examples/dashboards/web-analytics.dashboard.json`**.

### Sources and links

| Id | Entity | Link |
|---|---|---|
| `sessions` | `hw.web.session` | (root) |
| `views` | `hw.web.page_view` | views → sessions `[session]` (uses carried columns) |
| `devices` | `hw.web.device` | sessions → devices `[device]` |
| `clicks` | `hw.web.click_events` | clicks → sessions `[session]` |
| `pvevents` | `hw.web.page_view_events` | pvevents → sessions `[session]` |
| `forms` | `hw.web.form_visit` | unlinked (no references) |
| `requests` | `hw.web.page_request` | unlinked |

Use whatever entity path form the catalog writes (`hw.web.session` or `hw.session`).

### Filters: all five kinds, plus a hidden one and an `except`

| Filter | Kind | Field | Default |
|---|---|---|---|
| Period | relative | sessions.first_seen | last 30 days |
| Site | values, multiple | sessions.site | none |
| Traffic | values, multiple | sessions.traffic_type | none |
| Visitors | values | sessions.visitor_class | `human`; **except** the bot widgets |
| Country | values, multiple | sessions.country | none |
| Device | values, multiple | sessions.device_type | none |
| Entry page | text | sessions.entry_path | none |
| Paid entry | boolean | sessions.entry_paid | none |
| Form dates | range | forms.first_seen | none |
| Hidden | hidden, not editable | sessions.site | not blank |

### Tabs and widgets

About 40 widgets, under `MaxWidgets` 50.
- **Wide** layout (12 columns) is the design.
- **Medium** has an override.
- **Narrow** hides the raw tables.
- Pies and category bars **emit**; tables listen.
- The trend lines **listen to none**, so they keep their context.
- Refresh runs on an interval of 900 s.

1. **Overview**
   - A heading, plus a markdown note on definitions: a session is 30 minutes; bots are off by default; where the data
     comes from.
   - **Sites at a glance**: a grouped table by site with sessions, visitors (`approxCountDistinct device_id`), page
     views (`sum page_views`), pages per session (avg, 2 decimals), avg duration (`avg duration_ms`, compact, " ms"),
     and forms started. It emits.
   - **Sessions and visitors per day**: a line, area.
   - **Sessions per day by traffic type**: bars, stacked, series limit 8 with Other.
   - Donuts of **traffic type** and **device type** (the second with its own palette).
   - **Top entry pages**: horizontal bars.
   - **Bounce by traffic type**: bars by `traffic_type`, series `bounced`, stacked to 100%. This is the ratio workaround.
   - **Latest sessions**: a raw table sorted by `first_seen` descending.
2. **Acquisition**
   - **Traffic mix by month**: stacked to 100%, last 12 months by the widget's own condition, and in **Period's
     except**.
   - **Campaign sources**: `entry_utm_source`, not blank.
   - **Campaigns**: a grouped table by source, medium and campaign, with sessions, visitors, pages per session, and
     forms started.
   - **Referring sites**: `entry_ref_host`.
   - **Search vs social referrers**: `pvevents.ref_class`.
   - **Landing pages of paid traffic**: a condition `entry_paid` = true.
3. **Content**
   - **Top pages**: a grouped table by path with views, avg active ms, avg max scroll %, scrolled-to-end rate, clicks,
     and forms started.
   - **Page views per day**: a line.
   - Views by **hour of day** and **day of week** (series `session_device_type`).
   - **Scroll depth**: bars by `max_scroll_pct`, sorted by the dimension. This is the histogram workaround.
   - **Outbound link hosts** (`outbound = 1`) and **Downloads** (`download = 1`): grouped tables from `clicks`.
4. **Audience**
   - Countries (top 20) and cities.
   - Pies of browsers and OSes (limit 8, with Other, so they step into Other).
   - **Visitor classes per day**: stacked; in Visitors' except.
   - **Networks behind bot traffic**: a grouped table by `as_org`; in the except.
   - **Devices by number of sessions**: from `devices`.
   - **Languages**: `pvevents.lang`.
5. **Forms** (data since 13 Oct; the note says so)
   - An outcome pie.
   - **Outcomes by form**: stacked to 100%.
   - **Forms**: a grouped table with visits, attempts, blocked, rejections, and avg start-to-submit.
   - Pages with forms.
   - Visits by hour of day.
   - **Latest form visits**: a raw table.
6. **Server** (`page_request`, the WordPress plugin)
   - The outcome pie.
   - Status codes: bars coloured by category.
   - **Slowest paths**: a grouped table with requests and avg/max `server_ms`.
   - Requests by kind and post type.

Number formats (decimals, prefix, suffix, compact) are set in the file, since the editor has no control for them.

### The gaps to flag (the final report, and follow-up proposals)

1. **KPI / scorecard tile**: a big number with a change against the previous period and a sparkline. The Overview needs
   it most; there's no workaround, because a grouped table needs a dimension.
2. **Ratio and percentage measures**: bounce rate, conversion, pages per session as a share. `avg` refuses booleans,
   `countValues` counts values rather than trues, and there's no ×100 percent format. The workaround is 100%-stacked
   bars.
3. **Duration formatting**: ms shown as "2m 15s".
4. **Funnel**: form seen → started → attempted → submitted, and landing → form → submit.
5. **Map**: a choropleth by country, or points (`geo_events` lat/lon).
6. **Heatmap**: day of week × hour of day, and retention cohorts.
7. **Histograms and binning, and calculated dimensions**: scroll depth, duration, pages per session; new vs returning.
8. **Period-over-period comparison** on lines and KPIs.
9. **Combo / dual-axis chart**: sessions vs bounce rate.
10. **One period across unlinked sources by their time column** (semantic `timeColumn`), and filters scoped to a tab.
    `forms` and `requests` need a filter of their own today.
11. **Tables**: totals row, conditional formatting or data bars, links for URL columns, sparklines in rows.
12. **Path analysis / Sankey**: entry page → next page; user flows.
13. **Array and Map columns** (`failed_fields`, `query_other`, `attrs`) can't be grouped by.
14. **Editor gaps**: no number-format control (the editor writes null); the dashboard description can't be edited; a
    sort by dimension doesn't follow classify `values` order.
15. **"Active now"**: the last 5 minutes, live; needs the KPI tile.

Also, a **security note**: the query service is plain HTTP, so the query key crosses the internet in clear text. HTTPS
in front of `apps/query` is recommended.

## Order of work

1. Server: `DashboardExport`, `PaletteRefs.Remap`, the export and import endpoints, and the tests.
2. OpenAPI snapshot, then `npm run api`.
3. Client: the list Import button and dialog, the Export actions, the specs.
4. Docs, for server and client.
5. The `server-hw-live` instance, the connection, the palettes.
6. Generate the dashboard, import it, fix the issues in the browser, publish it, export it to `docs/examples/dashboards/`.
7. The example-file test.
8. The final report, with the gap list.

## Verification

- **Server:**
  ```
  MSBUILDCOPYTASKPARALLELISM=1 dotnet test --solution GalaxyData.slnx
  ```
  Accept the new OpenAPI snapshot with `GDQ_ACCEPT_SNAPSHOTS=1` only after reading its diff. Existing goldens mustn't
  change.
- **Client:**
  ```
  npm --prefix src/client test
  ```
  It checks `schema.d.ts` too. Then:
  ```
  npm --prefix src/client run lint
  ```
- **In the browser** (`preview_start` with `server-hw-live` and `client`):
  - **Export:** from the list and the page, the downloaded file matches the definition.
  - **Import:** the dialog maps `hw` to a different alias on a second import, and the palettes are reused rather than
    duplicated.
  - **Every tab loads with no issues** and no console errors.
  - **Filters:** each changes its widgets; Form dates affects only Forms.
  - **Cross-filtering:** clicking a traffic-type slice filters the others.
  - **Other:** a browser pie steps into Other.
  - **Visitors except:** the bot charts still show bots.
  - **Layout:** at narrow width the overrides apply.
  - **Themes:** light and dark.
  - Take screenshots of each tab as proof.
