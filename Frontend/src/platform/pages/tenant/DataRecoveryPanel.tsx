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
  RecordTenantDataRecoveryEvidenceInput, Tenant, TenantDataAsset,
  TenantDataRecoveryEvidenceType, TenantDeletionCertificate,
} from '../../types';
import { isOpaqueReference, isRecoveryEvidenceValid, isSha256 } from './dataGovernanceValidation';

const EVIDENCE_TYPES: TenantDataRecoveryEvidenceType[] = [
  'BackupSetObserved', 'RestoreDrillCompleted', 'TombstoneReapplied',
  'BackupDestructionConfirmed', 'SubprocessorDeletionRequested',
  'SubprocessorDeletionConfirmed', 'ResidencyVerified',
];
const localDate = (date = new Date()) => {
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
};
const utc = (value: string) => value ? new Date(value).toISOString() : null;

interface RecoveryDraft {
  assetId: string;
  scopeKey: string;
  evidenceType: TenantDataRecoveryEvidenceType;
  providerReference: string;
  backupSetReference: string;
  recoveryPoint: string;
  operationStarted: string;
  completed: string;
  configuredRpoSeconds: string;
  configuredRtoSeconds: string;
  retainUntil: string;
  customerRowsObserved: string;
  evidenceReference: string;
  evidenceSha256: string;
  correlationId: string;
  idempotencyKey: string;
  reason: string;
}

const newRecoveryDraft = (asset: TenantDataAsset): RecoveryDraft => {
  const nonce = crypto.randomUUID();
  return {
    assetId: asset.id,
    scopeKey: asset.logicalKey,
    evidenceType: 'BackupSetObserved',
    providerReference: asset.opaqueProviderReference,
    backupSetReference: '',
    recoveryPoint: '',
    operationStarted: '',
    completed: localDate(),
    configuredRpoSeconds: '',
    configuredRtoSeconds: '',
    retainUntil: '',
    customerRowsObserved: '',
    evidenceReference: '',
    evidenceSha256: '',
    correlationId: `recovery-${nonce}`,
    idempotencyKey: `recovery-${nonce}`,
    reason: '',
  };
};

