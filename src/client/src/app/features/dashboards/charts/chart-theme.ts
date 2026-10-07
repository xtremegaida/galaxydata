/** The colors and font a chart is drawn with, resolved (a canvas can't read `var()` or `light-dark()`). */
export interface ChartTheme {
  readonly dark: boolean;
  readonly text: string;
  readonly muted: string;
  readonly grid: string;
  readonly surface: string;
  readonly tooltip: string;
  readonly tooltipText: string;
  /** The series' colors, in their order. */
  readonly series: readonly string[];
  /** For the rest of a pie, and series past the last color. */
  readonly neutral: string;
  readonly font: string;
}

const light: ChartTheme = {
  dark: false,
  text: '#1a1b1f',
  muted: '#44474e',
  grid: '#c4c6d0',
  surface: '#faf9fd',
  tooltip: '#2f3033',
  tooltipText: '#f1f0f4',
  series: ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#4a3aa7', '#e34948'],
  neutral: '#9a9ca3',
  font: 'Roboto Variable, Roboto, sans-serif',
};

const dark: ChartTheme = {
  dark: true,
  text: '#e3e2e6',
  muted: '#c4c6d0',
  grid: '#44474e',
  surface: '#121316',
  tooltip: '#e3e2e6',
  tooltipText: '#2f3033',
  series: ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300', '#9085e9', '#e66767'],
  neutral: '#74777f',
  font: 'Roboto Variable, Roboto, sans-serif',
};

/** The theme's colors where they can't be read (jsdom, or a color format a canvas wouldn't take). */
export function fallbackTheme(isDark: boolean): ChartTheme {
  return isDark ? dark : light;
}

/**
 * The page's theme as a chart in `host` would see it: each token's color read off a hidden probe inside the host
 * (so a scheme forced on it holds), as `rgb()`. What doesn't come back as `rgb()` is the fallback's.
 */
export function readTheme(host: HTMLElement, isDark: boolean): ChartTheme {
  const fallback = fallbackTheme(isDark);
  const view = host.ownerDocument.defaultView;
  if (!view) {
    return fallback;
  }
  const probe = host.ownerDocument.createElement('span');
  probe.style.display = 'none';
  host.append(probe);
  const read = (token: string, otherwise: string): string => {
    probe.style.color = `var(${token})`;
    const color = view.getComputedStyle(probe).color;
    return /^rgba?\(/.test(color) ? color : otherwise;
  };
  try {
    return {
      dark: isDark,
      text: read('--mat-sys-on-surface', fallback.text),
      muted: read('--mat-sys-on-surface-variant', fallback.muted),
      grid: read('--mat-sys-outline-variant', fallback.grid),
      surface: read('--mat-sys-surface', fallback.surface),
      tooltip: read('--mat-sys-inverse-surface', fallback.tooltip),
      tooltipText: read('--mat-sys-inverse-on-surface', fallback.tooltipText),
      series: fallback.series.map((color, i) => read(`--gd-chart-${i + 1}`, color)),
      neutral: read('--gd-chart-neutral', fallback.neutral),
      font: view.getComputedStyle(host).fontFamily || fallback.font,
    };
  } finally {
    probe.remove();
  }
}
