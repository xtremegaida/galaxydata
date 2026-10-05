import { LiveAnnouncer } from '@angular/cdk/a11y';
import { DOCUMENT, Location, LocationStrategy, NgTemplateOutlet } from '@angular/common';
import {
  Component,
  DestroyRef,
  ElementRef,
  InjectionToken,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  linkedSignal,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource, takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatCheckbox } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatIcon } from '@angular/material/icon';
import { MatMenu, MatMenuItem, MatMenuTrigger } from '@angular/material/menu';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatTab, MatTabGroup, MatTabLabel } from '@angular/material/tabs';
import { MatTooltip } from '@angular/material/tooltip';
import {
  ActivatedRoute,
  NavigationStart,
  Router,
  type UrlMatchResult,
  type UrlSegment,
} from '@angular/router';
import { filter, firstValueFrom, map, tap } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import {
  type Problem,
  ProblemCode,
  isSessionProblem,
  problemMessage,
  problemOf,
} from '../../core/api/problem';
import { CodeEditor, type EditorMarker } from '../../core/editor/code-editor';
import { gdqLanguageId } from '../../core/editor/gdq-language';
import { PageTitle } from '../../core/page-titles';
import { LastQuery, newQueryState, opensNewQuery } from '../../core/query/last-query';
import {
  type ParameterValues,
  type QueryAddress,
  queryUrlTree,
  readQueryAddress,
  sameValues,
} from '../../core/query/query-url';
import { Confirmer } from '../../core/ui/confirmer';
import { debounced } from '../../core/ui/debounced';
import { Message } from '../../core/ui/message';
import type { QueryParameterInput } from './query-datasource';
import { type Checked, QueryMessages } from './query-messages';
import {
  QueryParameters,
  given,
  parameterInputOf,
  parameterProblemOf,
  valueText,
} from './query-parameters';
import { type QueryExplain, QueryPlan } from './query-plan';
import { type FollowedLink, type QueryRun, QueryResults, type RunOutcome } from './query-results';
import { QuerySql } from './query-sql';
import type { OpenQueryData } from './open-query-dialog';
import type { QueryDetails, SaveOutcome, SaveQueryData, SavedQuery } from './save-query-dialog';

/** The editor's addresses: `/query`, and `/query/<id>` for a saved query; one route, so the page stays as they change. */
export function queryMatcher(segments: UrlSegment[]): UrlMatchResult | null {
  if (segments.length === 0) {
    return { consumed: [] };
  }
  return segments.length === 1 && /^\d{1,9}$/.test(segments[0].path)
    ? { consumed: segments, posParams: { id: segments[0] } }
    : null;
}

/** A run shown, with the values it was run with as the editor holds them. */
interface ShownRun extends QueryRun {
  readonly values: ParameterValues;
}

/** An explanation, of the text and values it was asked with. */
interface Explained {
  readonly text: string;
  readonly values: ParameterValues;
  readonly explain: QueryExplain;
}

/** The tabs under the editor, in order. */
const tabs = ['results', 'plan', 'sql', 'messages'] as const;
type Tab = (typeof tabs)[number];

/** How long typing pauses before the query is checked (`check`), and its address written (`write`), in milliseconds. */
export const QUERY_WAITS = new InjectionToken<{ readonly check: number; readonly write: number }>(
  'QUERY_WAITS',
  { factory: () => ({ check: 300, write: 400 }) },
);

let runs = 0;

/**
 * Writing and running queries in the query language. The text is checked as typing pauses (what is wrong marked in
 * it), and the parameters it uses are asked for; Run (Ctrl+Enter) shows its rows, Explain how it would run (its plan
 * and the SQL each source runs), and Messages what checking and running it found. Queries are saved (the user's, or
 * shared with everyone) and opened again.
 *
 * The address says what the editor holds (see `queryUrlTree`): the saved query, its text and values when they aren't
 * the saved ones, and whether its rows are shown. So reloading the page, going back and forward, and links from the
 * rows to a group's rows keep each query, and the address of a query is a link to it.
 */
