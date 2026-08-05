import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Alert, Box, Button, Checkbox, Chip, CircularProgress, Dialog, DialogActions,
  DialogContent, DialogTitle, FormControl, InputLabel, MenuItem, Paper, Select,
  Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow,
  TextField, Typography,
} from '@mui/material';
import {
  CheckCircleOutlined, ErrorOutlined, FactCheck, Launch, PriorityHigh, Visibility,
} from '@mui/icons-material';
import {
  platformGovernanceService, type HumanActionItem, type HumanActionStatus,
} from '../../api/services/platformGovernanceService';

const active = (item: HumanActionItem) => !['Completed', 'Rejected'].includes(item.status);
const statusColor = (status: HumanActionStatus): 'default' | 'info' | 'warning' | 'success' | 'error' => ({
  Open: 'warning', InReview: 'info', Escalated: 'error', Completed: 'success', Rejected: 'default',
}[status] as 'default' | 'info' | 'warning' | 'success' | 'error');
const priorityColor = (priority: string): 'default' | 'warning' | 'error' =>
  priority === 'Critical' ? 'error' : priority === 'High' ? 'warning' : 'default';
const sourceRoute = (item: HumanActionItem) => {
  const source = item.sourceType.toLowerCase();
  const reference = encodeURIComponent(item.sourceReference);
  if (source.includes('lead')) return `/procurement/leads/all?search=${reference}`;
  if (source.includes('quote')) return '/sales/quotes';
  if (source.includes('supplier')) return '/suppliers';
  if (source.includes('inventory')) return '/inventory/availability';
  if (source.includes('clientpo')) return '/sales/client-pos';
  return null;
};

