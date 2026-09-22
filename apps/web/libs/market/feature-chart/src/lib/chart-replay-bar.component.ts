import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSliderModule } from '@angular/material/slider';

/**
 * The replay transport, shown only while the chart is stepping through
 * history. It says HISTORICAL REPLAY in as many words: a chart that is not
 * showing the latest price must never be mistaken for one that is.
 *
 * Live ticks keep arriving and the sidebar keeps showing them — replay moves
 * the BARS, it does not pause the market.
 */
@Component({
  selector: 'ak-chart-replay-bar',
  // The layout around it is the parent's: this host must not become the flex item.
  host: { class: 'contents' },
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatSliderModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-wrap items-center gap-2 border-b border-warning/30 bg-warning/10 px-3 text-xs" role="region" aria-label="Historical bar replay">
      <span class="font-semibold text-warning">HISTORICAL REPLAY</span>
      <button mat-icon-button [attr.aria-label]="playing() ? 'Pause replay' : 'Play replay'" [disabled]="atEnd()" (click)="playToggle.emit()"><mat-icon>{{ playing() ? 'pause' : 'play_arrow' }}</mat-icon></button>
      <button mat-icon-button aria-label="Advance one bar" [disabled]="atEnd()" (click)="step.emit()"><mat-icon>skip_next</mat-icon></button>
      <mat-slider class="min-w-30 flex-1" [min]="1" [max]="total()" [step]="1"><input matSliderThumb aria-label="Replay position" [value]="index()" (valueChange)="seek.emit($event)" /></mat-slider>
      <span class="tabular-nums">{{ index() }} / {{ total() }} bars</span>
      <span class="text-text-secondary">Live ticks paused on chart</span>
    </div>
  `,
})
export class ChartReplayBarComponent {
  /** Bars shown so far, and how many there are in all. */
  readonly index = input.required<number>();
  readonly total = input.required<number>();
  readonly playing = input(false);

  readonly playToggle = output<void>();
  readonly step = output<void>();
  readonly seek = output<number>();

  /** Play and step stop at the last bar rather than wrapping. */
  protected atEnd(): boolean {
    return this.index() >= this.total();
  }
}
