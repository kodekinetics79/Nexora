// ---------------------------------------------------------------------------
// Validation for the tenant-provisioning wizard.
//
// Kept free of React so the revenue guardrails can be asserted directly: a
// billable tenant without a plan, and a trial without an end date, are the two
// mistakes that cost money silently, and they must be unrepresentable rather
// than merely discouraged.
// ---------------------------------------------------------------------------

import { isPasswordAcceptable, PASSWORD_MAX_LENGTH, passwordProblem } from '../../utils/passwordPolicy';
import { COUNTRY_CODES, isValidLocale } from './localeData';
import { BILLING_MODES } from '../types';
import type {
  AdminActivationMode,
  BillingMode,
  ProvisionTenantInput,
  ProvisionTenantRequestBody,
} from '../types';

/**
 * Form state. Everything is a string because that is what the inputs hold;
 * `toProvisionInput` does the one conversion to the wire contract, so no
 * component has to reason about "" versus null.
 */
export interface ProvisionDraft {
  // Step 1 — company identity
  name: string;
  slug: string;
  legalName: string;
  registrationNumber: string;
  taxNumber: string;
  countryCode: string;
  industry: string;
  website: string;
  addressLine1: string;
  addressLine2: string;
  city: string;
  stateProvince: string;
  postalCode: string;
  phone: string;
  contactEmail: string;
  logoUrl: string;

  // Step 2 — commercial terms
  planId: string;
  billingMode: BillingMode;
  billingModeReason: string;
  rateCardId: string;
  billingStartsOn: string;
  trialEndsOn: string;
  contractStartOn: string;
  contractEndOn: string;
  paymentTermsDays: string;
  purchaseOrderReference: string;
  billingContactName: string;
  billingContactEmail: string;
  billingAddress: string;
  accountOwnerEmail: string;
  baseCurrencyCode: string;
  timeZoneId: string;
  locale: string;
  dataRegion: string;

  // Step 3 — founding administrator
  adminFirstName: string;
  adminLastName: string;
  adminEmail: string;
  adminJobTitle: string;
  adminPhone: string;
  adminActivation: AdminActivationMode;
  adminPassword: string;
}

export type DraftField = keyof ProvisionDraft;
export type FieldErrors = Partial<Record<DraftField, string>>;

export const WIZARD_STEPS = [
  { key: 'company', label: 'Company identity' },
  { key: 'commercial', label: 'Commercial terms' },
  { key: 'admin', label: 'Founding administrator' },
  { key: 'review', label: 'Review & provision' },
] as const;

export const STEP_COMPANY = 0;
export const STEP_COMMERCIAL = 1;
export const STEP_ADMIN = 2;
export const STEP_REVIEW = 3;

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const SLUG = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/;

/**
 * The currency Nexora BILLS in — a platform constant carried on every rate card, because
 * `PlatformBillingController` refuses any other on create and update ("v1 billing is USD-only").
 *
 * It is NOT the tenant's base currency. `baseCurrencyCode` is the tenant's FUNCTIONAL currency —
 * what it quotes, reports and keeps its ledger in (SAR for a Saudi client) — and activation
 * compares the pinned rate card to THIS constant, never to the tenant's own currency. The two
 * used to be conflated, which made every non-USD tenant permanently unactivatable.
 *
 * Named rather than inlined so the day multi-currency billing lands, the compiler lists every
 * place that assumed one.
 */
export const BILLABLE_CURRENCY = 'USD';

/**
 * Default FUNCTIONAL currency offered by the provisioning wizard.
 *
 * USD by the product owner's decision on 2026-09-03: the operator picks the tenant's own
 * currency deliberately rather than inheriting a market assumption. The decoupling from
 * BILLABLE_CURRENCY is what matters and is unchanged — a tenant set to SAR, AED or PKR here
 * still activates, which it could not before.
 */
export const DEFAULT_FUNCTIONAL_CURRENCY = 'USD';

const trimmed = (value: string) => value.trim();
const isBlank = (value: string) => trimmed(value).length === 0;

/** Today in the local calendar, as the `yyyy-mm-dd` a date input speaks. */
export const todayIso = (): string => {
  const now = new Date();
  const local = new Date(now.getTime() - now.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 10);
};

const isIsoDate = (value: string) => /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(value));

export const slugify = (value: string): string =>
  value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/(^-|-$)/g, '');

/** A trial whose end date is already behind us — the state that must be loud everywhere. */
export const isTrialExpired = (
  billingMode: BillingMode | null,
  trialEndsOn: string | null,
): boolean => {
  if (billingMode !== 'Trial' || !trialEndsOn) return false;
  const end = Date.parse(trialEndsOn);
  return !Number.isNaN(end) && end < Date.parse(todayIso());
};

