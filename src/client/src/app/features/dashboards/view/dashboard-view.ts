import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
} from '@angular/core';
import { MatTabLink, MatTabNav, MatTabNavPanel } from '@angular/material/tabs';
import { DashboardGrid } from '../layout/dashboard-grid';
import { tabsOf } from '../model/tabs';
import { DashboardStore } from '../state/dashboard-store';
import { FilterBar } from './filter-bar';
import { SelectionChips } from './selection-chips';

/**
 * A dashboard as viewers see it, in the application and embedded alike: the filters they see and the slices chosen
 * (each only when there are some), and its grid of widgets; with tabs, a grid per tab, under its tabs. It has no
 * title of its own: headings are widgets, so a blank dashboard is a blank page. A tab's grid is laid out once it is
 * shown, and kept when another is (collapsed: its widgets keep their rows and size, and ask for nothing till it is
 * shown again).
 */
@Component({
  selector: 'gd-dashboard-view',
  imports: [DashboardGrid, FilterBar, MatTabLink, MatTabNav, MatTabNavPanel, SelectionChips],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (filtered()) {
      <gd-filter-bar class="part" />
    }
    @if (chosen()) {
      <gd-selection-chips class="part" />
    }
    @if (store.stateRead()) {
      @if (tabs().length > 0) {
        <div class="tabs">
          <nav mat-tab-nav-bar mat-stretch-tabs="false" aria-label="Tabs" [tabPanel]="panel">
            @for (tab of tabs(); track tab.id) {
              <button
                mat-tab-link
                type="button"
                [active]="tab.id === store.tab()"
                [attr.data-gd-tab]="tab.id"
                (click)="show(tab.id)"
              >
                {{ tab.label }}
              </button>
            }
          </nav>
          <ng-content select="[gdTabsEnd]" />
        </div>
        <mat-tab-nav-panel #panel>
          @for (tab of laidOut(); track tab.id) {
            <gd-dashboard-grid
              [class.away]="tab.id !== store.tab()"
              [attr.inert]="tab.id === store.tab() ? null : ''"
              [definition]="store.definition()"
              [tab]="tab.id"
              [active]="tab.id === store.tab()"
              [forced]="breakpoint()"
            />
          }
        </mat-tab-nav-panel>
      } @else {
        <gd-dashboard-grid [definition]="store.definition()" [forced]="breakpoint()" />
      }
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .part {
      display: block;
      margin-bottom: 8px;
    }
    .tabs {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 12px;
    }
    .tabs nav {
      flex: 1 1 auto;
      min-width: 0;
    }
    mat-tab-nav-panel {
      display: block;
    }
    /* A tab not shown: kept as wide as it was (its breakpoint, and its charts' sizes, hold), taking no room. */
    .away {
      height: 0;
      overflow: hidden;
      visibility: hidden;
    }
  `,
})
export class DashboardView {
  protected readonly store = inject(DashboardStore);
  /** A breakpoint shown whatever the width (the editor's). */
  readonly breakpoint = input<string | null>(null);

  protected readonly tabs = computed(() => tabsOf(this.store.definition()));

  /** The tabs shown so far (once the address has said which is), in their order. */
  private readonly seen = linkedSignal<string | null, ReadonlySet<string>>({
    source: () => (this.store.stateRead() ? this.store.tab() : null),
    computation: (tab, previous) =>
      tab === null || previous?.value.has(tab)
        ? (previous?.value ?? new Set())
        : new Set([...(previous?.value ?? []), tab]),
  });
  protected readonly laidOut = computed(() => this.tabs().filter((tab) => this.seen().has(tab.id)));

  protected readonly filtered = computed(() =>
    this.store.definition().filters.some((filter) => filter.visible),
  );
  protected readonly chosen = computed(() =>
    Object.values(this.store.selections()).some((selection) => selection.keys.length > 0),
  );

  protected show(tab: string): void {
    this.store.chosenTab.set(tab);
  }
}
