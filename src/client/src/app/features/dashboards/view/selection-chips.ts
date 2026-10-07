import { ChangeDetectionStrategy, Component, LOCALE_ID, computed, inject } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatChip, MatChipRemove, MatChipSet } from '@angular/material/chips';
import { MatIcon } from '@angular/material/icon';
import { selectionText } from '../model/describe';
import { DashboardStore } from '../state/dashboard-store';

/** The slices chosen in the dashboard's widgets, as sentences ("Status: open, shipped"), each removable; and Clear all. */
@Component({
  selector: 'gd-selection-chips',
  imports: [MatButton, MatChip, MatChipRemove, MatChipSet, MatIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="chips" role="group" aria-label="Chosen in the widgets">
      <mat-chip-set>
        @for (chip of chips(); track chip.widget) {
          <mat-chip (removed)="store.setSelection(chip.widget, null)">
            <span class="name">{{ chip.name }}:</span> {{ chip.values }}
            <button
              matChipRemove
              type="button"
              [attr.aria-label]="'Clear ' + chip.name + ': ' + chip.values"
            >
              <mat-icon>cancel</mat-icon>
            </button>
          </mat-chip>
        }
      </mat-chip-set>
      @if (chips().length > 1) {
        <button matButton type="button" (click)="store.clearSelections()">Clear all</button>
      }
    </div>
  `,
  styles: `
    .chips {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }
    .name {
      color: var(--mat-sys-on-surface-variant);
      margin-inline-end: 4px;
    }
  `,
})
export class SelectionChips {
  protected readonly store = inject(DashboardStore);
  private readonly locale = inject(LOCALE_ID);

  readonly chips = computed(() => {
    const widgets = this.store.definition().widgets;
    return Object.entries(this.store.selections()).flatMap(([id, selection]) => {
      const widget = widgets.find((w) => w.id === id);
      return widget && selection.keys.length > 0
        ? [{ widget: id, ...selectionText(widget, selection, this.locale) }]
        : [];
    });
  });
}
