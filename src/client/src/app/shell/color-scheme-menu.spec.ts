import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { MatMenuHarness } from '@angular/material/menu/testing';
import { ColorScheme } from '../core/theme/color-scheme';
import { ColorSchemeMenu } from './color-scheme-menu';

describe('ColorSchemeMenu', () => {
  afterEach(() => {
    localStorage.clear();
    document.documentElement.style.colorScheme = '';
  });

  it('shows the color scheme chosen from its menu', async () => {
    const fixture = TestBed.createComponent(ColorSchemeMenu);
    const menu = await TestbedHarnessEnvironment.loader(fixture).getHarness(MatMenuHarness);
    await menu.open();
    const items = await menu.getItems();
    expect(await Promise.all(items.map((item) => item.getText()))).toEqual([
      'brightness_autoSystem',
      'light_modeLight',
      'dark_modeDark',
    ]);

    await menu.clickItem({ text: /Dark$/ });
    expect(TestBed.inject(ColorScheme).choice()).toBe('dark');
    expect(document.documentElement.style.colorScheme).toBe('dark');
    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('[aria-label="Color scheme"] mat-icon')?.textContent?.trim()).toBe(
      'dark_mode',
    );

    await menu.open();
    const checked = await Promise.all(
      (await menu.getItems()).map(async (item) => (await item.host()).getAttribute('aria-checked')),
    );
    expect(checked).toEqual(['false', 'false', 'true']);
  });
});
