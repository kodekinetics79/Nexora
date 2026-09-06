import { BILLABLE_CURRENCY } from '../../components/provisionValidation';
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, AlertTitle, Button, Dialog, DialogActions, DialogContent, DialogTitle,
  MenuItem, TextField, Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import ReasonDialog from '../../components/ReasonDialog';
import { ErrorState, LoadingState } from '../../components/States';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import {
  EMPTY_ACCOUNT_CONTACT,
  accountContactProblem,
  type AccountContactForm,
} from '../../components/accountContactValidation';
import type {
  ActivationRemediationAction, PlatformDataBoundary, RateCard, RegisterTenantDataAssetInput, Tenant,
  TenantBillingProfile, TenantDataAsset, UpdateTenantProfileInput, VerifyTenantDataAssetInput,
} from '../../types';

/**
 * The resolvers an operator can complete WITHOUT leaving the activation screen.
 *
 * <p>Every one is the same HTTP call, under the same server policy, audited under the same
 * action, as the edit the operator makes today from the tab that owns it. There is deliberately
 * no "resolve activation control" endpoint: a privileged one-shot that satisfies gates would be
 * an escape hatch wearing a helpful label, and this codebase already knows what happens to an
 * escape hatch that exists.</p>
 *
 * <p>The controls NOT in here are not oversights. Assigning a plan, editing commercial terms and
 * repricing the plan catalogue all move money for tenants other than this one, so the console
 * sends the operator to the screen that owns them with the full context around it rather than
 * reproducing a money decision inside an activation dialog.</p>
 */
export type InlineResolverAction =
  | 'tenant.profile-identity'
  | 'tenant.account-contact'
  | 'tenant.rate-card-pin'
  | 'tenant.data-asset-boundary';

const RESOLVER_ACTIONS: ReadonlySet<string> = new Set<InlineResolverAction>([
  'tenant.profile-identity',
  'tenant.account-contact',
  'tenant.rate-card-pin',
  'tenant.data-asset-boundary',
]);

export const isResolvableInline = (
  action: ActivationRemediationAction | undefined,
): action is InlineResolverAction => action !== undefined && RESOLVER_ACTIONS.has(action);

/** Mirrors `PlatformBillingController.MinimumAccountContactReasonLength`. */
const MINIMUM_ACCOUNT_CONTACT_REASON = 15;

/** Mirrors `TenantsController.UpdateProfile`, which refuses a reason under three characters. */
const MINIMUM_PROFILE_REASON = 3;

const opaque = (value: string) => value.trim().length > 0 && !/[\s@=?]|:\/\//.test(value);
const isSha256 = (value: string) => /^[a-fA-F0-9]{64}$/.test(value);
const toDateInput = (value: string | null): string => (value ? value.slice(0, 10) : '');

const POSTGRES_LOGICAL_KEY = 'postgresql.primary' as const;

interface ResolverProps {
  tenant: Tenant;
  action: InlineResolverAction | null;
  onClose: () => void;
  /** Called after the server accepted the change, so the caller can re-evaluate the policy. */
  onResolved: () => void;
}

export default function ActivationResolverDialog({ tenant, action, onClose, onResolved }: ResolverProps) {
  if (action === 'tenant.profile-identity') {
    return <ProfileIdentityResolver tenant={tenant} onClose={onClose} onResolved={onResolved} />;
  }
  if (action === 'tenant.account-contact') {
    return <AccountContactResolver tenant={tenant} onClose={onClose} onResolved={onResolved} />;
  }
  if (action === 'tenant.rate-card-pin') {
    return <RateCardPinResolver tenant={tenant} onClose={onClose} onResolved={onResolved} />;
  }
  if (action === 'tenant.data-asset-boundary') {
    return <DataAssetBoundaryResolver tenant={tenant} onClose={onClose} onResolved={onResolved} />;
  }
  return null;
}

type OpenResolverProps = Omit<ResolverProps, 'action'>;

/**
 * `identity.legal-customer` and the tax half of `billing.currency-tax`.
 *
 * PUT /api/platform/tenants/{id}/profile replaces the whole company record, so every other field
 * is sent back exactly as the tenant already carries it. Sending a partial body here would clear
 * the customer's address as a side effect of recording their registration number.
 */
