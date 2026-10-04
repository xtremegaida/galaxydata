import type { Schema } from '../app/core/api/api-client';
import descriptors from '../../../../tests/GalaxyData.Web.Tests/Connections/Snapshots/ConnectionApiTests.TheKindsDescribeTheirForms.json';

export type KindDto = Schema<'ConnectionKindDto'>;
export type ConnectionDto = Schema<'ConnectionDto'>;

/** The kinds as the server describes them (the snapshot its tests keep). */
export const kinds = descriptors as unknown as KindDto[];

/** A kind, by its id. */
export function kindOf(id: string): KindDto {
  const kind = kinds.find((candidate) => candidate.id === id);
  if (!kind) {
    throw new Error(`No kind ${id}`);
  }
  return kind;
}

/** A stored connection, as the server gives it: a SQLite one, unless said otherwise. */
export function connectionOf(changes: Partial<ConnectionDto> = {}): ConnectionDto {
  return {
    id: 1,
    alias: 'shop',
    kind: 'sqlite',
    displayName: 'The shop',
    mode: 'form',
    settings: { 'Data Source': 'C:\\data\\files\\shop.db' },
    secrets: {},
    connectionString: 'Data Source=C:\\data\\files\\shop.db',
    options: {},
    isReadOnly: true,
    secretsUnreadable: false,
    schemaStatus: 'ready',
    schemaError: null,
    schemaRefreshedAt: '2026-10-04T12:00:00Z',
    createdAt: '2026-10-01T09:00:00Z',
    updatedAt: '2026-10-04T12:00:00Z',
    version: 3,
    ...changes,
  };
}
