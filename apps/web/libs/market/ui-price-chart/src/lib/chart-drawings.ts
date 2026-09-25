import type { IPrimitivePaneRenderer, IPrimitivePaneView, ISeriesPrimitive, SeriesAttachedParameter, UTCTimestamp } from 'lightweight-charts';

export type DrawingTool = 'cursor' | 'horizontal' | 'vertical' | 'trend' | 'ray' | 'rectangle' | 'fibonacci' | 'measure';
export type PersistentDrawingTool = Exclude<DrawingTool, 'cursor' | 'measure'>;
export interface DrawingAnchor { readonly time: number; readonly price: number }
export interface ChartDrawing {
  readonly tool: PersistentDrawingTool;
  readonly start: DrawingAnchor;
  readonly end: DrawingAnchor;
}

/** Transient measure overlay — never persisted, unlike `ChartDrawing`. */
export interface MeasureOverlay {
  readonly start: DrawingAnchor;
  readonly end: DrawingAnchor;
  readonly color: string;
  readonly lines: readonly string[];
}

export const DRAWING_TOOLS: readonly { id: DrawingTool; label: string; icon: string }[] = [
  { id: 'cursor', label: 'Crosshair / select', icon: 'control_camera' },
  { id: 'measure', label: 'Measure', icon: 'straighten' },
  { id: 'trend', label: 'Trend line', icon: 'timeline' },
  { id: 'ray', label: 'Ray — extends right', icon: 'call_made' },
  { id: 'horizontal', label: 'Horizontal line', icon: 'horizontal_rule' },
  { id: 'vertical', label: 'Vertical line', icon: 'more_vert' },
  { id: 'rectangle', label: 'Rectangle', icon: 'crop_square' },
  { id: 'fibonacci', label: 'Fibonacci retracement', icon: 'format_line_spacing' },
];

const PERSISTENT_TOOLS: readonly string[] = ['trend', 'ray', 'horizontal', 'vertical', 'rectangle', 'fibonacci'];

/** Fibonacci retracement levels, in the order they are labelled. */
const FIB_RATIOS: readonly number[] = [0, 0.236, 0.382, 0.5, 0.618, 0.786, 1];

/** How near the pointer has to be, in pixels, for a click to select a drawing. */
const HIT_TOLERANCE = 6;

export function validDrawing(value: unknown): value is ChartDrawing {
  if (!value || typeof value !== 'object') { return false; }
  const drawing = value as Partial<ChartDrawing>;
  const anchor = (point: DrawingAnchor | undefined) => point && Number.isFinite(point.time) && Number.isFinite(point.price);
  return typeof drawing.tool === 'string' && PERSISTENT_TOOLS.includes(drawing.tool)
    && !!anchor(drawing.start) && !!anchor(drawing.end);
}

/**
 * Paints the drawings, the one being drawn, and the measure overlay onto the
 * price pane, and answers which drawing is under a pixel.
 *
 * The hit test lives here rather than in the controller because it is a
 * question about PIXELS: two prices a rupee apart are the same click on a
 * weekly chart and a mile apart on a one-minute one, so "did the user click
 * this trend line" can only be answered on the canvas, where the scales have
 * already been applied.
 */
export class ChartDrawings implements ISeriesPrimitive {
  private attachedTo: SeriesAttachedParameter | undefined;
  private drawings: readonly ChartDrawing[] = [];
  private color = '';
  private measure: MeasureOverlay | undefined;
  /** Index into `drawings` of the selected drawing, or -1. */
  private selected = -1;
  private readonly view: IPrimitivePaneView = {
    zOrder: () => 'top',
    renderer: () => ({ draw: (target) => this.draw(target) }),
  };

  attached(parameters: SeriesAttachedParameter): void { this.attachedTo = parameters; }
  detached(): void { this.attachedTo = undefined; }
  paneViews(): readonly IPrimitivePaneView[] { return [this.view]; }

  update(drawings: readonly ChartDrawing[], color: string, selected: number, measure?: MeasureOverlay): void {
    this.drawings = drawings;
    this.color = color;
    this.selected = selected;
    this.measure = measure;
    this.attachedTo?.requestUpdate();
  }

