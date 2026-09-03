import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { ConnectorStore } from '../../core/connector.store';
import { EmptyStateComponent } from '../../shared/empty-state/empty-state.component';

/**
 * Lists every broker the platform knows about, purely from their manifests.
 * Adding a connector to the backend makes it appear here with zero frontend
 * changes — that is the acceptance test for "no broker-specific code" as
 * much as the order ticket is.
 */
@Component({
  selector: 'ak-connector-catalogue',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatIconModule, MatProgressSpinnerModule, EmptyStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './connector-catalogue.component.html',
})
export class ConnectorCatalogueComponent implements OnInit {
  protected readonly store = inject(ConnectorStore);

  ngOnInit(): void {
    if (this.store.isEmpty()) {
      this.store.load();
    }
  }
}
