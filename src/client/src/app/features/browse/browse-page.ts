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
  RouterLink,
  type UrlMatchResult,
  type UrlSegment,
  type UrlTree,
} from '@angular/router';
import { combineLatest, map } from 'rxjs';
import { ApiClient, type Schema } from '../../core/api/api-client';
import { ProblemCode, isSessionProblem, problemMessage, problemOf } from '../../core/api/problem';
import {
  type BrowseCrumb,
  type BrowseLocation,
  activeCrumb,
  browseUrlTree,
  followedLocation,
  locationAt,
  readBrowseLocation,
  sameKey,
} from '../../core/browse/browse-url';
import { followCatalog } from '../../core/catalog/catalog-changes';
import { Message } from '../../core/ui/message';
import { type Entity, EntityStructure, describeEntity } from './entity-structure';
import type { BrowseSource } from './grid/browse-datasource';
import { BrowseGrid, type LinkTo } from './grid/browse-grid';
import { displayText } from './grid/grid-links';

type TrailCrumb = Schema<'TrailCrumbDto'>;
type TrailStep = Schema<'TrailStepDto'>;

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

/** A crumb as the path shows it: what it browses, the row chosen in it, and its address. */
interface PathCrumb {
  readonly label: string;
  /** The row chosen in it, by its display value when the trail has come (else its key); null for none. */
  readonly row: string | null;
  readonly url: UrlTree;
  readonly current: boolean;
}

/** What the trail's steps to the crumb shown depend on: the crumbs' names to it, and the rows chosen before it. */
interface StepsFor {
  readonly prefix: string;
  readonly at: number;
  readonly steps: readonly TrailStep[] | undefined;
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
    RouterLink,
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

  /**
   * The path's crumbs, for the trail: their rows chosen (but the last's, which nothing follows from). Rows chosen
   * in the last crumb, and other crumbs shown, don't ask for it again; nor do crumbs let go of (a row chosen anew),
   * as the trail asked for already says what one for those left would.
   */
  private readonly trailAsked = computed(
    () => {
      const { crumbs } = this.location();
      if (crumbs.length < 2) {
        return undefined;
      }
      const last = crumbs.length - 1;
      return crumbs.map((crumb, index): TrailCrumb => {
        const key = index < last && crumb.row ? [...crumb.row] : null;
        return index === 0 ? { entity: crumb.name, key } : { navigation: crumb.name, key };
      });
    },
    { equal: covers },
  );
  private readonly trailFollowed = followCatalog(() => this.trail.reload());
  protected readonly trail = rxResource({
    params: () => this.trailAsked(),
    stream: ({ params }) =>
      this.api.post('/api/browse/trail', { body: { crumbs: params } }).pipe(this.trailFollowed()),
  });
  private readonly trailPrefix = computed(() => {
    const { crumbs, at } = this.location();
    return JSON.stringify(
      crumbs.slice(0, at + 1).map((crumb, index) => [crumb.name, index < at ? crumb.row : null]),
    );
  });
  /**
   * The trail's steps. While it is read again for a change past the crumb shown (back or forward to the same crumbs
   * to it, then others), the steps to it are kept, as they are the same: the grid stays. The row chosen in the
   * crumb shown may be another, so what it is isn't known till the trail comes.
   */
  private readonly steps = linkedSignal<StepsFor, readonly TrailStep[] | undefined>({
    source: () => ({
      prefix: this.trailPrefix(),
      at: this.location().at,
      steps: this.trail.hasValue() ? this.trail.value().crumbs : undefined,
    }),
    computation: (now, previous) => {
      if (now.steps || !previous?.value || previous.source.prefix !== now.prefix) {
        return now.steps;
      }
      return previous.value
        .slice(0, now.at + 1)
        .map((step, index) => (index === now.at ? { ...step, title: null } : step));
    },
  });
  /**
   * Why the crumb shown can't be reached, from the trail. Its own step's problem (the row chosen in it isn't one)
   * doesn't keep its rows from being shown.
   */
  protected readonly trailProblem = computed(() => {
    const { crumbs, at } = this.location();
    const steps = this.steps();
    if (at === 0 || !steps) {
      return null;
    }
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
    const shown = steps[at];
    return shown?.problem && !shown.entity ? sentence(shown.problem) : null;
  });
  /** Whether the rows shown need the trail: those of a navigation's crumb, unless its steps were kept. */
  private readonly trailNeeded = computed(() => this.location().at > 0 && !this.steps());
  /** Why the trail couldn't be read, when only the path needs it (its rows go by their keys). */
  protected readonly pathProblem = computed(() => {
    const error = this.trailNeeded() ? undefined : this.trail.error();
    const problem = error ? problemOf(error) : null;
    return problem && !isSessionProblem(problem) ? problem : null;
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
      const before = this.steps()?.[at - 1];
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
    return this.steps()?.[at]?.entity ?? null;
  });

  /** The path through the data, when it has more than one crumb: each crumb, the row chosen in it, its address. */
  protected readonly pathCrumbs = computed<PathCrumb[]>(() => {
    const location = this.location();
    const { crumbs, at } = location;
    if (crumbs.length < 2) {
      return [];
    }
    const steps = this.steps();
    return crumbs.map((crumb, index) => {
      const step = steps?.[index];
      return {
        label: step?.label || crumb.name,
        row: index < crumbs.length - 1 && crumb.row ? rowText(crumb.row, step) : null,
        url: browseUrlTree(locationAt(location, index)),
        current: index === at,
      };
    });
  });
  /** Where a navigation followed from a row of the crumb shown leads: a crumb after it. */
  protected readonly linkTo = computed<LinkTo>(() => {
    const location = this.location();
    return (row, navigation) => browseUrlTree(followedLocation(location, row, navigation));
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
    const error = this.described.error() ?? (this.trailNeeded() ? this.trail.error() : undefined);
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
    const kept = sameKey(before.row, crumb.row) ? crumbs : crumbs.slice(0, at + 1);
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

/** A row chosen in a crumb, by its display value (the trail's title), else its key. */
function rowText(row: readonly string[], step: TrailStep | undefined): string {
  const title: unknown = step?.title;
  return title === null || title === undefined ? row.join(', ') : displayText(title);
}

/**
 * Whether the trail asked for says what one asked for now would: the same crumbs, or the first of them (those after
 * let go of), the same rows chosen in them, but in the last asked for now, whose row isn't asked.
 */
function covers(
  asked: readonly TrailCrumb[] | undefined,
  wanted: readonly TrailCrumb[] | undefined,
): boolean {
  if (!asked || !wanted) {
    return asked === wanted;
  }
  return (
    wanted.length <= asked.length &&
    wanted.every(
      (crumb, index) =>
        crumb.entity === asked[index].entity &&
        crumb.navigation === asked[index].navigation &&
        (index === wanted.length - 1 ||
          JSON.stringify(crumb.key) === JSON.stringify(asked[index].key)),
    )
  );
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