  /**
   * Index of the drawing under a pane pixel, or `undefined`. Later drawings win
   * — the one drawn on top is the one a click lands on.
   *
   * Not called `hitTest`: `ISeriesPrimitiveBase` already declares one with a
   * different signature, and implementing it by accident would have the library
   * calling this on every pointer move expecting a hovered-item descriptor.
   */
  drawingAt(x: number, y: number): number | undefined {
    const attached = this.attachedTo;
    if (!attached) { return undefined; }
    for (let index = this.drawings.length - 1; index >= 0; index--) {
      const drawing = this.drawings[index] as ChartDrawing;
      const box = this.pixels(drawing);
      if (!box) { continue; }
      const { x1, y1, x2, y2 } = box;
      switch (drawing.tool) {
        case 'horizontal':
          if (Math.abs(y - y1) <= HIT_TOLERANCE) { return index; }
          break;
        case 'vertical':
          if (Math.abs(x - x1) <= HIT_TOLERANCE) { return index; }
          break;
        case 'trend':
          if (distanceToSegment(x, y, x1, y1, x2, y2) <= HIT_TOLERANCE) { return index; }
          break;
        case 'ray': {
          // The drawn ray stops at the canvas edge, so the hit test does too.
          const width = attached.chart.timeScale().width();
          const [endX, endY] = extendRay(x1, y1, x2, y2, width);
          if (distanceToSegment(x, y, x1, y1, endX, endY) <= HIT_TOLERANCE) { return index; }
          break;
        }
        case 'rectangle':
          if (x >= Math.min(x1, x2) - HIT_TOLERANCE && x <= Math.max(x1, x2) + HIT_TOLERANCE
            && y >= Math.min(y1, y2) - HIT_TOLERANCE && y <= Math.max(y1, y2) + HIT_TOLERANCE) { return index; }
          break;
        case 'fibonacci':
          if (x >= Math.min(x1, x2) - HIT_TOLERANCE && x <= Math.max(x1, x2) + HIT_TOLERANCE) {
            for (const ratio of FIB_RATIOS) {
              const level = drawing.end.price + (drawing.start.price - drawing.end.price) * ratio;
              const levelY = attached.series.priceToCoordinate(level);
              if (levelY !== null && Math.abs(y - levelY) <= HIT_TOLERANCE) { return index; }
            }
          }
          break;
      }
    }
    return undefined;
  }

  /** A drawing's two anchors in pane pixels, or `undefined` when either is off the scales. */
  private pixels(drawing: ChartDrawing): { x1: number; y1: number; x2: number; y2: number } | undefined {
    const attached = this.attachedTo;
    if (!attached) { return undefined; }
    const x1 = attached.chart.timeScale().timeToCoordinate(drawing.start.time as UTCTimestamp);
    const x2 = attached.chart.timeScale().timeToCoordinate(drawing.end.time as UTCTimestamp);
    const y1 = attached.series.priceToCoordinate(drawing.start.price);
    const y2 = attached.series.priceToCoordinate(drawing.end.price);
    if (x1 === null || x2 === null || y1 === null || y2 === null) { return undefined; }
    return { x1, y1, x2, y2 };
  }

  private draw(target: Parameters<IPrimitivePaneRenderer['draw']>[0]): void {
    const attached = this.attachedTo;
    if (!attached) { return; }
    target.useMediaCoordinateSpace(({ context, mediaSize }) => {
      context.save();
      context.font = '11px sans-serif';
      for (const [index, drawing] of this.drawings.entries()) {
        const chosen = index === this.selected;
        context.strokeStyle = this.color;
        context.fillStyle = this.color;
        context.lineWidth = chosen ? 2.5 : 1.5;
        const x1 = attached.chart.timeScale().timeToCoordinate(drawing.start.time as UTCTimestamp);
        const x2 = attached.chart.timeScale().timeToCoordinate(drawing.end.time as UTCTimestamp);
        const y1 = attached.series.priceToCoordinate(drawing.start.price);
        const y2 = attached.series.priceToCoordinate(drawing.end.price);
        const line = (fromX: number, fromY: number, toX: number, toY: number) => {
          context.beginPath(); context.moveTo(fromX, fromY); context.lineTo(toX, toY); context.stroke();
        };
        if (drawing.tool === 'horizontal') {
          if (y1 === null) { continue; }
          line(0, y1, mediaSize.width, y1);
          context.fillText(drawing.start.price.toLocaleString(undefined, { maximumFractionDigits: 6 }), 8, y1 - 5);
          if (chosen) { this.handle(context, 12, y1); }
          continue;
        }
        if (drawing.tool === 'vertical') {
          if (x1 === null) { continue; }
          line(x1, 0, x1, mediaSize.height);
          if (chosen) { this.handle(context, x1, 12); }
          continue;
        }
        if (x1 === null || x2 === null || y1 === null || y2 === null) { continue; }
        if (drawing.tool === 'trend') { line(x1, y1, x2, y2); }
        if (drawing.tool === 'ray') {
          const [endX, endY] = extendRay(x1, y1, x2, y2, mediaSize.width);
          line(x1, y1, endX, endY);
        }
        if (drawing.tool === 'rectangle') {
          context.globalAlpha = 0.12;
          context.fillRect(x1, y1, x2 - x1, y2 - y1);
          context.globalAlpha = 1;
          context.strokeRect(x1, y1, x2 - x1, y2 - y1);
        }
        if (drawing.tool === 'fibonacci') {
          for (const ratio of FIB_RATIOS) {
            const level = drawing.end.price + (drawing.start.price - drawing.end.price) * ratio;
            const y = attached.series.priceToCoordinate(level);
            if (y === null) { continue; }
            line(x1, y, x2, y);
            context.fillText(`${ratio}  (${level.toLocaleString(undefined, { maximumFractionDigits: 4 })})`, Math.min(x1, x2) + 4, y - 4);
          }
        }
        this.handle(context, x1, y1, chosen);
        this.handle(context, x2, y2, chosen);
      }
      if (this.measure) {
        this.drawMeasure(context, mediaSize, this.measure, attached);
      }
      context.restore();
    });
  }