function ProfileIdentityResolver({ tenant, onClose, onResolved }: OpenResolverProps) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [legalName, setLegalName] = useState(tenant.legalName ?? '');
  const [registrationNumber, setRegistrationNumber] = useState(tenant.registrationNumber ?? '');
  const [countryCode, setCountryCode] = useState(tenant.countryCode ?? '');
  const [contactEmail, setContactEmail] = useState(tenant.contactEmail ?? '');
  const [taxNumber, setTaxNumber] = useState(tenant.taxNumber ?? '');

  const save = useMutation({
    mutationFn: (reason: string) => {
      const input: UpdateTenantProfileInput = {
        name: tenant.name,
        legalName: legalName.trim() || null,
        registrationNumber: registrationNumber.trim() || null,
        taxNumber: taxNumber.trim() || null,
        countryCode: countryCode.trim().toUpperCase() || null,
        industry: tenant.industry,
        website: tenant.website,
        addressLine1: tenant.addressLine1,
        addressLine2: tenant.addressLine2,
        city: tenant.city,
        stateProvince: tenant.stateProvince,
        postalCode: tenant.postalCode,
        phone: tenant.phone,
        contactEmail: contactEmail.trim() || null,
        logoUrl: tenant.logoUrl,
        timeZoneId: tenant.timeZoneId,
        locale: tenant.locale,
        reason,
      };
      return platformApi.updateTenantProfile(tenant.id, input);
    },
    onSuccess: (updated) => {
      queryClient.setQueryData(platformKeys.tenant(tenant.id), updated);
      enqueueSnackbar('Tenant identity updated and audited', { variant: 'success' });
      onClose();
      onResolved();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The identity change was refused'), { variant: 'error' }),
  });

  const missing = !legalName.trim() || !registrationNumber.trim() || !countryCode.trim() || !contactEmail.trim();

  return (
    <ReasonDialog
      open
      title="Record the legal identity"
      confirmLabel="Save and audit"
      minReasonLength={MINIMUM_PROFILE_REASON}
      reasonHelper="Required and written to the platform audit trail. Name the document these came from."
      description={
        <>
          Who <strong>{tenant.name}</strong> actually is, on paper. The activation control reads all
          four of legal name, registration number, country and customer contact — none of them is
          inferred from the trading name, because a workspace called {tenant.name} is not evidence
          that a company called {tenant.name} signed anything. The rest of the company record is
          edited on Profile &amp; access and is sent back here unchanged.
        </>
      }
      extra={
        <Stack spacing={2} sx={{ mt: 1 }}>
          <TextField fullWidth required label="Legal name" value={legalName}
            onChange={(event) => setLegalName(event.target.value)} />
          <TextField fullWidth required label="Registration number" value={registrationNumber}
            onChange={(event) => setRegistrationNumber(event.target.value)} />
          <TextField fullWidth required label="Country code" value={countryCode}
            helperText="ISO alpha-2, e.g. US."
            onChange={(event) => setCountryCode(event.target.value)} />
          <TextField fullWidth required label="Company email" value={contactEmail}
            helperText="The customer's own contact address, not the operator's."
            onChange={(event) => setContactEmail(event.target.value)} />
          <TextField fullWidth label="Tax number" value={taxNumber}
            helperText="Also satisfies the tax half of billing.currency-tax. Billing is in USD via the pinned rate card; the tenant's own base currency is fixed at provisioning and is not editable anywhere."
            onChange={(event) => setTaxNumber(event.target.value)} />
        </Stack>
      }
      extraProblem={missing
        ? 'Legal name, registration number, country and company email are all required by the control.'
        : null}
      busy={save.isPending}
      onClose={onClose}
      onConfirm={(reason) => save.mutate(reason)}
    />
  );
}

/**
 * `billing.account-recipient`, and the contract-date half of `commercial.approved-terms`.
 *
 * The server treats an omitted field as a CLEAR, so the form is seeded from the billing profile
 * and every field is sent every time. Seeding is not pre-filling evidence — these are values the
 * tenant already carries, and the reason below still starts blank and stays required.
 */
