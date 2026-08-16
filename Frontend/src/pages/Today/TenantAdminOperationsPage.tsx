import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import {
  Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack,
  Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography,
} from '@mui/material';
import { useNavigate } from 'react-router-dom';
import userService from '../../api/services/userService';
import operationalReadinessService from '../../api/services/operationalReadinessService';
import type { DeadLetterRecoveryResult } from '../../api/services/operationalReadinessService';
import type { IntelligenceMetric } from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { MetricGrid, PageShell, QueryState, ResponsiveTable, StatusChip } from '../SalesManagement/CommercialPagePrimitives';

type RecoveryDialog = { jobId: number; fileName: string; idempotencyKey: string };

export default function TenantAdminOperationsPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { userData, hasPermission } = useAuth();
  const canRecoverExtraction = hasPermission('Users', 'edit') && hasPermission('Leads', 'create');
  const [recovery, setRecovery] = useState<RecoveryDialog | null>(null);
  const [recoveryReason, setRecoveryReason] = useState('');
  const [recoveryResult, setRecoveryResult] = useState<DeadLetterRecoveryResult | null>(null);
  const users = useQuery({
    queryKey: ['tenant-admin-operations', userData.businessUnitId],
    queryFn: () => userService.getAll({ pageSize: 500 }),
  });
  const readiness = useQuery({
    queryKey: ['tenant-operational-readiness', userData.businessUnitId],
    queryFn: operationalReadinessService.get,
    refetchInterval: 30_000,
  });
  const deadLetters = useQuery({
    queryKey: ['extraction-dead-letters', userData.businessUnitId],
    queryFn: operationalReadinessService.getExtractionDeadLetters,
  });
  const recover = useMutation({
    mutationFn: () => operationalReadinessService.recoverExtractionDeadLetter(
      recovery!.jobId,
      recoveryReason,
      recovery!.idempotencyKey,
    ),
    onSuccess: async result => {
      setRecoveryResult(result);
      setRecovery(null);
      setRecoveryReason('');
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['extraction-dead-letters', userData.businessUnitId] }),
        queryClient.invalidateQueries({ queryKey: ['tenant-operational-readiness', userData.businessUnitId] }),
      ]);
    },
  });
  const rows = users.data?.items ?? [];
  const metrics: IntelligenceMetric[] = [
    { key: 'users', label: 'Tenant users', value: users.data?.totalCount ?? 0, unit: 'count' },
    { key: 'active', label: 'Active users', value: rows.filter(user => user.isActive).length, unit: 'count' },
    { key: 'inactive', label: 'Inactive users', value: rows.filter(user => !user.isActive).length, unit: 'count' },
  ];

  const openRecovery = (jobId: number, fileName: string) => {
    if (!canRecoverExtraction) return;
    setRecoveryResult(null);
    setRecoveryReason('');
    setRecovery({ jobId, fileName, idempotencyKey: crypto.randomUUID() });
  };

  return <PageShell
    title="Tenant admin operations"
    subtitle="Authorized user and access work for this tenant."
    actions={<><Button onClick={() => navigate('/security/roles')}>Roles and permissions</Button><Button variant="contained" onClick={() => navigate('/security/users')}>Manage users</Button></>}
  >
    <MetricGrid metrics={metrics} />
    <Stack spacing={1.5} sx={{ mb: 2.5 }}>
      <Typography variant="h6" sx={{ fontWeight: 800 }}>Production readiness</Typography>
      <QueryState loading={readiness.isLoading} error={readiness.isError} empty={!readiness.data} onRetry={() => void readiness.refetch()} emptyText="No readiness evidence is available.">
        {readiness.data && <>
          <Alert severity={readiness.data.deploymentReadiness === 'Healthy' ? 'success' : 'warning'}>
            Runtime readiness is {readiness.data.deploymentReadiness}. Last checked {new Date(readiness.data.checkedAt).toLocaleString()}.
            {readiness.data.blockingReasons.map(reason => <Typography component="div" variant="body2" key={reason}>{reason}</Typography>)}
          </Alert>
          <ResponsiveTable label="Runtime health checks"><Table size="small"><TableHead><TableRow><TableCell>Dependency</TableCell><TableCell>Status</TableCell><TableCell align="right">Duration</TableCell></TableRow></TableHead><TableBody>
            {readiness.data.healthChecks.map(check => <TableRow hover key={check.name}><TableCell>{check.name}</TableCell><TableCell><StatusChip value={check.status} /></TableCell><TableCell align="right">{check.durationMilliseconds.toLocaleString()} ms</TableCell></TableRow>)}
          </TableBody></Table></ResponsiveTable>
          <ResponsiveTable label="Tenant queue status"><Table size="small"><TableHead><TableRow><TableCell>Workflow</TableCell><TableCell align="right">Pending</TableCell><TableCell align="right">In flight</TableCell><TableCell align="right">Dead letter</TableCell></TableRow></TableHead><TableBody>
            {readiness.data.queues.map(queue => <TableRow hover key={queue.key}><TableCell>{queue.label}</TableCell><TableCell align="right">{queue.pending.toLocaleString()}</TableCell><TableCell align="right">{queue.inFlight.toLocaleString()}</TableCell><TableCell align="right">{queue.deadLetter.toLocaleString()}</TableCell></TableRow>)}
          </TableBody></Table></ResponsiveTable>
          <Alert severity={readiness.data.aiLast30Days.externalSharePercent > 10 ? 'error' : 'info'}>
            AI processing, last 30 days: {readiness.data.aiLast30Days.local.toLocaleString()} local, {readiness.data.aiLast30Days.external.toLocaleString()} external ({readiness.data.aiLast30Days.externalSharePercent.toLocaleString()}%), {readiness.data.aiLast30Days.unresolved.toLocaleString()} unresolved.
          </Alert>
        </>}
      </QueryState>
      {recoveryResult && <Alert severity={recoveryResult.blocksReadiness ? 'error' : recoveryResult.status === 'RetryQueued' ? 'success' : 'info'} onClose={() => setRecoveryResult(null)}>
        {recoveryResult.blocksReadiness
          ? `Verification result: ${recoveryResult.status}. This exception remains blocked.`
          : `Dead-letter verification completed: ${recoveryResult.status}.`}
      </Alert>}
      <Typography variant="h6" sx={{ fontWeight: 800 }}>Lead extraction exceptions</Typography>
      <QueryState loading={deadLetters.isLoading} error={deadLetters.isError} empty={!deadLetters.data?.length} onRetry={() => void deadLetters.refetch()} emptyText="No lead extraction exceptions require review.">
        {/* Fixed layout with explicit widths: the remedy sentence in Failure is the only
            long cell, and without a declared width it squeezed itself into a narrow column
            and stretched every row to ~15 lines while Attempts and Disposition sat nearly
            empty. Percentages keep the columns aligned at any viewport; minWidth hands the
            surrounding ResponsiveTable a horizontal scroll instead of crushing the text. */}
        <ResponsiveTable label="Lead extraction exceptions"><Table size="small" sx={{ tableLayout: 'fixed', minWidth: 900 }}><TableHead><TableRow>
          <TableCell sx={{ width: '22%' }}>Document</TableCell>
          <TableCell sx={{ width: '32%' }}>Failure</TableCell>
          <TableCell sx={{ width: '9%', whiteSpace: 'nowrap' }}>Attempts</TableCell>
          <TableCell sx={{ width: '11%' }}>Disposition</TableCell>
          <TableCell sx={{ width: '13%' }}>Last updated</TableCell>
          <TableCell align="right" sx={{ width: '13%' }}>Actions</TableCell>
        </TableRow></TableHead><TableBody>
          {deadLetters.data?.map(item => <TableRow hover key={item.jobId} sx={{ '& > td': { verticalAlign: 'top' } }}>
            <TableCell sx={{ overflowWrap: 'anywhere' }}>{item.fileName}</TableCell>
            <TableCell><Stack spacing={0.25}><Typography sx={{ fontSize: '0.85rem', fontWeight: 700 }}>{item.failureCategory.replaceAll('_', ' ')}</Typography>{item.operatorAction && <Typography sx={{ fontSize: '0.75rem', color: 'text.secondary' }}>{item.operatorAction}</Typography>}</Stack></TableCell>
            <TableCell sx={{ whiteSpace: 'nowrap' }}>{item.attempts} / {item.maxAttempts}</TableCell>
            <TableCell><StatusChip value={item.resolution} /></TableCell>
            <TableCell>{new Date(item.updatedOn).toLocaleString()}</TableCell>
            <TableCell align="right"><Stack spacing={1} sx={{ alignItems: 'flex-end' }}><Button size="small" onClick={() => navigate(`/procurement/leads/ingestion/${encodeURIComponent(item.batchId)}`)}>Open batch</Button>{canRecoverExtraction && <Button size="small" variant="contained" sx={{ whiteSpace: 'nowrap' }} onClick={() => openRecovery(item.jobId, item.fileName)}>Verify and retry</Button>}</Stack></TableCell>
          </TableRow>)}
        </TableBody></Table></ResponsiveTable>
      </QueryState>
    </Stack>
    <QueryState loading={users.isLoading} error={users.isError} empty={!rows.length} onRetry={() => void users.refetch()} emptyText="No tenant users are recorded.">
      <ResponsiveTable label="Tenant users"><Table size="small"><TableHead><TableRow><TableCell>User</TableCell><TableCell>Email</TableCell><TableCell>Role</TableCell><TableCell>Status</TableCell></TableRow></TableHead><TableBody>
        {rows.slice(0, 20).map(user => <TableRow hover key={user.id}><TableCell>{[user.firstName, user.lastName].filter(Boolean).join(' ')}</TableCell><TableCell>{user.email}</TableCell><TableCell>{user.roleName ?? 'Role unresolved'}</TableCell><TableCell>{user.isActive ? 'Active' : 'Inactive'}</TableCell></TableRow>)}
      </TableBody></Table></ResponsiveTable>
    </QueryState>
    <Dialog open={Boolean(recovery) && canRecoverExtraction} onClose={() => !recover.isPending && setRecovery(null)} fullWidth maxWidth="sm">
      <DialogTitle>Verify source and retry extraction</DialogTitle>
      <DialogContent><Stack spacing={2} sx={{ pt: 1 }}>
        <Typography variant="body2">{recovery?.fileName}</Typography>
        <TextField autoFocus required multiline minRows={3} label="Recovery reason" value={recoveryReason} onChange={event => setRecoveryReason(event.target.value)} slotProps={{ htmlInput: { maxLength: 1000 } }} />
        {recover.isError && <Alert severity="error">The source could not be verified. Refresh the queue status and try again.</Alert>}
      </Stack></DialogContent>
      <DialogActions><Button onClick={() => setRecovery(null)} disabled={recover.isPending}>Cancel</Button><Button variant="contained" disabled={recover.isPending || !recoveryReason.trim()} onClick={() => recover.mutate()}>{recover.isPending ? 'Verifying...' : 'Verify and retry'}</Button></DialogActions>
    </Dialog>
  </PageShell>;
}
