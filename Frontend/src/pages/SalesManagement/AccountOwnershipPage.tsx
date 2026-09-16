import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Button, Table, TableBody, TableCell, TableHead, TableRow, TextField } from '@mui/material';
import { ManageAccounts as ManageAccountsIcon, OpenInNew as OpenInNewIcon } from '@mui/icons-material';
import { useNavigate } from 'react-router-dom';
import commercialIntelligenceService, { type AccountOwnershipDTO } from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import AccountOwnerDialog from './AccountOwnerDialog';
import { PageShell, PipelineGroups, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';

export default function AccountOwnershipPage() {
  const [search, setSearch] = useState('');
  const [target, setTarget] = useState<AccountOwnershipDTO | null>(null);
  const { userData, hasPermission } = useAuth();
  const canAssign = (userData.isManager === true || userData.isSuperAdmin === true) && hasPermission('Customers', 'edit');
  const navigate = useNavigate();
  const query = useQuery({ queryKey: ['commercial-intelligence', 'account-ownership', search], queryFn: () => commercialIntelligenceService.getAccountOwnership({ search: search || undefined }) });
  const rows = query.data ?? [];
  return (
    <PageShell title="Account ownership" subtitle="Customer continuity and accountable commercial ownership." actions={<TextField size="small" label="Search accounts" value={search} onChange={event => setSearch(event.target.value)} />}>
      <QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No customer accounts match this view.">
        <ResponsiveTable label="Account ownership">
          <Table size="small">
            <TableHead><TableRow><TableCell>Account</TableCell><TableCell>Owner</TableCell><TableCell align="right">Open leads</TableCell><TableCell align="right">Open quotes</TableCell><TableCell align="right">Pipeline</TableCell><TableCell>Last activity</TableCell>{canAssign && <TableCell>Action</TableCell>}</TableRow></TableHead>
            <TableBody>{rows.map(row => <TableRow hover key={row.customerId}><TableCell><Button color="inherit" endIcon={<OpenInNewIcon />} onClick={() => navigate(`/customers/${row.customerId}`)}>{row.customerName}</Button></TableCell><TableCell>{row.ownerName || 'Unassigned'}</TableCell><TableCell align="right">{row.openLeads}</TableCell><TableCell align="right">{row.openQuotes}</TableCell><TableCell align="right"><PipelineGroups groups={row.pipelineGroups} weighted={false} /></TableCell><TableCell>{formatDateTime(row.lastActivityAt)}</TableCell>{canAssign && <TableCell><Button size="small" startIcon={<ManageAccountsIcon />} onClick={() => setTarget(row)}>{row.ownerUserId ? 'Reassign' : 'Assign'}</Button></TableCell>}</TableRow>)}</TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>
      {/* Keyed on the customer so each account opens a clean form. The same dialog serves the customer page. */}
      <AccountOwnerDialog key={target?.customerId ?? 'closed'} target={target} onClose={() => setTarget(null)} />
    </PageShell>
  );
}
