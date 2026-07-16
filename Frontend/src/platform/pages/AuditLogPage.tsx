import { useMemo, useState } from 'react';
import Stack from '../components/Flex';
import { useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box,
  Button,
  InputAdornment,
  MenuItem,
  Paper,
  TextField,
  Typography,
} from '@mui/material';
import { DataGrid, type GridColDef } from '@mui/x-data-grid';
import { FilterAltOff as ClearIcon, Search as SearchIcon } from '@mui/icons-material';
import { platformApi, type AuditQuery } from '../api/client';
import { platformKeys } from '../api/queryKeys';
import type { AuditEntry } from '../types';
import PageHeader from '../components/PageHeader';
import { SoftChip } from '../components/StatusChip';
import { EmptyState, ErrorState } from '../components/States';
import { fmtDateTime } from '../components/format';

const ACTIONS = [
  'tenant.provisioned',
  'tenant.suspended',
  'tenant.resumed',
  'plan.changed',
  'feature_flag.toggled',
  'user.impersonated',
  'deadletter.requeued',
  'quota.overridden',
];

export default function AuditLogPage() {
  const [searchParams] = useSearchParams();
  const tenantFromUrl = searchParams.get('tenant') ?? '';

  const [search, setSearch] = useState('');
  const [action, setAction] = useState('all');
  const [result, setResult] = useState<'all' | 'success' | 'failure'>('all');

  const query: AuditQuery = useMemo(
    () => ({
      action: action === 'all' ? undefined : action,
      result,
      tenantId: tenantFromUrl || undefined,
      search: search.trim() || undefined,
    }),
    [action, result, tenantFromUrl, search],
  );

  const { data: tenants } = useQuery({
    queryKey: platformKeys.tenants(),
    queryFn: () => platformApi.listTenants(),
  });

  const { data: rows, isLoading, isError, refetch } = useQuery({
    queryKey: platformKeys.audit(query),
    queryFn: () => platformApi.listAudit(query),
  });

  const tenantName = tenants?.find((t) => t.id === tenantFromUrl)?.name;

  const hasFilters = search.trim() !== '' || action !== 'all' || result !== 'all';
  const clearFilters = () => {
    setSearch('');
    setAction('all');
    setResult('all');
  };

  const columns: GridColDef<AuditEntry>[] = [
    { field: 'timestamp', headerName: 'Timestamp', width: 180, renderCell: (p) => (
      <Typography variant="caption" color="text.secondary">{fmtDateTime(p.row.timestamp)}</Typography>
    ) },
    { field: 'actor', headerName: 'Actor', flex: 1, minWidth: 170, renderCell: (p) => (
      <Box sx={{ lineHeight: 1.2 }}>
        <Typography variant="body2" sx={{ fontWeight: 600 }}>{p.row.actor}</Typography>
        <Typography variant="caption" color="text.secondary">{p.row.actorEmail}</Typography>
      </Box>
    ) },
    { field: 'action', headerName: 'Action', flex: 1, minWidth: 180, renderCell: (p) => (
      <Box>
        <Box component="code" sx={{ fontSize: 12.5, fontWeight: 700 }}>{p.row.action}</Box>
        {p.row.detail && (
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>{p.row.detail}</Typography>
        )}
      </Box>
    ) },
    { field: 'tenantName', headerName: 'Tenant', flex: 0.9, minWidth: 150, renderCell: (p) => (
      p.row.tenantName ? <Typography variant="body2">{p.row.tenantName}</Typography> : <Typography variant="caption" color="text.secondary">—</Typography>
    ) },
    { field: 'targetId', headerName: 'Target', width: 150, renderCell: (p) => (
      <Typography variant="caption" sx={{ fontFamily: 'monospace' }} color="text.secondary">{p.row.targetId}</Typography>
    ) },
    { field: 'ipAddress', headerName: 'Source IP', width: 130, renderCell: (p) => (
      <Typography variant="caption" sx={{ fontFamily: 'monospace' }} color="text.secondary">{p.row.ipAddress}</Typography>
    ) },
    { field: 'result', headerName: 'Result', width: 110, renderCell: (p) => (
      <SoftChip label={p.row.result} tone={p.row.result === 'success' ? 'success' : 'error'} dot={false} />
    ) },
  ];

  return (
    <Box>
      <PageHeader
        title="Audit Log"
        subtitle={tenantName ? `Platform activity filtered to ${tenantName}.` : 'Every privileged action taken across the platform.'}
      />

      <Paper sx={{ p: 1.5, mb: 2, borderRadius: 3 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} alignItems={{ md: 'center' }}>
          <TextField
            size="small"
            placeholder="Search actor, action, tenant, target…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            sx={{ flex: 1 }}
            slotProps={{ input: { startAdornment: <InputAdornment position="start"><SearchIcon fontSize="small" /></InputAdornment> } }}
          />
          <TextField size="small" select label="Action" value={action} onChange={(e) => setAction(e.target.value)} sx={{ minWidth: 200 }}>
            <MenuItem value="all">All actions</MenuItem>
            {ACTIONS.map((a) => (
              <MenuItem key={a} value={a}>
                {a}
              </MenuItem>
            ))}
          </TextField>
          <TextField size="small" select label="Result" value={result} onChange={(e) => setResult(e.target.value as 'all' | 'success' | 'failure')} sx={{ minWidth: 150 }}>
            <MenuItem value="all">All results</MenuItem>
            <MenuItem value="success">Success</MenuItem>
            <MenuItem value="failure">Failure</MenuItem>
          </TextField>
          {hasFilters && (
            <Button color="inherit" startIcon={<ClearIcon />} onClick={clearFilters} sx={{ fontWeight: 700 }}>
              Clear
            </Button>
          )}
        </Stack>
      </Paper>

      <Paper sx={{ borderRadius: 3, overflow: 'hidden', height: 'calc(100vh - 300px)', minHeight: 420 }}>
        {isError ? (
          <ErrorState message="The audit service did not respond." onRetry={() => refetch()} />
        ) : !isLoading && (rows?.length ?? 0) === 0 ? (
          <EmptyState title="No audit entries" message="No activity matches the current filters." />
        ) : (
          <DataGrid
            rows={rows ?? []}
            columns={columns}
            loading={isLoading}
            getRowId={(r) => r.id}
            disableRowSelectionOnClick
            rowHeight={60}
            initialState={{
              pagination: { paginationModel: { pageSize: 25, page: 0 } },
              sorting: { sortModel: [{ field: 'timestamp', sort: 'desc' }] },
            }}
            pageSizeOptions={[25, 50, 100]}
            sx={{
              border: 'none',
              '& .MuiDataGrid-columnHeaders': { bgcolor: 'action.hover' },
              '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 700 },
            }}
          />
        )}
      </Paper>
    </Box>
  );
}
