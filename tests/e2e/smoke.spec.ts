// The smoke test: the published application, as an administrator uses it, from the first sign-in to a query and a
// dashboard (built, published, chosen in, embedded by another site).
import { expect, test, type Locator, type Page } from '@playwright/test';
import { randomBytes } from 'node:crypto';
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { join } from 'node:path';
import { adminUserName, filesFolder, firstPassword } from './settings';

test.describe.configure({ mode: 'serial' });

let page: Page;
/** What the browser's console said that it shouldn't have (errors, and the content security policy's refusals). */
const complaints: string[] = [];

test.beforeAll(async ({ browser }) => {
  page = await browser.newPage();
  page.on('console', (message) => {
    if (message.type() === 'error') {
      complaints.push(message.text());
    }
  });
  page.on('pageerror', (error) => complaints.push(error.message));
});

test.afterAll(async () => {
  await page?.close();
});

/** Goes to a page through the navigation. */
async function visit(label: string): Promise<void> {
  await page
    .getByRole('navigation', { name: 'Pages' })
    .getByRole('link', { name: label, exact: true })
    .click();
}

/** The page's grid's cell in the row whose first column (its key) holds `key`, under the header named. */
async function cell(key: string, header: string): Promise<Locator> {
  const grid = page.getByRole('grid').first();
  const column = await grid
    .locator('.ag-header-cell')
    .filter({
      has: page.locator('.ag-header-cell-text', {
        hasText: new RegExp(`^${header}$`),
      }),
    })
    .getAttribute('col-id');
  // AG Grid writes rows' ids into the page escaped (row-id="[&quot;1&quot;]"), so rows are found by their keys' cells.
  const row = grid.locator('.ag-row').filter({
    has: page.locator(".ag-cell[col-id='c0']", {
      hasText: new RegExp(`^${key}$`),
    }),
  });
  return row.locator(`.ag-cell[col-id='${column}']`);
}

test('signs in and changes the first password', async () => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in/);
  await page.getByLabel('User name').fill(adminUserName);
  await page.getByLabel('Password', { exact: true }).fill(firstPassword());
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(page.getByRole('heading', { level: 1 })).toHaveText(/password/i);
  const next = randomBytes(18).toString('base64url');
  await page.getByLabel('Current password').fill(firstPassword());
  await page.getByLabel('New password', { exact: true }).fill(next);
  await page.getByLabel('New password again').fill(next);
  await page.getByRole('button', { name: 'Change password' }).click();

  await expect(page.getByRole('heading', { name: `Welcome, ${adminUserName}` })).toBeVisible();
});

test('makes a SQLite connection, and its schema is read', async () => {
  await visit('Connections');
  await page.getByRole('link', { name: 'New connection' }).click();
  await page.getByLabel('Kind of source').click();
  await page.getByRole('option', { name: 'SQLite' }).click();
  await page.getByLabel('Alias').fill('shop');
  await page.getByLabel('Database file').fill(join(filesFolder(), 'shop.db'));
  // Its rows are to be changed.
  await page.getByRole('switch', { name: 'Read-only' }).click();
  await page.getByRole('button', { name: 'Make the connection' }).click();

  await expect(page.getByRole('heading', { level: 1, name: 'shop' })).toBeVisible();
  const schema = page.getByRole('region', { name: 'Schema' });
  await expect(schema.getByRole('status')).toContainText(/Read \w/, {
    timeout: 30_000,
  });
  await expect(schema.getByRole('listitem').first()).toContainText('7 tables');
});

