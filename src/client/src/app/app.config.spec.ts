import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatIcon } from '@angular/material/icon';
import { appConfig } from './app.config';

@Component({
  imports: [MatIcon],
  template: '<mat-icon>home</mat-icon><mat-icon fontIcon="search" />',
})
class Icons {}

describe('appConfig', () => {
  it('shows icons by their names in Material Symbols, as text or fontIcon', async () => {
    TestBed.configureTestingModule({ imports: [Icons], providers: appConfig.providers });
    const fixture = TestBed.createComponent(Icons);
    await fixture.whenStable();
    const [byText, byFontIcon] = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('mat-icon'),
    );
    for (const icon of [byText, byFontIcon]) {
      expect(icon.classList).toContain('material-symbols-outlined');
      expect(icon.classList).toContain('mat-ligature-font');
    }
    expect(byFontIcon.getAttribute('fontIcon')).toBe('search');
    expect(byFontIcon.classList).not.toContain('search');
  });
});
