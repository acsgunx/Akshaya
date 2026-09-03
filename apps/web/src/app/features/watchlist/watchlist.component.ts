import { ChangeDetectionStrategy, Component, computed, effect, inject } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

import { BrokerLinksStore } from '../../core/broker-links.store';
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
    MatSelectModule,
    EmptyStateComponent,
    WatchlistRowComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './watchlist.component.html',
  styleUrl: './watchlist.component.scss',
})
export class WatchlistComponent {
  protected readonly store = inject(WatchlistStore);
  private readonly brokerLinks = inject(BrokerLinksStore);
  protected readonly searchControl = new FormControl<string>('', { nonNullable: true });

  /**
   * Only a link with a live session can answer either half of this screen —
   * instrument search goes through the connector's reference facet and the
   * prices through its stream — so a linked-but-signed-out account is not
   * offered here. It would fail at the first keystroke with a re-auth error.
   */
  protected readonly usableLinks = computed(() =>
    this.brokerLinks.links().filter((link) => link.isActive && link.hasSession),
  );

  protected readonly hasNoUsableLink = computed(() => this.usableLinks().length === 0);

  constructor() {
    this.searchControl.valueChanges.subscribe((value) => this.store.search(value));

    // Reactive forms own their own enabled state — setting `disabled` from the
    // template would throw NG0215 — so the "no broker linked" case is driven
    // from here.
    effect(() => {
      if (this.hasNoUsableLink()) {
        this.searchControl.disable({ emitEvent: false });
      } else {
        this.searchControl.enable({ emitEvent: false });
      }
    });

    // Keep the selection pointing at a link that can actually serve it: the
    // persisted choice may name an account that has since been unlinked or had
    // its session expire, and one usable link needs no picker at all.
    effect(() => {
      const links = this.usableLinks();
      const fallback = links[0];
      if (!fallback) {
        return;
      }
      const selected = this.store.brokerLinkId();
      if (!selected || !links.some((link) => link.id === selected)) {
        this.store.selectBrokerLink(fallback.id);
      }
    });
  }

  protected selectBrokerLink(brokerLinkId: string): void {
    this.store.selectBrokerLink(brokerLinkId);
    this.searchControl.setValue('');
  }

  protected addInstrument(instrument: InstrumentDefinition): void {
    this.store.add(instrument);
    this.searchControl.setValue('');
  }

  protected removeInstrument(key: string): void {
    this.store.remove(key);
  }
}
