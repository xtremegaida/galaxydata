import { TestBed } from '@angular/core/testing';
import { entityOf } from '../../../../testing/catalog';
import { requestTo, settle } from '../../../../testing/http';
import { answerLookups } from '../../../../testing/overlay';
import { openPage, pageProviders, textOf, wordsOf } from '../../../../testing/pages';
import { dashboardRoutes } from '../dashboards.routes';
import type { PaletteDefinition } from '../charts/series-colors';
import { DASHBOARD_WAITS } from '../state/waits';
import type { PaletteDto, PaletteSummary } from './palette-library';
import { PaletteEditor } from './palette-editor';

function summaryOf(id: number, name: string, extra: Partial<PaletteSummary> = {}): PaletteSummary {
  return {
    id,
    name,
    description: null,
    owner: 'ada',
    isMine: true,
    canEdit: true,
    colors: [
      { light: '#2a78d6', dark: '#3987e5' },
      { light: '#eb6834', dark: null },
    ],
    assign: 'label',
    overrides: 2,
    usedBy: 2,
    updatedAt: '2026-03-01T09:00:00Z',
    version: 0,
    ...extra,
  };
}

function definitionOf(extra: Partial<PaletteDefinition> = {}): PaletteDefinition {
  return {
    colors: [
      { light: '#2a78d6', dark: '#3987e5' },
      { light: '#eb6834', dark: null },
    ],
    assign: 'label',
    distinct: true,
    whenOut: 'repeat',
    matching: {
      ignoreCase: true,
      ignoreWhitespace: false,
      ignoreBrackets: false,
      ignoreAccents: false,
    },
    overrides: [{ label: 'open', color: { light: '#1baf7a', dark: null } }],
    ...extra,
  };
}

function paletteOf(extra: Partial<PaletteDto> = {}): PaletteDto {
  return {
    id: 4,
    name: 'Statuses',
    description: null,
    owner: 'ada',
    isMine: true,
    canEdit: true,
    definition: definitionOf(),
    hash: 'h'.repeat(64),
    usedBy: 1,
    dashboards: [{ id: 1, name: 'Sales', owner: 'ada' }],
    createdAt: '2026-03-01T08:00:00Z',
    updatedAt: '2026-03-01T09:00:00Z',
    version: 3,
    ...extra,
  };
}

