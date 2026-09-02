import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';

import type { InstrumentDefinition } from '../../core/models';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';
import { WatchlistRowComponent } from './watchlist-row.component';
import { WatchlistStore } from './watchlist.store';

/**
 * Live watchlist: search-to-add, then each row (`ak-watchlist-row`) owns its
 * own live subscription and flash-on-change — see that component's doc
 * comment for how the flash never shifts layout.
 */
@Component({
  selector: 'ak-watchlist',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    EmptyStateComponent,
    WatchlistRowComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './watchlist.component.html',
  styleUrl: './watchlist.component.scss',
})
export class WatchlistComponent {
  protected readonly store = inject(WatchlistStore);
  protected readonly searchControl = new FormControl<string>('', { nonNullable: true });

  constructor() {
    this.searchControl.valueChanges.subscribe((value) => this.store.search(value));
  }

  protected addInstrument(instrument: InstrumentDefinition): void {
    this.store.add(instrument);
    this.searchControl.setValue('');
  }

  protected removeInstrument(key: string): void {
    this.store.remove(key);
  }
}
