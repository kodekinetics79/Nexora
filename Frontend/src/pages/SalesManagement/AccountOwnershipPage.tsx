import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Table, TableBody, TableCell, TableHead, TableRow, TextField } from '@mui/material';
import { useSnackbar } from 'notistack';
import commercialIntelligenceService, { type AccountOwnershipDTO } from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { PageShell, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';

export default function AccountOwnershipPage() {
  const [search, setSearch] = useState('');
  const { userData, hasPermission } = useAuth();
  const canAssign = hasPermission('Customers', 'edit') && !!userData.id;
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const query = useQuery({ queryKey: ['commercial-intelligence', 'account-ownership', search], queryFn: () => commercialIntelligenceService.getAccountOwnership({ search: search || undefined }) });
  const mutation = useMutation({ mutationFn: (row: AccountOwnershipDTO) => commercialIntelligenceService.assignAccount(row.customerId, userData.id!, row.version, crypto.randomUUID()), onSuccess: () => { enqueueSnackbar('Account ownership updated', { variant: 'success' }); void client.invalidateQueries({ queryKey: ['commercial-intelligence', 'account-ownership'] }); }, onError: () => enqueueSnackbar('Account ownership could not be updated', { variant: 'error' }) });
  const rows = query.data ?? [];
  return <PageShell title="Account ownership" subtitle="Customer continuity and accountable commercial ownership." actions={<TextField size="small" label="Search accounts" value={search} onChange={event => setSearch(event.target.value)} />}><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No customer accounts match this view."><ResponsiveTable label="Account ownership"><Table size="small"><TableHead><TableRow><TableCell>Account</TableCell><TableCell>Owner</TableCell><TableCell align="right">Open leads</TableCell><TableCell align="right">Open quotes</TableCell><TableCell align="right">Pipeline</TableCell><TableCell>Last activity</TableCell>{canAssign && <TableCell>Action</TableCell>}</TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.customerId}><TableCell>{row.customerName}</TableCell><TableCell>{row.ownerName || 'Unassigned'}</TableCell><TableCell align="right">{row.openLeads}</TableCell><TableCell align="right">{row.openQuotes}</TableCell><TableCell align="right">{row.currencyCode} {row.pipelineValue.toLocaleString()}</TableCell><TableCell>{formatDateTime(row.lastActivityAt)}</TableCell>{canAssign && <TableCell><Button size="small" disabled={mutation.isPending || row.ownerUserId === userData.id} onClick={() => mutation.mutate(row)}>Assign to me</Button></TableCell>}</TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
