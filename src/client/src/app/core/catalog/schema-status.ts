import { DatePipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { MatIcon } from '@angular/material/icon';
import { MatProgressSpinner } from '@angular/material/progress-spinner';
import type { Schema } from '../api/api-client';

/** Whether a connection's schema has been read: not yet, being read, read (when), or not (why). */
@Component({
  selector: 'gd-schema-status',
  imports: [DatePipe, MatIcon, MatProgressSpinner],
  template: `
    @switch (status()) {
      @case ('loading') {
        <mat-progress-spinner mode="indeterminate" diameter="16" aria-hidden="true" />
        <span>Reading the schema…</span>
      }
      @case ('ready') {
        <mat-icon class="ready">check_circle</mat-icon>
        <span>Read {{ refreshedAt() | date: 'medium' }}</span>
      }
      @case ('failed') {
        <mat-icon class="failed">error</mat-icon>
        <span>{{
          detailed() && error() ? "Couldn't be read: " + error() : "Couldn't be read"
        }}</span>
      }
      @default {
        <mat-icon>schedule</mat-icon>
        <span>Not read yet</span>
      }
    }
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }

    mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }

    .ready {
      color: var(--mat-sys-primary);
    }

    .failed {
      color: var(--mat-sys-error);
    }
  `,
})
export class SchemaStatus {
  readonly status = input.required<Schema<'SchemaStatus'>>();
  readonly error = input<string | null>(null);
  readonly refreshedAt = input<string | null>(null);
  /** Whether to say why it couldn't be read. */
  readonly detailed = input(false);
}
