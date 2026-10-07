import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import {
  MatAutocomplete,
  MatAutocompleteTrigger,
  type MatAutocompleteSelectedEvent,
} from '@angular/material/autocomplete';
import { MatButton } from '@angular/material/button';
import { MatChipGrid, MatChipInput, MatChipRemove, MatChipRow } from '@angular/material/chips';
import { MatOption } from '@angular/material/core';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatRadioButton, MatRadioGroup } from '@angular/material/radio';
import { MatSlideToggle, type MatSlideToggleChange } from '@angular/material/slide-toggle';
import { type Observable, firstValueFrom, switchMap, timer } from 'rxjs';
import { ApiClient, type Schema } from '../../../core/api/api-client';
import { problemOf } from '../../../core/api/problem';
import { Confirmer } from '../../../core/ui/confirmer';
import { Message } from '../../../core/ui/message';
import { DASHBOARD_WAITS } from '../state/waits';
import { type DashboardDialogData, type DashboardDto, refusalOf } from './publish-dialogs';

type Person = Schema<'PersonDto'>;
type Who = 'private' | 'chosen' | 'everyone';

/** A person as the dialog names them: their display name and user name. */
export function personName(person: Person): string {
  return person.displayName ? `${person.displayName} (${person.userName})` : person.userName;
}

/** The snippet that frames a public dashboard in another site's page, as tall as it says it is. */
export function iframeSnippet(link: string, title: string): string {
  const quoted = title.replaceAll('&', '&amp;').replaceAll('"', '&quot;');
  return [
    `<iframe src="${link}" title="${quoted}" width="100%" height="600" style="border: 0"`,
    `  loading="lazy" referrerpolicy="no-referrer"></iframe>`,
    `<script>`,
    `  // GalaxyData's dashboard says its height; the frame follows it.`,
    `  addEventListener('message', (event) => {`,
    `    const frame = [...document.querySelectorAll('iframe')].find((f) => f.contentWindow === event.source);`,
    `    if (frame && event.origin === new URL(frame.src).origin && event.data?.type === 'galaxydata.dashboard.size') {`,
    `      frame.style.height = event.data.height + 'px';`,
    `    }`,
    `  });`,
    `</script>`,
  ].join('\n');
}

/**
 * Who sees the dashboard (its published copy): only its owner, people chosen (found by name), or everyone signed
 * in; and, for those who may, its public link (for anyone, without signing in), the snippet that embeds it, the
 * sites that may frame it, a new link (the old one stops), or none. Each change is saved as it is made.
 */
