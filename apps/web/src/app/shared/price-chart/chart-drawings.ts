import type { IPrimitivePaneRenderer, IPrimitivePaneView, ISeriesPrimitive, SeriesAttachedParameter, UTCTimestamp } from 'lightweight-charts';

export type DrawingTool = 'cursor' | 'horizontal' | 'trend' | 'rectangle' | 'fibonacci';
export interface DrawingAnchor { readonly time: number; readonly price: number }
export interface ChartDrawing {
  readonly tool: Exclude<DrawingTool, 'cursor'>;
  readonly start: DrawingAnchor;
  readonly end: DrawingAnchor;
}

export const DRAWING_TOOLS: readonly { id: DrawingTool; label: string; icon: string }[] = [
  { id: 'cursor', label: 'Crosshair / pan', icon: 'control_camera' },
  { id: 'trend', label: 'Trend line', icon: 'timeline' },
  { id: 'horizontal', label: 'Horizontal line', icon: 'horizontal_rule' },
  { id: 'rectangle', label: 'Rectangle', icon: 'crop_square' },
  { id: 'fibonacci', label: 'Fibonacci retracement', icon: 'format_line_spacing' },
];

export function validDrawing(value: unknown): value is ChartDrawing {
  if (!value || typeof value !== 'object') { return false; }
  const drawing = value as Partial<ChartDrawing>;
  const anchor = (point: DrawingAnchor | undefined) => point && Number.isFinite(point.time) && Number.isFinite(point.price);
  return DRAWING_TOOLS.some((tool) => tool.id !== 'cursor' && tool.id === drawing.tool)
    && !!anchor(drawing.start) && !!anchor(drawing.end);
}

export class ChartDrawings implements ISeriesPrimitive {
  private attachedTo: SeriesAttachedParameter | undefined;
  private drawings: readonly ChartDrawing[] = [];
  private color = '';
  private readonly view: IPrimitivePaneView = {
    zOrder: () => 'top',
    renderer: () => ({ draw: (target) => this.draw(target) }),
  };

  attached(parameters: SeriesAttachedParameter): void { this.attachedTo = parameters; }
  detached(): void { this.attachedTo = undefined; }
  paneViews(): readonly IPrimitivePaneView[] { return [this.view]; }

  update(drawings: readonly ChartDrawing[], color: string): void {
    this.drawings = drawings;
    this.color = color;
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
      context.restore();
    });
  }
}
