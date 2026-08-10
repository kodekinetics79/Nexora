import { describe, expect, it } from 'vitest';
import {
  draftFromRequestBody,
  emptyDraft,
  isTrialExpired,
  STEP_ADMIN,
  STEP_COMMERCIAL,
  STEP_COMPANY,
  STEP_REVIEW,
  todayIso,
  toProvisionInput,
  validateStep,
  type ProvisionDraft,
} from './provisionValidation';
import { toProvisionRequestBody } from '../api/client';

const dayOffset = (days: number): string => {
  const date = new Date(Date.parse(todayIso()) + days * 86_400_000);
  return date.toISOString().slice(0, 10);
};

const completeCompany = (): Partial<ProvisionDraft> => ({
  name: 'Acme Trading',
  slug: 'acme-trading',
  legalName: 'Acme Trading LLC',
  countryCode: 'AE',
  contactEmail: 'ops@acme.example',
  addressLine1: '1 Sheikh Zayed Road',
  city: 'Dubai',
});

const completeCommercial = (): Partial<ProvisionDraft> => ({
  planId: '3',
  billingMode: 'Billable',
  billingStartsOn: todayIso(),
  billingContactName: 'Dana Reed',
  billingContactEmail: 'ap@acme.example',
  accountOwnerEmail: 'owner@nexora.example',
  baseCurrencyCode: 'AED',
  timeZoneId: 'Asia/Dubai',
  locale: 'en-AE',
});

const completeAdmin = (): Partial<ProvisionDraft> => ({
  adminFirstName: 'Sam',
  adminLastName: 'Okoro',
  adminEmail: 'sam@acme.example',
});

const fullDraft = (overrides: Partial<ProvisionDraft> = {}): ProvisionDraft =>
  emptyDraft({ ...completeCompany(), ...completeCommercial(), ...completeAdmin(), ...overrides });

describe('company identity step', () => {
  it('refuses a workspace with no company behind it', () => {
    const errors = validateStep(STEP_COMPANY, emptyDraft());
    // The whole point of the rewrite: name + slug alone is not a company.
    expect(errors.name).toBeDefined();
    expect(errors.legalName).toBeDefined();
    expect(errors.countryCode).toBeDefined();
    expect(errors.contactEmail).toBeDefined();
    expect(errors.addressLine1).toBeDefined();
    expect(errors.city).toBeDefined();
  });

  it('accepts a fully identified company', () => {
    expect(validateStep(STEP_COMPANY, fullDraft())).toEqual({});
  });

  it('rejects a slug that would not survive a URL', () => {
    expect(validateStep(STEP_COMPANY, fullDraft({ slug: 'Acme Trading' })).slug).toBeDefined();
    expect(validateStep(STEP_COMPANY, fullDraft({ slug: '-acme-' })).slug).toBeDefined();
  });

  it('rejects a country code that is not a real country', () => {
    expect(validateStep(STEP_COMPANY, fullDraft({ countryCode: 'ZZ' })).countryCode).toBeDefined();
  });
});

describe('revenue guardrails', () => {
  it('will not create a billable tenant without a plan', () => {
    const errors = validateStep(STEP_COMMERCIAL, fullDraft({ billingMode: 'Billable', planId: '' }));
    expect(errors.planId).toBeDefined();
  });

  it('demands a written reason for every non-billable mode', () => {
    for (const mode of ['Trial', 'Internal', 'Partner'] as const) {
      const errors = validateStep(STEP_COMMERCIAL, fullDraft({ billingMode: mode, billingModeReason: '' }));
      expect(errors.billingModeReason).toBeDefined();
    }
  });

  it('rejects a reason too short to mean anything', () => {
    const errors = validateStep(STEP_COMMERCIAL, fullDraft({ billingMode: 'Internal', billingModeReason: 'demo' }));
    expect(errors.billingModeReason).toBeDefined();
  });

  it('makes an open-ended trial impossible to create', () => {
    const errors = validateStep(
      STEP_COMMERCIAL,
      fullDraft({ billingMode: 'Trial', billingModeReason: 'Pilot agreed with the CFO for Q3.', trialEndsOn: '' }),
    );
    expect(errors.trialEndsOn).toBeDefined();
  });

  it('rejects a trial that ends in the past', () => {
    const errors = validateStep(
      STEP_COMMERCIAL,
      fullDraft({ billingMode: 'Trial', billingModeReason: 'Pilot agreed with the CFO for Q3.', trialEndsOn: dayOffset(-1) }),
    );
    expect(errors.trialEndsOn).toBeDefined();
  });

  it('accepts a bounded trial with a stated reason', () => {
    const errors = validateStep(
      STEP_COMMERCIAL,
      fullDraft({
        billingMode: 'Trial',
        planId: '',
        billingModeReason: 'Thirty-day pilot agreed with the CFO before contract.',
        trialEndsOn: dayOffset(30),
      }),
    );
    expect(errors).toEqual({});
  });

  it('always requires an internal account owner', () => {
    expect(validateStep(STEP_COMMERCIAL, fullDraft({ accountOwnerEmail: '' })).accountOwnerEmail).toBeDefined();
  });

  it('rejects a contract that ends before it starts', () => {
    const errors = validateStep(
      STEP_COMMERCIAL,
      fullDraft({ contractStartOn: dayOffset(30), contractEndOn: dayOffset(10) }),
    );
    expect(errors.contractEndOn).toBeDefined();
  });
});

