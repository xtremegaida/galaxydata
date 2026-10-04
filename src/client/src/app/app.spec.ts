import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { MatMenuHarness } from '@angular/material/menu/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { ColorScheme } from './core/theme/color-scheme';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  afterEach(() => {
    localStorage.clear();
    document.documentElement.style.colorScheme = '';
  });

  it('names the application', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('h1')?.textContent).toBe('GalaxyData');
  });

  it('shows the color scheme chosen from its menu', async () => {
    const fixture = TestBed.createComponent(App);
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
