import { Injector, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { followCatalog } from './catalog-changes';
import { CatalogVersion } from './catalog-version';

describe('followCatalog', () => {
  let versions: CatalogVersion;
  let reloads: number;
  let follow: <T>() => import('rxjs').MonoTypeOperatorFunction<T>;

  beforeEach(() => {
    versions = TestBed.inject(CatalogVersion);
    reloads = 0;
    follow = runInInjectionContext(TestBed.inject(Injector), () => followCatalog(() => reloads++));
  });

  /** An answer read through the operator, given with the catalog's version (as the interceptor keeps it). */
  function answer(version: string): void {
    const answers = new Subject<string>();
    answers.pipe(follow()).subscribe();
    versions.seen(version);
    answers.next('read');
  }

  it('reads again when the catalog changes from the version read', () => {
    answer('v1');
    TestBed.tick();
    expect(reloads).toBe(0);
    versions.seen('v2');
    TestBed.tick();
    expect(reloads).toBe(1);
  });

  it("doesn't read again for an answer that is itself the first to say the catalog changed", () => {
    answer('v1');
    TestBed.tick();
    answer('v2');
    TestBed.tick();
    expect(reloads).toBe(0);
  });

  it('waits for a first answer', () => {
    versions.seen('v1');
    TestBed.tick();
    versions.seen('v2');
    TestBed.tick();
    expect(reloads).toBe(0);
  });
});