function AccountContactResolver({ tenant, onClose, onResolved }: OpenResolverProps) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [contact, setContact] = useState<AccountContactForm | null>(null);

  const profileQuery = useQuery({
    queryKey: platformKeys.tenantBilling(tenant.id),
    queryFn: () => platformApi.getTenantBillingProfile(tenant.id),
  });

  const profile: TenantBillingProfile | undefined = profileQuery.data;
  // Seeded once, on the first render that has a profile. Re-seeding on every render would
  // overwrite what the operator is typing the moment the query refetches in the background.
  const form = contact ?? (profile ? {
    ...EMPTY_ACCOUNT_CONTACT,
    billingContactName: profile.billingContactName ?? '',
    billingContactEmail: profile.billingContactEmail ?? '',
    billingAddress: profile.billingAddress ?? '',
    purchaseOrderReference: profile.purchaseOrderReference ?? '',
    paymentTermsDays: profile.paymentTermsDays == null ? '' : String(profile.paymentTermsDays),
    accountOwnerEmail: profile.accountOwnerEmail ?? '',
    contractStartOn: toDateInput(profile.contractStartOn),
    contractEndOn: toDateInput(profile.contractEndOn),
  } : EMPTY_ACCOUNT_CONTACT);

  const save = useMutation({
    mutationFn: (reason: string) => platformApi.setTenantAccountContact(tenant.id, {
      billingContactName: form.billingContactName.trim() || null,
      billingContactEmail: form.billingContactEmail.trim() || null,
      billingAddress: form.billingAddress.trim() || null,
      purchaseOrderReference: form.purchaseOrderReference.trim() || null,
      paymentTermsDays: form.paymentTermsDays.trim() ? Number(form.paymentTermsDays) : null,
      accountOwnerEmail: form.accountOwnerEmail.trim() || null,
      contractStartOn: form.contractStartOn || null,
      contractEndOn: form.contractEndOn || null,
      reason,
    }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: platformKeys.tenantBilling(tenant.id) });
      enqueueSnackbar('Invoicing details updated', { variant: 'success' });
      onClose();
      onResolved();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The invoicing details were refused'), { variant: 'error' }),
  });

  const positiveTerms = /^\d+$/.test(form.paymentTermsDays.trim()) && Number(form.paymentTermsDays) > 0;

  return (
    <ReasonDialog
      open
      title="Set the invoicing details"
      confirmLabel="Save and audit"
      minReasonLength={MINIMUM_ACCOUNT_CONTACT_REASON}
      reasonHelper="Redirecting a customer's invoice has to be attributable months later. Say on whose instruction."
      description={
        <>
          Who at <strong>{tenant.name}</strong> is invoiced, where, and on what terms. Invoicing
          refuses to issue without a recipient, so a tenant activated without one can never be
          billed and — because offboarding readiness needs a finalized invoice — can never be
          cleanly closed either. This is the same audited endpoint as Commercial → Edit invoicing
          details; it takes effect on the next invoice and rewrites no invoice already issued.
        </>
      }
      extra={
        profileQuery.isLoading ? <LoadingState label="Reading the billing profile…" minHeight={160} />
          : profileQuery.isError || !profile ? (
            <ErrorState
              minHeight={160}
              message={platformErrorMessage(profileQuery.error,
                'The billing profile could not be read, so these fields cannot be edited without clearing what is already there.')}
              onRetry={() => profileQuery.refetch()}
            />
          ) : (
            <Stack spacing={2} sx={{ mt: 1 }}>
              <TextField fullWidth required label="Invoice recipient email" value={form.billingContactEmail}
                helperText="Invoicing refuses to issue without this."
                onChange={(event) => setContact({ ...form, billingContactEmail: event.target.value })} />
              <TextField fullWidth label="Invoice recipient name" value={form.billingContactName}
                onChange={(event) => setContact({ ...form, billingContactName: event.target.value })} />
              <TextField fullWidth multiline minRows={2} required label="Invoice address" value={form.billingAddress}
                onChange={(event) => setContact({ ...form, billingAddress: event.target.value })} />
              <TextField fullWidth required label="Payment terms (days)" value={form.paymentTermsDays}
                helperText="The control requires a POSITIVE figure. Zero means due on receipt, which is a real commercial term and does not satisfy it."
                onChange={(event) => setContact({ ...form, paymentTermsDays: event.target.value })} />
              <TextField fullWidth label="Their PO reference" value={form.purchaseOrderReference}
                onChange={(event) => setContact({ ...form, purchaseOrderReference: event.target.value })} />
              <TextField fullWidth label="Account owner (ours)" value={form.accountOwnerEmail}
                onChange={(event) => setContact({ ...form, accountOwnerEmail: event.target.value })} />
              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                <TextField fullWidth type="date" label="Contract starts" value={form.contractStartOn}
                  slotProps={{ inputLabel: { shrink: true } }}
                  onChange={(event) => setContact({ ...form, contractStartOn: event.target.value })} />
                <TextField fullWidth type="date" label="Contract ends" value={form.contractEndOn}
                  slotProps={{ inputLabel: { shrink: true } }}
                  onChange={(event) => setContact({ ...form, contractEndOn: event.target.value })} />
              </Stack>
              <Alert severity="info">
                The billing start date lives on Commercial → Edit terms. It is the other half of
                commercial.approved-terms and this form deliberately does not set it.
              </Alert>
            </Stack>
          )
      }
      extraProblem={
        !profile ? 'The billing profile has not loaded.'
          : accountContactProblem(form, profile.billingMode)
            ?? (!form.billingAddress.trim()
              ? 'The activation control also requires an invoice address.'
              : !positiveTerms
                ? 'The activation control requires positive payment terms, so 0 and blank both leave it blocking.'
                : null)
      }
      busy={save.isPending}
      onClose={onClose}
      onConfirm={(reason) => save.mutate(reason)}
    />
  );
}

