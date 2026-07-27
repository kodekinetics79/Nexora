import { useQuery } from '@tanstack/react-query';
import { Button, Table, TableBody, TableCell, TableHead, TableRow } from '@mui/material';
import { useNavigate } from 'react-router-dom';
import userService from '../../api/services/userService';
import type { IntelligenceMetric } from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { MetricGrid, PageShell, QueryState, ResponsiveTable } from '../SalesManagement/CommercialPagePrimitives';

export default function TenantAdminOperationsPage() {
  const navigate = useNavigate();
  const { userData } = useAuth();
  const query = useQuery({ queryKey: ['tenant-admin-operations', userData.businessUnitId], queryFn: () => userService.getAll({ pageSize: 500 }) });
  const rows = query.data?.items ?? [];
  const metrics: IntelligenceMetric[] = [
    { key: 'users', label: 'Tenant users', value: query.data?.totalCount ?? 0, unit: 'count' },
    { key: 'active', label: 'Active users', value: rows.filter(user => user.isActive).length, unit: 'count' },
    { key: 'inactive', label: 'Inactive users', value: rows.filter(user => !user.isActive).length, unit: 'count' },
  ];
  return <PageShell title="Tenant admin operations" subtitle="Authorized user and access work for this tenant." actions={<><Button onClick={() => navigate('/security/roles')}>Roles and permissions</Button><Button variant="contained" onClick={() => navigate('/security/users')}>Manage users</Button></>}>
    <MetricGrid metrics={metrics} />
    <QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No tenant users are recorded.">
      <ResponsiveTable label="Tenant users"><Table size="small"><TableHead><TableRow><TableCell>User</TableCell><TableCell>Email</TableCell><TableCell>Role</TableCell><TableCell>Status</TableCell></TableRow></TableHead><TableBody>
        {rows.slice(0, 20).map(user => <TableRow hover key={user.id}><TableCell>{[user.firstName, user.lastName].filter(Boolean).join(' ')}</TableCell><TableCell>{user.email}</TableCell><TableCell>{user.roleName ?? 'Role unresolved'}</TableCell><TableCell>{user.isActive ? 'Active' : 'Inactive'}</TableCell></TableRow>)}
      </TableBody></Table></ResponsiveTable>
    </QueryState>
  </PageShell>;
}
