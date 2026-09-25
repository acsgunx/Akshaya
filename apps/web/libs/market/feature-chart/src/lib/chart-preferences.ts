import {
  CHART_TYPES,
  indicatorFor,
  newStudy,
  validStudy,
  type ChartType,
  type IndicatorKind,
  type StudyInstance,
} from '@akshaya/market/ui-price-chart';

/** How the user likes this chart drawn. Device-local, never server state — see CLAUDE.md. */
export interface ChartPreferences {
  readonly type: ChartType;
  /** The studies on the chart, in order. Instances with their own parameters and colours. */
  readonly studies: readonly StudyInstance[];
  readonly volume: boolean;
  readonly grid: boolean;
  readonly sidebar: boolean;
  readonly exposure: boolean;
  /** The values legend over the chart. Off gives the bars the whole card. */
  readonly legend: boolean;
  /** Crosshair snaps to the nearest OHLC value — what TradingView calls magnet. */
  readonly magnet: boolean;
  readonly scale: 'normal' | 'log' | 'percentage';
}

const KEY = 'akshaya.chart.preferences.v2';
/** The shape before studies became instances; still read once, to migrate. */
const LEGACY_KEY = 'akshaya.chart.preferences.v1';

/**
 * A chart nobody has configured yet: one moving average over the price and one
 * oscillator under it, which is what most people put on a chart first and what
 * shows that both kinds of pane exist.
 *
 * A function rather than a constant because every `StudyInstance` carries its
 * own id, and two charts must not share one.
 */
export function defaultStudies(): StudyInstance[] {
  const studies: StudyInstance[] = [];
  for (const kind of ['sma', 'rsi'] as const) {
    const study = newStudy(kind, studies);
    if (study) { studies.push(study); }
  }
  return studies;
}

export function defaultChartPreferences(): ChartPreferences {
  return {
    type: 'candles',
    studies: defaultStudies(),
    volume: true,
    grid: true,
    sidebar: true,
    exposure: true,
    legend: true,
    magnet: false,
    scale: 'normal',
  };
}

/**
 * Reads the saved preferences, field by field, keeping the default for
 * anything missing, mistyped or no longer offered — a study that has since
 * been removed, or a file someone hand-edited. Storage can also be
 * unavailable outright, so `ok` says whether this is what was saved or just
 * the defaults; the chart tells the user rather than silently resetting.
 *
 * A v1 file (studies as a list of flags, before they had parameters) is
 * MIGRATED rather than discarded. Every one of the six studies that shape could
 * express still exists, and its old hardcoded period is the new default for
 * that kind, so the migration is exact: someone who left the chart with
 * Bollinger and MACD on it gets Bollinger and MACD back.
 */
export function loadChartPreferences(): { preferences: ChartPreferences; ok: boolean } {
  const defaults = defaultChartPreferences();
  try {
    const raw = localStorage.getItem(KEY);
    const saved = JSON.parse(raw ?? '{}') as Record<string, unknown>;
    const legacy = raw === null ? readLegacy() : undefined;
    const source = legacy ?? saved;
    const studies = source['studies'];
    const scale = source['scale'];
    const boolean = (value: unknown, fallback: boolean) => typeof value === 'boolean' ? value : fallback;
    return {
      ok: true,
      preferences: {
        type: CHART_TYPES.find((item) => item.id === source['type'])?.id ?? defaults.type,
        studies: Array.isArray(studies) ? studies.filter(validStudy) : defaults.studies,
        volume: boolean(source['volume'], defaults.volume),
        grid: boolean(source['grid'], defaults.grid),
        sidebar: boolean(source['sidebar'], defaults.sidebar),
        exposure: boolean(source['exposure'], defaults.exposure),
        legend: boolean(source['legend'], defaults.legend),
        magnet: boolean(source['magnet'], defaults.magnet),
        scale: scale === 'normal' || scale === 'log' || scale === 'percentage' ? scale : defaults.scale,
      },
    };
  } catch {
    return { preferences: defaults, ok: false };
  }
}

/** The v1 file as a v2 one, or `undefined` when there is no v1 file either. */
function readLegacy(): Record<string, unknown> | undefined {
  const raw = localStorage.getItem(LEGACY_KEY);
  if (raw === null) { return undefined; }
  const saved = JSON.parse(raw) as Record<string, unknown>;
  const kinds = saved['studies'];
  const studies: StudyInstance[] = [];
  if (Array.isArray(kinds)) {
    for (const kind of kinds) {
      if (typeof kind !== 'string' || !indicatorFor(kind)) { continue; }
      const study = newStudy(kind as IndicatorKind, studies);
      if (study) { studies.push(study); }
    }
  }
  return { ...saved, studies };
}

/** False when the device refused to store them; the chart says so rather than pretending they are kept. */
export function saveChartPreferences(preferences: ChartPreferences): boolean {
  try {
    localStorage.setItem(KEY, JSON.stringify(preferences));
    return true;
  } catch {
    return false;
  }
}
