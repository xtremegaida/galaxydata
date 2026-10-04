import { DOCUMENT } from '@angular/common';
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  linkedSignal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatTab, MatTabContent, MatTabGroup } from '@angular/material/tabs';
import {
  ActivatedRoute,
  type ResolveFn,
  Router,
  type UrlMatchResult,
  type UrlSegment,
} from '@angular/router';
import { combineLatest, map } from 'rxjs';
import { ApiClient, type Schema } from '../../core/api/api-client';
import { ProblemCode, isSessionProblem, problemMessage, problemOf } from '../../core/api/problem';
import {
  type BrowseCrumb,
  type BrowseLocation,
  activeCrumb,
  browseUrlTree,
  readBrowseLocation,
} from '../../core/browse/browse-url';
import { followCatalog } from '../../core/catalog/catalog-changes';
import { Message } from '../../core/ui/message';
import { type Entity, EntityStructure, describeEntity } from './entity-structure';
import type { BrowseSource } from './grid/browse-datasource';
import { BrowseGrid } from './grid/browse-grid';

type TrailCrumb = Schema<'TrailCrumbDto'>;

/** Browsing's pages are every address under it with segments: a path through the data. */
export function browseMatcher(segments: UrlSegment[]): UrlMatchResult | null {
  return segments.length > 0 ? { consumed: segments } : null;
}

/** A page is titled by the path to the crumb it shows: `shop.customers › orders`. */
export const browseTitle: ResolveFn<string> = (route) => {
  const read = readBrowseLocation(route.url, route.queryParams);
  return read ? pathOf(read.location) : 'Browse';
};

/** The names of the crumbs up to the one shown. */
function pathOf(location: BrowseLocation): string {
  return location.crumbs
    .slice(0, location.at + 1)
    .map((crumb) => crumb.name)
    .join(' › ');
}

function sentence(text: string): string {
  return /[.!?]$/.test(text) ? text : `${text}.`;
}

/** An entity, as read for the crumb shown. */
interface Shown {
  readonly path: string;
  readonly entity: Entity;
}

/**
 * Browsing a path through the data: the rows of the crumb the address shows (an entity's, or those a navigation
 * leads to from the row chosen in the crumb before), in a grid whose state the address keeps, and what the entity
 * is. Navigations' crumbs are followed through the API's trail, which says what entity each reaches.
 */
@Component({
  selector: 'gd-browse-page',
  imports: [
    BrowseGrid,
    EntityStructure,
    MatButton,
    MatProgressBar,
    MatTab,
    MatTabContent,
    MatTabGroup,
    Message,
  ],
  templateUrl: './browse-page.html',
  styleUrls: ['./browse-shared.scss', './browse-page.scss'],
})
export class BrowsePage {
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');

  private readonly address = addressOf(inject(ActivatedRoute));
  protected readonly location = computed<BrowseLocation>(
    () => this.address()?.location ?? { crumbs: [], at: 0 },
  );
  /** What in the address couldn't be read, and was left out. */
  protected readonly addressProblems = computed(() => this.address()?.problems ?? []);
  protected readonly crumb = computed(() => activeCrumb(this.location()));
  protected readonly path = computed(() => pathOf(this.location()));
  protected readonly pathShown = computed(() => this.location().crumbs.length > 1);

  /** The crumbs to the one shown, their rows chosen (but the one shown's), for the trail. */
  private readonly trailAsked = computed(
    () => {
      const { crumbs, at } = this.location();
      if (at === 0) {
        return undefined;
      }
      return crumbs
        .slice(0, at + 1)
        .map((crumb, index): TrailCrumb =>
          index === 0
            ? { entity: crumb.name, key: crumb.row ? [...crumb.row] : null }
            : { navigation: crumb.name, key: index < at && crumb.row ? [...crumb.row] : null },
        );
    },
    { equal: (a, b) => JSON.stringify(a) === JSON.stringify(b) },
  );
  private readonly trailFollowed = followCatalog(() => this.trail.reload());
  protected readonly trail = rxResource({
    params: () => this.trailAsked(),
    stream: ({ params }) =>
      this.api.post('/api/browse/trail', { body: { crumbs: params } }).pipe(this.trailFollowed()),
  });
  /** Why the crumb shown can't be reached, from the trail. */
  protected readonly trailProblem = computed(() => {
    const { crumbs, at } = this.location();
    if (at === 0 || !this.trail.hasValue()) {
      return null;
    }
    const steps = this.trail.value().crumbs;
    for (let index = 0; index < at; index++) {
      const step = steps[index];
      if (!step || step.problem || !step.entity) {
        return sentence(step?.problem ?? `${crumbs[index].name} can't be followed`);
      }
      if (!crumbs[index].row) {
        return `No row is chosen in ${crumbs[index].name}, so ${crumbs[index + 1].name} leads nowhere.`;
      }
      if (step.found === false) {
        return `The row chosen in ${crumbs[index].name} isn't among its rows (any more), so ${crumbs[index + 1].name} leads nowhere.`;
      }
    }
    const problem = steps[at]?.problem;
    return problem ? sentence(problem) : null;
  });

