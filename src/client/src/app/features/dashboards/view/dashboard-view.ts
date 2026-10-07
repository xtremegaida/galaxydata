import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { DashboardGrid } from '../layout/dashboard-grid';
import { DashboardStore } from '../state/dashboard-store';
import { FilterBar } from './filter-bar';
import { SelectionChips } from './selection-chips';

/**
 * A dashboard as viewers see it, in the application and embedded alike: the filters they see and the slices chosen
 * (each only when there are some), and its grid of widgets. It has no title of its own: headings are widgets, so a
 * blank dashboard is a blank page.
 */
@Component({
  selector: 'gd-dashboard-view',
  imports: [DashboardGrid, FilterBar, SelectionChips],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (filtered()) {
      <gd-filter-bar class="part" />
    }
    @if (chosen()) {
      <gd-selection-chips class="part" />
    }
    <gd-dashboard-grid [definition]="store.definition()" [forced]="breakpoint()" />
  `,
  styles: `
    :host {
      display: block;
    }
    .part {
      display: block;
      margin-bottom: 8px;
    }
  `,
})
export class DashboardView {
  protected readonly store = inject(DashboardStore);
  /** A breakpoint shown whatever the width (the editor's). */
  readonly breakpoint = input<string | null>(null);

  protected readonly filtered = computed(() =>
    this.store.definition().filters.some((filter) => filter.visible),
  );
  protected readonly chosen = computed(() =>
    Object.values(this.store.selections()).some((selection) => selection.keys.length > 0),
  );
}
