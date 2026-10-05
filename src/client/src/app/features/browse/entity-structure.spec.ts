import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { Schema } from '../../core/api/api-client';
import { entityOf } from '../../../testing/catalog';
import { textOf } from '../../../testing/pages';
import { type Entity, EntityStructure } from './entity-structure';

type Navigation = Schema<'NavigationDto'>;

@Component({
  imports: [EntityStructure],
  template: `<gd-entity-structure [entity]="entity()" />`,
})
class Host {
  readonly entity = signal<Entity>(entityOf());
}

function render(entity: Entity): HTMLElement {
  TestBed.configureTestingModule({ providers: [provideRouter([])] });
  const fixture = TestBed.createComponent(Host);
  fixture.componentInstance.entity.set(entity);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

/** The links in an element: what they say (to screen readers too), and where they lead. */
function linksIn(element: Element | null | undefined): [string, string][] {
  return [...(element?.querySelectorAll('a') ?? [])].map((link) => [
    textOf(link),
    decodeURIComponent(link.getAttribute('href') ?? ''),
  ]);
}

/** The links of the facts' Overlay. */
function overlayFacts(page: HTMLElement): [string, string][] | null {
  const term = [...page.querySelectorAll('.facts dt')].find((dt) => textOf(dt) === 'Overlay');
  return term ? linksIn(term.nextElementSibling) : null;
}

/** Each navigation's links to the overlay, by its name. */
function overlayOfNavigations(page: HTMLElement): Record<string, [string, string][]> {
  const section = page.querySelector('[aria-labelledby="gd-entity-navigations"]')!;
  const headers = [...section.querySelectorAll('thead th')].map((th) => textOf(th));
  const at = headers.indexOf('Overlay');
  return Object.fromEntries(
    [...section.querySelectorAll('tbody tr')].map((row) => [
      textOf(row.querySelector('th')),
      at < 0 ? [] : linksIn(row.children[at]),
    ]),
  );
}

function navigationOf(changes: Partial<Navigation>): Navigation {
  return {
    name: 'shipments',
    target: 'wh.shipments',
    multiplicity: 'many',
    columns: ['id'],
    targetColumns: ['order_ref'],
    isInverse: true,
    origin: 'overlay',
    isEnforced: false,
    isCrossSource: true,
    hidden: false,
    inherited: false,
    overlay: null,
    ...changes,
  };
}

describe('EntityStructure', () => {
  it('leads administrators to the items that make and set the entity and its navigations', () => {
    const orders = entityOf();
    const page = render({
      ...orders,
      overlay: { virtualEntity: null, settings: 4 },
      navigations: [
        { ...orders.navigations[0], overlay: { relation: null, override: 7 } },
        { ...orders.navigations[1], overlay: { relation: null, override: null } },
        navigationOf({ overlay: { relation: 3, override: null } }),
        navigationOf({
          name: 'region',
          inherited: true,
          overlay: { relation: null, override: null },
        }),
      ],
    });

    expect(overlayFacts(page)).toEqual([
      ['Its settings', '/admin/overlay/entity-settings/4'],
      ['Make a relation from it', '/admin/overlay/relations/new?from=shop.orders'],
    ]);
    expect(overlayOfNavigations(page)).toEqual({
      customer: [['Its override: customer', '/admin/overlay/navigations/7']],
      order_lines: [
        [
          'Rename or hide order_lines',
          '/admin/overlay/navigations/new?entity=shop.orders&navigation=order_lines',
        ],
      ],
      shipments: [
        ['Its relation: shipments', '/admin/overlay/relations/3'],
        [
          'Rename or hide shipments',
          '/admin/overlay/navigations/new?entity=shop.orders&navigation=shipments',
        ],
      ],
      // Its entity's: renamed or hidden there.
      region: [],
    });
  });

  it('leads to a virtual entity’s definition, and to settings to make', () => {
    const name = 'xl["Q1 & Q2"]["Sheet 1"]';
    const page = render(
      entityOf({ name, kind: 'virtual', overlay: { virtualEntity: 2, settings: null } }),
    );

    expect(overlayFacts(page)).toEqual([
      ['Its definition', '/admin/overlay/virtual-entities/2'],
      ['Make settings for it', '/admin/overlay/entity-settings/new?entity=' + name],
      ['Make a relation from it', '/admin/overlay/relations/new?from=' + name],
    ]);
    const settings = page.querySelector<HTMLAnchorElement>('.facts a[href*="entity-settings"]')!;
    expect(new URL(settings.href).searchParams.get('entity')).toBe(name);
  });

  it('says nothing of the overlay to others', () => {
    const page = render(entityOf());

    expect(overlayFacts(page)).toBeNull();
    expect(overlayOfNavigations(page)).toEqual({ customer: [], order_lines: [] });
    expect(
      [...page.querySelectorAll('[aria-labelledby="gd-entity-navigations"] thead th')].map((th) =>
        textOf(th),
      ),
    ).toEqual(['Name', 'To', 'Rows', 'Through', 'Notes']);
  });
});
