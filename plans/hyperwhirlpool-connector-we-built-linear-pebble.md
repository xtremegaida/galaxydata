# HyperWhirlpool connector

## Context

HyperWhirlpool (HW, `C:\Data\HyperWhirlpool`) captures events and builds them into a ClickHouse warehouse according to a
published YAML model. We want GalaxyData to read it as a data source with three properties:
- **Read-only:** the events are immutable.
- **ClickHouse SQL:** that is its query engine.
- **Semantic:** the dashboard builder uses what the model declares (keys, relationships, descriptions, classify labels,
  attribute aggregates, time columns).

Connecting GalaxyData to ClickHouse directly would expose HW's internals and need ClickHouse credentials. The internals
are `hw_raw`, `hw_build` (fingerprinted physical tables) and `hw_meta`. Instead, a **read adapter** in HW, authenticated
with new **read API keys**, exposes only the model's public views and their semantic schema, and forwards queries to
ClickHouse.

What exists today:
- **HW has no read side.** There is no query or consumer-schema endpoint, and API keys are ingest-only (`c2s`/`s2s`,
  `apps/manager/src/keys.ts`).
- **HW's public surface is stable.**
  - One ClickHouse database per domain.
  - Stable view names: `<d>.<s>_events`, `<d>.<e>`, `<d>.link_<l>`, `<d>.cluster_<c>`, `<d>.<projection>`,
    `<d>.lookup_<l>`.
  - Each view is tagged `COMMENT 'hw:<hash>'` (`packages/warehouse/src/warehouse.ts:500-508`).
- **HW's model can be read as a library.** The manager has the resolved and compiled live model at go-live
  (`pipeline.ts` `finishVersion`). `resolveModel` and `compile` give the declarations and the stable views.
- **GalaxyData's engine is ADO.NET throughout.** It already has a ClickHouse connector over HTTP (ClickHouse.Driver),
  with a dialect, type mapper, provider and read-only kind.
- **GalaxyData already uses keys and foreign keys for navigation.** Introspected primary and foreign keys become catalog
  keys and navigations (`src/GalaxyData.Query/Catalog/CatalogBuilder.cs:297-337`). The dashboard builder follows those
  (`FieldRef` paths, links), and cross-schema foreign keys within one source resolve.

Agreed decisions:
- Do both phases, Phase 1 first.
- A read key sees the whole warehouse: every public domain view.
- Capture and PII columns are treated like any other column.

## Approach

**1. The adapter speaks ClickHouse's own HTTP protocol.**
- GalaxyData's connector reuses ClickHouse.Driver, the ClickHouse dialect, the type mapper and value handling unchanged.
  It differs only in authentication and where its schema comes from. A custom JSON query API would need a whole ADO.NET
  provider in GalaxyData.
- Any ClickHouse HTTP client (DBeaver, Grafana) works with a read key as its password.

**2. ClickHouse grants are the enforcement boundary.**
- The adapter runs queries as `hw_reader`, whose role can read only the domain view databases and `hw_catalog`.
- The views become `SQL SECURITY DEFINER`, so the reader needs no rights on `hw_build`.
- The adapter does no SQL parsing beyond a first-keyword guard.

**3. The semantic schema is one versioned JSON document (`hw-schema/1`).**
- The manager builds it at go-live and stores it in `hw_catalog.schema`.
- Clients read it through the same key and channel:
  - over SQL: `SELECT document FROM hw_catalog.schema ORDER BY seq DESC LIMIT 1`;
  - over HTTP: `GET /v1/schema` on the adapter.
- The document is atomic with the views it describes, and evolves without DDL.
- GalaxyData's introspector reads it over SQL, so GalaxyData needs **no new core seam** in Phase 1.

```text
GalaxyData ── ClickHouse HTTP + Basic(any, rk_key) ──> apps/query (HW) ── hw_reader ──> ClickHouse
   introspector: SELECT document FROM hw_catalog.schema          ├─ <domain>.* views (SQL SECURITY DEFINER)
   queries: SELECT … FROM "web"."session" …                      └─ hw_catalog.schema (written by manager at go-live)
```

## Phase 1a — HyperWhirlpool: read keys

