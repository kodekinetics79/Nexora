import { useMemo, useState } from 'react';
import Stack from '../components/Flex';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Chip,
  Grid,
  IconButton,
  MenuItem,
  Paper,
  Tab,
  Tabs,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import { DataGrid, type GridColDef } from '@mui/x-data-grid';
import {
  Bolt as InFlightIcon,
  DeleteOutlined as DiscardIcon,
  Layers as QueueIcon,
  ReportGmailerrorred as DeadLetterIcon,
  Replay as RequeueIcon,
  Speed as LatencyIcon,
  TaskAlt as ProcessedIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { platformApi } from '../api/client';
import { platformKeys } from '../api/queryKeys';
import type { ExtractionJob, JobStatus } from '../types';
import PageHeader from '../components/PageHeader';
import StatTile from '../components/StatTile';
import { JobStatusChip } from '../components/StatusChip';
import { EmptyState, ErrorState, TilesSkeleton } from '../components/States';
import { fmtDateTime, fmtLatency, fmtNumber, fmtPercent } from '../components/format';

export default function PipelinePage() {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [searchParams, setSearchParams] = useSearchParams();
  const tenantParam = searchParams.get('tenant') ?? 'all';
  const [tab, setTab] = useState(0); // 0 = all jobs, 1 = dead-letter

  const { data: stats, isLoading: statsLoading } = useQuery({
    queryKey: platformKeys.queue(),
    queryFn: () => platformApi.getQueueStats(),
  });

  const { data: tenants } = useQuery({
    queryKey: platformKeys.tenants(),
    queryFn: () => platformApi.listTenants(),
  });

  const statusFilter: JobStatus | 'all' = tab === 1 ? 'dead_letter' : 'all';
  const jobsQuery = { tenantId: tenantParam === 'all' ? undefined : tenantParam, status: statusFilter };
  const { data: jobs, isLoading: jobsLoading, isError, refetch } = useQuery({
    queryKey: platformKeys.jobs(jobsQuery),
    queryFn: () => platformApi.listJobs(jobsQuery),
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.all });
  };

  const requeueMutation = useMutation({
    mutationFn: (jobId: string) => platformApi.requeueJob(jobId),
    onSuccess: () => {
      enqueueSnackbar('Job requeued', { variant: 'success' });
      invalidate();
    },
    onError: () => enqueueSnackbar('Requeue failed', { variant: 'error' }),
  });

  const discardMutation = useMutation({
    mutationFn: (jobId: string) => platformApi.discardJob(jobId),
    onSuccess: () => {
      enqueueSnackbar('Job discarded', { variant: 'info' });
      invalidate();
    },
    onError: () => enqueueSnackbar('Discard failed', { variant: 'error' }),
  });

  const setTenant = (value: string) => {
    const next = new URLSearchParams(searchParams);
    if (value === 'all') next.delete('tenant');
    else next.set('tenant', value);
    setSearchParams(next, { replace: true });
  };

  const columns: GridColDef<ExtractionJob>[] = useMemo(
    () => [
      { field: 'documentName', headerName: 'Document', flex: 1.3, minWidth: 200, renderCell: (p) => (
        <Box sx={{ lineHeight: 1.2 }}>
          <Typography variant="body2" sx={{ fontWeight: 600 }}>{p.row.documentName}</Typography>
          <Typography variant="caption" color="text.secondary">{p.row.id}</Typography>
        </Box>
      ) },
      { field: 'tenantName', headerName: 'Tenant', flex: 1, minWidth: 150 },
      { field: 'status', headerName: 'Status', width: 130, renderCell: (p) => <JobStatusChip status={p.row.status} /> },
      { field: 'attempts', headerName: 'Attempts', width: 100, renderCell: (p) => (
        <Typography variant="body2" sx={{ fontWeight: 600, color: p.row.attempts >= p.row.maxAttempts ? 'error.main' : 'text.primary' }}>
          {p.row.attempts}/{p.row.maxAttempts}
        </Typography>
      ) },
      { field: 'latencyMs', headerName: 'Latency', width: 100, renderCell: (p) => <Typography variant="body2">{fmtLatency(p.row.latencyMs)}</Typography> },
      { field: 'enqueuedAt', headerName: 'Enqueued', width: 170, renderCell: (p) => (
        <Typography variant="caption" color="text.secondary">{fmtDateTime(p.row.enqueuedAt)}</Typography>
      ) },
      { field: 'error', headerName: 'Error', flex: 1.2, minWidth: 200, renderCell: (p) => p.row.error ? (
        <Tooltip title={p.row.error}>
          <Typography variant="caption" color="error.main" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {p.row.error}
          </Typography>
        </Tooltip>
      ) : <Typography variant="caption" color="text.secondary">—</Typography> },
      { field: 'actions', headerName: 'Actions', width: 110, sortable: false, filterable: false, renderCell: (p) => {
        const canRetry = p.row.status === 'failed' || p.row.status === 'dead_letter';
        return (
          <Stack direction="row" spacing={0.5}>
            <Tooltip title={canRetry ? 'Requeue' : 'Only failed jobs can be requeued'}>
              <span>
                <IconButton size="small" color="primary" disabled={!canRetry || requeueMutation.isPending} onClick={() => requeueMutation.mutate(p.row.id)}>
                  <RequeueIcon fontSize="small" />
                </IconButton>
              </span>
            </Tooltip>
            <Tooltip title="Discard">
              <span>
                <IconButton size="small" color="error" disabled={discardMutation.isPending} onClick={() => discardMutation.mutate(p.row.id)}>
                  <DiscardIcon fontSize="small" />
                </IconButton>
              </span>
            </Tooltip>
          </Stack>
        );
      } },
    ],
    [requeueMutation, discardMutation],
  );

  return (
    <Box>
      <PageHeader title="Extraction Pipeline" subtitle="Global and per-tenant extraction queue health with dead-letter review." />

      {statsLoading || !stats ? (
        <TilesSkeleton count={4} />
      ) : (
        <Grid container spacing={2.5} sx={{ mb: 3 }}>
          <Grid size={{ xs: 12, sm: 6, md: 3 }}>
            <StatTile label="Queue Depth" value={fmtNumber(stats.queueDepth)} icon={<QueueIcon />} color="#f59e0b" />
          </Grid>
          <Grid size={{ xs: 12, sm: 6, md: 3 }}>
            <StatTile label="In-flight" value={fmtNumber(stats.inFlight)} icon={<InFlightIcon />} color="#3b82f6" />
          </Grid>
          <Grid size={{ xs: 12, sm: 6, md: 3 }}>
            <StatTile label="Dead-letter" value={fmtNumber(stats.deadLetter)} icon={<DeadLetterIcon />} color="#ef4444" caption="requires review" />
          </Grid>
          <Grid size={{ xs: 12, sm: 6, md: 3 }}>
            <StatTile label="Processed 24h" value={fmtNumber(stats.processedLast24h)} icon={<ProcessedIcon />} color="#10b981" caption={`${fmtPercent(stats.successRate)} success`} />
          </Grid>
        </Grid>
      )}

      <Paper sx={{ borderRadius: 3, overflow: 'hidden' }}>
        <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" alignItems={{ md: 'center' }} sx={{ px: 2, pt: 1.5, gap: 1.5 }}>
          <Tabs value={tab} onChange={(_e, v) => setTab(v)}>
            <Tab label="All Jobs" sx={{ fontWeight: 700 }} />
            <Tab
              label={
                <Stack direction="row" spacing={1} alignItems="center">
                  <span>Dead-Letter</span>
                  {stats && stats.deadLetter > 0 && <Chip size="small" color="error" label={stats.deadLetter} sx={{ height: 20, fontWeight: 700 }} />}
                </Stack>
              }
              sx={{ fontWeight: 700 }}
            />
          </Tabs>
          <Stack direction="row" spacing={1} alignItems="center">
            <TextField size="small" select label="Tenant" value={tenantParam} onChange={(e) => setTenant(e.target.value)} sx={{ minWidth: 200 }}>
              <MenuItem value="all">All tenants</MenuItem>
              {(tenants ?? []).map((t) => (
                <MenuItem key={t.id} value={t.id}>
                  {t.name}
                </MenuItem>
              ))}
            </TextField>
            <Tooltip title="Refresh">
              <IconButton onClick={() => refetch()} sx={{ bgcolor: 'action.hover', borderRadius: 2 }}>
                <RequeueIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          </Stack>
        </Stack>

        {tab === 1 && (
          <Box sx={{ px: 2, pb: 1 }}>
            <Stack direction="row" spacing={1} alignItems="center" sx={{ color: 'text.secondary' }}>
              <LatencyIcon fontSize="small" />
              <Typography variant="caption">
                Jobs that exhausted all retries. Requeue after fixing the underlying cause, or discard to drop permanently.
              </Typography>
            </Stack>
          </Box>
        )}

        <Box sx={{ height: 'calc(100vh - 460px)', minHeight: 360 }}>
          {isError ? (
            <ErrorState message="The pipeline service did not respond." onRetry={() => refetch()} />
          ) : !jobsLoading && (jobs?.length ?? 0) === 0 ? (
            <EmptyState
              icon={<ProcessedIcon sx={{ fontSize: 44 }} />}
              title={tab === 1 ? 'Dead-letter queue is empty' : 'No jobs match this filter'}
              message={tab === 1 ? 'Every extraction job has completed or is still being retried.' : 'Try a different tenant or check back shortly.'}
            />
          ) : (
            <DataGrid
              rows={jobs ?? []}
              columns={columns}
              loading={jobsLoading}
              getRowId={(r) => r.id}
              disableRowSelectionOnClick
              rowHeight={58}
              initialState={{ pagination: { paginationModel: { pageSize: 25, page: 0 } } }}
              pageSizeOptions={[25, 50, 100]}
              sx={{
                border: 'none',
                '& .MuiDataGrid-columnHeaders': { bgcolor: 'action.hover' },
                '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 700 },
              }}
            />
          )}
        </Box>
      </Paper>
    </Box>
  );
}
