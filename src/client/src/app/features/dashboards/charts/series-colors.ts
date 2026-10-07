import type { Schema } from '../../../core/api/api-client';
import type { ChartConfig, WidgetData } from '../model/definition';
import { keyText } from '../model/definition';
import { type ChartTheme, galaxyColors } from './chart-theme';
import { categoryText } from './chart-values';

export type PaletteDefinition = Schema<'PaletteDefinition'>;
export type PaletteColor = Schema<'PaletteColor'>;
export type LabelMatching = Schema<'LabelMatching'>;
/** A label's colour as an answer gives it (embedded: its palette's overrides of the labels in it). */
export type LabelColor = Schema<'LabelColorDto'>;

/**
 * A palette as charts are drawn with it: signed in, whole; embedded, without its overrides (each answer carries
 * those of its own labels). `basis` changes with it, and what a chart has coloured is coloured again then.
 */
export interface PaletteView {
  readonly id: number;
  readonly basis: string;
  readonly colors: readonly PaletteColor[];
  readonly assign: PaletteDefinition['assign'];
  readonly distinct: boolean;
  readonly whenOut: PaletteDefinition['whenOut'];
  readonly matching: LabelMatching;
  readonly overrides: PaletteDefinition['overrides'];
}

/** A palette as a dashboard's answer, or the palettes' own, gives it (signed in): whole. */
export function chartPalette(dto: {
  readonly id: number;
  readonly hash: string;
  readonly definition: PaletteDefinition;
}): PaletteView {
  return { id: dto.id, basis: dto.hash, ...dto.definition };
}

/** A palette a public dashboard's answer gives: without overrides, which its rows' answers carry. */
export function publicPalette(dto: Schema<'PublicPaletteDto'>): PaletteView {
  return { ...dto, basis: JSON.stringify(dto), overrides: [] };
}

/**
 * Unicode's white space, and the byte order mark: named, as the server names them (`LabelText`), since JavaScript's
 * `\s` and .NET's white space aren't the same.
 */
const spaces = new Set([
  0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004,
  0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000, 0xfeff,
]);

/**
 * The text a label is matched by, as the server works it out (`LabelText.Normalize`; one fixture holds both to it):
 * composed; text in brackets left out (nested, each closing its own kind; one never closed, or closed without
 * being opened, is text); accents left out; each code point's simple lowercase; white space left out, or each run
 * of it one space; trimmed.
 */
export function normalizeLabel(text: string, matching: LabelMatching): string {
  let normalized = text.normalize('NFC');
  if (matching.ignoreBrackets) {
    normalized = withoutBrackets(normalized);
  }
  if (matching.ignoreAccents) {
    normalized = normalized
      .normalize('NFD')
      .replace(/\p{Mn}/gu, '')
      .normalize('NFC');
  }
  let kept = '';
  let space = false;
  for (const c of normalized) {
    if (spaces.has(c.codePointAt(0)!)) {
      space = true;
      continue;
    }
    if (space && !matching.ignoreWhitespace && kept.length > 0) {
      kept += ' ';
    }
    space = false;
    // A code point's own lowercase, not the string's (no final sigma), and İ's first (i, not i̇).
    kept += matching.ignoreCase ? String.fromCodePoint(c.toLowerCase().codePointAt(0)!) : c;
  }
  return kept;
}

function withoutBrackets(text: string): string {
  const closers: Record<string, string> = { '(': ')', '[': ']', '{': '}' };
  const open: { close: string; at: number }[] = [];
  const dropped = new Array<boolean>(text.length).fill(false);
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (closers[c]) {
      open.push({ close: closers[c], at: i });
    } else if ((c === ')' || c === ']' || c === '}') && open.at(-1)?.close === c) {
      dropped.fill(true, open.pop()!.at, i + 1);
    }
  }
  let kept = '';
  for (let i = 0; i < text.length; i++) {
    if (!dropped[i]) {
      kept += text[i];
    }
  }
  return kept;
}

/** A value's text, as answers carry it (and the server matches it): text as it is, numbers and booleans as JavaScript writes them; none for no value. */
export function labelText(value: unknown): string | null {
  if (value === null || value === undefined) {
    return null;
  }
  if (typeof value === 'string') {
    return value;
  }
  if (typeof value === 'number' || typeof value === 'boolean') {
    return String(value);
  }
  return JSON.stringify(value);
}

