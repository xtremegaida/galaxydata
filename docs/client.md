# GalaxyData client

The client is the Angular application the server serves to browsers, in `src/client`. This guide covers
developing it. Running and publishing the whole application is in [server.md](server.md).

## Developing

It needs Node.js 24.15 or later (or 22.22.3 or later), as Angular's tools do; `npm ci` refuses others
(`engines` in `package.json`, with `engine-strict` in `.npmrc`). Install its packages once, in `src/client`:

```bash
npm ci
```

Run the server, then the client's development server beside it:

```bash
dotnet run --project src/GalaxyData.Web
```

```bash
npm start
```

The client is then at `http://localhost:4200`. It is rebuilt as its files change, and it passes requests for
`/api` on to the server at `http://localhost:5180` (`proxy.conf.json`). The server's security headers don't
apply to the development server; they apply to the published client.

**Commands**, run in `src/client`:

| Command | What it does |
|---|---|
| `npm start` | The development server. |
| `npm run build` | Checks that the API's types are current, then makes a production build into `dist/browser`, checked against the content security policy (see [Building](#building)). |
| `npm test` | Checks that the API's types are current, runs the build scripts' tests (`scripts/*.test.mjs`, Node's test runner), then the unit tests, once. |
| `npm run test:watch` | Runs the unit tests as files change. |
| `npm run lint` | ESLint, with Angular's rules for code and templates (accessibility included). |
| `npm run format` | Formats every file with Prettier. |
| `npm run api` | Makes the API's types again (see [The API's types](#the-apis-types)). |

**Publishing replaces `node_modules`.** Publishing the server runs `npm ci`, which removes `node_modules` first.
Stop `npm start` and `npm run test:watch` before: on Windows they hold files there, and `npm ci` fails part way
(`EBUSY`), with packages missing until it runs again.

**Install scripts.** npm 11 warns about packages whose install scripts no `allowScripts` setting covers
(`@parcel/watcher`, `esbuild`, `lmdb`, `msgpackr-extract`). It runs them all the same: the warning is a notice, and
nothing needs approving.

## The API's types

`src/app/core/api/schema.d.ts` holds the API's types. They are made by
[openapi-typescript](https://openapi-ts.dev) from the OpenAPI document the server's tests keep
(`tests/GalaxyData.Web.Tests/Hosting/Snapshots`). The test `HostTests.TheOpenApiDocumentDescribesTheApi`
checks that document against the API.

**When the API changes:**

1. Run the server's tests. The OpenAPI test fails, leaving the new document beside the old one as
   `.received.json`.
2. Review the new document, and accept it: it replaces the old one.
3. Run `npm run api`.

`npm test` and `npm run build` fail while the types are older than the document, so the client is built against
the API as it is.

**Calling the API.** `ApiClient` calls the API's operations, named by their method and path as the document
writes them. Each call gives what its operation takes: the path's values, the query's, and the body. Each call's
answer has the operation's type. A call the document doesn't describe doesn't compile.

```ts
const api = inject(ApiClient);
api.get('/api/users').subscribe((users) => ...);                                  // UserDto[]
api.post('/api/users', { body: { userName, role, password } }).subscribe(...);    // CreateUserRequest, giving a UserDto
api.delete('/api/users/{id}', { path: { id }, query: { version } }).subscribe(); // void
```

As with Angular's `HttpClient`, a request is sent when its answer is subscribed to. A path value that is empty,
`.` or `..` is refused, as it would be read as a step along the path.

The API's schemas are types of their own (`UserDto`), and `Schema<'UserDto'>` names them too.

## Problems

A request that fails gives an `HttpErrorResponse`. `problemOf(error)` reads it as a `Problem`: the answer's status,
the API's `code` (`ProblemCode` names those the client works with), its title and detail, its errors by field,
`Retry-After`, and its trace. No answer, or a proxy's in place of the server's (the development server's while the
server is down), is `unreachable`. `problemMessage` gives a problem as a sentence or two.

`Notifier` tells the user what happened, at the foot of the page: `say(message)` for a few seconds,
`problem(problem)` until they close it. It says nothing of the session's problems, which the client deals with
itself.

## Signing in

**The session.** `AuthStore` holds the session as the server gives it (`GET /api/auth/session`): who is signed
in, and what they may do (`permissions`). It is asked for once, as the first page opens; asking also gives the
anti-forgery token. Signing in, signing out and changing the password go through it.

**Pages.** `/sign-in`, and `/change-password`: chosen from the user's menu, or first, when the password must be
changed. Both take `returnUrl`, where to go after: an address of the application's (read as a browser reads it,
so that one leading to another site doesn't pass), or else the start. They load when they are needed (lazy
routes), so the signed-in don't load their forms.

**Guards** (`core/auth/guards.ts`):

| Guard | Lets in | Sends the others |
|---|---|---|
| `signedInGuard` | the signed-in with no password to change (the shell, and as `canActivateChild` its pages) | to sign in, or to change their password, then back |
| `signedOutGuard` | the signed-out (the sign-in page) | where they were going |
| `passwordGuard` | the signed-in (the password page) | to sign in |
| `allowedTo('canAdmin')` | those allowed, as a `canMatch` or `canActivate` guard | to the start; the signed-out, or those with a password to change, as `signedInGuard` does (a `canMatch` guard runs before the guards above it) |

**What the server says, whichever request meets it** (`sessionInterceptor`):

- **401 `unauthenticated`:** the session ended (it expired, the password was reset, the user was disabled). The
  user signs in again, told why, and comes back where they were.
- **403 `password-change-required`:** the password page.
- **400 `xsrf-token-invalid`:** who is signed in changed since the token was given. The session is asked for
  again, for a new token, and the request sent again, once.

What a request sent under a session that has changed since meets (a slow request's 401, after the user signed in
again) is about that session, and left alone. The request's caller gets the error all the same. A refusal (403
`forbidden`) is the caller's: a role that changes ends the user's sessions, so it comes as a 401.

