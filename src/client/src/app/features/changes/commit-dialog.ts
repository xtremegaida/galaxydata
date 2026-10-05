import { DatePipe } from '@angular/common';
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { LiveAnnouncer } from '@angular/cdk/a11y';
import { MatButton } from '@angular/material/button';
import { MatCheckbox } from '@angular/material/checkbox';
import {
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatTab, MatTabGroup, MatTabLabel } from '@angular/material/tabs';
import { RouterLink } from '@angular/router';
import { type Problem, isSessionProblem, problemMessage } from '../../core/api/problem';
import { AuthStore } from '../../core/auth/auth-store';
import { issueText, rowLabelOf } from '../../core/changes/change-labels';
import {
  type ChangePreview,
  type CommitResult,
  type PendingChange,
  PendingChanges,
} from '../../core/changes/pending-changes';
import { CodeEditor, type EditorMarker } from '../../core/editor/code-editor';
import { sqlLanguageOf } from '../../core/editor/sql-languages';
import { Confirmer } from '../../core/ui/confirmer';
import { Message } from '../../core/ui/message';

type PreviewScript = ChangePreview['scripts'][number];

/** Something wrong with an edited script, as the server placed it. */
interface ScriptProblem {
  readonly message: string;
  readonly line: number;
  readonly start: number;
  readonly length: number;
}

/** Why an edited script can't run: its connection, what was said, and where in it. */
interface ScriptRefusal {
  readonly source: string;
  readonly title: string;
  readonly problems: readonly ScriptProblem[];
}

/** A script as the dialog shows it: as previewed, as edited, and what is wrong with it. */
interface ShownScript {
  readonly script: PreviewScript;
  readonly id: string;
  readonly text: string;
  readonly edited: boolean;
  readonly language: string;
  readonly summary: string;
  readonly refusal: ScriptRefusal | null;
  readonly markers: readonly EditorMarker[];
}

/** An issue as the dialog lists it: the change's entity and row (and column), and why. */
interface ShownIssue {
  readonly where: string;
  readonly message: string;
}

const statusTexts: Readonly<Record<CommitResult['scripts'][number]['status'], string>> = {
  committed: 'committed',
  rolledBack: 'rolled back',
  commitFailed: 'its commit failed',
};

let dialogs = 0;

/**
 * Commits the pending changes: previewed first, as the statements each connection would run (a tab each, in an
 * editor), with what can't be made as it is; scripts edited (but DuckDB's, by administrators alone), checked by the
 * server, which places what is wrong in them; committed; and what came of it on each connection. A preview gone
 * out of date is made again (edits kept where their scripts stay the same). It doesn't close while committing, nor
 * let edits go without asking.
 */
@Component({
  selector: 'gd-commit-dialog',
  imports: [
    CodeEditor,
    DatePipe,
    MatButton,
    MatCheckbox,
    MatDialogActions,
    MatDialogContent,
    MatDialogTitle,
    MatIcon,
    MatProgressBar,
    MatTab,
    MatTabGroup,
    MatTabLabel,
    Message,
    RouterLink,
  ],
  templateUrl: './commit-dialog.html',
  styleUrl: './commit-dialog.scss',
})
export class CommitDialog {
  private readonly changes = inject(PendingChanges);
  private readonly auth = inject(AuthStore);
  private readonly dialog = inject<MatDialogRef<CommitDialog, CommitResult | null>>(MatDialogRef);
  private readonly confirmer = inject(Confirmer);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly injector = inject(Injector);
  private readonly outcome = viewChild<ElementRef<HTMLElement>>('outcome');
  private readonly title = viewChild.required<ElementRef<HTMLElement>>('title');

  protected readonly id = `gd-commit-${dialogs++}`;
  protected readonly message = problemMessage;
  protected readonly admin = computed(() => this.auth.permissions().canAdmin);
  protected readonly preview = signal<ChangePreview | null>(null);
  protected readonly previewing = signal(false);
  protected readonly committing = signal(false);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly notice = signal<string | null>(null);
  /** The scripts' texts as edited, by connection. */
  protected readonly texts = signal<Readonly<Record<string, string>>>({});
  protected readonly refusal = signal<ScriptRefusal | null>(null);
  protected readonly allowAny = signal(false);
  protected readonly result = signal<CommitResult | null>(null);
  protected readonly selected = signal(0);

