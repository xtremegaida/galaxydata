import type { PaletteColor, PaletteDefinition } from './series-colors';

/**
 * What a palette's colours are checked by, as the dataviz method checks palettes: WCAG's contrast against the
 * surfaces charts sit on (3:1 for marks), and how far apart colours are (OKLab's ΔE, ×100), seen as they are and as
 * the commonest colour blindness sees them (Machado, Oliveira and Fernandes, 2009, at full severity). Warnings only:
 * charts have labels, legends and tables besides their colours.
 */

/** The surfaces a chart sits on, light and dark: the page's, and a widget's frame. */
export const chartSurfaces = {
  light: ['#faf9fd', '#f4f3f6'],
  dark: ['#121316', '#1a1b1f'],
} as const;

/** Marks stand out from their surface by this much at least. */
export const markContrast = 3;

/** Colours this far apart (ΔE ×100) are told apart with full colour vision. */
export const apartSeen = 15;

/** Colours this far apart seen as red–green colour blindness sees them are told apart. */
export const apartBlind = 6;

/** Colours with less chroma (OKLCH's) read as grey, as "Other" is. */
export const greyChroma = 0.1;

const machado = {
  protan: [
    [0.152286, 1.052583, -0.204868],
    [0.114503, 0.786281, 0.099216],
    [-0.003882, -0.048116, 1.051998],
  ],
  deutan: [
    [0.367322, 0.860646, -0.227968],
    [0.280085, 0.672501, 0.047413],
    [-0.01182, 0.04294, 0.968881],
  ],
} as const;

function linear(hex: string): [number, number, number] {
  const channel = (i: number) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return [channel(1), channel(3), channel(5)];
}

/** WCAG's contrast of two colours (#rrggbb). */
export function contrast(a: string, b: string): number {
  const luminance = (hex: string) => {
    const [r, g, bl] = linear(hex);
    return 0.2126 * r + 0.7152 * g + 0.0722 * bl;
  };
  const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (high + 0.05) / (low + 0.05);
}

function oklab([r, g, b]: readonly number[]): [number, number, number] {
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  return [
    0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s,
    1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s,
    0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s,
  ];
}

function seen(hex: string, vision: keyof typeof machado | null): number[] {
  const rgb = linear(hex);
  if (!vision) {
    return rgb;
  }
  return machado[vision].map((row) =>
    Math.max(0, Math.min(1, row[0] * rgb[0] + row[1] * rgb[1] + row[2] * rgb[2])),
  );
}

/** How far apart two colours are (OKLab, ×100), as they are or as a colour blindness sees them. */
export function deltaE(a: string, b: string, vision: keyof typeof machado | null = null): number {
  const [x, y] = [oklab(seen(a, vision)), oklab(seen(b, vision))];
  return 100 * Math.hypot(x[0] - y[0], x[1] - y[1], x[2] - y[2]);
}

/** A colour's chroma (OKLCH's): how far from grey it is. */
export function chroma(hex: string): number {
  const [, a, b] = oklab(linear(hex));
  return Math.hypot(a, b);
}

/** How far apart two colours are as red–green colour blindness sees them: the nearer of its two kinds. */
export function deltaEBlind(a: string, b: string): number {
  return Math.min(deltaE(a, b, 'protan'), deltaE(a, b, 'deutan'));
}

export interface ColorWarning {
  readonly scheme: 'light' | 'dark';
  readonly kind: 'contrast' | 'apart' | 'blind' | 'grey';
  /** The colours it is of, by their place in the palette (from 0). */
  readonly colors: readonly number[];
  readonly message: string;
}

/**
 * What may be hard to see in a palette's colours, in each scheme: colours that stand out little from a chart's
 * surface, colours that read as grey (as "Other" is), and colours hard to tell apart (neighbours, when colours go by order;
 * any two, by label, as any two labels may meet in a chart).
 */
export function colorWarnings(
  colors: readonly PaletteColor[],
  assign: PaletteDefinition['assign'],
): ColorWarning[] {
  const warnings: ColorWarning[] = [];
  const named = (indexes: readonly number[], hexes: readonly string[]) =>
    list(indexes.map((i) => `${i + 1} (${hexes[i]})`));
  for (const scheme of ['light', 'dark'] as const) {
    const hexes = colors.map((c) =>
      (scheme === 'dark' ? (c.dark ?? c.light) : c.light).toLowerCase(),
    );
    const where = scheme === 'dark' ? 'dark backgrounds' : 'light backgrounds';
    const low = hexes
      .map((hex, i) => ({
        i,
        worst: Math.min(...chartSurfaces[scheme].map((s) => contrast(hex, s))),
      }))
      .filter((c) => c.worst < markContrast)
      .map((c) => c.i);
    if (low.length > 0) {
      warnings.push({
        scheme,
        kind: 'contrast',
        colors: low,
        message: `${low.length === 1 ? 'Colour' : 'Colours'} ${named(low, hexes)} stand${low.length === 1 ? 's' : ''} out less than 3:1 from ${where}: charts' labels and tables say what they show.`,
      });
    }
    const grey = hexes
      .map((hex, i) => ({ i, chroma: chroma(hex) }))
      .filter((c) => c.chroma < greyChroma)
      .map((c) => c.i);
    if (grey.length > 0) {
      warnings.push({
        scheme,
        kind: 'grey',
        colors: grey,
        message: `${grey.length === 1 ? 'Colour' : 'Colours'} ${named(grey, hexes)} ${grey.length === 1 ? 'reads' : 'read'} as grey, as "Other" is, on ${where}.`,
      });
    }
    const pairs: [number, number][] = [];
    for (let i = 0; i < hexes.length; i++) {
      for (let j = i + 1; j < hexes.length; j++) {
        if (assign === 'label' || j === i + 1) {
          pairs.push([i, j]);
        }
      }
    }
    const apart = pairs.filter(([i, j]) => deltaE(hexes[i], hexes[j]) < apartSeen);
    const blind = pairs.filter(
      ([i, j]) =>
        deltaE(hexes[i], hexes[j]) >= apartSeen && deltaEBlind(hexes[i], hexes[j]) < apartBlind,
    );
    const which = assign === 'label' ? 'Any two may meet in a chart (by label)' : 'Neighbours';
    if (apart.length > 0) {
      warnings.push({
        scheme,
        kind: 'apart',
        colors: [...new Set(apart.flat())],
        message: `${which}: ${list(apart.map(([i, j]) => `${i + 1} and ${j + 1}`))} are hard to tell apart on ${where}.`,
      });
    }
    if (blind.length > 0) {
      warnings.push({
        scheme,
        kind: 'blind',
        colors: [...new Set(blind.flat())],
        message: `${which}: ${list(blind.map(([i, j]) => `${i + 1} and ${j + 1}`))} are hard to tell apart with red–green colour blindness, on ${where}.`,
      });
    }
  }
  return warnings;
}

function list(items: readonly string[]): string {
  return items.length <= 1
    ? (items[0] ?? '')
    : `${items.slice(0, -1).join(', ')} and ${items.at(-1)}`;
}