const mask = (1n << 64n) - 1n;

/** FNV-1a's 64-bit hash of the text's UTF-8. */
export function fnv1a64(text: string): bigint {
  let hash = 0xcbf29ce484222325n;
  for (const byte of new TextEncoder().encode(text)) {
    hash ^= BigInt(byte);
    hash = (hash * 0x100000001b3n) & mask;
  }
  return hash;
}

/**
 * Lamping and Veach's jump consistent hash: a bucket of `buckets` for the key, the same as the buckets grow but for
 * the keys the new bucket takes (about one in so many).
 */
export function jumpHash(key: bigint, buckets: number): number {
  let b = -1;
  let j = 0;
  let k = key & mask;
  while (j < buckets) {
    b = j;
    k = (k * 2862933555777941757n + 1n) & mask;
    j = Math.floor((b + 1) * (2147483648 / (Number(k >> 33n) + 1)));
  }
  return b;
}

/** What a chart colours: its entity (as the chart's options name it), its label's text (none: no value), and as it shows. */
export interface ColoredEntity {
  readonly entity: string;
  readonly label: string | null;
  readonly shown: string;
}

/** Where an entity's colour came from: an override, its label, its order, moved off one taken (distinct), grey, or no palette. */
export type ColorSource = 'override' | 'label' | 'order' | 'moved' | 'neutral' | 'default';

export interface EntityColor extends ColoredEntity {
  /** As it is painted now (the scheme's). */
  readonly color: string;
  readonly source: ColorSource;
  /** The colour it is, light and dark (as a palette writes it); none for grey. */
  readonly pair: PaletteColor | null;
}

/** The colours a chart's options read, by entity. */
export interface SeriesColors {
  color(entity: string, theme: ChartTheme): string;
}

/**
 * What a chart colours, in the order it meets them (so the colours by order are those it always had): a pie's
 * slices ("Other" is grey, and isn't listed); bars' and lines' series values, or else their measures; a bar chart's
 * categories when it is coloured by them.
 */
export function coloredEntities(
  config: ChartConfig,
  data: WidgetData,
  locale: string,
): ColoredEntity[] {
  const d = data.columns.findIndex((c) => c.role === 'dimension' && c.index === 0);
  const s = data.columns.findIndex((c) => c.role === 'series' && c.index === 0);
  const values = (
    index: number,
    prefix: string,
    bucket: ChartConfig['dimension']['bucket'] | undefined,
  ) => {
    const seen = new Set<string>();
    const listed: ColoredEntity[] = [];
    for (const row of data.rows) {
      const entity = prefix + keyText([row[index]]);
      if (!seen.has(entity)) {
        seen.add(entity);
        listed.push({
          entity,
          label: labelText(row[index]),
          shown: categoryText(row[index], data.columns[index].type, bucket ?? null, locale),
        });
      }
    }
    return listed;
  };
  const measures = () =>
    data.columns
      .filter((c) => c.role === 'measure')
      .map((c) => ({ entity: 'measure:' + c.label, label: c.label, shown: c.label }));
  if (config.kind === 'pie') {
    return d < 0 ? [] : values(d, '', config.dimension.bucket);
  }
  if (s >= 0) {
    return values(s, 'series:', config.series?.bucket);
  }
  return byCategory(config, data) && d >= 0
    ? values(d, 'category:', config.dimension.bucket)
    : measures();
}

/** Whether a bar chart's bars take their categories' colours: asked to, with one measure and no series. */
function byCategory(config: ChartConfig, data: WidgetData): boolean {
  return (
    config.kind === 'bar' &&
    config.colorBy === 'category' &&
    data.columns.filter((c) => c.role === 'measure').length === 1 &&
    !data.columns.some((c) => c.role === 'series')
  );
}

/**
 * The entity a cell of a chart's table shows the colour of (`row` null: a column's header): slices, series values
 * and bars' categories by row, measures by their column.
 */
