import { ApplicationConfig, isDevMode, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withViewTransitions } from '@angular/router';

import { routes } from './app.routes';
import {
  activityInterceptor,
  authInterceptor,
  devLatencyInterceptor,
  errorInterceptor,
} from '@akshaya/shared/data-access';

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
    // `activityInterceptor` first, so the bar spans everything after it,
    // including the error toast's handling. `devLatencyInterceptor` is the
    // dev server's "make the local API as slow as a real broker" switch —
    // see its doc comment; it is not registered in a production build.
    provideHttpClient(
      withFetch(),
      withInterceptors([
        activityInterceptor,
        authInterceptor,
        errorInterceptor,
        ...(isDevMode() ? [devLatencyInterceptor] : []),
      ]),
    ),
    // NOTE: no MAT_DIALOG_DEFAULT_OPTIONS. The app-wide dialog width cap now
    // travels as `AK_DIALOG_DEFAULTS` (@akshaya/shared/ui), spread into each
    // `MatDialog.open()`. Providing the token here means importing
    // `@angular/material/dialog` from the shell, which pins that module into
    // the initial bundle (measured: +29kB) and, with it, the CDK overlay stack
    // behind it — undoing the lazy loading every call site does. See that
    // constant's doc comment.
    // NOTE: no `provideAnimations`. Angular Material 22 drives its own
    // transitions from CSS and AG Grid ships its own, so `@angular/animations`
    // is not a dependency of this app at all — that whole runtime is out of
    // the bundle.
  ],
};
