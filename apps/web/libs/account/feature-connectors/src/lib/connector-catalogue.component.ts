import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

import { ConnectorStore } from '@akshaya/shared/data-access';
import { ACCOUNT_TABS, EmptyStateComponent, LoadingStateComponent, SectionTabsComponent } from '@akshaya/shared/ui';

/**
 * Lists every broker the platform knows about, purely from their manifests.
 * Adding a connector to the backend makes it appear here with zero frontend
 * changes — that is the acceptance test for "no broker-specific code" as
 * much as the order ticket is.
 */
@Component({
  selector: 'ak-connector-catalogue',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatIconModule, EmptyStateComponent, LoadingStateComponent, SectionTabsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './connector-catalogue.component.html',
})
export class ConnectorCatalogueComponent implements OnInit {
  protected readonly store = inject(ConnectorStore);
  protected readonly accountTabs = ACCOUNT_TABS;

  ngOnInit(): void {
    if (this.store.isEmpty()) {
      this.store.load();
    }
  }
}
