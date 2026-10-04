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

- **The bar at the top:** the navigation's button, the application's name, the color scheme, and the user's
  menu (who they are, changing the password, signing out).
- **The navigation.** `navItems` lists the pages, each with what the user must be allowed to do (`needs:
  'canAdmin'`) and the section it is shown under (`section: 'Administration'`); the navigation shows those the user
  may open. Below 960 pixels it opens over the page, and closes once a page is chosen; on wider screens it is
  beside the page, and may be closed.
- **The page.**

A route's `title` is shown with the application's name after it: "Sign in · GalaxyData".

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
  address (its filters, its rows) leave focus where it is.
- **Live regions** are in the page before what they announce: an alert or status region is always there, and its
  message is put in it (problems, a schema's state, the list's "no user has…"). Those a page has as it opens are
  filled once it shows (the sign-in page's notice that the session ended).
- **Focus** moves to what takes the place of a button that goes (a secret's Change, Clear and Keep it; Unlock; a
  removed setting's button), so it isn't lost to the page.
- **The shell:** its bar is the banner, with "Skip to the page" first for keyboards; the navigation is a landmark
  of its own (Material's navigation list is one).
- **Lint:** ESLint's template rules include Angular's accessibility checks.

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

**Size.** The first load holds the framework, the Material parts of the shell, and the shell: about 670 kB, 156 kB
compressed. It grows as the features use more of Angular's core (resources, for one), which every page shares.
Pages not needed at first are loaded when opened (lazy routes): the sign-in and password pages, the
administrators' pages (about 330 kB), and the features' pages as they come. The build warns above 700 kB and fails
above 1 MB (`budgets` in `angular.json`).

## Conventions

- **Angular 22:** standalone components, zoneless change detection, OnPush by default, and signals.
- **Data:** what a page reads comes from `rxResource` (stable in Angular 22), read again after a change or on
  asking. Its loader runs outside the injection context, so it uses fields `inject()` filled, never `inject()`
  itself. `pollWhile` loads a resource again while what it holds is still changing, every `POLL_INTERVAL`
  milliseconds (two seconds; tests make it one).
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
- **Layout:**
  - `src/app/core` holds what the whole application uses: the API, problems, the session, the theme, forms'
    helpers, and the pieces pages share (`core/ui`: messages, the confirmation dialog, the unsaved-changes
    guard);
  - `src/app/shell` holds the shell;
  - `src/app/features` holds the pages, by feature;
  - `src/testing` holds the tests' helpers (sessions, and fakes of the page and the other tabs), outside the
    application's build.
