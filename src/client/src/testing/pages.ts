import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import type { EnvironmentProviders, Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter, withComponentInputBinding, type Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AuthStore, type Session } from '../app/core/auth/auth-store';
import { sessionInterceptor } from '../app/core/auth/session.interceptor';
import { catalogVersionInterceptor } from '../app/core/catalog/catalog-version';
import { fakeBrowserProviders, sessionOf } from './auth';

/**
 * What pages' tests need: the API (answered by the test, its catalog versions kept), the router with `routes`, fakes
 * of the browser, and Material without animations (jsdom ends none, so a dialog would never finish closing).
 */
export function pageProviders(routes: Routes): (Provider | EnvironmentProviders)[] {
  return [
    provideHttpClient(withInterceptors([sessionInterceptor, catalogVersionInterceptor])),
    provideHttpClientTesting(),
    provideRouter(routes, withComponentInputBinding()),
    fakeBrowserProviders(),
    { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
  ];
}

/** Opens a page at `url` through the router, with `session` (an administrator's, unless said otherwise). */
export async function openPage(url: string, session: Session = sessionOf()) {
  const http = TestBed.inject(HttpTestingController);
  const loaded = TestBed.inject(AuthStore).ensure();
  http.expectOne('/api/auth/session').flush(session);
  await loaded;
  const harness = await RouterTestingHarness.create(url);
  return {
    harness,
    http,
    loader: TestbedHarnessEnvironment.loader(harness.fixture),
    /** For what opens over the page: dialogs, menus, snack bars. */
    root: TestbedHarnessEnvironment.documentRootLoader(harness.fixture),
    page: harness.routeNativeElement as HTMLElement,
  };
}

/** The text of an element, its white space made single. */
export function textOf(element: Element | null | undefined): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

/**
 * Clicks the button whose text is `text` without waiting for the application to be stable, as Material's harnesses
 * do: for a click that starts what only the test can finish (a request it answers, a dialog a navigation waits for).
 */
export function clickButton(container: ParentNode, text: string): void {
  const button = [...container.querySelectorAll<HTMLButtonElement>('button')].find(
    (candidate) => textOf(candidate) === text,
  );
  if (!button) {
    throw new Error(`No button "${text}"`);
  }
  button.click();
}

/** The text of all of a page's alerts (live regions with `role="alert"`), together. */
export function alertsOf(page: ParentNode): string {
  return [...page.querySelectorAll('[role=alert]')]
    .map((alert) => textOf(alert))
    .filter(Boolean)
    .join(' ');
}