@Component({
  selector: 'gd-share-dialog',
  imports: [
    MatAutocomplete,
    MatAutocompleteTrigger,
    MatButton,
    MatChipGrid,
    MatChipInput,
    MatChipRemove,
    MatChipRow,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
    MatFormField,
    MatHint,
    MatIcon,
    MatInput,
    MatLabel,
    MatOption,
    MatProgressBar,
    MatRadioButton,
    MatRadioGroup,
    MatSlideToggle,
    Message,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Share {{ dashboard().name }}</h2>
    <mat-dialog-content class="content">
      @if (busy()) {
        <mat-progress-bar mode="indeterminate" aria-label="Saving" />
      }
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">{{ problem }}</gd-message>
        }
      </div>
      <h3>Who sees it</h3>
      @if (!dashboard().published) {
        <p class="aside">Not published yet: no one sees it but you till it is.</p>
      }
      <mat-radio-group
        class="who"
        aria-label="Who sees it"
        [value]="who()"
        (change)="who.set($event.value)"
      >
        <mat-radio-button value="private">Only you</mat-radio-button>
        <mat-radio-button value="chosen">People you choose</mat-radio-button>
        <mat-radio-button value="everyone">Everyone signed in</mat-radio-button>
      </mat-radio-group>
      @if (who() === 'chosen') {
        <mat-form-field class="people" subscriptSizing="dynamic">
          <mat-label>People</mat-label>
          <mat-chip-grid #grid aria-label="People who see it">
            @for (person of people(); track person.id) {
              <mat-chip-row (removed)="unchoose(person.id)">
                {{ personName(person) }}
                <button
                  matChipRemove
                  type="button"
                  [attr.aria-label]="'Remove ' + personName(person)"
                >
                  <mat-icon>cancel</mat-icon>
                </button>
              </mat-chip-row>
            }
            <input
              #find
              [matChipInputFor]="grid"
              [matAutocomplete]="found"
              [value]="search()"
              (input)="search.set(find.value)"
              placeholder="Find by name"
              autocomplete="off"
            />
          </mat-chip-grid>
          <mat-autocomplete #found="matAutocomplete" (optionSelected)="choose($event, find)">
            @for (person of suggested(); track person.id) {
              <mat-option [value]="person">{{ personName(person) }}</mat-option>
            }
          </mat-autocomplete>
        </mat-form-field>
      }
      <button
        matButton="tonal"
        type="button"
        [disabled]="busy() || !sharingChanged()"
        (click)="saveSharing()"
      >
        Save who sees it
      </button>

      @if (publicOffered()) {
        <h3>Public link</h3>
        <p class="aside">
          Anyone with the link sees the published copy, without signing in, in this site or framed
          in another.
        </p>
        <mat-slide-toggle
          [checked]="!!dashboard().public"
          [disabled]="busy()"
          (change)="publicToggled($event)"
        >
          A public link
        </mat-slide-toggle>
        @if (dashboard().public; as shown) {
          @if (!shown.works) {
            <gd-message kind="warning">
              The link doesn't show the dashboard now: it isn't published, or whoever made it public
              may no longer.
            </gd-message>
          }
          <mat-form-field class="wide" subscriptSizing="dynamic">
            <mat-label>Link</mat-label>
            <input matInput readonly [value]="link()" />
          </mat-form-field>
          <div class="actions">
            <button matButton type="button" (click)="copy(link(), 'The link is copied')">
              <mat-icon>content_copy</mat-icon>
              Copy the link
            </button>
            <button matButton type="button" (click)="copy(snippet(), 'The snippet is copied')">
              <mat-icon>code</mat-icon>
              Copy the snippet to embed it
            </button>
            <button matButton type="button" [disabled]="busy()" (click)="regenerate()">
              <mat-icon>autorenew</mat-icon>
              A new link
            </button>
          </div>
          <mat-form-field class="wide" subscriptSizing="dynamic">
            <mat-label>The snippet</mat-label>
            <textarea matInput readonly rows="4" class="code" [value]="snippet()"></textarea>
          </mat-form-field>
          <mat-form-field class="wide" subscriptSizing="dynamic">
            <mat-label>Sites that may frame it</mat-label>
            <textarea
              #origins
              matInput
              rows="3"
              class="code"
              [value]="originsText()"
              (input)="originsText.set(origins.value)"
              placeholder="https://intranet.example.com"
            ></textarea>
            @if (originsProblem(); as problem) {
              <mat-hint class="problem" role="alert">{{ problem }}</mat-hint>
            } @else {
              <mat-hint>One a line; none: those the server allows</mat-hint>
            }
          </mat-form-field>
          <button
            matButton="tonal"
            type="button"
            [disabled]="busy() || !originsChanged()"
            (click)="saveOrigins()"
          >
            Save the sites
          </button>
        }
      }
      <p class="status" role="status">{{ said() }}</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton="filled" mat-dialog-close type="button">Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .content {
      display: grid;
      gap: 8px;
      min-width: min(560px, 85vw);
    }
    h3 {
      margin: 8px 0 0;
      font: var(--mat-sys-title-small);
    }
    .who {
      display: grid;
    }
    .people,
    .wide {
      width: 100%;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
    }
    .code {
      font-family: var(--gd-code-font-family);
      font-size: 12px;
    }
    .problem {
      color: var(--mat-sys-error);
    }
    .aside,
    .status {
      margin: 0;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class ShareDialog {
  private readonly data = inject<DashboardDialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(ApiClient);
  private readonly confirmer = inject(Confirmer);
  private readonly waits = inject(DASHBOARD_WAITS);
  private readonly document = inject(DOCUMENT);
  protected readonly personName = personName;

  protected readonly dashboard = signal(this.data.dashboard);
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  protected readonly originsProblem = signal<string | null>(null);
  protected readonly said = signal('');

  private readonly sharedWith = computed(() => this.dashboard().sharing);
  protected readonly who = signal<Who>(this.whoOf(this.data.dashboard));
  protected readonly people = signal<readonly Person[]>(this.data.dashboard.sharing?.users ?? []);
  protected readonly search = signal('');
  protected readonly originsText = signal((this.data.dashboard.public?.origins ?? []).join('\n'));

  /** Public links are for those who may make them, and for those who may revoke one there is. */
  protected readonly publicOffered = computed(
    () =>
      this.dashboard().can.makePublic ||
      (!!this.dashboard().public && this.dashboard().can.revokePublic),
  );

  protected readonly link = computed(() => {
    const token = this.dashboard().public?.token;
    return token ? new URL(`/embed/${token}`, this.document.location.href).href : '';
  });
  protected readonly snippet = computed(() => iframeSnippet(this.link(), this.dashboard().name));

  private readonly found = rxResource({
    params: () =>
      this.who() === 'chosen' && this.search().trim() ? this.search().trim() : undefined,
    stream: ({ params: text }) =>
      timer(this.waits.filterTyping).pipe(
        switchMap(() => this.api.get('/api/dashboards/people', { query: { text, take: 20 } })),
      ),
  });
  protected readonly suggested = computed(() => {
    const chosen = new Set(this.people().map((p) => p.id));
    return (this.found.hasValue() ? (this.found.value() ?? []) : []).filter(
      (p) => !chosen.has(p.id),
    );
  });

  protected readonly sharingChanged = computed(() => {
    const shared = this.sharedWith();
    const before = this.whoOf(this.dashboard());
    const ids = (list: readonly Person[]) =>
      list
        .map((p) => p.id)
        .sort()
        .join(',');
    return (
      this.who() !== before ||
      (this.who() === 'chosen' && ids(this.people()) !== ids(shared?.users ?? []))
    );
  });

  protected readonly originsChanged = computed(
    () => this.origins().join('\n') !== (this.dashboard().public?.origins ?? []).join('\n'),
  );

  protected choose(event: MatAutocompleteSelectedEvent, field: HTMLInputElement): void {
    const person = event.option.value as Person;
    field.value = '';
    this.search.set('');
    this.people.set([...this.people(), person]);
  }

  protected unchoose(id: number): void {
    this.people.set(this.people().filter((p) => p.id !== id));
  }

  protected async saveSharing(): Promise<void> {
    const who = this.who();
    if (who === 'chosen' && this.people().length === 0) {
      this.problem.set('Choose the people who see it, or that only you do.');
      return;
    }
    await this.run('save who sees it', 'Saved: who sees it', () =>
      this.api.put('/api/dashboards/{id}/sharing', {
        path: { id: this.dashboard().id },
        body: {
          everyone: who === 'everyone',
          users: who === 'chosen' ? this.people().map((p) => p.id) : [],
          version: this.dashboard().version,
        },
      }),
    );
  }

  protected async publicToggled(change: MatSlideToggleChange): Promise<void> {
    const on = change.checked;
    // The toggle shows what is till the server says otherwise.
    change.source.checked = !on;
    if (!on) {
      const sure = await this.confirmer.confirm({
        title: 'Stop the public link?',
        message: 'It stops at once, in this site and wherever it is framed.',
        confirm: 'Stop it',
        destructive: true,
      });
      if (!sure) {
        return;
      }
    }
    await this.run(
      on ? 'make it public' : 'stop its link',
      on ? 'It has a public link' : 'Its public link stopped',
      () =>
        this.api.put('/api/dashboards/{id}/public', {
          path: { id: this.dashboard().id },
          body: {
            enabled: on,
            origins: on ? this.origins() : null,
            version: this.dashboard().version,
          },
        }),
    );
  }

  protected async saveOrigins(): Promise<void> {
    await this.run('save the sites', 'Saved: the sites that may frame it', () =>
      this.api.put('/api/dashboards/{id}/public', {
        path: { id: this.dashboard().id },
        body: { enabled: true, origins: this.origins(), version: this.dashboard().version },
      }),
    );
  }

  protected async regenerate(): Promise<void> {
    const sure = await this.confirmer.confirm({
      title: 'A new public link?',
      message:
        'The link there is stops at once; wherever it is framed shows nothing till given the new one.',
      confirm: 'A new link',
      destructive: true,
    });
    if (sure) {
      await this.run('make a new link', 'It has a new link', () =>
        this.api.post('/api/dashboards/{id}/public/regenerate', {
          path: { id: this.dashboard().id },
          body: { version: this.dashboard().version },
        }),
      );
    }
  }

  protected async copy(text: string, said: string): Promise<void> {
    try {
      await this.document.defaultView?.navigator.clipboard.writeText(text);
      this.said.set(said);
    } catch {
      this.said.set('Copy it from the field: the clipboard refused it.');
    }
  }

  private origins(): string[] {
    return this.originsText()
      .split('\n')
      .map((o) => o.trim())
      .filter(Boolean);
  }

  private whoOf(dashboard: DashboardDto): Who {
    const sharing = dashboard.sharing;
    return sharing?.everyone
      ? 'everyone'
      : sharing && sharing.users.length > 0
        ? 'chosen'
        : 'private';
  }

  private async run(
    doing: string,
    done: string,
    request: () => Observable<DashboardDto>,
  ): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    this.originsProblem.set(null);
    this.said.set('');
    try {
      const dashboard = await firstValueFrom(request());
      this.dashboard.set(dashboard);
      this.people.set(dashboard.sharing?.users ?? []);
      this.originsText.set((dashboard.public?.origins ?? []).join('\n'));
      this.data.updated(dashboard);
      this.said.set(done);
    } catch (error) {
      const problem = problemOf(error);
      // A site refused is said under the sites.
      const origin = Object.entries(problem.errors ?? {}).find(([field]) =>
        field.startsWith('origins'),
      );
      if (origin) {
        const index = /^origins\[(\d+)\]/.exec(origin[0]);
        const site = index ? this.origins()[Number(index[1])] : null;
        this.originsProblem.set(site ? `${site}: ${origin[1][0]}` : origin[1][0]);
      } else if (problem.code === 'public-dashboards-disabled') {
        this.problem.set(
          'Public dashboards are turned off on this server (Dashboards:AllowPublic).',
        );
      } else {
        this.problem.set(refusalOf(problem, doing));
      }
    } finally {
      this.busy.set(false);
    }
  }
}
