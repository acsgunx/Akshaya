import { ApplicationConfig, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withViewTransitions } from '@angular/router';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';

import { routes } from './app.routes';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { errorInterceptor } from './core/interceptors/error.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    // Zoneless: nothing here schedules change detection implicitly. Every
    // piece of state that must update the view is a signal (SignalStores,
    // the market-data service's ticks, venue-state's clock) — see the
    // module docs on `market-data.service.ts` for the one place this needed
    // a deliberate note (a bare `setInterval` writing to a signal is still
    // zoneless-safe; it is only DOM events and callbacks that mutate plain
    // fields that would silently stop updating the view under zoneless).
    provideZonelessChangeDetection(),
    provideRouter(routes, withComponentInputBinding(), withViewTransitions()),
    provideHttpClient(withInterceptors([authInterceptor, errorInterceptor])),
    // Async-loaded so Material's animation runtime isn't in the initial
    // bundle for a first paint that is mostly tables and forms.
    provideAnimationsAsync(),
  ],
};
