import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { CatalogVersion, catalogVersionInterceptor } from './catalog-version';

describe('catalogVersionInterceptor', () => {
  let http: HttpTestingController;
  let client: HttpClient;
  let versions: CatalogVersion;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([catalogVersionInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
    versions = TestBed.inject(CatalogVersion);
  });

  afterEach(() => http.verify());

  it("keeps the version answers give, failures' too", async () => {
    expect(versions.version()).toBeNull();

    const first = firstValueFrom(client.get('/api/catalog'));
    http.expectOne('/api/catalog').flush({}, { headers: { 'X-Catalog-Version': 'a1' } });
    await first;
    expect(versions.version()).toBe('a1');

    const second = firstValueFrom(client.get('/api/health'));
    http.expectOne('/api/health').flush({});
    await second;
    expect(versions.version()).toBe('a1');

    const failed = firstValueFrom(client.post('/api/browse/page', {}));
    http
      .expectOne('/api/browse/page')
      .flush(
        { code: 'not-found', title: 'No such entity' },
        { status: 404, statusText: 'Not Found', headers: { 'X-Catalog-Version': 'b2' } },
      );
    await expect(failed).rejects.toBeTruthy();
    expect(versions.version()).toBe('b2');
  });
});
