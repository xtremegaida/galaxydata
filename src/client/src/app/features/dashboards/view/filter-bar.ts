import { ChangeDetectionStrategy, Component, LOCALE_ID, computed, inject } from '@angular/core';
import { MatIcon } from '@angular/material/icon';
import { filterValueOf, filterValueText } from '../model/describe';
import type { Filter } from '../model/definition';
import { DashboardStore } from '../state/dashboard-store';
import {
  BooleanFilter,
  RangeFilter,
  RelativeFilter,
  TextFilter,
  ValuesFilter,
} from './filter-controls';

/**
 * A dashboard's filters that viewers see: those they may change as controls by their kinds, the others as what
 * they keep (locked). Hidden filters aren't shown, and still apply.
 */
@Component({
  selector: 'gd-filter-bar',
  imports: [BooleanFilter, MatIcon, RangeFilter, RelativeFilter, TextFilter, ValuesFilter],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="bar" role="group" aria-label="Filters">
      @for (filter of shown(); track filter.id) {
        @if (filter.editable) {
          @switch (filter.kind) {
            @case ('values') {
              <gd-values-filter [filter]="filter" />
            }
            @case ('range') {
              <gd-range-filter [filter]="filter" />
            }
            @case ('relative') {
              <gd-relative-filter [filter]="filter" />
            }
            @case ('text') {
              <gd-text-filter [filter]="filter" />
            }
            @case ('boolean') {
              <gd-boolean-filter [filter]="filter" />
            }
          }
        } @else {
          <span class="locked" [attr.aria-label]="filter.label + ': ' + said(filter) + ', fixed'">
            <mat-icon aria-hidden="true">lock</mat-icon>
            <span class="label">{{ filter.label }}:</span> {{ said(filter) }}
          </span>
        }
      }
    </div>
  `,
  styles: `
    .bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px 12px;
    }
    .locked {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 4px 10px;
      border-radius: 8px;
      font: var(--mat-sys-label-large);
      background: var(--mat-sys-surface-container-high);
      color: var(--mat-sys-on-surface);
    }
    .locked mat-icon {
      font-size: 16px;
      width: 16px;
      height: 16px;
      color: var(--mat-sys-on-surface-variant);
    }
    .label {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class FilterBar {
  protected readonly store = inject(DashboardStore);
  private readonly locale = inject(LOCALE_ID);

  protected readonly shown = computed(() =>
    this.store.definition().filters.filter((filter) => filter.visible),
  );

  protected said(filter: Filter): string {
    return filterValueText(filterValueOf(filter, this.store.filters()), this.locale);
  }
}