export const emptyDraft = (defaults: Partial<ProvisionDraft> = {}): ProvisionDraft => ({
  name: '',
  slug: '',
  legalName: '',
  registrationNumber: '',
  taxNumber: '',
  countryCode: '',
  industry: '',
  website: '',
  addressLine1: '',
  addressLine2: '',
  city: '',
  stateProvince: '',
  postalCode: '',
  phone: '',
  contactEmail: '',
  logoUrl: '',

  planId: '',
  // Billable is the default because every other mode is revenue the company
  // chose to forgo, and that should be a deliberate act, not an accident of
  // whichever option happened to sort first.
  billingMode: 'Billable',
  billingModeReason: '',
  rateCardId: '',
  billingStartsOn: '',
  trialEndsOn: '',
  contractStartOn: '',
  contractEndOn: '',
  paymentTermsDays: '',
  purchaseOrderReference: '',
  billingContactName: '',
  billingContactEmail: '',
  billingAddress: '',
  accountOwnerEmail: '',
  // The tenant's FUNCTIONAL currency, defaulted rather than assumed — the operator sets the
  // tenant's real currency during provisioning. This column used to be compared against the
  // USD-only rate card at activation, which made every non-USD tenant permanently
  // unactivatable; activation now compares the rate card to BILLABLE_CURRENCY instead.
  baseCurrencyCode: DEFAULT_FUNCTIONAL_CURRENCY,
  timeZoneId: '',
  locale: 'en-SA',
  dataRegion: '',

  adminFirstName: '',
  adminLastName: '',
  adminEmail: '',
  adminJobTitle: '',
  adminPhone: '',
  adminActivation: 'invite',
  adminPassword: '',
  ...defaults,
});

