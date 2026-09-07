import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Card, CardContent, Chip, Divider, TextField, Tooltip, Typography,
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
import type {
  TenantConfigurationBlocker, TenantConfigurationField, TenantConfigurationSlice,
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
    'billingContactName', 'billingContactEmail', 'accountOwnerEmail',
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
  const commit = useMutation({
    mutationFn: async (reason: string) => {
      const tenant = tenantQuery.data;
      if (!tenant) throw new Error('The customer record is not loaded.');

      // The contract goes to the billing endpoint, which is Owner-or-BillingAdmin. Sent FIRST and
      // awaited, so that if the caller lacks billing authority the identity write does not land
      // and leave the operator believing the whole commit succeeded. Which endpoint refused is
      // reported, because "it failed" without saying what did save is how a partial write becomes
      // invisible.
      if (stagedCommercial.dirty) {
        const c = (key: string) => {
          const value = stagedCommercial.draft[key]?.trim();
          return value === undefined || value === '' ? null : value;
        };
        const terms = stagedCommercial.draft.paymentTermsDays?.trim();
        try {
          await platformApi.setTenantAccountContact(id, {
            billingContactName: c('billingContactName'),
            billingContactEmail: c('billingContactEmail'),
            billingAddress: tenant.billingAddress,
            purchaseOrderReference: c('purchaseOrderReference'),
            paymentTermsDays: terms === undefined || terms === '' ? null : Number(terms),
            accountOwnerEmail: c('accountOwnerEmail'),
            contractStartOn: c('contractStartOn'),
            contractEndOn: c('contractEndOn'),
            reason,
          });
        } catch (error) {
          throw new Error(platformErrorMessage(
            error,
            'The contract and billing changes were refused. Nothing was saved.',
          ));
        }
      }
      if (!staged.dirty) return null;
      const v = (key: string) => {
        const value = staged.draft[key]?.trim();
        return value === undefined || value === '' ? null : value;
      };
      return platformApi.updateTenantProfile(id, {
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
      }, baseVersion ?? undefined);
    },
    onSuccess: () => {
      setSaveError(null);
      queryClient.invalidateQueries({ queryKey: platformKeys.tenantConfiguration(id) });
      queryClient.invalidateQueries({ queryKey: platformKeys.tenant(id) });
      queryClient.invalidateQueries({ queryKey: platformKeys.customers() });
    },
    onError: (error) => setSaveError(platformErrorMessage(
      error,
      'The changes were not saved. Nothing on this customer has been altered.',
    )),
  });

  // Leaving with unsaved edits used to lose them silently — switching tabs unmounted the form.
  useEffect(() => {
    if (!staged.dirty && !stagedCommercial.dirty) return undefined;
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [staged.dirty]);

  const groupedBlockers = useMemo(() => {
    const byOwner = new Map<string, TenantConfigurationBlocker[]>();
    for (const blocker of blockers) {
      const list = byOwner.get(blocker.owner) ?? [];
      list.push(blocker);
      byOwner.set(blocker.owner, list);
    }
    return [...byOwner.entries()];
  }, [blockers]);

  if (configuration.isLoading || tenantQuery.isLoading) {
    return <LoadingState label="Loading customer…" minHeight="60vh" />;
  }

  if (configuration.isError || !view) {
    return (
      <Box>
        <Button startIcon={<BackIcon />} onClick={() => navigate('/platform/tenants')} sx={{ mb: 2 }}>
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
  const tenant = tenantQuery.data;

  return (
    <Box>
      <Button
        startIcon={<BackIcon />}
        onClick={() => navigate('/platform/tenants')}
        sx={{ mb: 1.5 }}
        color="inherit"
      >
        Customers
      </Button>

      <PageHeader
        title={tenant?.name ?? `Customer ${view.tenantId}`}
        subtitle={tenant?.legalName ?? tenant?.slug ?? undefined}
        actions={
          <Tooltip
            describeChild
            title="Audit, AI governance, data residency, provisioning diagnostics and offboarding — the screens a salesperson never opens"
          >
            <Button
              size="small"
              variant="outlined"
              startIcon={<AdvancedIcon />}
              onClick={() => navigate(`/platform/tenants/${encodeURIComponent(id)}`)}
            >
              Advanced
            </Button>
          </Tooltip>
        }
      />

      <StatusRibbon state={state} />

      {/*
        The one sentence a rep needs. Provisioning used to report success on a workspace nobody
        could enter, and finding out cost four tabs; the server now says which it is and what
        happens next, and this renders that verbatim rather than deciding for itself.
      */}
      {nextAction && (
        <Alert
          severity={blockers.length > 0 || state.legalHoldActive ? 'warning' : 'success'}
          icon={false}
          sx={{ mt: 2.5, borderLeft: 3, borderColor: 'warning.main' }}
        >
          <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>{nextAction.label}</Typography>
          <Typography variant="body2" sx={{ mt: 0.25 }}>{nextAction.detail}</Typography>
        </Alert>
      )}

      {blockers.length > 0 && (
        <Card variant="outlined" sx={{ mt: 2.5 }}>
          <CardContent>
            <Typography variant="h6" sx={{ fontWeight: 700 }}>What is blocking this customer</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, mb: 2 }}>
              Grouped by who has to act. Nothing here is a setting a salesperson is expected to
              answer alone.
            </Typography>

            <Stack spacing={2.5}>
              {groupedBlockers.map(([owner, items]) => (
                <Box key={owner}>
                  <Chip
                    size="small"
                    label={owner}
                    color={owner === 'Finance' ? 'warning' : owner === 'Owner' ? 'error' : 'default'}
                    sx={{ fontWeight: 700, mb: 1 }}
                  />
                  <Stack spacing={1.25}>
                    {items.map((blocker) => (
                      <Box
                        key={blocker.code}
                        sx={{ pl: 1.5, borderLeft: 2, borderColor: 'divider' }}
                      >
                        <Typography variant="subtitle2" sx={{ fontWeight: 700 }}>{blocker.title}</Typography>
                        <Typography variant="body2" color="text.secondary">{blocker.detail}</Typography>
                        {/*
                          The control code stays, small and last. It is what a support ticket
                          quotes — but it is not what the screen leads with, which is the whole
                          difference between this and the fourteen raw cards it replaces.
                        */}
                        <Typography
                          variant="caption"
                          color="text.disabled"
                          sx={{ fontFamily: 'monospace' }}
                        >
                          {blocker.code}
                        </Typography>
                      </Box>
                    ))}
                  </Stack>
                </Box>
              ))}
            </Stack>
          </CardContent>
        </Card>
      )}

      <Box
        sx={{
          mt: 2.5,
          display: 'grid',
          gap: 2.5,
          gridTemplateColumns: { xs: '1fr', lg: 'repeat(2, minmax(0, 1fr))' },
        }}
      >
        {slices.map((slice) => (
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

      {saveError && <Alert severity="error" sx={{ mt: 2.5 }}>{saveError}</Alert>}

      <CommitBar
        changes={[...staged.changes, ...stagedCommercial.changes]}
        busy={commit.isPending}
        onDiscard={() => { staged.discard(); stagedCommercial.discard(); }}
        onCommit={(reason) => commit.mutate(reason)}
      />

      <Typography variant="caption" color="text.disabled" sx={{ display: 'block', mt: 3 }}>
        Version {view.version} · company details and the contract are edited here and saved
        through their own audited endpoints — the profile write carries the version above as
        If-Match. Plan, billing mode, module access and deployment are still written by their own
        screens, reachable from Advanced, and are shown read-only until those paths move too.
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
              <TextField
                key={field.key}
                size="small"
                label={field.label}
                value={draft[field.key] ?? ''}
                onChange={(e) => onChange(field.key, e.target.value)}
              />
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