  protected readonly scripts = computed<ShownScript[]>(() =>
    (this.preview()?.scripts ?? []).map((script, index) => {
      const text = this.texts()[script.source] ?? script.text;
      const refusal = this.refusal()?.source === script.source ? this.refusal() : null;
      return {
        script,
        id: `${this.id}-script-${index}`,
        text,
        edited: normalized(text) !== normalized(script.text),
        language: sqlLanguageOf(script.dialect),
        summary: summaryOf(script),
        refusal,
        markers:
          refusal?.problems.map(({ start, length, message }) => ({ start, length, message })) ?? [],
      };
    }),
  );
  protected readonly edited = computed(() => this.scripts().some((script) => script.edited));
  protected readonly issues = computed<ShownIssue[]>(() => {
    const changes = this.changes.changes();
    return (this.preview()?.issues ?? []).map((issue) => ({
      where: whereOf(
        changes.find((change) => change.id === issue.change),
        changes,
        // The column, unless what is said of it names it.
        issue.column && issueText(issue) !== issue.message ? issue.column : null,
      ),
      message: issue.message,
    }));
  });
  protected readonly connections = computed(() =>
    listed((this.preview()?.scripts ?? []).map((script) => script.source)),
  );
  protected readonly canCommit = computed(
    () =>
      !!this.preview()?.planId &&
      !this.previewing() &&
      !this.committing() &&
      this.result() === null,
  );
  /** What the commit came to, in a sentence. */
  protected readonly headline = computed(() => {
    const result = this.result();
    if (!result) {
      return '';
    }
    const failure = result.failure ? ` ${sentence(result.failure.message)}` : '';
    switch (result.outcome) {
      case 'committed':
        return `Committed: the changes were written to ${listed(result.scripts.map((script) => script.source))}.`;
      case 'rolledBack':
        return `Nothing was written.${failure}`;
      default: {
        const written = result.scripts.filter((script) => script.status === 'committed');
        return `The changes were written in part: to ${listed(written.map((script) => script.source))}, not to the others.${failure}`;
      }
    }
  });
  protected readonly results = computed(() =>
    (this.result()?.scripts ?? []).map((script) => {
      const counted = script.statements
        .map((statement) => statement.rowsChanged)
        .filter((rows) => rows >= 0);
      const rows = counted.reduce((sum, each) => sum + each, 0);
      const statements = script.statements.length;
      return {
        source: script.source,
        status: script.status,
        text: [
          statusTexts[script.status],
          script.edited ? 'as edited' : null,
          `${statements} ${statements === 1 ? 'statement' : 'statements'}`,
          script.status === 'committed' && counted.length > 0
            ? `${rows} ${rows === 1 ? 'row' : 'rows'} changed`
            : null,
        ]
          .filter(Boolean)
          .join(', '),
        error: script.error,
      };
    }),
  );
  /** The change whose statement stopped the commit. */
  protected readonly failedChange = computed(() => {
    const result = this.result();
    const id = result?.failure?.change;
    const changes = result?.changes.changes ?? [];
    const change = changes.find((each) => each.id === id);
    return change ? whereOf(change, changes, null) : null;
  });
  protected readonly left = computed(() => {
    const count = this.result()?.changes.changes.length ?? 0;
    return count === 0
      ? 'No changes are left pending.'
      : count === 1
        ? '1 change is still pending.'
        : `${count} changes are still pending.`;
  });

  constructor() {
    this.dialog.disableClose = true;
    this.dialog.backdropClick().subscribe(() => void this.close());
    this.dialog.keydownEvents().subscribe((event) => {
      if (event.key === 'Escape' && !event.defaultPrevented) {
        event.preventDefault();
        void this.close();
      }
    });
    void this.refresh();
  }

  /**
   * Previews the changes again (as they are now): edits stay where their scripts are the same, and those let go
   * (their scripts changed, or gone) are said.
   */
  protected async refresh(notice: string | null = null): Promise<void> {
    if (this.previewing() || this.committing()) {
      return;
    }
    this.previewing.set(true);
    this.problem.set(null);
    this.notice.set(notice);
    this.refusal.set(null);
    const before = this.preview();
    const answered = await this.changes.preview();
    this.previewing.set(false);
    if ('problem' in answered) {
      if (!isSessionProblem(answered.problem)) {
        this.problem.set(answered.problem);
      }
      return;
    }
    const preview = answered.answer;
    const texts: Record<string, string> = {};
    const dropped: string[] = [];
    for (const [source, text] of Object.entries(this.texts())) {
      const was = before?.scripts.find((each) => each.source === source);
      if (!was || normalized(text) === normalized(was.text)) {
        continue;
      }
      if (preview.scripts.find((each) => each.source === source)?.text === was.text) {
        texts[source] = text;
      } else {
        dropped.push(source);
      }
    }
    this.texts.set(texts);
    this.preview.set(preview);
    if (dropped.length > 0) {
      this.notice.update(
        (said) =>
          `${said ? `${said} ` : ''}Your edits to the script for ${listed(dropped)} were let go: the changes to commit aren't what they were.`,
      );
    }
    this.announcer.announce(
      preview.issues.length > 0
        ? "Previewed: some changes can't be made as they are"
        : preview.scripts.length === 0
          ? 'Previewed: there are no changes to commit'
          : 'Previewed: the scripts are ready to commit',
    );
  }

  protected edit(script: PreviewScript, text: string): void {
    this.texts.update((texts) => ({ ...texts, [script.source]: text }));
    if (this.refusal()?.source === script.source) {
      // Where its problems were isn't where they are now.
      this.refusal.set(null);
    }
  }