/**
 * `commercial.rate-card`.
 *
 * The list is filtered to the cards that would actually SATISFY the control — active, in the
 * tenant's base currency, effective right now, and carrying at least one priced meter. Offering
 * a card that fails one of those would let an operator pin it, watch the control stay red and
 * have nothing on screen explaining which of the four rules it broke.
 *
 * Creating or repricing a card is not here on purpose: a rate card is shared by every tenant
 * pinned to it, so it belongs on the Billing page where the blast radius is visible.
 */
function RateCardPinResolver({ tenant, onClose, onResolved }: OpenResolverProps) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [rateCardId, setRateCardId] = useState('');
  // Billing currency, not the tenant's functional currency: rate cards are USD-only and
  // activation compares the card to the platform billing currency, so a SAR tenant pins a USD card.
  const currency = BILLABLE_CURRENCY;

  const cardsQuery = useQuery({
    queryKey: platformKeys.rateCards(),
    queryFn: () => platformApi.listRateCards(),
  });

  const now = Date.now();
  const eligible = (cardsQuery.data ?? []).filter((card: RateCard) =>
    card.isActive
    && card.currency.toUpperCase() === currency.toUpperCase()
    && Date.parse(card.effectiveFromUtc) <= now
    && (card.effectiveToUtc === null || Date.parse(card.effectiveToUtc) > now)
    && card.lines.length > 0);

  const pin = useMutation({
    mutationFn: (reason: string) => platformApi.setTenantRateCard(tenant.id, { rateCardId, reason }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: platformKeys.tenantBilling(tenant.id) });
      enqueueSnackbar('Rate card pinned', { variant: 'success' });
      onClose();
      onResolved();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The rate card change was refused'), { variant: 'error' }),
  });

  return (
    <ReasonDialog
      open
      title="Pin a rate card"
      confirmLabel="Pin this card"
      description={
        <>
          Pins the price list <strong>{tenant.name}</strong>&apos;s statements are computed against,
          which is what stops a negotiated customer being silently repriced the day somebody
          activates a new card. Only cards that satisfy every part of the control are listed:
          active, {currency}, effective today, and carrying at least one priced meter.
        </>
      }
      extra={
        cardsQuery.isLoading ? <LoadingState label="Reading the rate cards…" minHeight={140} />
          : cardsQuery.isError ? (
            <ErrorState
              minHeight={140}
              message={platformErrorMessage(cardsQuery.error, 'The rate cards could not be read.')}
              onRetry={() => cardsQuery.refetch()}
            />
          ) : eligible.length === 0 ? (
            <Alert severity="warning">
              <AlertTitle sx={{ fontWeight: 800 }}>No card can satisfy this control yet</AlertTitle>
              Nothing in the catalogue is simultaneously active, priced in {currency}, effective
              today and carrying a priced meter. Creating or correcting a card is a Billing-page
              action because the card is shared by every tenant pinned to it — this dialog will not
              mint one to get past a gate.
            </Alert>
          ) : (
            <Stack spacing={1} sx={{ mt: 1 }}>
              <TextField select fullWidth required label="Rate card" value={rateCardId}
                onChange={(event) => setRateCardId(event.target.value)}>
                {eligible.map((card) => (
                  <MenuItem key={card.id} value={card.id}>
                    {card.code} · {card.currency} · {card.lines.length} priced meter(s)
                  </MenuItem>
                ))}
              </TextField>
              <Typography variant="caption" color="text.secondary">
                Clearing an existing pin is deliberately not offered here: it un-satisfies the
                control, and the screen that owns unpinning is Commercial.
              </Typography>
            </Stack>
          )
      }
      extraProblem={rateCardId === '' ? 'Select a rate card.' : null}
      busy={pin.isPending}
      onClose={onClose}
      onConfirm={(reason) => pin.mutate(reason)}
    />
  );
}

