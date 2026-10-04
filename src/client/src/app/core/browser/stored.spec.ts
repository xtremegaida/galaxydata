import { readStored, storageOf, writeStored } from './stored';

describe('values kept in the browser', () => {
  beforeEach(() => localStorage.clear());

  it('keeps and reads values', () => {
    const storage = storageOf(document);
    expect(readStored(storage, 'gd.x')).toBeNull();
    writeStored(storage, 'gd.x', '1');
    expect(readStored(storage, 'gd.x')).toBe('1');
  });

  it('goes without storage the browser refuses, or has none of', () => {
    const refusing = {
      getItem: () => {
        throw new Error('refused');
      },
      setItem: () => {
        throw new Error('full');
      },
    } as unknown as Storage;
    expect(readStored(refusing, 'gd.x')).toBeNull();
    expect(() => writeStored(refusing, 'gd.x', '1')).not.toThrow();
    expect(readStored(undefined, 'gd.x')).toBeNull();
    const blocked = {
      get defaultView(): Window {
        return {
          get localStorage(): Storage {
            throw new Error('blocked');
          },
        } as unknown as Window;
      },
    } as Document;
    expect(storageOf(blocked)).toBeUndefined();
  });
});
