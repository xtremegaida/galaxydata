import { type Signal, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, type Params, Router } from '@angular/router';
import { sameJson } from '../../../core/api/same-json';
import {
  type DashboardUrlState,
  dashboardParams,
  isDashboardParam,
  readDashboardParams,
} from '../../../core/dashboards/dashboard-url';
import type { Definition } from '../model/definition';
import type { DashboardStore } from './dashboard-store';

/**
 * Keeps a dashboard's state (its filters' values, the slices chosen) in its page's address, so a reload, a link
 * or going back keeps it. The address is read when it changes (the page opens, back or forward, a link), and
 * written as the viewer changes the state, replacing the address (an embed's history is its parent's, and every
 * click would be a step back). Once the page has its dashboard (the store, a host): what is read is written back
 * the same, so nothing loops. Made in an injection context; gives what in the address couldn't be read.
 */
export function bindUrlState(store: DashboardStore, maxKeys: number): Signal<readonly string[]> {
  const route = inject(ActivatedRoute);
  const router = inject(Router);
  const params = toSignal(route.queryParams, { initialValue: route.snapshot.queryParams });
  const problems = signal<readonly string[]>([]);
  let seenDefinition: Definition | null = null;
  let seenUrl: Params | null = null;
  let seenState: DashboardUrlState | null = null;
  // Addresses being written: the address seen meanwhile may be the one before.
  let writing = 0;

  const write = (all: Params, next: Params) => {
    seenUrl = next;
    const others = Object.fromEntries(
      Object.entries(all).filter(([name]) => !isDashboardParam(name)),
    );
    writing++;
    void router
      .navigate([], { relativeTo: route, queryParams: { ...others, ...next }, replaceUrl: true })
      .finally(() => writing--);
  };

  effect(() => {
    const host = store.host();
    const definition = store.definition();
    const all = params();
    const state: DashboardUrlState = { filters: store.filters(), selections: store.selections() };
    if (!host) {
      return;
    }
    untracked(() => {
      const url = Object.fromEntries(
        Object.entries(all).filter(([name]) => isDashboardParam(name)),
      );
      if (definition !== seenDefinition) {
        // Another dashboard (or copy of it): the address says what of it is chosen.
        seenDefinition = definition;
        seenUrl = null;
      }
      if (writing === 0 && !sameJson(url, seenUrl)) {
        seenUrl = url;
        const read = readDashboardParams(definition, url, maxKeys);
        problems.set(read.problems);
        seenState = read.state;
        store.filters.set(read.state.filters);
        store.selections.set(read.state.selections);
        // What is left out, and values that are the defaults, go from the address.
        const written = dashboardParams(definition, read.state);
        if (!sameJson(written, url)) {
          write(all, written);
        }
      } else if (!sameJson(state, seenState)) {
        seenState = state;
        const next = dashboardParams(definition, state);
        if (!sameJson(next, seenUrl)) {
          write(all, next);
        }
      }
    });
  });
  return problems.asReadonly();
}