  /** The script as previewed again (an edit that can be undone in the editor), which keeps the keyboard. */
  protected undo(script: PreviewScript, editor: CodeEditor): void {
    this.edit(script, script.text);
    editor.focus();
  }

  protected async commit(): Promise<void> {
    const preview = this.preview();
    if (!preview?.planId || !this.canCommit()) {
      return;
    }
    this.committing.set(true);
    this.problem.set(null);
    this.notice.set(null);
    this.refusal.set(null);
    const scripts = this.scripts()
      .filter((script) => script.edited && script.script.editable)
      .map((script) => ({ source: script.script.source, text: script.text }));
    const answered = await this.changes.commit({
      planId: preview.planId,
      version: preview.version,
      scripts: scripts.length > 0 ? scripts : null,
      allowAnyStatement: this.admin() && this.allowAny() && scripts.length > 0,
    });
    this.committing.set(false);
    if ('answer' in answered) {
      this.result.set(answered.answer);
      this.announcer.announce(this.headline());
      afterNextRender(() => this.outcome()?.nativeElement.focus(), { injector: this.injector });
      return;
    }
    const problem = answered.problem;
    if (isSessionProblem(problem)) {
      return;
    }
    if (problem.code === 'plan-stale') {
      const why = (problem.detail ?? '').replace(/:\s*preview the changes again\.?$/i, '');
      await this.refresh(
        `The preview couldn't be committed${why ? ` (${why.charAt(0).toLowerCase()}${why.slice(1)})` : ''}, so the changes were previewed again: check them, and commit.`,
      );
      return;
    }
    const refusal = refusalOf(problem);
    if (refusal) {
      this.refusal.set(refusal);
      const at = preview.scripts.findIndex((script) => script.source === refusal.source);
      if (at >= 0) {
        this.selected.set(at);
      }
      return;
    }
    this.problem.set(problem);
  }

  /**
   * The preview again, after a commit that left changes pending (those it didn't write, or made since the
   * preview), with the edits made; the keyboard goes to the dialog's title, as the button goes.
   */
  protected back(): void {
    this.result.set(null);
    void this.refresh();
    afterNextRender(() => this.title().nativeElement.focus(), { injector: this.injector });
  }

  /** Closes the dialog: not while committing; asking first when scripts were edited (and not committed). */
  protected async close(): Promise<void> {
    if (this.committing()) {
      return;
    }
    const result = this.result();
    if (result?.outcome !== 'committed' && this.edited()) {
      const discard = await this.confirmer.confirm({
        title: 'Let your edits go?',
        message: "The scripts you edited haven't been committed: closing lets your edits go.",
        confirm: 'Let them go',
        cancel: 'Keep editing',
        destructive: true,
      });
      if (!discard) {
        return;
      }
    }
    this.dialog.close(result);
  }

  /** Previews the changes again after a failure. */
  protected retry(): void {
    void this.refresh();
  }

  protected capital(text: string): string {
    return text.charAt(0).toUpperCase() + text.slice(1);
  }

  /** A link followed out of the dialog (to the audit): it closes. */
  protected dialogClosed(): void {
    this.dialog.close(this.result());
  }
}

/** A script's text as the server compares edits: whatever its line breaks, and the white space after it. */
function normalized(text: string): string {
  return text.replace(/\r\n/g, '\n').trimEnd();
}

/** What a script's statements do: "2 statements: 1 insert, 1 update". */
function summaryOf(script: PreviewScript): string {
  const count = script.statements.length;
  const kinds = new Map<string, number>();
  for (const statement of script.statements) {
    kinds.set(statement.kind, (kinds.get(statement.kind) ?? 0) + 1);
  }
  const parts = [...kinds].map(([kind, n]) => `${n} ${n === 1 ? kind : `${kind}s`}`);
  return `${count} ${count === 1 ? 'statement' : 'statements'}${parts.length > 0 ? `: ${parts.join(', ')}` : ''}`;
}

/** Where a change is: its entity, its row, and the column at fault. */
function whereOf(
  change: PendingChange | undefined,
  changes: readonly PendingChange[],
  column: string | null,
): string {
  const label = change ? rowLabelOf(change, changes) : '';
  const row = change
    ? `${change.entity}, ${label.charAt(0).toLowerCase()}${label.slice(1)}`
    : 'A change';
  return column ? `${row}, ${column}` : row;
}

/** Why an edited script was refused (422 `script-invalid`), as the server placed it. */
function refusalOf(problem: Problem): ScriptRefusal | null {
  const body = problem.body;
  if (problem.code !== 'script-invalid' || typeof body?.['source'] !== 'string') {
    return null;
  }
  const problems = Array.isArray(body['problems']) ? (body['problems'] as ScriptProblem[]) : [];
  return { source: body['source'], title: problem.title, problems };
}

/** Names in a list: "a", "a and b", "a, b and c". */
function listed(names: readonly string[]): string {
  return names.length <= 1
    ? (names[0] ?? '')
    : `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`;
}

function sentence(text: string): string {
  return /[.!?]$/.test(text) ? text : `${text}.`;
}
