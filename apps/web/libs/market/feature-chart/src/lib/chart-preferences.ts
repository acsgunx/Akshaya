import { CHART_TYPES, STUDIES, type ChartType, type StudyId } from '@akshaya/market/ui-price-chart';

/** How the user likes this chart drawn. Device-local, never server state — see CLAUDE.md. */
export interface ChartPreferences {
  readonly type: ChartType;
  readonly studies: readonly StudyId[];
  readonly volume: boolean;
  readonly grid: boolean;
  readonly sidebar: boolean;
  readonly exposure: boolean;
  readonly scale: 'normal' | 'log' | 'percentage';
}

export const DEFAULT_CHART_PREFERENCES: ChartPreferences = {
  type: 'candles',
  studies: ['stochRsi'],
  volume: true,
  grid: true,
  sidebar: true,
  exposure: true,
  scale: 'normal',
};

const KEY = 'akshaya.chart.preferences.v1';

/**
 * Reads the saved preferences, field by field, keeping the default for
 * anything missing, mistyped or no longer offered — a study that has since
 * been removed, or a file someone hand-edited. Storage can also be
 * unavailable outright, so `ok` says whether this is what was saved or just
 * the defaults; the chart tells the user rather than silently resetting.
 */
export function loadChartPreferences(): { preferences: ChartPreferences; ok: boolean } {
  try {
    const saved = JSON.parse(localStorage.getItem(KEY) ?? '{}') as Record<string, unknown>;
    const type = CHART_TYPES.find((item) => item.id === saved['type'])?.id;
    const studies = saved['studies'];
    const scale = saved['scale'];
    const boolean = (value: unknown, fallback: boolean) => typeof value === 'boolean' ? value : fallback;
    return {
      ok: true,
      preferences: {
        type: type ?? DEFAULT_CHART_PREFERENCES.type,
        studies: Array.isArray(studies)
          ? STUDIES.filter((item) => studies.includes(item.id)).map((item) => item.id)
          : DEFAULT_CHART_PREFERENCES.studies,
        volume: boolean(saved['volume'], DEFAULT_CHART_PREFERENCES.volume),
        grid: boolean(saved['grid'], DEFAULT_CHART_PREFERENCES.grid),
        sidebar: boolean(saved['sidebar'], DEFAULT_CHART_PREFERENCES.sidebar),
        exposure: boolean(saved['exposure'], DEFAULT_CHART_PREFERENCES.exposure),
        scale: scale === 'normal' || scale === 'log' || scale === 'percentage' ? scale : DEFAULT_CHART_PREFERENCES.scale,
      },
    };
  } catch {
    return { preferences: DEFAULT_CHART_PREFERENCES, ok: false };
  }
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
