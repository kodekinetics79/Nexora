import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Chip, Divider, TextField, Tooltip,
  Typography,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  InfoOutlined as WhyIcon,
  LockOutlined as LockedIcon,
  TuneOutlined as AdvancedIcon,
} from '@mui/icons-material';
import Stack from '../components/Flex';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import PageHeader from '../components/PageHeader';
import { ErrorState, LoadingState } from '../components/States';
import CommitBar from '../components/CommitBar';
import { useStagedChanges } from '../components/useStagedChanges';
import PeopleSection from './customer/PeopleSection';
import CustomerAdvanced from './customer/CustomerAdvanced';
import { COUNTRY_CODES, countryLabel } from '../components/localeData';
import type {
  TenantConfigurationField, TenantConfigurationSlice,
  TenantConfigurationView,
} from '../types';

/**
 * THE CUSTOMER SCREEN — one page where there were twelve tabs.
 *
 * WHAT WAS WRONG. The tab strip was a map of the backend's controllers, not of anybody's work:
 * each tab owned one API surface, so no tab was a task and every real task crossed four to six
 * of them. "Switch a customer on" spanned Activation, Profile & access, Commercial, Data &
 * storage and Users. Behind those tabs sat sixty-eight independent mutations, exactly one
 * dirty-state commit bar, and no navigate-away guard anywhere — and the wizard that created a
 * customer ended by reporting success on a workspace nobody could log into.
 *
 * WHAT THIS IS. One read (`GET .../configuration`) and one vertical page: where the customer
 * stands, what is blocking them and who has to clear it, then the settings themselves as
 * read-only rows carrying the server's own answer about who may change each group.
 *
 * WHAT THIS DELIBERATELY IS NOT. It is not yet the write path. Editing still hands off to the
 * existing audited endpoints — which is why each group links to the surface that owns it rather
 * than pretending to save here. Merging the writes needs the If-Match plumbing to land first:
 * a page that commits five former tabs at once makes a stale overwrite MORE likely, not less,
 * because one commit carries edits somebody may have started ten minutes ago. The tenant row
 * only grew a concurrency token in 20260907111347; until every writer honours it, a single
 * save button here would be a new defect wearing the redesign's clothes.
 */