**Another user.** The application holds in memory what its user could see. When someone else is signed in, or the
same user may now see less (signing in again after an administrator changed their role), it is loaded anew, so
none of it stays: another user at the start, the same user where they were going. Signing out loads it anew too.
The same user signing in again after their session ended goes on with what the application held.

Who is signed in may change in another tab. Tabs tell each other, through a `BroadcastChannel`. A tab that may
have missed it asks the server again: one back from the browser's back-forward cache, or coming into view 30
seconds or more after it last asked. Whatever finds someone else signed in than the tab knew, it is as if they had
signed in in it.

**The catalog's version.** `CatalogVersion` keeps the last `X-Catalog-Version` the server answered with
(`catalogVersionInterceptor`), so that what was read of the catalog can be read again when it changes.

## The shell

Pages for the signed-in are in the shell (`src/app/shell`), which has:

- **The bar at the top:** the navigation's button, the application's name, for those who change data their
  pending changes (a button with their count, opening the drawer that lists them), the color scheme, and the user's
  menu (who they are, changing the password, signing out).
- **The navigation.** `navItems` lists the pages, each with what the user must be allowed to do (`needs:
  'canAdmin'`) and the section it is shown under (`section: 'Administration'`); the navigation shows those the user
  may open. Below 960 pixels it opens over the page, and closes once a page is chosen; on wider screens it is
  beside the page, and may be closed.
- **The page.**
- **The pending changes** (`ChangesDrawer`, `src/app/features/changes`), for those who change data: a drawer at the
  end of the page (beside it from 960 pixels, over it below), loaded when first opened (`@defer`), from which they
  are previewed and committed. See [Changing data](#changing-data).

A route's `title` is shown with the application's name after it: "Sign in · GalaxyData".

## Browsing

`/browse` (`src/app/features/browse`), for everyone who reads data: the catalog, and beside it the page of what is
chosen in it.

- **The tree** (`CatalogTree`, its state in `CatalogTreeStore`): the sources, then their schemas and entities, as
  queries name them (`GET /api/catalog/tree/children`). A node's children are loaded when it is first opened.
  - **What each node says:** a source's kind, whether its schema is being read or couldn't be, and whether it is
    read-only; an entity's kind (table, view or virtual entity) and its row count as the database estimates it. For
    those who change data, a table of a source that takes changes is marked when they can't change its rows (it has
    no primary key). An entity's comment is its row's tooltip.
  - **Only the rows in view are rendered** (the CDK's virtual scrolling, rows of 32 pixels), so a schema of
    thousands of tables opens at once.
  - **Kept:** the tree's state is the application's (`providedIn: 'root'`), so it is as the user left it when they
    come back to browsing.
  - **Following the catalog.** Every answer of the API gives the catalog's version (`X-Catalog-Version`, which
    `CatalogVersion` keeps). When it changes from the one the tree was loaded at, the tree is loaded again:
    - nodes still there stay open, and those gone close (the keyboard goes to the nearest ancestor still there);
    - a node whose children couldn't be loaded again keeps those it had;
    - the children of nodes closed aren't loaded again, but let go: they are loaded as their nodes open, and nodes
      left open under them open again with them;
    - nodes opened as it runs stay open.

    While a source's schema is being read, the tree loads the sources again, as the connection list does; reading
    the schema changes the version, and the rest is loaded again. "Try again", after the tree couldn't be loaded
    again, loads the whole tree. A node's loads are numbered: an answer a later load's outdates doesn't count, and
    what asked for it gets the later one's outcome.
- **Search** (`GET /api/catalog/tree/search`) asks as typing pauses (250 ms). It looks in names, in paths for text
  with a `.` or `[`, and in columns.
  - What it finds is listed in place of the tree, the text marked, with each node's path and the columns found:
    the first 50, then up to 200 on asking.
  - The field is a combobox: the arrow keys go through what was found, Enter chooses (the first found, when it comes
    before the answer), and Escape clears (without closing the catalog over the page; with nothing typed, Escape is
    the page's). "Show more" leaves focus in the field, as it goes once more are found.
  - The node chosen is shown in the tree, its ancestors opened (search gives their ids), and an entity's page
    opens.
- **The address** is a path through the data, and the grid's state at each step (`core/browse/browse-url.ts`, the
  only place its grammar lives):
  `/browse/shop.customers;f=country:eq:ZA;row=42/orders;sort=-placed_at;page=2/lines?at=1`.
  - The first segment is an entity, as queries write its name; each after it a navigation from the row chosen in the
    one before. Each is a crumb.
  - A crumb's state is in its segment's matrix parameters: `f` filters (`column:op:value`, conditions after one
    another, `or` between those any of which will do, `,` between filters), `w` a condition in the query language,
    `sort` (`-` for descending, `,` between columns), `page` (from 1), and `row`, the key of the row chosen (`~`
    between its values). Names and values with `,`, `:`, `~` or `'` in them, or empty, are quoted (`'a,b'`, a quote
    doubled). `?at=` says which crumb is shown, from 0, when it isn't the last.
  - What can't be read (a page that isn't one, an operation that isn't) is left out, and the page says so.
  - The grid's changes replace the address in the history (sorting, filtering, paging and choosing a row aren't
    steps back); going elsewhere is a step: a link followed from a row (a crumb after the one shown), a crumb of the
    path chosen, another entity. Choosing another row in a crumb lets go of those after it.
  - The tree shows the first crumb's entity, opening its ancestors; when it isn't loaded, searching for its name
    finds where it is.
- **The start of browsing** (`/browse`) lists the connections, with how their schemas stand (followed while they are
  read), and what building the catalog found wrong (`GET /api/catalog`).