describe('founding administrator step', () => {
  it('requires a named administrator with a valid email', () => {
    const errors = validateStep(STEP_ADMIN, emptyDraft());
    expect(errors.adminFirstName).toBeDefined();
    expect(errors.adminLastName).toBeDefined();
    expect(errors.adminEmail).toBeDefined();
  });

  it('treats a blank password on the password path as "generate one for me"', () => {
    const errors = validateStep(STEP_ADMIN, fullDraft({ adminActivation: 'password', adminPassword: '' }));
    expect(errors.adminPassword).toBeUndefined();
  });

  it('holds a typed password to the same policy the server enforces', () => {
    const errors = validateStep(STEP_ADMIN, fullDraft({ adminActivation: 'password', adminPassword: 'short1!A' }));
    expect(errors.adminPassword).toBeDefined();
  });
});

describe('review step', () => {
  it('re-checks every earlier step so a commit cannot be assembled by going back', () => {
    const errors = validateStep(STEP_REVIEW, fullDraft({ planId: '', city: '' }));
    expect(errors.planId).toBeDefined();
    expect(errors.city).toBeDefined();
  });

  it('is clean when every step is', () => {
    expect(validateStep(STEP_REVIEW, fullDraft())).toEqual({});
  });
});

describe('mapping the draft onto the wire contract', () => {
  it('never sends a trial end date for a tenant that is not on trial', () => {
    const input = toProvisionInput(fullDraft({ billingMode: 'Billable', trialEndsOn: dayOffset(30) }));
    expect(input.trialEndsOn).toBeNull();
  });

  it('never sends a password on the invite path', () => {
    const input = toProvisionInput(fullDraft({ adminActivation: 'invite', adminPassword: 'Correct-Horse-99!' }));
    expect(input.adminPassword).toBeNull();
  });

  it('sends a typed password on the password path', () => {
    const input = toProvisionInput(fullDraft({ adminActivation: 'password', adminPassword: 'Correct-Horse-99!' }));
    expect(input.adminPassword).toBe('Correct-Horse-99!');
  });

  it('drops the billing-mode reason once the tenant is billable', () => {
    const input = toProvisionInput(fullDraft({ billingMode: 'Billable', billingModeReason: 'stale text' }));
    expect(input.billingModeReason).toBeNull();
  });

  it('normalises the country and currency codes the backend keys off', () => {
    const input = toProvisionInput(fullDraft({ countryCode: 'ae', baseCurrencyCode: 'aed' }));
    expect(input.countryCode).toBe('AE');
    expect(input.baseCurrencyCode).toBe('AED');
  });
});

describe('trial expiry', () => {
  it('flags a trial whose end date has passed', () => {
    expect(isTrialExpired('Trial', dayOffset(-1))).toBe(true);
  });

  it('does not flag a trial that ends today or later', () => {
    expect(isTrialExpired('Trial', todayIso())).toBe(false);
    expect(isTrialExpired('Trial', dayOffset(1))).toBe(false);
  });

  it('says nothing about tenants that are not on trial', () => {
    expect(isTrialExpired('Billable', dayOffset(-90))).toBe(false);
    expect(isTrialExpired(null, dayOffset(-90))).toBe(false);
  });
});

describe('draft save and resume', () => {
  it('restores every field the operator typed, so nothing goes missing between sessions', () => {
    const original = fullDraft({
      billingMode: 'Trial',
      billingModeReason: 'Pilot agreed with the CRO for Q3 evaluation',
      trialEndsOn: dayOffset(45),
      rateCardId: '9',
      contractStartOn: dayOffset(1),
      contractEndOn: dayOffset(365),
      paymentTermsDays: '30',
      purchaseOrderReference: 'PO-88123',
      billingAddress: 'PO Box 1, Dubai',
      dataRegion: 'me-central-1',
      adminJobTitle: 'Head of Procurement',
      adminPhone: '+971 4 555 0100',
      industry: 'Construction',
      registrationNumber: 'CN-1122',
      taxNumber: 'TRN-9988',
      website: 'acme.example',
      logoUrl: 'https://acme.example/logo.png',
      addressLine2: 'Level 12',
      stateProvince: 'Dubai',
      postalCode: '00000',
      phone: '+971 4 555 0000',
    });

    const resumed = draftFromRequestBody(toProvisionRequestBody(toProvisionInput(original)));

    // The password is the one deliberate omission — see draftFromRequestBody.
    expect(resumed).toEqual({ ...original, adminPassword: '' });
  });

  it('never carries a credential into a stored draft', () => {
    const original = fullDraft({ adminActivation: 'password', adminPassword: 'Correct-Horse-99!' });
    const body = toProvisionRequestBody(toProvisionInput(original));

    expect(draftFromRequestBody(body).adminPassword).toBe('');
  });

  it('survives a draft saved before a field existed', () => {
    // Drafts outlive schema changes; a payload missing half its fields must resume as a
    // half-filled form, not as a crash on the operator's only copy of the work.
    const sparse = draftFromRequestBody({
      name: 'Acme Trading',
      adminEmail: 'admin@acme.example',
      adminFirstName: 'Dana',
      adminLastName: 'Reed',
    } as Parameters<typeof draftFromRequestBody>[0]);

    expect(sparse.name).toBe('Acme Trading');
    expect(sparse.slug).toBe('');
    expect(sparse.billingMode).toBe('Billable');
    // KSA-first provisioning defaults. The previous expectation pinned 'USD'/'en-US',
    // which silently provisioned every new Saudi tenant in the wrong currency.
    expect(sparse.baseCurrencyCode).toBe('SAR');
    expect(sparse.locale).toBe('en-SA');
    expect(sparse.adminActivation).toBe('invite');
  });
});
