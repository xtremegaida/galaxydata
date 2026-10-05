// The smoke test: the published application, as an administrator uses it, from the first sign-in to a query.
import { expect, test, type Locator, type Page } from '@playwright/test';
import { randomBytes } from 'node:crypto';
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

test('says nothing in the console', () => {
  expect(complaints).toEqual([]);
});
