import { Injector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { connectionOf, kindOf } from '../../../../testing/connections';
import { ConnectionDraft, isSecretKeyword, type Connection } from './connection-draft';

describe('ConnectionDraft', () => {
  const draftOf = (kind: string, connection: Connection | null = null) =>
    new ConnectionDraft(kindOf(kind), connection, TestBed.inject(Injector));

  const postgres = connectionOf({
    id: 2,
    alias: 'pg',
    kind: 'postgres',
    settings: {
      Host: 'db.example.com',
      Database: 'shop',
      Username: 'reader',
      'Max Pool Size': '20',
    },
    secrets: { Password: { hasValue: true }, Token: { hasValue: true } },
    connectionString:
      'Host=db.example.com;Database=shop;Username=reader;Max Pool Size=20;Password=********;Token=********',
    options: { trustForeignKeys: 'true' },
  });

  it("starts a new connection with the kind's fields empty, read-only, and its secrets to set", () => {
    const draft = draftOf('postgres');
    const model = draft.model();
    expect(draft.isNew).toBe(true);
    expect(model.mode).toBe('form');
    expect(model.isReadOnly).toBe(true);
    expect(model.settings['Host']).toBe('');
    expect(model.settings['Port']).toBe('');
    expect(model.secrets).toEqual({ Password: { action: 'set', text: '', stored: false } });
    expect(model.options).toEqual({ trustForeignKeys: '' });
    expect(draft.dirty()).toBe(false);
    expect(draft.input()).toEqual({
      mode: 'form',
      settings: {},
      secrets: {},
      options: {},
      isReadOnly: true,
    });
  });

  it('reads a stored connection: described settings in their fields, the others apart, secrets kept', () => {
    const draft = draftOf('postgres', postgres);
    const model = draft.model();
    expect(model.alias).toBe('pg');
    expect(model.settings['Host']).toBe('db.example.com');
    expect(model.others).toEqual([{ key: 'Max Pool Size', value: '20' }]);
    expect(model.secrets['Password']).toEqual({ action: 'keep', text: '', stored: true });
    expect(model.secrets['Token']).toEqual({ action: 'keep', text: '', stored: true });
    expect(draft.otherSecrets()).toEqual(['Token']);
    expect(draft.optionFlag(kindOf('postgres').options[0])).toBe(true);
    expect(draft.input()).toEqual({
      mode: 'form',
      settings: {
        Host: 'db.example.com',
        Database: 'shop',
        Username: 'reader',
        'Max Pool Size': '20',
      },
      secrets: { Password: { action: 'keep' }, Token: { action: 'keep' } },
      options: { trustForeignKeys: 'true' },
      isReadOnly: true,
    });
  });

  it('sets, clears and keeps secrets, and sends other settings named like secrets as secrets', () => {
    const draft = draftOf('postgres', postgres);
    draft.model.update((model) => ({
      ...model,
      secrets: {
        Password: { action: 'set', text: 'a new one', stored: true },
        Token: { action: 'clear', text: '', stored: true },
      },
      others: [
        { key: 'Max Pool Size', value: '20' },
        { key: 'Api Secret', value: 'shh' },
        { key: '', value: '' },
      ],
    }));
    expect(draft.dirty()).toBe(true);
    const input = draft.input();
    expect(input.secrets).toEqual({
      Password: { action: 'set', value: 'a new one' },
      Token: { action: 'clear' },
      'Api Secret': { action: 'set', value: 'shh' },
    });
    expect(input.settings).not.toHaveProperty('Api Secret');
  });

  it('clears a stored secret the connection string leaves out, coming back to the form', () => {
    // Saved as a connection string; the password removed from it, then back to the form.
    const draft = draftOf('postgres', { ...postgres, mode: 'raw' });
    draft.converted({
      mode: 'form',
      settings: { Host: 'db.example.com' },
      secrets: {},
      isReadOnly: true,
    });
    expect(draft.model().secrets['Password']).toEqual({ action: 'clear', text: '', stored: true });
    expect(draft.input().secrets).toEqual({
      Password: { action: 'clear' },
      Token: { action: 'clear' },
    });
  });

  it('keeps a clear through a conversion to the string and back', () => {
    const draft = draftOf('postgres', postgres);
    draft.model.update((model) => ({
      ...model,
      secrets: { ...model.secrets, Password: { action: 'clear', text: '', stored: true } },
    }));
    expect(draft.input().secrets?.['Password']).toEqual({ action: 'clear' });
    // The server leaves a cleared secret out of the string, and says nothing of it coming back.
    draft.converted({
      mode: 'raw',
      connectionString: 'Host=db.example.com;Token=********',
      secrets: { Token: { action: 'keep' } },
      isReadOnly: true,
    });
    draft.converted({
      mode: 'form',
      settings: { Host: 'db.example.com' },
      secrets: { Token: { action: 'keep' } },
      isReadOnly: true,
    });
    expect(draft.model().secrets['Password'].action).toBe('clear');
    expect(draft.model().secrets['Token'].action).toBe('keep');
  });

  it('sets a secret not stored once it is typed, whatever a conversion said of it', () => {
    const draft = draftOf('postgres');
    draft.converted({
      mode: 'form',
      settings: {},
      secrets: { Password: { action: 'clear' } },
      isReadOnly: true,
    });
    expect(draft.model().secrets['Password']).toEqual({ action: 'set', text: '', stored: false });
    draft.model.update((model) => ({
      ...model,
      secrets: { Password: { action: 'keep', text: 'typed', stored: false } },
    }));
    expect(draft.input().secrets).toEqual({ Password: { action: 'set', value: 'typed' } });
  });

  it('sends with a connection string only the secrets it still masks', () => {
    const draft = draftOf('postgres', postgres);
    draft.converted({
      mode: 'raw',
      connectionString: 'Host=db.example.com;Password=********;Token=********',
      secrets: { Password: { action: 'set', value: 'new' }, Token: { action: 'keep' } },
      isReadOnly: true,
    });
    draft.model.update((model) => ({
      ...model,
      connectionString: 'Host=db.example.com; token = ********',
    }));
    expect(draft.input().secrets).toEqual({ Token: { action: 'keep' } });
  });

  it('clears a stored secret whose field is hidden, as the server would keep it', () => {
    const draft = draftOf(
      'sqlserver',
      connectionOf({
        kind: 'sqlserver',
        settings: { 'Data Source': 'db', 'User ID': 'reader' },
        secrets: { Password: { hasValue: true } },
      }),
    );
    expect(draft.input().secrets).toEqual({ Password: { action: 'keep' } });
    draft.setFlag(
      kindOf('sqlserver').fields.find((field) => field.key === 'Integrated Security')!,
      true,
    );
    expect(draft.input().settings).toEqual({ 'Data Source': 'db', 'Integrated Security': 'True' });
    expect(draft.input().secrets).toEqual({ Password: { action: 'clear' } });
  });

  it('shows fields as what they depend on says, and leaves hidden ones out', () => {
    const draft = draftOf('sqlserver');
    const kind = kindOf('sqlserver');
    const field = (key: string) => kind.fields.find((candidate) => candidate.key === key)!;
    draft.model.update((model) => ({
      ...model,
      settings: { ...model.settings, 'Data Source': 'db', 'User ID': 'reader' },
      secrets: { Password: { action: 'set', text: 'pw', stored: false } },
    }));
    expect(draft.visible(field('User ID'))).toBe(true);
    expect(draft.flag(field('Integrated Security'))).toBe(false);

    draft.setFlag(field('Integrated Security'), true);
    expect(draft.model().settings['Integrated Security']).toBe('True');
    expect(draft.visible(field('User ID'))).toBe(false);
    expect(draft.input().settings).toEqual({ 'Data Source': 'db', 'Integrated Security': 'True' });
    expect(draft.input().secrets).toEqual({});

    draft.setFlag(field('Integrated Security'), false);
    expect(draft.model().settings['Integrated Security']).toBe('');
  });

  it('writes bools as their defaults are written, and leaves the default unset', () => {
    const draft = draftOf('excel');
    const [headerRow, allText] = kindOf('excel').options;
    draft.setOptionFlag(headerRow, false);
    draft.setOptionFlag(allText, true);
    expect(draft.input().options).toEqual({ headerRow: 'false', allText: 'true' });
    draft.setOptionFlag(headerRow, true);
    expect(draft.input().options).toEqual({ allText: 'true' });
  });

  it('keeps a folder of workbooks read-only', () => {
    const draft = draftOf('excel');
    draft.setReadOnly(false);
    expect(draft.model().isReadOnly).toBe(true);
    expect(draft.input().isReadOnly).toBe(true);
  });

  it('takes a conversion to a connection string, and sends its secrets with it', () => {
    const draft = draftOf('postgres', postgres);
    draft.converted({
      mode: 'raw',
      connectionString: 'Host=db.example.com;Password=********',
      secrets: { Password: { action: 'keep' } },
      isReadOnly: true,
    });
    expect(draft.model().mode).toBe('raw');
    expect(draft.input()).toEqual({
      mode: 'raw',
      connectionString: 'Host=db.example.com;Password=********',
      secrets: { Password: { action: 'keep' } },
      options: { trustForeignKeys: 'true' },
      isReadOnly: true,
    });
  });

  it('takes a conversion back to the form: settings in their fields, secrets set, kept or cleared', () => {
    const draft = draftOf('postgres', postgres);
    draft.converted({
      mode: 'form',
      settings: { Host: 'other.example.com', 'Search Path': 'sales', Pooling: 'false' },
      secrets: {
        password: { action: 'set', value: 'typed out' },
        Token: { action: 'clear' },
      },
      isReadOnly: true,
    });
    const model = draft.model();
    expect(model.mode).toBe('form');
    expect(model.settings['Host']).toBe('other.example.com');
    expect(model.settings['Search Path']).toBe('sales');
    expect(model.settings['Database']).toBe('');
    expect(model.others).toEqual([{ key: 'Pooling', value: 'false' }]);
    expect(model.secrets['Password']).toEqual({ action: 'set', text: 'typed out', stored: true });
    expect(model.secrets['Token']).toEqual({ action: 'clear', text: '', stored: true });
  });

  it('checks the alias, needed settings, numbers, and secrets to change', () => {
    const draft = draftOf('postgres');
    const errors = (field: () => { errors: () => { message?: string }[] }) =>
      field()
        .errors()
        .map((error) => error.message);
    expect(errors(draft.form.alias)).toEqual(['Give an alias']);
    expect(errors(draft.form.settings['Host'])).toEqual(['Host is needed']);
    draft.model.update((model) => ({
      ...model,
      alias: 'not',
      settings: { ...model.settings, Port: '70000' },
    }));
    expect(errors(draft.form.alias)[0]).toContain('An alias is a plain name');
    expect(errors(draft.form.settings['Port'])).toEqual(['At most 65535']);
    draft.model.update((model) => ({
      ...model,
      alias: '_shop2',
      settings: { ...model.settings, Port: '5x' },
    }));
    expect(errors(draft.form.alias)).toEqual([]);
    expect(errors(draft.form.settings['Port'])).toEqual(['Give a whole number']);

    const stored = draftOf('postgres', postgres);
    stored.model.update((model) => ({
      ...model,
      secrets: { ...model.secrets, Password: { action: 'set', text: '', stored: true } },
    }));
    expect(errors(stored.form.secrets['Password'].text)).toEqual([
      'Give the new password, or keep it',
    ]);
    expect(errors(stored.form.alias)).toEqual([]);
  });

  it('checks the connection string, not the fields, as a connection string', () => {
    const draft = draftOf('postgres');
    draft.model.update((model) => ({ ...model, mode: 'raw' }));
    expect(draft.form.settings['Host']().errors()).toEqual([]);
    expect(
      draft.form
        .connectionString()
        .errors()
        .map((error) => error.message),
    ).toEqual(['Write the connection string']);
  });

  it("names the fields a problem's errors can be shown on as the API names them", () => {
    const fields = draftOf('postgres').errorFields();
    expect(Object.keys(fields)).toEqual(
      expect.arrayContaining([
        'alias',
        'connectionString',
        'settings.Host',
        'settings.SSL Mode',
        'settings.Root Certificate',
        'secrets.Password',
      ]),
    );
    expect(Object.keys(fields)).not.toContain('settings.otherSettings');
    expect(Object.keys(draftOf('sqlite').errorFields())).toContain('options.enforceForeignKeys');
  });
});

describe('isSecretKeyword', () => {
  it('takes keywords as the server does', () => {
    expect(['Password', 'pwd', 'Access Token', 'client_secret'].map(isSecretKeyword)).toEqual([
      true,
      true,
      true,
      true,
    ]);
    expect(isSecretKeyword('Host')).toBe(false);
  });
});