/**
 * `data.residency-isolation`, in whichever of its two steps is actually next.
 *
 * <p><b>The platform describes the platform.</b> Every tenant on a Nexora-hosted deployment lives
 * in the same database, in the region that deployment declares, under the backup policy it pays
 * for. None of that is a fact about the customer and none of it is something the operator opening
 * this dialog knows better than the server does — so where the deployment has declared its estate
 * (<code>Platform:DataBoundaries</code>) this dialog is one button and no fields. The
 * register-then-verify forms stay for a deployment that has declared nothing, and for the operator
 * who has to record something other than what it declares.</p>
 *
 * <p>The button asserts NOTHING. The server registers from configuration and verifies from a live
 * probe of the running database; a probe that disagrees refuses the whole action and says what
 * disagreed. Nothing on the manual verify form is pre-filled, for the same reason: every field on
 * it is an OBSERVATION, and a form that suggested the answer would be manufacturing the evidence
 * it exists to record.</p>
 */
function DataAssetBoundaryResolver({ tenant, onClose, onResolved }: OpenResolverProps) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  // Set only when the operator deliberately leaves the automatic path. It is not a preference and
  // is not remembered: the next time this control blocks, the platform gets to describe itself
  // again first.
  const [byHand, setByHand] = useState(false);
  const [providerReference, setProviderReference] = useState('');
  const [backupPolicyReference, setBackupPolicyReference] = useState('');
  const [backupPolicyVersion, setBackupPolicyVersion] = useState('1');
  // The contractual region the tenant already carries. Seeded rather than blank because the
  // server refuses any other value outright — this is the recorded claim being restated, not an
  // observation being suggested.
  const [region, setRegion] = useState(tenant.dataRegion ?? '');
  const [observedBusinessUnitId, setObservedBusinessUnitId] = useState('');
  const [observedRegion, setObservedRegion] = useState('');
  const [evidenceReference, setEvidenceReference] = useState('');
  const [evidenceSha256, setEvidenceSha256] = useState('');

  const assetsQuery = useQuery({
    queryKey: platformKeys.tenantDataAssets(tenant.id),
    queryFn: () => platformApi.listTenantDataAssets(tenant.id),
  });

  // Deployment-wide configuration, so a failure here is never allowed to block the manual path:
  // an unreadable manifest reads exactly like an undeclared one, which is the fallback that
  // already works.
  const manifestQuery = useQuery({
    queryKey: platformKeys.platformDataBoundaries(),
    queryFn: () => platformApi.getPlatformDataBoundaries(),
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantDataAssets(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantActivationDataDecision(tenant.id) });
  };

  const primary: TenantDataAsset | null =
    (assetsQuery.data ?? []).find((asset) => asset.logicalKey === POSTGRES_LOGICAL_KEY) ?? null;
  const declared: PlatformDataBoundary | null = manifestQuery.data?.primaryPostgreSqlScope ?? null;
  const manifestDefect = (manifestQuery.data?.defects ?? [])
    .find((defect) => defect.assetType === 'PostgreSqlTenantScope')?.reason ?? null;

  const applyManifest = useMutation({
    mutationFn: () => platformApi.applyPlatformDataBoundaries(tenant.id),
    onSuccess: (result) => {
      invalidate();
      // The tenant row itself can have changed: a tenant with no contractual region has one
      // recorded as part of this action, and the header and Profile & access both read it.
      queryClient.invalidateQueries({ queryKey: platformKeys.tenant(tenant.id) });
      enqueueSnackbar(
        result.decision.dataGateReady
          ? 'Data boundary registered and verified from the platform configuration'
          : 'Boundaries registered, but the data gate is still blocked — see Data & storage',
        { variant: result.decision.dataGateReady ? 'success' : 'warning' },
      );
      onClose();
      onResolved();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The registration was refused'), { variant: 'error' }),
  });

  const register = useMutation({
    mutationFn: (reason: string) => {
      const input: RegisterTenantDataAssetInput = {
        logicalKey: POSTGRES_LOGICAL_KEY,
        opaqueProviderReference: providerReference.trim(),
        region: region.trim(),
        classification: 'CustomerData',
        disposition: 'BackupRetainedUntilExpiryThenDestroy',
        backupPolicyReference: backupPolicyReference.trim(),
        backupPolicyVersion: Number(backupPolicyVersion),
        reason,
      };
      return platformApi.registerTenantDataAsset(tenant.id, input);
    },
    onSuccess: () => {
      invalidate();
      enqueueSnackbar('PostgreSQL tenant scope registered — verification is the next step', { variant: 'success' });
      onClose();
      onResolved();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Registration was refused'), { variant: 'error' }),
  });

  const verify = useMutation({
    mutationFn: (reason: string) => {
      const input: VerifyTenantDataAssetInput = {
        expectedVersion: primary!.version,
        observedBusinessUnitId: observedBusinessUnitId.trim(),
        observedRegion: observedRegion.trim(),
        evidenceReference: evidenceReference.trim(),
        evidenceSha256: evidenceSha256.trim().toLowerCase(),
        reason,
      };
      return platformApi.verifyTenantDataAsset(tenant.id, primary!.id, input);
    },
    onSuccess: () => {
      invalidate();
      enqueueSnackbar('PostgreSQL tenant scope verified', { variant: 'success' });
      onClose();
      onResolved();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Verification was refused'), { variant: 'error' }),
  });

  if (assetsQuery.isLoading || assetsQuery.isError || manifestQuery.isLoading) {
    return (
      <ReasonDialog
        open
        title="Register or verify the data boundary"
        confirmLabel="Continue"
        description="Reading this tenant's registered data boundaries."
        extra={assetsQuery.isError ? (
          <ErrorState
            minHeight={140}
            message={platformErrorMessage(assetsQuery.error, 'The data-asset registry could not be read.')}
            onRetry={() => assetsQuery.refetch()}
          />
        ) : <LoadingState label="Reading the data-asset registry…" minHeight={140} />}
        extraProblem="The registry has not been read, so neither step can be taken."
        onClose={onClose}
        onConfirm={() => undefined}
      />
    );
  }

  if (primary?.status === 'Verified') {
    return (
      <ReasonDialog
        open
        title="The data boundary is already verified"
        confirmLabel="Close"
        description={
          <>
            The primary boundary is registered and verified. If the residency control is still
            blocking, the disagreement is between the verified asset and the tenant record — the
            region, the business-unit scope or the backup policy version — and it is diagnosed on
            Data &amp; storage, which shows both sides.
          </>
        }
        extraProblem="Nothing to record here."
        onClose={onClose}
        onConfirm={() => undefined}
      />
    );
  }

  // THE ORDINARY CASE on a Nexora-hosted deployment: no fields, one button.
  if (declared && !byHand) {
    const regionDisagrees = Boolean(tenant.dataRegion)
      && tenant.dataRegion!.trim().toLowerCase() !== declared.region.toLowerCase();
    return (
      <Dialog
        open
        onClose={() => (applyManifest.isPending ? undefined : onClose())}
        maxWidth="sm"
        fullWidth
      >
        <DialogTitle sx={{ fontWeight: 800 }}>Register the data boundary from this deployment</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2}>
            <Typography variant="body2" component="div" color="text.secondary">
              This deployment already declares what its own database is, so there is nothing here for
              you to type. This records that declaration against <strong>{tenant.name}</strong> and
              verifies it against the running database.
            </Typography>
            <Alert severity="info" sx={{ borderRadius: 2 }}>
              <AlertTitle sx={{ fontWeight: 800 }}>What gets recorded</AlertTitle>
              <Stack spacing={0.25}>
                <Typography variant="body2">Scope · {declared.logicalKey} (PostgreSQL tenant scope)</Typography>
                <Typography variant="body2">Provider · {declared.opaqueProviderReference}</Typography>
                <Typography variant="body2">Region · {declared.region}</Typography>
                <Typography variant="body2">
                  Backup policy · {declared.backupPolicyReference} v{declared.backupPolicyVersion}
                </Typography>
              </Stack>
            </Alert>
            {!tenant.dataRegion && (
              <Alert severity="warning" sx={{ borderRadius: 2 }}>
                This tenant carries no contractual data region, so <strong>{declared.region}</strong>{' '}
                — the region this deployment declares for the database every tenant lives in — is
                recorded as part of this action and audited against your account.
              </Alert>
            )}
            {regionDisagrees && (
              <Alert role="alert" severity="error" sx={{ borderRadius: 2 }}>
                This tenant's contractual region is <strong>{tenant.dataRegion}</strong> and the
                deployment declares <strong>{declared.region}</strong>. The probe refuses that rather
                than rewriting the claim: correct the contractual region on Profile &amp; access, or
                move the data.
              </Alert>
            )}
            <Typography variant="caption" color="text.secondary">
              Verification is a live probe of this database — the tenant's business-unit scope and
              row-level isolation — recorded with the hash of what it observed. Nothing here is an
              attestation, and a probe that disagrees refuses the whole action rather than
              registering half of it.
            </Typography>
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button
            color="inherit"
            disabled={applyManifest.isPending}
            onClick={() => {
              // Seeded from the declaration so the manual path starts where the automatic one
              // would have ended. The verify form below is untouched by this: it is an
              // observation, and it stays blank.
              setProviderReference(declared.opaqueProviderReference);
              setBackupPolicyReference(declared.backupPolicyReference);
              setBackupPolicyVersion(String(declared.backupPolicyVersion));
              setRegion(tenant.dataRegion ?? declared.region);
              setByHand(true);
            }}
          >
            Enter it by hand
          </Button>
          <Button onClick={onClose} color="inherit" disabled={applyManifest.isPending}>Cancel</Button>
          <Button
            variant="contained"
            sx={{ fontWeight: 700 }}
            disabled={applyManifest.isPending}
            onClick={() => applyManifest.mutate()}
          >
            {applyManifest.isPending
              ? 'Working…'
              : primary ? 'Verify from the platform probe' : 'Register and verify'}
          </Button>
        </DialogActions>
      </Dialog>
    );
  }

  if (primary === null) {
    const problem = !opaque(providerReference) ? 'The provider reference must be an identifier — no URL, connection string, credential, whitespace, @, = or ?.'
      : !region.trim() ? 'A data region is required, and it must match the tenant\'s contractual region.'
        : !opaque(backupPolicyReference) ? 'The backup policy reference must be an opaque identifier.'
          : !/^\d+$/.test(backupPolicyVersion) || Number(backupPolicyVersion) < 1
            ? 'The backup policy version must be a positive whole number.'
            : null;
    return (
      <ReasonDialog
        open
        title="Register the PostgreSQL tenant scope"
        confirmLabel="Register"
        minReasonLength={MINIMUM_PROFILE_REASON}
        description={
          <>
            No primary boundary is registered for <strong>{tenant.name}</strong>, which is the first
            of the two steps this control needs. The registry stores opaque references only — never
            a connection string, never a credential — and registration alone does not satisfy the
            control: an external probe still has to verify it.
          </>
        }
        extra={
          <Stack spacing={2} sx={{ mt: 1 }}>
            {!declared && (
              // The reason an operator is being asked to type infrastructure facts at all. Without
              // this, the form looks like the product's idea of normal rather than the fallback
              // for a deployment that has not described itself.
              <Alert severity="warning" sx={{ borderRadius: 2 }}>
                <AlertTitle sx={{ fontWeight: 800 }}>This deployment has not declared its own database</AlertTitle>
                {manifestDefect
                  ? <>It declared one and the server refused it: {manifestDefect}</>
                  : <>
                      Set <code>Platform__DataBoundaries__PostgreSqlTenantScope__OpaqueProviderReference</code>,{' '}
                      <code>__Region</code>, <code>__BackupPolicyReference</code> and{' '}
                      <code>__BackupPolicyVersion</code> on the API service and every tenant registers
                      and verifies itself. Until then this form is the only way, and it is per tenant.
                    </>}
              </Alert>
            )}
            <Alert severity="info">
              Fixed contract: PostgreSqlTenantScope · CustomerData · BackupRetainedUntilExpiryThenDestroy.
            </Alert>
            <TextField fullWidth required label="Opaque provider reference" value={providerReference}
              error={Boolean(providerReference && !opaque(providerReference))}
              helperText="Identifier only — no URL, connection string, credential, whitespace, @, = or ?."
              onChange={(event) => setProviderReference(event.target.value)} />
            <TextField fullWidth required label="Data region" value={region}
              helperText={tenant.dataRegion
                ? `Must match the contractual region ${tenant.dataRegion}; the server refuses anything else.`
                : 'This tenant carries no contractual region yet, and the server requires one server-side.'}
              onChange={(event) => setRegion(event.target.value)} />
            <TextField fullWidth required label="Opaque backup policy reference" value={backupPolicyReference}
              error={Boolean(backupPolicyReference && !opaque(backupPolicyReference))}
              onChange={(event) => setBackupPolicyReference(event.target.value)} />
            <TextField fullWidth required label="Backup policy version" value={backupPolicyVersion}
              slotProps={{ htmlInput: { inputMode: 'numeric' } }}
              onChange={(event) => setBackupPolicyVersion(event.target.value)} />
          </Stack>
        }
        extraProblem={problem}
        busy={register.isPending}
        onClose={onClose}
        onConfirm={(reason) => register.mutate(reason)}
      />
    );
  }

  const verifyProblem = !/^\d+$/.test(observedBusinessUnitId) || Number(observedBusinessUnitId) <= 0
    ? 'The observed business-unit id must be a positive whole number read from the probe.'
    : !observedRegion.trim() ? 'The observed region is required.'
      : !opaque(evidenceReference) ? 'The evidence reference must be an opaque identifier.'
        : !isSha256(evidenceSha256) ? 'The evidence SHA-256 must be exactly 64 hexadecimal characters.'
          : null;

  return (
    <ReasonDialog
      open
      title="Verify the PostgreSQL tenant scope"
      confirmLabel="Verify evidence"
      confirmColor="warning"
      minReasonLength={MINIMUM_PROFILE_REASON}
      description={
        <>
          The boundary is registered at version {primary.version} and not yet verified. Record only
          what a completed external probe actually observed — this form does not connect to
          PostgreSQL, cannot check any of it, and every field on it starts blank for that reason.
        </>
      }
      extra={
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="warning">
            This is an attestation. Nothing here is pre-filled and nothing here is verified by the
            console; what you type is what an auditor will read as the observation.
          </Alert>
          <TextField fullWidth required label="Observed business-unit ID" value={observedBusinessUnitId}
            onChange={(event) => setObservedBusinessUnitId(event.target.value.replace(/\D/g, ''))} />
          <TextField fullWidth required label="Observed region" value={observedRegion}
            helperText={`The registered region is ${primary.region}; the server refuses a mismatch rather than accepting the correction.`}
            onChange={(event) => setObservedRegion(event.target.value)} />
          <TextField fullWidth required label="Opaque evidence reference" value={evidenceReference}
            error={Boolean(evidenceReference && !opaque(evidenceReference))}
            onChange={(event) => setEvidenceReference(event.target.value)} />
          <TextField fullWidth required label="Evidence SHA-256" value={evidenceSha256}
            error={Boolean(evidenceSha256 && !isSha256(evidenceSha256))}
            helperText="Exactly 64 hexadecimal characters."
            onChange={(event) => setEvidenceSha256(event.target.value.trim())} />
        </Stack>
      }
      extraProblem={verifyProblem}
      busy={verify.isPending}
      onClose={onClose}
      onConfirm={(reason) => verify.mutate(reason)}
    />
  );
}
