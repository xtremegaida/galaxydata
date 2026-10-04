import { provideHttpClient, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { MatIconRegistry } from '@angular/material/icon';
import { TitleStrategy, provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { sessionInterceptor, xsrfCookie, xsrfHeader } from './core/auth/session.interceptor';
import { catalogVersionInterceptor } from './core/catalog/catalog-version';
import { PageTitles } from './core/page-titles';
import { ColorScheme } from './core/theme/color-scheme';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    { provide: TitleStrategy, useClass: PageTitles },
    provideHttpClient(
      withXsrfConfiguration({ cookieName: xsrfCookie, headerName: xsrfHeader }),
      withInterceptors([catalogVersionInterceptor, sessionInterceptor]),
    ),
    provideAppInitializer(() => {
      // Icons are Material Symbols, served with the client, by name (as text or fontIcon).
      inject(MatIconRegistry).setDefaultFontSetClass(
        'material-symbols-outlined',
        'mat-ligature-font',
      );
      // The color scheme chosen before, from the start.
      inject(ColorScheme);
    }),
  ],
};
