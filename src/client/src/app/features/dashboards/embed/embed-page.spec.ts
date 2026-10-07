import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { publicOf, rowsOf, salesDefinition } from '../../../../testing/dashboards';
import { FakeECharts, fakeEChartsProviders } from '../../../../testing/echarts';
import { requestTo, settle } from '../../../../testing/http';
import { textOf, wordsOf } from '../../../../testing/pages';
import { FakeResizeObserver } from '../../../../testing/resize';
import { AuthStore } from '../../../core/auth/auth-store';
import { sessionInterceptor } from '../../../core/auth/session.interceptor';
import { catalogVersionInterceptor } from '../../../core/catalog/catalog-version';
import type { Definition } from '../model/definition';
import { DASHBOARD_WAITS } from '../state/waits';
import { EmbedPage, type SizeMessage } from './embed-page';

const token = 'Abc123_-Abc123_-Abc123';

/** The sales dashboard as anyone with its link gets it: a Status filter viewers may change. */
const shown: Definition = publicOf(
  salesDefinition({
    filters: [
      {
        id: 'status',
        label: 'Status',
        field: { source: '', path: [], column: '' },
        kind: 'values',
        value: null,
        visible: true,
        editable: true,
        multiple: true,
        except: [],
      },
    ],
  }),
);

/** A state as a public request's `s` holds it: base64url JSON. */
function stateOf(s: string | null): unknown {
  if (s === null) {
    return null;
  }
  const binary = atob(s.replace(/-/g, '+').replace(/_/g, '/'));
  return JSON.parse(new TextDecoder().decode(Uint8Array.from(binary, (c) => c.charCodeAt(0))));
}

describe('an embedded dashboard', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    FakeResizeObserver.install();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([catalogVersionInterceptor, sessionInterceptor])),
        provideHttpClientTesting(),
        provideRouter(
          [{ path: 'embed/:token', component: EmbedPage }],
          withComponentInputBinding(),
        ),
        // Nothing of an embed asks who is signed in: the session's store isn't to be made.
        {
          provide: AuthStore,
          useFactory: () => {
            throw new Error('An embedded dashboard asks for no session');
          },
        },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        fakeEChartsProviders(new FakeECharts()),
        { provide: DASHBOARD_WAITS, useValue: { preview: 1, filterTyping: 1 } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    FakeResizeObserver.uninstall();
    delete (window as { parent?: unknown }).parent;
    TestBed.resetTestingModule();
  });

  /** The widgets' rows asked for now, by widget, with their states; each answered. */
  async function rows(): Promise<Record<string, unknown>> {
    await settle(5);
    const asked = http.match((r) => r.url.startsWith(`/api/public/dashboards/${token}/widgets/`));
    const states: Record<string, unknown> = {};
    for (const request of asked) {
      states[request.request.url.split('/')[6]] = stateOf(request.request.params.get('s'));
      request.flush(rowsOf('Status', [['open', '2']]));
    }
    return states;
  }

  it('shows the dashboard of its link, asking for no session, what is chosen in its address', async () => {
    const harness = await RouterTestingHarness.create(`/embed/${token}?f.status=open&theme=dark`);
    (await requestTo(http, `/api/public/dashboards/${token}`)).flush({
      name: 'Sales',
      description: null,
      definition: shown,
    });
    const states = await rows();
    expect(states).toEqual({
      'by-status': { filters: { status: { op: 'in', values: ['open'] } }, selections: {} },
      'by-city': { filters: { status: { op: 'in', values: ['open'] } }, selections: {} },
    });
    harness.detectChanges();
    const page = harness.routeNativeElement as HTMLElement;
    expect(textOf(page.querySelector('h1'))).toBe('Sales');
    expect(page.querySelector('gd-filter-bar')).not.toBeNull();
    // Nothing signed in: no session asked for, no query to see.
    http.expectNone('/api/auth/session');
    page.querySelector<HTMLButtonElement>('[aria-label="Actions of Orders by status"]')!.click();
    harness.detectChanges();
    await settle();
    const items = [...document.querySelectorAll('.mat-mdc-menu-panel button')].map((b) =>
      textOf(b),
    );
    expect(items.some((i) => i.includes('View query'))).toBe(false);
    expect(items.some((i) => i.includes('Data'))).toBe(true);
    http.verify();
  });

  it("says plainly when the dashboard isn't there, and asks no one to sign in", async () => {
    const harness = await RouterTestingHarness.create(`/embed/${token}`);
    (await requestTo(http, `/api/public/dashboards/${token}`)).flush(
      { status: 404, code: 'not-found', title: 'Not found' },
      { status: 404, statusText: 'Not found' },
    );
    await settle();
    harness.detectChanges();
    const page = harness.routeNativeElement as HTMLElement;
    expect(wordsOf(page.querySelector('[role=alert]'))).toBe(
      "This dashboard isn't available. It may no longer be public.",
    );
    expect(textOf(page)).not.toMatch(/sign in/i);
    expect(page.querySelector('button')).toBeNull();
    http.expectNone('/api/auth/session');
  });

  it('says when it is asked for too often, and when to try again', async () => {
    const harness = await RouterTestingHarness.create(`/embed/${token}`);
    (await requestTo(http, `/api/public/dashboards/${token}`)).flush(
      { status: 429, code: 'too-many-requests', title: 'Too many requests' },
      { status: 429, statusText: 'Too many', headers: { 'Retry-After': '20' } },
    );
    await settle();
    harness.detectChanges();
    const alert = (harness.routeNativeElement as HTMLElement).querySelector('[role=alert]')!;
    expect(textOf(alert)).toContain(
      'The dashboard is asked for too often just now. Try again in 20 seconds.',
    );
    expect(textOf(alert.querySelector('button'))).toBe('Try again');
  });

  it('framed, tells its parent its height as it changes, and only then', async () => {
    const told: [SizeMessage, string][] = [];
    const parent = {
      postMessage: (message: SizeMessage, origin: string) => told.push([message, origin]),
    };
    Object.defineProperty(window, 'parent', { configurable: true, value: parent });
    const harness = await RouterTestingHarness.create(`/embed/${token}`);
    (await requestTo(http, `/api/public/dashboards/${token}`)).flush({
      name: 'Sales',
      description: null,
      definition: shown,
    });
    await rows();
    harness.detectChanges();
    const host =
      (harness.routeNativeElement as HTMLElement).closest('gd-embed-page') ??
      (harness.routeNativeElement as HTMLElement);
    let height = 640;
    host.getBoundingClientRect = () => ({ height }) as DOMRect;
    const frame = () => new Promise((resolve) => requestAnimationFrame(resolve));
    FakeResizeObserver.resize(host, 800, height);
    await frame();
    FakeResizeObserver.resize(host, 800, height);
    await frame();
    height = 900.4;
    FakeResizeObserver.resize(host, 800, height);
    await frame();
    expect(told).toEqual([
      [{ type: 'galaxydata.dashboard.size', version: 1, height: 640 }, '*'],
      [{ type: 'galaxydata.dashboard.size', version: 1, height: 901 }, '*'],
    ]);
  });
});
