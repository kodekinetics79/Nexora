import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Autocomplete,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  InputAdornment,
  MenuItem,
  Paper,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import { DataGrid, type GridColDef } from '@mui/x-data-grid';
import { FilterAltOff as ClearIcon, Search as SearchIcon } from '@mui/icons-material';
import Stack from './Flex';
import { EmptyState, ErrorState, LoadingState } from './States';
import { SoftChip } from './StatusChip';
import { fmtDateTime, fmtNumber } from './format';
import { platformApi, type AuditExplorerQuery } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import type { PlatformAuditEntry } from '../types';

const PAGE_SIZE = 25;

/** A JSON value rendered on one line, matching how the server compares before/after. */
const renderValue = (value: unknown): string => {
  if (value === null || value === undefined) return '—';
  if (typeof value === 'string') return value;
  return JSON.stringify(value, null, 2);
};

interface Props {
  /** Locks the explorer to one customer's activity. */
  tenantId?: string;
  height?: number | string;
}

/**
 * The audit explorer.
 *
 * <p>The action filter is built from `GET /audit/actions` — the verbs that are actually in
 * the log, with their counts — rather than from a list maintained here. A hard-coded
 * vocabulary is a filter that silently stops offering the newest action the day somebody
 * adds an endpoint, which is exactly when an investigator needs it.</p>
 */
