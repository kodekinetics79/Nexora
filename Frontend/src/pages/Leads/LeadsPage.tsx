import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Tooltip, Stack, TextField, MenuItem, CircularProgress,
  Dialog, DialogTitle, DialogContent, DialogActions, Alert,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  CheckCircle as AcceptIcon,
  Cancel as RejectIcon,
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Layers as ItemsIcon,
  Email as EmailIcon,
  AutoAwesome as SparkleIcon,
} from '@mui/icons-material';
import leadService from '../../api/services/leadService';
import SearchField from '../../components/common/SearchField';
import { useSnackbar } from 'notistack';

const LeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');
  const [leadSource, setLeadSource] = useState('all');

  // Rejection Dialog State
  const [rejectionDialogOpen, setRejectionDialogOpen] = useState(false);
  const [selectedLeadId, setSelectedLeadId] = useState<number | null>(null);
  const [rejectionReasonId, setRejectionReasonId] = useState<string>('');

  const syncEmailsMutation = useMutation({
    mutationFn: () => leadService.fetchEmails(),
    onSuccess: () => {
      enqueueSnackbar('Email synchronization started successfully!', { variant: 'success' });
      queryClient.invalidateQueries({ queryKey: ['leads'] });
    },
    onError: (err: any) => enqueueSnackbar(err.response?.data || 'Sync failed', { variant: 'error' }),
  });

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['leads', paginationModel, search, leadSource],
    queryFn: () => leadService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      rfqno: search || undefined,
      leadSource: leadSource === 'all' ? undefined : leadSource,
    }),
  });

  const { data: reasons = [] } = useQuery({
    queryKey: ['rejection-reasons'],
    queryFn: () => leadService.getRejectionReasons(),
  });

  const acceptMutation = useMutation({
    mutationFn: (id: number) => leadService.acceptLead(id),
    onSuccess: () => {
      enqueueSnackbar('Lead accepted successfully!', { variant: 'success' });
      queryClient.invalidateQueries({ queryKey: ['leads'] });
    },
    onError: (err: any) => enqueueSnackbar(err.response?.data || 'Failed to accept lead', { variant: 'error' }),
  });

  const rejectMutation = useMutation({
    mutationFn: ({ id, reasonId }: { id: number; reasonId: number }) => leadService.rejectLead(id, reasonId),
    onSuccess: () => {
      enqueueSnackbar('Lead rejected successfully', { variant: 'info' });
      setRejectionDialogOpen(false);
      setRejectionReasonId('');
      queryClient.invalidateQueries({ queryKey: ['leads'] });
    },
    onError: (err: any) => enqueueSnackbar(err.response?.data || 'Failed to reject lead', { variant: 'error' }),
  });

  const handleRejectClick = (id: number) => {
    setSelectedLeadId(id);
    setRejectionDialogOpen(true);
  };

  const handleConfirmReject = () => {
    if (selectedLeadId && rejectionReasonId) {
      rejectMutation.mutate({ id: selectedLeadId, reasonId: Number(rejectionReasonId) });
    }
  };

  const formatDate = (dateStr: string | null) => {
    if (!dateStr) return '—';
    const d = new Date(dateStr);
    return isNaN(d.getTime()) ? '—' : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  const getUrgencyColor = (dateStr: string | null) => {
    if (!dateStr) return 'text.secondary';
    const d = new Date(dateStr);
    const now = new Date();
    const diff = (d.getTime() - now.getTime()) / (1000 * 60 * 60 * 24);
    if (diff < 0) return 'error.dark';
    if (diff < 3) return 'error.main';
    if (diff < 7) return 'warning.main';
    return 'success.main';
  };

  const columns: GridColDef[] = [
    {
      field: 'rfqno',
      headerName: t('rfq_management'),
      width: 200,
      renderCell: (p) => (
        <Box sx={{ py: 1.5 }}>
          <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', color: 'primary.main', fontFamily: 'monospace', letterSpacing: '-0.02em' }}>
            {p.row.rfqno || 'NO RFQ #'}
          </Typography>
          {p.row.rfqtype && (
            <Chip
              label={p.row.rfqtype}
              size="small"
              sx={{ height: 16, fontSize: '0.6rem', fontWeight: 900, bgcolor: 'primary.lighter', color: 'primary.dark', mt: 0.5 }}
            />
          )}
        </Box>
      )
    },
    {
      field: 'buyer',
      headerName: 'Buyer And Contact',
      flex: 1,
      minWidth: 200,
      renderCell: (p) => (
        <Box sx={{ py: 1.5 }}>
          <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', color: 'text.primary', mb: 0.2 }}>
            {p.row.buyersName || 'Unknown Buyer'}
          </Typography>
          <Typography variant="caption" sx={{ color: 'text.secondary', fontSize: '0.7rem', display: 'flex', alignItems: 'center', gap: 0.5 }}>
            <EmailIcon sx={{ fontSize: 12 }} /> {p.row.clientemail}
          </Typography>
          {(p.row.buyersName?.toLowerCase().includes('aramco') || p.row.buyersName?.toLowerCase().includes('sec')) && (
            <Chip
              label="KEY ACCOUNT"
              size="small"
              sx={{ height: 14, fontSize: '0.55rem', fontWeight: 900, bgcolor: 'error.main', color: 'white', mt: 0.5, borderRadius: 1 }}
            />
          )}
        </Box>
      )
    },
    {
      field: 'timelines',
      headerName: 'Dates & Deadlines',
      width: 140,
      renderCell: (p) => (
        <Box sx={{ py: 1.5 }}>
          <Box sx={{ mb: 1 }}>
            <Typography sx={{ fontSize: '0.6rem', fontWeight: 900, color: 'text.disabled', textTransform: 'uppercase' }}>Received</Typography>
            <Typography sx={{ fontSize: '0.75rem', fontWeight: 700 }}>{formatDate(p.row.recDate)}</Typography>
          </Box>
          <Box>
            <Typography sx={{ fontSize: '0.6rem', fontWeight: 900, color: 'text.disabled', textTransform: 'uppercase' }}>Deadline</Typography>
            <Typography sx={{ fontSize: '0.8rem', fontWeight: 900, color: getUrgencyColor(p.row.bidClosingDate) }}>
              {formatDate(p.row.bidClosingDate)}
            </Typography>
          </Box>
        </Box>
      )
    },
    {
      field: 'itemCount',
      headerName: t('invoice_items'),
      width: 80,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', height: '100%' }}>
          <ItemsIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 800 }}>{p.value || 0}</Typography>
        </Stack>
      )
    },
    {
      field: 'source_confidence',
      headerName: 'Source And Confidence',
      width: 180,
      renderCell: (p) => {
        const val = (p.row.aiconfidence || 0) * 100;
        const color = val > 80 ? 'success' : val > 50 ? 'warning' : 'error';
        return (
          <Box sx={{ py: 1.5, width: '100%' }}>
            <Chip
              label={p.row.leadSource}
              size="small"
              sx={{
                fontWeight: 900,
                fontSize: '0.65rem',
                height: 18,
                mb: 0.5,
                borderRadius: 1,
                color: 'white !important', // Force white text
                bgcolor: p.row.leadSource === 'Email' ? 'primary.main' : 'secondary.main'
              }}
            />
            <Typography sx={{ fontSize: '0.7rem', fontWeight: 900, color: `${color}.main`, display: 'block' }}>
              AI: {Math.round(val)}% Match
            </Typography>
            <Box sx={{ height: 3, width: '100%', bgcolor: 'action.hover', borderRadius: 1, overflow: 'hidden', mt: 0.3 }}>
              <Box sx={{ height: '100%', width: `${val}%`, bgcolor: `${color}.main` }} />
            </Box>
          </Box>
        );
      }
    },
    {
      field: 'details',
      headerName: 'Details',
      width: 110,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', height: '100%', gap: 0.5 }}>
          <Tooltip title="View Details">
            <IconButton
              size="small"
              sx={{ color: 'primary.main', bgcolor: 'primary.lighter', '&:hover': { bgcolor: 'primary.light', color: 'white' } }}
              onClick={() => navigate(`/leads/view/${p.row.id}`)}
            >
              <ViewIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Convert with AI">
            <IconButton
              size="small"
              aria-label="Convert with AI"
              sx={{ color: 'secondary.main', '&:hover': { bgcolor: 'action.hover' } }}
              onClick={() => navigate(`/procurement/leads/${p.row.id}/convert`)}
            >
              <SparkleIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Box>
      )
    },
    {
      field: 'actions',
      headerName: t('actions'),
      width: 100,
      sortable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ height: '100%', alignItems: 'center' }}>
          {!p.row.isAccepted && !p.row.isRejected ? (
            <>
              <Tooltip title="Approve">
                <IconButton size="small" sx={{ color: 'success.main', bgcolor: 'success.lighter' }} onClick={() => acceptMutation.mutate(p.row.id)}>
                  <AcceptIcon fontSize="small" />
                </IconButton>
              </Tooltip>
              <Tooltip title="Discard">
                <IconButton size="small" sx={{ color: 'error.main', bgcolor: 'error.lighter' }} onClick={() => handleRejectClick(p.row.id)}>
                  <RejectIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            </>
          ) : (
            <Chip
              label={p.row.isAccepted ? 'OK' : 'X'}
              size="small"
              color={p.row.isAccepted ? 'success' : 'error'}
              sx={{ fontWeight: 900, height: 20, fontSize: '0.65rem' }}
            />
          )}
        </Stack>
      ),
    },
  ];

  return (
    <Box sx={{ p: 3, bgcolor: 'background.default', minHeight: '100vh' }}>
      {/* Header Section */}
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em', mb: 0.5 }}>
            {t('leads')}
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Master Lead Dashboard
          </Typography>
        </Box>
        <Stack direction="row" spacing={2}>
          <Button
            variant="outlined"
            startIcon={syncEmailsMutation.isPending ? <CircularProgress size={20} color="inherit" /> : <EmailIcon />}
            onClick={() => syncEmailsMutation.mutate()}
            disabled={syncEmailsMutation.isPending}
            sx={{ fontWeight: 800, borderRadius: 2, border: '2px solid' }}
          >
            {syncEmailsMutation.isPending ? 'Getting Leads...' : 'Get New Leads'}
          </Button>
          <Tooltip title="Refresh Data">
            <IconButton onClick={() => refetch()} sx={{ bgcolor: 'white', boxShadow: 1 }}>
              <RefreshIcon />
            </IconButton>
          </Tooltip>
        </Stack>
      </Stack>

      {/* Filters */}
      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField width="360px" value={search} onChange={setSearch} placeholder="Search by RFQ No, Buyer Name..." />
        <TextField select size="small" value={leadSource} onChange={(e) => setLeadSource(e.target.value)} sx={{ minWidth: 160 }} label="Lead Source">
          <MenuItem value="all">All Sources</MenuItem>
          <MenuItem value="Email">Email</MenuItem>
          <MenuItem value="Manual">Manual</MenuItem>
          <MenuItem value="Bulk">Bulk Upload</MenuItem>
        </TextField>
      </Paper>

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 240px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        {isError ? (
          <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 2, p: 3, textAlign: 'center' }}>
            <Alert severity="error" sx={{ borderRadius: 2, maxWidth: 480 }}>
              We couldn't load leads. The service may be temporarily unavailable.
            </Alert>
            <Button variant="contained" startIcon={<RefreshIcon />} onClick={() => refetch()} sx={{ fontWeight: 700, borderRadius: 2 }}>
              Retry
            </Button>
          </Box>
        ) : (
          <DataGrid
            rows={data?.items ?? []}
            columns={columns}
            rowCount={data?.totalCount ?? 0}
            loading={isLoading}
            pageSizeOptions={[10, 25, 50]}
            paginationModel={paginationModel}
            paginationMode="server"
            onPaginationModelChange={setPaginationModel}
            disableRowSelectionOnClick
            getRowId={(r) => r.id}
            rowHeight={100}
          />
        )}
      </Paper>

      {/* Rejection Dialog */}
      <Dialog open={rejectionDialogOpen} onClose={() => setRejectionDialogOpen(false)}>
        <DialogTitle sx={{ fontWeight: 800 }}>Reject Lead</DialogTitle>
        <DialogContent dividers>
          <Typography variant="body2" sx={{ mb: 2 }}>Please select a reason for rejecting this lead.</Typography>
          <TextField
            select
            fullWidth
            label="Rejection Reason"
            value={rejectionReasonId}
            onChange={(e) => setRejectionReasonId(e.target.value)}
          >
            {reasons.map((r: any) => (
              <MenuItem key={r.id} value={r.id}>{r.reasonName}</MenuItem>
            ))}
          </TextField>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setRejectionDialogOpen(false)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            color="error"
            onClick={handleConfirmReject}
            disabled={!rejectionReasonId || rejectMutation.isPending}
          >
            Confirm Rejection
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default LeadsPage;