- **`apps/manager/src/store.ts:46,198-220`:** rebuild `api_keys` so its CHECK allows `'read'`. Use the existing
  table-rebuild migration pattern inside `tx()`.
- **`apps/manager/src/keys.ts`:**
  - `createRead(name)` creates a `rk_<token>` key: shown once, stored as sha256 plus a 4-character hint.
  - **Fix `edgeConfig()`** (:178-189) so it branches on kind explicitly and excludes `read`. Today it would publish a
    read key to `edge.json` as an ingest secret.
  - New `queryConfig()` produces `{version, keys:[{id,name,secret_sha256,revoked}]}`, versioned by its own counter.
  - `publish()` writes `config/edge.json` and `config/query.json`.
  - At startup, `manager.ts:101` publishes `query.json` if it is missing.
- **`apps/manager/src/api.ts:157-166`:** accept `kind: 'read'`.
- **`apps/manager-ui`** (`types.ts`, `pages/Keys.tsx`): add a "Query key" section and a create dialog, reusing the
  shown-once secret dialog.
- **New `packages/keyring`:** lift the polled S3 config (with last-good and disk cache) and `SecretIndex.find`
  (constant-time sha256) out of `apps/edge/src/keys.ts:87-275,200-208`. Edge's `KeyStore` keeps its API and tests and
  uses the package.

## Phase 1b — HyperWhirlpool: views and ClickHouse access

- **View security** (`packages/compiler/src/compile.ts`):
  - Add `CompileOptions.viewSecurity` (`DEFINER = hw_definer SQL SECURITY DEFINER`). Store it on `StableView` and
    include it in the view hash (:542), so `syncViews()` at manager start re-applies existing views.
  - `applyViews` (`warehouse.ts:505`) writes `CREATE OR REPLACE VIEW x <security> AS (…) COMMENT 'hw:…'`.
- **`StableView` metadata:** add `object: {kind, name}` and `variant?: 'range'|'live'`, set at each `views.push`. This
  avoids parsing view names downstream.
- **Typed reference columns.** These touch only stable-view SQL, never `mergedSql`, whose fingerprints would trigger
  rebuilds.
  - Link views: add typed endpoint columns, e.g. `toUUIDOrNull(from_id) AS visitor_id`. For match links, where from and
    to are the same entity, use `from_`/`to_` prefixes. `from_id`/`to_id` stay String.
  - Projection views: cast the String key to the entity's id type.
  - Clusters: no references in v1 (their member type is polymorphic).
- **Access provisioning** (manager bootstrap, next to where `hw_raw`/`hw_meta` are created):
  - Create `hw_catalog`.
  - Create the SQL-defined role `hw_read`.
  - Create the users `hw_reader` (profile `hw_reader`, default role `hw_read`) and `hw_definer` (`HOST NONE`, with
    SELECT and dictGet on `hw_build.*`).
  - Grant `SELECT ON hw_catalog.*` to `hw_read`.
  - `applyViews` runs `GRANT SELECT ON <domain db>.* TO hw_read` for each domain, so new domains need no manual work.
  - The loader needs `access_management` and the `SET DEFINER` right.
- **`deploy/dev/clickhouse/hw-users.xml` and the config:**
  - The loader gets `access_management`.
  - The `hw_reader` profile gets constraints: maximum `max_execution_time`, `max_memory_usage`, `max_result_rows`, and
    `max_threads`.
  - Add a quota keyed by `client_key`.
  - Set `access_control_improvements.select_from_system_db_requires_grant=true` explicitly.

## Phase 1c — HyperWhirlpool: the semantic schema document

**New `packages/compiler/src/semantic.ts`:** a pure function `describeModel(model, compiled, columnTypes) → document`.
Don't name it `catalog.ts`; `@hw/model` already has one.

Document shape (`format: "hw-schema/1"`):

```text
model {seq, hash, published_at}
domains [{name, database, description}]
objects [{database, name, kind: stream|entity|link|cluster|projection|lookup, declaration, description,
          key [cols], time col?,
          columns [{name, type, role, description?, aggregate?, values?}],
          references [{name, columns, database, object, ref_columns, kind}]}]
```

How the model maps into the document:
- **Roles:** `key | parent | ref | time | field | attribute | derive | classify | capture | internal`.
- **Descriptions:** taken from `decl.description`, field and attribute object forms, classify descriptions, and the
  domain docs.
