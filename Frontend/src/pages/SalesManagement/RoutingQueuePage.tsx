import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Alert, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle, FormControl, InputLabel, MenuItem, Select, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { AssignmentInd as AssignmentIcon, OpenInNew as OpenInNewIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { useNavigate, useSearchParams } from 'react-router-dom';
import commercialIntelligenceService, { type RoutingQueueItemDTO } from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { PageShell, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';

export default function RoutingQueuePage() {
  const [searchParams] = useSearchParams();
  const sourceId = Number(searchParams.get('sourceId')) || undefined;
  const [target, setTarget] = useState<RoutingQueueItemDTO | null>(null);
  const [ownerUserId, setOwnerUserId] = useState<number | ''>('');
  const [reason, setReason] = useState('');
  const mutationIntent = useRef<{ fingerprint: string; key: string } | null>(null);
  const { userData, hasPermission } = useAuth();
  const canAssign = (userData.isManager === true || userData.isSuperAdmin === true) && hasPermission('Leads', 'edit');
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const navigate = useNavigate();
  const query = useQuery({ queryKey: ['commercial-intelligence', 'routing-queue', sourceId], queryFn: () => commercialIntelligenceService.getRoutingQueue({ sourceId }), refetchInterval: 60_000 });
  const owners = useQuery({ queryKey: ['commercial-intelligence', 'routing-owner-options'], queryFn: commercialIntelligenceService.getRoutingOwnerOptions, enabled: canAssign });
  const mutation = useMutation({
    mutationFn: () => {
      const normalizedReason = reason.trim();
      const fingerprint = `${target!.sourceId}|${ownerUserId}|${target!.version}|${normalizedReason}`;
      if (mutationIntent.current?.fingerprint !== fingerprint) mutationIntent.current = { fingerprint, key: crypto.randomUUID() };
      return commercialIntelligenceService.assignRoutingItem(target!.sourceId, Number(ownerUserId), target!.version, normalizedReason || undefined, mutationIntent.current.key);
    },
    onSuccess: () => { enqueueSnackbar('Lead assigned', { variant: 'success' }); mutationIntent.current = null; setTarget(null); void client.invalidateQueries({ queryKey: ['commercial-intelligence', 'routing-queue'] }); },
    onError: (error: any) => { const conflict = error?.response?.status === 409; enqueueSnackbar(conflict ? 'This queue item changed. Refresh before trying again.' : (error?.response?.data?.error || 'Lead assignment failed'), { variant: conflict ? 'warning' : 'error' }); if (conflict) { mutationIntent.current = null; setTarget(null); void client.invalidateQueries({ queryKey: ['commercial-intelligence', 'routing-queue'] }); } },
  });
  const rows = query.data ?? [];
  const override = !!target?.recommendedOwnerUserId && target.recommendedOwnerUserId !== ownerUserId;
  const canSubmit = ownerUserId !== '' && (!override || reason.trim().length >= 5);
  const closeAssignment = () => { mutationIntent.current = null; setTarget(null); };
  const openAssignment = (row: RoutingQueueItemDTO) => { mutationIntent.current = null; setTarget(row); setOwnerUserId(row.recommendedOwnerUserId ?? ''); setReason(''); };
  return <PageShell title="Routing queue" subtitle="Unowned inquiries with measured workload and explainable assignment recommendations."><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No inquiries are waiting for assignment."><ResponsiveTable label="Lead routing queue"><Table size="small"><TableHead><TableRow><TableCell>Nexora Serial</TableCell><TableCell>Customer</TableCell><TableCell>Received / due</TableCell><TableCell>Routing evidence</TableCell><TableCell>Recommended owner</TableCell>{canAssign && <TableCell>Action</TableCell>}</TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.sourceId}><TableCell><Button color="inherit" endIcon={<OpenInNewIcon />} onClick={() => navigate(`/procurement/leads/view/${row.leadId}`)}>{row.nexoraSerial}</Button></TableCell><TableCell>{row.customerName || 'Customer unresolved'}</TableCell><TableCell><Typography variant="body2">Received {formatDateTime(row.receivedAt)}</Typography><Typography variant="caption" color={row.overdue ? 'error.main' : 'text.secondary'}>Due {formatDateTime(row.dueAt)}</Typography></TableCell><TableCell><Typography variant="body2">{row.reason}</Typography><Typography variant="caption" color="text.secondary">{row.recommendationReason} | {(row.matchConfidence * 100).toFixed(0)}% match | policy {row.policyVersion}</Typography></TableCell><TableCell><Typography variant="body2" sx={{ fontWeight: 700 }}>{row.recommendedOwnerName || 'No recommendation'}</Typography>{row.recommendedOwnerWorkloadPoints != null && <Typography variant="caption" color="text.secondary">{row.recommendedOwnerWorkloadPoints} workload points | {row.recommendedOwnerCapacityPercent}% capacity</Typography>} {!row.recommendedOwnerAvailable && row.recommendedOwnerUserId && <Chip size="small" color="warning" label="At capacity" />}</TableCell>{canAssign && <TableCell><Button size="small" startIcon={<AssignmentIcon />} disabled={mutation.isPending} onClick={() => openAssignment(row)}>Assign</Button></TableCell>}</TableRow>)}</TableBody></Table></ResponsiveTable></QueryState>
    <Dialog open={!!target} onClose={() => !mutation.isPending && closeAssignment()} fullWidth maxWidth="sm"><DialogTitle>Assign {target?.nexoraSerial}</DialogTitle><DialogContent><Stack spacing={2} sx={{ pt: 1 }}>
      {target?.recommendedOwnerUserId && <Alert severity="info">Recommendation: {target.recommendedOwnerName}. Policy {target.policyVersion}, measured {formatDateTime(target.recommendationMeasuredAt)}.</Alert>}
      <FormControl fullWidth><InputLabel id="routing-owner-label">Owner</InputLabel><Select labelId="routing-owner-label" label="Owner" value={ownerUserId} onChange={event => setOwnerUserId(Number(event.target.value))}>{(owners.data ?? []).map(owner => <MenuItem key={owner.userId} value={owner.userId} disabled={!owner.isAvailable}>{owner.name} - {owner.workload.workloadPoints} points, {owner.capacityPercent}% capacity{owner.isAvailable ? '' : ' (at capacity)'}</MenuItem>)}</Select></FormControl>
      {override && <TextField label="Override reason" value={reason} onChange={event => setReason(event.target.value)} required error={reason.length > 0 && reason.trim().length < 5} helperText="Required when choosing someone other than the recommended owner; minimum 5 characters." multiline minRows={2} slotProps={{ htmlInput: { maxLength: 500 } }} />}
    </Stack></DialogContent><DialogActions><Button onClick={closeAssignment} disabled={mutation.isPending}>Cancel</Button><Button variant="contained" startIcon={<AssignmentIcon />} disabled={!canSubmit || mutation.isPending || owners.isLoading} onClick={() => mutation.mutate()}>{override ? 'Confirm override' : 'Assign owner'}</Button></DialogActions></Dialog>
  </PageShell>;
}
