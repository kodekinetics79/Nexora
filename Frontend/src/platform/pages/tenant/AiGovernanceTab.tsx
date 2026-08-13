import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, AlertTitle, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle,
  FormControlLabel, Grid, Paper, Switch, Table, TableBody, TableCell, TableHead, TableRow,
  TextField, Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import PageSection from '../../components/PageSection';
import ReasonDialog from '../../components/ReasonDialog';
import { ErrorState, LoadingState } from '../../components/States';
import { SoftChip } from '../../components/StatusChip';
import { fmtDateTime } from '../../components/format';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import type {
  AiExtractionReadinessCheck, AiExtractionReadinessReport, AiReadinessStatus,
  AuthorizeAiProviderInput, Tenant, TenantAiPolicy,
} from '../../types';

const optionalNumber = (value: string): number | null => value.trim() === '' ? null : Number(value);

/**
 * The extraction pre-flight.
 *
 * <b>The defect this closes.</b> Five controls in three layers must all agree before an
 * unstructured document is read, they refuse with different reason codes, and each one is
 * discoverable only by fixing the previous one and resubmitting. The 2026-08 pilot dead-lettered
 * every document it sent for days doing exactly that, one lock per deploy, while the extraction
 * log said the call had been allowed right up to the token ledger refusing it for a capital
 * letter. This panel reports all of them at once, in the order they fire.
 *
 * <b>It diagnoses and nothing else.</b> There is deliberately no button here that opens a
 * control. Letting a customer's document text leave their infrastructure stays an explicit,
 * attributable human act with a written justification and an expiry — the two Owner controls
 * further down this tab, which each row names.
 */

const STATUS_TONE: Record<AiReadinessStatus, 'success' | 'error' | 'neutral'> = {
  Pass: 'success',
  Fail: 'error',
  // Grey, never green: a control that cannot bite here has not been satisfied, and a tick would
  // tell a reader that a local deployment had passed an egress check it never ran.
  NotApplicable: 'neutral',
};

const STATUS_LABEL: Record<AiReadinessStatus, string> = {
  Pass: 'Satisfied',
  Fail: 'Blocking',
  NotApplicable: 'Not applicable',
};

/**
 * A value that has to reach a form field byte for byte — the normalised endpoint origin, and the
 * model id, which AllowedModel compares ORDINAL. Rendered selectable and copyable rather than as
 * something to retype, because one capital letter refuses every document the tenant submits.
 */
function ExactValue({ label, value }: { label: string; value: string }) {
  const [notice, setNotice] = useState<string | null>(null);

  const copy = () => {
    if (!navigator.clipboard?.writeText) {
      setNotice('Copying is unavailable in this browser — select the value and copy it manually.');
      return;
    }
    navigator.clipboard.writeText(value)
      .then(() => setNotice('Copied exactly as shown.'))
      .catch(() => setNotice('Copy failed — select the value and copy it manually.'));
  };

  return (
    <Box sx={{ mt: 0.75 }}>
      <Typography variant="caption" color="text.secondary">{label}</Typography>
      <Stack direction="row" spacing={1} alignItems="center" sx={{ flexWrap: 'wrap' }}>
        <Box
          component="code"
          sx={{
            px: 1, py: 0.5, borderRadius: 1, bgcolor: 'action.hover', fontFamily: 'monospace',
            fontSize: '0.8rem', fontWeight: 700, userSelect: 'all', overflowWrap: 'anywhere',
          }}
        >
          {value}
        </Box>
        <Button size="small" onClick={copy}>Copy</Button>
      </Stack>
      {notice && <Typography variant="caption" color="text.secondary">{notice}</Typography>}
    </Box>
  );
}

