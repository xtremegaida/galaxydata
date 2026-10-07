import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Injector, runInInjectionContext, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CatalogVersion, catalogVersionHeader, catalogVersionInterceptor } from './catalog-version';
import { entityOf, hitOf, schemaNode, sourceNode, tableNode } from '../../../testing/catalog';
import { requestTo, settle } from '../../../testing/http';
import { entityUrl, suggestUrl } from '../../../testing/overlay';
import { EntityLookup, EntitySearch, suggestions } from './catalog-lookups';

describe('catalog lookups', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([catalogVersionInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function made<T>(make: () => T): T {
    return runInInjectionContext(TestBed.inject(Injector), make);
  }

  async function turns(): Promise<void> {
    await settle();
    TestBed.tick();
    await settle();
  }

  it('suggests the entities the catalog finds for what is typed, as typing pauses', async () => {
    const typed = signal('');
    const search = made(() => new EntitySearch(typed, 1));
    await turns();
    // Nothing typed, nothing asked.
    expect(search.paths()).toEqual([]);

    typed.set(' ord');
    await turns();
    (await requestTo(http, suggestUrl('ord'))).flush({
      text: 'ord',
      hits: [
        hitOf(sourceNode('ord'), []),
        hitOf(schemaNode('wh.ord'), ['wh']),
        hitOf(tableNode('shop.orders'), ['shop']),
        hitOf(tableNode('shop.order_totals', { kind: 'view' }), ['shop']),
        hitOf(tableNode('reports.big_orders', { kind: 'virtual' }), ['reports']),
        hitOf({ id: 'xl.orders', kind: 'folder', name: 'orders', hasChildren: true }, ['xl']),
      ],
      more: false,
    });
    await turns();
    expect(search.paths()).toEqual(['shop.orders', 'shop.order_totals', 'reports.big_orders']);

    // What was found stays while the next text is searched; a search that fails finds nothing.
    typed.set('orde');
    await turns();
    expect(search.paths()).toEqual(['shop.orders', 'shop.order_totals', 'reports.big_orders']);
    (await requestTo(http, suggestUrl('orde'))).flush(null, {
      status: 500,
      statusText: 'Server error',
    });
    await turns();
    expect(search.paths()).toEqual([]);

    typed.set('');
    await turns();
    expect(search.paths()).toEqual([]);
  });

  it('looks up the entity a path names, for its columns and key', async () => {
    const path = signal('');
    const lookup = made(() => new EntityLookup(path, 1));
    await turns();
    expect(lookup.entity()).toBeNull();
    expect(lookup.columns()).toEqual([]);
    expect(lookup.key()).toBeNull();

    path.set('shop.orders ');
    await turns();
    (await requestTo(http, entityUrl('shop.orders'))).flush(entityOf(), {
      headers: { [catalogVersionHeader]: 'v1' },
    });
    await turns();
    expect(lookup.columns()).toEqual(['id', 'customer_id', 'status']);
    expect(lookup.key()).toEqual(['id']);

    // Looked up again as the catalog changes.
    TestBed.inject(CatalogVersion).seen('v2');
    await turns();
    (await requestTo(http, entityUrl('shop.orders'))).flush(
      entityOf({ key: null, columns: entityOf().columns.slice(0, 1) }),
    );
    await turns();
    expect(lookup.columns()).toEqual(['id']);
    expect(lookup.key()).toBeNull();

    // An entity that isn't there has nothing to suggest.
    path.set('shop.nope');
    await turns();
    (await requestTo(http, entityUrl('shop.nope'))).flush(null, {
      status: 404,
      statusText: 'Not found',
    });
    await turns();
    expect(lookup.entity()).toBeNull();
    expect(lookup.columns()).toEqual([]);
  });

  it('suggests the names holding what is typed, ignoring case', () => {
    const names = ['id', 'customer_id', 'Status'];
    expect(suggestions(names, '')).toEqual(names);
    expect(suggestions(names, ' ID ')).toEqual(['id', 'customer_id']);
    expect(suggestions(names, 'stat')).toEqual(['Status']);
    expect(suggestions(names, 'x')).toEqual([]);
  });
});