- **A path's page** (`BrowsePage`) has the entity of the crumb shown as its heading, and two tabs: its rows, and
  what it is. A navigation's crumb is followed through the API's trail (`POST /api/browse/trail`), which says what
  entity each crumb reaches; its rows are those the navigation leads to from the row chosen in the crumb before
  (`{from: {entity, key}, navigation}`). A crumb that can't be followed (no row chosen before it, a row chosen that
  isn't among its crumb's rows, a navigation the entity hasn't) says why. A row chosen in the crumb shown that isn't
  one (a key that can't be read) doesn't keep its rows from being shown. The grid stays as it was while the other
  tab is shown.
  - **The path** (a `nav`, "Path"), when it has more than one crumb: each crumb a link to it (`?at=`), the one shown
    marked (`aria-current`), and the row chosen in each but the last by its display value, as the trail says it
    (`shop.customers (Acme) › orders (1001) › lines`); by its key until the trail comes, and when it can't be read
    (which the path says, with "Try again", when the rows shown don't need it). Crumbs after the one shown stay, to
    go on to.
  - **The trail** is asked for the whole path, with the rows chosen in all but the last crumb, so rows chosen in the
    last one, and showing another crumb, don't ask for it again; nor do crumbs let go of (a row chosen anew in a
    crumb before the last), as the trail asked for already says what one for those left would. While it is read
    again for a change past the crumb shown (back or forward to the same crumbs to it, then others), the steps to
    that crumb are kept: they are the same, and the grid stays.
- **The grid** (`BrowseGrid`, AG Grid Community's infinite row model with pages of 100 rows) shows the rows a page at
  a time from `POST /api/browse/page`:
  - **First,** the rows' schema (their columns) and the page the address names, counted, in one request. When the
    address's state can't be asked for (a column gone, a wrong condition), the schema alone, and the grid says
    what's wrong.
  - **Pages** are asked for as the grid shows them; the rows are counted with the first page of each query
    ("1,234 rows"), or, when counting takes too long, the grid says how many it has seen ("At least 101 rows", and
    "of more" in its pages). A query changed (sorted, filtered) is a datasource of its own: pages of the one before
    that are under way are let go. An address's page past the rows' end shows the last page (the first, when they
    weren't counted), and the address follows. A first page fetched ahead answers one grid only.
  - **Columns** are named by their places (names may be any text), the key's marked, numbers on the right, NULL set
    apart; date-times read with a space, binary values as hexadecimal. A column's header, pointed at, says its type
    and where its values come from.
  - **References:** a column whose values refer to a row (a foreign key; the schema's `references`) shows that
    row's display value (the page's `r`), with the value itself beside it, as a link: following it shows the row
    (a crumb after the one shown: the navigation from the row the cell is in, that row chosen). A value that
    refers to no row (a null among its columns) has no link, nor do rows without a key, which no crumb can follow
    from. Display values of date-times read with a space. A click on a link follows it without choosing its row;
    with Ctrl, Shift or Meta, or the middle button, the browser opens it (a new tab); with Alt (which would download
    it) nothing happens, so its text can be selected. Enter on a cell with a link follows it (`suppressKeyboardEvent`:
    before the grid does anything with the key, and so that a link the pointer left the keyboard on isn't followed
    twice).
  - **Collections:** a column for each collection of rows that refer to the rows (the schema's `collections`,
    inverse navigations), after the rows' columns: a link in each row with a key (`orders ›`, the arrow drawn but
    not read) to the rows that refer to it. Rows without a key have none.
  - **Links** lead where the page says (`linkTo`, from the address: a link from a row of an earlier crumb lets go
    of the crumbs after it, unless they already follow the same navigation from the same row, when they stay). The
    cells say where again as the address changes, in place (`LinkCell`, a renderer that changes what it shows), so
    the keyboard on a link stays there. Without `linkTo` (a grid that isn't browsing's), references show their
    display values without links, and there are no collections.
  - **Filters** follow each column's type: text (contains, starts and ends with, equals); numbers; 64-bit whole
    numbers (AG Grid's filter for them, which holds them as text, so those past 2^53 stay exact); decimals, times and
    intervals in text fields, with comparisons of our own (the server reads the text as the column's type); dates (a
    date stands for its day in a date-time); booleans (true or false); guids (equal or not); and the rest, blank or
    not. A filter holds two conditions, all or any of which a row must meet. The server's operations are the
    address's.
  - **Where:** a condition in the query language over the rows, navigations included (`customer.city == 'Cape
    Town'`), applied on Enter (an unchanged one isn't asked for again). What's wrong with it is said under it,
    placed in it.
  - **Sorting** by the headers (by all but binary, JSON and unknown values); the server adds the key after.
  - **Choosing a row** (clicking it, or Space) puts its key in the address, and the address's row is chosen when
    its page comes. Rows of entities without a key aren't chosen.
  - **Following the address:** back, forward or a link to another page or row of the same query shows it in place
    (a page past those the grid knows of makes it anew); another query makes the grid anew in the address's state,
    and the keyboard, if it was in the grid, goes to the new one's first header. An address that goes elsewhere as
    a grid is made is followed once it is ready.
  - **Following the catalog:** the schema is read again when the catalog changes, with the page shown and the
    count; the same columns keep the grid, which shows them.
  - **Problems:** rows that couldn't be read are said above the grid ("Try again" asks for the same page anew), and
    over it. A first page that failed leaves the address's page as it was.
  - **The theme** is Material's: the grid's colours are its system tokens, so it is light or dark as the page is.
  - **What it leaves out** of an address it can't hold (a column it hasn't, a second filter on a column, a third
    condition, an operation a column's filter doesn't offer) is said, and the address says what the grid shows.
- **The inspector,** beside the grid (under it when narrow), shows the cell the keyboard is on (a click puts it
  there) whole: its column (type, key), its value (NULL, a text's length, a binary value's bytes), and where the
  column's values come from: read from a column, worked out, aggregated, a constant or put together; the tables'
  columns they come from, through which navigations, and the expression. A reference's cell says what it refers to
  (the entity, through which navigation, the row's display value, or that it refers to none: a NULL), and a
  collection's what rows it leads to; each says that Enter follows its link. It can be hidden, and stays as the user left it.
- **What an entity is** (the Structure tab, `EntityStructure`, from `GET /api/catalog/entity`):
  - its facts: its full name, rows, key, unique keys, the column that shows its rows where others refer to
    them, triggers, and what the user may do with its rows (and why not);
  - its columns: types as the language and the database write them, keys, identity, computed, defaults, row
    versions, hidden; and, when its rows may be changed, what may be done with each;
  - its navigations: where they lead, how many rows, through which columns, and whether they are inverse, added by
    the overlay, not enforced, across connections, hidden or inherited;
  - a virtual entity's query, and what is wrong with it.

  It is read again when the catalog changes, and shown meanwhile (and when reading it again fails).
- **Reading again** what was read of the catalog: `followCatalog(reload)` gives an operator a resource reads its
  answers through, which notes the version each came with, and reloads it when the catalog is at another. An answer
  that is itself the first to give the new version isn't read again.
- **The layout.** From 1024 pixels the catalog is beside the page, its width set by a splitter and kept in the
  browser. The splitter is dragged, or moved with the arrow keys, Home and End, from 200 to 640 pixels. On narrower
  screens the catalog opens over the page: at the start of browsing, and on asking ("Catalog"). It closes once an
  entity is chosen.

## Changing data

Those who change data (data managers and administrators) change rows where they browse them, as the entity allows
(a table of a source that takes changes; changing rows needs its own primary key). What they change isn't written
to the database at once: it is a **pending change**, kept on the server (`/api/changes`) until it is committed or
reverted, and shown wherever the rows are. Committing them previews them first, as the statements each connection
would run ([Committing](#committing)).

- **The store** (`PendingChanges`, `src/app/core/changes`) holds the user's changes, read when they may change data
  (`GET /api/changes`). Changing them is an action: setting values (`set`), a new row (`insert`, named by a
  temporary id until it is committed), deleting a row (`delete`), reverting (`revert`, a row's change or its
  values for some columns), clearing (`DELETE /api/changes`, all, a connection's or an entity's).
  - **Shown at once.** An action changes what the store holds as the server will (`foldOp` follows the server's
    rules: a column's original is the value it had when first changed; a value set back to it is no change; deleting
    a changed row keeps its originals; deleting a new row drops it), and the server's answer, the changes as they
    are, takes its place.
  - **One at a time, in order.** Actions are sent one after another. An action the server refuses goes, and what
    came of it (`Outcome`) says why, a sentence for each thing wrong (a value's column named); the next is sent all
    the same. One that fails otherwise (no answer, a server's failure) may have been made: the changes are read
    again once the actions are answered. Answers are numbered: an answer to a request made before one whose answer
    was taken is left.
  - **Other tabs.** A tab whose action was answered tells the others the changes' version (`ChangesChannel`, a
    `BroadcastChannel`); a tab at another reads them again, as one does coming back into view, once the actions it
    sent are answered.
  - **Previews and commits** (`preview()`, `commit()`) wait for the actions asked for before them to be answered;
    while a commit is answered, actions asked for and reading the changes again wait for it (a read answered
    first would be of the changes before the commit). What a preview found wrong with changes (`issues`, by
    change) is kept while each change stays as it was then (its `updatedAt`), the same while it is (so other
    changes don't make the grid draw anew); a preview of changes changed elsewhere shows its issues once the
    changes at its version are read. A commit's answer holds the changes left, which the store takes; the change
    whose statement stopped it is marked with why, through later previews, till it is changed. A commit that
    wrote changes is counted (`commits`), here and in the other tabs it tells (`committed`), so the rows shown are
    read again; so is one that may have (no answer, a server's failure), whose changes are read again.
- **In the grid** (`grid-edits.ts`), with `editing` (browsing's grid), as the entity and each column allow
  (`capabilities`, and each column's `canUpdate` and `insert`):
  - **Values as they will be:** a cell shows its change's value, else the row's. A cell changed is marked
    (`gd-dirty`; pointed at, it says what it was), and so is one whose row was changed elsewhere since it was
    changed here (`gd-conflict`: the value read now isn't the original kept, so the commit would change nothing).
    A column before the rows says each row's change (new, changed, to be deleted), drawn and said; rows to be deleted
    are struck through (`gd-deleted`).
  - **Editing:** Enter, F2, typing or a double click edits a cell (but a reference's, below). Editors follow the
    column's type: a choice for booleans (with NULL when the column may be), a date's for dates, text for the rest
    (a larger editor over the grid for long text, or text with lines). A value is checked before it is sent
    (`parsedValue`: whole numbers in range, decimals within their precision and scale, dates, date-times, times,
    intervals, guids, as the server reads them) and sent as it is typed (but numbers to 32 bits and doubles as
    numbers, booleans as booleans, date-times with a `T`). What isn't one of its type is said above the grid, and
    nothing is sent. Empty text is NULL for columns that aren't text; Delete sets a cell to NULL. A value the same
    as the cell's sends nothing. The originals sent are the values the row was read with. What was done is said to
    screen readers ("Row 1003 to be deleted", "status of row 1001 reverted").
  - **Rows:** Ctrl+Delete (or the bar's "Delete the row") deletes the row the keyboard is on, with the values it
    had (its key's and those of its columns that take values), and restores it when it is to be deleted; its cells
    can't be changed then. Ctrl+Z reverts the change of the cell the keyboard is on (or restores its row); "Revert
    the row" its whole change. The bar's buttons act on the row the keyboard is on (a click puts it there).
  - **New rows** ("Add a row") are pinned at the top of the grid, in the order they were made, each by its
    temporary id (`getRowId`), so the grid changes them in place as their values change. A new row starts with
    what the page gives (`insertDefaults`): in a collection's crumb (the order lines of an order), the columns that
    refer to the row chosen before get its key's values, and the navigation back its display value. The keyboard
    goes to its first column that needs a value (else its first that takes one, but the key), which is edited.
    Columns not given show `DEFAULT` (the database gives them a value); those that need one are blank, and marked
    (`gd-invalid`). "Drop the new row" (or Ctrl+Delete) drops it. A collection's crumb shows the new rows that
    start as its own do (those of its row), not those of other rows.
  - **References** are set by choosing the row they refer to: F2 or a double click (away from the link) opens the
    picker (`NavPicker`, loaded when first wanted): the target's rows in a grid of their own (filtered, sorted and
    paged as in browsing, without links or changes), one chosen and confirmed with Choose, a double click or Enter;
    or "No row", when the reference's columns all may be NULL. Its columns (all of a composite key's, though one of
    them shows another reference) are set to the row's values for the columns they match (the schema's
    `targetColumns`), with the row's display value (`displayColumn`) for the cell to show (none when the target
    has no such column, or the rows chosen from don't show it: then nothing is shown for it). Delete (or Backspace)
    sets them all to NULL, where they may be, and nothing is shown for them. A reference whose columns aren't all
    the grid's (`complete`) isn't set so. A reference changed shows the display value given, without a link to
    click or follow with Enter (crumbs follow references as committed); Enter on one not changed follows its link.
    The picker opens once at a time.
  - **Following the changes:** when the entity's changes change (an action, another tab's, an answer), or what a
    preview found wrong with them, the cells and rows show them (`refreshCells`, the rows' classes set again, new
    rows pinned), the keyboard staying where it is. A cell whose value can't be committed as it is is marked
    (`gd-invalid`; pointed at, it says why), and so is its row's change (an error drawn, "can't be committed" said).
  - **After a commit** (here or in another tab), the grid reads its rows again, from the page shown (and counts
    them), once a cell being edited is (the grid makes its rows anew, which would end the edit).
  - **Failures:** an action refused (by the client's checks or the server) is said above the grid ("Couldn't change
    total: …"), with Dismiss; its value goes from the cell.
- **The inspector** says a cell's change: a new row's, a row to be deleted, a value changed (what it was), changed
  elsewhere since (what it is now), why it can't be committed as it is (its column's issues and its row's), with
  Revert (Restore the row, Clear the value); and why a cell can't be given a value. A reference's says that F2 or a
  double click chooses its row.
- **The drawer** (`ChangesDrawer`) lists the changes by connection, entity (a link to browse it) and row: the
  columns changed, what each was and will be (a new row's values; a row to be deleted), and the display values given
  with references. A column's change, a row's, an entity's, a connection's or all can be reverted (clearing asks
  first). The bar's button counts the rows changed. Opened, the keyboard goes to its heading; closed, back to the
  button; when a button it was on goes with what it reverted, to its heading. Under each row, what the last preview
  found wrong with its change. "Preview and commit" opens the commit dialog.

### Committing

`CommitDialog` (`features/changes/commit-dialog.ts`, loaded when first opened, from the drawer) commits the
changes:

- **The preview** (`POST /api/changes/preview`): the statements each connection would run, a tab for each, in an
  editor ([The editor](#the-editor)), with what they do ("2 statements: 1 insert, 1 update", and each statement's
  description). When changes can't be made as they are, they are listed (entity, row, column, why), there is
  nothing to commit, and the grid and the drawer mark them; "Preview again" previews the changes as they are now.
  Changes to more than one connection are warned of: they commit one after another, so a commit that fails after
  another's succeeded leaves them written in part. The preview may be committed until it expires.
- **Editing a script:** a script the preview says may be edited (`editable`: all for administrators, but DuckDB's
  for others, as it runs in the application) is edited in place; edited, it runs as written (statements that
  change data only, unless an administrator allows any), without counting the rows it changes. A script is edited
  when its text differs but for line breaks and white space at its end (as the server compares them); the
  server writes values' line breaks by their codes, so the editor, which makes a text's line breaks one kind,
  changes no value. Scripts that may not be edited are sent as planned. "Undo my edits" puts the preview's text
  back (an edit the editor can undo), the keyboard staying in the editor. An edited script the server refuses
  (`script-invalid`) says why, its problems placed in it (markers, and a list by line), till it is edited again.
- **Committing** (`POST /api/changes/commit`) sends the plan, the changes' version, the edited scripts alone, and
  whether any statement may run. What came of it: committed, nothing written, or written in part (with why), each
  connection's outcome (statements, rows changed, its error), the warnings, the new rows made, how many changes are
  left pending, and for administrators the commit in the audit. With changes left pending (those it didn't write,
  or made since the preview), it goes back to the preview on asking, with the edits made (the keyboard going to
  the dialog's title).
- **A preview gone out of date** (`plan-stale`: the changes or the catalog changed, the plan expired or was
  replaced) is previewed again at once, and said; so is "Preview again". Edits stay where their scripts are the
  same, and those let go (their scripts changed, or gone) are said.
- **Closing:** Escape (but in an editor, which keeps it), the backdrop and Cancel close it, asking first when
  scripts were edited and not committed (after a commit that didn't write everything too); nothing closes it while
  it commits. Closed, the keyboard goes back to the drawer.

## Administration

Administrators' pages are under `/admin` (`features/admin`). They are loaded when one is opened, and only for
administrators (`allowedTo('canAdmin')` as a `canMatch` guard).

**Users** (`/admin/users`):

- **The list:** found by name, sorted by column, with each user's role and state (disabled, locked out, a password
  to change).
- **A user's page:** their name, role and whether they are disabled, saved to the version read. When someone else
  saved in between, the page says so, with "Read it again" (which asks first, as the changes made would be lost).
  From it, the administrator can reset the password (one made up, or their own, to pass on), unlock, and delete;
  changes not saved stay through unlocking and resetting. Administrators can't demote, disable, reset or delete
  themselves; their own page doesn't offer it.
- **A new user** (`/admin/users/new`) gets a first password, made up on asking, which they must change at their
  first sign-in.
- **Passwords:** `PasswordPolicy` asks the server once what a password must be; `passwordRules` puts it in a
  form's schema, with the server's messages.

**Connections** (`/admin/connections`):

- **The list:** each connection's kind, whether it is read-only, whether its secrets must be entered again, and its
  schema's state. The list looks again every two seconds while a schema is being read.
- **The form** is the one the connection's kind describes (`GET /api/connection-kinds`). `ConnectionDraft` holds
  it, and gives what the API takes; `ConnectionFields` shows it:
  - **Groups.** Fields come in their groups. Groups the kind collapses (the advanced settings) are folded unless
    they hold a value.
  - **Field types.** Each field shows as its type: text, number, select (with the provider's default first), bool
    (a switch), file or folder path (a full path in a folder connections may use), password, and the other
    settings.
  - **Conditional fields.** A field shows only while the setting it depends on has one of its values
    (`visibleWhen`). Hidden settings aren't sent; a hidden secret that is stored is cleared (the server would keep
    a stored secret it isn't told about, and connect with it: a SQL Server password once Windows authentication
    is on).
  - **Empty means the default.** Settings left empty, and bools set back to their default, aren't sent, so the
    provider's defaults apply.
  - **Other settings** are keyword and value pairs. A keyword named like a secret (`token`, `secret`) is sent as a
    secret, and its value typed as a password.
  - **Secrets** are kept, changed or cleared (`SecretField`): they are never sent back, only whether one is stored.
    One not stored is set by typing it. When the stored secrets can't be read (the keys that protected them are
    gone), the page says so, and they are typed again.
- **Field by field, or as a connection string.** The two convert into each other through the server
  (`POST /api/connection-kinds/{kind}/convert`), which keeps nothing between conversions:
  - **To the string:** the form sends a "keep" for each stored secret, so the string shows it as `********`.
    A secret set in the form is masked too, and sent with the string while it masks it.
  - **Back to the form:** the string decides the secrets. One it masks is kept, one it writes out is set, and a
    stored one it leaves out is cleared.
  - **A refused conversion** stays in the mode it was in, and says why above the form.
- **Trying it** (`POST /api/connections/test`) tries the settings as they are, before they are saved, with the
  stored secrets.
- **The schema.** It is read when the connection is saved, and again on asking. The page follows the reading,
  then lists the last readings and what changed at each.
- **Leaving.** A page with changes not saved asks before it is left (`unsavedChangesGuard`), and the browser asks
  before the tab is closed or reloaded (`warnBeforeUnload`). Reading the page's record again asks too.
- **Following what changes.** Looks at a schema being read stop when it is read. After one that fails, the page
  looks again later, waiting twice as long each time (up to 30 seconds); the list keeps the connections it showed.
  A look begun before the connection was saved, or read again, is left aside.

**The audit** (`/admin/audit`, `features/admin/audit`), as tabs:

- **Commits of changes** (`/admin/audit/commits`): newest first, who committed, when, the outcome (committed,
  rolled back, written in part, in progress, unknown: the application stopped as it ran), whether scripts were
  edited (and any statement allowed), how many changes, to which connections, and why it failed.
- **A commit** (`/admin/audit/commits/:id`): the same, how long it took, the catalog's version, and each script it
  ran: its outcome, statements, rows changed, error, and its text.
- **What administrators did** (`/admin/audit/events`): who (the application itself as it started: `(system)`),
  what (`user.updated`), to what, and what changed, a line each (`role: dataManager → read`; settings by name).
- **Pages** (`AuditPages`) come 50 at a time (51 asked for, to know whether there are more), "Show more" asking for
  those before the last shown; the keyboard goes to the first of a page shown after the first.

## Forms

Forms use Angular's signal forms (`@angular/forms/signals`), in Material's form fields.

- **Rules** go in the form's schema (`required`, `minLength`, `validate`), with messages as the server writes
  them.
- **Submitting** goes through the form's `submission.action`, with `<form [formRoot]="form">`, or through
  `submit(form, { action })` where one form has two actions (the connection page's Save and Try it). The action
  sends the request, and gives back the server's errors for fields; each field shows its error until its value
  changes.
  `fieldErrors(problem, fields)` puts a problem's errors by field (`invalid-request`) on the form's fields, named as
  the API names them, and gives back the messages of the fields the form hasn't.
- **Problems with the whole form** (wrong credentials, the server out of reach) are shown above it, with
  `role="alert"`, until it is submitted again.
- **Focus** goes to the first field with an error, after a submission refused by the form or by the server
  (`focusFirstInvalid`).

## Accessibility

- **Another page opened:** focus goes to the page (`main`), as it would to a page loaded. A page's own changes of
  address (its filters, its rows) leave focus where it is. So does a navigation that says so (its state is
  `keepFocus`): an entity chosen in the catalog's tree beside the page, where the keyboard goes on choosing.
  Going back or forward doesn't leave focus where it is, though the browser gives such a navigation its state again.
- **Another entity shown in place of one** moves focus to its page's heading: after a link followed in the page
  (the link is gone: a navigation, a reference or collection in the grid, a crumb of the path), or an entity chosen
  in the catalog over the page (which gives focus back to its button).
  Focus in the catalog beside the page (its tree, its search) stays. The grid's changes of the address leave focus
  where it is.
- **The grid** is AG Grid's ARIA grid: its cells and headers are reached by the arrow keys (Enter on a header sorts,
  Shift+Enter adds it to the sort, Ctrl+Enter opens its filter), Space chooses the row the keyboard is on, Enter
  follows the link of the cell it is on (links in cells aren't stops of their own, `tabindex="-1"`, as the grid
  is one), and its pages have buttons of their own. Its rows are in the page in their order (`ensureDomOrder`). Headers' tooltips
  (type, lineage) show when pointed at; for the keyboard, the inspector says the same of the column of the cell it
  is on, in a region of its own (hidden, not removed, so its button always controls it). The condition's field is
  labelled and described, and its problems are an alert tied to it (`aria-describedby`, `aria-invalid`); Apply
  stays focusable when there is nothing to apply (`disabledInteractive`), and clearing the condition leaves the
  keyboard in its field. The count is a status.
- **Changing rows:** the grid's keys are AG Grid's (Enter, F2 or typing edits; Escape cancels; Enter or Tab ends
  an edit) and ours: Delete sets a cell to NULL, Ctrl+Z reverts a cell's change, Ctrl+Delete deletes or restores a
  row, F2 on a reference opens the picker (`suppressKeyboardEvent`, before the grid does anything with the key; not
  while editing). The bar above the grid is a group ("Changes to the rows") whose hint says the keys, and its
  buttons act on the row the keyboard is on, which their names say ("Delete the row: row 1003"; Ctrl+Delete is
  the delete button's `aria-keyshortcuts`), a disabled one's tooltip why. What was done is said (`LiveAnnouncer`,
  politely), a change refused is an alert, and the inspector's Revert gives the keyboard back to the cell. The column saying rows' changes draws an icon and says its words to screen readers. The
  picker is a dialog titled by what it chooses; closed, focus goes back to the cell. The drawer is a region
  ("Pending changes"); its buttons say what they revert (`aria-label`: "Revert status of shop.orders Row 1001"),
  and a column's change reads as a sentence ("status: open becomes paid", the arrow drawn, not read).
- **Committing:** the dialog takes the keyboard as it opens (the dialog itself, titled), and gives it back to the
  drawer; the outcome's heading takes it once committed, the title going back to the preview. Its scripts are tabs
  (a tab list named "The scripts, by connection"); each editor is labelled ("The script for shop") and described
  by what the script does and the keys (Tab inserts a tab; Ctrl+M, on macOS Ctrl+Shift+M, makes it move on), and
  by its problems when refused; problems are an alert, and listed by line as well as marked. Undoing edits keeps
  the keyboard in the editor. Commit and Preview again stay focusable when they can't be pressed
  (`disabledInteractive`). Previews, and what came of the commit, are said.
- **The path** is a navigation landmark ("Path"), a list of links, the crumb shown marked `aria-current="page"`;
  the `›` between crumbs is drawn by the style sheet, with no text for screen readers (`content: '›' / ''`).
- **The catalog's tree** is a tree as WAI-ARIA describes one. It is one stop in the tab order.
  - The arrow keys move through it, and Right and Left open and close. Home, End, Page Up and Page Down go further;
    `*` opens a node's siblings.
  - Enter or Space opens an entity, or opens and closes a node. Typing a name's first letters goes to it.
  - The node the keyboard is on is the tree's active descendant, as rows out of view aren't in the page. It is
    once its row is rendered; until then the tree itself has focus. Each node says its level, its place among its
    siblings, whether it is open, and whether it is the entity shown.
  - Pressing a row leaves focus on the tree, not on the entity's link in it (whose row may scroll out of the page).
  - What the icons show is said in words to screen readers ("table", "read-only").
- **Live regions** are in the page before what they announce: an alert or status region is always there, and its
  message is put in it (problems, a schema's state, the list's "no user has…"). Those a page has as it opens are
  filled once it shows (the sign-in page's notice that the session ended).
- **Focus** moves to what takes the place of a button that goes (a secret's Change, Clear and Keep it; Unlock; a
  removed setting's button), so it isn't lost to the page.
- **The shell:** its bar is the banner, with "Skip to the page" first for keyboards; the navigation is a landmark
  of its own (Material's navigation list is one).
- **Lint:** ESLint's template rules include Angular's accessibility checks.

## The editor

Code is edited in Monaco (`monaco-editor`, `CodeEditor` in `core/editor`): the commit dialog's scripts, and F8's
queries.

- **Loaded when first shown** (`MonacoLoader`): its ESM modules, those the application uses (`monaco-modules.ts`:
  the editor and its commands, the features for editing scripts, SQL's and PostgreSQL's languages), are a chunk of
  their own. Monaco's modules import their styles, which the application's builder doesn't load for a lazy chunk:
  those imports are empty (`loader: {".css": "empty"}` in `angular.json`), and Monaco's whole stylesheet is a
  bundle of its own (`monaco.css`, `inject: false`), linked as Monaco loads. Its worker (`editor.worker.ts`, with
  `tsconfig.worker.json`) is a file of the application's (the policy's `worker-src 'self'`).
- **The theme** follows the page's colour scheme (`vs`, `vs-dark`); the font is the page's code font, measured
  again once loaded.
- **The text** is two-way (`[(text)]`): as typed, and set from outside as an edit that can be undone. Markers
  (`EditorMarker`: an offset, a length, a message) are placed by offsets in the text. A read-only editor says why
  when typed in.
- **Keys:** Tab inserts a tab; Ctrl+M (Ctrl+Shift+M on macOS) switches Tab to moving the keyboard on, and back
  (Monaco's tab focus mode). The editor says so under itself, and is described by it, when it may be typed in.
  Escape is the editor's. Monaco's text area is labelled (`label`) and described (`describedBy`); its edit context
  (experimental) is off. The font is read from the page (an editor in a tab not shown is made outside it).
- **Should Monaco fail to load,** the text is edited in a plain text area, labelled and described the same.
- **Packages:** `monaco-editor` (MIT) brings `marked` and `dompurify`; an npm override gives Monaco dompurify
  3.4.16, for an advisory of the version it pins (GHSA-p98j-92pf-mc4p).

## The theme

**Material 3.** The theme is Angular Material's (`src/styles.scss`): azure and blue palettes, and Roboto. Material
sets its system colors as `light-dark()` values, so the page's `color-scheme` makes it light or dark.

**Light or dark.** `ColorScheme` holds the scheme chosen: the system's (the default), light or dark. It sets the
page's `color-scheme`, and keeps the choice in the browser. Its `dark` signal says whether the application is dark,
with the system's setting resolved. It is for what Material doesn't theme: the grid and the editor.

**Fonts.** Fonts are served with the client, as the content security policy loads none from elsewhere: Roboto,
Roboto Mono for code (`--gd-code-font-family`), and Material Symbols (outlined) for icons. `<mat-icon>` shows the
symbols by their names, as its text or its `fontIcon`: `<mat-icon>dark_mode</mat-icon>`,
`<mat-icon fontIcon="dark_mode" />`.

## Building

`npm run build` builds into `dist/browser`. Publishing the server runs it, and publishes what it leaves there as
the server's `wwwroot` (see [server.md](server.md#running)). Files are named by a hash of their contents, so the
server lets browsers keep them.

**The content security policy.** The server's policy runs the client's own script files and nothing inline, and
loads nothing from other origins. After each build, `scripts/check-csp.mjs` fails the build when it finds what the
policy refuses:

- in `index.html`: inline scripts (data blocks, such as `application/json`, aside), event handler attributes,
  `javascript:` URLs, and scripts, stylesheets and other resources from other origins;
- in the stylesheets: URLs and imports from other origins.

This is why the build doesn't inline critical CSS (`inlineCritical: false` in `angular.json`). Angular would load
the rest of the stylesheet with an inline script, which the policy refuses.

**Size.** The first load holds the framework, the Material parts of the shell, and the shell: about 718 kB, 168 kB
compressed (the pending changes' store and badge are in it; their drawer, about 13 kB, and the commit dialog, about
25 kB, load when first opened). It grows as the features use more of Angular's core (resources, for one), which every page shares, and
of modules the shell uses: the CDK's virtual scrolling is in the module of the scrolling the shell's navigation
uses, so it loads at first though only browsing uses it. Pages not needed at first are loaded when opened (lazy
routes): the sign-in and password pages, the administrators' pages (about 255 kB), browsing (about 1.32 MB, 297 kB
compressed; its picker loads when first wanted), and the features' pages as they come. The editor (Monaco) is a
chunk of 3.3 MB (660 kB compressed), with its stylesheet (`monaco.css`, 390 kB, 108 kB compressed, its icons'
font in it) and worker (300 kB), loaded when an editor is first shown. Browsing's chunk is nearly
all AG Grid: its core and the modules the grid registers (its infinite row model, pages, filters, choosing rows,
tooltips, refreshing cells, its state and words, its editors, rows' classes, pinned rows, scrolling). The build
warns above 750 kB (700 kB until F6) and fails above 1 MB for the first load (`budgets` in `angular.json`). GalaxyData isn't meant
for slow networks, so these can be raised when a feature needs it.

## Conventions

- **Angular 22:** standalone components, zoneless change detection, OnPush by default, and signals.
- **Data:** what a page reads comes from `rxResource` (stable in Angular 22), read again after a change or on
  asking. Its loader runs outside the injection context, so it uses fields `inject()` filled, never `inject()`
  itself. `pollWhile` loads a resource again while what it holds is still changing, every `POLL_INTERVAL`
  milliseconds (two seconds; tests make it one). What was read of the catalog is read through `followCatalog`.
- **Names:** the selector prefix is `gd`. File names have no type suffix (`color-scheme.ts`), as Angular's
  current style guide has it.
- **Format:** Prettier, with 2-space indentation and lines of up to 100 characters.
- **Tests:** beside their code (`*.spec.ts`), run by Vitest in jsdom. The test build type-checks the tests, so
  `expectTypeOf` and `@ts-expect-error` check types. Pages are tested through the router (`RouterTestingHarness`)
  and Material's harnesses, with the API answered by `HttpTestingController` (`pageProviders`, `openPage` in
  `src/testing/pages.ts`).
  - **Material's animations are off** in pages' tests: jsdom ends no animation, so a dialog would never close.
  - **Harnesses wait until the application is stable.** A request the test hasn't answered, or a navigation
    waiting for a dialog, keeps it busy, so a harness click that starts one would never return. Such clicks are
    plain clicks (`clickButton`); the test then answers the request.
  - **Connections' forms are tested with the server's own descriptors** (the snapshot its tests keep), so a change
    to a kind's form shows in the client's tests too.
  - **The catalog's tree is given a height** (`viewportsFor` in `src/testing/catalog.ts`): jsdom lays nothing out,
    so a virtual scroll viewport would render a few rows. The search's wait is `SEARCH_WAIT` (none in tests).
  - **The grid renders in jsdom**, a few rows at a time (`gridCells`, `gridHeaders` in `src/testing/browse.ts`);
    pages have 3 rows in its tests (`BROWSE_PAGE_SIZE`). The grid waits 10 ms before it fetches a page it asks for
    (`blockLoadDebounceMillis`), so tests wait for it (`pagesFetched`). An entity's page asks for its grid's first
    page: tests that open one answer it (`answerGrid`), or a harness would wait for it. The whole application's test
    leaves the grid out: with every stylesheet in the page, jsdom takes seconds to resolve the styles AG Grid reads
    as it starts.
  - **Pending changes:** a component that injects `PendingChanges` (the shell, browsing's page and grid, the
    drawer) reads the changes of a user who changes data (`GET /api/changes`): tests answer it (`answerChanges` in
    `src/testing/changes.ts`, with `changeOf`, `insertOf` and `setOf`), or browse as readers (browsing's page
    tests do, but those of changes). The other tabs are `FakeChangesChannel`.
  - **AG Grid's columns and rows in jsdom:** an initial state unpins the columns its `columnPinning` doesn't pin,
    so the grid's state names the column of rows' changes; jsdom lays nothing out, so AG Grid unpins pinned columns
    (they don't fit), and that column's cells are in the rows (`gridCells` leaves them out). New rows are in
    `.ag-grid-pinned-top-rows` (`newRowCells`; `gridCells` leaves them out too). Key events a test makes are
    `cancelable`, or `preventDefault` does nothing.
  - **Editors** are on a fake Monaco in tests (`fakeMonacoProviders` in `src/testing/monaco.ts`): its editors and
    models hold their options, text, edits and markers, and a test types in a model (`type`); without one
    (`fakeMonacoProviders(null)`), Monaco fails to load and editors are text areas. Monaco itself isn't loaded in
    jsdom (it lays out what jsdom doesn't).
  - **Back and forward:** the router follows the browser's history only once it listens, which an application's
    first navigation sets up and a test's router harness doesn't: a test that goes back calls
    `router.setUpLocationChangeListener()` first.
  - **In a browser,** AG Grid makes cells' renderers (the links) in animation frames when it first draws them, so a
    page that isn't being painted (a window behind another) shows those cells empty until it is.
- **Layout:**
  - `src/app/core` holds what the whole application uses: the API, problems, the session, the theme, forms'
    helpers, the catalog (its version, the tree's state, following it), the pending changes (`core/changes`),
    browsing's addresses (`core/browse`), the editor (`core/editor`), the
    browser's storage (`core/browser/stored.ts`), and the pieces pages share (`core/ui`: messages, the confirmation
    dialog, the unsaved-changes guard, `debounced`);
  - `src/app/shell` holds the shell;
  - `src/app/features` holds the pages, by feature;
  - `src/testing` holds the tests' helpers (sessions, and fakes of the page and the other tabs), outside the
    application's build.