export function cellEntity(
  config: ChartConfig,
  data: WidgetData,
  row: readonly unknown[] | null,
  column: number,
): string | null {
  const shown = data.columns[column];
  if (!shown) {
    return null;
  }
  if (config.kind === 'pie') {
    return row && shown.role === 'dimension' ? keyText([row[column]]) : null;
  }
  if (data.columns.some((c) => c.role === 'series')) {
    return row && shown.role === 'series' ? 'series:' + keyText([row[column]]) : null;
  }
  if (byCategory(config, data)) {
    return row && shown.role === 'dimension' ? 'category:' + keyText([row[column]]) : null;
  }
  return !row && shown.role === 'measure' ? 'measure:' + shown.label : null;
}

/** No value's key among a palette's labels, which no text normalises to. */
const none = '\u0000';

/**
 * A widget's colours from a palette, kept while it is shown: what has a colour keeps it (a filter that leaves some
 * out doesn't paint the others anew). Overrides first (they take no slot); then by order (slots as entities are
 * first met) or by label (a jump hash of the label's text, so a label has its colour everywhere); distinct: what
 * would take a colour another label has in the chart takes the next one free; when colours run out, they repeat or
 * are grey.
 */
export class PaletteMemory {
  /** What has had a colour: its slot, and whether distinct moved it there. */
  private readonly slots = new Map<string, { slot: number | 'neutral'; moved: boolean }>();
  private met = 0;

  constructor(readonly palette: PaletteView) {}

  assign(
    entities: readonly ColoredEntity[],
    theme: ChartTheme,
    answer: readonly LabelColor[] | null,
  ): EntityColor[] {
    const palette = this.palette;
    const n = palette.colors.length;
    const paint = (color: { light: string; dark?: string | null }) =>
      theme.dark ? (color.dark ?? color.light) : color.light;
    const keyOf = (label: string | null) =>
      label === null ? none : normalizeLabel(label, palette.matching);
    const overrides = new Map<string, LabelColor>();
    if (answer) {
      for (const color of answer) {
        overrides.set(color.label === null ? none : color.label, color);
      }
    } else {
      for (const item of palette.overrides) {
        const key = keyOf(item.label);
        if (!overrides.has(key)) {
          overrides.set(key, { label: item.label, ...item.color });
        }
      }
    }
    const overrideOf = (label: string | null) =>
      overrides.get(answer ? (label ?? none) : keyOf(label));
    const colored = new Map<string, EntityColor>();
    // Colours taken in this chart, by whose label took them: one label (as matched) may have its colour twice.
    const taken = new Map<string, string>();
    const take = (color: string, key: string) => {
      if (!taken.has(color)) {
        taken.set(color, key);
      }
    };
    const fresh: ColoredEntity[] = [];
    for (const entity of entities) {
      if (colored.has(entity.entity)) {
        continue;
      }
      const override = overrideOf(entity.label);
      const kept = this.slots.get(entity.entity);
      if (override) {
        colored.set(entity.entity, {
          ...entity,
          color: paint(override),
          source: 'override',
          pair: { light: override.light, dark: override.dark ?? null },
        });
        take(override.light.toLowerCase(), keyOf(entity.label));
      } else if (kept !== undefined) {
        colored.set(entity.entity, this.colorOf(entity, kept.slot, kept.moved, theme, paint));
        if (kept.slot !== 'neutral') {
          take(palette.colors[kept.slot].light.toLowerCase(), keyOf(entity.label));
        }
      } else if (!fresh.some((f) => f.entity === entity.entity)) {
        fresh.push(entity);
      }
    }
    if (palette.assign === 'label') {
      fresh.sort((a, b) => compare(keyOf(a.label), keyOf(b.label)));
    }
    for (const entity of fresh) {
      const key = keyOf(entity.label);
      let preferred: number | null;
      if (palette.assign === 'order') {
        const index = this.met++;
        preferred = index < n ? index : palette.whenOut === 'repeat' ? index % n : null;
      } else {
        preferred = jumpHash(fnv1a64(key), n);
      }
      const slot = this.pick(preferred, key, taken);
      const moved = slot !== 'neutral' && slot !== preferred;
      this.slots.set(entity.entity, { slot, moved });
      colored.set(entity.entity, this.colorOf(entity, slot, moved, theme, paint));
      if (slot !== 'neutral') {
        take(palette.colors[slot].light.toLowerCase(), key);
      }
    }
    return entities.map((e) => colored.get(e.entity)!);
  }

