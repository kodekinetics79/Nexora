import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Dialog, DialogActions, DialogContent, DialogTitle, FormControl, InputLabel, MenuItem, Select, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { ManageAccounts as ManageAccountsIcon, OpenInNew as OpenInNewIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { useNavigate } from 'react-router-dom';
import commercialIntelligenceService, { type AccountOwnershipDTO } from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { PageShell, PipelineGroups, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';

export default function AccountOwnershipPage() {
  const [search, setSearch] = useState('');
  const [target, setTarget] = useState<AccountOwnershipDTO | null>(null);
  const [ownerUserId, setOwnerUserId] = useState<number | ''>('');
  const [reason, setReason] = useState('');
  const mutationIntent = useRef<{ fingerprint: string; key: string } | null>(null);
  const { userData, hasPermission } = useAuth();
  const canAssign = (userData.isManager === true || userData.isSuperAdmin === true) && hasPermission('Customers', 'edit');
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const navigate = useNavigate();
  const query = useQuery({ queryKey: ['commercial-intelligence', 'account-ownership', search], queryFn: () => commercialIntelligenceService.getAccountOwnership({ search: search || undefined }) });
  const owners = useQuery({ queryKey: ['commercial-intelligence', 'account-owner-options'], queryFn: commercialIntelligenceService.getAccountOwnerOptions, enabled: canAssign });
  const mutation = useMutation({
    mutationFn: () => {
      const normalizedReason = reason.trim();
      const fingerprint = `${target!.customerId}|${ownerUserId}|${target!.version}|${normalizedReason}`;
      if (mutationIntent.current?.fingerprint !== fingerprint) mutationIntent.current = { fingerprint, key: crypto.randomUUID() };
      return commercialIntelligenceService.assignAccount(target!.customerId, Number(ownerUserId), target!.version, normalizedReason || undefined, mutationIntent.current.key);
    },
    onSuccess: () => { enqueueSnackbar('Account ownership updated', { variant: 'success' }); mutationIntent.current = null; setTarget(null); void client.invalidateQueries({ queryKey: ['commercial-intelligence', 'account-ownership'] }); },
    onError: (error: any) => { const conflict = error?.response?.status === 409; enqueueSnackbar(conflict ? 'Ownership changed. Refresh the row before trying again.' : (error?.response?.data?.error || 'Account ownership could not be updated'), { variant: conflict ? 'warning' : 'error' }); if (conflict) { mutationIntent.current = null; setTarget(null); void client.invalidateQueries({ queryKey: ['commercial-intelligence', 'account-ownership'] }); } },
  });
  const rows = query.data ?? [];
  const reassignment = !!target?.ownerUserId && target.ownerUserId !== ownerUserId;
  const canSubmit = ownerUserId !== '' && ownerUserId !== target?.ownerUserId && (!reassignment || reason.trim().length >= 5);
  const closeAssignment = () => { mutationIntent.current = null; setTarget(null); };
  const openAssignment = (row: AccountOwnershipDTO) => { mutationIntent.current = null; setTarget(row); setOwnerUserId(row.ownerUserId ?? ''); setReason(''); };
  return (
    <PageShell title="Account ownership" subtitle="Customer continuity and accountable commercial ownership." actions={<TextField size="small" label="Search accounts" value={search} onChange={event => setSearch(event.target.value)} />}>
      <QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No customer accounts match this view.">
        <ResponsiveTable label="Account ownership">
          <Table size="small">
            <TableHead><TableRow><TableCell>Account</TableCell><TableCell>Owner</TableCell><TableCell align="right">Open leads</TableCell><TableCell align="right">Open quotes</TableCell><TableCell align="right">Pipeline</TableCell><TableCell>Last activity</TableCell>{canAssign && <TableCell>Action</TableCell>}</TableRow></TableHead>
            <TableBody>{rows.map(row => <TableRow hover key={row.customerId}><TableCell><Button color="inherit" endIcon={<OpenInNewIcon />} onClick={() => navigate(`/customers/${row.customerId}`)}>{row.customerName}</Button></TableCell><TableCell>{row.ownerName || 'Unassigned'}</TableCell><TableCell align="right">{row.openLeads}</TableCell><TableCell align="right">{row.openQuotes}</TableCell><TableCell align="right"><PipelineGroups groups={row.pipelineGroups} weighted={false} /></TableCell><TableCell>{formatDateTime(row.lastActivityAt)}</TableCell>{canAssign && <TableCell><Button size="small" startIcon={<ManageAccountsIcon />} onClick={() => openAssignment(row)}>{row.ownerUserId ? 'Reassign' : 'Assign'}</Button></TableCell>}</TableRow>)}</TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>
      <Dialog open={!!target} onClose={() => !mutation.isPending && closeAssignment()} fullWidth maxWidth="sm">
        <DialogTitle>{target?.ownerUserId ? 'Reassign account owner' : 'Assign account owner'}</DialogTitle>
        <DialogContent><Stack spacing={2} sx={{ pt: 1 }}>
          <Typography variant="body2" color="text.secondary">{target?.customerName}</Typography>
          <FormControl fullWidth><InputLabel id="account-owner-label">Owner</InputLabel><Select labelId="account-owner-label" label="Owner" value={ownerUserId} onChange={event => setOwnerUserId(Number(event.target.value))}>
            {(owners.data ?? []).map(owner => <MenuItem key={owner.userId} value={owner.userId} disabled={!owner.isAvailable}>{owner.name} - {owner.workload.workloadPoints} points, {owner.capacityPercent}% capacity{owner.isAvailable ? '' : ' (at capacity)'}</MenuItem>)}
          </Select></FormControl>
          {reassignment && <TextField label="Reassignment reason" value={reason} onChange={event => setReason(event.target.value)} required error={reason.length > 0 && reason.trim().length < 5} helperText="Required for an ownership change; minimum 5 characters." multiline minRows={2} slotProps={{ htmlInput: { maxLength: 500 } }} />}
        </Stack></DialogContent>
        <DialogActions><Button onClick={closeAssignment} disabled={mutation.isPending}>Cancel</Button><Button variant="contained" startIcon={<ManageAccountsIcon />} disabled={!canSubmit || mutation.isPending || owners.isLoading} onClick={() => mutation.mutate()}>Confirm owner</Button></DialogActions>
      </Dialog>
    </PageShell>
  );
}
