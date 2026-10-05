import { HttpParams } from '@angular/common/http';
import type { HttpTestingController } from '@angular/common/http/testing';
import type { RouterTestingHarness } from '@angular/router/testing';
import type {
  EntitySettings,
  NavigationOverride,
  Overlay,
  OverlayCheck,
  OverlayIssue,
  Relation,
  VirtualEntity,
} from '../app/features/admin/overlay/overlay-items';
import { entityOf, hitOf, tableNode, type EntityDto } from './catalog';
import { settle } from './http';
import { textOf, wordsOf } from './pages';

export const overlayUrl = '/api/overlay';
export const relationsUrl = '/api/overlay/relations';
export const navigationsUrl = '/api/overlay/navigations';
export const virtualEntitiesUrl = '/api/overlay/virtual-entities';
export const settingsUrl = '/api/overlay/entity-settings';

/** Where an item of a kind (its path) is tried: in place of the item `id` when given. */
export function validateUrl(path: string, id?: number): string {
  return id === undefined ? `${path}/validate` : `${path}/validate?id=${id}`;
}

/** Where an entity is looked up. */
export function entityUrl(name: string): string {
  return `/api/catalog/entity?${new HttpParams({ fromObject: { name } }).toString()}`;
}

/** Where entities are searched for, to suggest. */
export function suggestUrl(text: string): string {
  return `/api/catalog/tree/search?${new HttpParams({ fromObject: { text, take: 20 } }).toString()}`;
}

/** An issue: an error, unless said otherwise. */
export function issueOf(
  message: string,
  severity: OverlayIssue['severity'] = 'error',
  code = 'GDQ5001',
): OverlayIssue {
  return { code, severity, message };
}

const stamps = {
  createdAt: '2026-10-01T09:00:00Z',
  updatedAt: '2026-10-04T12:00:00Z',
};

/** A relation: wh.orders (customer_id) to shop.customers (id), which works, unless said otherwise. */
export function relationOf(changes: Partial<Relation> = {}): Relation {
  return {
    id: 3,
    from: 'wh.orders',
    fromColumns: ['customer_id'],
    to: 'shop.customers',
    toColumns: ['id'],
    name: null,
    inverseName: null,
    description: null,
    navigations: { forward: 'customer', inverse: 'wh_orders' },
    issues: [],
    ...stamps,
    version: 2,
    ...changes,
  };
}

/** An override hiding shop.orders' navigation bill_address, unless said otherwise. */
export function overrideOf(changes: Partial<NavigationOverride> = {}): NavigationOverride {
  return {
    id: 4,
    entity: 'shop.orders',
    navigation: 'bill_address',
    renameTo: null,
    hidden: true,
    issues: [],
    ...stamps,
    version: 1,
    ...changes,
  };
}

/** A virtual entity: reports.big_orders, the orders over 100, unless said otherwise. */
export function virtualEntityOf(changes: Partial<VirtualEntity> = {}): VirtualEntity {
  return {
    id: 5,
    name: 'reports.big_orders',
    query: 'shop.orders.where(total > 100)',
    key: null,
    description: null,
    issues: [],
    ...stamps,
    version: 1,
    ...changes,
  };
}

/** Settings of shop.customers: shown by name, its city labelled, unless said otherwise. */
export function settingsOf(changes: Partial<EntitySettings> = {}): EntitySettings {
  return {
    id: 6,
    entity: 'shop.customers',
    key: null,
    displayColumn: 'name',
    hidden: false,
    columns: [{ name: 'city', hidden: false, label: 'Town', type: null }],
    issues: [],
    ...stamps,
    version: 3,
    ...changes,
  };
}

/** The overlay: none of each, unless said otherwise. */
export function overlayOf(changes: Partial<Overlay> = {}): Overlay {
  return {
    catalogVersion: 'v1',
    relations: [],
    navigations: [],
    virtualEntities: [],
    entitySettings: [],
    errors: 0,
    warnings: 0,
    ...changes,
  };
}

/** What trying an item gives: it works, unless said otherwise. */
export function checkOf(changes: Partial<OverlayCheck> = {}): OverlayCheck {
  return { issues: [], breaks: [], ...changes };
}

