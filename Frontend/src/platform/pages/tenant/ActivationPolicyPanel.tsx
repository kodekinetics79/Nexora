import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, AlertTitle, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle,
  FormControl, InputLabel, MenuItem, Paper, Select, TextField, Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import { ErrorState, LoadingState } from '../../components/States';
import { SoftChip } from '../../components/StatusChip';
import { fmtDateTime } from '../../components/format';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import type {
  RecordActivationControlEvidenceInput, Tenant,
} from '../../types';
import { isAbsoluteHttpUrl, isActivationEvidenceValid, isSha256 } from './dataGovernanceValidation';

const EVIDENCE_CONTROLS = new Set(['security.privileged-mfa-policy', 'integrations.mandatory']);
const localNow = () => {
  const now = new Date();
  return new Date(now.getTime() - now.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
};
const toUtc = (value: string) => value ? new Date(value).toISOString() : null;

interface EvidenceDraft {
  controlCode: string;
  disposition: 'approved' | 'deferred';
  evidenceReference: string;
  evidenceSha256: string;
  effectiveFrom: string;
  effectiveTo: string;
  reason: string;
}

const newEvidence = (controlCode: string): EvidenceDraft => ({
  controlCode,
  disposition: 'approved',
  evidenceReference: '',
  evidenceSha256: '',
  effectiveFrom: localNow(),
  effectiveTo: '',
  reason: '',
});

export default function ActivationPolicyPanel({ tenant }: { tenant: Tenant }) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [evidence, setEvidence] = useState<EvidenceDraft | null>(null);
  const [confirmActivation, setConfirmActivation] = useState(false);
  const decisionQuery = useQuery({
    queryKey: platformKeys.tenantActivationDecision(tenant.id),
    queryFn: () => platformApi.getTenantActivationDecision(tenant.id),
  });
  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantActivationDecision(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenant(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
  };
  const activateMutation = useMutation({
    mutationFn: () => platformApi.activateTenant(tenant.id),
    onSuccess: () => {
      setConfirmActivation(false);
      refresh();
      enqueueSnackbar('Tenant activated under the authoritative policy', { variant: 'success' });
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Activation was refused'), { variant: 'error' }),
  });
  const evidenceMutation = useMutation({
    mutationFn: ({ controlCode, input }: { controlCode: string; input: RecordActivationControlEvidenceInput }) =>
      platformApi.recordTenantActivationEvidence(tenant.id, controlCode, input),
    onSuccess: () => {
      setEvidence(null);
      refresh();
      enqueueSnackbar('Activation evidence recorded', { variant: 'success' });
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Evidence was refused'), { variant: 'error' }),
  });

  if (decisionQuery.isLoading) return <LoadingState label="Evaluating the authoritative activation policy…" />;
  if (decisionQuery.isError || !decisionQuery.data) {
    return <ErrorState message={platformErrorMessage(
      decisionQuery.error,
      'The activation policy could not be evaluated. Activation and evidence actions are unavailable.',
    )} onRetry={() => decisionQuery.refetch()} />;
  }

  const decision = decisionQuery.data;
  const evidenceValid = Boolean(evidence && isActivationEvidenceValid(evidence));
  const canActivate = tenant.status === 'provisioning' && decision.ready;

  const submitEvidence = () => {
    if (!evidence || !evidenceValid) return;
    evidenceMutation.mutate({
      controlCode: evidence.controlCode,
      input: {
        disposition: evidence.disposition,
        evidenceReference: evidence.evidenceReference.trim(),
        evidenceSha256: evidence.evidenceSha256.trim().toLowerCase(),
        effectiveFromUtc: toUtc(evidence.effectiveFrom)!,
        effectiveToUtc: toUtc(evidence.effectiveTo),
        reason: evidence.reason.trim(),
      },
    });
  };

  return <>
    <Paper sx={{ p: 3, borderRadius: 3 }}>
      <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={2}>
        <Box>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>Authoritative tenant activation</Typography>
          <Typography variant="body2" color="text.secondary">
            Server-evaluated commercial, access, data, security and provisioning controls. Policy {decision.policyVersion}.
          </Typography>
        </Box>
        <Stack direction="row" spacing={1} alignItems="center">
          <SoftChip label={decision.ready ? 'Ready' : 'Blocked'} tone={decision.ready ? 'success' : 'error'} dot={false} />
          <Button variant="contained" disabled={!canActivate} onClick={() => setConfirmActivation(true)}>
            Activate tenant
          </Button>
        </Stack>
      </Stack>

      <Alert severity={decision.ready ? 'success' : 'warning'} sx={{ mt: 2 }}>
        <AlertTitle sx={{ fontWeight: 800 }}>{decision.ready ? 'All activation controls pass' : 'Activation blocked by server policy'}</AlertTitle>
        {decision.blockingControls.length === 0
          ? 'The decision is ready. The separate Owner action is still required.'
          : <Box component="ul" sx={{ m: 0, pl: 2.5 }}>{decision.blockingControls.map((code) => <li key={code}>{code}</li>)}</Box>}
      </Alert>
      {tenant.status !== 'provisioning' && (
        <Alert severity="info" sx={{ mt: 2 }}>Only a tenant in Provisioning can be activated. Current status: {tenant.status}.</Alert>
      )}
      {decision.warnings.map((warning) => <Alert severity="warning" sx={{ mt: 1 }} key={warning}>{warning}</Alert>)}

      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ mt: 2 }}>
        <SoftChip label={`Commercial ${decision.commercialState}`} tone="neutral" dot={false} />
        <SoftChip label={`Access ${decision.accessState}`} tone="neutral" dot={false} />
        <SoftChip label={`Data ${decision.dataState}`} tone="neutral" dot={false} />
        <SoftChip label={`Legal hold ${decision.legalHoldState}`} tone="neutral" dot={false} />
      </Stack>

      <Stack spacing={1.25} sx={{ mt: 2 }}>
        {decision.controls.map((control) => <Paper key={control.code} variant="outlined" sx={{ p: 2 }}>
          <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={1.5}>
            <Box>
              <Stack direction="row" spacing={1} alignItems="center">
                <Typography sx={{ fontWeight: 800 }}>{control.code}</Typography>
                <SoftChip label={control.satisfied ? 'Pass' : 'Block'} tone={control.satisfied ? 'success' : 'error'} />
              </Stack>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>{control.detail}</Typography>
              {control.evidenceReferences.length > 0 && (
                <Typography variant="caption" sx={{ overflowWrap: 'anywhere' }}>
                  Evidence: {control.evidenceReferences.join(' · ')}
                </Typography>
              )}
            </Box>
            {EVIDENCE_CONTROLS.has(control.code) && (
              <Button variant="outlined" onClick={() => setEvidence(newEvidence(control.code))}>Record evidence</Button>
            )}
          </Stack>
        </Paper>)}
      </Stack>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 2 }}>
        Evaluated {fmtDateTime(decision.evaluatedAtUtc)}. A visible pass is not an activation; only the server transition changes tenant state.
      </Typography>
    </Paper>

    <Dialog open={confirmActivation} onClose={() => setConfirmActivation(false)} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>Activate {tenant.name}?</DialogTitle>
      <DialogContent dividers>
        <Alert severity="warning">This enables tenant access. The server will re-evaluate every control inside the activation transaction.</Alert>
      </DialogContent>
      <DialogActions>
        <Button color="inherit" onClick={() => setConfirmActivation(false)}>Cancel</Button>
        <Button variant="contained" disabled={!canActivate || activateMutation.isPending} onClick={() => activateMutation.mutate()}>
          Activate under policy
        </Button>
      </DialogActions>
    </Dialog>

    <Dialog open={Boolean(evidence)} onClose={() => setEvidence(null)} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>Record activation control evidence</DialogTitle>
      <DialogContent dividers>{evidence && <Stack spacing={2}>
        <Alert severity="warning">Record a real, externally retained evidence artifact. This form does not verify the artifact or the underlying control.</Alert>
        <TextField label="Control" value={evidence.controlCode} disabled />
        <FormControl fullWidth>
          <InputLabel id="activation-evidence-disposition">Disposition</InputLabel>
          <Select labelId="activation-evidence-disposition" label="Disposition" value={evidence.disposition}
            onChange={(event) => setEvidence({ ...evidence, disposition: event.target.value as 'approved' | 'deferred' })}>
            <MenuItem value="approved">Approved</MenuItem>
            {evidence.controlCode === 'integrations.mandatory' && <MenuItem value="deferred">Deferred</MenuItem>}
          </Select>
        </FormControl>
        <TextField required label="Evidence URL" value={evidence.evidenceReference}
          error={Boolean(evidence.evidenceReference && !isAbsoluteHttpUrl(evidence.evidenceReference))}
          helperText="Absolute HTTP(S) reference to the governed evidence artifact."
          onChange={(event) => setEvidence({ ...evidence, evidenceReference: event.target.value })} />
        <TextField required label="Evidence SHA-256" value={evidence.evidenceSha256}
          error={Boolean(evidence.evidenceSha256 && !isSha256(evidence.evidenceSha256))}
          helperText="Exactly 64 hexadecimal characters."
          onChange={(event) => setEvidence({ ...evidence, evidenceSha256: event.target.value.trim() })} />
        <TextField required type="datetime-local" label="Effective from" value={evidence.effectiveFrom}
          slotProps={{ inputLabel: { shrink: true } }} onChange={(event) => setEvidence({ ...evidence, effectiveFrom: event.target.value })} />
        <TextField type="datetime-local" label="Effective until (optional)" value={evidence.effectiveTo}
          slotProps={{ inputLabel: { shrink: true } }} onChange={(event) => setEvidence({ ...evidence, effectiveTo: event.target.value })} />
        <TextField required multiline minRows={2} label="Approval reason" value={evidence.reason}
          onChange={(event) => setEvidence({ ...evidence, reason: event.target.value })} />
      </Stack>}</DialogContent>
      <DialogActions>
        <Button color="inherit" onClick={() => setEvidence(null)}>Cancel</Button>
        <Button variant="contained" disabled={!evidenceValid || evidenceMutation.isPending} onClick={submitEvidence}>Record immutable evidence</Button>
      </DialogActions>
    </Dialog>
  </>;
}
