import type { IPrimitivePaneRenderer, IPrimitivePaneView, ISeriesPrimitive, SeriesAttachedParameter, UTCTimestamp } from 'lightweight-charts';

export type DrawingTool = 'cursor' | 'horizontal' | 'trend' | 'rectangle' | 'fibonacci' | 'measure';
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
  { id: 'cursor', label: 'Crosshair / pan', icon: 'control_camera' },
  { id: 'measure', label: 'Measure', icon: 'straighten' },
  { id: 'trend', label: 'Trend line', icon: 'timeline' },
  { id: 'horizontal', label: 'Horizontal line', icon: 'horizontal_rule' },
  { id: 'rectangle', label: 'Rectangle', icon: 'crop_square' },
  { id: 'fibonacci', label: 'Fibonacci retracement', icon: 'format_line_spacing' },
];

const PERSISTENT_TOOLS: readonly string[] = ['trend', 'horizontal', 'rectangle', 'fibonacci'];

export function validDrawing(value: unknown): value is ChartDrawing {
  if (!value || typeof value !== 'object') { return false; }
  const drawing = value as Partial<ChartDrawing>;
  const anchor = (point: DrawingAnchor | undefined) => point && Number.isFinite(point.time) && Number.isFinite(point.price);
  return typeof drawing.tool === 'string' && PERSISTENT_TOOLS.includes(drawing.tool)
    && !!anchor(drawing.start) && !!anchor(drawing.end);
}

export class ChartDrawings implements ISeriesPrimitive {
  private attachedTo: SeriesAttachedParameter | undefined;
  private drawings: readonly ChartDrawing[] = [];
  private color = '';
  private measure: MeasureOverlay | undefined;
  private readonly view: IPrimitivePaneView = {
    zOrder: () => 'top',
    renderer: () => ({ draw: (target) => this.draw(target) }),
  };

  attached(parameters: SeriesAttachedParameter): void { this.attachedTo = parameters; }
  detached(): void { this.attachedTo = undefined; }
  paneViews(): readonly IPrimitivePaneView[] { return [this.view]; }

  update(drawings: readonly ChartDrawing[], color: string, measure?: MeasureOverlay): void {
    this.drawings = drawings;
    this.color = color;
    this.measure = measure;
    this.attachedTo?.requestUpdate();
  }

  private draw(target: Parameters<IPrimitivePaneRenderer['draw']>[0]): void {
    const attached = this.attachedTo;
    if (!attached) { return; }
    target.useMediaCoordinateSpace(({ context, mediaSize }) => {
      context.save();
      context.strokeStyle = this.color;
      context.fillStyle = this.color;
      context.lineWidth = 1.5;
      context.font = '11px sans-serif';
      for (const drawing of this.drawings) {
        const x1 = attached.chart.timeScale().timeToCoordinate(drawing.start.time as UTCTimestamp);
        const x2 = attached.chart.timeScale().timeToCoordinate(drawing.end.time as UTCTimestamp);
        const y1 = attached.series.priceToCoordinate(drawing.start.price);
        const y2 = attached.series.priceToCoordinate(drawing.end.price);
        if (y1 === null || y2 === null) { continue; }
        const line = (fromX: number, fromY: number, toX: number, toY: number) => {
          context.beginPath(); context.moveTo(fromX, fromY); context.lineTo(toX, toY); context.stroke();
        };
        if (drawing.tool === 'horizontal') {
          line(0, y1, mediaSize.width, y1);
          context.fillText(drawing.start.price.toLocaleString(undefined, { maximumFractionDigits: 6 }), 8, y1 - 5);
          continue;
        }
        if (x1 === null || x2 === null) { continue; }
        if (drawing.tool === 'trend') { line(x1, y1, x2, y2); }
        if (drawing.tool === 'rectangle') {
          context.globalAlpha = 0.12;
          context.fillRect(x1, y1, x2 - x1, y2 - y1);
          context.globalAlpha = 1;
          context.strokeRect(x1, y1, x2 - x1, y2 - y1);
        }
        if (drawing.tool === 'fibonacci') {
          for (const ratio of [0, 0.236, 0.382, 0.5, 0.618, 0.786, 1]) {
            const level = drawing.end.price + (drawing.start.price - drawing.end.price) * ratio;
            const y = attached.series.priceToCoordinate(level);
            if (y === null) { continue; }
            line(x1, y, x2, y);
            context.fillText(`${ratio}  (${level.toLocaleString(undefined, { maximumFractionDigits: 4 })})`, Math.min(x1, x2) + 4, y - 4);
          }
        }
        for (const [x, y] of [[x1, y1], [x2, y2]]) {
          context.beginPath(); context.arc(x ?? 0, y ?? 0, 3, 0, Math.PI * 2); context.fill();
        }
      }
      if (this.measure) {
        this.drawMeasure(context, mediaSize, this.measure, attached);
      }
      context.restore();
    });
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