export default function DataRecoveryPanel({ tenant, assets }: { tenant: Tenant; assets: TenantDataAsset[] }) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [record, setRecord] = useState<RecoveryDraft | null>(null);
  const [certificateReason, setCertificateReason] = useState<string | null>(null);
  const [certificate, setCertificate] = useState<TenantDeletionCertificate | null>(null);
  const evidenceQuery = useQuery({
    queryKey: platformKeys.tenantRecoveryEvidence(tenant.id),
    queryFn: () => platformApi.listTenantRecoveryEvidence(tenant.id),
  });
  const decisionQuery = useQuery({
    queryKey: platformKeys.tenantDeletionCertification(tenant.id),
    queryFn: () => platformApi.getTenantDeletionCertificationDecision(tenant.id),
  });
  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantRecoveryEvidence(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantDeletionCertification(tenant.id) });
  };
  const onError = (fallback: string) => (error: unknown) =>
    enqueueSnackbar(platformErrorMessage(error, fallback), { variant: 'error' });
  const recordMutation = useMutation({
    mutationFn: (input: RecordTenantDataRecoveryEvidenceInput) => platformApi.recordTenantRecoveryEvidence(tenant.id, input),
    onSuccess: () => { setRecord(null); refresh(); enqueueSnackbar('Recovery evidence recorded', { variant: 'success' }); },
    onError: onError('Recovery evidence was refused'),
  });
  const certificateMutation = useMutation({
    mutationFn: (reason: string) => platformApi.certifyTenantDeletion(tenant.id, reason),
    onSuccess: (receipt) => {
      setCertificate(receipt);
      setCertificateReason(null);
      refresh();
      enqueueSnackbar('Deletion certificate issued', { variant: 'success' });
    },
    onError: onError('Deletion certification was refused'),
  });

  if (evidenceQuery.isLoading || decisionQuery.isLoading) return <LoadingState label="Reading recovery and deletion evidence…" />;
  if (evidenceQuery.isError || decisionQuery.isError || !evidenceQuery.data || !decisionQuery.data) {
    return <ErrorState message={platformErrorMessage(
      evidenceQuery.error ?? decisionQuery.error,
      'Recovery evidence could not be established. Recording and certification are unavailable.',
    )} onRetry={() => { evidenceQuery.refetch(); decisionQuery.refetch(); }} />;
  }

  const decision = decisionQuery.data;
  const needsBackup = record?.evidenceType === 'BackupSetObserved';
  const needsRestore = record?.evidenceType === 'RestoreDrillCompleted';
  const needsRows = needsRestore || record?.evidenceType === 'TombstoneReapplied';
  const recordValid = Boolean(record && isRecoveryEvidenceValid(record));

  const submitRecord = () => {
    if (!record || !recordValid) return;
    recordMutation.mutate({
      tenantDataAssetId: Number(record.assetId),
      scopeKey: record.scopeKey,
      evidenceType: record.evidenceType,
      opaqueProviderReference: record.providerReference.trim(),
      opaqueBackupSetReference: record.backupSetReference.trim() || null,
      recoveryPointUtc: utc(record.recoveryPoint),
      operationStartedUtc: utc(record.operationStarted),
      completedUtc: utc(record.completed)!,
      configuredRpoSeconds: record.configuredRpoSeconds ? Number(record.configuredRpoSeconds) : null,
      configuredRtoSeconds: record.configuredRtoSeconds ? Number(record.configuredRtoSeconds) : null,
      retainUntilUtc: utc(record.retainUntil),
      customerRowsObserved: record.customerRowsObserved ? Number(record.customerRowsObserved) : null,
      evidenceReference: record.evidenceReference.trim(),
      evidenceSha256: record.evidenceSha256.trim().toLowerCase(),
      correlationId: record.correlationId.trim(),
      idempotencyKey: record.idempotencyKey.trim(),
      reason: record.reason.trim(),
    });
  };

  return <>
    <Paper sx={{ p: 3, borderRadius: 3 }}>
      <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={2}>
        <Box>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>Recovery and non-resurrection evidence</Typography>
          <Typography variant="body2" color="text.secondary">
            Append-only provider and operator receipts. Evidence references are identifiers; this console never claims it performed a backup or restore.
          </Typography>
        </Box>
        <Button variant="outlined" disabled={assets.length === 0} onClick={() => setRecord(newRecoveryDraft(assets[0]))}>
          Record recovery evidence
        </Button>
      </Stack>
      {assets.length === 0 && <Alert severity="warning" sx={{ mt: 2 }}>Register a data boundary before attaching recovery evidence.</Alert>}
      {evidenceQuery.data.length === 0 ? (
        <Alert severity="info" sx={{ mt: 2 }}>No immutable recovery evidence has been recorded for this tenant.</Alert>
      ) : <Stack spacing={1.25} sx={{ mt: 2 }}>{evidenceQuery.data.map((item) => (
        <Paper variant="outlined" sx={{ p: 2 }} key={item.id}>
          <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={1}>
            <Box>
              <Typography sx={{ fontWeight: 800 }}>{item.evidenceType}</Typography>
              <Typography variant="body2" color="text.secondary">{item.scopeKey} · completed {fmtDateTime(item.completedUtc)}</Typography>
              <Typography variant="caption" sx={{ overflowWrap: 'anywhere' }}>Evidence {item.evidenceReference} · SHA-256 {item.evidenceSha256}</Typography>
            </Box>
            <Box sx={{ textAlign: { md: 'right' } }}>
              {item.actualRecoverySeconds != null && <Typography variant="body2">Actual recovery: {item.actualRecoverySeconds}s</Typography>}
              {item.configuredRpoSeconds != null && <Typography variant="body2">RPO / RTO: {item.configuredRpoSeconds}s / {item.configuredRtoSeconds}s</Typography>}
              <Typography variant="caption">Recorded by {item.actorEmail} · {fmtDateTime(item.recordedUtc)}</Typography>
            </Box>
          </Stack>
        </Paper>
      ))}</Stack>}
    </Paper>

    <Paper sx={{ p: 3, borderRadius: 3 }}>
      <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={2}>
        <Box>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>Deletion certification</Typography>
          <Typography variant="body2" color="text.secondary">Issued only after the tenant purge and every registered external boundary is proven resolved.</Typography>
        </Box>
        <Stack direction="row" spacing={1} alignItems="center">
          <SoftChip label={decision.ready ? 'Ready' : 'Blocked'} tone={decision.ready ? 'success' : 'error'} dot={false} />
          <Button variant="contained" color="error" disabled={!decision.ready} onClick={() => setCertificateReason('')}>
            Issue certificate
          </Button>
        </Stack>
      </Stack>
      <Alert severity={decision.ready ? 'success' : 'warning'} sx={{ mt: 2 }}>
        <AlertTitle sx={{ fontWeight: 800 }}>{decision.ready ? 'Deletion evidence is complete' : 'Deletion certification blocked'}</AlertTitle>
        {decision.blockers.length === 0
          ? `Evidence manifest contains ${decision.evidenceIds.length} immutable receipt(s).`
          : <Box component="ul" sx={{ m: 0, pl: 2.5 }}>{decision.blockers.map((blocker) => <li key={blocker}>{blocker}</li>)}</Box>}
      </Alert>
      <Alert severity="info" sx={{ mt: 2 }}><AlertTitle sx={{ fontWeight: 800 }}>Certification boundary</AlertTitle>{decision.boundary}</Alert>
      {certificate && <Alert severity="success" sx={{ mt: 2 }}>
        Certificate {certificate.id} issued {fmtDateTime(certificate.certifiedUtc)}. Manifest SHA-256: <code>{certificate.evidenceManifestSha256}</code>
      </Alert>}
    </Paper>

    <Dialog open={Boolean(record)} onClose={() => setRecord(null)} fullWidth maxWidth="md">
      <DialogTitle sx={{ fontWeight: 800 }}>Record recovery evidence</DialogTitle>
      <DialogContent dividers>{record && <Stack spacing={2}>
        <Alert severity="warning">Submit only after independently verifying the real provider or recovery operation. This creates an immutable receipt; it does not execute the operation.</Alert>
        <FormControl fullWidth><InputLabel id="recovery-asset">Data boundary</InputLabel>
          <Select labelId="recovery-asset" label="Data boundary" value={record.assetId} onChange={(event) => {
            const asset = assets.find((candidate) => candidate.id === event.target.value);
            if (asset) setRecord({ ...record, assetId: asset.id, scopeKey: asset.logicalKey, providerReference: asset.opaqueProviderReference });
          }}>{assets.map((asset) => <MenuItem key={asset.id} value={asset.id}>{asset.logicalKey} · {asset.assetType}</MenuItem>)}</Select>
        </FormControl>
        <FormControl fullWidth><InputLabel id="recovery-type">Evidence type</InputLabel>
          <Select labelId="recovery-type" label="Evidence type" value={record.evidenceType} onChange={(event) => {
            const evidenceType = event.target.value as TenantDataRecoveryEvidenceType;
            setRecord({ ...record, evidenceType, customerRowsObserved: evidenceType === 'TombstoneReapplied' ? '0' : '' });
          }}>{EVIDENCE_TYPES.map((type) => <MenuItem key={type} value={type}>{type}</MenuItem>)}</Select>
        </FormControl>
        <TextField required label="Scope key" value={record.scopeKey} disabled />
        <TextField required label="Opaque provider reference" value={record.providerReference}
          error={Boolean(record.providerReference && !isOpaqueReference(record.providerReference))}
          onChange={(event) => setRecord({ ...record, providerReference: event.target.value })} />
        {needsBackup && <>
          <TextField required label="Opaque backup-set reference" value={record.backupSetReference}
            error={Boolean(record.backupSetReference && !isOpaqueReference(record.backupSetReference))}
            onChange={(event) => setRecord({ ...record, backupSetReference: event.target.value })} />
          <DateField required label="Recovery point" value={record.recoveryPoint} onChange={(value) => setRecord({ ...record, recoveryPoint: value })} />
          <DateField required label="Retention expiry" value={record.retainUntil} onChange={(value) => setRecord({ ...record, retainUntil: value })} />
        </>}
        {needsRestore && <>
          <DateField required label="Recovery point" value={record.recoveryPoint} onChange={(value) => setRecord({ ...record, recoveryPoint: value })} />
          <DateField required label="Operation started" value={record.operationStarted} onChange={(value) => setRecord({ ...record, operationStarted: value })} />
          <TextField required type="number" label="Configured RPO (seconds)" value={record.configuredRpoSeconds}
            slotProps={{ htmlInput: { min: 1, step: 1 } }} onChange={(event) => setRecord({ ...record, configuredRpoSeconds: event.target.value.replace(/\D/g, '') })} />
          <TextField required type="number" label="Configured RTO (seconds)" value={record.configuredRtoSeconds}
            slotProps={{ htmlInput: { min: 1, step: 1 } }} onChange={(event) => setRecord({ ...record, configuredRtoSeconds: event.target.value.replace(/\D/g, '') })} />
        </>}
        {needsRows && <TextField required type="number" label="Customer rows observed" value={record.customerRowsObserved}
          disabled={record.evidenceType === 'TombstoneReapplied'} slotProps={{ htmlInput: { min: 0, step: 1 } }}
          onChange={(event) => setRecord({ ...record, customerRowsObserved: event.target.value.replace(/\D/g, '') })} />}
        <DateField required label="Operation completed" value={record.completed} onChange={(value) => setRecord({ ...record, completed: value })} />
        <TextField required label="Opaque evidence reference" value={record.evidenceReference}
          error={Boolean(record.evidenceReference && !isOpaqueReference(record.evidenceReference))}
          onChange={(event) => setRecord({ ...record, evidenceReference: event.target.value })} />
        <TextField required label="Evidence SHA-256" value={record.evidenceSha256}
          error={Boolean(record.evidenceSha256 && !isSha256(record.evidenceSha256))}
          helperText="Exactly 64 hexadecimal characters." onChange={(event) => setRecord({ ...record, evidenceSha256: event.target.value.trim() })} />
        <TextField required label="Correlation ID" value={record.correlationId}
          error={Boolean(record.correlationId && !isOpaqueReference(record.correlationId))}
          onChange={(event) => setRecord({ ...record, correlationId: event.target.value })} />
        <TextField required label="Idempotency key" value={record.idempotencyKey}
          error={Boolean(record.idempotencyKey && !isOpaqueReference(record.idempotencyKey))}
          onChange={(event) => setRecord({ ...record, idempotencyKey: event.target.value })} />
        <TextField required multiline minRows={2} label="Evidence reason" value={record.reason}
          onChange={(event) => setRecord({ ...record, reason: event.target.value })} />
      </Stack>}</DialogContent>
      <DialogActions><Button color="inherit" onClick={() => setRecord(null)}>Cancel</Button>
        <Button variant="contained" disabled={!recordValid || recordMutation.isPending} onClick={submitRecord}>Record immutable evidence</Button>
      </DialogActions>
    </Dialog>

    <Dialog open={certificateReason !== null} onClose={() => setCertificateReason(null)} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>Issue deletion certificate</DialogTitle>
      <DialogContent dividers><Stack spacing={2}>
        <Alert severity="error">This permanently attests that every registered customer-data boundary is resolved. The server will re-evaluate all blockers.</Alert>
        <TextField required multiline minRows={3} label="Certification reason" value={certificateReason ?? ''}
          onChange={(event) => setCertificateReason(event.target.value)} />
      </Stack></DialogContent>
      <DialogActions><Button color="inherit" onClick={() => setCertificateReason(null)}>Cancel</Button>
        <Button variant="contained" color="error" disabled={!decision.ready || !certificateReason?.trim() || certificateMutation.isPending}
          onClick={() => certificateReason && certificateMutation.mutate(certificateReason.trim())}>Issue immutable certificate</Button>
      </DialogActions>
    </Dialog>
  </>;
}

function DateField({ label, value, onChange, required = false }: {
  label: string; value: string; onChange: (value: string) => void; required?: boolean;
}) {
  return <TextField required={required} type="datetime-local" label={label} value={value}
    slotProps={{ inputLabel: { shrink: true } }} onChange={(event) => onChange(event.target.value)} />;
}
