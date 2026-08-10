import { describe, expect, it } from 'vitest';
import {
  EMPTY_ACCOUNT_CONTACT,
  MAX_PAYMENT_TERMS_DAYS,
  accountContactProblem,
  type AccountContactForm,
} from './accountContactValidation';

const form = (overrides: Partial<AccountContactForm> = {}): AccountContactForm => ({
  ...EMPTY_ACCOUNT_CONTACT,
  billingContactEmail: 'ap@buyer.example',
  paymentTermsDays: '30',
  ...overrides,
});

describe('accountContactProblem', () => {
  it('accepts a complete, ordinary set of invoicing details', () => {
    expect(accountContactProblem(form(), 'Billable')).toBeNull();
  });

  it('refuses to clear the invoice recipient on a tenant that is charged', () => {
    // Not a required-field nicety. Invoicing refuses to issue without a recipient, so an empty
    // value silently stops the customer being billed — and the offboarding readiness gate wants a
    // finalized invoice, so it also strands them.
    const problem = accountContactProblem(form({ billingContactEmail: '' }), 'Billable');
    expect(problem).toContain('offboarding');
  });

  it('allows an operator-owned Internal tenant to carry no invoice recipient', () => {
    // The exemption has to exist or the rule above would make the operator's own demo and QA
    // workspaces unmaintainable through the console.
    expect(accountContactProblem(form({ billingContactEmail: '' }), 'Internal')).toBeNull();
  });

  it.each(['not-an-address', 'ap@buyer', 'ap @buyer.example', 'a@@b.example'])(
    'refuses %s as an invoice recipient',
    (value) => {
      expect(accountContactProblem(form({ billingContactEmail: value }), 'Billable')).toContain(
        'not an email address',
      );
    },
  );

  it('checks the account owner too, but only when one is supplied', () => {
    expect(accountContactProblem(form({ accountOwnerEmail: 'nobody' }), 'Billable'))
      .toContain('account owner');
    expect(accountContactProblem(form({ accountOwnerEmail: '' }), 'Billable')).toBeNull();
  });

  it.each(['-1', '366', '3650', '30.5', 'thirty'])(
    'refuses %s as payment terms',
    (value) => {
      // The figure is added to the issue date to compute the due date. Outside this range the
      // invoice is either overdue the moment it is issued or effectively never due, and the second
      // one never looks like a problem in any collections view.
      expect(accountContactProblem(form({ paymentTermsDays: value }), 'Billable'))
        .toContain('Payment terms');
    },
  );

  it('accepts zero, because due on receipt is a real commercial term', () => {
    // A range check that also refuses a legitimate value is not a stricter control, it is a
    // different defect.
    expect(accountContactProblem(form({ paymentTermsDays: '0' }), 'Billable')).toBeNull();
    expect(accountContactProblem(form({ paymentTermsDays: String(MAX_PAYMENT_TERMS_DAYS) }), 'Billable'))
      .toBeNull();
  });

  it('accepts blank payment terms, which fall back to the platform default', () => {
    expect(accountContactProblem(form({ paymentTermsDays: '' }), 'Billable')).toBeNull();
  });

  it('refuses a contract that ends before or on the day it starts', () => {
    expect(accountContactProblem(
      form({ contractStartOn: '2026-01-01', contractEndOn: '2026-01-01' }), 'Billable',
    )).toContain('after the start date');

    expect(accountContactProblem(
      form({ contractStartOn: '2026-01-01', contractEndOn: '2027-01-01' }), 'Billable',
    )).toBeNull();
  });

  it('leaves an open-ended contract alone', () => {
    // One date without the other is an ordinary state — a contract with a start and no agreed end.
    expect(accountContactProblem(form({ contractStartOn: '2026-01-01' }), 'Billable')).toBeNull();
    expect(accountContactProblem(form({ contractEndOn: '2027-01-01' }), 'Billable')).toBeNull();
  });
});
