import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, AlertTitle, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, Paper, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import { ErrorState, LoadingState } from '../../components/States';
import { SoftChip } from '../../components/StatusChip';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import type { Tenant } from '../../types';
import type { DocumentCoveragePolicy, DocumentCoverageSegment, UsageRatingCorrection } from '../../types';
import { usePlatformPermissions } from '../../auth/usePlatformPermissions';
import { usePlatformAuth } from '../../auth/usePlatformAuth';
import { fmtDateTime } from '../../components/format';

const certificationTone = (value: string) =>
  value === 'BillingCertified' ? 'success' as const : value === 'Blocked' ? 'error' as const : 'warning' as const;

export default function BillingGovernanceSection({ tenant, period }: { tenant: Tenant; period: string }) {
  const queryClient = useQueryClient();
  const permissions = usePlatformPermissions();
  const { platformUser } = usePlatformAuth();
  const { enqueueSnackbar } = useSnackbar();
  const [correction, setCorrection] = useState({ open: false, usageEventId: '', reason: '', idempotencyKey: '' });
  const [proposal, setProposal] = useState({ open: false, cutover: '', reason: '' });
  const [approval, setApproval] = useState({ open: false, reason: '' });
  const [lastCorrection, setLastCorrection] = useState<UsageRatingCorrection | null>(null);
  const [lastProposal, setLastProposal] = useState<DocumentCoveragePolicy | null>(null);
  const [lastSegments, setLastSegments] = useState<DocumentCoverageSegment[] | null>(null);
  const periodValid = /^\d{4}-(0[1-9]|1[0-2])$/.test(period);
  const catalogQuery = useQuery({
    queryKey: platformKeys.billingMeterCatalog(),
    queryFn: () => platformApi.listBillingMeterCatalog(),
  });
  const readinessQuery = useQuery({
    queryKey: platformKeys.billingReadiness(tenant.id, period),
    queryFn: () => platformApi.getBillingReadiness(tenant.id, period),
    enabled: periodValid,
  });
  const refreshReadiness = () => queryClient.invalidateQueries({ queryKey: platformKeys.billingReadiness(tenant.id, period) });
  const correctionMutation = useMutation({
    mutationFn: () => platformApi.correctUsageRating(correction.usageEventId.trim(), correction.idempotencyKey, correction.reason.trim()),
    onSuccess: (rating) => {
      setLastCorrection(rating);
      setCorrection({ open: false, usageEventId: '', reason: '', idempotencyKey: '' });
      enqueueSnackbar('Governed rating correction appended', { variant: 'success' });
      refreshReadiness();
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Rating correction was refused'), { variant: 'error' }),
  });
  const proposalMutation = useMutation({
    mutationFn: () => platformApi.proposeDocumentCoverage(
      tenant.id,
      period,
      proposal.cutover ? new Date(proposal.cutover).toISOString() : null,
      proposal.reason.trim(),
    ),
    onSuccess: (policy) => {
      setLastProposal(policy);
      setProposal({ open: false, cutover: '', reason: '' });
      enqueueSnackbar('Document coverage cutover proposed', { variant: 'success' });
      refreshReadiness();
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Cutover proposal was refused'), { variant: 'error' }),
  });
  const approvalMutation = useMutation({
    mutationFn: () => platformApi.approveDocumentCoverage(tenant.id, period, approval.reason.trim()),
    onSuccess: (segments) => {
      setLastSegments(segments);
      setApproval({ open: false, reason: '' });
      enqueueSnackbar('Document coverage cutover approved', { variant: 'success' });
      refreshReadiness();
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Cutover approval was refused'), { variant: 'error' }),
  });
  const sameMaker = Boolean(lastProposal && platformUser?.email.toLowerCase() === lastProposal.proposedBy.toLowerCase());

  return (
    <Stack spacing={2.5}>
      <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Typography variant="h6" sx={{ fontWeight: 800 }}>Billing readiness · {period}</Typography>
        <Typography variant="body2" color="text.secondary">
          Frozen-source coverage, effective event ratings, reconciliation, and rate-lineage checks required before Final.
        </Typography>
        {!periodValid ? (
          <Alert severity="warning" sx={{ mt: 2 }}>Select a valid YYYY-MM period to evaluate readiness.</Alert>
        ) : readinessQuery.isLoading ? (
          <LoadingState label="Evaluating billing readiness…" minHeight={140} />
        ) : readinessQuery.isError || !readinessQuery.data ? (
          <ErrorState
            message={platformErrorMessage(readinessQuery.error, 'Billing readiness could not be established. Treat this period as blocked.')}
            onRetry={() => readinessQuery.refetch()}
            minHeight={140}
          />
        ) : (
          <Stack spacing={1.5} sx={{ mt: 2 }}>
            <Alert severity={readinessQuery.data.ready ? 'success' : 'error'}>
              <AlertTitle sx={{ fontWeight: 800 }}>{readinessQuery.data.ready ? 'Ready to finalize' : 'Billing blocked'}</AlertTitle>
              {readinessQuery.data.ready
                ? 'No readiness failures were returned for this tenant-period.'
                : `${readinessQuery.data.failures.length} governed readiness failure${readinessQuery.data.failures.length === 1 ? '' : 's'} must be resolved.`}
            </Alert>
            {readinessQuery.data.failures.map((failure, index) => (
              <Alert severity="warning" key={`${failure.code}-${failure.meterKey}-${index}`}>
                <AlertTitle sx={{ fontWeight: 800 }}>{failure.code} · {failure.meterKey}</AlertTitle>{failure.detail}
              </Alert>
            ))}
            <Box>
              <Typography variant="caption" color="text.secondary">Manifest SHA-256</Typography>
              <Box component="code" sx={{ display: 'block', overflowWrap: 'anywhere', fontSize: 12.5, fontWeight: 700 }}>
                {readinessQuery.data.manifestSha256}
              </Box>
            </Box>
          </Stack>
        )}
      </Paper>

      <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Typography variant="h6" sx={{ fontWeight: 800 }}>Closed meter catalog</Typography>
        <Typography variant="body2" color="text.secondary">
          Server-defined event types and certification state. Only BillingCertified meters may contribute to a final invoice.
        </Typography>
        {catalogQuery.isLoading ? (
          <LoadingState label="Reading meter catalog…" minHeight={140} />
        ) : catalogQuery.isError || !catalogQuery.data ? (
          <ErrorState message={platformErrorMessage(catalogQuery.error, 'The meter catalog could not be read.')} onRetry={() => catalogQuery.refetch()} minHeight={140} />
        ) : (
          <Box sx={{ overflowX: 'auto', mt: 2 }}><Table size="small"><TableHead><TableRow>
            <TableCell sx={{ fontWeight: 700 }}>Event type</TableCell><TableCell sx={{ fontWeight: 700 }}>Billing meter</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Unit</TableCell><TableCell sx={{ fontWeight: 700 }}>Certification</TableCell>
          </TableRow></TableHead><TableBody>{catalogQuery.data.map((entry) => <TableRow key={entry.eventType} hover>
            <TableCell><code>{entry.eventType}</code></TableCell><TableCell><code>{entry.billingMeterKey}</code></TableCell>
            <TableCell>{entry.unit}</TableCell><TableCell><SoftChip label={entry.certification} tone={certificationTone(entry.certification)} dot={false} /></TableCell>
          </TableRow>)}</TableBody></Table></Box>
        )}
      </Paper>

      {permissions.isOwner && (
        <Paper sx={{ p: 3, borderRadius: 3 }}>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>Owner billing governance</Typography>
          <Typography variant="body2" color="text.secondary">
            These append or approve evidence; they never edit a prior rating or silently change authoritative coverage. MFA is enforced by the server.
          </Typography>
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} sx={{ mt: 2 }}>
            <Button variant="outlined" onClick={() => setCorrection({ open: true, usageEventId: '', reason: '', idempotencyKey: crypto.randomUUID() })}>Correct event rating</Button>
            <Button variant="outlined" disabled={!periodValid} onClick={() => setProposal({ open: true, cutover: '', reason: '' })}>Propose document cutover</Button>
            <Button variant="outlined" color="success" disabled={!periodValid || sameMaker} onClick={() => setApproval({ open: true, reason: '' })}>Approve document cutover</Button>
          </Stack>
          {sameMaker && <Alert severity="warning" sx={{ mt: 2 }}>The proposal maker cannot approve the same cutover. Another Owner with MFA must check it.</Alert>}
          {lastProposal && <Alert severity="info" sx={{ mt: 2 }}>
            Proposed {lastProposal.mode} for {lastProposal.meterKey}, effective {fmtDateTime(lastProposal.proposedEffectiveAtUtc)}, by {lastProposal.proposedBy}. Policy version {lastProposal.version}.
          </Alert>}
          {lastCorrection && <Alert severity="success" sx={{ mt: 2 }}>
            Rating attempt {lastCorrection.attemptNumber} · {lastCorrection.status} · {lastCorrection.reasonCode ?? 'no reason code'} · {lastCorrection.ratedAmount ?? '—'} {lastCorrection.currency}.
          </Alert>}
          {lastSegments && <Alert severity="success" sx={{ mt: 2 }}>
            Approval created {lastSegments.length} immutable coverage segment{lastSegments.length === 1 ? '' : 's'} for {period}.
          </Alert>}
        </Paper>
      )}

      <Dialog open={correction.open} onClose={() => setCorrection({ open: false, usageEventId: '', reason: '', idempotencyKey: '' })} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Correct usage-event rating</DialogTitle>
        <DialogContent dividers><Stack spacing={2}>
          <Alert severity="warning">Creates a new append-only rating attempt using the event-time tenant rate card. Owner + MFA required.</Alert>
          <TextField required label="Usage event ID" value={correction.usageEventId} onChange={(e) => setCorrection({ ...correction, usageEventId: e.target.value })} error={Boolean(correction.usageEventId && !/^[0-9a-fA-F-]{36}$/.test(correction.usageEventId))} />
          <TextField required multiline minRows={2} label="Correction reason" value={correction.reason} onChange={(e) => setCorrection({ ...correction, reason: e.target.value })} helperText="At least 10 characters; retained in the audit trail." />
        </Stack></DialogContent>
        <DialogActions><Button color="inherit" onClick={() => setCorrection({ open: false, usageEventId: '', reason: '', idempotencyKey: '' })}>Cancel</Button><Button variant="contained" disabled={!/^[0-9a-fA-F-]{36}$/.test(correction.usageEventId) || correction.reason.trim().length < 10 || correctionMutation.isPending} onClick={() => correctionMutation.mutate()}>Append correction</Button></DialogActions>
      </Dialog>

      <Dialog open={proposal.open} onClose={() => setProposal({ open: false, cutover: '', reason: '' })} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Propose document coverage cutover</DialogTitle>
        <DialogContent dividers><Stack spacing={2}>
          <Alert severity="info">Blank cutover uses the next period boundary. A mid-period boundary is refused when per-period allowance attribution is ambiguous.</Alert>
          <TextField type="datetime-local" label="Optional mid-period cutover" value={proposal.cutover} onChange={(e) => setProposal({ ...proposal, cutover: e.target.value })} slotProps={{ inputLabel: { shrink: true } }} />
          <TextField required multiline minRows={2} label="Proposal reason" value={proposal.reason} onChange={(e) => setProposal({ ...proposal, reason: e.target.value })} helperText="At least 10 characters." />
        </Stack></DialogContent>
        <DialogActions><Button color="inherit" onClick={() => setProposal({ open: false, cutover: '', reason: '' })}>Cancel</Button><Button variant="contained" disabled={proposal.reason.trim().length < 10 || proposalMutation.isPending} onClick={() => proposalMutation.mutate()}>Create proposal</Button></DialogActions>
      </Dialog>

      <Dialog open={approval.open} onClose={() => setApproval({ open: false, reason: '' })} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Approve document coverage cutover</DialogTitle>
        <DialogContent dividers><Stack spacing={2}>
          <Alert severity="warning">Another Owner must have proposed this cutover. Approval succeeds only when every event is rated and legacy/canonical totals reconcile.</Alert>
          <TextField required multiline minRows={2} label="Independent approval reason" value={approval.reason} onChange={(e) => setApproval({ ...approval, reason: e.target.value })} helperText="At least 10 characters." />
        </Stack></DialogContent>
        <DialogActions><Button color="inherit" onClick={() => setApproval({ open: false, reason: '' })}>Cancel</Button><Button variant="contained" color="success" disabled={approval.reason.trim().length < 10 || approvalMutation.isPending} onClick={() => approvalMutation.mutate()}>Approve cutover</Button></DialogActions>
      </Dialog>
    </Stack>
  );
}
