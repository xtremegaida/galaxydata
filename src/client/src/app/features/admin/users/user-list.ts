import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatAnchor, MatButton } from '@angular/material/button';
import { MatFormField, MatLabel, MatPrefix } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatSort, MatSortHeader, type Sort } from '@angular/material/sort';
import {
  MatCell,
  MatCellDef,
  MatColumnDef,
  MatHeaderCell,
  MatHeaderCellDef,
  MatHeaderRow,
  MatHeaderRowDef,
  MatRow,
  MatRowDef,
  MatTable,
} from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { ApiClient } from '../../../core/api/api-client';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { roleLabels } from '../../../core/auth/roles';
import { Message } from '../../../core/ui/message';
import { userStates, type User } from './users';

/** The users, to find one, or make one. */
@Component({
  selector: 'gd-user-list',
  imports: [
    DatePipe,
    MatAnchor,
    MatButton,
    MatCell,
    MatCellDef,
    MatColumnDef,
    MatFormField,
    MatHeaderCell,
    MatHeaderCellDef,
    MatHeaderRow,
    MatHeaderRowDef,
    MatIcon,
    MatInput,
    MatLabel,
    MatPrefix,
    MatProgressBar,
    MatRow,
    MatRowDef,
    MatSort,
    MatSortHeader,
    MatTable,
    Message,
    RouterLink,
  ],
  templateUrl: './user-list.html',
  styleUrl: '../admin-page.scss',
})
export class UserList {
  private readonly api = inject(ApiClient);

  protected readonly users = rxResource({ stream: () => this.api.get('/api/users') });
  protected readonly find = signal('');
  protected readonly sort = signal<Sort>({ active: 'userName', direction: 'asc' });
  protected readonly columns = ['userName', 'displayName', 'role', 'state', 'lastSignInAt'];
  protected readonly states = userStates;
  protected readonly message = problemMessage;

  protected readonly problem = computed(() => {
    const error = this.users.error();
    return error ? problemOf(error) : null;
  });

  protected roleOf(user: User): string {
    return roleLabels[user.role];
  }

  /** The users found, in the order asked for. */
  protected readonly rows = computed(() => {
    const users = this.users.hasValue() ? this.users.value() : [];
    const words = this.find().trim().toLowerCase();
    const found = words
      ? users.filter((user) =>
          [user.userName, user.displayName ?? ''].some((name) =>
            name.toLowerCase().includes(words),
          ),
        )
      : users;
    return sorted(found, this.sort());
  });
}

function sorted(users: readonly User[], sort: Sort): User[] {
  if (!sort.direction) {
    return [...users];
  }
  const key = sort.active as keyof User;
  const sign = sort.direction === 'asc' ? 1 : -1;
  return [...users].sort((a, b) => {
    const x = a[key] ?? '';
    const y = b[key] ?? '';
    return sign * String(x).localeCompare(String(y), undefined, { sensitivity: 'base' });
  });
}