  /** What the crumb shown browses: an entity's rows, or those a navigation leads to from the row chosen before. */
  protected readonly source = computed<BrowseSource | null>(
    () => {
      const { crumbs, at } = this.location();
      if (crumbs.length === 0) {
        return null;
      }
      if (at === 0) {
        return { entity: crumbs[0].name };
      }
      const before = this.trail.hasValue() ? this.trail.value().crumbs[at - 1] : undefined;
      const row = crumbs[at - 1].row;
      if (this.trailProblem() || !before?.entity || !row) {
        return null;
      }
      return { from: { entity: before.entity, key: [...row] }, navigation: crumbs[at].name };
    },
    { equal: (a, b) => JSON.stringify(a) === JSON.stringify(b) },
  );
  /** The entity whose rows the crumb shown are. */
  protected readonly entityName = computed(() => {
    const { crumbs, at } = this.location();
    if (at === 0) {
      return crumbs[0]?.name ?? null;
    }
    return this.trail.hasValue() ? (this.trail.value().crumbs[at]?.entity ?? null) : null;
  });

  private readonly followed = followCatalog(() => this.described.reload());
  protected readonly described = rxResource({
    params: () => {
      const name = this.entityName();
      return name === null ? undefined : { name };
    },
    stream: ({ params }) =>
      this.api.get('/api/catalog/entity', { query: params }).pipe(this.followed()),
  });
  /** The entity read: kept while it is read again (the catalog changed), not while another is read. */
  protected readonly shown = linkedSignal<Shown | undefined, Shown | undefined>({
    source: () =>
      this.described.hasValue() ? { path: this.path(), entity: this.described.value() } : undefined,
    computation: (shown, previous) =>
      shown ?? (previous?.value?.path === this.path() ? previous.value : undefined),
  });
  protected readonly problem = computed(() => {
    const error = this.described.error() ?? this.trail.error();
    const problem = error ? problemOf(error) : null;
    return problem && !isSessionProblem(problem) ? problem : null;
  });
  protected readonly notFound = computed(
    () => this.location().at === 0 && this.problem()?.code === ProblemCode.notFound,
  );
  protected readonly message = problemMessage;
  protected readonly describe = describeEntity;

  constructor() {
    this.focusWhenReplaced(inject(DOCUMENT), inject(Injector));
  }

  /** The grid's state changed: the address follows it, in place (a row chosen anew lets go of the crumbs after). */
  protected update(crumb: BrowseCrumb): void {
    const { crumbs, at } = this.location();
    const before = crumbs[at];
    if (crumb.name !== before?.name) {
      // A grid of another crumb, going as this one comes.
      return;
    }
    const kept =
      JSON.stringify(before.row) === JSON.stringify(crumb.row) ? crumbs : crumbs.slice(0, at + 1);
    const next = kept.map((each, index) => (index === at ? crumb : each));
    void this.router.navigateByUrl(browseUrlTree({ crumbs: next, at }), { replaceUrl: true });
  }

  protected reload(): void {
    if (this.trail.error()) {
      this.trail.reload();
    }
    if (this.described.error()) {
      this.described.reload();
    }
  }

  /**
   * Another entity shown in place of one (a navigation's link followed, an entity chosen in the catalog over the
   * page): focus goes to its heading, from the link lost with the page, or the button that opened the catalog.
   * Focus in the catalog beside the page (its tree, its search) stays.
   */
  private focusWhenReplaced(document: Document, injector: Injector): void {
    let shownPath: string | null = null;
    effect(() => {
      const path = this.shown()?.path ?? null;
      if (path === null || path === shownPath) {
        return;
      }
      const replaced = shownPath !== null;
      shownPath = path;
      if (!replaced) {
        return;
      }
      untracked(() =>
        afterNextRender(
          () => {
            const focused = document.activeElement;
            if (!focused?.closest('gd-catalog-panel')) {
              this.heading()?.nativeElement.focus();
            }
          },
          { injector },
        ),
      );
    });
  }
}

/** The address under browsing, as each navigation leaves it. */
function addressOf(route: ActivatedRoute) {
  return toSignal(
    combineLatest([route.url, route.queryParams]).pipe(
      map(([segments, query]) => readBrowseLocation(segments, query)),
    ),
    { initialValue: readBrowseLocation(route.snapshot.url, route.snapshot.queryParams) },
  );
}
