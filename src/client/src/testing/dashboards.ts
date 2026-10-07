import type { Schema } from '../app/core/api/api-client';
import type { Definition, Widget, WidgetData } from '../app/features/dashboards/model/definition';
import { barDefaults, pieDefaults } from '../app/features/dashboards/model/widget-defaults';
import { blankDefinition } from '../app/features/dashboards/state/dashboard-store';

type DashboardDto = Schema<'DashboardDto'>;

/** A dashboard of the shop's orders: a heading, a bar chart of orders by status, a pie of customers by city. */
export function salesDefinition(extra: Partial<Definition> = {}): Definition {
  const blank = blankDefinition();
  const widgets: Widget[] = [
    { id: 'title', title: null, config: { kind: 'text', markdown: '# Sales' } },
    {
      id: 'by-status',
      title: 'Orders by status',
      config: {
        ...barDefaults('orders'),
        dimension: { field: { path: [], column: 'status' }, bucket: null, label: 'Status' },
      },
    },
    {
      id: 'by-city',
      title: 'Customers by city',
      config: {
        ...pieDefaults('customers'),
        dimension: { field: { path: [], column: 'city' }, bucket: null, label: 'City' },
      },
    },
  ];
  return {
    ...blank,
    sources: [
      { id: 'orders', entity: 'shop.orders', label: 'Orders' },
      { id: 'customers', entity: 'shop.customers', label: 'Customers' },
    ],
    links: [{ from: 'orders', to: 'customers', path: ['customer'] }],
    widgets,
    layout: {
      ...blank.layout,
      items: {
        title: { x: 0, y: 0, w: 12, h: 1 },
        'by-status': { x: 0, y: 1, w: 6, h: 6 },
        'by-city': { x: 6, y: 1, w: 6, h: 6 },
      },
    },
    ...extra,
  };
}

/** A dashboard as the API gives it: published, and (for its owner) its working copy. */
export function dashboardOf(
  definition: Definition = salesDefinition(),
  extra: Partial<DashboardDto> = {},
): DashboardDto {
  return {
    id: 1,
    name: 'Sales',
    description: null,
    owner: 'ada',
    isMine: true,
    working: definition,
    workingHash: 'w'.repeat(64),
    published: definition,
    publishedHash: 'p'.repeat(64),
    publishedNumber: 1,
    publishedAt: '2026-03-01T09:00:00Z',
    publishedBy: 'ada',
    hasUnpublishedChanges: false,
    sharing: { everyone: false, users: [] },
    public: null,
    can: {
      edit: true,
      publish: true,
      share: true,
      makePublic: true,
      revokePublic: false,
      delete: true,
      copy: true,
    },
    issues: [],
    palettes: [],
    createdAt: '2026-03-01T08:00:00Z',
    updatedAt: '2026-03-01T09:00:00Z',
    version: 2,
    ...extra,
  };
}

const text = { kind: 'string', nullable: true, text: 'string?' } as const;
const int64 = { kind: 'int64', nullable: false, text: 'int64' } as const;

/** A widget's rows: a dimension of text and a count. */
export function rowsOf(
  label: string,
  rows: [string | null, string][],
  extra: Partial<WidgetData> = {},
): WidgetData {
  return {
    columns: [
      { name: 'd0', role: 'dimension', index: 0, label, type: text, format: null },
      { name: 'm0', role: 'measure', index: 0, label: 'Rows', type: int64, format: null },
    ],
    rows,
    truncated: false,
    offset: 0,
    total: null,
    categories: null,
    other: null,
    refreshedAt: '2026-03-01T10:00:00Z',
    cached: false,
    issues: [],
    ...extra,
  } as WidgetData;
}

/** The URL of a published widget's rows. */
export function widgetDataUrl(id: number, widget: string): string {
  return `/api/dashboards/${id}/widgets/${widget}/data`;
}

/** A definition as anyone with its public link gets it: no sources, links, conditions or fields (the server's). */
export function publicOf(definition: Definition): Definition {
  const blank = { path: [], column: '' };
  const plain = <T extends { field: unknown }>(d: T): T => ({ ...d, field: blank });
  return {
    ...definition,
    sources: [],
    links: [],
    filters: definition.filters
      .filter((f) => f.visible)
      .map((f) => ({ ...f, field: { source: '', path: [], column: '' }, except: [] })),
    widgets: definition.widgets.map((w) => {
      const config = w.config as Record<string, unknown>;
      if (config['kind'] === 'text') {
        return w;
      }
      return {
        ...w,
        config: {
          ...config,
          source: '',
          conditions: [],
          ...(config['dimension']
            ? { dimension: plain(config['dimension'] as { field: unknown }) }
            : {}),
        } as unknown as Widget['config'],
      };
    }),
  };
}
