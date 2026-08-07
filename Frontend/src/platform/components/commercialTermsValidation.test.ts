import { describe, expect, it } from 'vitest';
import {
  commercialConfigurationRequired,
  commercialConfigurationState,
  MIN_BILLING_MODE_REASON_LENGTH,
  validateCommercialTerms,
  type CommercialTermsForm,
} from './commercialTermsValidation';

const form = (overrides: Partial<CommercialTermsForm> = {}): CommercialTermsForm => ({
  billingMode: 'Billable',
  billingModeReason: '',
  trialEndsOn: '',
  billingStartsOn: '',
  ...overrides,
});

const inDays = (days: number): string => {
  const date = new Date();
  date.setDate(date.getDate() + days);
  return date.toISOString().slice(0, 10);
};

describe('commercial terms — the rules that decide whether a customer is charged', () => {
  it('refuses Billable on a tenant with no plan', () => {
    // Such a tenant produces statements with no subscription line at all, so "billable"
    // would be a claim the billing engine cannot honour.
    const errors = validateCommercialTerms(form({ billingMode: 'Billable' }), false);
    expect(errors.billingMode).toMatch(/no plan/i);
  });

  it('accepts Billable once a plan exists', () => {
    expect(validateCommercialTerms(form({ billingMode: 'Billable' }), true)).toEqual({});
  });

  it('demands a substantial written reason for every mode that is not Billable', () => {
    (['Trial', 'Internal', 'Partner'] as const).forEach((mode) => {
      const errors = validateCommercialTerms(
        form({ billingMode: mode, billingModeReason: 'because', trialEndsOn: inDays(30) }),
        true,
      );
      expect(errors.billingModeReason).toBeTruthy();
    });
  });

  it('accepts an exemption reason at the platform floor', () => {
    const errors = validateCommercialTerms(
      form({
        billingMode: 'Internal',
        billingModeReason: 'x'.repeat(MIN_BILLING_MODE_REASON_LENGTH),
      }),
      true,
    );
    expect(errors.billingModeReason).toBeUndefined();
  });

  it('refuses an open-ended trial', () => {
    const errors = validateCommercialTerms(
      form({ billingMode: 'Trial', billingModeReason: 'Pilot agreed with the CRO for Q3' }),
      true,
    );
    expect(errors.trialEndsOn).toMatch(/open-ended|end date/i);
  });

  it('refuses a back-dated trial end, because that is a conversion and not a trial', () => {
    const errors = validateCommercialTerms(
      form({
        billingMode: 'Trial',
        billingModeReason: 'Pilot agreed with the CRO for Q3',
        trialEndsOn: inDays(-1),
      }),
      true,
    );
    expect(errors.trialEndsOn).toMatch(/future/i);
  });

  it('accepts a trial that ends in the future with a recorded reason', () => {
    expect(
      validateCommercialTerms(
        form({
          billingMode: 'Trial',
          billingModeReason: 'Pilot agreed with the CRO for Q3',
          trialEndsOn: inDays(30),
        }),
        true,
      ),
    ).toEqual({});
  });

  it('rejects a malformed billing start date rather than sending it', () => {
    const errors = validateCommercialTerms(form({ billingStartsOn: '2026-13-45' }), true);
    expect(errors.billingStartsOn).toBeTruthy();
  });
});

describe('commercial configuration state, mirrored from the tenant row', () => {
  it('flags a billable tenant with no plan', () => {
    expect(
      commercialConfigurationState({ billingMode: 'Billable', planId: null, billingModeReason: null }),
    ).toBe('plan-missing');
  });

  it('flags a trial with no plan for the same reason', () => {
    expect(commercialConfigurationState({ billingMode: 'Trial', planId: null, billingModeReason: 'x' })).toBe(
      'plan-missing',
    );
  });

  it('flags an exemption nobody wrote down', () => {
    expect(
      commercialConfigurationState({ billingMode: 'Internal', planId: null, billingModeReason: '   ' }),
    ).toBe('exemption-unrecorded');
  });

  it('clears once the cause is fixed', () => {
    expect(commercialConfigurationState({ billingMode: 'Billable', planId: '7', billingModeReason: null })).toBe(
      'complete',
    );
    expect(
      commercialConfigurationState({
        billingMode: 'Partner',
        planId: null,
        billingModeReason: 'Reseller agreement 2026-11 signed by the CFO',
      }),
    ).toBe('complete');
  });

  it('claims nothing about a tenant provisioned before billing modes existed', () => {
    expect(commercialConfigurationRequired({ billingMode: null, planId: null, billingModeReason: null })).toBe(
      false,
    );
  });
});
