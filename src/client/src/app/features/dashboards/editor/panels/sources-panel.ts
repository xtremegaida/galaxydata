import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import {
  MatAutocomplete,
  MatAutocompleteTrigger,
  type MatAutocompleteSelectedEvent,
} from '@angular/material/autocomplete';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatRadioButton, MatRadioGroup } from '@angular/material/radio';
import { MatSelect } from '@angular/material/select';
import { MatTooltip } from '@angular/material/tooltip';
import { ApiClient } from '../../../../core/api/api-client';
import { problemMessage, problemOf } from '../../../../core/api/problem';
import { EntitySearch } from '../../../../core/catalog/catalog-lookups';
import { Message } from '../../../../core/ui/message';
import {
  addLink,
  addSource,
  removeLink,
  removeSource,
  sourceUsedBy,
  updateSource,
  wouldCycle,
} from '../../model/definition-ops';
import { DASHBOARD_WAITS } from '../../state/waits';
import { EditorStore } from '../editor-store';
import { humanize } from '../controls/field-picker';

/**
 * The dashboard's sources (tables, views or virtual entities of the catalog), each labelled; and the links between
 * them (a path of navigations from one's entity to the other's, as the catalog finds them), through which choices
 * and filters on one reach widgets of the other. Links make a forest: two sources are related one way at most.
 */