function validateCompany(draft: ProvisionDraft): FieldErrors {
  const errors: FieldErrors = {};

  if (trimmed(draft.name).length < 2) errors.name = 'Enter the organization name.';

  if (isBlank(draft.slug)) errors.slug = 'A slug is required — it becomes the workspace address.';
  else if (!SLUG.test(trimmed(draft.slug)) || trimmed(draft.slug).length < 2) {
    errors.slug = 'Lowercase letters, numbers and hyphens only, starting and ending with a letter or number.';
  }

  if (isBlank(draft.legalName)) errors.legalName = 'Enter the registered legal name — this is the name on the invoice.';

  if (isBlank(draft.countryCode)) errors.countryCode = 'Select the country of registration.';
  else if (!COUNTRY_CODES.includes(trimmed(draft.countryCode).toUpperCase())) {
    errors.countryCode = 'Select a country from the list.';
  }

  if (isBlank(draft.contactEmail)) errors.contactEmail = 'Enter the company contact email.';
  else if (!EMAIL.test(trimmed(draft.contactEmail))) errors.contactEmail = 'Enter a valid email address.';

  if (isBlank(draft.addressLine1)) errors.addressLine1 = 'Enter the street address.';
  if (isBlank(draft.city)) errors.city = 'Enter the city.';

  if (!isBlank(draft.phone) && trimmed(draft.phone).replace(/[^\d]/g, '').length < 7) {
    errors.phone = 'Enter a full phone number, including the country code.';
  }
  if (!isBlank(draft.website) && !/^(https?:\/\/)?[\w-]+(\.[\w-]+)+([/?#].*)?$/i.test(trimmed(draft.website))) {
    errors.website = 'Enter a valid web address, for example acme.com.';
  }
  if (!isBlank(draft.logoUrl) && !/^https?:\/\/\S+$/i.test(trimmed(draft.logoUrl))) {
    errors.logoUrl = 'Enter a full https:// link to the logo image.';
  }

  return errors;
}

function validateCommercial(draft: ProvisionDraft): FieldErrors {
  const errors: FieldErrors = {};
  const billable = draft.billingMode === 'Billable';

  // Revenue guardrail: a billable tenant with no plan has no quotas and no
  // price, so it consumes the platform for free until somebody notices.
  if (billable && isBlank(draft.planId)) {
    errors.planId = 'A billable tenant must be on a plan — otherwise it runs with no quotas and no price.';
  }

  // Revenue guardrail: anything that is not Billable is service given away, and
  // the decision has to be attributable to a person and a reason.
  if (!billable) {
    if (trimmed(draft.billingModeReason).length < 15) {
      errors.billingModeReason = `Explain why this tenant is ${draft.billingMode} rather than billable (at least 15 characters).`;
    }
  }

  // Revenue guardrail: an open-ended trial never ends, so it must not exist.
  if (draft.billingMode === 'Trial') {
    if (isBlank(draft.trialEndsOn)) {
      errors.trialEndsOn = 'A trial must have an end date. An open-ended trial cannot be created.';
    } else if (!isIsoDate(draft.trialEndsOn)) {
      errors.trialEndsOn = 'Enter a valid date.';
    } else if (Date.parse(draft.trialEndsOn) <= Date.parse(todayIso())) {
      errors.trialEndsOn = 'The trial end date must be in the future.';
    }
  }

  if (billable) {
    if (isBlank(draft.billingStartsOn)) errors.billingStartsOn = 'Set the date billing starts.';
    else if (!isIsoDate(draft.billingStartsOn)) errors.billingStartsOn = 'Enter a valid date.';

    if (isBlank(draft.billingContactName)) errors.billingContactName = 'Name the person who receives invoices.';
    if (isBlank(draft.billingContactEmail)) errors.billingContactEmail = 'Enter the billing contact email.';
    else if (!EMAIL.test(trimmed(draft.billingContactEmail))) errors.billingContactEmail = 'Enter a valid email address.';
  } else if (!isBlank(draft.billingContactEmail) && !EMAIL.test(trimmed(draft.billingContactEmail))) {
    errors.billingContactEmail = 'Enter a valid email address.';
  }

  if (isBlank(draft.accountOwnerEmail)) {
    // Asks for the EMAIL, because that is the only thing this field accepts. "Name the internal
    // owner" read as an instruction to type a name, and the operator who did got told to enter a
    // valid email address on a field that had just asked them for a person.
    errors.accountOwnerEmail = "Enter the internal owner's email address — an unowned account is an unbilled account.";
  } else if (!EMAIL.test(trimmed(draft.accountOwnerEmail))) {
    errors.accountOwnerEmail = 'Enter a valid email address.';
  }

  if (!isBlank(draft.contractStartOn) && !isIsoDate(draft.contractStartOn)) {
    errors.contractStartOn = 'Enter a valid date.';
  }
  if (!isBlank(draft.contractEndOn)) {
    if (!isIsoDate(draft.contractEndOn)) errors.contractEndOn = 'Enter a valid date.';
    else if (!isBlank(draft.contractStartOn) && Date.parse(draft.contractEndOn) <= Date.parse(draft.contractStartOn)) {
      errors.contractEndOn = 'The contract must end after it starts.';
    }
  }

  if (!isBlank(draft.paymentTermsDays)) {
    const days = Number(draft.paymentTermsDays);
    if (!Number.isInteger(days) || days < 0 || days > 365) {
      errors.paymentTermsDays = 'Enter a whole number of days between 0 and 365.';
    }
  }

  if (isBlank(draft.baseCurrencyCode)) errors.baseCurrencyCode = 'Select the base currency.';
  else if (!/^[A-Za-z]{3}$/.test(trimmed(draft.baseCurrencyCode))) {
    errors.baseCurrencyCode = 'Use a three-letter ISO-4217 code, for example SAR.';
  }
  // Any ISO code is acceptable here: this is the currency the tenant QUOTES in. Billing stays in
  // BILLABLE_CURRENCY regardless, and activation checks the rate card against that, not this.

  if (isBlank(draft.timeZoneId)) errors.timeZoneId = 'Select the tenant time zone.';

  if (isBlank(draft.locale)) errors.locale = 'Select the tenant language and region.';
  else if (!isValidLocale(draft.locale)) errors.locale = 'Enter a valid language tag, for example en-US.';

  return errors;
}

function validateAdmin(draft: ProvisionDraft): FieldErrors {
  const errors: FieldErrors = {};

  if (isBlank(draft.adminFirstName)) errors.adminFirstName = 'Enter the administrator’s first name.';
  if (isBlank(draft.adminLastName)) errors.adminLastName = 'Enter the administrator’s last name.';

  if (isBlank(draft.adminEmail)) errors.adminEmail = 'Enter the administrator’s work email.';
  else if (!EMAIL.test(trimmed(draft.adminEmail))) errors.adminEmail = 'Enter a valid email address.';

  if (!isBlank(draft.adminPhone) && trimmed(draft.adminPhone).replace(/[^\d]/g, '').length < 7) {
    errors.adminPhone = 'Enter a full phone number, including the country code.';
  }

  // A blank password on the password path is legitimate: it asks the server to
  // generate one, which it returns exactly once. Only a typed password is held
  // to the policy, and it must not fail server-side after the wizard said yes.
  if (draft.adminActivation === 'password' && draft.adminPassword.length > 0) {
    if (draft.adminPassword.length > PASSWORD_MAX_LENGTH) {
      errors.adminPassword = `Use at most ${PASSWORD_MAX_LENGTH} characters.`;
    } else if (!isPasswordAcceptable(draft.adminPassword)) {
      errors.adminPassword = passwordProblem(draft.adminPassword) ?? 'Choose a stronger password.';
    }
  }

  return errors;
}

export const validateStep = (step: number, draft: ProvisionDraft): FieldErrors => {
  if (step === STEP_COMPANY) return validateCompany(draft);
  if (step === STEP_COMMERCIAL) return validateCommercial(draft);
  if (step === STEP_ADMIN) return validateAdmin(draft);
  // The review step owns no fields of its own; it is valid exactly when every
  // step behind it is, which is what blocks a commit assembled by going back.
  return { ...validateCompany(draft), ...validateCommercial(draft), ...validateAdmin(draft) };
};

export const stepIsValid = (step: number, draft: ProvisionDraft): boolean =>
  Object.keys(validateStep(step, draft)).length === 0;

/**
 * Things the operator is allowed to skip but will regret skipping. Surfaced on
 * the review step so an incomplete record is a conscious choice rather than an
 * oversight discovered at the first invoice.
 */
export const reviewAdvisories = (draft: ProvisionDraft): string[] => {
  const notes: string[] = [];
  if (isBlank(draft.taxNumber)) notes.push('No tax / VAT number — invoices for this tenant may not be compliant in its jurisdiction.');
  if (isBlank(draft.registrationNumber)) notes.push('No company registration number recorded.');
  if (draft.billingMode === 'Billable' && isBlank(draft.rateCardId)) {
    notes.push('No rate card selected — metered usage will not be priced until one is attached.');
  }
  if (isBlank(draft.contractStartOn) && isBlank(draft.contractEndOn)) {
    notes.push('No contract period recorded — renewal and expiry cannot be tracked.');
  }
  if (isBlank(draft.paymentTermsDays)) notes.push('No payment terms recorded — collections has nothing to chase against.');
  if (draft.billingMode === 'Trial' && !isBlank(draft.trialEndsOn)) {
    const days = Math.round((Date.parse(draft.trialEndsOn) - Date.parse(todayIso())) / 86_400_000);
    if (days > 90) notes.push(`This trial runs for ${days} days. Trials beyond 90 days are usually a contract, not a trial.`);
  }
  if (draft.adminActivation === 'password' && isBlank(draft.adminPassword)) {
    notes.push('The server will generate the first password. It is shown once, on the next screen, and can never be retrieved.');
  }
  return notes;
}

/** A date input speaks `yyyy-mm-dd`; the wire carries a full ISO instant. */
const toDateInput = (value: string | null | undefined): string => (value ? value.slice(0, 10) : '');

const asText = (value: string | null | undefined): string => value ?? '';

/**
 * Rebuilds the wizard's form state from a saved draft.
 *
 * <p>The inverse of `toProvisionInput` composed with the API client's request mapping, and
 * deliberately total: every field the draft can carry is restored, because a resume that
 * silently drops the billing contact is worse than no resume at all — the operator has no
 * way to notice what went missing before they submit.</p>
 */
export const draftFromRequestBody = (payload: ProvisionTenantRequestBody): ProvisionDraft => {
  const billingMode =
    BILLING_MODES.find((mode) => mode.toLowerCase() === (payload.billingMode ?? '').toLowerCase()) ??
    'Billable';
  const adminActivation: AdminActivationMode =
    (payload.adminActivation ?? '').toLowerCase() === 'password' ? 'password' : 'invite';

  return emptyDraft({
    name: asText(payload.name),
    slug: asText(payload.slug),
    legalName: asText(payload.legalName),
    registrationNumber: asText(payload.registrationNumber),
    taxNumber: asText(payload.taxNumber),
    countryCode: asText(payload.countryCode).toUpperCase(),
    industry: asText(payload.industry),
    website: asText(payload.website),
    addressLine1: asText(payload.addressLine1),
    addressLine2: asText(payload.addressLine2),
    city: asText(payload.city),
    stateProvince: asText(payload.stateProvince),
    postalCode: asText(payload.postalCode),
    phone: asText(payload.phone),
    contactEmail: asText(payload.contactEmail),
    logoUrl: asText(payload.logoUrl),

    planId: payload.planId == null ? '' : String(payload.planId),
    billingMode,
    billingModeReason: asText(payload.billingModeReason),
    rateCardId: payload.rateCardId == null ? '' : String(payload.rateCardId),
    billingStartsOn: toDateInput(payload.billingStartsOn),
    trialEndsOn: toDateInput(payload.trialEndsOn),
    contractStartOn: toDateInput(payload.contractStartOn),
    contractEndOn: toDateInput(payload.contractEndOn),
    paymentTermsDays: payload.paymentTermsDays == null ? '' : String(payload.paymentTermsDays),
    purchaseOrderReference: asText(payload.purchaseOrderReference),
    billingContactName: asText(payload.billingContactName),
    billingContactEmail: asText(payload.billingContactEmail),
    billingAddress: asText(payload.billingAddress),
    accountOwnerEmail: asText(payload.accountOwnerEmail),
    // A resumed draft keeps whatever it stored; an empty one gets the functional default.
    baseCurrencyCode: asText(payload.baseCurrencyCode).toUpperCase() || DEFAULT_FUNCTIONAL_CURRENCY,
    timeZoneId: asText(payload.timeZoneId),
    locale: asText(payload.locale) || 'en-SA',
    dataRegion: asText(payload.dataRegion),

    adminFirstName: asText(payload.adminFirstName),
    adminLastName: asText(payload.adminLastName),
    adminEmail: asText(payload.adminEmail),
    adminJobTitle: asText(payload.adminJobTitle),
    adminPhone: asText(payload.adminPhone),
    adminActivation,
    // A password is never persisted in a draft and never restored into one. The draft is
    // stored server-side; a credential sitting in it would be a credential at rest that
    // nobody decided to keep.
    adminPassword: '',
  });
};

/** The one place the string-shaped form becomes the wire contract. */
export const toProvisionInput = (draft: ProvisionDraft): ProvisionTenantInput => ({
  name: trimmed(draft.name),
  slug: trimmed(draft.slug),
  legalName: trimmed(draft.legalName),
  registrationNumber: trimmed(draft.registrationNumber),
  taxNumber: trimmed(draft.taxNumber),
  countryCode: trimmed(draft.countryCode).toUpperCase(),
  industry: trimmed(draft.industry),
  website: trimmed(draft.website),
  addressLine1: trimmed(draft.addressLine1),
  addressLine2: trimmed(draft.addressLine2),
  city: trimmed(draft.city),
  stateProvince: trimmed(draft.stateProvince),
  postalCode: trimmed(draft.postalCode),
  phone: trimmed(draft.phone),
  contactEmail: trimmed(draft.contactEmail),
  logoUrl: trimmed(draft.logoUrl),

  planId: isBlank(draft.planId) ? null : trimmed(draft.planId),
  billingMode: draft.billingMode,
  billingModeReason: draft.billingMode === 'Billable' ? null : trimmed(draft.billingModeReason),
  rateCardId: isBlank(draft.rateCardId) ? null : trimmed(draft.rateCardId),
  billingStartsOn: isBlank(draft.billingStartsOn) ? null : draft.billingStartsOn,
  // Only a trial carries an end date; carrying one over from an abandoned trial
  // selection would hand the backend an expiry for a tenant that has none.
  trialEndsOn: draft.billingMode === 'Trial' && !isBlank(draft.trialEndsOn) ? draft.trialEndsOn : null,
  contractStartOn: isBlank(draft.contractStartOn) ? null : draft.contractStartOn,
  contractEndOn: isBlank(draft.contractEndOn) ? null : draft.contractEndOn,
  paymentTermsDays: isBlank(draft.paymentTermsDays) ? null : Number(draft.paymentTermsDays),
  purchaseOrderReference: trimmed(draft.purchaseOrderReference),
  billingContactName: trimmed(draft.billingContactName),
  billingContactEmail: trimmed(draft.billingContactEmail),
  billingAddress: trimmed(draft.billingAddress),
  accountOwnerEmail: trimmed(draft.accountOwnerEmail),
  baseCurrencyCode: trimmed(draft.baseCurrencyCode).toUpperCase(),
  timeZoneId: trimmed(draft.timeZoneId),
  locale: trimmed(draft.locale),
  dataRegion: trimmed(draft.dataRegion),

  adminFirstName: trimmed(draft.adminFirstName),
  adminLastName: trimmed(draft.adminLastName),
  adminEmail: trimmed(draft.adminEmail),
  adminJobTitle: trimmed(draft.adminJobTitle),
  adminPhone: trimmed(draft.adminPhone),
  adminActivation: draft.adminActivation,
  adminPassword: draft.adminActivation === 'password' && draft.adminPassword.length > 0 ? draft.adminPassword : null,
});