  /** The slot preferred, or (distinct) the next whose colour no other label in the chart has; grey or repeated when none is free. */
  private pick(
    preferred: number | null,
    key: string,
    taken: ReadonlyMap<string, string>,
  ): number | 'neutral' {
    const palette = this.palette;
    if (preferred === null) {
      return 'neutral';
    }
    const free = (slot: number) => {
      const by = taken.get(palette.colors[slot].light.toLowerCase());
      return by === undefined || by === key;
    };
    if (!palette.distinct || free(preferred)) {
      return preferred;
    }
    const n = palette.colors.length;
    for (let step = 1; step < n; step++) {
      const slot = (preferred + step) % n;
      if (free(slot)) {
        return slot;
      }
    }
    return palette.whenOut === 'repeat' ? preferred : 'neutral';
  }

  private colorOf(
    entity: ColoredEntity,
    slot: number | 'neutral',
    moved: boolean,
    theme: ChartTheme,
    paint: (color: PaletteColor) => string,
  ): EntityColor {
    if (slot === 'neutral') {
      return { ...entity, color: theme.neutral, source: 'neutral', pair: null };
    }
    const source: ColorSource = moved ? 'moved' : this.palette.assign;
    const pair = this.palette.colors[slot];
    return { ...entity, color: paint(pair), source, pair };
  }
}

/**
 * Colors by the entity they stand for (a series, a slice), with no palette: the theme's, in their order as entities
 * are first met, kept while the widget is shown: a filter that leaves some out doesn't paint the others anew. Past
 * the theme's last color, entities are the neutral one.
 */
export class ColorMemory implements SeriesColors {
  private readonly slots = new Map<string, number>();

  slot(entity: string): number {
    let slot = this.slots.get(entity);
    if (slot === undefined) {
      slot = this.slots.size;
      this.slots.set(entity, slot);
    }
    return slot;
  }

  color(entity: string, theme: ChartTheme): string {
    return theme.series[this.slot(entity)] ?? theme.neutral;
  }
}

/**
 * A chart widget's colours: its palette's (none: the theme's, as charts always had), kept while it is shown, and
 * started again when its palette (or the palette's content), or what it colours, changes.
 */
export class WidgetColors {
  private basis: string | null = null;
  private memory: PaletteMemory | ColorMemory = new ColorMemory();

  paint(
    config: ChartConfig,
    data: WidgetData,
    palette: PaletteView | null,
    theme: ChartTheme,
    locale: string,
  ): AssignedColors {
    const series = config.kind === 'pie' ? null : config.series;
    const basis = JSON.stringify([
      palette?.basis ?? null,
      config.kind,
      config.kind === 'bar' ? (config.colorBy ?? 'measure') : null,
      config.dimension.field,
      config.dimension.bucket,
      series?.field ?? null,
      series?.bucket ?? null,
    ]);
    if (basis !== this.basis) {
      this.basis = basis;
      this.memory = palette ? new PaletteMemory(palette) : new ColorMemory();
    }
    const entities = coloredEntities(config, data, locale);
    const memory = this.memory;
    return new AssignedColors(
      memory instanceof PaletteMemory
        ? memory.assign(entities, theme, data.colors ?? null)
        : entities.map((e) => ({
            ...e,
            color: memory.color(e.entity, theme),
            source: 'default' as const,
            pair: galaxyColors[memory.slot(e.entity)] ?? null,
          })),
    );
  }
}

function compare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Colours by entity, as a chart's options read them; grey for any not listed. */
export class AssignedColors implements SeriesColors {
  private readonly byEntity: ReadonlyMap<string, string>;

  constructor(readonly entries: readonly EntityColor[]) {
    this.byEntity = new Map(entries.map((e) => [e.entity, e.color]));
  }

  color(entity: string, theme: ChartTheme): string {
    return this.byEntity.get(entity) ?? theme.neutral;
  }
}

/** A colour as `#rrggbb`: as it is, or as a page reads it (`rgb(…)`); black for what isn't one. */
export function hexOf(color: string): string {
  if (/^#[0-9a-f]{6}$/i.test(color)) {
    return color.toLowerCase();
  }
  const rgb = /^rgba?\(\s*(\d+)[\s,]+(\d+)[\s,]+(\d+)/i.exec(color);
  return rgb
    ? '#' +
        rgb
          .slice(1, 4)
          .map((c) => Math.min(255, Number(c)).toString(16).padStart(2, '0'))
          .join('')
    : '#000000';
}
