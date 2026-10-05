import { Component, computed, inject, linkedSignal, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatAnchor, MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatMenu, MatMenuItem, MatMenuTrigger } from '@angular/material/menu';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import { RouterLink } from '@angular/router';
import { ApiClient } from '../../../core/api/api-client';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { followCatalog } from '../../../core/catalog/catalog-changes';
import { Message } from '../../../core/ui/message';
import { OverlayIssues } from './overlay-check';
import {
  overrideText,
  relationText,
  settingsParts,
  type Overlay,
  type OverlayIssue,
} from './overlay-items';

/** How many items have warnings only, in words: "1 has warnings", "3 have warnings". */
function withWarnings(count: number): string {
  return count === 1 ? '1 has warnings' : `${count} have warnings`;
}

/**
 * The overlay: what the catalog adds to what the databases declare, item by item (relations, virtual entities,
 * entities' settings, navigations renamed or hidden), each with what the catalog finds wrong with it now (read
 * again as the catalog changes), those with issues alone on asking; new items, and the overlay as a file.
 */
@Component({
  selector: 'gd-overlay-list',
  imports: [
    MatAnchor,
    MatButton,
    MatIcon,
    MatMenu,
    MatMenuItem,
    MatMenuTrigger,
    MatProgressBar,
    MatSlideToggle,
    Message,
    OverlayIssues,
    RouterLink,
  ],
  templateUrl: './overlay-list.html',
  styleUrls: ['../admin-page.scss', './overlay-list.scss'],
})
export class OverlayList {
  private readonly api = inject(ApiClient);
  private readonly follow = followCatalog(() => this.overlay.reload());

  protected readonly overlay = rxResource({
    stream: () => this.api.get('/api/overlay').pipe(this.follow()),
  });
  /** The overlay last read: kept while it is read again (as the catalog changes), and when that fails. */
  protected readonly shown = linkedSignal<Overlay | undefined, Overlay | null>({
    source: () => (this.overlay.hasValue() ? this.overlay.value() : undefined),
    computation: (overlay, previous) => overlay ?? previous?.value ?? null,
  });
  protected readonly problem = computed(() => {
    const error = this.overlay.error();
    return error ? problemOf(error) : null;
  });
  protected readonly message = problemMessage;

  /** Whether only the items with issues are listed. */
  protected readonly onlyIssues = signal(false);

  protected readonly relations = computed(() => this.listed(this.shown()?.relations ?? []));
  protected readonly virtualEntities = computed(() =>
    this.listed(this.shown()?.virtualEntities ?? []),
  );
  protected readonly entitySettings = computed(() =>
    this.listed(this.shown()?.entitySettings ?? []),
  );
  protected readonly navigations = computed(() => this.listed(this.shown()?.navigations ?? []));
  protected readonly empty = computed(() => {
    const overlay = this.shown();
    return (
      !!overlay &&
      overlay.relations.length +
        overlay.virtualEntities.length +
        overlay.entitySettings.length +
        overlay.navigations.length ===
        0
    );
  });

  /** What the catalog finds wrong, overall. */
  protected readonly summary = computed(() => {
    const overlay = this.shown();
    if (!overlay) {
      return null;
    }
    if (overlay.errors > 0) {
      const broken =
        overlay.errors === 1 ? "1 item doesn't work" : `${overlay.errors} items don't work`;
      const warnings = overlay.warnings > 0 ? `, and ${withWarnings(overlay.warnings)}` : '';
      return {
        kind: 'warning' as const,
        text: `${broken} as the catalog is now: it leaves out what is at fault${warnings}.`,
      };
    }
    if (overlay.warnings > 0) {
      return {
        kind: 'notice' as const,
        text: `Every item works; ${withWarnings(overlay.warnings)}.`,
      };
    }
    return null;
  });

  protected readonly relationText = relationText;
  protected readonly overrideText = overrideText;
  protected readonly settingsParts = settingsParts;

  protected toggleOnlyIssues(): void {
    this.onlyIssues.update((only) => !only);
  }

  private listed<T extends { readonly issues: readonly OverlayIssue[] }>(all: readonly T[]): T[] {
    return this.onlyIssues() ? all.filter((item) => item.issues.length > 0) : [...all];
  }
}