- **Classify columns:** `values` holds the rule labels in order, then the default.
- **Attributes:** `aggregate` is the declared aggregate (`count`, `sum`, …).
- **Left out:** `_range` and `_live` variants, and references to entities that have no view (entities without streams).

**Keys, references and time columns per view:**

| View | Key | References | Time |
|---|---|---|---|
| Stream `<d>.<s>_events` | `event_id` | subject and ancestor ref columns → their entity views (cross-domain ones too) | `ts` |
| Entity `<d>.<e>` | `<e>_id` | parents → parent entity view | `first_seen` |
| Link `<d>.link_<l>` | (`from_id`, `to_id`) | typed endpoint columns → the two entity views | `first_seen` |
| Projection `<d>.<p>` | key column | key → the projected entity | none |
| Cluster, lookup | their natural keys | none | none |

**Manager:** after `applyViews` in `finishVersion` (`pipeline.ts:852`, before `markCurrent` :867), in `syncViews` when
views change, and at startup when it is missing:
1. Run `DESCRIBE` on the stable views (new `describeViews` in `warehouse.ts`).
2. Call `describeModel`.
3. Insert `(seq, hash, published_at, document)` into `hw_catalog.schema` (MergeTree, `ORDER BY seq`).
4. Trim to the last few versions.

## Phase 1d — HyperWhirlpool: `apps/query`, the adapter

A new Fastify app. Add it to `tsconfig.json`, the root `package.json` scripts, and `deploy/systemd/hw-query.service`
with `query.env.example`. Files:
- `config.ts`: ClickHouse URL, reader password, S3, limits.
- `auth.ts`: accepts Bearer, or Basic auth with the key as the password (the username is ignored); polls `query.json`
  via `@hw/keyring`.
- `proxy.ts`, `app.ts`, `main.ts`.

What the proxy (`/`) does:
- **Upstream calls:** use `node:http` `request`, not `fetch`, which would decompress responses.
- **Request bodies:** register a `'*'` content-type parser; the driver posts `text/sql`, gzipped.
- **Long URLs:** raise `maxHeaderSize`, because the driver puts every `param_*` in the URL.
- **Streaming:** stream responses with `reply.hijack()`. When the client goes away, destroy the upstream request, which
  is what makes `cancel_http_readonly_queries_on_client_close` work.
- **Headers in:** strip the client's `Authorization` and `X-ClickHouse-*`, and authenticate upstream as `hw_reader`.
- **Headers out:** pass through `X-ClickHouse-*`, `Content-Encoding` and the status code.
- **URL parameters, allow-listed:** `query`, `query_id`, `database`, `default_format`, `param_*`,
  `enable_http_compression`, `max_execution_time`, the driver's JSON-mode parameters, and GalaxyData's five settings.
- **URL parameters, rejected:** `session_id`, `role`, `user`, `password`, `quota_key` and `replace_running_query`, and
  multipart form bodies.
- **Added by the proxy:** `quota_key=<keyId>` and `log_comment=<keyId>`.
- **Statement guard:** decompress and inspect the small body. Allow only statements whose first keyword is `SELECT`,
  `WITH`, `SHOW`, `DESC`/`DESCRIBE`, `EXPLAIN` or `EXISTS`. The HTTP interface runs one statement per request, so this
  is enough, and it blocks `KILL QUERY` across keys sharing `hw_reader`.
- **Errors:** auth failures answer in ClickHouse's error format (401, `Code: 516 … Authentication failed`), so drivers
  report them sensibly.
- **Other endpoints:**
  - `GET /ping` answers `Ok.` without auth.
  - `GET /v1/schema` (authenticated) returns the latest `hw_catalog.schema` document, with the model hash as its ETag.
  - `/healthz`, `/metrics`.

**Docs:** a new `docs/read-api.md` (keys, protocol, limits, the `hw-schema/1` format, revocation within about 60 s).
Also update `docs/operations.md` (users, roles, grants) and the README architecture.

## Phase 1e — GalaxyData: the connector

