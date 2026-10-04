import { DOCUMENT } from '@angular/common';
import { Injector } from '@angular/core';
import { PageLoader } from './page-loader';

describe('PageLoader', () => {
  const location = { origin: 'https://galaxy.example', assign: vi.fn(), reload: vi.fn() };
  const page = Injector.create({
    providers: [{ provide: DOCUMENT, useValue: { location } }, PageLoader],
  }).get(PageLoader);

  beforeEach(() => vi.clearAllMocks());

  it("loads the application's addresses", () => {
    page.load('/query/7?x=1');
    expect(location.assign).toHaveBeenCalledWith('https://galaxy.example/query/7?x=1');
  });

  it('loads the start in place of addresses of other sites', () => {
    for (const url of [
      'https://evil.example/',
      '//evil.example/x',
      '/\t/evil.example',
      '/\\evil.example',
    ]) {
      page.load(url);
    }
    expect(location.assign.mock.calls).toEqual([['/'], ['/'], ['/'], ['/']]);
  });

  it('loads the application anew where it is', () => {
    page.reload();
    expect(location.reload).toHaveBeenCalledOnce();
  });
});