export default function AuditExplorer({ tenantId, height = 'calc(100vh - 340px)' }: Props) {
  const [actions, setActions] = useState<string[]>([]);
  const [result, setResult] = useState<'all' | 'success' | 'failure'>('all');
  const [search, setSearch] = useState('');
  const [fromUtc, setFromUtc] = useState('');
  const [toUtc, setToUtc] = useState('');
  const [page, setPage] = useState(0);
  const [openEntryId, setOpenEntryId] = useState<string | null>(null);

  const query: AuditExplorerQuery = useMemo(
    () => ({
      tenantId,
      action: actions.length > 0 ? actions : undefined,
      result: result === 'all' ? undefined : result,
      search: search.trim() || undefined,
      // A date input gives a local calendar day; the API takes an instant, so the day is
      // widened to its full span rather than silently meaning midnight.
      fromUtc: fromUtc ? new Date(`${fromUtc}T00:00:00`).toISOString() : undefined,
      toUtc: toUtc ? new Date(`${toUtc}T23:59:59.999`).toISOString() : undefined,
      page: page + 1,
      pageSize: PAGE_SIZE,
    }),
    [tenantId, actions, result, search, fromUtc, toUtc, page],
  );

  const entriesQuery = useQuery({
    queryKey: platformKeys.auditQuery(query),
    queryFn: () => platformApi.queryAudit(query),
  });

  const actionsQuery = useQuery({
    queryKey: platformKeys.auditActions({ tenantId }),
    queryFn: () => platformApi.listAuditActions({ tenantId }),
  });

  const entryQuery = useQuery({
    queryKey: platformKeys.auditEntry(openEntryId ?? ''),
    queryFn: () => platformApi.getAuditEntry(openEntryId as string),
    enabled: Boolean(openEntryId),
  });

  const hasFilters =
    actions.length > 0 || result !== 'all' || search.trim() !== '' || fromUtc !== '' || toUtc !== '';

  const columns: GridColDef<PlatformAuditEntry>[] = [
    {
      field: 'occurredAtUtc',
      headerName: 'When',
      width: 180,
      renderCell: (p) => (
        <Typography variant="caption" color="text.secondary">
          {fmtDateTime(p.row.occurredAtUtc)}
        </Typography>
      ),
    },
    {
      field: 'actor',
      headerName: 'Actor',
      flex: 1,
      minWidth: 170,
      renderCell: (p) => (
        <Box sx={{ lineHeight: 1.2 }}>
          <Typography variant="body2" sx={{ fontWeight: 600 }}>
            {p.row.actor}
          </Typography>
          <Typography variant="caption" color="text.secondary">
            {p.row.actorEmail ?? '—'}
          </Typography>
        </Box>
      ),
    },
    {
      field: 'action',
      headerName: 'Action',
      flex: 1.2,
      minWidth: 200,
      renderCell: (p) => (
        <Box component="code" sx={{ fontSize: 12.5, fontWeight: 700 }}>
          {p.row.action}
        </Box>
      ),
    },
    ...(tenantId
      ? []
      : ([
          {
            field: 'tenantName',
            headerName: 'Tenant',
            flex: 0.9,
            minWidth: 150,
            renderCell: (p) =>
              p.row.tenantName ? (
                <Typography variant="body2">{p.row.tenantName}</Typography>
              ) : (
                // A null tenant is a platform-wide action, not a missing value.
                <Typography variant="caption" color="text.secondary">
                  platform-wide
                </Typography>
              ),
          },
        ] as GridColDef<PlatformAuditEntry>[])),
    {
      field: 'targetId',
      headerName: 'Target',
      width: 170,
      renderCell: (p) => (
        <Typography variant="caption" sx={{ fontFamily: 'monospace' }} color="text.secondary">
          {p.row.targetType ? `${p.row.targetType}:${p.row.targetId ?? '—'}` : (p.row.targetId ?? '—')}
        </Typography>
      ),
    },
    {
      field: 'result',
      headerName: 'Result',
      width: 110,
      renderCell: (p) => (
        <SoftChip label={p.row.result} tone={p.row.result === 'success' ? 'success' : 'error'} dot={false} />
      ),
    },
  ];

  return (
    <Box>
      <Paper sx={{ p: 1.5, mb: 2, borderRadius: 3 }}>
        <Stack direction={{ xs: 'column', lg: 'row' }} spacing={1.5} sx={{ flexWrap: 'wrap' }} alignItems={{ lg: 'center' }}>
          <TextField
            size="small"
            placeholder="Search actor, action, target…"
            value={search}
            onChange={(event) => {
              setSearch(event.target.value);
              setPage(0);
            }}
            aria-label="Search audit entries"
            sx={{ flex: 1, minWidth: 200 }}
            slotProps={{
              input: {
                startAdornment: (
                  <InputAdornment position="start">
                    <SearchIcon fontSize="small" />
                  </InputAdornment>
                ),
              },
            }}
          />
          <Autocomplete
            multiple
            size="small"
            options={(actionsQuery.data ?? []).map((entry) => entry.action)}
            value={actions}
            onChange={(_event, next) => {
              setActions(next);
              setPage(0);
            }}
            sx={{ minWidth: 280 }}
            renderInput={(params) => (
              <TextField {...params} label="Action" placeholder={actions.length === 0 ? 'Any action' : ''} />
            )}
            renderOption={(props, option) => {
              const meta = (actionsQuery.data ?? []).find((entry) => entry.action === option);
              const { key, ...rest } = props as { key: string } & Record<string, unknown>;
              return (
                <Box component="li" key={key} {...rest}>
                  <Box component="code" sx={{ flex: 1, fontSize: 12.5 }}>
                    {option}
                  </Box>
                  <Typography variant="caption" color="text.secondary">
                    {meta ? fmtNumber(meta.count) : ''}
                  </Typography>
                </Box>
              );
            }}
          />
          <TextField
            size="small"
            select
            label="Result"
            value={result}
            onChange={(event) => {
              setResult(event.target.value as 'all' | 'success' | 'failure');
              setPage(0);
            }}
            sx={{ minWidth: 130 }}
          >
            <MenuItem value="all">All results</MenuItem>
            <MenuItem value="success">Success</MenuItem>
            <MenuItem value="failure">Failure</MenuItem>
          </TextField>
          <TextField
            size="small"
            type="date"
            label="From"
            value={fromUtc}
            onChange={(event) => {
              setFromUtc(event.target.value);
              setPage(0);
            }}
            slotProps={{ inputLabel: { shrink: true } }}
          />
          <TextField
            size="small"
            type="date"
            label="To"
            value={toUtc}
            onChange={(event) => {
              setToUtc(event.target.value);
              setPage(0);
            }}
            slotProps={{ inputLabel: { shrink: true } }}
          />
          {hasFilters && (
            <Button
              color="inherit"
              startIcon={<ClearIcon />}
              onClick={() => {
                setActions([]);
                setResult('all');
                setSearch('');
                setFromUtc('');
                setToUtc('');
                setPage(0);
              }}
              sx={{ fontWeight: 700 }}
            >
              Clear
            </Button>
          )}
        </Stack>
      </Paper>

      <Paper sx={{ borderRadius: 3, overflow: 'hidden', height, minHeight: 380 }}>
        {entriesQuery.isError ? (
          <ErrorState
            message={platformErrorMessage(entriesQuery.error, 'The audit service did not respond.')}
            onRetry={() => entriesQuery.refetch()}
          />
        ) : !entriesQuery.isLoading && (entriesQuery.data?.items.length ?? 0) === 0 ? (
          <EmptyState title="No audit entries" message="No activity matches the current filters." />
        ) : (
          <DataGrid
            rows={entriesQuery.data?.items ?? []}
            columns={columns}
            loading={entriesQuery.isLoading}
            getRowId={(row) => row.id}
            onRowClick={(params) => setOpenEntryId(String(params.id))}
            disableRowSelectionOnClick
            rowHeight={58}
            paginationMode="server"
            rowCount={entriesQuery.data?.totalCount ?? 0}
            paginationModel={{ page, pageSize: PAGE_SIZE }}
            onPaginationModelChange={(model) => setPage(model.page)}
            pageSizeOptions={[PAGE_SIZE]}
            sx={{
              border: 'none',
              '& .MuiDataGrid-row': { cursor: 'pointer' },
              '& .MuiDataGrid-columnHeaders': { bgcolor: 'action.hover' },
              '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 700 },
            }}
          />
        )}
      </Paper>

      <Dialog open={Boolean(openEntryId)} onClose={() => setOpenEntryId(null)} fullWidth maxWidth="md">
        <DialogTitle sx={{ fontWeight: 800 }}>Audit entry {openEntryId}</DialogTitle>
        <DialogContent dividers>
          {entryQuery.isLoading ? (
            <LoadingState label="Loading the entry…" minHeight={200} />
          ) : entryQuery.isError || !entryQuery.data ? (
            <ErrorState
              message={platformErrorMessage(entryQuery.error, 'The entry could not be loaded.')}
              onRetry={() => entryQuery.refetch()}
              minHeight={200}
            />
          ) : (
            <Stack spacing={2}>
              <Stack direction="row" spacing={1.5} alignItems="center" sx={{ flexWrap: 'wrap' }}>
                <Box component="code" sx={{ fontWeight: 800 }}>
                  {entryQuery.data.action}
                </Box>
                <SoftChip
                  label={entryQuery.data.result}
                  tone={entryQuery.data.result === 'success' ? 'success' : 'error'}
                  dot={false}
                />
                <Typography variant="caption" color="text.secondary">
                  {fmtDateTime(entryQuery.data.occurredAtUtc)} · {entryQuery.data.actor}
                  {entryQuery.data.ip ? ` · ${entryQuery.data.ip}` : ''}
                </Typography>
              </Stack>

              <Divider />

              <Box>
                <Typography variant="overline" sx={{ fontWeight: 800 }}>
                  What changed
                </Typography>
                {entryQuery.data.changes.length === 0 ? (
                  // Not a bug and not an empty state to dress up: the row recorded no
                  // before/after pair, and inventing a plausible diff would be worse.
                  <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                    This action recorded no before/after pair, so there is no field-level diff. The raw context
                    below is everything the endpoint wrote.
                  </Typography>
                ) : (
                  <Box sx={{ overflowX: 'auto', mt: 1 }}>
                    <Table size="small">
                      <TableHead>
                        <TableRow>
                          <TableCell sx={{ fontWeight: 700 }}>Field</TableCell>
                          <TableCell sx={{ fontWeight: 700 }}>Before</TableCell>
                          <TableCell sx={{ fontWeight: 700 }}>After</TableCell>
                        </TableRow>
                      </TableHead>
                      <TableBody>
                        {entryQuery.data.changes.map((change) => (
                          <TableRow key={change.field}>
                            <TableCell sx={{ fontWeight: 700 }}>{change.field}</TableCell>
                            <TableCell>
                              <Typography
                                variant="caption"
                                sx={{ fontFamily: 'monospace', color: 'error.main', wordBreak: 'break-word' }}
                              >
                                {change.before ?? '—'}
                              </Typography>
                            </TableCell>
                            <TableCell>
                              <Typography
                                variant="caption"
                                sx={{ fontFamily: 'monospace', color: 'success.main', wordBreak: 'break-word' }}
                              >
                                {change.after ?? '—'}
                              </Typography>
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </Box>
                )}
              </Box>

              <Box>
                <Typography variant="overline" sx={{ fontWeight: 800 }}>
                  Raw context
                </Typography>
                <Box
                  component="pre"
                  sx={{ mt: 0.5, p: 1.5, m: 0, borderRadius: 2, bgcolor: 'action.hover', fontSize: 12, overflowX: 'auto' }}
                >
                  {renderValue(entryQuery.data.metadata)}
                </Box>
              </Box>
            </Stack>
          )}
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setOpenEntryId(null)} sx={{ fontWeight: 700 }}>
            Close
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