**New `src/GalaxyData.Query.HyperWhirlpool`.** It references `GalaxyData.Connectors` and `GalaxyData.Query.ClickHouse`;
the Excel → DuckDB precedent shows a connector can reuse another's provider. Add it to `GalaxyData.slnx`,
`GalaxyData.Connectors.BuiltIn.csproj` and `BuiltInConnectors.Create`.

**`HyperWhirlpoolSourceProvider`:**
- `ProviderKind = "hyperwhirlpool"`; `Dialect = ClickHouseDialect.Instance`.
- Delegates `PrepareConnectionAsync`, `PrepareCommand`, `BindParameter` and `ReadValue` to
  `ClickHouseSourceProvider.Instance`.
- `CancelCommand` only cancels the request (no `KILL`, which the adapter refuses); the server stops through
  `cancel_http_readonly_queries_on_client_close`.
- `PrepareWriteAsync` throws.
- Connections come from `ClickHouseSourceProvider.CreateDataSource`.

**`HyperWhirlpoolKind : ConnectionKind`:**
- `AlwaysReadOnly`; `Configure` sets `IsReadOnly = true, SupportsDml = false`.
- Fields:
  - Host; Port (default 443); Protocol (default https); Path.
  - **API key:** the driver's `Password` keyword, `FieldType.Password`, so `IsSecret` holds and it is encrypted at rest.
  - **Default domain:** the driver's `Database` keyword.
  - Timeout, Compression, SkipServerCertificateValidation.
- `Reserved`: `Username`, `Roles`, `UseSession`, `SessionId`, `ReadStringsAsByteArrays`, and the `set_*` engine
  settings.
- Share `ClickHouseKind`'s keyword canonicalization (`ClickHouseConnector.cs:109-129`) as a public static helper rather
  than copying it.
- `FoundAsync` runs `SELECT version(), currentDatabase()` through the adapter (this checks the key) and reports the
  model version from `hw_catalog.schema`.

**`HyperWhirlpoolSchemaIntrospector`:**
- Reads the latest document with the existing `ScalarTextAsync` / `QueryAsync` helpers (`GalaxyData.Query.Providers`),
  then deserializes it with System.Text.Json records.
- Maps it to `SourceSchema`:
  - **Domains → schemas.** All domains are read, not just `currentDatabase()`. The default schema is the connection's
    domain, or else the first.
  - **Objects → `TableSchema`** of `TableKind.View`, with `Comment` = description and `PrimaryKey` = key.
  - **References → `ForeignKeySchema`** with `IsEnforced = false`. Missing refs are zero values, so navigations stay
    optional.
  - **Columns → `ColumnSchema`** with `ClickHouseTypeMapper.Map`/`ReadAs` and `Comment` = description.
  - **`ServerVersion`** = "HyperWhirlpool model N (hash) on ClickHouse x".
- `gdq` works unchanged through `OpenDatabaseAsync`.

**Docs:** add a HyperWhirlpool section to `docs/connectors.md` and its kind to `docs/server.md#connections`.

## Phase 2 — GalaxyData: semantic hints in the dashboard builder

These are generic and connector-agnostic. The HW document carries them from Phase 1. Later, ClickHouse `Enum8/16` and
PostgreSQL enums could fill `Values` too.

**Engine:**
- `ColumnSchema` and `ColumnDef` (`SourceSchema.cs:66`, `CatalogModel.cs:245`) gain:
  - `IReadOnlyList<string>? Values`: the values the column is known to take, in order;
  - `AggregateHint? Aggregate`: `Sum | Avg | Min | Max | CountDistinct`.
- `TableSchema` and `EntityDef` gain `TimeColumn`.
- New members are null by default and omitted when null, so old snapshots read unchanged and goldens don't churn.
- No label or hidden hints: the model has no labels, capture columns stay visible, and the overlay's `Label` and
  `Hidden` rules (`CatalogBuilder.cs:256-257`) stay as they are.

**HW mapping:**
- Classify `values` → `Values`.
- Attribute `aggregate` → `AggregateHint`: `count`/`sum` → Sum; `min` → Min; `max` → Max; the others (`first`, `last`,
  `latest`, `any`, `exists`, `set`, `count_distinct`) get none.
- Each object's `time` → `TimeColumn`.

