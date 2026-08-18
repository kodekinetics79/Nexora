import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  Divider,
  Paper,
  Switch,
  TextField,
  Typography,
} from '@mui/material';
import { UndoOutlined as UndoIcon } from '@mui/icons-material';
import Stack from '../../components/Flex';
import PageSection from '../../components/PageSection';
import RoleGate from '../../components/RoleGate';
import { ErrorState, LoadingState } from '../../components/States';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import { usePlatformPermissions } from '../../auth/usePlatformPermissions';
import type { Tenant, TenantModuleGrant } from '../../types';

/**
 * The server refuses a shorter one and says so in prose. Mirroring the bound here turns that 400
 * into a disabled Save button, which is the difference between the form telling you and the
 * server telling you after you have filled it in.
 */
const MINIMUM_REASON_LENGTH = 15; // TenantModuleGrantRules.MinimumReasonLength

/**
 * What each switch actually does, in the words of somebody who has to answer for it.
 *
 * <p>This is the half the old screen was missing. It printed sixteen raw catalogue keys as chips
 * — `capability.supplier-search` — and left the operator to work out which product surface each
 * one closed. A control nobody can predict the effect of is a control nobody uses, so every key
 * here names the screens a customer loses, drawn from the server's own
 * `EntitlementEnforcementCoverage` map rather than from a guess.</p>
 */
const MODULE_COPY: Record<string, { label: string; effect: string }> = {
  'module.rfq': {
    label: 'RFQs',
    effect: 'Capturing and extracting customer RFQs. Turning this off closes the RFQ screens and stops document extraction.',
  },
  'module.quotes': {
    label: 'Quotes',
    effect: 'Building, revising and sending quotations against an RFQ.',
  },
  'module.orders': {
    label: 'Orders',
    effect: 'Customer POs and sales orders — everything after a quote is accepted.',
  },
  'module.procurement': {
    label: 'Procurement',
    effect: 'Supplier POs, RFQs to suppliers, and the supplier side of the order.',
  },
  'module.inventory': {
    label: 'Inventory',
    effect: 'Stock intelligence, material traceability and lot/batch records.',
  },
  'capability.ai': {
    label: 'AI assistance',
    effect: 'The in-product agent. Extraction still runs without it; the assistant does not.',
  },
  'capability.ocr': {
    label: 'OCR',
    effect: 'Reading scanned and image-only documents. Text documents still extract without it.',
  },
  'capability.email-intake': {
    label: 'Email intake',
    effect: 'Polling the customer mailbox and turning inbound mail into leads.',
  },
  'capability.supplier-search': {
    label: 'Supplier search',
    effect: 'Finding sourcing candidates from inside procurement.',
  },
  'capability.integrations': {
    label: 'Integrations',
    effect: 'Outbound procurement integrations with the customer’s own systems.',
  },
  'capability.exports': {
    label: 'Exports',
    effect: 'Downloading customer, supplier, product and BOQ data as files.',
  },
  'capability.api': { label: 'API access', effect: 'Programmatic access for the customer’s own systems.' },
  'capability.automation': { label: 'Automation', effect: 'Customer-defined automated workflows.' },
  'capability.sso': { label: 'SSO', effect: 'Single sign-on against the customer’s identity provider.' },
  'capability.scim': { label: 'SCIM', effect: 'Automatic user provisioning from the customer’s directory.' },
  'capability.dedicated-resources': {
    label: 'Dedicated resources',
    effect: 'Isolated extraction capacity rather than the shared pool.',
  },
};

const copyFor = (key: string) => MODULE_COPY[key] ?? { label: key, effect: '' };

const isModule = (key: string) => key.startsWith('module.');

/**
 * Per-customer module control.
 *
 * <p><b>What changed and why.</b> This screen used to read the assigned PLAN and print its
 * feature flags read-only, because the plan was where entitlements lived: granting one customer
 * Procurement, or revoking Inventory from one customer, could only be expressed by moving them to
 * a different plan — which also moved their seat cap, their document quota and their price. So
 * operators cloned a plan per customer and the plan catalogue stopped describing the commercial
 * offer. 20260818013530 moved the grant onto the tenant. A plan now carries capacity and price;
 * this screen carries scope of access, and it is the authority the runtime actually reads.</p>
 */
