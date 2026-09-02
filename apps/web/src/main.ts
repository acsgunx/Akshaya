import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { AppComponent } from './app/app.component';

bootstrapApplication(AppComponent, appConfig).catch((err: unknown) => {
  // A bootstrap failure means the trader has no UI at all — including no kill switch and
  // no way to see stale positions — so it goes to the console loudly rather than being
  // swallowed by a generic error boundary.
  // eslint-disable-next-line no-console
  console.error('Akshaya failed to start', err);
});