@Component({
  selector: 'gd-query-page',
  imports: [
    CodeEditor,
    MatButton,
    MatCheckbox,
    MatIcon,
    MatIconButton,
    MatMenu,
    MatMenuItem,
    MatMenuTrigger,
    MatProgressBar,
    MatTab,
    MatTabGroup,
    MatTabLabel,
    MatTooltip,
    Message,
    NgTemplateOutlet,
    QueryMessages,
    QueryParameters,
    QueryPlan,
    QueryResults,
    QuerySql,
  ],
  templateUrl: './query-page.html',
  styleUrl: './query-page.scss',
  host: { '(keydown)': 'keyDown($event)' },
})
export class QueryPage {
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly dialog = inject(MatDialog);
  private readonly confirmer = inject(Confirmer);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly pageTitle = inject(PageTitle);
  private readonly last = inject(LastQuery);
  private readonly document = inject(DOCUMENT);
  private readonly locationStrategy = inject(LocationStrategy);
  private readonly location = inject(Location);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly waits = inject(QUERY_WAITS);
  private readonly injector = inject(Injector);
  private readonly editor = viewChild.required<CodeEditor>('editor');
  private readonly runButton = viewChild.required('runButton', { read: ElementRef<HTMLElement> });

  protected readonly languageId = gdqLanguageId;
  protected readonly message = problemMessage;

  // What the address says.
  private readonly routeId = toSignal(this.route.paramMap.pipe(map((params) => params.get('id'))), {
    initialValue: this.route.snapshot.paramMap.get('id'),
  });
  private readonly routeFragment = toSignal(this.route.fragment, {
    initialValue: this.route.snapshot.fragment,
  });
  private readonly addressRead = computed(() =>
    readQueryAddress(this.routeId(), this.routeFragment()),
  );
  /** The address the page wrote last (serialized): the address's changes that aren't the page's are the user's. */
  private written: string | null = null;
  private writeTimer: ReturnType<typeof setTimeout> | null = null;
  /** An address to show once its saved query is read. */
  private pending: QueryAddress | null = null;
  /** Set while the query left in the editor is shown again: the page writes no address of its own till it is. */
  private restoring = false;
  /** Numbers the reads of saved queries, explanations: an answer to one before the last is left. */
  private loads = 0;
  private explains = 0;
  /** Counts the addresses shown: what is answered for one shown before (a save, a delete) is left. */
  private shows = 0;

  // What the editor holds.
  /** The saved query shown (its id), or none. */
  protected readonly id = signal<number | null>(null);
  /** The saved query, as read (or saved) last; null until it is, and without one. */
  protected readonly saved = signal<SavedQuery | null>(null);
  protected readonly loadProblem = signal<Problem | null>(null);
  /** The saved query that couldn't be read. */
  protected readonly unread = signal<number | null>(null);
  protected readonly addressProblem = signal<string | null>(null);
  protected readonly text = signal('');
  /** The parameters' values, as typed (those of parameters the text no longer uses too). */
  protected readonly values = signal<ParameterValues>({});
  /** The run whose rows are shown. */
  protected readonly run = signal<ShownRun | null>(null);
  protected readonly outcome = signal<RunOutcome | null>(null);
  protected readonly explained = signal<Explained | null>(null);
  protected readonly explainProblem = signal<Problem | null>(null);
  protected readonly explaining = signal(false);
  protected readonly verbose = signal(false);
  protected readonly saveProblem = signal<{
    readonly text: string;
    readonly conflict: boolean;
  } | null>(null);
  protected readonly saving = signal(false);
  /** A notice of what was done (the link copied, the query saved), in the page. */
  protected readonly notice = signal<string | null>(null);
  protected readonly tab = signal<Tab>('results');
  /** What waits for the text as it is to be checked (a run, an explanation), its parameters typed then. */
  private readonly afterCheck = signal<(() => void) | null>(null);
  /** The text whose check failed last: what waits for it goes on (its parameters typed as the command line types them). */
  private readonly checkFailed = signal<string | null>(null);
  /** Whether a run waits for the text to be checked. */
  protected readonly waitingToRun = signal(false);
  protected readonly tabIndex = computed(() => tabs.indexOf(this.tab()));

