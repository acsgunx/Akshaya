import { ApplicationConfig, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withViewTransitions } from '@angular/router';

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
    // fetch rather than XHR: no zone.js in this app, and fetch is the backend
    // the framework now optimises for. Interceptors are unaffected.
    provideHttpClient(withFetch(), withInterceptors([authInterceptor, errorInterceptor])),
    // NOTE: no `provideAnimations`. Angular Material 22 drives its own
    // transitions from CSS, so `@angular/animations` is no longer a
    // dependency of this app at all — that whole runtime is out of the bundle.
  ],
};