**API:**
- `EntityColumnDto` gets `Values` and `Aggregate`; `EntityDto` gets `TimeColumn` (`CatalogEndpoints.cs:81,103`).
- Regenerate `src/client/src/app/core/api/schema.d.ts` with `scripts/api-types.mjs`.

**Client** (`src/client/src/app/features/dashboards/editor/`):
- `controls/field-picker.ts`: show a column's description (`comment`) as its hint.
- `controls/measure-list.ts`: a picked column's `aggregate` becomes the measure's default aggregate.
- `controls/condition-editor.ts`: offer `values` as choices.
- `controls/dimension-editor.ts` and the line/bar defaults in `model/widget-defaults.ts`: default the date dimension to
  the source entity's `timeColumn` (month bucket, as now).

## Risks and checks

- **ClickHouse version.** GalaxyData documents ClickHouse 26.3+ (`docs/connectors.md:107`), but HW's dev default is
  25.8. Run E2E with `HW_CH_VERSION` at 26.x, and check what in GalaxyData's dialect needs 26.3.
- **View security on 25.8 and 26.x needs an integration test:**
  - Definer views work, including the parameterized `_range` views and `dictGet` inside views (keep
    `dictGet ON hw_build.*` for `hw_read` as a fallback).
  - The reader can't `SELECT` from `hw_build`, `hw_raw` or `hw_meta`.
  - Today's views probably run with invoker rights, so the grants documented in `docs/operations.md:38` likely don't
    work as written.
- **Bounded-entity views** merge at query time (a `UNION` with an on-the-fly merge), which may be slow for dashboards.
  Unbounded `<e>` views are periodic snapshots. Document both.
- **One shared `hw_reader`.** Quotas are per key via `quota_key`, but `query_log` and isolation are shared. A ClickHouse
  user per key (`IDENTIFIED WITH sha256_hash`) is a later option, and would also allow per-domain scoping.
- **Repository state.** The HW checkout is on `hb-import` with uncommitted WordPress changes. Do the HW work in a new
  worktree or branch from its main.

## Verification

**HyperWhirlpool:**
- `pnpm check` passes, including Vitest units for:
  - the store migration keeps existing rows;
  - a read key is absent from `edge.json` and present in `query.json`;
  - `describeModel` snapshots over `model/web.yaml`, `crm.yaml` and `fixtures/*.yaml` (cross-domain refs, links,
    projections);
  - the proxy against a fake upstream: parameter filtering, header stripping, the statement guard, gzip pass-through,
    abort propagation, 401 and 503.
- `pnpm test:int` against `pnpm dev:stack` passes:
  - after a publish, the document is in `hw_catalog.schema`;
  - through `apps/query` with a read key, view selects, `_range` views and `dictGet` work;
  - `hw_build`, `hw_raw` and `hw_meta` and `KILL` are refused;
  - a revoked key is refused after the poll interval;
  - cancelling a request stops the server query (`system.processes`).

**GalaxyData:**
- `dotnet test` passes:
  - `ConnectionKindTests`: the API key is a secret; the reserved keywords are refused.
  - Re-accept `ConnectionApiTests.TheKindsDescribeTheirForms.json`.
  - A golden test mapping a fixture `hw-schema/1` document to `SourceSchema` (keys, cross-domain foreign keys,
    comments; in Phase 2, hints).
  - The architecture tests still pass.
- Container tests in `tests/GalaxyData.Query.ContainerTests`, with a `hyperwhirlpool.clickhouse.sql` fixture: two
  domain databases with views, plus `hw_catalog.schema` holding a document.
  - Point the connector straight at the test ClickHouse; the adapter speaks the same protocol, and the password plays
    the key.
  - Read the schema; run a query across a stream → entity navigation.
  - Cancel, and confirm the server query stops.

**End to end:**
1. Start HW's dev stack, the manager and `apps/query`.
2. Create a query key in the manager UI.
3. Add a HyperWhirlpool connection in GalaxyData and try it.
4. Confirm the schema shows domains, descriptions and navigations.
5. Build a dashboard: a line chart of `web.page_view_events` by month, grouped by `session.traffic_type` (a navigation).
6. In Phase 2, check classify values in the filter editor, the default aggregate and the default time column.
7. Use the run skill / browser preview to screenshot the result.
