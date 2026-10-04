import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { isSessionProblem, problemMessage, type Problem } from '../api/problem';

/** Tells the user what happened, at the foot of the page. */
@Injectable({ providedIn: 'root' })
export class Notifier {
  private readonly snackBar = inject(MatSnackBar);

  /** Tells the user something, for a few seconds. */
  say(message: string): void {
    this.snackBar.open(message, 'Close', { duration: 5000 });
  }

  /**
   * Tells the user of a problem, until they close it; but not of the session's, which the client deals with
   * itself (signing in again).
   */
  problem(problem: Problem): void {
    if (!isSessionProblem(problem)) {
      this.snackBar.open(problemMessage(problem), 'Close', { politeness: 'assertive' });
    }
  }
}