/** Columns of those names, as an entity describes them: text, but `id`, the key. */
export function columnsNamed(...names: string[]): EntityDto['columns'] {
  const [id, , text] = entityOf().columns;
  return names.map((name, ordinal) => ({ ...(name === 'id' ? id : text), name, ordinal }));
}

/**
 * Answers the lookups the pages make as typing pauses: the entities named (from `entities`; others aren't there),
 * and searches (finding the paths `found` has for their text; nothing, for others).
 */
export async function answerLookups(
  http: HttpTestingController,
  entities: Readonly<Record<string, EntityDto>>,
  found: Readonly<Record<string, readonly string[]>> = {},
): Promise<void> {
  await settle();
  for (const request of http.match((each) => each.url === '/api/catalog/entity')) {
    const entity = entities[request.request.params.get('name') ?? ''];
    if (entity) {
      request.flush(entity);
    } else {
      request.flush(
        { code: 'not-found', title: 'There is no such entity', status: 404 },
        { status: 404, statusText: 'Not found' },
      );
    }
  }
  for (const request of http.match((each) => each.url === '/api/catalog/tree/search')) {
    const text = request.request.params.get('text') ?? '';
    request.flush({
      text,
      hits: (found[text] ?? []).map((path) => hitOf(tableNode(path), [path.split('.')[0]])),
      more: false,
    });
  }
}

/** An overlay page opened, and what its tests do with it: its fields by label, typing, choosing, pressing. */
export function overlayPage<
  T extends { harness: RouterTestingHarness; http: HttpTestingController; page: HTMLElement },
>(opened: T) {
  const { harness } = opened;
  /** The page shown now: another, once a navigation made one (a new item, once made). */
  const current = () => harness.routeNativeElement as HTMLElement;
  const shown = async (turns = 3) => {
    for (let turn = 0; turn < turns; turn++) {
      await settle();
      harness.detectChanges();
    }
  };
  const formFields = (label: string) =>
    [...current().querySelectorAll('mat-form-field')].filter(
      (field) => textOf(field.querySelector('mat-label')).replace(/\s*\*$/, '') === label,
    );
  const fields = (label: string) =>
    formFields(label).map(
      (field) => field.querySelector<HTMLInputElement>('input, textarea') as HTMLInputElement,
    );
  const field = (label: string) => {
    const found = fields(label)[0];
    if (!found) {
      throw new Error(`No field "${label}"`);
    }
    return found;
  };
  /** A button, by its words or its name (an icon's). */
  const button = (text: string) => {
    const found = [...current().querySelectorAll<HTMLButtonElement>('button')].find(
      (candidate) => wordsOf(candidate) === text || candidate.getAttribute('aria-label') === text,
    );
    if (!found) {
      throw new Error(`No button "${text}"`);
    }
    return found;
  };
  return {
    ...opened,
    get page() {
      return current();
    },
    shown,
    field,
    fields,
    /** Types a field's whole text, the keyboard on it (as an autocomplete asks). */
    typeIn: async (input: HTMLInputElement, text: string) => {
      input.focus();
      input.value = text;
      input.dispatchEvent(new Event('input'));
      await shown();
    },
    /** The options an autocomplete offers. */
    options: () => [...document.querySelectorAll('mat-option')].map((option) => wordsOf(option)),
    choose: async (text: string) => {
      const option = [...document.querySelectorAll<HTMLElement>('mat-option')].find(
        (candidate) => wordsOf(candidate) === text,
      );
      if (!option) {
        throw new Error(`No option "${text}"`);
      }
      option.click();
      await shown();
    },
    /** Closes what autocompletes offer, as Escape does. */
    closePanels: async () => {
      document.activeElement?.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
      );
      (document.activeElement as HTMLElement | null)?.blur();
      await shown();
    },
    /** The buttons, by their words or their names (an icon's). */
    buttons: () =>
      [...current().querySelectorAll('button')].map(
        (each) => wordsOf(each) || (each.getAttribute('aria-label') ?? ''),
      ),
    isDisabled: (text: string) => button(text).disabled,
    press: async (text: string) => {
      button(text).click();
      await shown();
    },
    hintOf: (label: string) => formFields(label)[0]?.querySelector('mat-hint') ?? null,
    errorOf: (input: Element) =>
      input.closest('mat-form-field')?.querySelector('mat-error') ?? null,
  };
}