describe('the palettes', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'dashboards', children: dashboardRoutes }]),
        { provide: DASHBOARD_WAITS, useValue: { preview: 1, filterTyping: 1 } },
      ],
    });
  });

  afterEach(() => TestBed.resetTestingModule());

  async function shown(harness: { detectChanges(): void }, turns = 3) {
    for (let turn = 0; turn < turns; turn++) {
      await settle();
      harness.detectChanges();
    }
  }

  function button(container: ParentNode, text: string): HTMLButtonElement {
    const found = [...container.querySelectorAll<HTMLButtonElement>('button, a')].find(
      (b) => wordsOf(b) === text,
    ) as HTMLButtonElement | undefined;
    if (!found) {
      throw new Error(`No button "${text}"`);
    }
    return found;
  }

  function type(input: HTMLInputElement | HTMLTextAreaElement, text: string): void {
    input.value = text;
    input.dispatchEvent(new Event('input'));
  }

  it('lists every palette, with whose it is and how many dashboards use it; deletes one after asking', async () => {
    const { page, http, harness } = await openPage('/dashboards/palettes');
    (await requestTo(http, '/api/palettes')).flush([
      summaryOf(1, 'Brand', {
        owner: 'grace',
        isMine: false,
        canEdit: false,
        usedBy: 0,
        overrides: 0,
      }),
      summaryOf(2, 'Statuses'),
    ]);
    await shown(harness);
    const rows = [...page.querySelectorAll('.palette')];
    expect(rows.map((r) => wordsOf(r.querySelector('.about')))).toEqual([
      'grace · by label · 0 labels of their own · no dashboard uses it',
      'ada · by label · 2 labels of their own · used by 2 dashboards',
    ]);
    expect(rows[1].querySelectorAll('.swatch')).toHaveLength(2);
    // Another's: copied, not deleted.
    page.querySelector<HTMLButtonElement>('[aria-label="Actions of Brand"]')!.click();
    await shown(harness);
    expect(
      [...document.querySelectorAll('.mat-mdc-menu-panel [mat-menu-item]')].map((i) => wordsOf(i)),
    ).toEqual(['Copy']);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    (document.querySelector('.cdk-overlay-backdrop') as HTMLElement | null)?.click();
    await shown(harness);
    page.querySelector<HTMLButtonElement>('[aria-label="Actions of Statuses"]')!.click();
    await shown(harness);
    [...document.querySelectorAll<HTMLElement>('.mat-mdc-menu-panel [mat-menu-item]')]
      .find((i) => wordsOf(i) === 'Delete')!
      .click();
    await shown(harness);
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(textOf(dialog)).toContain(
      '2 dashboards use it: their charts get their default colours.',
    );
    button(dialog, 'Delete').click();
    (await requestTo(http, '/api/palettes/2?version=0', 'DELETE')).flush(null);
    await shown(harness);
    expect([...page.querySelectorAll('.palette .name')].map((n) => textOf(n))).toEqual(['Brand']);
  });

  it('makes a palette from a base, and saves it at its own address', async () => {
    const { page, http, harness } = await openPage('/dashboards/palettes/new');
    await shown(harness);
    const editor = harness.routeDebugElement!.componentInstance as PaletteEditor;
    expect(editor.hasUnsavedChanges()).toBe(false);
    // From Okabe and Ito's: six colours, light and dark.
    const bases = [...page.querySelectorAll<HTMLButtonElement>('.base')];
    expect(bases.map((b) => wordsOf(b.querySelector('.base-name')))).toEqual([
      'GalaxyData',
      'Okabe–Ito',
      'Blank',
    ]);
    bases[1].click();
    await shown(harness);
    expect(bases[1].getAttribute('aria-checked')).toBe('true');
    expect(page.querySelectorAll('.colors .color')).toHaveLength(6);
    type(page.querySelector<HTMLInputElement>('input[required]')!, ' Regions ');
    // By order, and grey past the last.
    page.querySelector<HTMLInputElement>('mat-radio-button[value="order"] input')!.click();
    await shown(harness);
    expect(editor.hasUnsavedChanges()).toBe(true);
    button(page, 'Save').click();
    const saved = await requestTo(http, '/api/palettes', 'POST');
    expect(saved.request.body.name).toBe('Regions');
    expect(saved.request.body.definition).toMatchObject({
      assign: 'order',
      distinct: true,
      whenOut: 'repeat',
      overrides: [],
    });
    expect(saved.request.body.definition.colors[0]).toEqual({ light: '#e69f00', dark: '#c08400' });
    saved.flush(
      paletteOf({
        id: 9,
        name: 'Regions',
        definition: saved.request.body.definition,
        usedBy: 0,
        dashboards: [],
        version: 0,
      }),
    );
    await shown(harness);
    // At its own address, where the editor reads it.
    (await requestTo(http, '/api/palettes/9')).flush(
      paletteOf({
        id: 9,
        name: 'Regions',
        definition: saved.request.body.definition,
        usedBy: 0,
        dashboards: [],
        version: 0,
      }),
    );
    await shown(harness);
    const there = harness.routeNativeElement as HTMLElement;
    expect(textOf(there)).toContain('No dashboard uses it yet.');
    expect(
      (harness.routeDebugElement!.componentInstance as PaletteEditor).hasUnsavedChanges(),
    ).toBe(false);
  });

  it('flags labels that match what another does, places what the server refuses, and reads again after a conflict', async () => {
    const { page, http, harness } = await openPage('/dashboards/palettes/4');
    (await requestTo(http, '/api/palettes/4')).flush(paletteOf());
    await shown(harness);
    expect(textOf(page)).toContain('Used by 1 dashboard (Sales): changes show in them at once.');
    button(page, 'Add a label').click();
    await shown(harness);
    const labels = [...page.querySelectorAll<HTMLInputElement>('.override input.label')];
    type(labels[1], 'OPEN');
    await shown(harness);
    expect(textOf(page.querySelectorAll('.override')[1].querySelector('.problem'))).toBe(
      'It matches what label 1 matches',
    );
    // The preview: its own colour, light and dark.
    expect(wordsOf(page.querySelector('.preview li'))).toBe('open its own');
    button(page, 'Save').click();
    (await requestTo(http, '/api/palettes/4', 'PUT')).flush(
      {
        status: 400,
        code: 'invalid-request',
        title: 'Not valid',
        errors: { 'definition.overrides[1].label': ['It matches what overrides[0] matches'] },
      },
      { status: 400, statusText: 'Bad request' },
    );
    await shown(harness);
    expect(textOf(page.querySelectorAll('.override')[1].querySelector('.problem'))).toBe(
      'It matches what overrides[0] matches',
    );
    // Written apart, saved: changed elsewhere since, it is read again and saved over.
    type([...page.querySelectorAll<HTMLInputElement>('.override input.label')][1], 'shipped');
    await shown(harness);
    button(page, 'Save').click();
    const conflict = await requestTo(http, '/api/palettes/4', 'PUT');
    expect(conflict.request.body.version).toBe(3);
    conflict.flush(
      { status: 409, code: 'concurrency-conflict', title: 'Changed' },
      { status: 409, statusText: 'Conflict' },
    );
    await shown(harness);
    button(page, 'Read it again').click();
    (await requestTo(http, '/api/palettes/4')).flush(paletteOf({ version: 5 }));
    await shown(harness);
    button(page, 'Save').click();
    const again = await requestTo(http, '/api/palettes/4', 'PUT');
    expect(again.request.body.version).toBe(5);
    expect(again.request.body.definition.overrides.map((o: { label: string }) => o.label)).toEqual([
      'open',
      'shipped',
    ]);
  });

  it("finds labels in the data, and adds them with the colours they'd get", async () => {
    const { page, http, harness } = await openPage('/dashboards/palettes/4');
    (await requestTo(http, '/api/palettes/4')).flush(paletteOf());
    await shown(harness);
    const finder = page.querySelector('gd-data-labels')!;
    type(finder.querySelector<HTMLInputElement>('input')!, 'orders');
    await answerLookups(http, {}, { orders: ['shop.orders'] });
    await shown(harness);
    finder.querySelector<HTMLInputElement>('input')!.dispatchEvent(new Event('focusin'));
    await shown(harness);
    [...document.querySelectorAll<HTMLElement>('mat-option')]
      .find((o) => textOf(o) === 'shop.orders')!
      .click();
    await shown(harness);
    await answerLookups(http, { 'shop.orders': entityOf() });
    const field = finder.querySelector<HTMLInputElement>('gd-field-picker input')!;
    type(field, 'status');
    field.dispatchEvent(new Event('change'));
    await shown(harness);
    const values = await requestTo(http, '/api/dashboards/filter-values', 'POST');
    expect(values.request.body.slice.sources).toEqual([
      { id: 'labels', entity: 'shop.orders', label: 'Labels' },
    ]);
    expect(values.request.body.slice.filters[0].field).toEqual({
      source: 'labels',
      path: [],
      column: 'status',
    });
    values.flush({
      type: { kind: 'string', nullable: true, text: 'string?' },
      values: [
        { value: 'open', rows: 4 },
        { value: 'cancelled', rows: 2 },
        { value: null, rows: 1 },
      ],
      more: false,
    });
    await shown(harness);
    const boxes = [...finder.querySelectorAll<HTMLInputElement>('.found input[type=checkbox]')];
    expect(boxes).toHaveLength(3);
    boxes[1].click();
    boxes[2].click();
    await shown(harness);
    button(finder, 'Add 2 overrides').click();
    await shown(harness);
    const editor = harness.routeDebugElement!.componentInstance as PaletteEditor;
    const definition = (editor as unknown as { definition: () => PaletteDefinition }).definition();
    expect(definition.overrides.map((o) => o.label)).toEqual(['open', 'cancelled', null]);
    // Each with a colour of the palette's (by label, its own hash's), to change.
    expect(['#2a78d6', '#eb6834']).toContain(definition.overrides[1].color.light);
  });
});
