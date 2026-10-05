import { Injector, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { form } from '@angular/forms/signals';
import { fieldErrors } from './server-errors';

describe('fieldErrors', () => {
  it("puts a problem's errors on the form's fields, and keeps the others' messages", () => {
    const fields = form(signal({ userName: '', password: '' }), {
      injector: TestBed.inject(Injector),
    });
    const { errors, others } = fieldErrors(
      {
        status: 400,
        code: 'invalid-request',
        title: 'One or more validation errors occurred.',
        errors: {
          userName: ['The UserName field is required.', 'It is too long.'],
          role: ['Not a role.'],
          constructor: ['Not a field.'],
        },
      },
      { userName: fields.userName, password: fields.password },
    );
    expect(errors.map((error) => [error.fieldTree, error.message])).toEqual([
      [fields.userName, 'The UserName field is required.'],
      [fields.userName, 'It is too long.'],
    ]);
    expect(others).toEqual(['Not a role.', 'Not a field.']);
  });

  it("words the others' messages as asked, to say what they are about", () => {
    const fields = form(signal({ name: '' }), { injector: TestBed.inject(Injector) });
    const { others } = fieldErrors(
      {
        status: 400,
        code: 'invalid-request',
        title: 'One or more validation errors occurred.',
        errors: { name: ['Taken.'], 'columns[0].type': ["'dat' isn't a type."] },
      },
      { name: fields.name },
      (field, message) => `${field}: ${message}`,
    );
    expect(others).toEqual(["columns[0].type: 'dat' isn't a type."]);
  });

  it('has nothing for a problem without errors', () => {
    expect(fieldErrors({ status: 500, code: 'internal-error', title: 'Oops' }, {})).toEqual({
      errors: [],
      others: [],
    });
  });
});
