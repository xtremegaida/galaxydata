# The GalaxyData query language

This is the reference for the language that GalaxyData queries are written in. It is for people who write queries
(analysts, users of an application built on GalaxyData) and for developers who use the library. Every example in a
`gdq` block runs as written; every example in a `gdq-error` block is refused.

Contents

1. [Introduction](#1-introduction)
2. [Names](#2-names)
3. [Statements](#3-statements)
4. [Values](#4-values)
5. [Operators](#5-operators)
6. [Rows and scopes](#6-rows-and-scopes)
7. [Query methods](#7-query-methods)
8. [Functions](#8-functions)
9. [Nulls and semantics](#9-nulls-and-semantics)
10. [Results](#10-results)
11. [Changes to data](#11-changes-to-data)
12. [Diagnostics](#12-diagnostics)
13. [Limits](#13-limits)

## 1. Introduction

### 1.1 A first query

A query starts from an entity of the catalog (a table, a view, or a virtual entity) and applies methods to its rows,
one after the other. Each method takes the rows before it and gives new rows.

```gdq
shop.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city).orderBy(id)
```

```text
id   | total  | who       | city
-----+--------+-----------+-------------
1001 | 250.00 | Acme Ltd  | Cape Town
1003 |  12.25 | Beta Corp | Johannesburg
```

Read it from left to right:

- `shop.orders` is the `orders` table of the source `shop`.
- `.where(status == 'open')` keeps the rows whose `status` is `'open'`.
- `.select(...)` says what each result row holds: `id` and `total` of the order, and `who` and `city` from the
  order's customer. `customer` is a *navigation*: it follows the foreign key `customer_id` to the customer's row.
- `.orderBy(id)` sorts the rows.

The same query runs against SQLite, DuckDB, PostgreSQL, SQL Server or a folder of Excel workbooks, and a query may
combine several of them. The engine writes the SQL for each database; what can't run in a database runs in DuckDB,
the *merge engine*.

### 1.2 Running queries

From the command line, `gdq` runs a query against the sources given with `-s alias=kind:target`:

```text
gdq run -s shop=sqlite:shop.db 'shop.orders.where(total > 50).select(id, total)'
gdq run -s shop=sqlite:shop.db -p since=2026-01-06 'shop.orders.where(order_date >= $since)'
gdq explain -s shop=sqlite:shop.db 'shop.orders.count()'
```

(Single quotes keep a Unix shell from reading `$since` itself.)

Kinds are `sqlite`, `duckdb`, `postgres`, `sqlserver` (a connection string) and `excel` (a folder of `.xlsx`
workbooks). A `.sql` target is run into a new in-memory database. `gdq sql` prints the SQL each source runs,
`gdq explain` the plan, the result columns with their lineage and links, and the SQL; `gdq schema` lists the
catalog; `gdq repl` runs queries interactively. `--overlay file.json` adds an overlay (section 2.8), `-p name=value`
a parameter (section 4.2), `--timeout seconds` a time limit.

From .NET, a `QueryEngine` prepares and runs queries over a catalog built from the sources' schemas (each read by its
provider's `Introspector`):

```text
QueryCatalog catalog = new CatalogBuilder()
   .AddSource(new SourceInfo("shop", SqliteSourceProvider.Instance.ProviderKind, schema.DefaultSchema), schema)
   .WithOverlay(overlay)
   .Build();
QueryEngine engine = new(catalog, connections, [SqliteSourceProvider.Instance, DuckDbSourceProvider.Instance], merge);

PreparedQuery prepared = engine.Prepare(new QueryRequest("shop.orders.where(total > $min)")
{
   Parameters = new QueryParameters().Add("min", 50),
});
if (!prepared.Success) { /* prepared.Diagnostics says why, with positions in the text */ }
await using QueryResult result = await prepared.ExecuteAsync(cancellationToken);
while (await result.ReadAsync(cancellationToken)) { object?[] row = result.Current; }
```

`Prepare` binds and plans the query and writes its SQL without touching a database. The application supplies the
connections (`IConnectionFactory`), so the library never holds secrets.

### 1.3 The examples in this document

The examples run against two copies of a small shop:

- `shop` is a SQLite database with the tables `customers`, `addresses`, `orders`, `order_lines`, `employees` and
  `audit_log`, and the view `open_orders`.
- `wh` is a DuckDB database with the same tables, plus `wh.crm.contacts` in the schema `crm`, whose columns include
  `"Display Name"`. Its `orders` also have a column `tags`, a DuckDB list, which the engine has no type for.
- An overlay relates `wh.orders.customer_id` to `shop.customers.id`, as the navigation `shop_customer`.
- The parameters are `$min` = 50 (a whole number), `$since` = `'2026-01-06'` (text) and `$n` = 3.

To run them yourself:

```text
gdq run -s shop=sqlite:tests/fixtures/shop.sqlite.sql -s wh=duckdb:tests/fixtures/shop.duckdb.sql \
        --overlay overlay.json -p min=50 -p since=2026-01-06 -p n=3 '<query>'
```

with this `overlay.json`:

```text
{
  "relations": [
    { "from": "wh.orders", "fromColumns": ["customer_id"], "to": "shop.customers", "toColumns": ["id"],
      "name": "shop_customer" }
  ]
}
```

Results are shown as `gdq run` prints them, without the line it adds with the row count and time.

## 2. Names

### 2.1 Sources, schemas and entities

Every entity has a name of the form `alias.schema.table`. The alias is the name the source was registered under
(`shop`, `wh`); it must be a plain name. The schema and table are the database's own names.

The tables of a source's default schema can also be named without the schema: `shop.orders` is `shop.main.orders`
(SQLite's and DuckDB's default schema is `main`; PostgreSQL's is usually `public`, SQL Server's `dbo`). Tables in
other schemas need the schema: `wh.crm.contacts`.

```gdq
shop.main.orders.where(total > 50).select(id)
```

When a schema of a source has the same name as a table of its default schema, the shortcut for that table is left
out (a GDQ5003 warning), and the table must be written with its schema.

An Excel folder source has one schema per workbook (the file name without `.xlsx`) and one table per sheet, and no
default schema: `xl["Budget 2024"]["Sheet 1"]`.

Virtual entities (section 2.8) live in namespaces of their own, such as `reports.big_orders`.

### 2.2 Case

Names match without regard to case: `SHOP.Orders`, `shop.ORDERS` and `shop.orders` are the same table, and `ID` is
the column `id`. A name written differently keeps its own spelling as a column name:

```gdq
SHOP.Orders.Where(Total > 50).Select(ID)
```

gives a column named `ID`. Method and function names match without regard to case too.

When a database has several names that differ only by case (PostgreSQL may have `"Orders"` and `orders`), the one
written with exactly the same case wins. When none matches exactly, the name is ambiguous (GDQ2002); write it with
the exact case.

Words of the language itself are lower case and match exactly: `true`, `false`, `null`, `and`, `or`, `not`, `in`,
`let`, `it`, `outer`, `inner`, `desc` and `asc`. Lambda parameters match exactly too. (A group's `key`, section
6.4, is a member of the group and matches as members do.)

### 2.3 Names that aren't plain identifiers

A plain name (an identifier) starts with a letter or `_` and goes on with letters, digits and `_`. (`$` may appear
in names too, but a name that starts with `$` is a parameter.) Any other name is written in brackets as a quoted
string, after the thing it belongs to:

```gdq
wh.crm.contacts.select(email, it["Display Name"])
```

```text
email         | Display Name
--------------+-------------
ann@acme.test | Ann at Acme
```

- `it["Display Name"]` is the column `Display Name` of the row (`it` is the row, section 6.1).
- `xl["Budget 2024"]["Sheet 1"]` is the sheet `Sheet 1` of the workbook `Budget 2024`.
- `shop["order lines"]` would be a table named `order lines`.

Brackets work for every name, plain or not: `wh["crm"]["contacts"]` is `wh.crm.contacts`, and `customer["name"]` is
`customer.name`. They take one quoted name; there is no indexing by position (GDQ2008).

```gdq
wh["crm"]["contacts"].select(email)
```

The first part of a path must be a plain name, because brackets need something on their left; source aliases are
always plain names.

### 2.4 Reserved words

These words can't be used as plain names; write a column with such a name as `it["in"]`, `it["true"]` and so on:

- `true`, `false` and `null`, the literals;
- `and`, `or`, `not` and `in`, the word operators.

These words mean something else in some places:

- `it` is the current row in a method's argument (so a column named `it` is `it["it"]` there);
- `key` is a group's key after `groupBy` (section 6.4);
- `outer` and `inner` are the two rows in a join's condition and items (section 6.3);
- `desc(...)` and `asc(...)` set the direction of a sort key inside `orderBy(...)`;
- `let` starts a named subtree when it begins a statement and is followed by a name and `=` (section 3.1).

`if`, `for`, `break` and `continue` start statements the language refuses (section 3.4).

### 2.5 Looking up from the catalog root: `::`

A named subtree (section 3.1) may have the same name as a source. It then hides the source (with a GDQ2101 warning).
`alias::name` looks `alias` up in the catalog, skipping named subtrees:

```gdq
shop := shop.orders.where(total > 50);
shop::customers.where(c => shop.any(o => o.customer_id == c.id)).select(name)
```

Here `shop` is the subtree, and `shop::customers` the table `customers` of the source `shop`. Only the part before
`::` is looked up from the root; what follows is its member, as after a `.`.

### 2.6 Columns

The members of an entity's rows are its columns and its navigations. Inside a method's argument they are named
directly (`total`, `customer`), or through the row (`it.total`, `o.total` in a lambda).

The type of each column comes from the database (section 4.3). An overlay can change it, for example to read a SQLite
text column as a date.

### 2.7 Navigations

A foreign key, or a relation declared in the overlay, gives two navigations:

- a **forward** navigation on the table with the foreign key (the dependent), which leads to one row of the other
  table (the principal): `order.customer`;
- an **inverse** navigation on the principal, which leads to the rows that refer to it: `customer.orders`. It is a
  collection, unless the foreign key's columns are unique, in which case it leads to at most one row.

A navigation to one row is a record: take its columns (`customer.name`), follow it further (`order.customer.city`),
compare it with `null`, or select it whole (section 7.2). A collection is a query: aggregate it (`orders.count()`,
`orders.sum(total)`), test it (`orders.any(...)`), or flatten it with `selectMany` (section 7.9). A collection can't be
used as a single value (GDQ2015).

```gdq
shop.employees.select(name, boss: manager.name, reports: employees_by_manager.count()).orderBy(name)
```

```text
name | boss | reports
-----+------+--------
Ann  | null |       2
Ben  | Ann  |       0
Cal  | Ann  |       0
```

```gdq
shop.order_lines.select(order_id, line_no, city: order.customer.city).orderBy(order_id, line_no)
```

**Naming.** Navigations are named by convention:

- A forward navigation takes the name of its foreign key column without the key suffix: `customer_id` gives
  `customer`, `ship_address_id` gives `ship_address`. The suffixes are `_id`, `_fk`, `_key`, `_ref` and `_code` in any
  case, and `Id`, `ID`, `Fk`, `FK`, `Key`, `Ref` and `Code` after a lower-case letter or digit (`CustomerID` gives
  `Customer`). A key of several columns, or a column without such a suffix, gives the name of the principal table.
- An inverse navigation takes the name of the dependent table: `orders` on `customers`. When several relations lead
  from the same table to the principal, or a table refers to itself, it is `<table>_by_<forward>`:
  `orders_by_ship_address` and `orders_by_bill_address` on `addresses`, `employees_by_manager` on `employees`.
- When the name is already a column or another navigation of the table, the navigation takes the name of the foreign
  key constraint, or else the name followed by `_2`, `_3`, and so on. A navigation of an overlay relation to another
  source takes that source's alias before its name instead (`wh_orders`).
- The databases' own foreign keys are named first: a relation the overlay adds never takes a name they give. With the
  overlay of these examples, `shop.customers.orders` is still shop's orders, and wh's are `shop.customers.wh_orders`.

The overlay can name a relation's navigations (`name`, `inverseName`), and rename or hide any navigation. The
suffixes are an overlay setting too.

```gdq
shop.addresses.select(line1, ships: orders_by_ship_address.count(), bills: orders_by_bill_address.count())
```

**Required and optional.** A forward navigation leads to exactly one row when its foreign key is enforced and its
columns can't be null; otherwise there may be no row, and the values read through it can be null. SQLite's foreign
keys don't count as enforced unless the source is set to trust them (`SourceInfo.TrustForeignKeys`); relations from
the overlay never do. So `shop.orders.customer` is optional and `wh.orders.customer` is required.

**Across sources.** A relation in the overlay may join tables of two sources. Navigating it reads both:

```gdq
wh.orders.where(status == 'open').select(id, total, who: shop_customer.name).orderBy(id)
```

```text
id   | total  | who
-----+--------+----------
1001 | 250.00 | Acme Ltd
1003 |  12.25 | Beta Corp
```

The relation's inverse on `shop.customers` is `wh_orders`, as `orders` is shop's own:

```gdq
shop.customers.select(name, here: orders.count(), there: wh_orders.count()).orderBy(name)
```

### 2.8 The overlay: relations and virtual entities

The overlay is a JSON file (or a `CatalogOverlay` object) that adds to what the databases declare:

- `relations`: many-to-one relations the databases don't declare, also across sources. `from` and `to` are entity
  paths as in queries; `toColumns` must be the key or a unique key of `to`. `name` and `inverseName` name the
  navigations.
- `virtualEntities`: entities defined by a query (`name` with a namespace, such as `reports.big_orders`; `query`;
  optionally `key`).
- `entities`: per-entity settings: a declared `key` (for views and keyless tables; it serves navigation, never
  editing), a `displayColumn`, `hidden`, and per column `hidden`, a `label` and a `type` such as `"date"` or
  `"decimal(12,2)"`.
- `navigations`: rename (`renameTo`) or hide a navigation, found by the name the convention gave it.
- `naming`: the key suffixes and display-column names the conventions use.

```text
{
  "relations": [
    { "from": "wh.orders", "fromColumns": ["customer_id"], "to": "shop.customers", "toColumns": ["id"],
      "name": "shop_customer", "inverseName": "wh_orders" }
  ],
  "virtualEntities": [
    { "name": "reports.big_orders", "query": "shop.orders.where(total > 50)" },
    { "name": "reports.spend",
      "query": "shop.orders.groupBy(customer_id).select(customer_id, spend: sum(total))",
      "key": ["customer_id"] }
  ],
  "entities": [],
  "navigations": []
}
```

Lists may be left out, and are then empty. A relation without `from`, `fromColumns`, `to` or `toColumns`, or a
virtual entity without `name` or `query`, fails to load, saying which.

A virtual entity is used like a table. Its columns are its query's columns. When its query keeps the rows of one
entity (filters, sorts, pages or extends them), the virtual entity also has that entity's navigations, and its key
unless rows may repeat (as after `concat`). A key in the overlay is used as the key in any case. With the overlay
above (and the same sources), `gdq schema` lists:

```text
reports.big_orders (virtual, key id)
   id int64, customer_id int64, ship_address_id int64?, bill_address_id int64?, status string, total decimal(10,2), order_date date, placed_at datetimeoffset?
   customer -> shop.customers?, ship_address -> shop.addresses?, bill_address -> shop.addresses?, order_lines -> shop.order_lines*
reports.spend (virtual, key customer_id)
   customer_id int64, spend decimal(38,2)
```

and `reports.big_orders.select(id, total, customer.name)` gives:

```text
id   | total  | name
-----+--------+---------
1001 | 250.00 | Acme Ltd
1002 |  99.50 | Acme Ltd
```

A virtual entity's query must be a query (not a single value) whose members are columns: a selected record, such as
a navigation, is refused. It may use other virtual entities, but not itself, even through others (GDQ2026). A virtual entity whose query doesn't bind can't be used (GDQ2025); the catalog's
diagnostics say why. Each use of a virtual entity is planned afresh, as a named subtree is.

**Hidden.** Hiding is advice for tools: hidden entities, columns and navigations can still be named in queries. A
spread (`x.*`) leaves hidden columns out, and a hidden navigation gives no links.

## 3. Statements

### 3.1 Named subtrees

A query text is one or more statements separated by `;`. Every statement but the last names a subtree:

```gdq
big := shop.orders.where(total > 50);
big.select(id, total)
```

`let name = ...;` is the same as `name := ...;`:

```gdq
let rate = 0.15;
let floor = $min * rate;
shop.orders.where(total * rate > floor).select(id, fee: total * rate)
```

A named subtree is a query or a single value, and it can use the ones before it. Each use of a named query is
planned afresh, as if its text were written there. A named value that is a constant is written into the query where it
is used; any other value (a parameter expression, an aggregate) is computed.

```gdq
average := shop.orders.avg(total);
shop.orders.where(total > average).select(id, total, average)
```

```text
id   | total  | average
-----+--------+--------
1001 | 250.00 | 90.4375
1002 |  99.50 | 90.4375
```

Rules:

- The last statement is the result. A text whose last statement only names a subtree is refused (GDQ2021), and so is
  a statement before the last that doesn't name one (GDQ2022).
- A name can be defined once (GDQ2020), and can't start with `$` (GDQ2020).
- A name may hide a source of the same name (warning GDQ2101; reach the source with `::`, section 2.5). Inside a
  method's argument, a column of the row hides a subtree of the same name (warning GDQ2101).
- `:=` is a statement of its own; inside an expression it is refused (GDQ2009).

```gdq-error
x := shop.orders
```

```gdq-error
shop.orders; shop.customers
```

### 3.2 The result

The result is usually a query: its rows are the result's rows. It may also be:

- a single value, as one row with one column named `value`;
- one row, from `first()` or `firstOrDefault()` (section 7.12), or a navigation from one.

```gdq
1 + 2 * 3
```

```gdq
shop.orders.count(total > 50)
```

```gdq
shop.orders.orderBy(id).first().customer
```

### 3.3 Comments, spacing and commas

Spaces and line breaks may be used freely between tokens. Comments are ignored:

- `#` or `//` to the end of the line;
- `/* ... */`, which may span lines (and doesn't nest).

```gdq
# Orders over the minimum
shop.orders
   .where(total > $min)   // $min is a parameter
   /* most recent first */
   .orderBy(desc(order_date))
   .select(id, order_date, total)
```

A trailing comma is allowed in argument lists and lists: `select(id, total,)`, `['open', 'shipped',]`.

### 3.4 What isn't part of the language

The parser reads more than the language accepts. These are refused with GDQ2008:

- `if`, `for`, `break` and `continue` statements (use `iif(c, a, b)` or `c ? a : b`);
- blocks `{ ... }` inside an expression, and object literals `{a: 1}` (name columns in `select(name: value)`);
- lists `[...]` anywhere but after `in`;
- a lambda anywhere but as a method's argument;
- the bitwise operators `&`, `|`, `^`, `<<`, `>>` and `~`.

```gdq-error
if (true) 1 else 2
```

```gdq-error
shop.orders.where(total & 1 == 1)
```

## 4. Values

### 4.1 Literals

| Literal | Examples | Type |
|---|---|---|
| Whole number | `42`, `0`, `0x1F` | `int64` |
| Decimal number | `2.5`, `0.15`, `1000.00` | `decimal` with the digits written: `2.5` is `decimal(2,1)` |
| Number with an exponent | `2.5e0`, `1e3`, `1E-2` | `double` |
| Text | `'open'`, `"Cape Town"` | `string` |
| Raw text | `` `C:\temp` `` | `string` |
| True and false | `true`, `false` | `boolean` |
| Null | `null` | no type of its own: it takes the type it meets |
| List | `['open', 'shipped']` | only after `in` |

Numbers:

- A whole number too large for 64 bits is a decimal. A hexadecimal number has `0x` and 1 to 8 hexadecimal digits,
  read as a signed 32-bit value: `0x7FFFFFFF` is 2147483647 and `0xFFFFFFFF` is -1.
- A number needs a digit before and after the point: `0.5`, not `.5` or `5.`. Digits can't be separated by `_`, and a
  number can't run into letters (`12abc`).
- `-2` is the operator `-` applied to `2`.
- `2.5` is an exact decimal and `2.5e0` a double; arithmetic follows the type (section 5.2).

Text is written between single or double quotes, on one line. A backslash starts an escape: `\'`, `\"`, `\\`, `\/`,
`\n` (line feed), `\r`, `\t`, `\b`, `\f`, `\0` and `\uXXXX` (four hexadecimal digits). Any other escape is an error.

```gdq
shop.customers.select(a: 'it\'s', b: "say \"hi\"", c: 'tab\there', d: 'caf\u00e9').take(1)
```

Raw text is written between backticks and has no escapes: `` `C:\temp\x` `` is the text `C:\temp\x`. It can't
contain a backtick. Raw text that spans lines starts on the line after the opening backtick and ends on the line
before the closing one, which stands on a line of its own; the closing backtick's indentation is taken off every
line:

```gdq
shop.customers.select(note: `
      first line
        second line, indented by two
      `).take(1)
```

gives the text `first line` and `  second line, indented by two` on two lines.

### 4.2 Parameters

`$name` is a parameter: a value supplied with the query, not written in it. Parameters keep values out of the query
text, and the SQL sends them as parameters too.

```gdq
shop.orders.where(total > $min and order_date >= $since).select(id, total, order_date).orderBy(id)
```

A parameter's type follows from its value:

| .NET value | Type |
|---|---|
| `bool` | `boolean` |
| `byte`, `sbyte`, `short` | `int16` |
| `ushort`, `int` | `int32` |
| `uint`, `long` | `int64` |
| `ulong` | `decimal(20,0)` |
| `decimal` | `decimal` |
| `float` | `single` |
| `double` | `double` |
| `string` | `string` |
| `char` | `string(1)` |
| `Guid` | `guid` |
| `DateOnly` | `date` |
| `TimeOnly`, `TimeSpan` | `time` |
| `DateTime` | `datetime` |
| `DateTimeOffset` | `datetimeoffset` |
| `byte[]` | `binary` |
| `null` | unknown: it takes the type it meets |

`QueryParameters.Add(name, value, type)` gives a type explicitly. Names may be given with or without the `$`.

A parameter then adapts to what it meets, as a constant does (section 4.4):

- text compared with a date, date-time, date-time with offset, time or guid becomes that type; the text is
  converted when the query runs, and if it doesn't convert, the query fails then (`$since can't be used as a date:
  ...`). This is how `$since`, given as text, compares with `order_date`.
- a whole number meeting a whole-number column takes the column's width (its value is checked when the query runs);
- a decimal meeting a decimal column takes its precision and scale (more digits are kept if the value needs them);
- a null takes the type it meets.

On the command line, `-p name=value` types the value by how it is written: `null`, `true` or `false`, a whole number
(`int64`), a decimal number (`decimal`), or else text. Quote the value with `'` or `"` to keep it text.

A parameter the query uses but no value was given for is an error (GDQ2012). An unnamed `select` item that is a
parameter takes its name without the `$`.

```gdq-error
shop.orders.where(status == $missing)
```

### 4.3 Types

Every value has a logical type, the same whatever database it comes from:

| Type | Values | In .NET | Notes |
|---|---|---|---|
| `boolean` | true, false | `bool` | |
| `int16`, `int32`, `int64` | whole numbers | `short`, `int`, `long` | |
| `decimal(p,s)` | exact numbers, `p` digits, `s` after the point | `decimal` | `decimal` without `(p,s)` has no fixed precision |
| `single`, `double` | floating-point numbers | `float`, `double` | |
| `string(n)` | text of at most `n` characters | `string` | `string` is unbounded; `string(n,ansi)` is single-byte text (SQL Server `varchar`) |
| `binary(n)` | bytes | `byte[]` | |
| `guid` | identifiers | `Guid` | |
| `date` | dates | `DateOnly` | |
| `time` | times of day | `TimeOnly` | |
| `datetime` | dates with a time of day, no offset | `DateTime` | |
| `datetimeoffset` | instants | `DateTimeOffset` | read in UTC (section 9) |
| `interval` | durations | `TimeSpan` | |
| `json` | JSON text | `string` | can be selected, tested for null and converted with `toString` |
| `unknown` | anything the engine has no type for | as the database gives it | can be selected, tested for null and converted with `toString` |

Type names are written in lower case, with a `?` when the value may be null: `int64`, `decimal(10,2)?`,
`string(50)`. Explain and `gdq schema` show them so.

What each type allows:

- Numbers of any kind compare with each other; dates, date-times and date-times with offsets compare with each other;
  other types compare only with their own type.
- Guids and binary values can be tested for equality, but not ordered: `<`, `orderBy`, `min` and `max` refuse them,
  as databases disagree on their order (GDQ2006, GDQ2024).
- `json` and `unknown` values can't be compared, sorted, grouped or counted distinct (GDQ2006); test them with
  `== null`, or convert them with `toString(...)`.

```gdq-error
wh.orders.where(tags == 'x')
```

```gdq-error
wh.crm.contacts.orderBy(id)
```

### 4.4 Constants take the type they meet

A literal or a parameter compared with (or combined with) a value of another type is converted to that type when the
query is bound, if that loses nothing. This keeps comparisons with columns exact, and lets text stand for dates and
guids:

- Text compared with a `date` is read as `yyyy-MM-dd`; with a `datetime`, as a date and time without an offset, such
  as `'2026-01-05 10:30'` or `'2026-01-05T10:30:00'`; with a `datetimeoffset`, as a date and time with an offset (one
  without is taken as UTC); with a `time`, as `HH:mm:ss`; with a `guid`, as a guid in either case. Text that doesn't
  read as the type is an error (GDQ2013).
- Text compared with text takes the column's text type (its length, and SQL Server's `varchar`), so the database can
  use its indexes.
- A whole number takes a narrower whole-number type it fits, or becomes a decimal or double.
- A decimal becomes a double, and a double a decimal, when it meets one.
- `null` takes the type it meets.

```gdq
shop.orders.where(order_date > '2026-01-31').select(id)
```

```gdq
wh.crm.contacts.where(id == '2F1C0000-0000-4000-8000-000000000001').select(email)
```

```gdq-error
shop.orders.where(order_date > '31 Jan 2026')
```

### 4.5 Implicit conversions

Apart from constants, values are not converted from one kind to another: text doesn't become a number, and a number
doesn't become text (GDQ2006). Use the conversion functions (section 8.5).

```gdq-error
shop.orders.where(status == 3)
```

Numbers of different types combine in the wider type: `int16` < `int32` < `int64` < `decimal` < `double`. Two
`single` values give a `single`; a `single` with anything else a `double`. Two decimals of the same precision and
scale keep it; other decimals give a `decimal` without fixed precision.

The branches of `? :`, `iif`, `coalesce` and `??`, and the two sides of a set operation, need a common type: numbers
give the wider type, dates and date-times give a date-time (a date-time with offset if either is one), text gives
text, and `null` takes the other's type.

## 5. Operators

### 5.1 Precedence

Operators bind from the top of this table to the bottom; operators on the same line bind equally, and group from the
left unless the table says otherwise.

| Operators | Meaning | Grouping |
|---|---|---|
| `a.b` | member access | left |
| `f(...)`, `a["name"]` | call, member by quoted name | left |
| `a.*` | spread (only as an item of `select`, `extend`, joins and `selectMany`) | |
| `**` | power | right: `2 ** 3 ** 2` is `2 ** 9` |
| `-a`, `+a`, `!a` | negation, plus, not | prefix |
| `*`, `/`, `%` | multiply, divide, remainder | left |
| `+`, `-` | add (or join text), subtract | left |
| `<<`, `>>` | refused | |
| `<`, `<=`, `>`, `>=`, `in` | ordering, membership | left |
| `==`, `!=`, `<>` | equality | left |
| `&`, `^`, `\|` | refused | |
| `not a` | not | prefix |
| `and`, `&&` | and | left |
| `or`, `\|\|` | or | left |
| `??` | first value that isn't null | right |
| `c ? a : b` | conditional | right |
| `x => ...`, `(a, b) => ...` | lambda | |

Things to note:

- `!` binds tightly and `not` loosely: `not status == 'open'` is `not (status == 'open')`, but `!status == 'open'`
  is `(!status) == 'open'`, which is refused.
- `-2 ** 2` is `-(2 ** 2)`, which is `-4`.
- `a in [...] == true` is `(a in [...]) == true`.
- `a--b` is `a - (-b)`: there are no `++` and `--` operators.

```gdq
shop.orders.where(not status == 'open').select(id)
```

```gdq-error
shop.orders.where(!status == 'open')
```

### 5.2 Arithmetic

`+`, `-`, `*`, `/` and `%` take numbers. `a ** b` is `power(a, b)` and gives a double.

- A whole number divided by a whole number gives a **double**: `7 / 2` is `3.5`, and `qty / 2` is `0.5` for a `qty`
  of 1.
- `*` and `/` with a decimal give a decimal without fixed precision; `+` and `-` of two decimals of the same
  precision and scale keep it.
- `%` is the remainder, with the sign of the left side: `-7 % 3` is `-1`.
- Dates are not numbers: use `addDays`, `addMonths` and `daysBetween` (section 8.3).
- Any null operand gives null.

```gdq
shop.order_lines.select(order_id, line_no, a: qty / 2, b: price / 2, c: qty % 2).orderBy(order_id, line_no)
```

```text
order_id | line_no | a   | b     | c
---------+---------+-----+-------+--
    1001 |       1 |   1 |    50 | 0
    1001 |       2 | 0.5 |    25 | 1
    1002 |       1 | 0.5 | 49.75 | 1
    1003 |       1 | 2.5 | 1.225 | 1
```

```gdq-error
shop.orders.select(d: order_date + 1)
```

### 5.3 Text

`+` joins text with text. If either side is null, the result is null. To join other values, convert them with
`toString(...)`, or use `concat(...)`, which takes any values and treats nulls as empty text.

```gdq
shop.customers.select(name, plus: name + ' / ' + city, joined: concat(name, ' / ', city)).orderBy(name)
```

```text
name      | plus                     | joined
----------+--------------------------+-------------------------
Acme Ltd  | Acme Ltd / Cape Town     | Acme Ltd / Cape Town
Beta Corp | Beta Corp / Johannesburg | Beta Corp / Johannesburg
Gamma Inc | null                     | Gamma Inc /
```

```gdq-error
shop.customers.select(n: name + 1)
```

### 5.4 Comparison

`==` is equal, `!=` and `<>` are not equal, and `<`, `<=`, `>`, `>=` order. `=` is not a comparison: inside an
expression it is refused (GDQ2009).

- Comparing with `null` tests for null: `city == null` is true when `city` has no value, and `city != null` when it
  has one. Any other comparison with a null value is null (section 9).
- Which types compare is in section 4.3.
- Text compares as the database compares it (section 9).

```gdq-error
shop.orders.where(status = 'open')
```

### 5.5 Logic

`and` (or `&&`), `or` (or `||`) and `not` (or `!`) take true/false values and follow three-valued logic (section 9).
A condition must be a true/false value: a number or text is not one (GDQ2007).

```gdq
shop.customers.select(name, a: credit_limit > 2000 or city == null, b: credit_limit > 2000 and city == null).orderBy(name)
```

```text
name      | a     | b
----------+-------+------
Acme Ltd  | true  | false
Beta Corp | false | false
Gamma Inc | true  | null
```

### 5.6 `in`

`value in [a, b, ...]` is true when the value equals one of the list's items. The items take the value's type, as
constants do (section 4.4). An empty list gives false. `null` in the list is refused, as it never matches; test for
it with `== null`.

`value in query` is true when the query's one column has the value. The query must have exactly one column.

```gdq
shop.orders.where(status in ['open', 'shipped'] and order_date in ['2026-01-05', '2026-01-09']).select(id)
```

```gdq
shop.orders.where(customer_id in shop.customers.where(city == 'Cape Town').select(id)).select(id).orderBy(id)
```

To negate it, write `not (x in [...])`.

```gdq-error
shop.orders.where(status in ['open', null])
```

### 5.7 `??` and `? :`

`a ?? b` is `coalesce(a, b)`: `a` when it isn't null, else `b`. `c ? a : b` is `a` when `c` is true, and `b`
when `c` is false or null; it is the same as `iif(c, a, b)`. Both group from the right:
`a > 100 ? 'big' : a > 10 ? 'medium' : 'small'`.

```gdq
shop.customers.select(name, city: city ?? 'unknown', size: credit_limit > 1000 ? 'big' : 'small').orderBy(name)
```

```text
name      | city         | size
----------+--------------+------
Acme Ltd  | Cape Town    | big
Beta Corp | Johannesburg | small
Gamma Inc | unknown      | small
```

### 5.8 Member access, brackets and spread

- `a.b` is the member `b` of `a`: a column or navigation of a row, an entity of a namespace, or a member of a record.
- `a["b"]` is the same, for any name (section 2.3).
- `a.*` stands for all of `a`'s columns (not its navigations, nor hidden columns), each under its own name. It is
  allowed only as an item of `select`, `extend`, `join`, `leftJoin` and `selectMany` (GDQ2018 elsewhere).

A query has no members: `shop.orders.total` is refused (GDQ2014); take a column from each row with
`.select(total)`, or a single value with an aggregate.

### 5.9 Calling functions as methods

A function can be called on its first argument: `x.f(a)` is `f(x, a)`. So `name.lower()` is `lower(name)`, and
calls can be chained:

```gdq
shop.customers.where(name.startsWith('Ac') or name.contains('eta')).select(name, short: name.upper().left(3))
```

On a query, the names of methods (section 7) are methods; on a single value, they are functions. `contains` is both:
`shop.orders.select(id).contains(1003)` asks whether the query has the value, and `name.contains('eta')` whether the
text contains `eta`.

## 6. Rows and scopes

### 6.1 The implicit row and `it`

A method's argument is evaluated for each row. Inside it, the row's columns, navigations and other members are named
directly, and `it` is the row itself:

```gdq
shop.orders.where(total > 50).select(id, it.total, it["status"])
```

`it` is useful for names that aren't plain identifiers (`it["Display Name"]`), for names that the row shares with
something outside it, and to select the whole row as a record.

### 6.2 Lambdas

An argument may instead be a lambda, `o => ...`, which names the row. Inside a lambda, the row's members are reached
only through the name (`o.total`); bare names and `it` refer to enclosing rows (section 6.6).

```gdq
shop.orders.where(o => o.total > 100 and o.customer.city == 'Cape Town').select(id)
```

```gdq-error
shop.orders.where(o => total > 100)
```

Lambdas are allowed as the arguments of `where`, `orderBy` (and the other sorts), `groupBy`, `first`,
`firstOrDefault`, `selectMany`'s collection, the aggregates (`count`, `sum`, `any`, ...) and, with two parameters,
`join`'s condition: `(o, c) => o.customer_id == c.id`. They are not allowed in `select` or `extend`, whose items are
expressions over the row (GDQ2008), and a lambda must have as many parameters as its method gives rows (GDQ2019).

### 6.3 `outer` and `inner`

In a join's condition and items, the two rows are `outer` (the left side) and `inner` (the right side). The same
names are the row and the element in `selectMany`'s items. Bare column names are not in scope there.

```gdq
shop.orders.join(shop.customers, outer.customer_id == inner.id).select(outer.id, inner.name).orderBy(id)
```

```gdq-error
shop.orders.join(shop.customers, customer_id == id)
```

### 6.4 `key`

After `groupBy(...)`, each row is a group. Its members are the key parts, and `key`: the key part itself when there
is one, or a record of the parts when there are several (`key.y`, `key.m`). When a key part is named `key`, that part
is `key`.

```gdq
shop.orders.groupBy(customer_id).select(customer_id: key, spend: sum(total), orders: count())
```

### 6.5 Navigations are joins

A forward navigation used in a query (`customer.name`) is a join to the principal's row; the engine joins once for
all the columns read through the same navigation. Through an optional navigation, a missing row gives nulls; the row
itself is kept. A collection navigation (`orders.count()`) is a subquery over the rows that refer to the current one.

```gdq
shop.orders.where(customer.city == 'Cape Town').select(id, customer.name).orderBy(id)
```

### 6.6 Outer rows (correlation)

Inside a query nested in a method's argument, the nested query's own row comes first, and names it doesn't have come
from the enclosing rows. A nested query that refers to an enclosing row is computed for each of those rows.

```gdq
wh.customers.where(orders.any(total > credit_limit / 10)).select(name)
```

Here `total` belongs to each order, and `credit_limit` (which orders don't have) to the customer.

When both rows have a column of the same name, the nested row wins. Name the enclosing row with a lambda to reach
it. This counts each employee's colleagues with the same manager:

```gdq
shop.employees.select(name, peers: shop.employees.where(e => e.manager_id == manager_id).count()).orderBy(name)
```

```text
name | peers
-----+------
Ann  |     0
Ben  |     2
Cal  |     2
```

`e` is the inner employee and the bare `manager_id` the outer one. (Ann's manager is null, so her comparison is never
true.) Writing `shop.employees.count(manager_id == it.manager_id)` would compare each inner row with itself, since
`it` is the innermost implicit row.

### 6.7 How a name is found

A bare name is looked up in this order:

1. `$name` is a parameter; `alias::rest` starts from the catalog root (section 2.5).
2. Then the scopes, from the innermost outwards. At each:
   - a lambda: its parameter's name (matched exactly);
   - a join's condition and items, and `selectMany`'s items: `outer` and `inner`;
   - a method's implicit row: `it`, then the row's members (columns, navigations, records); after `groupBy`, the
     group's own members (key parts and `key`) and then the members of the grouped rows (section 7.7);
   - the argument of an aggregate over a group's collection (`o.sum(total)` and `total.any(it > 100)`, section 7.7):
     `it` is each element, and for a collection of records its members are named directly.
3. Named subtrees.
4. The catalog: sources, and the namespaces of virtual entities.

Members match without regard to case, with an exact-case tie-break (section 2.2). A name found nowhere is an error
(GDQ2001) that suggests the closest name.

## 7. Query methods

Methods apply to queries: an entity, a named query, a collection navigation, or the result of another method. Their
names match without regard to case.

Filtering, sorting and paging keep the rows of the entity they start from: such rows still have their navigations,
can be edited (section 10) and keep their key. `select` and the other methods that shape new rows don't.

### 7.1 `where`

`.where(condition)` keeps the rows for which the condition is true; rows where it is false or null are dropped.

- One condition; combine several with `and` (GDQ2005 for more arguments), or chain `where`s.
- The condition must be a true/false value (GDQ2007).
- After `groupBy`, `where` filters groups and may use aggregates (section 7.7).

```gdq
shop.orders.where(o => o.status == 'open').where(total > 100).select(id)
```

```gdq-error
shop.orders.where(status: 'open')
```

### 7.2 `select`

`.select(item, ...)` gives one new row for each row, with the items as its members.

An item is an expression, optionally named:

- `name: expression` names it; the name may be quoted to be any text: `'Order Total': total`.
- An unnamed item takes the last name in a path (`customer.name` gives `name`, `it["Display Name"]` gives
  `Display Name`), a parameter's name without `$`, or else the expression's text with its spacing made single
  (`total * 2`, `lower(status)`).
- `x.*` adds each column of the record `x` under its own name (`it.*` for the row's own columns).
- Two items with the same name are refused (GDQ2010).
- An item may be a record, such as a forward navigation (`customer`) or `it`: the result shows it by its entity's
  display column (section 10), and a later method can use its members (`customer.name`).
- An item can't be a collection (GDQ2015); aggregate it.
- Items can't refer to each other; use `extend` to add values that later items use.

```gdq
shop.orders.select(id, 'Order Total': total, $n, lower(status)).orderBy(id)
```

```text
id   | Order Total | n | lower(status)
-----+-------------+---+--------------
1001 |      250.00 | 3 | open
1002 |       99.50 | 3 | shipped
1003 |       12.25 | 3 | open
1004 |        0.00 | 3 | cancelled
```

```gdq
shop.orders.select(order_id: id, customer.*).orderBy(order_id)
```

```gdq
shop.orders.select(id, c: customer, a: ship_address).orderBy(id)
```

```text
id   | c         | a
-----+-----------+----------
1001 | Acme Ltd  | 1 Main Rd
1002 | Acme Ltd  | 1 Main Rd
1003 | Beta Corp | 9 High St
1004 | Gamma Inc | null
```

A selected record can be used afterwards:

```gdq
shop.orders.select(id, customer).where(customer.city != null).select(id, customer.name).orderBy(id)
```

```gdq-error
shop.orders.select(a: total, b: a + 1)
```

```gdq-error
shop.orders.select(id, customer.*)
```

The last one fails because `customer.*` adds a second `id`.

```gdq-error
shop.customers.select(name, addresses)
```

### 7.3 `extend`

`.extend(item, ...)` adds members to the rows and keeps the ones they have, so the rows keep their entity: its
navigations, keys and edits. Its items are written as in `select`; a name the rows already have is refused (GDQ2010).

```gdq
shop.orders.extend(gross: total * 1.15).where(gross > 100).select(id, gross, customer.name)
```

```gdq-error
shop.orders.extend(total: 1)
```

### 7.4 Sorting: `orderBy`, `orderByDescending`, `thenBy`, `thenByDescending`

`.orderBy(key, ...)` sorts the rows by the keys, the first key first. Each key is an expression over the row, or a
lambda. `desc(key)` sorts that key in descending order and `asc(key)` in ascending order.

- `.orderByDescending(key, ...)` sorts every key in descending order, unless `asc(...)` says otherwise.
- `.thenBy(key, ...)` and `.thenByDescending(key, ...)` add keys to the sort right before them; they must follow
  `orderBy` or another `thenBy` (GDQ2023).
- `.orderByDesc(...)` and `.thenByDesc(...)` are shorter spellings of `orderByDescending` and `thenByDescending`.
- Nulls sort first in ascending order and last in descending order (section 9).
- A key must be a single value that can be ordered: not a record (sort by one of its columns), a guid, binary data,
  `json` or an unknown type (GDQ2024).
- Without a sort, the order of the rows is whatever the database gives.

```gdq
shop.orders.orderBy(desc(total), order_date).thenByDescending(id).select(id, total)
```

```gdq
shop.orders.orderBy(status).thenBy(desc(total)).select(id, status, total)
```

```gdq
shop.orders.orderByDescending(total, asc(id)).select(id, total)
```

```gdq
shop.orders.orderByDesc(total).thenByDesc(id).select(id, total)
```

```gdq
shop.orders.orderBy(o => o.customer.name, desc(id)).select(id, customer.name)
```

```gdq
shop.customers.orderBy(city).select(name, city)
```

```text
name      | city
----------+-------------
Gamma Inc | null
Acme Ltd  | Cape Town
Beta Corp | Johannesburg
```

```gdq-error
shop.orders.thenBy(id)
```

```gdq-error
shop.orders.orderBy(customer)
```

### 7.5 `take` and `skip`

`.take(n)` keeps the first `n` rows, and `.skip(n)` drops the first `n`. `n` is a whole number of zero or more
written in the query, or a whole-number parameter; it can't depend on the rows (GDQ2011).

Sort before paging: `take` without a sort keeps any `n` rows, and a later sort only sorts those.

```gdq
shop.orders.orderBy(id).skip(1).take($n).select(id)
```

```gdq
shop.orders.orderBy(desc(total)).take(2).select(id, total)
```

```gdq-error
shop.orders.take(total)
```

### 7.6 `distinct`

`.distinct()` drops repeated rows. It takes no arguments: to keep the distinct values of some columns, select them
first (GDQ2005).

```gdq
shop.orders.select(status).distinct().orderBy(status)
```

```gdq-error
shop.orders.distinct(status)
```

### 7.7 `groupBy`

`.groupBy(part, ...)` groups the rows that have the same values for the parts. Each result row is a group.

**Key parts** are written like `select` items: `customer_id`, `y: year(order_date)`, a lambda `o => o.status`, or a
named lambda `open: o => o.status == 'open'`. An unnamed part takes its name as a `select` item would. A part may be:

- a value (not `json` or an unknown type, GDQ2006);
- a forward navigation, such as `customer`: the groups are by its key (the foreign key's values), and the group has
  the navigation as a record (`customer.name`, `customer.city`);
- nothing: `groupBy()` makes one group of all the rows, which is there even when there are no rows.

**In a group**, the names in scope are:

- the key parts, by name, and `key` (section 6.4);
- the members of the grouped rows, as collections of their values: `total` is the totals of the group's rows.

A collection used as a single value must be a key part, or a member of one (`customer.name` after `groupBy(customer)`);
otherwise it is refused (GDQ2027). An expression written exactly as a key part, such as `year(order_date)` after
`groupBy(year(order_date))`, is that part.

**Aggregates** reduce a group's rows to one value. Called bare, they evaluate their argument for each grouped row:

- `count()` counts the rows; `count(condition)` the rows where it is true; `count(value)` the rows where the value
  isn't null.
- `countDistinct(value)`, `sum(value)`, `avg(value)`, `min(value)`, `max(value)`.
- `any(condition)` and `all(condition)`; `any()` is always true, as a group always has rows.
- `it.count()` or `g.count()` (with a lambda parameter `g`) call them on the group.

Called on a collection, they reduce it: `total.sum()`, `total.max()`, `customer_id.countDistinct()`,
`id.contains(1001)`, and `total.any(it > 100)`, where `it` is each value. A collection of records (a record member of
joined rows, section 7.8) takes an argument over the record: `o.sum(total)`, `o.count()`.

Aggregates can't be nested (GDQ2004), and an aggregate's argument must be a value of each grouped row, not of the
group (GDQ2008). Outside a group, aggregates are methods of queries (section 7.11): bare `count()` without `groupBy`
is refused (GDQ2004).

**After `groupBy`:** `where` filters the groups (like SQL's `HAVING`), `orderBy` sorts them, and `select`, `extend`
and `first` shape them. Groups can't be grouped again, joined, combined with set operations, or aggregated as a query
before they are selected into rows (GDQ2008).

```gdq
shop.orders.groupBy(customer_id).select(customer_id, spend: sum(total), orders: count()).orderBy(customer_id)
```

```text
customer_id | spend  | orders
------------+--------+-------
          1 | 349.50 |      2
          2 |  12.25 |      1
          3 |   0.00 |      1
```

```gdq
shop.orders.groupBy(status).where(count() > 1).select(status, n: count(), mean: avg(total), big: any(total > 100), all: all(total > 10))
```

```text
status | n | mean    | big  | all
-------+---+---------+------+-----
open   | 2 | 131.125 | true | true
```

```gdq
wh.orders.groupBy(y: year(order_date), m: month(order_date)).select(y, m, n: count(), total: sum(total)).orderBy(y, m)
```

```text
y    | m | n | total
-----+---+---+-------
2026 | 1 | 2 | 349.50
2026 | 2 | 2 |  12.25
```

```gdq
shop.orders.groupBy(y: year(order_date), m: month(order_date)).select(key.y, key.m, n: it.count())
```

```gdq
shop.orders.groupBy(year(order_date)).select(year(order_date), n: count())
```

```gdq
shop.orders.groupBy(customer).select(customer.name, customer.city, n: count()).orderBy(name)
```

```gdq
shop.orders.groupBy(status, open: o => o.status == 'open').select(status, open, n: count()).orderBy(status)
```

```gdq
shop.orders.groupBy(status).select(status, over50: count(total > 50), shipped: count(ship_address)).orderBy(status)
```

```gdq
shop.orders.groupBy(status).select(status, totals: total.sum(), biggest: total.max(), has1001: id.contains(1001)).orderBy(status)
```

```gdq
shop.orders.groupBy(o => o.status).where(g => g.count() > 1).select(status)
```

```gdq
shop.orders.groupBy(status).extend(n: count()).where(n > 1).select(status, n)
```

```gdq
shop.orders.groupBy(status).orderBy(desc(count()), status).select(status, n: count())
```

```gdq
shop.orders.groupBy().select(n: count(), total: sum(total), low: min(total), high: max(total))
```

```gdq
shop.customers.groupBy(city).select(city, addresses: sum(addresses.count())).orderBy(city)
```

```gdq-error
shop.orders.groupBy(customer_id).select(total)
```

```gdq-error
shop.orders.groupBy(customer_id).select(m: max(count()))
```

```gdq-error
shop.orders.select(n: count())
```

### 7.8 `join` and `leftJoin`

`.join(other, condition, item, ...)` pairs each row with the rows of `other` for which the condition is true.
`.leftJoin(...)` also keeps the rows that have no partner, with an absent `inner` (its values are null).

- `other` is a query: an entity, a named query, or any other query (GDQ2016 if it isn't one).
- The condition uses `outer` (this side's row) and `inner` (the other's), or is a lambda with two parameters,
  `(o, c) => ...`. It may be any true/false expression, not only an equality.
- The items say what the joined rows hold, as `select` items over `outer` and `inner`: `id: outer.id`, `inner.name`
  (named `name`), `outer.*`. Without items, each joined row has two record members, `outer` and `inner`.
- Rows that are groups can't be joined; select from them first.

Navigations are usually simpler than joins: `customer.name` is a join already. Use `join` for relations that aren't
navigations, or for conditions other than a key.

```gdq
shop.orders.join(shop.customers, (o, c) => o.customer_id == c.id and c.city != null, id: outer.id, who: inner.name, inner.city).orderBy(id)
```

```gdq
shop.orders.leftJoin(shop.addresses, outer.bill_address_id == inner.id, id: outer.id, line: inner.line1).orderBy(id)
```

```text
id   | line
-----+----------
1001 | 1 Main Rd
1002 | null
1003 | 9 High St
1004 | null
```

Joined rows can be grouped; a record member is then a collection of records:

```gdq
shop.orders.join(shop.customers, outer.customer_id == inner.id, o: outer, c: inner).groupBy(c.city).select(city: c.city, n: o.count(), spend: o.sum(total)).orderBy(city)
```

```gdq-error
shop.orders.join(shop.customers, (o) => o.customer_id == 1)
```

### 7.9 `selectMany`

`.selectMany(collection)` gives, for each row, the rows of a collection that depends on it, all in one list. The
collection is usually a collection navigation (`orders`), but it can be any query, also through a lambda
(`o => o.order_lines.where(qty > 1)`), and also one that doesn't depend on the row (which pairs every row with every
element).

- Without items, the result's rows are the elements (with their entity, its navigations and keys).
- With items, `.selectMany(collection, item, ...)`, the items are written as in `select`, with `outer` for the row
  and `inner` for the element.

```gdq
wh.customers.selectMany(orders, who: outer.name, total: inner.total).orderBy(who, total)
```

```text
who       | total
----------+-------
Acme Ltd  |  99.50
Acme Ltd  | 250.00
Beta Corp |  12.25
Gamma Inc |   0.00
```

```gdq
shop.orders.where(id == 1002).selectMany(order_lines).select(order_id, line_no, product_code)
```

```gdq
shop.orders.selectMany(o => o.order_lines.where(qty > 1), order: outer.id, outer.status, inner.product_code, inner.qty).orderBy(order)
```

```gdq-error
shop.customers.selectMany(name)
```

### 7.10 Set operations: `union`, `concat`, `intersect`, `except`

| Method | Rows |
|---|---|
| `.union(other)` | rows of either side, each once |
| `.concat(other)` | rows of both sides, repeats kept |
| `.intersect(other)` | rows on both sides, each once |
| `.except(other)` | rows of this side that the other doesn't have, each once |

- **Columns are matched by name, not by position.** Both sides must have the same column names; the result has the
  left side's order. Each column's two types need a common type (section 4.5).
- The sides must be rows of columns: a record member (`customer`) is refused; select its columns (`customer.*`).
- Rows that are groups must be selected first.
- When both sides are rows of the same entity, the result keeps it (and its navigations).

```gdq
shop.orders.select(id, total, status).union(wh.orders.where(total > 50).select(status, id, total)).orderBy(id)
```

```text
id   | total  | status
-----+--------+----------
1001 | 250.00 | open
1002 |  99.50 | shipped
1003 |  12.25 | open
1004 |   0.00 | cancelled
```

```gdq
shop.orders.where(total > 50).concat(shop.orders.where(status == 'open')).select(id).orderBy(id)
```

```gdq
shop.orders.select(customer_id).intersect(shop.addresses.select(customer_id)).orderBy(customer_id)
```

```gdq
shop.orders.select(customer_id).except(shop.addresses.select(customer_id))
```

```gdq-error
shop.orders.union(shop.customers)
```

### 7.11 Aggregates and quantifiers

Called on a query, these methods reduce its rows to one value. On a collection navigation they are computed for
each row (`orders.count()` per customer); on any other query that refers to enclosing rows, too.

| Method | Value | Type |
|---|---|---|
| `.count()` | the number of rows | `int64` |
| `.count(condition)` | the number of rows where the condition is true | `int64` |
| `.count(value)` | the number of rows where the value isn't null | `int64` |
| `.countDistinct(value)` | the number of distinct values, nulls not counted | `int64` |
| `.sum(value)` | the sum; 0 when there are no rows | whole numbers: `int64`; `decimal(p,s)`: `decimal(38,s)`; a decimal without fixed precision: the same; otherwise `double` |
| `.avg(value)` | the average; null when there are no rows | decimals: `decimal`; otherwise `double` |
| `.min(value)`, `.max(value)` | the smallest, largest; null when there are no rows | the value's type |
| `.any()` | whether there are rows | `boolean` |
| `.any(condition)` | whether the condition is true for some row | `boolean` |
| `.all(condition)` | whether no row fails the condition (true when there are no rows) | `boolean` |
| `.contains(value)` | whether the query's one column has the value; the same as `value in query` | `boolean` |

- The argument is an expression over the row, or a lambda.
- `sum`, `avg`, `min`, `max` and `countDistinct` without an argument take the query's only column:
  `shop.orders.select(total).sum()`. With more columns, they need the value (GDQ2005).
- `sum` and `avg` need numbers (GDQ2006). `min` and `max` need values that can be ordered, and not true/false values
  (use `any` and `all`); GDQ2024.
- In `all(condition)`, a row where the condition is null fails it.
- The result of an aggregate is a single value: it can be the query's result, an item, or part of a condition.

```gdq
wh.customers.select(name, n: orders.count(), spend: orders.sum(total), last: orders.max(order_date)).orderBy(name)
```

```text
name      | n | spend  | last
----------+---+--------+-----------
Acme Ltd  | 2 | 349.50 | 2026-01-09
Beta Corp | 1 |  12.25 | 2026-02-01
Gamma Inc | 1 |   0.00 | 2026-02-14
```

```gdq
shop.orders.select(id, lines: order_lines.count(), amount: order_lines.sum(qty * price)).orderBy(id)
```

```gdq
shop.customers.select(name, n: addresses.count(), cities: addresses.countDistinct(city)).orderBy(name)
```

```gdq
shop.orders.where(order_lines.any(product_code == 'P-100')).select(id)
```

```gdq
shop.orders.where(order_lines.all(qty > 1)).select(id)
```

(Order 1004 has no lines, so `all` is true for it.)

```gdq
shop.customers.where(c => shop.orders.all(o => o.customer_id != c.id or o.total > 50)).select(name).orderBy(name)
```

```gdq
shop.orders.where(total > shop.orders.avg(total)).select(id).orderBy(id)
```

```gdq
shop.orders.select(total).sum()
```

```gdq
shop.orders.select(id).contains(1003)
```

```gdq
shop.orders.max(o => o.total)
```

```gdq
shop.orders.where(total > 1000).groupBy().select(n: count(), total: sum(total), low: min(total), mean: avg(total))
```

```text
n | total | low  | mean
--+-------+------+-----
0 |  0.00 | null | null
```

```gdq-error
shop.orders.sum()
```

### 7.12 `first` and `firstOrDefault`

`.first()` is the query's first row; `.first(condition)` the first row for which the condition is true (as
`.where(condition).first()`). `.firstOrDefault()` and `.firstOrDefault(condition)` are the same, but allow there
to be no row. Without a sort, the first row is any row.

**As the result**, `first()` gives one row; when there is none, the query fails when it runs ("first() found no rows;
use firstOrDefault() when there may be none"). `firstOrDefault()` gives one row or none. A navigation from it gives
that row, or none:

```gdq
shop.orders.orderBy(desc(total)).first()
```

```text
id   | customer_id | ship_address_id | bill_address_id | status | total  | order_date | placed_at
-----+-------------+-----------------+-----------------+--------+--------+------------+--------------------------
1001 |           1 |               1 |               1 | open   | 250.00 | 2026-01-05 | 2026-01-05 08:30:00+00:00
```

```gdq
shop.orders.firstOrDefault(total > 1000)
```

```gdq
shop.orders.orderBy(desc(id)).first().ship_address
```

**In an expression**, both give a record, which is null when there is no row: take a member (`.total`), compare it
with `null`, or select it. Every value taken from it comes from the same row.

```gdq
wh.customers.select(name, latest: orders.orderBy(desc(order_date)).first().order_date, open: orders.firstOrDefault(status == 'open').id).orderBy(name)
```

```text
name      | latest     | open
----------+------------+-----
Acme Ltd  | 2026-01-09 | 1001
Beta Corp | 2026-02-01 | 1003
Gamma Inc | 2026-02-14 | null
```

```gdq
wh.customers.where(orders.orderBy(order_date).firstOrDefault(status == 'open') == null).select(name)
```

```gdq
shop.orders.groupBy(status).orderBy(desc(count()), status).first().status
```

A first row is one row, not a query: it has no methods (GDQ2003), and it can't be a group key (GDQ2008). The first
row of groups has the group's key parts as members, not its rows.

```gdq-error
shop.orders.first().where(total > 1)
```

## 8. Functions

Functions are called by name, `f(a, b)`, or on their first argument, `a.f(b)` (section 5.9). Names match without
regard to case. A function gives null when an argument is null, unless the table says otherwise. Constants among the
arguments take the type the function expects, as in comparisons (section 4.4).

### 8.1 Text

| Function | Gives | Meaning |
|---|---|---|
| `lower(text)` | text | lower case |
| `upper(text)` | text | upper case |
| `trim(text)` | text | without spaces at either end |
| `ltrim(text)` | text | without spaces at the start |
| `rtrim(text)` | text | without spaces at the end |
| `length(text)` | `int32` | the number of characters |
| `substring(text, start[, length])` | text | the part from `start` (counting from 1), to the end or of `length` characters |
| `indexOf(text, find)` | `int32` | where `find` first starts, counting from 1; 0 when it isn't there |
| `replace(text, find, replacement)` | text | every `find` replaced |
| `left(text, count)` | text | the first `count` characters |
| `right(text, count)` | text | the last `count` characters |
| `startsWith(text, find)` | `boolean` | whether the text starts with `find` |
| `endsWith(text, find)` | `boolean` | whether the text ends with `find` |
| `contains(text, find)` | `boolean` | whether `find` is in the text |
| `icontains(text, find)` | `boolean` | the same, ignoring case |
| `like(text, pattern)` | `boolean` | whether the text matches the pattern |
| `ilike(text, pattern)` | `boolean` | the same, ignoring case |
| `concat(value, value, ...)` | text | the values joined as text; nulls count as empty text; never null |

Notes:

- `startsWith`, `endsWith`, `contains` and `icontains` look for the text as it is: `%` and `_` in `find` are plain
  characters.
- A `like` pattern matches the whole text: `%` stands for any run of characters (also none), `_` for one character,
  and `\` makes the next character plain (`\%`, `\_`, `\\`). In a quoted string the backslash itself must be escaped:
  `'50\\% off'`, or write the pattern as raw text: `` `50\% off` ``.
- `like` and `contains` compare as the database compares text (section 9); `ilike` and `icontains` ignore case in
  every database.
- `concat` takes two or more values of any type but binary data and unknown types; numbers, dates and true/false
  values are written as text (true/false values as `true` and `false` in every database).

```gdq
shop.customers.select(name, s: substring(name, 2, 3), i: indexOf(name, ' '), z: indexOf(name, 'z'), l: left(name, 3), r: replace(name, ' ', '_')).orderBy(name)
```

```text
name      | s   | i | z | l   | r
----------+-----+---+---+-----+----------
Acme Ltd  | cme | 5 | 0 | Acm | Acme_Ltd
Beta Corp | eta | 5 | 0 | Bet | Beta_Corp
Gamma Inc | amm | 6 | 0 | Gam | Gamma_Inc
```

```gdq
shop.customers.select(name, l: like(name, 'acme%'), i: ilike(name, 'acme%'), c: contains(name, 'Ltd'), ic: icontains(name, 'LTD')).orderBy(name)
```

```text
name      | l     | i     | c     | ic
----------+-------+-------+-------+------
Acme Ltd  | false | true  | true  | true
Beta Corp | false | false | false | false
Gamma Inc | false | false | false | false
```

```gdq
shop.customers.where(startsWith(name, 'Ac') or endsWith(name, 'Inc')).select(name, upper(name), length(name), t: trim('  x  '), lt: ltrim('  x'), rt: rtrim('x  '))
```

```gdq
shop.customers.select(a: like('50% off', '50\\% off'), b: like('50x off', `50\% off`), c: startsWith('50% off', '50%'), d: right(name, 3)).take(1)
```

### 8.2 Numbers

| Function | Gives | Meaning |
|---|---|---|
| `abs(number)` | the number's type | the absolute value |
| `round(number[, digits])` | the number's type | rounded to `digits` places after the point (0 when not given); halves round away from zero |
| `floor(number)` | the number's type | the largest whole number not above it |
| `ceiling(number)` | the number's type | the smallest whole number not below it |
| `power(base, exponent)` | `double` | `base` to the power `exponent`; also written `base ** exponent` |
| `sqrt(number)` | `double` | the square root |
| `sign(number)` | `int32` | -1, 0 or 1 |

`round`, `floor` and `ceiling` keep their argument's type, so a `decimal(10,2)` rounded to one place still shows two
places: a `price` of 2.45 rounds to `2.50`.

```gdq
shop.order_lines.select(price, a: abs(-price), r: round(price), r1: round(price, 1), f: floor(price), c: ceiling(price), p: power(qty, 2), s: sqrt(qty), sg: sign(qty - 2)).orderBy(price)
```

### 8.3 Dates and times

| Function | Gives | Meaning |
|---|---|---|
| `year(date)` | `int32` | the year of a date, date-time or date-time with offset |
| `month(date)` | `int32` | the month, 1 to 12 |
| `day(date)` | `int32` | the day of the month |
| `hour(time)` | `int32` | the hour of a time, date-time or date-time with offset (not a date) |
| `minute(time)` | `int32` | the minute |
| `second(time)` | `int32` | the second |
| `date(dateTime)` | `date` | the date part |
| `now()` | `datetime` | the current date and time in UTC |
| `today()` | `date` | the current date in UTC |
| `addDays(date, days)` | the date's type | the date plus a (possibly negative) number of days |
| `addMonths(date, months)` | the date's type | the date plus a number of months, kept in the month it lands in: 31 January plus a month is 28 February |
| `daysBetween(from, to)` | `int32` | the number of days from `from` to `to`, negative when `to` is earlier |
| `startOfWeek(date)` | `date` | the Monday of the date's week |
| `startOfMonth(date)` | `date` | the first day of its month |
| `startOfQuarter(date)` | `date` | the first day of its quarter: 1 January, 1 April, 1 July or 1 October |
| `startOfYear(date)` | `date` | 1 January of its year |
| `quarter(date)` | `int32` | the quarter, 1 to 4 |
| `dayOfWeek(date)` | `int32` | the day of the week, 1 (Monday) to 7 (Sunday) |

Notes:

- `now()` and `today()` are the moment the query started to run (the engine's clock, `QueryEngineOptions.Clock`),
  the same everywhere in the query and in every source it reads.
- The parts of a date-time with offset (`year` to `second`, `date`, `quarter`, `dayOfWeek`) are those of its UTC
  time, and `addDays`, `addMonths`, `daysBetween` and the `startOf…` functions work on its UTC time too (section 9).
- `daysBetween` counts the change of date: from 23:00 on one day to 01:00 on the next is 1.
- `startOfWeek`, `startOfMonth`, `startOfQuarter` and `startOfYear` give a date whatever they are given, which makes
  them the periods to group by: weeks start on Monday (as ISO 8601 has them), and `dayOfWeek` counts from Monday,
  whatever the database's or the session's settings say.

```gdq
shop.orders.groupBy(m: startOfMonth(order_date)).select(m, n: count(), total: sum(total)).orderBy(m)
```

```text
m          | n | total
-----------+---+-------
2026-01-01 | 2 | 349.50
2026-02-01 | 2 |  12.25
```

```gdq
shop.orders.where(placed_at != null).select(id, w: startOfWeek(placed_at), q: startOfQuarter(placed_at), y: startOfYear(placed_at), qn: quarter(placed_at), wd: dayOfWeek(placed_at)).orderBy(id)
```

```gdq-error
shop.orders.select(id, m: startOfMonth(status))
```

```gdq
shop.orders.select(id, y: year(order_date), m: month(order_date), d: day(order_date), next: addMonths(order_date, 1), plus: addDays(order_date, 30), days: daysBetween(order_date, toDate('2026-03-01'))).orderBy(id)
```

```gdq
shop.orders.where(placed_at != null).select(id, placed_at, h: hour(placed_at), mi: minute(placed_at), s: second(placed_at), d: date(placed_at)).orderBy(id)
```

```text
id   | placed_at                 | h | mi | s | d
-----+---------------------------+---+----+---+-----------
1001 | 2026-01-05 08:30:00+00:00 | 8 | 30 | 0 | 2026-01-05
1002 | 2026-01-10 04:15:00+00:00 | 4 | 15 | 0 | 2026-01-10
1003 | 2026-01-05 06:00:00+00:00 | 6 |  0 | 0 | 2026-01-05
```

(Order 1001 was placed at 10:30 at +02:00, which is 08:30 UTC.)

```gdq
shop.orders.select(id, age: daysBetween(order_date, today()), now: now()).orderBy(id)
```

### 8.4 Nulls and conditions

| Function | Gives | Meaning |
|---|---|---|
| `coalesce(value, fallback, ...)` | the common type | the first value that isn't null; null only when all are. `a ?? b` is `coalesce(a, b)` |
| `nullif(value, sameAs)` | the value's type | null when the two are equal, else the value |
| `iif(condition, whenTrue, whenFalse)` | the common type | `whenTrue` when the condition is true, else `whenFalse`; the same as `condition ? whenTrue : whenFalse` |
| `between(value, low, high)` | `boolean` | whether `low <= value` and `value <= high`: both ends included |

The values of `coalesce` and the two branches of `iif` need a common type (section 4.5).

```gdq
shop.customers.select(name, a: coalesce(city, 'none'), b: nullif(city, 'Cape Town'), c: iif(credit_limit > 1000, 'big', 'small'), d: between(credit_limit, 1000, 5000)).orderBy(name)
```

```text
name      | a            | b            | c     | d
----------+--------------+--------------+-------+-----
Acme Ltd  | Cape Town    | null         | big   | true
Beta Corp | Johannesburg | Johannesburg | small | true
Gamma Inc | none         | null         | small | null
```

```gdq
shop.orders.where(between(order_date, '2026-01-01', '2026-01-31')).select(id)
```

### 8.5 Conversions

| Function | Gives | Takes | Meaning |
|---|---|---|---|
| `toInt(value)` | `int32` | a number, text or true/false | a whole number; decimals and doubles are cut toward zero |
| `toLong(value)` | `int64` | a number, text or true/false | the same, 64-bit |
| `toDouble(value)` | `double` | a number, text or true/false | a double |
| `toDecimal(value[, precision, scale])` | `decimal`, or `decimal(precision,scale)` | a number, text or true/false | a decimal; with a scale, rounded to it |
| `toString(value)` | text | anything | the value as text |
| `toDate(value)` | `date` | text, a date or date-time | a date; a date-time loses its time |
| `toDateTime(value)` | `datetime` | text, a date or date-time | a date-time; a date is at midnight |
| `toBool(value)` | `boolean` | true/false, a number or text | numbers: 0 is false, others true; text: `true`, `t`, `yes`, `y`, `1` and `false`, `f`, `no`, `n`, `0`, in any case |

Notes:

- `toDecimal`'s precision and scale are whole numbers written in the query: precision 1 to 38, scale 0 to the
  precision; both or neither (GDQ2006).
- true converts to 1 and false to 0.
- Text that doesn't convert isn't defined by the language (section 9).
- `toString` of a date-time, time, date-time with offset or double gives the database's text for it (section 9).

```gdq
shop.orders.select(id, t: toString(total), i: toInt(total), l: toLong(total), f: toDouble(total), m: toDecimal(total, 5, 1), b: toBool(total)).orderBy(id)
```

```text
id   | t      | i   | l   | f     | m     | b
-----+--------+-----+-----+-------+-------+------
1001 | 250.00 | 250 | 250 |   250 | 250.0 | true
1002 | 99.50  |  99 |  99 |  99.5 |  99.5 | true
1003 | 12.25  |  12 |  12 | 12.25 |  12.3 | true
1004 | 0.00   |   0 |   0 |     0 |   0.0 | false
```

```gdq
shop.orders.select(id, a: toDate('2026-03-01'), b: toDateTime('2026-03-01 10:00'), c: toDateTime(order_date), d: toBool('yes')).orderBy(id)
```

```gdq-error
shop.customers.select(x: toDecimal(credit_limit, 10))
```

## 9. Nulls and semantics

A query gives the same rows wherever it runs: in one database, split across several, or in the merge engine. This
section says what is the same everywhere, and where the language leaves the result to the database.

### 9.1 Null

Null is the absence of a value.

- `x == null` is true when `x` is null, and `x != null` when it isn't. These never give null.
- Any other comparison with a null value gives null, and so does arithmetic with one. `and`, `or` and `not` follow
  three-valued logic: `false and null` is false, `true or null` is true, and otherwise a null operand gives null.
- `where` keeps only the rows where the condition is true. So `city != 'Cape Town'` drops the rows whose city is
  null (write `city != 'Cape Town' or city == null` to keep them).
- `? :` and `iif` take the second branch when the condition is null.
- Nulls sort first in ascending order and last in descending order, as if they were smaller than any value.
- Aggregates skip nulls. `count()` counts rows, `count(x)` the rows where `x` isn't null. Over no rows, `count` is 0
  and `sum` is 0, while `avg`, `min` and `max` are null.
- `distinct`, `groupBy` and set operations treat nulls as equal to each other.
- `x in [...]` gives null when `x` is null; the list can't hold null.

```gdq
shop.customers.where(city != 'Cape Town').select(name, city)
```

```text
name      | city
----------+-------------
Beta Corp | Johannesburg
```

Gamma Inc, whose city is null, is not in the result.

### 9.2 The same in every database

- Whole numbers divided give a double: `7 / 2` is `3.5`.
- `substring` and `indexOf` count from 1; `indexOf` gives 0 when the text isn't found.
- `concat` treats nulls as empty text, and writes true/false values as `true` and `false`, as `toString` does;
  `+` on text gives null.
- `startsWith`, `endsWith`, `contains` and `icontains` match text as it is; `like` and `ilike` patterns use `%`, `_`
  and `\`.
- `ilike` and `icontains` ignore case.
- `round` rounds halves away from zero; `toInt` and `toLong` cut toward zero.
- `addMonths` keeps to the month it lands in.
- `now()` and `today()` are UTC and fixed for the whole query.
- Date-times with an offset are instants, compared, sorted and taken apart in UTC.
- Guids and binary values have no order (the language refuses to sort them).

### 9.3 Left to the database

These differ between databases, and the language doesn't say which result is right:

- **Text comparison** (`==`, `<`, sorting, `like`, `contains`, `distinct` and grouping of text, `min` and `max` of
  text) follows the database's collation. SQLite and DuckDB compare case-sensitively, and so do PostgreSQL databases
  with the usual collations; SQL Server databases usually ignore case, and queries that run there do too. Use
  `ilike` and `icontains` to ignore case everywhere. What runs in the merge engine compares as DuckDB does. For
  example, `shop.customers.where(name > 'b')` gives no rows in SQLite, where upper-case letters come before
  lower-case ones.
- **Decimal division and averages** have each database's digits: the language gives them no fixed precision
  (PostgreSQL gives 20 places, SQL Server 6 to 10, SQLite and DuckDB a double's).
- **`toString` of date-times, times, date-times with offsets and doubles** is each database's text. SQLite gives
  `placed_at` as it holds it (`2026-01-05 10:30:00+02:00`), DuckDB in UTC (`2026-01-05 08:30:00+00`).
- **Division by zero**: SQLite gives null; DuckDB gives infinity for doubles (a decimal result that is infinite fails
  as a value that doesn't convert); PostgreSQL and SQL Server fail the query.
- **Text that doesn't convert** in `toInt`, `toLong`, `toDouble`, `toDecimal` and `toBool`: SQLite gives a number
  (`toInt('x')` is 0) or null (`toBool('maybe')`), DuckDB fails the query.
- **`char(n)` padding**: PostgreSQL's text functions ignore the padding of `char(n)` columns, which the values read
  keep; text compares with a `char(n)` column without its padding on PostgreSQL and SQL Server.
- **Characters outside the Basic Multilingual Plane** (such as emoji) count as two in SQL Server's `length()`
  without an `_SC` collation, as one elsewhere.
- **Date-times with an offset** are read as instants in UTC; SQL Server's and SQLite's own offsets are not kept.
  SQLite groups offset date-times, and takes their `min` and `max`, by their text.
- `tinyint + tinyint` overflows past 255 in SQL Server (the language's `int16` would not).
- The merge engine keeps date-times to the microsecond.

```gdq
shop.customers.where(name > 'b').select(name)
```

## 10. Results

This section is for developers using the library.

### 10.1 Columns

`PreparedQuery.Schema` (and `QueryResult.Schema`) is a `ResultSchema`, known before the query runs:

- `Columns`: every column in row order, each a `ResultColumn` with `Ordinal`, `Name`, `Type`, `Lineage`, `Link`,
  `EditTarget` and `IsHidden`. Names are any text, and rows are arrays, so read values by ordinal.
- `VisibleColumns`: the columns the query asked for, ordinals 0 to n - 1.
- Hidden columns come after the visible ones. They hold key values that links and edit targets need, such as the
  customer's key behind a selected `customer`. Grids show only the visible columns.
- `Entity`: the entity the rows belong to, when the query only filters, sorts or pages one entity's rows.
- `RowIdentity`: when the query only filters, sorts, pages or extends the rows of one entity that has a key, the
  key's ordinals, and links to the rows that refer to each row (its inverse navigations).

A selected record (`select(customer)`) is one column holding the entity's display column (set in the overlay, or
else a column named `name`, `title`, `display_name`, `displayname`, `label`, `code` or `description`, or the first
text column that isn't the key, or the key), with a link to the row. A scalar result is one column named `value`.

### 10.2 Lineage

`ResultColumn.Lineage` says where the values come from: its `Kind` is `Direct` (one column, perhaps through
navigations), `Computed`, `Aggregated`, `Constant`, `Union` or `Unknown`, with the physical columns (`Sources`, each
with its navigation path) and the expression's text. Virtual entities' columns resolve to the columns they read.

### 10.3 Links, edit targets and row identity

- **`RowLink`**: one row of an entity. Foreign-key columns have one (`customer_id` leads to the customer), and so do
  selected records.
- **`CollectionLink`**: the rows of a collection navigation. Aggregates of a collection (`order_lines.count()`) have
  one, and so do the rows of an entity (`RowIdentity.Related`).
- **`DrillDownLink`**: the rows an aggregate of a group was computed from: the query before `groupBy` (with its
  named subtrees), filtered on each key part as written (a null key part as `== null`).
- **`EditTarget`**: the table, column and key ordinals an edit of the value goes to. A value has one when it is a
  table's column (not a view's) read along a path that keeps rows apart: filters, projections, joins and many-to-one
  navigations, but not `distinct`, grouping or set operations.

Every link's `Query(row, parameters)` gives the `QueryRequest` for the rows it leads to from one result row, with the
row's values as parameters (`$key1`, ...); it is null when the row leads nowhere (a null foreign key).

`gdq explain` shows all of this. For
`shop.orders.where(status == 'open').select(id, total, who: customer.name, c: customer, n: order_lines.count())`:

```text
Columns
  0 id int64: direct shop.orders.id; edits shop.orders.id by id = [0]
  1 total decimal(10,2): direct shop.orders.total; edits shop.orders.total by id = [0]
  2 who string?: direct customer -> shop.customers.name; edits shop.customers.name by id = [5]
  3 c string?: direct customer -> shop.customers.name; edits shop.customers.name by id = [5]; links to row shop.customers(id) = [5] via customer
  4 n int64: aggregated (order_lines.count()); links to rows shop.order_lines(order_id) = [0] via order_lines
  5 id int64? (hidden): direct customer -> shop.customers.id
```

### 10.4 Composing and paging

- `QueryText.Compose(text, filters, sort, tiebreak)` adds filters and a sort to a query's last statement, as
  `(last).where(...).orderBy(...)`, for grids that filter and sort what a user wrote.
- `QueryText.QuoteName(name)` writes a column name for such filters (`it["Total Spend"]` when needed),
  `QueryText.QuoteString(text)` a text literal, and `QueryText.FormatPath(parts)` an entity path.
- `QueryRequest.Paging` (`PageRequest(offset, limit)`) pages the result; the sort is made stable with the row
  identity's key. `PreparedQuery.ForCount()` counts the rows the query gives.

## 11. Changes to data

Changing rows is not part of the query language. The library plans and writes changes separately:

- A `ChangeSet` holds `InsertRow(entity, values)`, `UpdateRow(entity, key, values) { Original = ... }` and
  `DeleteRow(entity, key) { Original = ... }`. Values are by column name.
- `ResultRowEditor.Update(schema, row, values)` and `ResultRowEditor.Delete(schema, row)` make them from a query's
  result rows, through the columns' edit targets and the row identity.
- `QueryEngine.PlanChanges(changes)` checks the changes and writes the statements for each source, without running
  anything; what can't be done is in the plan's `Issues`.
- `QueryEngine.CommitAsync(plan)` runs them in a transaction on each connection, and gives a `DmlResult`.

Only tables (not views or virtual entities) in sources that are writable can be changed. Updates and deletes need the
table's own primary key (a key the overlay declares serves navigation only), and change a row only when the
`Original` values given still hold. Each statement must change exactly one row, or nothing is written.

From the command line, `gdq changes -s ... -w alias -f changes.json` shows the statements of a change file, and with
`--commit` writes them; `-w` makes a source writable (sources are read-only otherwise). `gdq script alias -f edits.sql
[--commit] [--any-statement]` checks and runs a script of SQL statements that change data.

A change file is a JSON array of changes:

```text
[
  { "update": "shop.orders", "key": { "id": 1001 }, "values": { "status": "shipped" }, "original": { "status": "open" } },
  { "insert": "shop.customers", "values": { "id": 4, "name": "Delta LLC", "city": "Durban" } },
  { "delete": "shop.order_lines", "key": { "order_id": 1003, "line_no": 1 } }
]
```

Whole numbers are 64-bit, other numbers decimals, and text converts to the column's type (`"2026-03-01"` for a date).
Values are written into the statements (so the text can be edited and run as a script); line breaks in text are
written by their codes (`('a' || char(10) || 'b')` in SQLite, `chr` in DuckDB, `NCHAR` in SQL Server, an escape
string `E'a\nb'` in PostgreSQL), so the text has none in its values. For this file `gdq changes` prints:

```text
-- shop (SQLite)
INSERT INTO customers (id, name, city)
VALUES (4, 'Delta LLC', 'Durban');

UPDATE orders
SET status = 'shipped'
WHERE id = 1001 AND status = 'open';

DELETE FROM order_lines
WHERE order_id = 1003 AND line_no = 1;
```

## 12. Diagnostics

Problems are `QueryDiagnostic`s: a code, a severity, a message, and the range of the query text they are about
(`Start`, `End`). Binding stops at the first error. Codes starting `GDQ1` are about the text, `GDQ2` about binding
(`GDQ21..` are warnings), `GDQ3` about planning and running (`GDQ31..` are warnings), and `GDQ5` about the catalog
(in `ICatalog.Diagnostics`, shown by `gdq schema`; one about an item of the overlay names it, in `Item`: its list
and index).

| Code | Severity | Meaning |
|---|---|---|
| GDQ1001 | error | The text doesn't parse: an unexpected character, an unterminated string or comment, a bad number or escape, or nesting too deep (section 13). |
| GDQ1002 | error | The query is longer than `QueryEngineOptions.MaxQueryLength`. |
| GDQ2001 | error | A name isn't found: no such column, entity, namespace or subtree here. The message suggests close names. |
| GDQ2002 | error | A name matches several that differ only by case, none exactly. |
| GDQ2003 | error | No such method; or a method called on something that has none (a row, a namespace, a group's collection). |
| GDQ2004 | error | No such function; or an aggregate where there is no group, or inside another aggregate. |
| GDQ2005 | error | The wrong number of arguments, or a missing value to aggregate. |
| GDQ2006 | error | Types that don't fit: values that can't be compared or combined, a function argument of the wrong type, set-operation columns of different kinds. |
| GDQ2007 | error | A condition that isn't a true/false value. |
| GDQ2008 | error | Syntax the language doesn't support (statements, blocks, object literals, lists outside `in`, bitwise operators, lambdas out of place), and things that can't be done with groups or first rows. |
| GDQ2009 | error | `=` or `:=` inside an expression. |
| GDQ2010 | error | Two items, or key parts, of the same name; or `extend` with a name the rows have. |
| GDQ2011 | error | `take` or `skip` without a whole number of zero or more written in the query or given as a parameter. |
| GDQ2012 | error | A parameter without a value. |
| GDQ2013 | error | Text that doesn't read as the date, date-time, time or guid it is compared with. |
| GDQ2014 | error | A member of something that has no members: a query (`shop.orders.total`) or a single value. |
| GDQ2015 | error | A row, collection or query where a single value is needed. |
| GDQ2016 | error | Something that isn't a value or query where one is needed: a namespace, or a join, set operation or `selectMany` argument that isn't a query. |
| GDQ2017 | error | A named argument (`name: value`) where names aren't taken. |
| GDQ2018 | error | `.*` outside the items of `select`, `extend`, joins and `selectMany`, or on something that isn't a row. |
| GDQ2019 | error | A lambda with the wrong number of parameters. |
| GDQ2020 | error | A named subtree defined twice, or named with `$`. |
| GDQ2021 | error | The last statement only names a subtree; there is no result. |
| GDQ2022 | error | A statement before the last that doesn't name a subtree. |
| GDQ2023 | error | `thenBy` not right after `orderBy` or `thenBy`. |
| GDQ2024 | error | Sorting, or taking `min` or `max` of, values that have no order (records, guids, binary, `json`, unknown types, true/false values for `min` and `max`). |
| GDQ2025 | error | A virtual entity that can't be used, because its definition doesn't bind; also in the catalog's diagnostics. |
| GDQ2026 | error | A virtual entity defined in terms of itself. |
| GDQ2027 | error | A member of the grouped rows used as one value, but not part of the group key. |
| GDQ2028 | error | Set operation sides whose columns don't match by name or type, or that have record members. |
| GDQ2099 | error | Reserved for features not supported yet; not raised at present. |
| GDQ2101 | warning | A name hides another: a subtree hides a source, or a row's column hides a subtree. |
| GDQ3001 | error | The query reads several sources (or push-down is off), and the engine has no merge engine. |
| GDQ3002 | error | No provider is registered for a source's kind. |
| GDQ3003 | error | The query can't be written as SQL where it would run, for example a function a source and the merge engine can't run. |
| GDQ3004 | error | There is no source to run the query in. |
| GDQ3005 | error | The plan would be too large (section 13). |
| GDQ3101 | warning | A part of the query is expected to fetch more than `QueryEngineOptions.LargeFetchRows` rows into the merge engine. |
| GDQ5001 | error | A source alias that isn't a plain name, or is a reserved word. |
| GDQ5002 | error | Two sources with the same alias (ignoring case). |
| GDQ5003 | warning | A default-schema table has the name of a schema, so it has no shortcut. |
| GDQ5004 | error | The overlay names an entity that isn't in the catalog. |
| GDQ5005 | error | A key, relation or setting names a column the entity doesn't have. |
| GDQ5006 | warning | A foreign key refers to the primary key of a table that has none. |
| GDQ5007 | warning or error | A relation with different numbers of columns on each side (a foreign key: warning; an overlay relation: error). |
| GDQ5008 | error | An overlay relation whose target columns aren't a key or unique key. |
| GDQ5009 | warning | A relation's column types may not compare equal. |
| GDQ5010 | warning | A foreign key refers to a table that isn't in the catalog. |
| GDQ5011 | error | The overlay renames or hides a navigation that doesn't exist. |
| GDQ5012 | warning or error | A navigation name is taken: an explicit name had to change (warning), or a rename can't be made (error). |
| GDQ5013 | warning | A key declared in the overlay for a table that has a primary key; it is ignored. |
| GDQ5015 | error | An entity path in the overlay that isn't valid, or a virtual entity's name without a namespace, or one that exists already. |
| GDQ5016 | error | Something the overlay gives twice: settings for an entity (or a column) that has some already, an override of a navigation renamed or hidden already, a relation the overlay or the database has already, or a column named twice; the second is left out. |

## 13. Limits

| Limit | Value | When it is passed |
|---|---|---|
| Query length (`QueryEngineOptions.MaxQueryLength`) | 100,000 characters | refused before it is parsed (GDQ1002) |
| Nesting depth (`QueryParser.MaxDepth`) | 256 levels | GDQ1001 |
| Plan size (`PlanIds.Limit`) | 100,000 columns | GDQ3005 |
| `QueryEngineOptions.Timeout` (or `QueryRequest.Timeout`) | none by default | the query is stopped and fails with `QueryTimeoutException` |
| `QueryEngineOptions.MaxFetchedRows` | none by default | the query fails |
| `QueryEngineOptions.CommandTimeout` | the provider's default | as each database applies it (below) |

**Nesting depth.** Every operator and every call in a chain is a level of its own. A query of one statement can hold
a run of 255 `+` operators between plain values, and no more; a chain of methods counts two levels for each
`.method(...)`, so it can hold about 127 methods. Split long chains into named subtrees, and long runs of `or` into
`in [...]`.

**Plan size.** Named subtrees and virtual entities are planned afresh at each use, so subtrees that each use the one
before several times grow quickly:

```gdq-error
a := shop.orders; b := a.concat(a); c := b.concat(b); d := c.concat(c); e := d.concat(d); f := e.concat(e);
g := f.concat(f); h := g.concat(g); i := h.concat(h); j := i.concat(i); k := j.concat(j); l := k.concat(k);
m := l.concat(l); n := m.concat(m); n.count()
```

**Timeout** bounds a query from when it starts until its last row is read, fetches and merging included, in every
database; it also bounds how long changes take to write before they commit (they are then rolled back). `gdq
--timeout seconds` sets it. **MaxFetchedRows** caps the rows one query fetches from its sources into the merge
engine; a query that runs whole in one database fetches none. **CommandTimeout** is passed to each database command;
SQLite takes it as how long to wait for a locked database, and DuckDB doesn't use it.

Other options of `QueryEngineOptions`: `LargeFetchRows` (1,000,000; GDQ3101), `MaxParallelFetches` (4 fetches at a
time), `MaxBindKeys` (10,000) and `MaxBindBatch` (2,000) for fetching a source's rows by the keys of another,
`LenientConversion` (read values that don't convert to their column's type as null instead of failing), and `Clock`
(for `now()` and `today()`).