export default function ModulesTab({ tenant }: { tenant: Tenant }) {
  const permissions = usePlatformPermissions();
  const queryClient = useQueryClient();

  const modulesQuery = useQuery({
    queryKey: platformKeys.tenantModules(tenant.id),
    queryFn: () => platformApi.getTenantModules(tenant.id),
  });

  // Capacity is the plan's half of the answer and it is read here rather than on a separate tab:
  // "what can this customer do" and "how much of it" is one question an operator asks once, and
  // splitting it across two screens is what made the old Entitlements tab feel like a report.
  const plans = useQuery({ queryKey: platformKeys.plans(), queryFn: () => platformApi.listPlans() });

  /** Only the keys the operator has actually touched. Empty means nothing to save. */
  const [draft, setDraft] = useState<Record<string, boolean>>({});
  const [reason, setReason] = useState('');

  const rows = modulesQuery.data?.modules ?? [];
  const effective = (row: TenantModuleGrant) => draft[row.key] ?? row.enabled;

  const changes = useMemo(
    () => rows
      .filter((row) => draft[row.key] !== undefined && draft[row.key] !== row.enabled)
      .map((row) => ({ key: row.key, granting: draft[row.key] })),
    [rows, draft],
  );

  const save = useMutation({
    mutationFn: () => platformApi.updateTenantModules(
      tenant.id,
      // The WHOLE set, not the delta. The server stores every catalogue key explicitly because
      // the activation policy requires each one to be decided, and "absent" is not "off" to it.
      Object.fromEntries(rows.map((row) => [row.key, effective(row)])),
      reason.trim(),
    ),
    onSuccess: (updated) => {
      queryClient.setQueryData(platformKeys.tenantModules(tenant.id), updated);
      // The tenant's activation decision reads the same grant, so it goes stale with it.
      queryClient.invalidateQueries({ queryKey: platformKeys.tenant(tenant.id) });
      setDraft({});
      setReason('');
    },
  });

  if (modulesQuery.isLoading) return <LoadingState label="Reading this customer’s modules…" />;
  if (modulesQuery.isError || !modulesQuery.data) {
    return (
      <ErrorState
        message={platformErrorMessage(modulesQuery.error, 'This customer’s modules could not be read.')}
        onRetry={() => modulesQuery.refetch()}
      />
    );
  }

  const data = modulesQuery.data;
  const dirty = changes.length > 0;
  const reasonTooShort = reason.trim().length < MINIMUM_REASON_LENGTH;
  const grantedCount = rows.filter((row) => effective(row) && row.available).length;
  const plan = plans.data?.find((candidate) => candidate.id === String(data.planId ?? ''));

  const toggle = (row: TenantModuleGrant) => setDraft((current) => ({
    ...current,
    [row.key]: !effective(row),
  }));

  const section = (title: string, subtitle: string, keys: TenantModuleGrant[]) => (
    <PageSection title={title} subtitle={subtitle}>
      <Stack spacing={0}>
        {keys.map((row, index) => (
          <Box key={row.key}>
            {index > 0 && <Divider />}
            <Stack
              direction="row"
              alignItems="flex-start"
              spacing={2}
              sx={{ py: 1.75 }}
            >
              <RoleGate
                allowed={permissions.canAdministerBilling && row.available}
                requirement={row.available
                  ? 'Changing what a customer is entitled to requires the Owner or BillingAdmin role.'
                  : 'This capability has no product behind it yet, so granting it would change nothing.'}
              >
                {(disabled) => (
                  <Switch
                    checked={effective(row)}
                    disabled={disabled || save.isPending}
                    onChange={() => toggle(row)}
                    slotProps={{ input: { 'aria-label': copyFor(row.key).label } }}
                  />
                )}
              </RoleGate>
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Stack direction="row" spacing={1} alignItems="center" sx={{ flexWrap: 'wrap' }}>
                  <Typography sx={{ fontWeight: 700 }}>{copyFor(row.key).label}</Typography>
                  {!row.available && (
                    <Chip
                      size="small"
                      label="Not built yet"
                      variant="outlined"
                      // Deliberately not sellable-looking. The server denies these keys however
                      // the grant reads, and a switch that silently grants nothing is how a
                      // capability nobody implemented ends up on a signed order form.
                      sx={{ fontWeight: 700, fontSize: '0.68rem' }}
                    />
                  )}
                  {row.fromPlanTemplate !== null && row.fromPlanTemplate !== effective(row) && (
                    <Chip
                      size="small"
                      color="warning"
                      variant="outlined"
                      label={row.fromPlanTemplate ? 'Removed from plan' : 'Added beyond plan'}
                      sx={{ fontWeight: 700, fontSize: '0.68rem' }}
                    />
                  )}
                  {draft[row.key] !== undefined && draft[row.key] !== row.enabled && (
                    <Chip
                      size="small"
                      color={draft[row.key] ? 'success' : 'error'}
                      label={draft[row.key] ? 'Will be granted' : 'Will be revoked'}
                      sx={{ fontWeight: 700, fontSize: '0.68rem' }}
                    />
                  )}
                </Stack>
                <Typography variant="body2" color="text.secondary" sx={{ mt: 0.25 }}>
                  {copyFor(row.key).effect}
                </Typography>
              </Box>
            </Stack>
          </Box>
        ))}
      </Stack>
    </PageSection>
  );

  return (
    <Stack spacing={2.5}>
      <Alert severity="info">
        <AlertTitle>
          {data.tenantName} has {grantedCount} of {rows.filter((row) => row.available).length} available
          {' '}modules and capabilities
        </AlertTitle>
        This is what the runtime actually enforces for this customer — not what their plan says.
        {data.planCode
          ? ` Plan ${data.planCode} decides their seats, document quota and price; it does not decide this.`
          : ' This customer has no plan, which affects their capacity and billing but not this screen.'}
      </Alert>

      {plan && (
        <PageSection
          title={`Capacity from plan ${plan.name}`}
          subtitle="Set on the plan, not here — changing it moves every customer on this plan."
        >
          <Stack direction="row" spacing={4} sx={{ flexWrap: 'wrap' }}>
            <Capacity label="Seats" value={plan.seatQuota} />
            <Capacity label="Documents per month" value={plan.monthlyDocQuota} />
            <Capacity label="Concurrent extractions" value={plan.concurrencyCap} />
          </Stack>
        </PageSection>
      )}

      {!permissions.canAdministerBilling && (
        <Alert severity="warning">
          <AlertTitle>You can read this, not change it</AlertTitle>
          Deciding what a customer is entitled to is the same authority that decides what they are
          charged, so it belongs to Owner and BillingAdmin. Ask one of them to make the change.
        </Alert>
      )}

      {section(
        'Product modules',
        'The five parts of the product. Turning one off closes its screens and refuses its API for this customer immediately.',
        rows.filter((row) => isModule(row.key)),
      )}

      {section(
        'Capabilities',
        'Features that cut across the modules. Granting one that is not built yet grants nothing — the server still refuses it.',
        rows.filter((row) => !isModule(row.key)),
      )}

      {/* The commit bar. Kept out of the sections so there is exactly one Save on the screen and
          it can never be mistaken for a per-row control that has already taken effect. */}
      <Paper sx={{ p: { xs: 2, md: 3 }, borderRadius: 3, position: 'sticky', bottom: 16 }}>
        <Stack spacing={2}>
          <Box>
            <Typography sx={{ fontWeight: 800 }}>
              {dirty ? `${changes.length} pending change${changes.length === 1 ? '' : 's'}` : 'No pending changes'}
            </Typography>
            <Typography variant="body2" color="text.secondary">
              {dirty
                ? changes
                  .map((change) => `${change.granting ? 'Grant' : 'Revoke'} ${copyFor(change.key).label}`)
                  .join(' · ')
                : 'Flip a switch above to stage a change. Nothing is applied until you save.'}
            </Typography>
          </Box>

          {dirty && (
            <TextField
              label="Why"
              required
              multiline
              minRows={2}
              value={reason}
              onChange={(event) => setReason(event.target.value)}
              disabled={save.isPending}
              error={reason.length > 0 && reasonTooShort}
              helperText={
                reason.length > 0 && reasonTooShort
                  ? `At least ${MINIMUM_REASON_LENGTH} characters.`
                  : 'Recorded on the audit trail. Revoking takes work away from a live customer; this is what explains it later.'
              }
              fullWidth
            />
          )}

          {save.isError && (
            <Alert severity="error">
              {platformErrorMessage(save.error, 'The change could not be saved.')}
            </Alert>
          )}

          <Stack direction="row" spacing={1.5} sx={{ flexWrap: 'wrap' }}>
            <RoleGate
              allowed={permissions.canAdministerBilling}
              requirement="Owner or BillingAdmin."
            >
              {(disabled) => (
                <Button
                  variant="contained"
                  disabled={disabled || !dirty || reasonTooShort || save.isPending}
                  onClick={() => save.mutate()}
                >
                  {save.isPending ? 'Saving…' : 'Save module access'}
                </Button>
              )}
            </RoleGate>
            <Button
              startIcon={<UndoIcon />}
              disabled={!dirty || save.isPending}
              onClick={() => { setDraft({}); setReason(''); }}
            >
              Discard
            </Button>
          </Stack>
        </Stack>
      </Paper>
    </Stack>
  );
}

/** One plan capacity figure. Null is "unlimited" on the wire and must not read as zero. */
function Capacity({ label, value }: { label: string; value: number | null }) {
  return (
    <Box>
      <Typography variant="caption" color="text.secondary">{label}</Typography>
      <Typography variant="h6" sx={{ fontWeight: 800 }}>
        {value == null ? 'Unlimited' : value.toLocaleString()}
      </Typography>
    </Box>
  );
}
