import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Stack, Chip, TextField, CircularProgress,
  Alert, Table, TableBody, TableCell, TableContainer, TableHead, TableRow,
  TablePagination, Dialog, DialogTitle, DialogContent, DialogActions, MenuItem,
  InputAdornment, Tooltip,
} from '@mui/material';
import {
  PostAdd as DraftIcon,
  Search as SearchIcon,
  ReceiptLong as BoqIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import boqService, { BOQ_SERVICE_CATEGORIES } from '../../api/services/boqService';
import type { BoqListItemDto, BoqStatus } from '../../api/services/boqService';
import { ConfidenceChip, formatMoney } from '../Intelligence/common';

// ─── Plain-language helpers (zero-training mandate) ──────────────────────────

const STATUS_META: Record<BoqStatus, { label: string; color: 'default' | 'info' | 'success' }> = {
  Draft: { label: 'Draft', color: 'default' },
  InReview: { label: 'In review', color: 'info' },
  Approved: { label: 'Approved', color: 'success' },
};

const CATEGORY_LABELS: Record<string, string> = {
  electrical: 'Electrical',
  mechanical: 'Mechanical',
  civil: 'Civil',
  maintenance: 'Maintenance',
  manpower: 'Manpower',
  mixed: 'Mixed trades',
  other: 'Other',
};

export const categoryLabel = (raw: string): string => CATEGORY_LABELS[raw] ?? 'Other';

const StatusChip: React.FC<{ status: BoqStatus }> = ({ status }) => {
  const meta = STATUS_META[status] ?? STATUS_META.Draft;
  return <Chip label={meta.label} color={meta.color} size="small" sx={{ fontWeight: 700 }} />;
};

/** "4 items need details" badge — the honest-TBD count, first-class. */
const TbdBadge: React.FC<{ count: number }> = ({ count }) =>
  count > 0 ? (
    <Tooltip title="Lines where the request didn't state a quantity or unit — open the BOQ to fill them in.">
      <Chip
        label={`${count} item${count === 1 ? '' : 's'} need${count === 1 ? 's' : ''} details`}
        color="warning"
        size="small"
        variant="outlined"
        sx={{ fontWeight: 700 }}
      />
    </Tooltip>
  ) : (
    <Chip label="All lines complete" color="success" size="small" variant="outlined" sx={{ fontWeight: 700 }} />
  );

// ─── New-draft dialog ────────────────────────────────────────────────────────

const NewBoqDialog: React.FC<{ open: boolean; onClose: () => void }> = ({ open, onClose }) => {
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [title, setTitle] = useState('');
  const [category, setCategory] = useState('');
  const [text, setText] = useState('');

  const draftMutation = useMutation({
    mutationFn: () =>
      boqService.draft({
        title: title.trim() || undefined,
        text: text.trim(),
        serviceCategory: category || undefined,
      }),
    onSuccess: (doc) => {
      queryClient.invalidateQueries({ queryKey: ['boq-list'] });
      enqueueSnackbar(
        doc.tbdCount > 0
          ? `Draft ready — ${doc.tbdCount} item${doc.tbdCount === 1 ? '' : 's'} still need details.`
          : 'Draft ready.',
        { variant: 'success' }
      );
      onClose();
      navigate(`/services/boq/${doc.id}`);
    },
    onError: (err: any) => {
      enqueueSnackbar(err?.response?.data ?? 'Could not create the draft. Please try again.', {
        variant: 'error',
      });
    },
  });

  return (
    <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth>
      <DialogTitle sx={{ fontWeight: 800 }}>Draft a bill of quantities</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="info" sx={{ fontSize: '0.82rem' }}>
            Paste the service request or scope of work below. Nexora will structure it into
            sections and line items. Quantities the text doesn't state are marked
            “needs details” for you — they are never guessed.
          </Alert>
          <TextField
            label="Title (optional)"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            fullWidth
            size="small"
          />
          <TextField
            select
            label="Type of work (optional)"
            value={category}
            onChange={(e) => setCategory(e.target.value)}
            fullWidth
            size="small"
          >
            <MenuItem value="">Let Nexora decide</MenuItem>
            {BOQ_SERVICE_CATEGORIES.map((c) => (
              <MenuItem key={c} value={c}>
                {categoryLabel(c)}
              </MenuItem>
            ))}
          </TextField>
          <TextField
            label="Scope of work / request text"
            value={text}
            onChange={(e) => setText(e.target.value)}
            fullWidth
            multiline
            minRows={8}
            placeholder={
              'e.g. Supply and install one 250A distribution panel in the utility building, ' +
              'run LV cabling to 12 existing machines, test all circuits and commission…'
            }
          />
        </Stack>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        <Button onClick={onClose} disabled={draftMutation.isPending}>
          Cancel
        </Button>
        <Button
          variant="contained"
          startIcon={draftMutation.isPending ? <CircularProgress size={16} color="inherit" /> : <DraftIcon />}
          disabled={!text.trim() || draftMutation.isPending}
          onClick={() => draftMutation.mutate()}
        >
          {draftMutation.isPending ? 'Drafting…' : 'Draft it for me'}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

// ─── Page ────────────────────────────────────────────────────────────────────

const BoqListPage: React.FC = () => {
  const navigate = useNavigate();
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(20);
  const [statusFilter, setStatusFilter] = useState('');
  const [search, setSearch] = useState('');
  const [dialogOpen, setDialogOpen] = useState(false);

  const { data, isLoading, isError } = useQuery({
    queryKey: ['boq-list', page, pageSize, statusFilter, search],
    queryFn: () =>
      boqService.list({
        page: page + 1,
        pageSize,
        status: statusFilter || undefined,
        search: search || undefined,
      }),
  });

  const rows: BoqListItemDto[] = data?.items ?? [];

  return (
    <Box sx={{ p: 3 }}>
      <Stack direction="row" sx={{ mb: 2, justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, display: 'flex', alignItems: 'center', gap: 1 }}>
            <BoqIcon color="primary" /> Service BOQs
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
            Bills of quantities for service work — maintenance, installation, testing and manpower.
          </Typography>
        </Box>
        <Button variant="contained" startIcon={<DraftIcon />} onClick={() => setDialogOpen(true)}>
          New BOQ from text
        </Button>
      </Stack>

      <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
          <TextField
            size="small"
            placeholder="Search by title…"
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setPage(0);
            }}
            slotProps={{
              input: {
                startAdornment: (
                  <InputAdornment position="start">
                    <SearchIcon fontSize="small" />
                  </InputAdornment>
                ),
              },
            }}
            sx={{ minWidth: 260 }}
          />
          <TextField
            select
            size="small"
            label="Status"
            value={statusFilter}
            onChange={(e) => {
              setStatusFilter(e.target.value);
              setPage(0);
            }}
            sx={{ minWidth: 160 }}
          >
            <MenuItem value="">All</MenuItem>
            <MenuItem value="Draft">Draft</MenuItem>
            <MenuItem value="InReview">In review</MenuItem>
            <MenuItem value="Approved">Approved</MenuItem>
          </TextField>
        </Stack>
      </Paper>

      {isLoading ? (
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
          <CircularProgress />
        </Box>
      ) : isError ? (
        <Alert severity="error">
          Couldn't load your BOQs. Check your connection and try again.
        </Alert>
      ) : rows.length === 0 ? (
        <Paper variant="outlined" sx={{ p: 6, textAlign: 'center' }}>
          <Typography sx={{ fontWeight: 700, mb: 1 }}>No BOQs yet</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            Draft one from a service request — paste the scope text and Nexora will structure it for you.
          </Typography>
          <Button variant="contained" startIcon={<DraftIcon />} onClick={() => setDialogOpen(true)}>
            New BOQ from text
          </Button>
        </Paper>
      ) : (
        <Paper variant="outlined">
          <TableContainer>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={{ fontWeight: 800 }}>Title</TableCell>
                  <TableCell sx={{ fontWeight: 800 }}>Type of work</TableCell>
                  <TableCell sx={{ fontWeight: 800 }}>Status</TableCell>
                  <TableCell sx={{ fontWeight: 800 }}>Completeness</TableCell>
                  <TableCell sx={{ fontWeight: 800 }}>Confidence</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Lines</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Priced total</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {rows.map((row) => (
                  <TableRow
                    key={row.id}
                    hover
                    sx={{ cursor: 'pointer' }}
                    onClick={() => navigate(`/services/boq/${row.id}`)}
                  >
                    <TableCell sx={{ fontWeight: 600, maxWidth: 340 }}>
                      <Typography noWrap sx={{ fontSize: '0.85rem', fontWeight: 600 }}>
                        {row.title}
                      </Typography>
                      {row.leadId != null && (
                        <Typography variant="caption" color="text.secondary">
                          From lead #{row.leadId}
                        </Typography>
                      )}
                    </TableCell>
                    <TableCell>
                      <Chip label={categoryLabel(row.serviceCategory)} size="small" variant="outlined" />
                    </TableCell>
                    <TableCell>
                      <StatusChip status={row.status} />
                    </TableCell>
                    <TableCell>
                      <TbdBadge count={row.tbdCount} />
                    </TableCell>
                    <TableCell>
                      {row.overallConfidence != null ? (
                        <ConfidenceChip score={Number(row.overallConfidence)} />
                      ) : (
                        <Typography variant="caption" color="text.secondary">—</Typography>
                      )}
                    </TableCell>
                    <TableCell align="right">{row.itemCount}</TableCell>
                    <TableCell align="right" sx={{ fontWeight: 700 }}>
                      {formatMoney(row.totalAmount)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
          <TablePagination
            component="div"
            count={data?.totalCount ?? 0}
            page={page}
            onPageChange={(_e, p) => setPage(p)}
            rowsPerPage={pageSize}
            onRowsPerPageChange={(e) => {
              setPageSize(parseInt(e.target.value, 10));
              setPage(0);
            }}
            rowsPerPageOptions={[10, 20, 50]}
          />
        </Paper>
      )}

      <NewBoqDialog open={dialogOpen} onClose={() => setDialogOpen(false)} />
    </Box>
  );
};

export default BoqListPage;
