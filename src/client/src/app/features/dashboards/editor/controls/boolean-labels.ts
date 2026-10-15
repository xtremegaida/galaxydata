import { ChangeDetectionStrategy, Component, model } from '@angular/core';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import type { BooleanLabels } from '../../model/definition';

/**
 * How a true/false field's values show: a label for each (Bounced, Went on), yes or no where it is left empty; none
 * kept when both are.
 */
@Component({
  selector: 'gd-boolean-labels',
  imports: [MatFormField, MatInput, MatLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="labels" role="group" aria-label="How yes and no show">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Yes shows as</mat-label>
        <input
          matInput
          maxlength="60"
          placeholder="Yes"
          [value]="labels()?.true ?? ''"
          (input)="typed('true', $event)"
          autocomplete="off"
        />
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>No shows as</mat-label>
        <input
          matInput
          maxlength="60"
          placeholder="No"
          [value]="labels()?.false ?? ''"
          (input)="typed('false', $event)"
          autocomplete="off"
        />
      </mat-form-field>
    </div>
  `,
  styles: `
    .labels {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 8px;
    }
  `,
})
export class BooleanLabelsEditor {
  readonly labels = model<BooleanLabels | null | undefined>(null);

  protected typed(which: 'true' | 'false', event: Event): void {
    const labels: BooleanLabels = {
      true: this.labels()?.true ?? '',
      false: this.labels()?.false ?? '',
      [which]: (event.target as HTMLInputElement).value,
    };
    this.labels.set(labels.true.trim() || labels.false.trim() ? labels : null);
  }
}