  // What checking the text finds.
  private readonly toCheck = debounced(
    () => ({ text: this.text(), values: this.values() }),
    this.waits.check,
  );
  private readonly validation = rxResource({
    params: () => {
      const check = this.toCheck();
      return check.text.trim() === '' ? undefined : check;
    },
    stream: ({ params }) =>
      this.api
        .post('/api/query/validate', {
          body: { text: params.text, parameters: this.sendable(params.values) },
        })
        .pipe(
          map((validation): Checked => ({ text: params.text, validation })),
          tap({ error: () => this.checkFailed.set(params.text) }),
        ),
  });
  /** What checking found last (kept while the text is checked again); none for an empty text. */
  protected readonly checked = linkedSignal<Checked | undefined, Checked | null>({
    source: () => (this.validation.hasValue() ? this.validation.value() : undefined),
    computation: (next, previous) =>
      this.text().trim() === '' ? null : (next ?? previous?.value ?? null),
  });
  /** Whether what checking found is of the text as it is. */
  private readonly checkedNow = computed(() => this.checked()?.text === this.text());
  /** The parameters the text uses (as last checked). */
  protected readonly used = computed(() => this.checked()?.validation.parameters ?? []);
  /** The names of the parameters the text uses, as it is now; null until it is checked. */
  private readonly usedNames = computed(() => {
    const checked = this.checked();
    return checked && this.checkedNow()
      ? new Set(checked.validation.parameters.map((parameter) => parameter.name))
      : null;
  });
  /** The values of the parameters the text uses (all given, until it is checked). */
  private readonly usedValues = computed(() => this.usedOf(this.values()));
  /** What is wrong with the values given, by parameter: values that aren't of the type the query takes. */
  protected readonly parameterProblems = computed<Readonly<Record<string, string>>>(() => {
    const values = this.values();
    return Object.fromEntries(
      this.used().flatMap((parameter) => {
        const problem = parameterProblemOf(parameter.name, values[parameter.name], parameter.type);
        return problem ? [[parameter.name, problem]] : [];
      }),
    );
  });
  protected readonly markers = computed<EditorMarker[]>(() => {
    const checked = this.checked();
    if (!checked || !this.checkedNow()) {
      return [];
    }
    return checked.validation.diagnostics.map((diagnostic) => ({
      start: diagnostic.start,
      length: diagnostic.end - diagnostic.start,
      message: `${diagnostic.message} (${diagnostic.code})`,
      severity: diagnostic.severity,
    }));
  });
  /** How many errors and warnings the messages hold (for their tab), and whether an error is among them. */
  protected readonly messageCount = computed(() => {
    const found = (this.checked()?.validation.diagnostics ?? []).filter(
      (diagnostic) => diagnostic.severity !== 'info',
    );
    const outcome = this.outcome();
    const warnings = outcome?.kind === 'read' ? outcome.warnings.length : 0;
    const failed = outcome?.kind === 'failed' ? 1 : 0;
    return {
      count: found.length + warnings + failed,
      errors: failed > 0 || found.some((diagnostic) => diagnostic.severity === 'error'),
    };
  });
  /** Whether a saved query is being read (or couldn't be): its text isn't the editor's yet. */
  protected readonly reading = computed(() => this.id() !== null && !this.saved());
  protected readonly readOnlyMessage = computed(() =>
    this.loadProblem() ? "The saved query couldn't be read" : 'The saved query is being read',
  );

  /** Whether the text or values differ from the saved query's (or, without one, there is a text). */
  protected readonly edited = computed(() => {
    const saved = this.saved();
    if (!saved) {
      return this.id() === null && this.text() !== '';
    }
    return (
      this.text() !== saved.text ||
      !sameValues(this.usedValues(), this.usedOf(savedValuesOf(saved)))
    );
  });
  protected readonly heading = computed(() => {
    const saved = this.saved();
    if (saved) {
      return saved.name;
    }
    return this.id() === null ? 'New query' : 'Query';
  });
  /** Whether the rows shown are the query's as it is now (its text and values): not a run stopped before they came. */
  private readonly shown = computed(() => {
    const run = this.run();
    return (
      run !== null &&
      this.outcome()?.kind !== 'stopped' &&
      run.text === this.text() &&
      sameValues(this.usedOf(run.values), this.usedValues())
    );
  });
  /** The address of what the editor holds; null while its saved query isn't known. */
  private readonly address = computed<QueryAddress | null>(() => {
    const id = this.id();
    const saved = this.saved();
    if (id !== null && saved?.id !== id) {
      return null;
    }
    const text = this.text();
    const values = this.usedValues();
    const savedValues = saved ? this.usedOf(savedValuesOf(saved)) : {};
    return {
      id,
      text: saved ? (text === saved.text ? null : text) : text === '' ? null : text,
      values: sameValues(values, savedValues) ? null : values,
      // A run waiting for the text to be checked is shown as soon as it is.
      run: this.shown() || this.waitingToRun(),
    };
  });