function ReadinessCheckRow({ check }: { check: AiExtractionReadinessCheck }) {
  const blocking = check.status === 'Fail';
  return (
    <Paper
      variant="outlined"
      sx={{
        p: blocking ? 2 : 1.25,
        borderColor: blocking ? 'error.main' : undefined,
        opacity: check.status === 'NotApplicable' ? 0.75 : 1,
      }}
    >
      <Stack direction="row" spacing={1} alignItems="center" sx={{ flexWrap: 'wrap' }}>
        <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 800, minWidth: 18 }}>
          {check.order}
        </Typography>
        <Typography sx={{ fontWeight: 700 }}>{check.title}</Typography>
        <SoftChip label={STATUS_LABEL[check.status]} tone={STATUS_TONE[check.status]} dot={false} />
        {check.denialReason && (
          <>
            <Typography variant="caption" color="text.secondary">refuses with</Typography>
            {/* Selectable, and the exact string the enforcing layer emits, so this row can be
                matched against a dead-lettered job's stored error. */}
            <Box
              component="code"
              sx={{ fontFamily: 'monospace', fontSize: '0.78rem', fontWeight: 700, color: 'error.main', userSelect: 'all' }}
            >
              {check.denialReason}
            </Box>
          </>
        )}
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ overflowWrap: 'anywhere' }}>
        Now: {check.currentValue}
      </Typography>
      {blocking && (
        <>
          <ExactValue label="Required value" value={check.requiredValue} />
          <Typography variant="body2" sx={{ mt: 0.75, fontWeight: 650, overflowWrap: 'anywhere' }}>
            Set it in: {check.setItIn}
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>{check.detail}</Typography>
        </>
      )}
    </Paper>
  );
}

function ReadinessReport({ report }: { report: AiExtractionReadinessReport }) {
  const resolved = report.resolvedProvider;
  return (
    <>
      <Alert severity={report.ready ? 'success' : 'error'}>
        <AlertTitle sx={{ fontWeight: 800 }}>
          {report.ready
            ? 'Documents will extract'
            : `Documents will not extract — ${report.blockingCount} control${report.blockingCount === 1 ? '' : 's'} blocking`}
        </AlertTitle>
        {report.ready ? (
          <Typography variant="body2">
            Every control in the chain is open for unstructured {report.purpose} on this tenant.
          </Typography>
        ) : (
          <>
            <Typography variant="body2">
              A document submitted now is refused with{' '}
              <Box component="code" sx={{ fontFamily: 'monospace', fontWeight: 700, userSelect: 'all' }}>
                {report.firstBlockingReason}
              </Box>
              .
            </Typography>
            {/* The whole point of the panel: the gate can only ever name the first refusal, so an
                operator who fixes it and resubmits meets the next one, and the next. */}
            <Typography variant="body2" sx={{ mt: 1 }}>
              That is only the first one. Every control marked blocking below has to be opened —
              fixing one reveals the next, which is what cost the pilot its first week.
            </Typography>
          </>
        )}
      </Alert>

      {resolved.endpoint && <ExactValue label="Endpoint origin a grant must name, exactly" value={resolved.endpoint} />}
      {resolved.model && <ExactValue label="Model id, compared case-sensitively" value={resolved.model} />}

      <Stack spacing={1} sx={{ mt: 2 }}>
        {report.checks.map((check) => <ReadinessCheckRow key={check.code} check={check} />)}
      </Stack>

      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1.5 }}>
        Evaluated {fmtDateTime(report.evaluatedOnUtc)} against provider {resolved.provider || 'Unknown'}.
        This report reads; it never changes anything and offers no control that would. Every value is
        set through the audited Owner requests each row names.
      </Typography>
    </>
  );
}

