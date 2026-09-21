import { ApplicationConfig, isDevMode, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { MAT_DIALOG_DEFAULT_OPTIONS, MatDialogConfig } from '@angular/material/dialog';
import { provideRouter, withComponentInputBinding, withViewTransitions } from '@angular/router';

import { routes } from './app.routes';
import { activityInterceptor } from './core/interceptors/activity.interceptor';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { devLatencyInterceptor } from './core/interceptors/dev-latency.interceptor';
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
    // Dialogs ask for a fixed width (440px for modify/convert, 420px for the
    // confirm) and Material caps that at 80vw — on a 390px phone, a 312px
    // dialog with a type-to-confirm field and two buttons in it. A 16px gutter
    // each side is the most a phone can spare. Spread over the stock config so
    // every other default (focus restore, close on navigation) is kept.
    { provide: MAT_DIALOG_DEFAULT_OPTIONS, useValue: { ...new MatDialogConfig(), maxWidth: 'calc(100vw - 32px)' } },
    // NOTE: no `provideAnimations`. Angular Material 22 drives its own
    // transitions from CSS and AG Grid ships its own, so `@angular/animations`
    // is not a dependency of this app at all — that whole runtime is out of
    // the bundle.
  ],
};