export default function HumanActionCenterPage() {
  const navigate = useNavigate();
  const client = useQueryClient();
  const [status, setStatus] = useState<HumanActionStatus | ''>('');
  const [selectedIds, setSelectedIds] = useState<number[]>([]);
  const [detailId, setDetailId] = useState<number | null>(null);
  const [decisionOpen, setDecisionOpen] = useState(false);
  const [targetStatus, setTargetStatus] = useState<HumanActionStatus>('Completed');
  const [comment, setComment] = useState('');

  const list = useQuery({
    queryKey: ['human-actions', status],
    queryFn: () => platformGovernanceService.listActions(status || undefined),
  });
  const detail = useQuery({
    queryKey: ['human-action', detailId],
    queryFn: () => platformGovernanceService.getAction(detailId!),
    enabled: detailId !== null,
  });
  const rows = useMemo(() => list.data ?? [], [list.data]);
  const selected = rows.filter((item) => selectedIds.includes(item.id) && active(item));
  const refresh = async () => {
    setSelectedIds([]);
    setDecisionOpen(false);
    setComment('');
    await client.invalidateQueries({ queryKey: ['human-actions'] });
    await client.invalidateQueries({ queryKey: ['human-action'] });
  };
  const decide = useMutation({
    mutationFn: () => selected.length === 1
      ? platformGovernanceService.transitionAction(selected[0], targetStatus, comment)
      : platformGovernanceService.bulkTransitionActions(selected, targetStatus, comment),
    onSuccess: refresh,
  });
  const activeRows = rows.filter(active);
  const allSelected = activeRows.length > 0 && activeRows.every((item) => selectedIds.includes(item.id));

  return (
    <Box sx={{ maxWidth: 1600, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack direction={{ xs: 'column', md: 'row' }} sx={{ justifyContent: 'space-between', gap: 2, mb: 2 }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 750 }}>Human Action Center</Typography>
          <Typography variant="body2" color="text.secondary">Evidence-backed decisions across the commercial workflow</Typography>
        </Box>
        <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
          <Button variant="outlined" startIcon={<ErrorOutlined />} onClick={() => navigate('/sales/exceptions')}>Commercial exceptions</Button>
          <Button variant="contained" startIcon={<FactCheck />} disabled={!selected.length}
            onClick={() => { setTargetStatus('Completed'); setDecisionOpen(true); }}>Decide selected</Button>
        </Stack>
      </Stack>

      {(list.isError || detail.isError || decide.isError) && <Alert severity="error" sx={{ mb: 2 }}>The action request could not be completed. Refresh to reconcile current versions and permissions.</Alert>}

      <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
        <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ gap: 2, alignItems: { sm: 'center' } }}>
          <FormControl size="small" sx={{ minWidth: 220 }}>
            <InputLabel>Status</InputLabel>
            <Select label="Status" value={status} onChange={(event) => { setStatus(event.target.value as HumanActionStatus | ''); setSelectedIds([]); }}>
              <MenuItem value="">All statuses</MenuItem>
              {(['Open', 'InReview', 'Escalated', 'Completed', 'Rejected'] as HumanActionStatus[]).map((value) => <MenuItem key={value} value={value}>{value}</MenuItem>)}
            </Select>
          </FormControl>
          <Typography variant="body2" color="text.secondary">{activeRows.length} active · {rows.filter((item) => item.isOverdue).length} overdue</Typography>
        </Stack>
      </Paper>

      <TableContainer component={Paper} variant="outlined">
        {list.isLoading ? <Box sx={{ p: 7, textAlign: 'center' }}><CircularProgress /></Box> : (
          <Table size="small" aria-label="Human action queue">
            <TableHead><TableRow>
              <TableCell padding="checkbox"><Checkbox checked={allSelected} disabled={!activeRows.length} onChange={() => setSelectedIds(allSelected ? [] : activeRows.map((item) => item.id))} slotProps={{ input: { 'aria-label': 'Select all active actions' } }} /></TableCell>
              <TableCell>Action</TableCell><TableCell>Source</TableCell><TableCell>Priority</TableCell><TableCell>Status</TableCell><TableCell>Confidence</TableCell><TableCell>Due</TableCell><TableCell align="right">Controls</TableCell>
            </TableRow></TableHead>
            <TableBody>
              {rows.map((item) => {
                const route = sourceRoute(item);
                return <TableRow key={item.id} hover>
                  <TableCell padding="checkbox"><Checkbox checked={selectedIds.includes(item.id)} disabled={!active(item)} onChange={() => setSelectedIds((current) => current.includes(item.id) ? current.filter((id) => id !== item.id) : [...current, item.id])} slotProps={{ input: { 'aria-label': `Select ${item.title}` } }} /></TableCell>
                  <TableCell sx={{ minWidth: 250 }}><Typography variant="body2" sx={{ fontWeight: 700 }}>{item.title}</Typography><Typography variant="caption" color="text.secondary">{item.actionType} · {item.recommendation}</Typography></TableCell>
                  <TableCell><Typography variant="body2">{item.sourceType}</Typography><Typography variant="caption" color="text.secondary">{item.sourceReference}</Typography></TableCell>
                  <TableCell><Chip size="small" color={priorityColor(item.priority)} icon={item.priority === 'Critical' ? <PriorityHigh /> : undefined} label={item.priority} /></TableCell>
                  <TableCell><Chip size="small" color={statusColor(item.status)} label={item.status} /></TableCell>
                  <TableCell>{Math.round(item.confidence * 100)}%</TableCell>
                  <TableCell><Typography variant="body2" color={item.isOverdue ? 'error.main' : 'text.primary'}>{new Date(item.dueOn).toLocaleString()}</Typography></TableCell>
                  <TableCell align="right"><Stack direction="row" sx={{ justifyContent: 'flex-end' }}>
                    <Button size="small" startIcon={<Visibility />} onClick={() => setDetailId(item.id)}>Review</Button>
                    {route && <Button size="small" startIcon={<Launch />} onClick={() => navigate(route)}>Open source</Button>}
                  </Stack></TableCell>
                </TableRow>;
              })}
              {!rows.length && <TableRow><TableCell colSpan={8}><Box sx={{ py: 7, textAlign: 'center' }}><CheckCircleOutlined color="success" /><Typography sx={{ fontWeight: 700 }}>No actions match this view</Typography></Box></TableCell></TableRow>}
            </TableBody>
          </Table>
        )}
      </TableContainer>

      <Dialog open={detailId !== null} onClose={() => setDetailId(null)} fullWidth maxWidth="md">
        <DialogTitle>{detail.data?.item.title ?? 'Action evidence'}</DialogTitle>
        <DialogContent>
          {detail.isLoading ? <Box sx={{ p: 5, textAlign: 'center' }}><CircularProgress /></Box> : detail.data && <Stack sx={{ gap: 2 }}>
            <Alert severity={detail.data.item.isOverdue ? 'warning' : 'info'}>{detail.data.item.commercialImpact}</Alert>
            <Box><Typography variant="subtitle2">Recommendation</Typography><Typography variant="body2">{detail.data.item.recommendation}</Typography></Box>
            <Box><Typography variant="subtitle2">Evidence</Typography><Paper variant="outlined" sx={{ p: 1.5, mt: .5, overflow: 'auto' }}><Typography component="pre" variant="caption" sx={{ m: 0, whiteSpace: 'pre-wrap' }}>{JSON.stringify(JSON.parse(detail.data.item.evidenceJson), null, 2)}</Typography></Paper></Box>
            <Box><Typography variant="subtitle2">Next action after approval</Typography><Typography variant="body2">{detail.data.item.resumeActionCode}</Typography></Box>
            <Box><Typography variant="subtitle2">Immutable decision history</Typography>{detail.data.events.map((event) => <Typography key={event.id} variant="body2" sx={{ mt: .5 }}>{new Date(event.occurredOn).toLocaleString()} · {event.action} · {event.comment}</Typography>)}</Box>
          </Stack>}
        </DialogContent>
        <DialogActions><Button onClick={() => setDetailId(null)}>Close</Button></DialogActions>
      </Dialog>

      <Dialog open={decisionOpen} onClose={() => setDecisionOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle>Decide {selected.length} action{selected.length === 1 ? '' : 's'}</DialogTitle>
        <DialogContent><Stack sx={{ pt: 1, gap: 2 }}>
          <FormControl><InputLabel>Decision</InputLabel><Select label="Decision" value={targetStatus} onChange={(event) => setTargetStatus(event.target.value as HumanActionStatus)}>
            <MenuItem value="InReview">Start review</MenuItem><MenuItem value="Escalated">Escalate</MenuItem><MenuItem value="Completed">Approve and resume</MenuItem><MenuItem value="Rejected">Reject</MenuItem>
          </Select></FormControl>
          <TextField label="Decision comment" value={comment} onChange={(event) => setComment(event.target.value)} multiline minRows={3} required />
        </Stack></DialogContent>
        <DialogActions><Button onClick={() => setDecisionOpen(false)}>Cancel</Button><Button variant="contained" disabled={!comment.trim() || decide.isPending} onClick={() => decide.mutate()}>Record decision</Button></DialogActions>
      </Dialog>
    </Box>
  );
}
