import { isAuthPage, returnTo, safeReturnUrl } from './return-url';

describe('safeReturnUrl', () => {
  it("keeps the application's addresses", () => {
    expect(safeReturnUrl('/browse/shop.customers;f=country:eq:ZA/orders?at=1')).toBe(
      '/browse/shop.customers;f=country:eq:ZA/orders?at=1',
    );
    expect(safeReturnUrl('/query/7#plan')).toBe('/query/7#plan');
    expect(safeReturnUrl('/')).toBe('/');
  });

  it('takes anything else to the start, as browsers read it', () => {
    for (const url of [
      null,
      undefined,
      '',
      'browse',
      'https://example.com/',
      '//example.com/',
      '//example.com/query/7',
      '/\\example.com/',
      '/\t/example.com/',
      '/\n/example.com/',
      '/\r\n/example.com/',
      'javascript:alert(1)',
    ]) {
      expect(safeReturnUrl(url)).toBe('/');
    }
  });

  it("doesn't return to signing in or changing the password", () => {
    expect(safeReturnUrl('/sign-in?returnUrl=%2Fx')).toBe('/');
    expect(safeReturnUrl('/change-password')).toBe('/');
    expect(safeReturnUrl('/sign-in-help')).toBe('/sign-in-help');
  });
});

describe('returnTo', () => {
  it('asks to come back, but to the start', () => {
    expect(returnTo('/query/7')).toEqual({ returnUrl: '/query/7' });
    expect(returnTo('/')).toEqual({});
    expect(returnTo('/sign-in')).toEqual({});
  });

  it('asks from the pages of signing in or the password to go back where they were to go', () => {
    expect(returnTo('/change-password?returnUrl=%2Fquery%2F7')).toEqual({
      returnUrl: '/query/7',
    });
    expect(returnTo('/sign-in?returnUrl=%2F%2Fexample.com')).toEqual({});
  });
});

describe('isAuthPage', () => {
  it('tells the pages of signing in and changing the password', () => {
    expect(isAuthPage('/sign-in?returnUrl=%2F')).toBe(true);
    expect(isAuthPage('/change-password;x=1')).toBe(true);
    expect(isAuthPage('/')).toBe(false);
  });
});
