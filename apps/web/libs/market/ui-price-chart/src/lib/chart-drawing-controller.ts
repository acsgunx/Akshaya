import { ChartDrawings, validDrawing, type ChartDrawing, type DrawingAnchor, type DrawingTool, type MeasureOverlay } from './chart-drawings';
import type { ChartBar } from './candle-bucket';

/** What the toolbar needs to know: whether undo/redo are live, and how much is on the chart. */
export interface DrawingState {
  readonly undo: boolean;
  readonly redo: boolean;
  readonly count: number;
  /** One drawing is selected, so "delete this one" is available. */
  readonly selected: boolean;
}

/**
 * What the controller needs from the chart around it. Everything here is a
 * getter or a notification, so the controller never touches the chart API,
 * the DOM or Angular — it only decides what a click means and what should be
 * on screen.
 */
export interface DrawingSurface {
  /** The selected tool, and whether saved drawings are shown. */
  readonly tool: () => DrawingTool;
  readonly visible: () => boolean;
  /** Price precision, for the measure readout. */
  readonly precision: () => number;
  /** The bars currently drawn — the measure readout counts them. */
  readonly bars: () => readonly ChartBar[];
  /** Resolves an `--ak-*` token to a colour the canvas can paint. */
  readonly color: (token: string) => string;
  /** Status-line text for the screen reader and the footer. */
  readonly notice: (text: string) => void;
  readonly state: (state: DrawingState) => void;
  /** A drawing was finished, so the screen can drop back to the cursor tool. */
  readonly completed: () => void;
}

/** A chart carries at most this many saved drawings; beyond it, the oldest are dropped on load. */
const MAX_DRAWINGS = 200;

const storageKey = (key: string) => `akshaya.chart.drawings.${key}`;

/**
 * The drawing tools' state machine: what a click does, what is previewed
 * while the pointer moves, the undo/redo stacks, and persistence.
 *
 * Drawings are DEVICE-LOCAL (`localStorage`, keyed per link, instrument and
 * timeframe), never server state — see the chart notes in CLAUDE.md. Storage
 * can throw or be full, and every path below stays usable when it does: the
 * drawing still appears, it just will not come back tomorrow, and the
 * `notice` says so rather than failing silently.
 *
 * `measure` is the odd one out: it is never persisted and never enters the
 * undo stack, because it answers a question ("how far is this move?") rather
 * than marking up the chart.
 */
export class DrawingController {
  /** Attached to the price series by the component; it paints everything below. */
  readonly primitive = new ChartDrawings();

  private items: ChartDrawing[] = [];
  /**
   * The redo stack keeps each removal's POSITION, not just the drawing. A
   * drawing deleted from the middle of the list has to come back to the middle:
   * draw order is what decides which of two overlapping drawings a click
   * selects, so restoring it last would silently rearrange the chart.
   */
  private redoItems: { readonly drawing: ChartDrawing; readonly index: number }[] = [];
  private startAnchor: DrawingAnchor | undefined;
  private previewAnchor: DrawingAnchor | undefined;
  private measureStart: DrawingAnchor | undefined;
  private measureEnd: DrawingAnchor | undefined;
  private measurePreview: DrawingAnchor | undefined;
  /** Index of the selected drawing, or -1. Selection is never persisted. */
  private selected = -1;
  /** Where these drawings are saved: one key per link, instrument and timeframe. */
  private key = '';

  constructor(private readonly surface: DrawingSurface) {}

  /** Swaps in the drawings saved for `key` — a new instrument or timeframe. */
  load(key: string): void {
    this.key = key;
    this.items = [];
    this.redoItems = [];
    this.startAnchor = undefined;
    this.measureStart = undefined;
    this.measureEnd = undefined;
    this.measurePreview = undefined;
    this.selected = -1;
    try {
      const parsed: unknown = JSON.parse(localStorage.getItem(storageKey(key)) ?? '[]');
      if (Array.isArray(parsed)) { this.items = parsed.filter(validDrawing).slice(-MAX_DRAWINGS); }
    } catch { this.surface.notice('Saved drawings could not be restored on this device.'); }
    this.publish();
  }

  /** Drops a half-finished drawing: the tool or a chart setting changed under it. */
  resetPending(): void {
    this.startAnchor = undefined;
    this.previewAnchor = undefined;
  }

  /** True when a drawing is selected — what the Delete key acts on. */
  get hasSelection(): boolean {
    return this.selected >= 0;
  }

  /**
   * The pointer moved. The anchor is a thunk because resolving a price from a
   * pixel costs a chart call on every crosshair move, and nothing needs it
   * unless a drawing or a measure is actually open.
   */
  pointerMoved(anchor: () => DrawingAnchor | undefined): void {
    if (this.measureStart && !this.measureEnd) {
      this.measurePreview = anchor();
      this.render();
    }
    if (this.startAnchor) {
      this.previewAnchor = anchor();
      this.render();
    }
  }

  /** The keyboard cursor moved; it previews the same way the pointer does. */
  cursorMoved(anchor: DrawingAnchor): void {
    this.previewAnchor = anchor;
    if (this.measureStart && !this.measureEnd) { this.measurePreview = anchor; }
    this.render();
  }

