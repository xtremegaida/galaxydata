import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient, type Schema } from '../../../core/api/api-client';
import { problemOf } from '../../../core/api/problem';

export type PaletteDto = Schema<'PaletteDto'>;
export type PaletteSummary = Schema<'PaletteSummaryDto'>;

/**
 * The palettes the editors know: their summaries, to choose one by, and those read whole, to draw with (null: there
 * is none, deleted or never there). The application's, so a palette saved in one place recolours the charts drawn
 * with it everywhere it is open.
 */
@Injectable({ providedIn: 'root' })
export class PaletteLibrary {
  private readonly api = inject(ApiClient);
  private readonly asked = new Set<number>();

  /** Read whole, by id. */
  readonly read = signal<ReadonlyMap<number, PaletteDto | null>>(new Map());

  /** Every palette, by name (none until first listed). */
  readonly summaries = signal<readonly PaletteSummary[] | null>(null);

  /** Reads the palettes not read yet (once each, but after a failure). */
  want(ids: Iterable<number>): void {
    for (const id of ids) {
      if (this.asked.has(id)) {
        continue;
      }
      this.asked.add(id);
      this.api.get('/api/palettes/{id}', { path: { id } }).subscribe({
        next: (palette) => this.put(palette),
        error: (error: unknown) => {
          if (problemOf(error).status === 404) {
            this.set(id, null);
          } else {
            this.asked.delete(id);
          }
        },
      });
    }
  }

  /** Lists every palette (again). */
  async list(): Promise<readonly PaletteSummary[]> {
    const summaries = await firstValueFrom(this.api.get('/api/palettes'));
    this.summaries.set(summaries);
    return summaries;
  }

  /** A palette as it was saved, or read again: what is drawn with it follows. */
  put(palette: PaletteDto): void {
    this.asked.add(palette.id);
    this.set(palette.id, palette);
    const summaries = this.summaries();
    if (summaries) {
      const summary: PaletteSummary = {
        id: palette.id,
        name: palette.name,
        description: palette.description,
        owner: palette.owner,
        isMine: palette.isMine,
        canEdit: palette.canEdit,
        colors: palette.definition.colors,
        assign: palette.definition.assign,
        overrides: palette.definition.overrides.length,
        usedBy: palette.usedBy,
        updatedAt: palette.updatedAt,
        version: palette.version,
      };
      const others = summaries.filter((s) => s.id !== palette.id);
      this.summaries.set(
        [...others, summary].sort(
          (a, b) => a.name.localeCompare(b.name) || a.owner.localeCompare(b.owner),
        ),
      );
    }
  }

  /** A palette deleted: what was drawn with it goes back to the dashboard's colours. */
  removed(id: number): void {
    this.asked.add(id);
    this.set(id, null);
    const summaries = this.summaries();
    if (summaries) {
      this.summaries.set(summaries.filter((s) => s.id !== id));
    }
  }

  private set(id: number, palette: PaletteDto | null): void {
    const next = new Map(this.read());
    next.set(id, palette);
    this.read.set(next);
  }
}
