import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Stack, Table, TableBody, TableCell, TableHead, TableRow, Typography } from '@mui/material';
import { useNavigate } from 'react-router-dom';
import userService from '../../api/services/userService';
import operationalReadinessService from '../../api/services/operationalReadinessService';
import type { IntelligenceMetric } from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { MetricGrid, PageShell, QueryState, ResponsiveTable, StatusChip } from '../SalesManagement/CommercialPagePrimitives';

export default function TenantAdminOperationsPage() {
  const navigate = useNavigate();
  const { userData } = useAuth();
  const query = useQuery({ queryKey: ['tenant-admin-operations', userData.businessUnitId], queryFn: () => userService.getAll({ pageSize: 500 }) });
  const readiness = useQuery({
    queryKey: ['tenant-operational-readiness', userData.businessUnitId],
    queryFn: operationalReadinessService.get,
    refetchInterval: 30_000,
  });
  const rows = query.data?.items ?? [];
  const metrics: IntelligenceMetric[] = [
    { key: 'users', label: 'Tenant users', value: query.data?.totalCount ?? 0, unit: 'count' },
    { key: 'active', label: 'Active users', value: rows.filter(user => user.isActive).length, unit: 'count' },
    { key: 'inactive', label: 'Inactive users', value: rows.filter(user => !user.isActive).length, unit: 'count' },
  ];
  return <PageShell title="Tenant admin operations" subtitle="Authorized user and access work for this tenant." actions={<><Button onClick={() => navigate('/security/roles')}>Roles and permissions</Button><Button variant="contained" onClick={() => navigate('/security/users')}>Manage users</Button></>}>
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
    </Stack>
    <QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No tenant users are recorded.">
      <ResponsiveTable label="Tenant users"><Table size="small"><TableHead><TableRow><TableCell>User</TableCell><TableCell>Email</TableCell><TableCell>Role</TableCell><TableCell>Status</TableCell></TableRow></TableHead><TableBody>
        {rows.slice(0, 20).map(user => <TableRow hover key={user.id}><TableCell>{[user.firstName, user.lastName].filter(Boolean).join(' ')}</TableCell><TableCell>{user.email}</TableCell><TableCell>{user.roleName ?? 'Role unresolved'}</TableCell><TableCell>{user.isActive ? 'Active' : 'Inactive'}</TableCell></TableRow>)}
      </TableBody></Table></ResponsiveTable>
    </QueryState>
  </PageShell>;
}
