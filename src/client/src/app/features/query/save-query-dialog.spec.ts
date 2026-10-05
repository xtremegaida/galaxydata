import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import { settle } from '../../../testing/http';
import { alertsOf, clickButton, textOf } from '../../../testing/pages';
import { savedQueryOf } from '../../../testing/query';
import {
  type QueryDetails,
  type SaveOutcome,
  SaveQueryDialog,
  type SaveQueryData,
  type SavedQuery,
} from './save-query-dialog';

describe('SaveQueryDialog', () => {
  async function opened(save: (details: QueryDetails) => Promise<SaveOutcome>) {
    TestBed.configureTestingModule({
      providers: [{ provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } }],
    });
    const data: SaveQueryData = {
      title: 'Save the query',
      action: 'Save',
      details: { name: '', description: '', isShared: false },
      save,
    };
    const ref = TestBed.inject(MatDialog).open<SaveQueryDialog, SaveQueryData, SavedQuery>(
      SaveQueryDialog,
      { data },
    );
    const closed = firstValueFrom(ref.afterClosed());
    await settle();
    const dialog = document.querySelector('gd-save-query-dialog') as HTMLElement;
    const name = dialog.querySelector<HTMLInputElement>('input')!;
    const description = dialog.querySelector<HTMLTextAreaElement>('textarea')!;
    const typed = async (field: HTMLInputElement | HTMLTextAreaElement, text: string) => {
      field.value = text;
      field.dispatchEvent(new Event('input'));
      await settle();
    };
    const submit = async () => {
      clickButton(dialog, 'Save');
      await settle(5);
    };
    return { dialog, name, description, typed, submit, closed, ref };
  }

  it('asks for a name before saving, and one not too long', async () => {
    const asked: QueryDetails[] = [];
    const { dialog, name, typed, submit } = await opened((details) => {
      asked.push(details);
      return Promise.resolve({ saved: savedQueryOf() });
    });
    await submit();
    expect(asked).toEqual([]);
    expect(textOf(dialog.querySelector('mat-error'))).toBe('Name the query');
    expect(document.activeElement).toBe(name);
    await typed(name, ` ${'x'.repeat(201)} `);
    await submit();
    expect(textOf(dialog.querySelector('mat-error'))).toBe('A name has 200 characters at most');
    expect(asked).toEqual([]);
  });

  it("doesn't close while saving, as the query may be saved", async () => {
    let answered: (outcome: SaveOutcome) => void = () => undefined;
    const { name, typed, submit, closed, dialog } = await opened(
      () => new Promise((resolve) => (answered = resolve)),
    );
    await typed(name, 'Big orders');
    await submit();
    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    (document.querySelector('.cdk-overlay-backdrop') as HTMLElement | null)?.click();
    await settle();
    expect(document.querySelector('gd-save-query-dialog')).not.toBeNull();
    const saved = savedQueryOf();
    answered({ saved });
    expect(await closed).toBe(saved);
  });

  it('saves what is given, trimmed, and closes with the query saved', async () => {
    const asked: QueryDetails[] = [];
    const saved = savedQueryOf();
    const { name, description, dialog, typed, submit, closed } = await opened((details) => {
      asked.push(details);
      return Promise.resolve({ saved });
    });
    await typed(name, '  Big orders ');
    await typed(description, ' For the weekly review ');
    dialog.querySelector<HTMLInputElement>('mat-checkbox input')!.click();
    await settle();
    await submit();
    expect(asked).toEqual([
      { name: 'Big orders', description: 'For the weekly review', isShared: true },
    ]);
    expect(await closed).toBe(saved);
  });

  it('says what the server found wrong: at the fields it is about, and else above them', async () => {
    let answer: SaveOutcome = {
      problem: {
        status: 400,
        code: 'invalid-request',
        title: 'The request is invalid',
        errors: {
          'query.description': ['A description has 1000 characters at most'],
          other: ['Odd'],
        },
      },
    };
    const { name, description, dialog, typed, submit, ref } = await opened(() =>
      Promise.resolve(answer),
    );
    await typed(name, 'Big orders');
    await submit();
    expect(textOf(dialog.querySelector('mat-error'))).toBe(
      'A description has 1000 characters at most',
    );
    expect(alertsOf(dialog)).toContain('Odd');

    answer = {
      problem: {
        status: 403,
        code: 'forbidden',
        title: "Big orders is bob's",
        detail: 'Save a copy instead',
      },
    };
    // What the server found wrong stays till the field is changed.
    await submit();
    expect(textOf(dialog.querySelector('mat-error'))).toBe(
      'A description has 1000 characters at most',
    );
    await typed(description, 'Shorter');
    await submit();
    expect(alertsOf(dialog)).toContain("Big orders is bob's. Save a copy instead.");
    expect(ref.getState()).toBe(0);
    ref.close();
  });
});
