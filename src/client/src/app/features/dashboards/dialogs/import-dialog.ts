import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  linkedSignal,
  signal,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { MatOption, MatSelect } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { ApiClient, type Schema } from '../../../core/api/api-client';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { Message } from '../../../core/ui/message';

export type DashboardExport = Schema<'DashboardExport'>;

/** What a dashboard's file says it is, and the version of files read here. */
export const exportFormat = 'galaxydata.dashboard';
export const exportVersion = 1;

/** The largest file read: a definition's most, with its palettes, and room to spare. */
export const maxExportBytes = 1024 * 1024;

/** What the import dialog gets: the file read, and a name for the dashboard none of the user's has. */
export interface ImportDialogData {
  readonly file: DashboardExport;
  readonly name: string;
}

/**
 * The file as a dashboard's, or why it isn't one: JSON of the format and version read here, with a definition. The
 * server checks the rest.
 */
export function readExport(text: string): DashboardExport | string {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return "It isn't JSON.";
  }
  const file = value as Partial<DashboardExport> | null;
  if (typeof file !== 'object' || file === null || file.format !== exportFormat) {
    return "It isn't a dashboard's file.";
  }
  if (file.version !== exportVersion) {
    return `It is of version ${String(file.version)}; files here are of version ${exportVersion}.`;
  }
  if (typeof file.definition !== 'object' || file.definition === null) {
    return 'It has no definition.';
  }
  return file as DashboardExport;
}

/** The connections' aliases a definition's sources name, each once, in order: the first part of each entity's path. */
export function aliasesOf(file: DashboardExport): string[] {
  const aliases: string[] = [];
  for (const source of file.definition.sources ?? []) {
    const alias = /^[A-Za-z_][A-Za-z0-9_]*/.exec(source.entity ?? '')?.[0];
    if (alias && !aliases.some((a) => a.toLowerCase() === alias.toLowerCase())) {
      aliases.push(alias);
    }
  }
  return aliases;
}

/**
 * A dashboard's file brought in as a new dashboard of the user's: under a name, its sources' connections taken to
 * this instance's (each alias of the file's to one here, the same one when there is one), and its palettes found
 * here when the same, or added as the user's. It comes in unpublished and private.
 */
@Component({
  selector: 'gd-import-dialog',
  imports: [
    MatButton,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
    MatFormField,
    MatInput,
    MatLabel,
    MatOption,
    MatSelect,
    Message,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Import {{ data.file.name }}</h2>
    <mat-dialog-content>
      <div class="fields">
        <mat-form-field class="name" subscriptSizing="dynamic">
          <mat-label>Name</mat-label>
          <input #name matInput required [value]="text()" (input)="text.set(name.value)" />
        </mat-form-field>
        @if (data.file.description) {
          <p class="aside">{{ data.file.description }}</p>
        }
        @if (aliases.length > 0) {
          <h3>Its data</h3>
          <p class="aside">Each connection its sources name, and the one here to read instead.</p>
          @for (alias of aliases; track alias) {
            <mat-form-field class="name" subscriptSizing="dynamic">
              <mat-label>{{ alias }}</mat-label>
              <mat-select [value]="mapping()[alias]" (valueChange)="choose(alias, $event)">
                @if (!here(alias)) {
                  <mat-option [value]="alias">{{ alias }} (not here)</mat-option>
                }
                @for (source of sources(); track source.alias) {
                  <mat-option [value]="source.alias">
                    {{ source.alias }}
                    @if (source.kindName) {
                      <span class="aside">· {{ source.kindName }}</span>
                    }
                  </mat-option>
                }
              </mat-select>
            </mat-form-field>
          }
        }
        @if (data.file.palettes.length > 0) {
          <h3>Its palettes</h3>
          <p class="aside">
            {{ paletteNames() }}: the same palettes here are used, and others are added as yours.
          </p>
        }
      </div>
      <div role="alert">
        @if (problems().length > 0) {
          <gd-message kind="problem">
            Couldn't import it:
            <ul class="problems">
              @for (problem of problems(); track $index) {
                <li>{{ problem }}</li>
              }
            </ul>
          </gd-message>
        }
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close type="button">Cancel</button>
      <button
        matButton="filled"
        type="button"
        [disabled]="busy() || !text().trim()"
        (click)="import()"
      >
        Import
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .fields {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .name {
      width: min(420px, 80vw);
    }
    h3 {
      font: var(--mat-sys-title-small);
      margin: 12px 0 0;
    }
    .aside {
      margin: 0;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
    .problems {
      margin: 4px 0 0;
      padding-left: 20px;
    }
  `,
})
export class ImportDialog {
  protected readonly data = inject<ImportDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ImportDialog>);
  private readonly api = inject(ApiClient);

  protected readonly text = signal(this.data.name);
  protected readonly busy = signal(false);
  protected readonly problems = signal<string[]>([]);
  protected readonly aliases = aliasesOf(this.data.file);

  private readonly catalog = rxResource({ stream: () => this.api.get('/api/catalog') });
  protected readonly sources = computed(() =>
    this.catalog.hasValue() ? this.catalog.value().sources : [],
  );

  /** The connection here each alias of the file's is read from: the one of its name, until another is chosen. */
  protected readonly mapping = linkedSignal<Record<string, string>>(() =>
    Object.fromEntries(this.aliases.map((alias) => [alias, this.here(alias) ?? alias])),
  );

  protected readonly paletteNames = computed(() =>
    this.data.file.palettes.map((p) => p.name).join(', '),
  );

  /** The alias here that is the file's (aliases ignore case), if there is one. */
  protected here(alias: string): string | undefined {
    return this.sources().find((s) => s.alias.toLowerCase() === alias.toLowerCase())?.alias;
  }

  protected choose(alias: string, to: string): void {
    this.mapping.update((mapping) => ({ ...mapping, [alias]: to }));
  }

  protected async import(): Promise<void> {
    this.busy.set(true);
    this.problems.set([]);
    const mapping = this.mapping();
    const aliases = Object.fromEntries(
      Object.entries(mapping).filter(([from, to]) => from.toLowerCase() !== to.toLowerCase()),
    );
    try {
      const dashboard = await firstValueFrom(
        this.api.post('/api/dashboards/import', {
          body: {
            dashboard: this.data.file,
            name: this.text().trim(),
            aliases: Object.keys(aliases).length > 0 ? aliases : null,
          },
        }),
      );
      this.ref.close(dashboard);
    } catch (error) {
      const problem = problemOf(error);
      if (problem.code === 'dashboard-name-taken') {
        this.problems.set(['You have a dashboard of this name already.']);
      } else if (problem.errors) {
        this.problems.set(
          Object.entries(problem.errors)
            .slice(0, 8)
            .map(([field, messages]) => `${field}: ${messages.join(' ')}`),
        );
      } else {
        this.problems.set([problemMessage(problem)]);
      }
    } finally {
      this.busy.set(false);
    }
  }
}
