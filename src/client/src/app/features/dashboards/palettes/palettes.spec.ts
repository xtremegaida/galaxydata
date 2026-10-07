import { galaxyColors } from '../charts/chart-theme';
import { colorWarnings, contrast, deltaE, deltaEBlind } from '../charts/color-checks';
import { paletteBases } from './bases';

describe("a palette's colour checks", () => {
  /** The nearest neighbours of a list of colours, as seen and with red–green colour blindness. */
  function nearest(hexes: readonly string[]): { seen: number; blind: number } {
    const pairs = hexes.slice(1).map((hex, i) => [hexes[i], hex] as const);
    return {
      seen: Math.min(...pairs.map(([a, b]) => deltaE(a, b))),
      blind: Math.min(...pairs.map(([a, b]) => deltaEBlind(a, b))),
    };
  }

  it("measures as the dataviz skill's validator does", () => {
    // Its figures for the application's eight, by order (adjacent pairs).
    const lightWorst = nearest(galaxyColors.map((c) => c.light));
    expect(lightWorst.seen).toBeCloseTo(19.6, 1);
    expect(lightWorst.blind).toBeCloseTo(9.1, 1);
    const darkWorst = nearest(galaxyColors.map((c) => c.dark));
    expect(darkWorst.seen).toBeCloseTo(19.3, 1);
    expect(darkWorst.blind).toBeCloseTo(8.4, 1);
    // And for two of Okabe and Ito's.
    expect(deltaE('#cc79a7', '#d55e00')).toBeCloseTo(16.4, 1);
    expect(deltaE('#cc79a7', '#d55e00', 'deutan')).toBeCloseTo(15.8, 1);
    expect(contrast('#e69f00', '#faf9fd')).toBeCloseTo(2.15, 2);
    expect(contrast('#000000', '#ffffff')).toBeCloseTo(21, 5);
  });

  it('passes for every base by order, in both schemes, but for contrast on light surfaces', () => {
    for (const base of paletteBases) {
      const warnings = colorWarnings(base.colors, 'order');
      expect(
        warnings.filter((w) => w.kind !== 'contrast' || w.scheme === 'dark'),
        base.name,
      ).toEqual([]);
    }
    // The application's eight: four of them under 3:1 on light, as styles.scss says (orange, on a widget's frame).
    expect(colorWarnings(galaxyColors, 'order').map((w) => [w.scheme, w.kind, w.colors])).toEqual([
      ['light', 'contrast', [1, 2, 3, 4]],
    ]);
  });

  it('says which colours are hard to tell apart: neighbours by order, any two by label', () => {
    const near = [
      { light: '#2a78d6', dark: null },
      { light: '#2b7ad8', dark: '#e34948' },
      { light: '#e34948', dark: null },
    ];
    const byOrder = colorWarnings(near, 'order');
    expect(byOrder.map((w) => [w.scheme, w.kind, w.colors])).toEqual([
      ['light', 'apart', [0, 1]],
      ['dark', 'apart', [1, 2]],
    ]);
    expect(byOrder[0].message).toBe(
      'Neighbours: 1 and 2 are hard to tell apart on light backgrounds.',
    );
    // By label any two may meet: the first and last are a pair too.
    const byLabel = colorWarnings(
      [
        { light: '#2a78d6', dark: null },
        { light: '#e34948', dark: null },
        { light: '#2b7ad8', dark: null },
      ],
      'label',
    );
    expect(byLabel.filter((w) => w.kind === 'apart').map((w) => w.message)).toEqual([
      'Any two may meet in a chart (by label): 1 and 3 are hard to tell apart on light backgrounds.',
      'Any two may meet in a chart (by label): 1 and 3 are hard to tell apart on dark backgrounds.',
    ]);
    // Red and green, as many with colour blindness see them.
    expect(
      colorWarnings(
        [
          { light: '#d03b3b', dark: null },
          { light: '#3b8a3b', dark: null },
        ],
        'order',
      ).map((w) => w.kind),
    ).toContain('blind');
    // Near the grey of "Other".
    expect(colorWarnings([{ light: '#9a9ca4', dark: null }], 'order').map((w) => w.kind)).toContain(
      'grey',
    );
  });
});
