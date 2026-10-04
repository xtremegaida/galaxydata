import { themeQuartz } from 'ag-grid-community';

/**
 * The grid in the application's colours and type: Material's system tokens, which follow the colour scheme chosen
 * (light, dark or the system's), so the grid follows it too. No web fonts are loaded (the page's own are used).
 */
export const gridTheme = themeQuartz.withParams({
  fontFamily: 'inherit',
  headerFontFamily: 'inherit',
  fontSize: 14,
  headerFontWeight: 500,
  browserColorScheme: 'inherit',
  backgroundColor: 'var(--mat-sys-surface)',
  foregroundColor: 'var(--mat-sys-on-surface)',
  textColor: 'var(--mat-sys-on-surface)',
  subtleTextColor: 'var(--mat-sys-on-surface-variant)',
  accentColor: 'var(--mat-sys-primary)',
  borderColor: 'var(--mat-sys-outline-variant)',
  chromeBackgroundColor: 'var(--mat-sys-surface-container-low)',
  headerBackgroundColor: 'var(--mat-sys-surface-container)',
  headerTextColor: 'var(--mat-sys-on-surface)',
  menuBackgroundColor: 'var(--mat-sys-surface-container)',
  menuTextColor: 'var(--mat-sys-on-surface)',
  tooltipBackgroundColor: 'var(--mat-sys-inverse-surface)',
  tooltipTextColor: 'var(--mat-sys-inverse-on-surface)',
  invalidColor: 'var(--mat-sys-error)',
  borderRadius: 4,
  wrapperBorderRadius: 0,
});
