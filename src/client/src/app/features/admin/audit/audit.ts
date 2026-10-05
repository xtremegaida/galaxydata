import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatTabLink, MatTabNav, MatTabNavPanel } from '@angular/material/tabs';
import { RouterLink, RouterLinkActive } from '@angular/router';
import type { Observable } from 'rxjs';
import type { Schema } from '../../../core/api/api-client';
import { type Problem, isSessionProblem, problemOf } from '../../../core/api/problem';

export type CommitSummary = Schema<'CommitAuditSummaryDto'>;
export type CommitAudit = Schema<'CommitAuditDto'>;
export type AdminEvent = Schema<'AdminEventDto'>;
type CommitStatus = CommitSummary['status'];
type ScriptStatus = CommitAudit['scripts'][number]['status'];

/** How many entries a page of the audit has. */
export const auditPageSize = 50;

export const commitStatusLabels: Readonly<Record<CommitStatus, string>> = {
  inProgress: 'In progress',
  committed: 'Committed',
  rolledBack: 'Rolled back',
  partiallyCommitted: 'Written in part',
  unknown: 'Unknown',
};

export const scriptStatusLabels: Readonly<Record<ScriptStatus, string>> = {
  pending: 'Not run',
  committed: 'Committed',
  rolledBack: 'Rolled back',
  commitFailed: 'Its commit failed',
  unknown: 'Unknown',
};

/** Whether a commit's status is one to look at (it didn't commit, or isn't known to have). */
export function warns(status: CommitStatus | ScriptStatus): boolean {
  return status !== 'committed' && status !== 'inProgress';
}

/**
 * Entries of the audit, newest first, a page at a time ("Show more" asks for those before the last shown); the
 * keyboard goes to the first of a page shown after the first, as the button may go.
 */
export class AuditPages<T extends { readonly id: number }> {
  readonly rows = signal<readonly T[]>([]);
  readonly more = signal(false);
  readonly loading = signal(false);
  readonly problem = signal<Problem | null>(null);

  constructor(
    private readonly fetch: (before: number | undefined, take: number) => Observable<T[]>,
    private readonly host: ElementRef<HTMLElement>,
    private readonly injector: Injector,
    private readonly destroyed: DestroyRef,
  ) {
    this.load();
  }

  /** The next page (or the first again, after it failed). */
  load(): void {
    if (this.loading()) {
      return;
    }
    const before = this.rows().at(-1)?.id;
    this.loading.set(true);
    this.problem.set(null);
    // One more than shown, to know whether there are more.
    this.fetch(before, auditPageSize + 1)
      .pipe(takeUntilDestroyed(this.destroyed))
      .subscribe({
        next: (page) => {
          const shown = page.slice(0, auditPageSize);
          this.rows.update((rows) => [...rows, ...shown]);
          this.more.set(page.length > auditPageSize);
          this.loading.set(false);
          if (before !== undefined && shown.length > 0) {
            afterNextRender(
              () =>
                this.host.nativeElement
                  .querySelector<HTMLElement>(`[data-entry="${shown[0].id}"]`)
                  ?.focus(),
              { injector: this.injector },
            );
          }
        },
        error: (error: unknown) => {
          const problem = problemOf(error);
          this.loading.set(false);
          if (!isSessionProblem(problem)) {
            this.problem.set(problem);
          }
        },
      });
  }
}

/** The audit's pages, as tabs: its commits, and what administrators did. */
@Component({
  selector: 'gd-audit-nav',
  imports: [MatTabLink, MatTabNav, MatTabNavPanel, RouterLink, RouterLinkActive],
  template: `
    <nav mat-tab-nav-bar mat-stretch-tabs="false" [tabPanel]="panel" aria-label="The audit">
      <a
        mat-tab-link
        routerLink="/admin/audit/commits"
        routerLinkActive
        #commits="routerLinkActive"
        [active]="commits.isActive"
        >Commits of changes</a
      >
      <a
        mat-tab-link
        routerLink="/admin/audit/events"
        routerLinkActive
        #events="routerLinkActive"
        [active]="events.isActive"
        >What administrators did</a
      >
    </nav>
    <mat-tab-nav-panel #panel><ng-content /></mat-tab-nav-panel>
  `,
  styles: `
    nav {
      margin-bottom: 16px;
    }
  `,
})
export class AuditNav {}

/** What an administrator's action changed, a line each: `role: dataManager → admin`. */
export function detailLines(details: string | null): { name: string; value: string }[] {
  if (!details) {
    return [];
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(details);
  } catch {
    return [{ name: '', value: details }];
  }
  const lines: { name: string; value: string }[] = [];
  const add = (name: string, value: unknown) => {
    if (isRecord(value) && 'from' in value && 'to' in value && Object.keys(value).length === 2) {
      lines.push({ name, value: `${textOf(value['from'])} → ${textOf(value['to'])}` });
    } else if (isRecord(value)) {
      for (const [key, each] of Object.entries(value)) {
        add(name ? `${name}.${key}` : key, each);
      }
    } else {
      lines.push({ name, value: textOf(value) });
    }
  };
  add('', parsed);
  return lines;
}

function textOf(value: unknown): string {
  if (value === null || value === undefined) {
    return 'none';
  }
  if (Array.isArray(value)) {
    return value.length === 0 ? 'none' : value.map(textOf).join(', ');
  }
  return typeof value === 'string' ? value : JSON.stringify(value);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** For pages to make their entries' pages. */
export function auditPagesOf<T extends { readonly id: number }>(
  fetch: (before: number | undefined, take: number) => Observable<T[]>,
): AuditPages<T> {
  return new AuditPages(fetch, inject(ElementRef), inject(Injector), inject(DestroyRef));
}