  /** The dot on a drawing's anchor; hollow and larger when the drawing is selected. */
  private handle(context: CanvasRenderingContext2D, x: number, y: number, chosen = true): void {
    context.beginPath();
    context.arc(x, y, chosen ? 4.5 : 3, 0, Math.PI * 2);
    if (chosen) {
      context.lineWidth = 2;
      context.stroke();
    } else {
      context.fill();
    }
  }

  private drawMeasure(
    context: CanvasRenderingContext2D,
    mediaSize: { width: number; height: number },
    measure: MeasureOverlay,
    attached: SeriesAttachedParameter,
  ): void {
    const x1 = attached.chart.timeScale().timeToCoordinate(measure.start.time as UTCTimestamp);
    const x2 = attached.chart.timeScale().timeToCoordinate(measure.end.time as UTCTimestamp);
    const y1 = attached.series.priceToCoordinate(measure.start.price);
    const y2 = attached.series.priceToCoordinate(measure.end.price);
    if (x1 === null || x2 === null || y1 === null || y2 === null) { return; }

    const left = Math.min(x1, x2);
    const top = Math.min(y1, y2);
    const width = Math.abs(x2 - x1);
    const height = Math.abs(y2 - y1);

    context.globalAlpha = 0.15;
    context.fillStyle = measure.color;
    context.fillRect(left, top, Math.max(width, 1), Math.max(height, 1));
    context.globalAlpha = 1;
    context.strokeStyle = measure.color;
    context.lineWidth = 1.5;
    context.setLineDash([4, 4]);
    context.strokeRect(left, top, Math.max(width, 1), Math.max(height, 1));
    context.setLineDash([]);
    context.fillStyle = measure.color;
    context.beginPath(); context.arc(x1, y1, 3, 0, Math.PI * 2); context.fill();
    context.beginPath(); context.arc(x2, y2, 3, 0, Math.PI * 2); context.fill();

    if (measure.lines.length === 0) { return; }
    const padX = 9;
    const padY = 7;
    const lineHeight = 15;
    context.font = '600 11px sans-serif';
    const boxWidth = Math.max(...measure.lines.map((line) => context.measureText(line).width)) + padX * 2;
    const boxHeight = measure.lines.length * lineHeight + padY * 2 - 3;
    const boxX = Math.max(4, Math.min(x2 + 10, mediaSize.width - boxWidth - 4));
    const boxY = Math.max(4, Math.min(y2 - boxHeight / 2, mediaSize.height - boxHeight - 4));
    context.fillStyle = measure.color;
    context.beginPath();
    context.roundRect(boxX, boxY, boxWidth, boxHeight, 4);
    context.fill();
    context.fillStyle = '#ffffff';
    measure.lines.forEach((line, i) => context.fillText(line, boxX + padX, boxY + padY + i * lineHeight + 4));
  }
}

/** Where a ray through two points leaves the canvas, to the right of its start. */
function extendRay(x1: number, y1: number, x2: number, y2: number, width: number): [number, number] {
  if (x2 === x1) { return [x1, y2 > y1 ? Number.MAX_SAFE_INTEGER : -Number.MAX_SAFE_INTEGER]; }
  const slope = (y2 - y1) / (x2 - x1);
  const edge = x2 >= x1 ? Math.max(width, x2) : 0;
  return [edge, y1 + slope * (edge - x1)];
}

/** Shortest pixel distance from a point to a line segment. */
function distanceToSegment(x: number, y: number, x1: number, y1: number, x2: number, y2: number): number {
  const dx = x2 - x1;
  const dy = y2 - y1;
  const lengthSquared = dx * dx + dy * dy;
  if (lengthSquared === 0) { return Math.hypot(x - x1, y - y1); }
  const along = Math.max(0, Math.min(1, ((x - x1) * dx + (y - y1) * dy) / lengthSquared));
  return Math.hypot(x - (x1 + along * dx), y - (y1 + along * dy));
}