export default function CustomerPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const permissions = usePlatformPermissions();

  const configuration = useQuery({
    queryKey: platformKeys.tenantConfiguration(id),
    queryFn: () => platformApi.getTenantConfiguration(id),
    enabled: id !== '',
  });

  // The trading name is not in the configuration read — that read is about POSITION, not
  // identity — so the header still asks for the tenant record. Two reads, not eleven.
  const tenantQuery = useQuery({
    queryKey: platformKeys.tenant(id),
    queryFn: () => platformApi.getTenant(id),
    enabled: id !== '',
  });

  const queryClient = useQueryClient();
  const view = configuration.data;
  const blockers = view?.blockers ?? [];

  // Identity and the contract are both written from here now. Modules and deployment are still
  // owned by their own screens, and the footer says so rather than leaving it to be discovered.
  const identity = view?.slices.find((x) => x.key === 'identity');
  const commercial = view?.slices.find((x) => x.key === 'commercial');
  const identityLabels = useMemo(() => {
    const out: Record<string, string> = {};
    for (const f of identity?.fields ?? []) out[f.key] = f.label;
    return out;
  }, [identity]);
  const identityInitial = useMemo(() => {
    const out: Record<string, string> = {};
    for (const f of identity?.fields ?? []) out[f.key] = f.value ?? '';
    return out;
  }, [identity]);

  // The contract fields this page owns. Plan and billing mode are NOT among them: changing what
  // a customer pays is a different act from correcting who the invoice goes to, and it belongs
  // with the maker-checker and the proration preview the billing programme specifies.
  const COMMERCIAL_EDITABLE = [
    'contractStartOn', 'contractEndOn', 'paymentTermsDays', 'purchaseOrderReference',
    'billingContactName', 'billingContactEmail', 'billingAddress', 'accountOwnerEmail',
  ];
  const commercialLabels = useMemo(() => {
    const out: Record<string, string> = {};
    for (const f of commercial?.fields ?? []) {
      if (COMMERCIAL_EDITABLE.includes(f.key)) out[f.key] = f.label;
    }
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [commercial]);
  const commercialInitial = useMemo(() => {
    const out: Record<string, string> = {};
    for (const f of commercial?.fields ?? []) {
      if (COMMERCIAL_EDITABLE.includes(f.key)) {
        // Payment terms arrive rendered as "30 days"; the input wants the number.
        out[f.key] = f.key === 'paymentTermsDays'
          ? (f.value ?? '').replace(/\D+/g, '')
          : (f.value ?? '');
      }
    }
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [commercial]);

  const staged = useStagedChanges<Record<string, string>>(identityInitial, identityLabels);
  const stagedCommercial = useStagedChanges<Record<string, string>>(commercialInitial, commercialLabels);
  const { rebase } = staged;
  const { rebase: rebaseCommercial } = stagedCommercial;
  // Adopt a newly loaded server state as the baseline, but never while the operator has unsaved
  // edits — a background refetch silently discarding somebody's typing is the defect the old
  // profile tab had, where saving the region wiped a half-typed company profile.
  const dirtyRef = staged.dirty || stagedCommercial.dirty;
  useEffect(() => {
    if (!dirtyRef) { rebase(identityInitial); rebaseCommercial(commercialInitial); }
  }, [identityInitial, commercialInitial, dirtyRef, rebase, rebaseCommercial]);

  // The version the edits were started from. Sent as If-Match so a write that lost a race is
  // refused rather than landing on top of somebody else's change.
  const [baseVersion, setBaseVersion] = useState<number | null>(null);
  useEffect(() => {
    if (!dirtyRef && view) setBaseVersion(view.version);
  }, [view, dirtyRef]);

  const [saveError, setSaveError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

  /**
   * Refused here rather than after the operator has typed a reason and pressed Save.
   *
   * Every rule below is one the server already enforces; catching it in the browser is not a
   * second opinion, it is the difference between a sentence naming the field and a JSON binding
   * error quoting `System.Nullable\`1[System.DateTime]`.
   */
  const commitBlockedReason = useMemo(() => {
    const d = stagedCommercial.draft;
    const terms = (d.paymentTermsDays ?? '').trim();
    if (terms !== '' && !/^\d+$/.test(terms)) {
      return 'Payment terms must be a whole number of days — 0 for due on receipt, 30 for net 30.';
    }
    if (terms !== '' && Number(terms) > 365) {
      return 'Payment terms cannot exceed 365 days; an invoice due beyond that is effectively never due.';
    }
    const start = (d.contractStartOn ?? '').trim();
    const end = (d.contractEndOn ?? '').trim();
    if (start && end && end < start) {
      return 'The renewal date is before the contract start date.';
    }
    const country = (staged.draft.countryCode ?? '').trim();
    if (country !== '' && !/^[A-Za-z]{2}$/.test(country)) {
      return 'Country must be a two-letter ISO code, such as SA or GB.';
    }
    const email = (staged.draft.contactEmail ?? '').trim();
    if (email !== '' && !/^[^@\s]+@[^@\s.]+\.[^@\s]+$/.test(email)) {
      return 'The company email does not look like an email address.';
    }
    const invoiceEmail = (d.billingContactEmail ?? '').trim();
    if (invoiceEmail !== '' && !/^[^@\s]+@[^@\s.]+\.[^@\s]+$/.test(invoiceEmail)) {
      return 'The invoice email does not look like an email address.';
    }
    return null;
  }, [staged.draft, stagedCommercial.draft]);

  /**
   * One operator act, two audited endpoints, and an HONEST report of what landed.
   *
   * The first version of this returned a single error saying "Nothing on this customer has been
   * altered" whenever anything threw — including when the billing write had already committed and
   * only the profile write failed. It also never rebased the draft on success, so the bar still
   * read "2 unsaved changes" after a good save, and pressing Save again sent the now-stale
   * If-Match and produced "This customer was changed by somebody else while you had it open."
   * The operator's own successful save was reported to them as a colleague's edit.
   */
  const commit = useMutation({
    mutationFn: async (reason: string): Promise<{ saved: string[]; failed?: string }> => {
      const tenant = tenantQuery.data;
      if (!tenant) throw new Error('The customer record is not loaded.');
      const done: string[] = [];

      // Billing first and awaited: it is Owner-or-BillingAdmin while the profile write is
      // TenantAdmin, so the reverse order would land the identity change and then fail on
      // authority.
      if (stagedCommercial.dirty) {
        const c = (key: string) => {
          const value = stagedCommercial.draft[key]?.trim();
          return value === undefined || value === '' ? null : value;
        };
        const rawTerms = stagedCommercial.draft.paymentTermsDays?.trim() ?? '';
        try {
          await platformApi.setTenantAccountContact(id, {
            billingContactName: c('billingContactName'),
            billingContactEmail: c('billingContactEmail'),
            billingAddress: c('billingAddress'),
            purchaseOrderReference: c('purchaseOrderReference'),
            paymentTermsDays: rawTerms === '' ? null : Number(rawTerms),
            accountOwnerEmail: c('accountOwnerEmail'),
            contractStartOn: c('contractStartOn'),
            contractEndOn: c('contractEndOn'),
            reason,
          });
          done.push('Contract and billing');
        } catch (error) {
          return {
            saved: done,
            failed: `Contract and billing was refused: ${platformErrorMessage(error, 'the server did not say why')}`,
          };
        }
      }

      if (staged.dirty) {
        // If-Match exists to catch SOMEBODY ELSE's change, and the billing write above is not
        // somebody else — it is the first half of this same operator act, and it advances the
        // tenant's Version through TenantConcurrencyStamp. Sending the version we loaded the page
        // with would fail our own second write and report it as a colleague's edit, which is
        // exactly what it did the first time this ran end to end.
        //
        // So re-read the version after our own write, and assert against that. A change made by
        // anyone else between the two is still caught, because their write moves it again.
        let expected = baseVersion ?? undefined;
        if (done.includes('Contract and billing')) {
          try {
            expected = (await platformApi.getTenantConfiguration(id)).version;
          } catch {
            // Could not re-read: send no assertion rather than a known-stale one. A missing
            // If-Match is "no opinion"; a wrong one is a false conflict.
            expected = undefined;
          }
        }

        const v = (key: string) => {
          const value = staged.draft[key]?.trim();
          return value === undefined || value === '' ? null : value;
        };
        try {
          await platformApi.updateTenantProfile(id, {
            name: tenant.name,
            legalName: v('legalName'),
            registrationNumber: v('registrationNumber'),
            taxNumber: v('taxNumber'),
            countryCode: v('countryCode'),
            industry: v('industry'),
            website: v('website'),
            addressLine1: v('addressLine1'),
            addressLine2: tenant.addressLine2,
            city: v('city'),
            stateProvince: tenant.stateProvince,
            postalCode: v('postalCode'),
            phone: v('phone'),
            contactEmail: v('contactEmail'),
            logoUrl: tenant.logoUrl,
            timeZoneId: tenant.timeZoneId,
            locale: tenant.locale,
            reason,
          }, expected);
          done.push('Company details');
        } catch (error) {
          return {
            saved: done,
            failed: `Company details were refused: ${platformErrorMessage(error, 'the server did not say why')}`,
          };
        }
      }
      return { saved: done };
    },
    onSuccess: (result) => {
      // Rebase whatever actually landed, so the bar stops offering to save it again and the next
      // If-Match is taken from fresh state rather than from the version we started with.
      queryClient.invalidateQueries({ queryKey: platformKeys.tenantConfiguration(id) });
      queryClient.invalidateQueries({ queryKey: platformKeys.tenant(id) });
      queryClient.invalidateQueries({ queryKey: platformKeys.customers() });

      if (result.failed) {
        setSaved(null);
        setSaveError(result.saved.length > 0
          // Naming what DID land is the whole point: "nothing was saved" was false, and a
          // committed contract change that the operator believes was rolled back is worse than
          // an error, because they will make it again.
          ? `${result.saved.join(' and ')} saved. ${result.failed}`
          : result.failed);
        if (result.saved.includes('Contract and billing')) stagedCommercial.rebase(stagedCommercial.draft);
        return;
      }
      setSaveError(null);
      setSaved(result.saved.join(' and ') || 'Nothing to save');
      staged.rebase(staged.draft);
      stagedCommercial.rebase(stagedCommercial.draft);
    },
    onError: (error) => {
      setSaved(null);
      setSaveError(platformErrorMessage(error, 'The changes were not saved.'));
    },
  });

  // Leaving with unsaved edits used to lose them silently — switching tabs unmounted the form.
  useEffect(() => {
    if (!staged.dirty && !stagedCommercial.dirty) return undefined;
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [staged.dirty, stagedCommercial.dirty]);

  if (configuration.isLoading || tenantQuery.isLoading) {
    return <LoadingState label="Loading customer…" minHeight="60vh" />;
  }

  if (configuration.isError || !view) {
    return (
      <Box>
        <Button startIcon={<BackIcon />} onClick={() => navigate('/platform/customers')} sx={{ mb: 2 }}>
          Back to customers
        </Button>
        <ErrorState
          message={platformErrorMessage(configuration.error, 'This customer could not be loaded.')}
          onRetry={() => configuration.refetch()}
        />
      </Box>
    );
  }

  const { state, nextAction, slices } = view;
  const needsSomebody = blockers.length > 0 || state.legalHoldActive;
  const tenant = tenantQuery.data;

  return (
    <Box>
      <Button
        startIcon={<BackIcon />}
        onClick={() => navigate('/platform/customers')}
        sx={{ mb: 1.5 }}
        color="inherit"
      >
        Customers
      </Button>

      <PageHeader
        title={tenant?.name ?? `Customer ${view.tenantId}`}
        subtitle={tenant?.legalName ?? tenant?.slug ?? undefined}
        actions={
          // This used to navigate to /platform/tenants/:id — a SECOND console for the same
          // customer, with ten tabs of its own. That was the two-doors defect: the same record
          // rendered as two different products depending on which link you clicked. Everything
          // that lived there is now further down this page, so this only scrolls.
          <Button
            size="small"
            variant="outlined"
            color="inherit"
            startIcon={<AdvancedIcon />}
            onClick={() => document.getElementById('customer-advanced')?.scrollIntoView({ behavior: 'smooth' })}
          >
            Everything else
          </Button>
        }
      />

      <StatusRibbon state={state} />

      {/*
        The one sentence a rep needs. Provisioning used to report success on a workspace nobody
        could enter, and finding out cost four tabs; the server now says which it is and what
        happens next, and this renders that verbatim rather than deciding for itself.
      */}
      {nextAction && (
        <Box sx={{ mt: 3, pl: 2, borderLeft: 3, borderColor: 'primary.main' }}>
          <Typography
            sx={{
              fontFamily: '"Cambay", "Source Sans 3", sans-serif',
              fontWeight: 700, fontSize: 20, lineHeight: 1.25,
              color: needsSomebody ? 'warning.main' : 'text.primary',
            }}
          >
            {nextAction.label}
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, maxWidth: '68ch' }}>
            {nextAction.detail}
          </Typography>
          {/* The one route from "something is wrong" to the controls that fix it. Without this the
              operator has to know that the answer is inside a collapsed section further down. */}
          {blockers.length > 0 && (
            <Button
              size="small"
              sx={{ mt: 0.5, ml: -1, fontWeight: 700 }}
              onClick={() => {
                // The section list already opens whatever `?section=` names, so this drives the
                // URL rather than reaching into the child's state — and the resulting address is
                // one an operator can paste into a ticket.
                navigate(`/platform/customers/${encodeURIComponent(id)}?section=activation`, { replace: true });
                requestAnimationFrame(() =>
                  document.getElementById('customer-advanced')?.scrollIntoView({ behavior: 'smooth' }));
              }}
            >
              Show me the {blockers.length} outstanding {blockers.length === 1 ? 'item' : 'items'}
            </Button>
          )}
        </Box>
      )}

      {/*
        THE BLOCKER LIST THAT STOOD HERE IS GONE. It was the third of four renderings of the same
        five blockers on one page — the ribbon counted them, the line above named the first one,
        this listed all of them grouped by owner, and the activation section below listed them
        AGAIN with the buttons that actually clear them. Four passes over identical facts is not
        thoroughness; it is why this page read as scattered. The one with the fix buttons wins,
        and the line above now points at it.
      */}

      <Box
        sx={{
          mt: 2.5,
          display: 'grid',
          gap: 2.5,
          gridTemplateColumns: { xs: '1fr', lg: 'repeat(2, minmax(0, 1fr))' },
        }}
      >
        {/*
          Only the slices this page can EDIT. `modules` and `deployment` were rendered here too,
          read-only, while "What they are allowed to use" and the activation section below show the
          same facts with the controls that change them. A read-only card whose editable twin is
          further down the same page is not context, it is a second place to look.
        */}
        {slices.filter((slice) => slice.key !== 'modules' && slice.key !== 'deployment').map((slice) => (
          <SliceCard
            key={slice.key}
            slice={slice}
            editing={(slice.key === 'identity' || slice.key === 'commercial') && slice.editable}
            draft={slice.key === 'commercial' ? stagedCommercial.draft : staged.draft}
            editableKeys={slice.key === 'commercial' ? COMMERCIAL_EDITABLE : undefined}
            onChange={slice.key === 'commercial' ? stagedCommercial.set : staged.set}
          />
        ))}
      </Box>

      {/*
        People sits below the settings and outside the commit bar, deliberately: inviting somebody
        and taking somebody out of service are acts with their own audit verbs and their own
        immediate effect, not edits that should sit in a draft waiting for a Save button.
      */}
      <Box sx={{ mt: 2.5 }}>
        <PeopleSection
          tenantId={id}
          canAdminister={permissions.canAdministerTenants}
          isOwner={permissions.isOwner}
        />
      </Box>

      {saveError && (
        <Alert severity="error" sx={{ mt: 2.5 }} onClose={() => setSaveError(null)}>{saveError}</Alert>
      )}
      {saved && (
        <Alert severity="success" sx={{ mt: 2.5 }} onClose={() => setSaved(null)}>{saved} saved.</Alert>
      )}

      <CommitBar
        changes={[...staged.changes, ...stagedCommercial.changes]}
        busy={commit.isPending}
        blockedReason={commitBlockedReason}
        onDiscard={() => { staged.discard(); stagedCommercial.discard(); }}
        onCommit={(reason) => commit.mutate(reason)}
      />

      {tenant && <CustomerAdvanced tenant={tenant} outstandingCount={blockers.length} />}

      <Typography variant="caption" color="text.disabled" sx={{ display: 'block', mt: 3 }}>
        Version {view.version} · company details and the contract are edited here and saved
        through their own audited endpoints — the profile write carries the version above as
        If-Match. Plan, billing mode, module access and deployment are written by the sections
        under “Everything else”, each through its own audited endpoint, and are shown read-only
        above.
      </Typography>
    </Box>
  );
}

/**
 * Contract and trial dates arrive as full timestamps because they are DateTime on the wire, and
 * a renewal date rendered as `2027-08-31T00:00:00` is precisely the raw-record leakage this
 * screen exists to stop. The time component is not merely noise here — it is meaningless: these
 * are calendar dates a contract names, with no clock attached.
 */
function asDate(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? value
    : parsed.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

/** Where the customer stands, on one line, always visible. */
function StatusRibbon({ state }: { state: TenantConfigurationView['state'] }) {
  const items: Array<{ label: string; value: string; tone?: 'warn' | 'bad' }> = [
    { label: 'Status', value: state.status, tone: state.status === 'Active' ? undefined : 'warn' },
    { label: 'Billing', value: state.billingMode },
    { label: 'Plan', value: state.planCode ?? 'None yet' },
  ];
  if (state.contractEndOn) items.push({ label: 'Renews', value: asDate(state.contractEndOn) });
  if (state.trialEndsOn) items.push({ label: 'Trial ends', value: asDate(state.trialEndsOn) });
  if (state.deploymentProfile !== 'Production') {
    items.push({ label: 'Workspace', value: state.deploymentProfile, tone: 'warn' });
  }
  if (state.offboardingStage !== 'NotScheduled') {
    items.push({ label: 'Offboarding', value: state.offboardingStage, tone: 'bad' });
  }
  if (state.legalHoldActive) items.push({ label: 'Legal hold', value: 'Active', tone: 'bad' });

  return (
    <Box
      sx={{
        mt: 2,
        display: 'flex',
        flexWrap: 'wrap',
        gap: 3,
        px: 2,
        py: 1.5,
        borderRadius: 1,
        border: '1px solid',
        borderColor: 'divider',
        bgcolor: 'action.hover',
      }}
    >
      {items.map((item) => (
        <Box key={item.label}>
          <Typography
            variant="caption"
            sx={{ textTransform: 'uppercase', letterSpacing: '0.08em', color: 'text.secondary' }}
          >
            {item.label}
          </Typography>
          <Typography
            variant="body2"
            sx={{
              fontWeight: 700,
              color: item.tone === 'bad' ? 'error.main' : item.tone === 'warn' ? 'warning.main' : 'text.primary',
            }}
          >
            {item.value}
          </Typography>
        </Box>
      ))}
      {state.statusReason && (
        <Box sx={{ flexBasis: '100%' }}>
          <Typography variant="body2" color="text.secondary">{state.statusReason}</Typography>
        </Box>
      )}
    </Box>
  );
}

/**
 * Input types for the fields whose free-text form was a trap.
 *
 * Dates arrive as ISO strings and were rendered in bare text boxes, so "15/01/2027" reached the
 * server and came back as a JSON binding error quoting a .NET nullable type. Payment terms were
 * coerced with Number(), so "30 days" became NaN and then null — silently clearing the terms on a
 * tenant the server does not refuse.
 */
const FIELD_INPUT: Record<string, object> = {
  contractStartOn: { type: 'date', slotProps: { inputLabel: { shrink: true } } },
  contractEndOn: { type: 'date', slotProps: { inputLabel: { shrink: true } } },
  trialEndsOn: { type: 'date', slotProps: { inputLabel: { shrink: true } } },
  billingStartsOn: { type: 'date', slotProps: { inputLabel: { shrink: true } } },
  paymentTermsDays: {
    type: 'number',
    slotProps: { htmlInput: { min: 0, max: 365, inputMode: 'numeric' } },
    helperText: '0 for due on receipt',
  },
  contactEmail: { type: 'email' },
  billingContactEmail: { type: 'email' },
  accountOwnerEmail: { type: 'email' },
  billingAddress: { multiline: true, minRows: 2 },
};

function SliceCard({
  slice, editing, draft, onChange, editableKeys,
}: {
  slice: TenantConfigurationSlice;
  editing: boolean;
  draft: Record<string, string>;
  onChange: (key: string, value: string) => void;
  /** When present, only these fields become inputs; the rest stay read-only rows. */
  editableKeys?: string[];
}) {
  return (
    <Card variant="outlined">
      <CardContent>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
          <Typography variant="h6" sx={{ fontWeight: 700, flex: 1 }}>{slice.label}</Typography>
          {!slice.editable && (
            // The server decides this, not a role name guessed on the client. A disabled control
            // that does not say who CAN use it is the pattern that sent operators to ask in Slack.
            <Tooltip describeChild title={slice.requiredAuthority}>
              <Chip
                size="small"
                icon={<LockedIcon sx={{ fontSize: 15 }} />}
                label={slice.requiredAuthority}
                variant="outlined"
              />
            </Tooltip>
          )}
        </Stack>

        <Divider sx={{ my: 1.5 }} />

        <Box component="dl" sx={{ m: 0, display: 'grid', gap: editing ? 2 : 1.25 }}>
          {slice.fields.map((field) => (
            editing && !field.derived && (!editableKeys || editableKeys.includes(field.key)) ? (
              field.key === 'countryCode' ? (
                // A picker, as the creation flow already uses. Free text here accepted "UK",
                // which is two ASCII letters and passes the server check, then breaks every
                // country-derived default because the ISO code is GB.
                <Autocomplete
                  key={field.key}
                  size="small"
                  options={COUNTRY_CODES}
                  value={draft[field.key] || null}
                  onChange={(_e, v) => onChange(field.key, v ?? '')}
                  getOptionLabel={(code) => countryLabel(code)}
                  renderInput={(params) => <TextField {...params} label={field.label} />}
                />
              ) : (
                <TextField
                  key={field.key}
                  size="small"
                  label={field.label}
                  value={draft[field.key] ?? ''}
                  onChange={(e) => onChange(field.key, e.target.value)}
                  {...FIELD_INPUT[field.key]}
                />
              )
            ) : (
              <FieldRow key={field.key} field={field} />
            )
          ))}
        </Box>

        {/*
          No per-card save button, on purpose. Every edit on this page leaves through the single
          commit bar at the bottom — the console used to carry sixty-eight independent saves and
          exactly one dirty-state bar between them, which is how an operator edited two sections,
          pressed one button, and silently persisted half their work.
        */}
      </CardContent>
    </Card>
  );
}

function FieldRow({ field }: { field: TenantConfigurationField }) {
  return (
    <Box sx={{ display: 'flex', gap: 2, alignItems: 'baseline' }}>
      <Typography
        component="dt"
        variant="body2"
        color="text.secondary"
        sx={{ minWidth: 150, flexShrink: 0 }}
      >
        {field.label}
      </Typography>
      <Typography
        component="dd"
        variant="body2"
        sx={{ m: 0, fontWeight: field.value ? 600 : 400, color: field.value ? 'text.primary' : 'text.disabled' }}
      >
        {field.value ?? 'Not set'}
        {/*
          A derived value is not keyboard input, and saying WHERE it comes from is what removes
          it from the operator's job. Region, currency, locale and quotas were free-text boxes
          that a rep could not correctly answer and a typo in which blocked activation from a
          different screen than the one that caused it.
        */}
        {field.derived && field.source && (
          <Tooltip describeChild title={field.source}>
            <WhyIcon
              sx={{ fontSize: 14, ml: 0.75, verticalAlign: 'middle', color: 'text.disabled' }}
              aria-label={`Why this value: ${field.source}`}
            />
          </Tooltip>
        )}
      </Typography>
    </Box>
  );
}