export default function AiGovernanceTab({ tenant }: { tenant: Tenant }) {
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [policyOpen, setPolicyOpen] = useState(false);
  const [draft, setDraft] = useState<TenantAiPolicy | null>(null);
  const [policyReason, setPolicyReason] = useState('');
  const [authorizeOpen, setAuthorizeOpen] = useState(false);
  const [revokeId, setRevokeId] = useState<string | null>(null);
  const [provider, setProvider] = useState({
    provider: '', endpoint: '', model: '', purposes: 'RfqExtraction', expiresOn: '', unstructured: false,
  });

  const policyQuery = useQuery({
    queryKey: platformKeys.tenantAiPolicy(tenant.id),
    queryFn: () => platformApi.getTenantAiPolicy(tenant.id),
  });
  const providersQuery = useQuery({
    queryKey: platformKeys.tenantAiProviders(tenant.id),
    queryFn: () => platformApi.getTenantAiProviders(tenant.id),
  });
  const readinessQuery = useQuery({
    queryKey: platformKeys.tenantAiReadiness(tenant.id),
    queryFn: () => platformApi.getTenantAiReadiness(tenant.id),
  });

  useEffect(() => {
    if (policyQuery.data) setDraft(policyQuery.data);
  }, [policyQuery.data]);

  const invalidate = () => {
    client.invalidateQueries({ queryKey: platformKeys.tenantAiPolicy(tenant.id) });
    client.invalidateQueries({ queryKey: platformKeys.tenantAiProviders(tenant.id) });
    // The verdict is the policy and the grants combined, so it is stale the moment either
    // changes — and a stale "will not extract" is what sends an operator round the loop again.
    client.invalidateQueries({ queryKey: platformKeys.tenantAiReadiness(tenant.id) });
  };
  const fail = (fallback: string) => (error: unknown) =>
    enqueueSnackbar(platformErrorMessage(error, fallback), { variant: 'error' });

  const updatePolicy = useMutation({
    mutationFn: () => platformApi.updateTenantAiPolicy(tenant.id, {
      ...draft!,
      allowedPurposes: draft!.allowedPurposes,
      reason: policyReason.trim(),
    }),
    onSuccess: () => {
      enqueueSnackbar('AI policy updated', { variant: 'success' });
      setPolicyOpen(false);
      invalidate();
    },
    onError: fail('The AI policy change was refused'),
  });
  const authorize = useMutation({
    mutationFn: (justification: string) => platformApi.authorizeTenantAiProvider(tenant.id, {
      provider: provider.provider.trim(),
      endpoint: provider.endpoint.trim(),
      model: provider.model.trim() || null,
      allowedPurposes: provider.purposes.trim(),
      unstructuredDocumentsAllowed: provider.unstructured,
      justification,
      expiresOn: provider.expiresOn ? new Date(provider.expiresOn).toISOString() : null,
    } satisfies AuthorizeAiProviderInput),
    onSuccess: () => {
      enqueueSnackbar('External AI provider authorized', { variant: 'success' });
      setAuthorizeOpen(false);
      invalidate();
    },
    onError: fail('The provider authorization was refused'),
  });
  const revoke = useMutation({
    mutationFn: (reason: string) => platformApi.revokeTenantAiProvider(tenant.id, revokeId!, reason),
    onSuccess: () => {
      enqueueSnackbar('Provider authorization revoked', { variant: 'success' });
      setRevokeId(null);
      invalidate();
    },
    onError: fail('The provider revocation was refused'),
  });

  if (policyQuery.isLoading || providersQuery.isLoading) return <LoadingState label="Reading AI governance…" />;
  if (policyQuery.isError || !policyQuery.data) {
    return <ErrorState message={platformErrorMessage(policyQuery.error, 'The tenant AI policy could not be read.')} onRetry={() => policyQuery.refetch()} />;
  }
  if (providersQuery.isError || !providersQuery.data) {
    return <ErrorState message={platformErrorMessage(providersQuery.error, 'Provider authorizations could not be read.')} onRetry={() => providersQuery.refetch()} />;
  }

  const policy = policyQuery.data;
  const trust = providersQuery.data;
  const resolved = trust.resolvedProvider;
  const field = (label: string, value: unknown) => (
    <Box><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="body2" sx={{ fontWeight: 650 }}>{value == null || value === '' ? 'Not set' : String(value)}</Typography></Box>
  );

  return (
    <Stack spacing={2.5}>
      <Alert severity="info">Owner authority only. Changes are version-checked, attributed, and written to the platform audit trail.</Alert>

      <PageSection
        title="Will documents extract?"
        subtitle="Every control that must agree before an unstructured RFQ document can be read by AI, in the order it fires. Read-only."
      >
        {readinessQuery.isLoading && <LoadingState label="Running the extraction pre-flight…" minHeight={120} />}
        {/* An unreadable pre-flight never hides the policy and grant editors below it: the
            operator can still act, they just do not get told what to act on. */}
        {readinessQuery.isError && (
          <Alert
            severity="warning"
            action={<Button color="inherit" size="small" onClick={() => readinessQuery.refetch()}>Retry</Button>}
          >
            <AlertTitle sx={{ fontWeight: 800 }}>The pre-flight could not be read</AlertTitle>
            {platformErrorMessage(readinessQuery.error, 'The extraction readiness report could not be read.')}
            {' '}Nothing can be said about whether documents will extract — this is not the same as them being ready.
          </Alert>
        )}
        {readinessQuery.data && <ReadinessReport report={readinessQuery.data} />}
      </PageSection>

      <PageSection title="Effective AI policy" actions={<Button variant="outlined" onClick={() => { setDraft(policy); setPolicyReason(''); setPolicyOpen(true); }}>Edit policy</Button>}>
        <Grid container spacing={2}>
          <Grid size={{ xs: 12, md: 4 }}>{field('Processing', policy.isEnabled ? 'Enabled' : 'Emergency shutdown')}</Grid>
          <Grid size={{ xs: 12, md: 4 }}>{field('External processing', policy.externalProcessingAllowed ? 'Allowed with controls' : 'Denied')}</Grid>
          <Grid size={{ xs: 12, md: 4 }}>{field('Purposes', policy.allowedPurposes.join(', ') || 'None')}</Grid>
          <Grid size={{ xs: 12, md: 4 }}>{field('Provider / model', `${policy.allowedProvider ?? 'None'} / ${policy.allowedModel ?? 'None'}`)}</Grid>
          <Grid size={{ xs: 12, md: 4 }}>{field('Token budgets (soft / hard / document)', `${policy.monthlySoftTokenLimit ?? 'None'} / ${policy.monthlyHardTokenLimit ?? 'None'} / ${policy.maxTokensPerDocument ?? 'None'}`)}</Grid>
          <Grid size={{ xs: 12, md: 4 }}>{field('External dependency ceiling', `${policy.externalDependencyCeilingPercent}%`)}</Grid>
          <Grid size={{ xs: 12, md: 4 }}>{field('Privacy controls', `Redaction ${policy.redactionRequired ? 'required' : 'optional'}; review ${policy.privacyReviewRequired ? 'required' : 'optional'}`)}</Grid>
          <Grid size={{ xs: 12, md: 4 }}>{field('Classification / egress', `${policy.allowedDataClassifications} / ${policy.egressPolicy}`)}</Grid>
          <Grid size={{ xs: 12, md: 4 }}>{field('Residency / retention', `${policy.dataResidency} / ${policy.retentionDays} days`)}</Grid>
          <Grid size={{ xs: 12, md: 4 }}>{field('External pricing', `${policy.externalInputCostPerMillionTokens ?? '—'} / ${policy.externalOutputCostPerMillionTokens ?? '—'} ${policy.externalCostCurrency ?? ''} (${policy.externalPricingVersion ?? 'unversioned'})`)}</Grid>
          <Grid size={{ xs: 12, md: 4 }}>{field('Local pricing', `${policy.localComputeCostPerHour ?? '—'} compute/hour; ${policy.ocrCostPerPage ?? '—'} OCR/page ${policy.localCostCurrency ?? ''}`)}</Grid>
          <Grid size={{ xs: 12, md: 4 }}>{field('Version', `v${policy.version}, ${fmtDateTime(policy.updatedOn)} by ${policy.updatedBy}`)}</Grid>
        </Grid>
      </PageSection>

      <PageSection title="External provider authorization" actions={<Button variant="contained" onClick={() => { setProvider({ provider: resolved.provider, endpoint: resolved.endpoint, model: resolved.model, purposes: 'RfqExtraction', expiresOn: '', unstructured: false }); setAuthorizeOpen(true); }}>Authorize provider</Button>}>
        <Alert severity={resolved.providerClass === 'External' ? 'warning' : 'success'} sx={{ mb: 2 }}>
          Resolved deployment target: {resolved.provider || 'Unknown'} · {resolved.endpoint || 'Unresolved'} · {resolved.model || 'No model'} ({resolved.providerClass}). Decision: {trust.resolvedProviderDecisionReason}.
        </Alert>
        <Table size="small"><TableHead><TableRow><TableCell>Provider</TableCell><TableCell>Scope</TableCell><TableCell>Authorized</TableCell><TableCell>Status</TableCell><TableCell align="right">Action</TableCell></TableRow></TableHead><TableBody>
          {trust.authorizations.map((item) => <TableRow key={item.id}><TableCell>{item.provider}<Typography variant="caption" sx={{ display: 'block' }}>{item.endpoint} · {item.model}</Typography></TableCell><TableCell>{item.allowedPurposes}<Typography variant="caption" sx={{ display: 'block' }}>{item.unstructuredDocumentsAllowed ? 'Unstructured documents allowed' : 'Structured/redacted only'}</Typography></TableCell><TableCell>{fmtDateTime(item.authorizedOn)} by {item.authorizedBy}<Typography variant="caption" sx={{ display: 'block' }}>{item.justification}</Typography></TableCell><TableCell><SoftChip label={item.isActive ? 'Active' : item.revokedOn ? 'Revoked' : 'Expired'} tone={item.isActive ? 'success' : 'neutral'} /></TableCell><TableCell align="right">{item.isActive && <Button color="error" size="small" onClick={() => setRevokeId(item.id)}>Revoke</Button>}</TableCell></TableRow>)}
          {trust.authorizations.length === 0 && <TableRow><TableCell colSpan={5} align="center">No provider authorizations recorded.</TableCell></TableRow>}
        </TableBody></Table>
      </PageSection>

      <Dialog open={policyOpen} onClose={() => !updatePolicy.isPending && setPolicyOpen(false)} fullWidth maxWidth="md">
        <DialogTitle>Edit tenant AI policy</DialogTitle><DialogContent dividers>{draft && <Stack spacing={2}>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}><FormControlLabel control={<Switch checked={draft.isEnabled} onChange={(_, value) => setDraft({ ...draft, isEnabled: value })} />} label="AI processing enabled" /><FormControlLabel control={<Switch checked={draft.externalProcessingAllowed} onChange={(_, value) => setDraft({ ...draft, externalProcessingAllowed: value })} />} label="External processing allowed" /><FormControlLabel control={<Switch checked={draft.redactionRequired} onChange={(_, value) => setDraft({ ...draft, redactionRequired: value })} />} label="Redaction required" /><FormControlLabel control={<Switch checked={draft.privacyReviewRequired} onChange={(_, value) => setDraft({ ...draft, privacyReviewRequired: value })} />} label="Privacy review required" /></Stack>
          <TextField label="Allowed purposes (comma-separated)" value={draft.allowedPurposes.join(',')} onChange={(e) => setDraft({ ...draft, allowedPurposes: e.target.value.split(',').map((x) => x.trim()).filter(Boolean) })} />
          <Grid container spacing={2}>
            <Grid size={{ xs: 12, sm: 6 }}><TextField fullWidth label="Allowed provider" value={draft.allowedProvider ?? ''} onChange={(e) => setDraft({ ...draft, allowedProvider: e.target.value || null })} /></Grid>
            <Grid size={{ xs: 12, sm: 6 }}><TextField fullWidth label="Allowed model" value={draft.allowedModel ?? ''} onChange={(e) => setDraft({ ...draft, allowedModel: e.target.value || null })} /></Grid>
            {([['Monthly soft token limit', 'monthlySoftTokenLimit'], ['Monthly hard token limit', 'monthlyHardTokenLimit'], ['Document token limit', 'maxTokensPerDocument'], ['External input cost / 1M', 'externalInputCostPerMillionTokens'], ['External output cost / 1M', 'externalOutputCostPerMillionTokens'], ['Local compute cost / hour', 'localComputeCostPerHour'], ['OCR cost / page', 'ocrCostPerPage']] as const).map(([label, key]) => <Grid key={key} size={{ xs: 12, sm: 6 }}><TextField fullWidth type="number" label={label} value={draft[key] ?? ''} onChange={(e) => setDraft({ ...draft, [key]: optionalNumber(e.target.value) })} /></Grid>)}
            <Grid size={{ xs: 12, sm: 6 }}><TextField fullWidth type="number" label="External dependency ceiling (%)" value={draft.externalDependencyCeilingPercent} onChange={(e) => setDraft({ ...draft, externalDependencyCeilingPercent: Number(e.target.value) })} slotProps={{ htmlInput: { min: 0, max: 10 } }} /></Grid>
            <Grid size={{ xs: 12, sm: 6 }}><TextField fullWidth type="number" label="Retention days" value={draft.retentionDays} onChange={(e) => setDraft({ ...draft, retentionDays: Number(e.target.value) })} /></Grid>
            {([['Data classifications', 'allowedDataClassifications'], ['Egress policy', 'egressPolicy'], ['Data residency', 'dataResidency'], ['External cost currency', 'externalCostCurrency'], ['External pricing version', 'externalPricingVersion'], ['Local cost currency', 'localCostCurrency']] as const).map(([label, key]) => <Grid key={key} size={{ xs: 12, sm: 6 }}><TextField fullWidth label={label} value={draft[key] ?? ''} onChange={(e) => setDraft({ ...draft, [key]: e.target.value || null })} /></Grid>)}
          </Grid>
          <FormControlLabel control={<Checkbox checked={draft.inputOutputAuditAllowed} onChange={(_, value) => setDraft({ ...draft, inputOutputAuditAllowed: value })} />} label="Input/output audit content permitted" />
          <TextField label="Change reason" value={policyReason} onChange={(e) => setPolicyReason(e.target.value)} required multiline minRows={2} />
        </Stack>}</DialogContent><DialogActions><Button onClick={() => setPolicyOpen(false)}>Cancel</Button><Button variant="contained" disabled={!policyReason.trim() || updatePolicy.isPending} onClick={() => updatePolicy.mutate()}>Save policy</Button></DialogActions>
      </Dialog>

      <ReasonDialog open={authorizeOpen} title="Authorize external AI provider" confirmLabel="Authorize provider" minReasonLength={5} reasonLabel="Justification / approval reference" description="Grants this tenant access to one exact external endpoint and model. The provider, scope and approval remain auditable." extra={<Stack spacing={2}><TextField label="Provider" value={provider.provider} onChange={(e) => setProvider({ ...provider, provider: e.target.value })} /><TextField label="Endpoint" value={provider.endpoint} onChange={(e) => setProvider({ ...provider, endpoint: e.target.value })} /><TextField label="Model" value={provider.model} onChange={(e) => setProvider({ ...provider, model: e.target.value })} /><TextField label="Allowed purposes" value={provider.purposes} onChange={(e) => setProvider({ ...provider, purposes: e.target.value })} /><TextField label="Expires on" type="datetime-local" value={provider.expiresOn} onChange={(e) => setProvider({ ...provider, expiresOn: e.target.value })} slotProps={{ inputLabel: { shrink: true } }} /><FormControlLabel control={<Checkbox checked={provider.unstructured} onChange={(_, value) => setProvider({ ...provider, unstructured: value })} />} label="Allow unstructured document content" /></Stack>} extraProblem={!provider.provider.trim() || !provider.endpoint.trim() || !provider.purposes.trim() ? 'Provider, endpoint and at least one purpose are required.' : null} busy={authorize.isPending} onClose={() => setAuthorizeOpen(false)} onConfirm={(reason) => authorize.mutate(reason)} />
      <ReasonDialog open={revokeId !== null} title="Revoke provider authorization" confirmLabel="Revoke authorization" confirmColor="error" minReasonLength={5} description="External calls covered by this grant will fail closed immediately. The authorization record remains in the audit history." busy={revoke.isPending} onClose={() => setRevokeId(null)} onConfirm={(reason) => revoke.mutate(reason)} />
    </Stack>
  );
}