@Component({
  selector: 'gd-sources-panel',
  imports: [
    MatAutocomplete,
    MatAutocompleteTrigger,
    MatButton,
    MatFormField,
    MatHint,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatOption,
    MatRadioButton,
    MatRadioGroup,
    MatSelect,
    MatTooltip,
    Message,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="panel">
      <h3>Sources</h3>
      @for (source of store.draft().sources; track source.id) {
        <div class="source">
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>{{ source.entity }}</mat-label>
            <input matInput [value]="source.label" (input)="labelled(source.id, $event)" />
          </mat-form-field>
          <span [matTooltip]="usedBy(source.id)">
            <button
              matIconButton
              type="button"
              [disabled]="usedBy(source.id) !== ''"
              [attr.aria-label]="'Remove ' + source.label"
              (click)="remove(source.id)"
            >
              <mat-icon>delete</mat-icon>
            </button>
          </span>
        </div>
      } @empty {
        <p class="aside">No sources yet: a widget's rows are a source's.</p>
      }
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Add a source</mat-label>
        <input
          #find
          matInput
          [value]="typed()"
          [matAutocomplete]="entities"
          (input)="typed.set(find.value)"
          autocomplete="off"
          spellcheck="false"
        />
        <mat-autocomplete #entities="matAutocomplete" (optionSelected)="add($event, find)">
          @for (path of search.paths(); track path) {
            <mat-option [value]="path">{{ path }}</mat-option>
          }
        </mat-autocomplete>
        <mat-hint>A table, view or virtual entity, by name</mat-hint>
      </mat-form-field>

      <h3>Links</h3>
      <p class="aside">
        Choices and filters on one source reach the widgets of sources linked to it. A widget on a
        source keeps the rows related to at least one row the other source's conditions keep.
      </p>
      @for (link of store.draft().links; track link.from + link.to) {
        <div class="link">
          <span
            >{{ labelOf(link.from) }} → {{ link.path.join(' → ') || '(the same rows)' }} →
            {{ labelOf(link.to) }}</span
          >
          <button
            matIconButton
            type="button"
            [attr.aria-label]="
              'Remove the link of ' + labelOf(link.from) + ' and ' + labelOf(link.to)
            "
            (click)="unlink(link.from, link.to)"
          >
            <mat-icon>link_off</mat-icon>
          </button>
        </div>
      }
      @if (store.draft().sources.length > 1) {
        <div class="new-link">
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Link</mat-label>
            <mat-select [value]="from()" (valueChange)="from.set($event); path.set(null)">
              @for (source of store.draft().sources; track source.id) {
                <mat-option [value]="source.id">{{ source.label }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>with</mat-label>
            <mat-select [value]="to()" (valueChange)="to.set($event); path.set(null)">
              @for (source of linkable(); track source.id) {
                <mat-option [value]="source.id">{{ source.label }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        </div>
        @if (paths.isLoading()) {
          <p class="aside">Finding the paths between them…</p>
        }
        <div role="alert">
          @if (paths.error(); as error) {
            <gd-message kind="problem">Couldn't find paths: {{ message(error) }}</gd-message>
          }
        </div>
        @if (paths.hasValue() ? paths.value() : null; as found) {
          @if (found.length === 0) {
            <p class="aside">The catalog has no path of navigations between them.</p>
          } @else {
            <mat-radio-group
              class="paths"
              aria-label="Through"
              [value]="path()"
              (change)="path.set($event.value)"
            >
              @for (option of found; track $index) {
                <mat-radio-button [value]="option.navigations">
                  {{ option.navigations.join(' → ') }}
                  @if (option.many) {
                    <span class="aside">(to many)</span>
                  }
                </mat-radio-button>
              }
            </mat-radio-group>
            <button matButton="tonal" type="button" [disabled]="!path()" (click)="link()">
              <mat-icon>link</mat-icon>
              Link them
            </button>
          }
        }
      }
    </div>
  `,
  styles: `
    .panel {
      display: grid;
      gap: 8px;
    }
    h3 {
      margin: 8px 0 0;
      font: var(--mat-sys-title-small);
    }
    .source,
    .link {
      display: grid;
      grid-template-columns: 1fr auto;
      align-items: center;
      gap: 4px;
    }
    .new-link {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 8px;
    }
    .paths {
      display: grid;
    }
    .aside {
      margin: 0;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class SourcesPanel {
  protected readonly store = inject(EditorStore);
  private readonly api = inject(ApiClient);
  private readonly waits = inject(DASHBOARD_WAITS);
  protected readonly message = (error: unknown) => problemMessage(problemOf(error));

  protected readonly typed = signal('');
  protected readonly search = new EntitySearch(() => this.typed(), this.waits.filterTyping);
  protected readonly from = signal<string | null>(null);
  protected readonly to = signal<string | null>(null);
  protected readonly path = signal<readonly string[] | null>(null);

  /** The sources the one chosen may be linked with: not itself, nor one related to it already. */
  protected readonly linkable = computed(() => {
    const from = this.from();
    const definition = this.store.draft();
    return definition.sources.filter((s) => from && !wouldCycle(definition, from, s.id));
  });

  protected readonly paths = rxResource({
    params: () => {
      const sources = this.store.draft().sources;
      const from = sources.find((s) => s.id === this.from());
      const to = sources.find((s) => s.id === this.to());
      return from && to && from.id !== to.id ? { from: from.entity, to: to.entity } : undefined;
    },
    stream: ({ params }) =>
      this.api
        .get('/api/catalog/paths', { query: { from: params.from, to: params.to, depth: 3 } })
        .pipe(
          // The steps' navigations, and whether one leads to many.
          map((found) =>
            found.map((p) => ({
              navigations: p.steps.map((s) => s.navigation),
              many: p.steps.some((s) => s.isCollection),
            })),
          ),
        ),
  });

  protected labelOf(id: string): string {
    return this.store.draft().sources.find((s) => s.id === id)?.label ?? id;
  }

  /** What uses a source (it can't go till they don't), said; empty when nothing does. */
  protected usedBy(id: string): string {
    const used = sourceUsedBy(this.store.draft(), id);
    return used.length > 0 ? `Used by ${used.join(', ')}` : '';
  }

  protected labelled(id: string, event: Event): void {
    const label = (event.target as HTMLInputElement).value;
    this.store.apply('Renamed a source', (d) => updateSource(d, id, { label }), `source:${id}`);
  }

  protected add(event: MatAutocompleteSelectedEvent, field: HTMLInputElement): void {
    const entity = String(event.option.value);
    field.value = '';
    this.typed.set('');
    this.store.apply(
      `Added the source ${entity}`,
      (d) => addSource(d, entity, humanize(entity.split('.').at(-1) ?? entity)).definition,
    );
  }

  protected remove(id: string): void {
    this.store.apply(`Removed the source ${this.labelOf(id)}`, (d) => removeSource(d, id));
  }

  protected link(): void {
    const from = this.from();
    const to = this.to();
    const path = this.path();
    if (!from || !to || !path) {
      return;
    }
    this.store.apply(`Linked ${this.labelOf(from)} and ${this.labelOf(to)}`, (d) =>
      addLink(d, { from, to, path: [...path] }),
    );
    this.to.set(null);
    this.path.set(null);
  }

  protected unlink(from: string, to: string): void {
    this.store.apply(`Unlinked ${this.labelOf(from)} and ${this.labelOf(to)}`, (d) =>
      removeLink(d, from, to),
    );
  }
}
