import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import { problemMessage, type Problem } from '../../../core/api/problem';
import { Message } from '../../../core/ui/message';
import {
  itemLink,
  itemTitle,
  itemsOf,
  severityIcons,
  severityWords,
  worstOf,
  type OverlayCheck,
  type OverlayIssue,
} from './overlay-items';

/** An item's issues, each with its severity's icon and word, its message and its code. */
@Component({
  selector: 'gd-overlay-issues',
  imports: [MatIcon],
  template: `
    <ul class="issues">
      @for (issue of issues(); track $index) {
        <li class="issue" [class]="issue.severity">
          <mat-icon class="icon" aria-hidden="true">{{ icons[issue.severity] }}</mat-icon>
          <span>
            <span class="cdk-visually-hidden">{{ words[issue.severity] }}:</span>
            {{ issue.message }}
            <span class="code">{{ issue.code }}</span>
          </span>
        </li>
      }
    </ul>
  `,
  styleUrl: './overlay-issues.scss',
})
export class OverlayIssues {
  readonly issues = input.required<readonly OverlayIssue[]>();

  protected readonly icons = severityIcons;
  protected readonly words = severityWords;
}

/**
 * What the catalog makes of an item as it is edited, tried without saving it: whether it works, its issues, and the
 * other items it would break (named as the overlay names them, read when there are some). What the kind adds (a
 * relation's navigations, the entity made) goes inside it.
 */
@Component({
  selector: 'gd-overlay-check',
  imports: [MatButton, MatProgressBar, Message, OverlayIssues, RouterLink],
  template: `
    <section class="section check" aria-labelledby="gd-overlay-check">
      <h2 id="gd-overlay-check" tabindex="-1">What the catalog makes of it</h2>
      @if (checking()) {
        <mat-progress-bar mode="indeterminate" aria-label="Trying it" />
      }
      <div role="status">
        @if (waiting(); as waiting) {
          <p class="aside">{{ waiting }}</p>
        } @else if (reasons().length > 0) {
          <gd-message kind="problem">
            It can't be tried as it is:
            <ul class="reasons">
              @for (reason of reasons(); track $index) {
                <li>{{ reason }}</li>
              }
            </ul>
          </gd-message>
        } @else if (problem(); as problem) {
          <gd-message kind="problem">
            It couldn't be tried: {{ message(problem) }}
            <button gdMessageAction matButton type="button" (click)="tryAgain()">Try again</button>
          </gd-message>
        } @else if (check(); as check) {
          <p class="verdict" [class]="worst() ?? 'fine'">{{ verdict() }}</p>
        }
      </div>
      @if (!waiting() && !problem() && check(); as check) {
        @if (check.issues.length > 0) {
          <gd-overlay-issues [issues]="check.issues" />
        }
        @if (breaks().length > 0) {
          <h3>It would break what works now</h3>
          <ul class="breaks">
            @for (broken of breaks(); track broken.kind + broken.id) {
              <li>
                <a [routerLink]="broken.link">{{ broken.name }}</a>
                <gd-overlay-issues [issues]="broken.issues" />
              </li>
            }
          </ul>
        }
        <ng-content />
      }
    </section>
  `,
  styleUrls: ['../admin-page.scss', './overlay-issues.scss'],
})
export class OverlayCheckPanel {
  private readonly api = inject(ApiClient);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** What trying the item gave, for the item as it is now; null before that. */
  readonly check = input<OverlayCheck | null>(null);
  /** Whether it is being tried (again). */
  readonly checking = input(false);
  /** Why it couldn't be tried. */
  readonly problem = input<Problem | null>(null);
  /** What it needs before it can be tried ("Name the entity"), said in place of what came of it. */
  readonly waiting = input<string | null>(null);
  /** What the server refused in it, when that is why it couldn't be tried (trying it again would fail alike). */
  readonly reasons = input<readonly string[]>([]);
  /** The problem's Try again. */
  readonly retry = output<void>();

  protected readonly message = problemMessage;
  protected readonly worst = computed(() => worstOf(this.check()?.issues ?? []));
  protected readonly verdict = computed(() => {
    switch (this.worst()) {
      case 'error':
        return "It doesn't work as it is: the catalog leaves it out, or the part of it at fault.";
      case 'warning':
        return 'It works, with warnings.';
      default:
        return 'It works.';
    }
  });

  /** Tries it again: the button goes with its message, so the keyboard goes to the heading. */
  protected tryAgain(): void {
    this.retry.emit();
    afterNextRender(
      () => this.host.nativeElement.querySelector<HTMLElement>('#gd-overlay-check')?.focus(),
      { injector: this.injector },
    );
  }

  /** The overlay, to name the items it would break; read only when there are some. */
  private readonly overlay = rxResource({
    params: () => ((this.check()?.breaks?.length ?? 0) > 0 ? true : undefined),
    stream: () => this.api.get('/api/overlay').pipe(catchError(() => of(null))),
  });

  protected readonly breaks = computed(() => {
    const overlay = this.overlay.hasValue() ? this.overlay.value() : null;
    return (this.check()?.breaks ?? []).map((broken) => {
      const item = overlay
        ? itemsOf(overlay, broken.kind).find((each) => each.id === broken.id)
        : undefined;
      return {
        ...broken,
        link: itemLink(broken.kind, broken.id),
        name: itemTitle(broken.kind, item, broken.id),
      };
    });
  });
}