  constructor() {
    // The address changed, but not by the page (back, forward, a link, the page opened): the editor shows it.
    effect(() => {
      const { address, problem } = this.addressRead();
      untracked(() => {
        const url = this.urlOf(address);
        if (url !== this.written) {
          this.apply(address, problem);
        }
      });
    });
    // What the editor holds: the address says it, once typing pauses.
    effect(() => {
      const address = this.address();
      untracked(() => this.scheduleWrite(address));
    });
    // The saved query's name titles the page.
    effect(() => {
      const saved = this.saved();
      untracked(() => this.pageTitle.detail.set(saved?.name ?? null));
    });
    // What waits for the text to be checked goes on once it is (or its check failed).
    effect(() => {
      const action = this.afterCheck();
      const text = this.text();
      const ready = this.checkedNow() || this.checkFailed() === text || text.trim() === '';
      if (action && ready) {
        untracked(() => {
          this.afterCheck.set(null);
          action();
        });
      }
    });
    // The page left (another address asked for) before its address was written: it is written in place first,
    // without a navigation (which would take the place of the one under way), so going back shows the query as left.
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationStart),
        takeUntilDestroyed(),
      )
      .subscribe((event) => {
        if (event.navigationTrigger === 'popstate' || this.writeTimer === null) {
          return;
        }
        this.cancelWrite();
        const address = untracked(this.address);
        if (address) {
          const url = this.urlOf(address);
          this.written = url;
          this.last.url = url;
          this.location.replaceState(url);
        }
      });
    inject(DestroyRef).onDestroy(() => {
      this.pageTitle.detail.set(null);
      this.cancelWrite();
      // Gone elsewhere: the editor's query, as it is, is the one shown when the editor is opened again; not when
      // the page showed none of its own (left as it was about to show the query left).
      const address = untracked(this.address);
      if (address && this.written !== null) {
        this.last.url = this.urlOf(address);
      }
    });
  }

  /**
   * Runs the query as it is, with its values: its rows are shown. It runs once the text as it is has been checked,
   * so its parameters are typed as the query takes them.
   */
  protected runQuery(): void {
    if (this.text().trim() === '' || this.reading()) {
      return;
    }
    this.waitingToRun.set(true);
    this.tab.set('results');
    this.whenChecked(() => {
      this.waitingToRun.set(false);
      const text = this.text();
      if (text.trim() === '' || !this.valuesFine()) {
        return;
      }
      const values = this.usedValues();
      this.run.set({ text, values, parameters: this.sendable(values), serial: ++runs });
      this.outcome.set(null);
    });
  }

  /** Explains how the query would run: its plan, and the SQL each source runs (once the text is checked). */
  protected explain(verbose = this.verbose()): void {
    if (this.text().trim() === '' || this.reading()) {
      return;
    }
    if (this.tab() !== 'sql') {
      this.tab.set('plan');
    }
    const was = this.verbose();
    this.verbose.set(verbose);
    this.explaining.set(true);
    this.whenChecked(() => void this.explainNow(verbose, was));
  }

  private async explainNow(verbose: boolean, was: boolean): Promise<void> {
    const text = this.text();
    if (text.trim() === '' || !this.valuesFine()) {
      this.explaining.set(false);
      this.verbose.set(was);
      return;
    }
    const values = this.usedValues();
    const asked = ++this.explains;
    this.explainProblem.set(null);
    try {
      const explain = await firstValueFrom(
        this.api.post('/api/query/explain', {
          body: { text, parameters: this.sendable(values), verbose },
        }),
      );
      if (asked === this.explains) {
        this.explained.set({ text, values, explain });
      }
    } catch (error) {
      const problem = problemOf(error);
      if (asked === this.explains && !isSessionProblem(problem)) {
        this.explainProblem.set(problem);
        // As it was: the checkbox asked for what couldn't be had.
        this.verbose.set(was);
      }
    } finally {
      if (asked === this.explains) {
        this.explaining.set(false);
      }
    }
  }

  /** Whether the explanation shown is of the query as it is now. */
  protected explainedNow(explained: Explained): boolean {
    return explained.text === this.text() && sameValues(explained.values, this.usedValues());
  }

  protected tabChanged(index: number): void {
    this.tab.set(tabs[index] ?? 'results');
  }

  protected outcomeOf(outcome: RunOutcome): void {
    this.outcome.set(outcome);
    if (outcome.kind === 'stopped') {
      // Its Stop button gone, the keyboard goes to Run.
      this.keepKeyboard(() => this.runButton().nativeElement.focus());
    }
  }

  /** The editor goes to an offset of the text. */
  protected goTo(offset: number): void {
    this.editor().reveal(offset);
  }

  /** A link of the rows followed: the address of this query kept, then where the link leads. */
  protected async follow(link: FollowedLink): Promise<void> {
    await this.flush();
    if (link.kind === 'browse') {
      await this.router.navigateByUrl(link.url);
    } else {
      await this.router.navigateByUrl(
        queryUrlTree({ id: null, text: link.text, values: link.values, run: true }),
      );
      // The grid the link was in is gone: the keyboard goes to the query of the rows it led to.
      this.say("The rows it was worked out from, as a query: they're shown once it runs");
      this.keepKeyboard(() => this.editor().focus());
    }
  }

  /** A new query, in place of this one (which going back shows again). */
  protected async newQuery(): Promise<void> {
    await this.flush();
    await this.router.navigateByUrl(
      queryUrlTree({ id: null, text: null, values: null, run: false }),
      {
        state: newQueryState,
      },
    );
    this.keepKeyboard(() => this.editor().focus());
  }

  /** Opens a saved query, chosen in a dialog. */
  protected async open(): Promise<void> {
    await this.flush();
    const { OpenQueryDialog } = await import('./open-query-dialog');
    this.dialog.open<InstanceType<typeof OpenQueryDialog>, OpenQueryData>(OpenQueryDialog, {
      data: { current: this.id() },
      width: '560px',
      maxHeight: '90vh',
    });
  }

  /** Saves the query: in place, when it is a saved query the user may change; else as a new one. */
  protected async save(): Promise<void> {
    if (this.reading() || this.saving() || !this.valuesFine()) {
      return;
    }
    const saved = this.saved();
    if (!saved || !saved.canEdit) {
      await this.saveAs();
      return;
    }
    const shown = this.shows;
    this.saving.set(true);
    this.saveProblem.set(null);
    try {
      const outcome = await this.update(saved, {
        name: saved.name,
        description: saved.description ?? '',
        isShared: saved.isShared,
      });
      if (shown !== this.shows) {
        return;
      }
      if ('saved' in outcome) {
        this.say(`${outcome.saved.name} is saved`);
      } else if (!isSessionProblem(outcome.problem)) {
        this.saveProblem.set({
          text: `Couldn't save ${saved.name}: ${problemMessage(outcome.problem)}`,
          conflict:
            outcome.problem.code === ProblemCode.concurrencyConflict ||
            outcome.problem.code === ProblemCode.forbidden,
        });
      }
    } finally {
      this.saving.set(false);
    }
  }

  /** Saves the query as a new one of the user's, named in a dialog. */
  protected async saveAs(): Promise<void> {
    if (this.reading() || !this.valuesFine()) {
      return;
    }
    const saved = this.saved();
    const created = await this.ask({
      title: saved ? 'Save a copy' : 'Save the query',
      action: 'Save',
      details: {
        name: saved ? `${saved.name} (copy)` : '',
        description: saved?.description ?? '',
        isShared: false,
      },
      save: (details) => this.create(details),
    });
    if (created) {
      this.saveProblem.set(null);
      this.say(`${created.name} is saved`);
    }
  }

  /** Changes the saved query's name, description and sharing (its text as saved). */
  protected async editDetails(): Promise<void> {
    const saved = this.saved();
    if (!saved?.canEdit) {
      return;
    }
    const changed = await this.ask({
      title: `${saved.name}'s details`,
      action: 'Save the details',
      details: {
        name: saved.name,
        description: saved.description ?? '',
        isShared: saved.isShared,
      },
      save: (details) => this.update(saved, details, true),
    });
    if (changed) {
      this.say(`${changed.name}'s details are saved`);
    }
  }

  /** Deletes the saved query, once confirmed: the editor keeps its text, a query not saved. */
  protected async deleteQuery(): Promise<void> {
    const saved = this.saved();
    if (!saved?.canEdit) {
      return;
    }
    const sure = await this.confirmer.confirm({
      title: `Delete ${saved.name}?`,
      message: saved.isShared
        ? 'Everyone it is shared with loses it too. The editor keeps its text.'
        : 'The editor keeps its text.',
      confirm: 'Delete',
      destructive: true,
    });
    if (!sure) {
      return;
    }
    const shown = this.shows;
    try {
      await firstValueFrom(
        this.api.delete('/api/saved-queries/{id}', {
          path: { id: saved.id },
          query: { version: saved.version },
        }),
      );
      if (shown !== this.shows) {
        return;
      }
      this.saved.set(null);
      this.id.set(null);
      this.saveProblem.set(null);
      this.say(`${saved.name} is deleted`);
      // Its menu's item gone, the keyboard goes to the editor, which keeps the text.
      this.keepKeyboard(() => this.editor().focus());
    } catch (error) {
      const problem = problemOf(error);
      if (shown === this.shows && !isSessionProblem(problem)) {
        this.saveProblem.set({
          text: `Couldn't delete ${saved.name}: ${problemMessage(problem)}`,
          conflict: problem.code === ProblemCode.concurrencyConflict,
        });
      }
    }
  }

  /**
   * The saved query read again: after a conflict (the editor keeps its text and values), or a failure to read it
   * (the editor then shows what its address said).
   */
  protected readAgain(): void {
    const id = this.id();
    if (id !== null) {
      this.saveProblem.set(null);
      this.loadProblem.set(null);
      void this.load(id, this.pending);
    }
  }

  /** The editor's text and values go back to the saved query's. */
  protected undoEdits(): void {
    const saved = this.saved();
    if (saved) {
      this.text.set(saved.text);
      this.values.set(savedValuesOf(saved));
      this.editor().focus();
    }
  }

  /** Copies a link to the query as it is: a shared saved query's own address, else its text and values. */
  protected async copyLink(): Promise<void> {
    const saved = this.saved();
    const values = this.usedValues();
    const address: QueryAddress =
      saved?.isShared && !this.edited()
        ? { id: saved.id, text: null, values: null, run: false }
        : {
            id: null,
            text: this.text() === '' ? null : this.text(),
            values: Object.keys(values).length === 0 ? null : values,
            run: false,
          };
    const path = this.locationStrategy.prepareExternalUrl(this.urlOf(address));
    const link = new URL(path, this.document.location.href).href;
    try {
      await this.document.defaultView?.navigator.clipboard.writeText(link);
      this.say('A link to the query is copied');
    } catch {
      this.notice.set(`Copy this link to the query: ${link}`);
    }
  }

  /** Ctrl+Enter runs the query (the editor's own does too), Ctrl+S saves it. */
  protected keyDown(event: KeyboardEvent): void {
    const mac = /Mac|iPhone|iPad/.test(this.document.defaultView?.navigator.platform ?? '');
    if (!(mac ? event.metaKey : event.ctrlKey) || event.altKey) {
      return;
    }
    // Editors' Ctrl+Enter is their own (the query's runs it), and so is the grid's (on a header, its filter).
    const own = (event.target as Element | null)?.closest?.('gd-code-editor, ag-grid-angular');
    if (event.key === 'Enter' && !own && !event.shiftKey) {
      event.preventDefault();
      this.runQuery();
    } else if (event.key.toLowerCase() === 's' && !event.shiftKey) {
      event.preventDefault();
      void this.save();
    }
  }

  /** Shows an address the page didn't write: its saved query (read first), text, values and rows. */
  private apply(address: QueryAddress, problem: string | null): void {
    this.addressProblem.set(problem);
    const bare = address.id === null && address.text === null && address.values === null;
    const navigation = this.router.currentNavigation() ?? this.router.lastSuccessfulNavigation();
    if (
      bare &&
      !address.run &&
      navigation?.trigger !== 'popstate' &&
      !opensNewQuery(navigation) &&
      this.last.url !== null &&
      this.last.url !== this.urlOf(address)
    ) {
      // Opened again from the navigation (or chosen there while open): the query left here, which the page writes
      // nothing over till it is shown.
      this.cancelWrite();
      this.restoring = true;
      void this.router
        .navigateByUrl(this.last.url, { replaceUrl: true })
        .finally(() => (this.restoring = false));
      return;
    }
    this.restoring = false;
    this.shows++;
    this.cancelWrite();
    this.written = this.urlOf(address);
    this.last.url = this.written;
    this.loadProblem.set(null);
    this.saveProblem.set(null);
    this.notice.set(null);
    if (address.id === null) {
      this.pending = null;
      this.id.set(null);
      this.saved.set(null);
      this.adopt(address, null);
    } else if (this.saved()?.id === address.id) {
      this.pending = null;
      this.id.set(address.id);
      this.adopt(address, this.saved());
    } else {
      // Nothing of the query shown before, while this one is read.
      this.pending = address;
      this.id.set(address.id);
      this.saved.set(null);
      this.text.set('');
      this.values.set({});
      this.run.set(null);
      this.outcome.set(null);
      void this.load(address.id, address);
    }
  }

  /** The editor holds an address's text and values (or its saved query's), and its rows when it ran. */
  private adopt(address: QueryAddress, saved: SavedQuery | null): void {
    this.text.set(address.text ?? saved?.text ?? '');
    this.values.set(address.values ?? (saved ? savedValuesOf(saved) : {}));
    this.run.set(null);
    this.outcome.set(null);
    this.afterCheck.set(null);
    this.waitingToRun.set(false);
    if (address.run) {
      this.runQuery();
    }
  }

  /** Reads a saved query; with an address waiting for it, shows the address once it is read. */
  private async load(id: number, waiting: QueryAddress | null): Promise<void> {
    const asked = ++this.loads;
    try {
      const saved = await firstValueFrom(this.api.get('/api/saved-queries/{id}', { path: { id } }));
      if (asked !== this.loads || this.id() !== id) {
        return;
      }
      this.saved.set(saved);
      if (waiting && this.pending === waiting) {
        this.pending = null;
        this.adopt(waiting, saved);
      }
    } catch (error) {
      if (asked !== this.loads || this.id() !== id) {
        return;
      }
      const problem = problemOf(error);
      if (isSessionProblem(problem)) {
        return;
      }
      this.unread.set(id);
      if (problem.status === 404) {
        // A query the user can't see (any more): what the editor holds (or its address said) isn't saved.
        this.id.set(null);
        this.saved.set(null);
        if (waiting && this.pending === waiting) {
          this.adopt({ ...waiting, id: null }, null);
        }
        this.pending = null;
      }
      this.loadProblem.set(problem);
    }
  }

  /** Asks for a saved query's details in a dialog, which saves them; the query saved, or none. */
  private async ask(data: SaveQueryData): Promise<SavedQuery | undefined> {
    const { SaveQueryDialog } = await import('./save-query-dialog');
    return firstValueFrom(
      this.dialog
        .open<InstanceType<typeof SaveQueryDialog>, SaveQueryData, SavedQuery>(SaveQueryDialog, {
          data,
          width: '480px',
        })
        .afterClosed(),
    );
  }

  /** Saves a new query of the user's, with the editor's text and values: the editor shows it. */
  private async create(details: QueryDetails): Promise<SaveOutcome> {
    const shown = this.shows;
    try {
      const saved = await firstValueFrom(
        this.api.post('/api/saved-queries', {
          body: { ...this.savable(details), text: this.text(), parameters: this.savedInputs() },
        }),
      );
      // Saved, though the page may show another query by now.
      if (shown === this.shows) {
        this.saved.set(saved);
        this.id.set(saved.id);
      }
      return { saved };
    } catch (error) {
      return { problem: problemOf(error) };
    }
  }

  /**
   * Saves a saved query: with the editor's text and values, or (`detailsOnly`) as it was saved, its details changed.
   */
  private async update(
    saved: SavedQuery,
    details: QueryDetails,
    detailsOnly = false,
  ): Promise<SaveOutcome> {
    const shown = this.shows;
    try {
      const updated = await firstValueFrom(
        this.api.put('/api/saved-queries/{id}', {
          path: { id: saved.id },
          body: {
            query: {
              ...this.savable(details),
              text: detailsOnly ? saved.text : this.text(),
              parameters: detailsOnly ? saved.parameters : this.savedInputs(),
            },
            version: saved.version,
          },
        }),
      );
      if (shown === this.shows) {
        this.saved.set(updated);
      }
      return { saved: updated };
    } catch (error) {
      return { problem: problemOf(error) };
    }
  }

  private savable(details: QueryDetails) {
    return {
      name: details.name,
      description: details.description === '' ? null : details.description,
      isShared: details.isShared,
    };
  }

  /** The values saved with a query: those of the parameters it uses, typed as it takes them. */
  private savedInputs(): QueryParameterInput[] {
    return this.sendable(this.usedValues());
  }

  /**
   * Values as sent: those given, typed as the query takes them (as last checked), and fine for their types (those
   * that aren't are said at their fields, and not sent).
   */
  private sendable(values: ParameterValues): QueryParameterInput[] {
    const types = new Map(
      untracked(this.used).map((parameter) => [parameter.name, parameter.type] as const),
    );
    return Object.entries(values)
      .filter(([name]) => given(values, name))
      .filter(([name, value]) => parameterProblemOf(name, value, types.get(name) ?? null) === null)
      .map(([name, value]) => parameterInputOf(name, value, types.get(name) ?? null));
  }

  /** Whether the values given are fine for their types: if not, the keyboard goes to the first that isn't. */
  private valuesFine(): boolean {
    const wrong = Object.keys(this.parameterProblems());
    if (wrong.length === 0) {
      return true;
    }
    this.host.nativeElement
      .querySelector<HTMLInputElement>(`input[data-parameter="${CSS.escape(wrong[0])}"]`)
      ?.focus();
    this.say(`$${wrong[0]}: ${this.parameterProblems()[wrong[0]]}`);
    return false;
  }

  /** Values of the parameters the text uses (all, until it is checked). */
  private usedOf(values: ParameterValues): ParameterValues {
    const names = this.usedNames();
    return names === null
      ? values
      : Object.fromEntries(Object.entries(values).filter(([name]) => names.has(name)));
  }

  private urlOf(address: QueryAddress): string {
    return this.router.serializeUrl(queryUrlTree(address));
  }

  /** The address follows the editor once typing pauses (or before the page is left). */
  private scheduleWrite(address: QueryAddress | null): void {
    if (!address || this.restoring) {
      return;
    }
    const url = this.urlOf(address);
    this.cancelWrite();
    if (url === this.written) {
      return;
    }
    this.writeTimer = setTimeout(() => {
      this.writeTimer = null;
      this.write();
    }, this.waits.write);
  }

  private cancelWrite(): void {
    if (this.writeTimer !== null) {
      clearTimeout(this.writeTimer);
      this.writeTimer = null;
    }
  }

  /** Writes the address of what the editor holds (in place of the address shown). */
  private write(): Promise<boolean> {
    const address = untracked(this.address);
    if (!address) {
      return Promise.resolve(true);
    }
    const url = this.urlOf(address);
    this.last.url = url;
    if (url === this.written) {
      return Promise.resolve(true);
    }
    this.written = url;
    return this.router.navigateByUrl(queryUrlTree(address), { replaceUrl: true });
  }

  /** The address written now (not once typing pauses): before the page goes elsewhere. */
  private async flush(): Promise<void> {
    this.cancelWrite();
    await this.write();
  }

  private say(message: string): void {
    void this.announcer.announce(message, 'polite');
    this.notice.set(message);
  }

  /** Does `action` once the page is rendered: when what had the keyboard is gone with what it changed. */
  private keepKeyboard(action: () => void): void {
    afterNextRender(action, { injector: this.injector });
  }

  /** Does `action` once the text as it is has been checked (at once, when it is). */
  private whenChecked(action: () => void): void {
    if (this.checkedNow()) {
      action();
    } else {
      this.afterCheck.set(action);
    }
  }
}

/** A saved query's values, as the editor holds them. */
function savedValuesOf(saved: SavedQuery): ParameterValues {
  return Object.fromEntries(
    saved.parameters.map((parameter) => [
      parameter.name.replace(/^\$/, ''),
      valueText(parameter.value),
    ]),
  );
}