  /** A click on the price pane, already converted to a time and a price. */
  click(anchor: DrawingAnchor | undefined): void {
    const tool = this.surface.tool();
    if (tool === 'cursor' || !anchor) { return; }
    if (tool === 'measure') {
      if (this.measureStart && !this.measureEnd) {
        this.measureEnd = anchor;
        this.measurePreview = undefined;
        this.surface.notice(`Measured ${this.measureLines(this.measureStart, anchor).join(' · ')}. Click again to measure a new range.`);
      } else {
        this.beginMeasure(anchor);
      }
      this.render();
      return;
    }
    if (this.items.length >= MAX_DRAWINGS) {
      this.surface.notice(`This chart has reached its ${MAX_DRAWINGS}-drawing limit. Remove a drawing before adding another.`);
      return;
    }
    // Two-point tools take the first click as their start; a horizontal or
    // vertical line is finished by the click that placed it.
    if (tool !== 'horizontal' && tool !== 'vertical' && !this.startAnchor) {
      this.startAnchor = anchor;
      this.surface.notice('Choose the second point on the price chart. Escape cancels.');
      return;
    }
    this.items.push({ tool, start: this.startAnchor ?? anchor, end: anchor });
    this.redoItems = [];
    this.cancel();
    this.save();
    this.surface.completed();
  }

  /**
   * A click with the cursor tool: selects the drawing under the pixel, or
   * clears the selection when the click landed on empty chart.
   *
   * Returns true when something was selected, so the caller can say so in the
   * status line — the only feedback a keyboard user gets that Delete will now
   * remove something.
   */
  selectAt(point: { readonly x: number; readonly y: number } | undefined): boolean {
    const hit = point ? this.primitive.drawingAt(point.x, point.y) : undefined;
    const changed = this.selected !== (hit ?? -1);
    this.selected = hit ?? -1;
    if (changed) { this.publish(); }
    return hit !== undefined;
  }

  /** Removes the selected drawing. Undoable, like any other removal. */
  removeSelected(): boolean {
    const drawing = this.items[this.selected];
    if (!drawing) { return false; }
    this.redoItems.push({ drawing, index: this.selected });
    this.items.splice(this.selected, 1);
    this.selected = -1;
    this.save();
    return true;
  }

  /** Drops a horizontal line straight onto the chart (context menu "add line at price"). */
  addHorizontalLine(price: number, time: number): void {
    this.items.push({ tool: 'horizontal', start: { time, price }, end: { time, price } });
    this.redoItems = [];
    this.save();
  }

  /** Arms the measure tool with its first point; the next click finishes it. */
  beginMeasure(anchor: DrawingAnchor): void {
    this.measureStart = anchor;
    this.measureEnd = undefined;
    this.measurePreview = undefined;
    this.render();
    this.surface.notice('Click the second point on the price chart to finish measuring. Escape cancels.');
  }

  /** Abandons whatever is half-drawn, including a measure. */
  cancel(): void {
    this.startAnchor = undefined;
    this.previewAnchor = undefined;
    this.measureStart = undefined;
    this.measureEnd = undefined;
    this.measurePreview = undefined;
    this.render();
  }

  undo(): void {
    const drawing = this.items.pop();
    if (drawing) { this.redoItems.push({ drawing, index: this.items.length }); this.selected = -1; this.save(); }
  }

  redo(): void {
    const undone = this.redoItems.pop();
    if (undone) { this.items.splice(Math.min(undone.index, this.items.length), 0, undone.drawing); this.save(); }
  }

  clear(): void {
    this.items = [];
    this.redoItems = [];
    this.selected = -1;
    this.save();
  }

  /** Repaints: saved drawings (unless hidden), the drawing in progress, and any measure. */
  render(): void {
    const tool = this.surface.tool();
    const preview = this.startAnchor && this.previewAnchor && tool !== 'cursor' && tool !== 'measure'
      ? [{ tool, start: this.startAnchor, end: this.previewAnchor }] : [];
    const end = this.measureEnd ?? this.measurePreview;
    const measure: MeasureOverlay | undefined = tool === 'measure' && this.measureStart && end
      ? {
        start: this.measureStart,
        end,
        color: this.surface.color(end.price >= this.measureStart.price ? '--ak-buy' : '--ak-sell'),
        lines: this.measureLines(this.measureStart, end),
      }
      : undefined;
    this.primitive.update(
      this.surface.visible() ? [...this.items, ...preview] : [],
      this.surface.color('--ak-brand'),
      this.surface.visible() ? this.selected : -1,
      measure,
    );
  }

  /** Price change, percentage change, bar count and elapsed time between the two anchors. */
  private measureLines(start: DrawingAnchor, end: DrawingAnchor): string[] {
    const precision = this.surface.precision();
    const delta = end.price - start.price;
    const sign = delta >= 0 ? '+' : '';
    const percent = start.price ? delta / start.price * 100 : 0;
    const low = Math.min(start.time, end.time);
    const high = Math.max(start.time, end.time);
    const count = this.surface.bars().filter((bar) => bar.time >= low && bar.time <= high).length;
    const seconds = Math.abs(end.time - start.time);
    const days = Math.floor(seconds / 86400);
    const hours = Math.floor((seconds % 86400) / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const elapsed = [days && `${days}d`, hours && `${hours}h`, minutes && `${minutes}m`]
      .filter(Boolean).join(' ') || `${seconds}s`;
    return [
      `${sign}${delta.toFixed(precision)} (${sign}${percent.toFixed(2)}%)`,
      `${count} ${count === 1 ? 'bar' : 'bars'} · ${elapsed}`,
    ];
  }

  private save(): void {
    this.items = this.items.slice(-MAX_DRAWINGS);
    if (this.selected >= this.items.length) { this.selected = -1; }
    try { localStorage.setItem(storageKey(this.key), JSON.stringify(this.items)); }
    catch { this.surface.notice('Drawings are available for this session only; device storage is unavailable.'); }
    this.publish();
  }

  private publish(): void {
    this.render();
    this.surface.state({ undo: this.items.length > 0, redo: this.redoItems.length > 0,
      count: this.items.length, selected: this.selected >= 0 });
  }
}
