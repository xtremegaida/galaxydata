import { galaxyColors } from '../charts/chart-theme';
import type { PaletteColor, PaletteDefinition } from '../charts/series-colors';

/** What a new palette may start from: its colours, light and dark. */
export interface PaletteBase {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly colors: readonly PaletteColor[];
}

/**
 * The bases a palette starts from, each checked as the dataviz method checks palettes (by order, in both schemes:
 * `palettes.spec.ts`). Okabe and Ito's colours for colour blindness come without their black (unseen on dark
 * backgrounds) and yellow (too light on light ones, and near vermillion once darkened), and with darker steps of
 * orange, sky blue and reddish purple for dark backgrounds.
 */
export const paletteBases: readonly PaletteBase[] = [
  {
    id: 'galaxydata',
    name: 'GalaxyData',
    description: "The application's eight, which charts have without a palette",
    colors: galaxyColors,
  },
  {
    id: 'okabe-ito',
    name: 'Okabe–Ito',
    description: 'Six colours chosen to be told apart with colour blindness',
    colors: [
      { light: '#e69f00', dark: '#c08400' },
      { light: '#56b4e9', dark: '#3a95cc' },
      { light: '#009e73', dark: null },
      { light: '#0072b2', dark: null },
      { light: '#d55e00', dark: null },
      { light: '#cc79a7', dark: '#bb6496' },
    ],
  },
  {
    id: 'blank',
    name: 'Blank',
    description: 'One colour, to make your own from',
    colors: [{ light: '#2a78d6', dark: '#3987e5' }],
  },
];

/** A new palette's ways: by label, kept apart in a chart, repeating, ignoring case. */
export function newPalette(colors: readonly PaletteColor[]): PaletteDefinition {
  return {
    colors: colors.map((c) => ({ ...c })),
    assign: 'label',
    distinct: true,
    whenOut: 'repeat',
    matching: {
      ignoreCase: true,
      ignoreWhitespace: false,
      ignoreBrackets: false,
      ignoreAccents: false,
    },
    overrides: [],
  };
}