test('finds the orders in the catalog', async () => {
  await visit('Browse');
  await page.getByLabel('Find in the catalog').fill('orders');
  await page.getByRole('option', { name: /^orders , table/ }).click();

  await expect(page).toHaveURL(/\/browse\/shop\.orders$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('shop.orders');
  await expect(page.getByRole('status').filter({ hasText: /rows$/ })).toHaveText('250 rows');
  // The key's header says so in words, not by its icon's name.
  await expect(page.getByRole('columnheader', { name: 'Key: id', exact: true })).toBeVisible();
  await expect(page.getByRole('treeitem', { name: /^orders/ })).toHaveAttribute(
    'aria-selected',
    'true',
  );
});

test('filters, sorts and pages, and the address keeps them through a reload', async () => {
  await page.getByLabel('Where', { exact: true }).fill('total > 500');
  await page.getByLabel('Where', { exact: true }).press('Enter');
  await expect(page.getByRole('status').filter({ hasText: /rows$/ })).toHaveText('122 rows');

  const total = page.getByRole('columnheader', { name: 'total', exact: true });
  await total.click();
  await total.click();
  await expect(total).toHaveAttribute('aria-sort', 'descending');
  await page.getByRole('button', { name: 'Next Page' }).click();
  await expect(page.getByText('101 to 122 of 122')).toBeVisible();
  await expect(page).toHaveURL(/;w=.*;sort=-total;page=2$/);

  await page.reload();
  await expect(page.getByLabel('Where', { exact: true })).toHaveValue('total > 500');
  await expect(page.getByRole('columnheader', { name: 'total', exact: true })).toHaveAttribute(
    'aria-sort',
    'descending',
  );
  await expect(page.getByText('101 to 122 of 122')).toBeVisible();
});

test('follows a reference to the order’s customer, and back and forward', async () => {
  const customer = (await cell('2096', 'customer_id')).getByRole('link');
  await expect(customer).toHaveText('Acme Ltd');
  await customer.click();

  await expect(page.getByRole('heading', { level: 1 })).toHaveText('shop.customers');
  await expect(page).toHaveURL(/;page=2;row=2096\/customer$/);
  // The order is shown by its first text column (status), as the catalog's convention has it.
  const path = page.getByRole('navigation', { name: 'Path' });
  await expect(path.getByRole('link')).toHaveText('shop.orders (open)');
  await expect(path.locator('[aria-current=page]')).toHaveText('customer');

  await page.goBack();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('shop.orders');
  await expect(page.getByText('101 to 122 of 122')).toBeVisible();
  await page.goForward();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('shop.customers');
  await expect(await cell('1', 'name')).toHaveText('Acme Ltd');
});

test('changes a value, adds a row and deletes one', async () => {
  const city = await cell('1', 'city');
  await city.click();
  await city.press('Enter');
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type('Stellenbosch');
  await page.keyboard.press('Enter');
  await expect(city).toHaveText('Stellenbosch');
  await expect(city).toHaveClass(/gd-dirty/);
  await expect(page.getByRole('button', { name: 'Pending changes: 1 row' })).toBeVisible();

  await page.getByRole('tree').getByRole('link', { name: 'customers', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('shop.customers');
  await page.getByRole('button', { name: 'Add a row' }).click();
  // The keyboard is on the new row's name, which it needs.
  await page.keyboard.type('Delta Ltd');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('button', { name: 'Pending changes: 2 rows' })).toBeVisible();

  await page.getByRole('tree').getByRole('link', { name: 'orders', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('shop.orders');
  await (await cell('2005', 'total')).click();
  await page.getByRole('button', { name: /^Delete the row/ }).click();
  await expect(page.getByRole('button', { name: 'Pending changes: 3 rows' })).toBeVisible();
});

test('previews and commits the changes, which stay', async () => {
  await page.getByRole('button', { name: 'Pending changes: 3 rows' }).click();
  await expect(page.getByRole('heading', { name: 'Pending changes' })).toBeVisible();
  await page.getByRole('button', { name: 'Preview and commit' }).click();

  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('heading', { name: 'Commit the changes' })).toBeVisible();
  await expect(dialog).toContainText('1 insert, 1 update, 1 delete');
  await dialog.getByRole('button', { name: 'Commit', exact: true }).click();
  await expect(
    dialog.getByRole('region', { name: 'What came of the commit' }).getByRole('status'),
  ).toContainText('Committed: the changes were written to shop.');
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(page.getByRole('button', { name: 'Pending changes: none' })).toBeVisible();

  await page.goto('/browse/shop.customers');
  await expect(page.getByRole('status').filter({ hasText: /rows$/ })).toHaveText('154 rows');
  await expect(await cell('1', 'city')).toHaveText('Stellenbosch');
  await page.getByLabel('Where', { exact: true }).fill("name == 'Delta Ltd'");
  await page.getByLabel('Where', { exact: true }).press('Enter');
  await expect(page.getByRole('status').filter({ hasText: /rows?$/ })).toHaveText('1 row');
  await page.goto('/browse/shop.orders');
  await expect(page.getByRole('status').filter({ hasText: /rows$/ })).toHaveText('249 rows');

  await visit('Audit');
  const commit = page.getByRole('table', { name: 'Commits of changes' }).getByRole('row').nth(1);
  await expect(commit).toContainText(adminUserName);
  await expect(commit).toContainText('Committed');
});

test('opens the reference picker at the row the reference refers to', async () => {
  await page.goto('/browse/shop.orders');
  // The keyboard on order 2007's customer (its cell's link would be followed if clicked).
  await (await cell('2007', 'id')).click();
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('F2');

  const picker = page.getByRole('dialog');
  await expect(
    picker.getByRole('heading', { name: 'Choose a row of shop.customers' }),
  ).toBeVisible();
  // Customer 230 is the 133rd of the 154 customers by key: on the second page, chosen, the keyboard on it.
  await expect(picker.getByText('101 to 154 of 154')).toBeVisible();
  const chosen = picker.locator('.ag-row-selected');
  await expect(chosen).toContainText('Customer 230');
  await expect(chosen.locator(':focus')).toHaveCount(1);
  await picker.getByRole('button', { name: 'Cancel' }).click();
  await expect(picker).toBeHidden();
});

test('makes settings for an entity from its page', async () => {
  await page.goto('/browse/shop.customers');
  await page.getByRole('tab', { name: 'Structure' }).click();
  await page.getByRole('link', { name: 'Make settings for it' }).click();

  await expect(page.getByRole('heading', { level: 1 })).toHaveText('New entity settings');
  await expect(page.getByLabel('Entity', { exact: true })).toHaveValue('shop.customers');
  await page.getByLabel('city’s label').fill('Town');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page).toHaveURL(/\/admin\/overlay\/entity-settings\/\d+$/);

  await page.goto('/browse/shop.customers');
  await page.getByRole('tab', { name: 'Structure' }).click();
  await expect(page.getByRole('row', { name: /^city/ })).toContainText('Town');
  await expect(page.getByRole('link', { name: 'Its settings' })).toBeVisible();
});

test('runs a query, and explains it', async () => {
  await visit('Query');
  await page.getByRole('textbox', { name: 'The query' }).focus();
  await page.keyboard.insertText(
    'shop.orders.groupBy(status).select(status, orders: count()).orderBy(status)',
  );
  await page.keyboard.press('ControlOrMeta+Enter');

  const results = page.getByRole('tabpanel', { name: 'Results' });
  await expect(results.getByRole('status').filter({ hasText: /rows/ })).toContainText('3 rows');
  await expect(results.getByRole('gridcell', { name: 'cancelled' })).toBeVisible();
  await expect(page).toHaveURL(/\/query#/);

  await page.getByRole('button', { name: 'Explain' }).click();
  await page.getByRole('tab', { name: 'Plan' }).click();
  await expect(page.getByRole('tabpanel', { name: 'Plan' })).toContainText('Aggregate');
  await page.getByRole('tab', { name: 'SQL' }).click();
  await expect(page.getByRole('tabpanel', { name: 'SQL' })).toContainText(/GROUP\s+BY/);
});

/** The dashboard made, at its address; and its public link. */
let dashboardUrl = '';
let publicLink = '';

/** The editor's settings, beside the canvas. */
const settings = () => page.getByRole('complementary', { name: 'Settings' });

/** Picks a field: typed into the picker labelled, then chosen among those suggested. */
async function pickField(label: string, field: string): Promise<void> {
  await settings().getByRole('combobox', { name: label, exact: true }).fill(field);
  await page
    .getByRole('option', { name: new RegExp(`^${field.replace('.', '\\.')}\\b`) })
    .first()
    .click();
}

/** A widget's frame on the editor's canvas, by its id (its move handle's). */
const frameOf = (id: string) => page.locator(`gd-widget-frame:has([data-gd-move="${id}"])`);

/** A widget on the dashboard's page, by its title. */
const widget = (title: string) => page.getByRole('region', { name: title, exact: true });

test('builds a dashboard in the editor: sources linked, widgets, a filter', async () => {
  await visit('Dashboards');
  await page.getByRole('link', { name: 'New dashboard' }).click();
  await expect(page).toHaveURL(/\/dashboards\/new$/);
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill('Shop');

  await settings().getByRole('tab', { name: 'Sources' }).click();
  for (const entity of ['shop.orders', 'shop.customers']) {
    await settings().getByRole('combobox', { name: 'Add a source' }).fill(entity.split('.')[1]);
    await page.getByRole('option', { name: entity, exact: true }).click();
  }
  await settings().getByRole('combobox', { name: 'Link' }).click();
  await page.getByRole('option', { name: 'Orders', exact: true }).click();
  await settings().getByRole('combobox', { name: 'with' }).click();
  await page.getByRole('option', { name: 'Customers', exact: true }).click();
  await settings().getByRole('radio', { name: 'customer', exact: true }).check();
  await settings().getByRole('button', { name: 'Link them' }).click();
  await expect(settings()).toContainText('Orders → customer → Customers');

  const palette = page.getByRole('complementary', { name: 'Add a widget' });
  await palette.getByRole('button', { name: 'Text' }).click();
  await settings()
    .getByRole('textbox', { name: 'Text, in Markdown' })
    .fill('# Shop\n\nOrders by status; see [the docs](https://example.com/docs).');

  await palette.getByRole('button', { name: 'Bar chart' }).click();
  await settings().getByRole('textbox', { name: 'Title', exact: true }).fill('Orders by status');
  await pickField('Category', 'status');

  await palette.getByRole('button', { name: 'Pie chart' }).click();
  await settings().getByRole('textbox', { name: 'Title', exact: true }).fill('Customers by city');
  await settings().getByRole('combobox', { name: 'Source' }).click();
  await page.getByRole('option', { name: /^Customers/ }).click();
  await pickField('Slices', 'city');

  await palette.getByRole('button', { name: 'Table' }).click();
  await settings().getByRole('textbox', { name: 'Title', exact: true }).fill('Orders by customer');
  await pickField('Group 1', 'customer.name');

  await settings().getByRole('tab', { name: 'Filters' }).click();
  await settings().getByRole('button', { name: 'Add a filter' }).click();
  await settings().locator('mat-expansion-panel-header').first().click();
  await settings().getByRole('textbox', { name: 'Label', exact: true }).fill('Status');
  await pickField('Field', 'status');

  await page.keyboard.press('ControlOrMeta+s');
  await expect(page).toHaveURL(/\/dashboards\/\d+\/edit$/);
  dashboardUrl = page.url().replace(/\/edit$/, '');
  // Each widget of data previews its rows: the charts draw.
  await expect(page.locator('gd-editor-canvas canvas')).toHaveCount(2, { timeout: 30_000 });
  await expect(frameOf('table')).toContainText('Customer 230');
});

test('lays the widgets out by keyboard and pointer, and a narrower width by hand', async () => {
  await expect(frameOf('pie')).toHaveAttribute('data-gd-cell', '6,2,4,6');
  const handle = page.getByRole('button', { name: 'Move Customers by city' });
  await handle.focus();
  await page.keyboard.press('Enter');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Enter');
  await expect(frameOf('pie')).toHaveAttribute('data-gd-cell', '8,2,4,6');

  // The grid's columns, as the pointer crosses them.
  const grid = (await page.locator('gd-editor-canvas gd-dashboard-grid').boundingBox())!;
  const step = (grid.width - 11 * 12) / 12 + 12;
  const box = (await handle.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 - 2 * step, box.y + box.height / 2, { steps: 8 });
  await page.mouse.up();
  await expect(frameOf('pie')).toHaveAttribute('data-gd-cell', '6,2,4,6');

  const corner = (await frameOf('table').locator('.resize').boundingBox())!;
  await page.mouse.move(corner.x + 8, corner.y + 8);
  await page.mouse.down();
  await page.mouse.move(corner.x + 8, corner.y + 8 - 2 * 60, { steps: 8 });
  await page.mouse.up();
  await expect(frameOf('table')).toHaveAttribute('data-gd-cell', '0,8,12,4');

  await page.getByRole('radio', { name: 'Narrow' }).click();
  await settings().getByRole('tab', { name: 'Layout' }).click();
  await settings().getByRole('button', { name: 'Lay it out by hand' }).click();
  await expect(settings()).toContainText('Laid out by hand');
  await page.getByRole('radio', { name: 'Wide' }).click();
  await page.keyboard.press('ControlOrMeta+s');
  await expect(page.getByText('Not saved')).toBeHidden();
});

test('publishes it, chooses slices that filter the widgets listening, and the address keeps them', async () => {
  await page.goto(dashboardUrl);
  await page.getByRole('button', { name: 'Publish' }).click();
  const publish = page.getByRole('dialog', { name: 'Publish Shop' });
  await publish.getByRole('textbox', { name: 'Note' }).fill('The first');
  await publish.getByRole('button', { name: 'Publish' }).click();
  await expect(publish).toBeHidden();
  await expect(page.getByRole('button', { name: 'Publish' })).toBeHidden();

  // The bar chart's rows, as a table to choose from by keyboard.
  await page.getByRole('button', { name: 'Actions of Orders by status' }).click();
  await page.getByRole('menuitem', { name: 'Show as table' }).click();
  await widget('Orders by status').getByRole('button', { name: 'Add open' }).click();
  await expect(page).toHaveURL(/[?&]s\.bar=open\b/);
  await widget('Orders by status').getByRole('button', { name: 'Add shipped' }).click();
  await expect(page).toHaveURL(/[?&]s\.bar=open,shipped\b/);
  const chips = page.getByRole('group', { name: 'Chosen in the widgets' });
  await expect(chips).toContainText('Status: open, shipped');
  // The bars keep their rows; those listening are filtered.
  await expect(widget('Orders by status').getByRole('row')).toHaveCount(4);
  await expect(widget('Customers by city').locator('canvas')).toBeVisible();

  await page.reload();
  await expect(page.getByRole('group', { name: 'Chosen in the widgets' })).toContainText(
    'Status: open, shipped',
  );
});

test("shows a widget's query and SQL, opens it in the query editor, and its rows", async () => {
  await page.getByRole('button', { name: 'Actions of Customers by city' }).click();
  await page.getByRole('menuitem', { name: 'View query' }).click();
  const query = page.getByRole('dialog', { name: 'Query of Customers by city' });
  await expect(query.locator('.view-lines')).toContainText('shop.customers');
  await query.getByRole('tab', { name: 'SQL' }).click();
  await expect(query).toContainText(/SELECT/);
  await query.getByRole('button', { name: 'Open in the query editor' }).click();
  await expect(page).toHaveURL(/\/query#/);
  await expect(
    page
      .getByRole('tabpanel', { name: 'Results' })
      .getByRole('status')
      .filter({ hasText: /rows?/ }),
  ).toBeVisible({ timeout: 30_000 });
  await page.goBack();

  await page.getByRole('button', { name: 'Actions of Orders by customer' }).click();
  await page.getByRole('menuitem', { name: 'Data' }).click();
  const data = page.getByRole('dialog', { name: 'Data of Orders by customer' });
  await expect(data.getByRole('table', { name: 'Orders by customer' })).toBeVisible();
  await data.getByRole('tab', { name: 'Underlying rows' }).click();
  await expect(data.getByRole('status').filter({ hasText: /rows/ })).toBeVisible({
    timeout: 30_000,
  });
  await data.getByRole('button', { name: 'Close' }).click();
});

test('is public: another site frames it, asking for no session, as tall as it is', async ({
  browser,
}) => {
  await page.getByRole('button', { name: 'Share' }).click();
  const share = page.getByRole('dialog', { name: 'Share Shop' });
  await share.getByRole('switch', { name: 'A public link' }).click();
  await expect(share.getByRole('textbox', { name: 'Link' })).toHaveValue(/\/embed\/[\w-]{22}$/);
  publicLink = await share.getByRole('textbox', { name: 'Link' }).inputValue();

  const context = await browser.newContext();
  // In every frame, the content security policy's refusals are said.
  await context.addInitScript(() =>
    document.addEventListener('securitypolicyviolation', (event) =>
      console.error(`CSP refused ${event.blockedURI} (${event.violatedDirective})`),
    ),
  );
  // Another site: a page of its own origin (an address of the machine's, as a site's own is to it; a page made up
  // by routing has none, and Chrome keeps such pages from framing the machine's own).
  const embedderHtml = `<!doctype html><title>An embedder</title>
        <iframe id="dashboard" src="${publicLink}?f.status=open&theme=dark" title="Shop" style="width: 100%; height: 300px; border: 0"></iframe>
        <script>
          addEventListener('message', (event) => {
            const frame = document.getElementById('dashboard');
            if (event.source === frame.contentWindow && event.origin === new URL(frame.src).origin
                && event.data?.type === 'galaxydata.dashboard.size') {
              frame.style.height = event.data.height + 'px';
            }
          });
        </script>`;
  const embedding = createServer((_, response) => {
    response.writeHead(200, { 'content-type': 'text/html' });
    response.end(embedderHtml);
  });
  await new Promise<void>((resolve) => embedding.listen(0, '127.0.0.1', resolve));
  const embedderUrl = `http://127.0.0.1:${(embedding.address() as AddressInfo).port}/`;
  await context.route('https://example.com/**', (route) =>
    route.fulfill({ contentType: 'text/html', body: '<title>The docs</title>' }),
  );
  const embedder = await context.newPage();
  const said: string[] = [];
  embedder.on('console', (message) => {
    if (message.type() === 'error') {
      said.push(message.text());
    }
  });
  embedder.on('pageerror', (error) => said.push(error.message));
  const asked: string[] = [];
  embedder.on('request', (request) => asked.push(new URL(request.url()).pathname));

  await embedder.goto(embedderUrl);
  const frame = embedder.frameLocator('#dashboard');
  await expect(frame.getByRole('heading', { level: 2, name: 'Shop' })).toBeVisible({
    timeout: 30_000,
  });
  await expect(frame.getByRole('group', { name: 'Filters' })).toContainText('open');
  await expect(frame.locator('canvas').first()).toBeVisible();
  await expect
    .poll(() => embedder.locator('#dashboard').evaluate((f) => f.getBoundingClientRect().height))
    .toBeGreaterThan(600);
  // Only the public API: no session asked for, nothing signed in.
  expect(
    asked.filter((path) => path.startsWith('/api/') && !path.startsWith('/api/public/')),
  ).toEqual([]);

  const [docs] = await Promise.all([
    context.waitForEvent('page'),
    frame.getByRole('link', { name: 'the docs' }).click(),
  ]);
  await expect(docs).toHaveURL('https://example.com/docs');
  expect(said).toEqual([]);

  // Stopped, the link shows nothing: the frame says so, asking no one to sign in.
  await share.getByRole('switch', { name: 'A public link' }).click();
  await page
    .getByRole('alertdialog', { name: 'Stop the public link?' })
    .getByRole('button', { name: 'Stop it' })
    .click();
  await expect(share.getByRole('textbox', { name: 'Link' })).toBeHidden();
  await share.getByRole('button', { name: 'Close' }).click();
  await embedder.reload();
  await expect(frame.getByRole('alert')).toContainText("This dashboard isn't available.");
  await expect(frame.getByText(/sign in/i)).toHaveCount(0);
  await context.close();
  await new Promise((resolve) => embedding.close(resolve));
});

test('says nothing in the console', () => {
  expect(complaints).toEqual([]);
});
