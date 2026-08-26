import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, IconButton,
  Stack, Dialog, DialogTitle, DialogContent,
  DialogActions, TextField, MenuItem, Chip,
  Tooltip, Alert,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Email as EmailIcon,
  Layers as ItemsIcon,
} from '@mui/icons-material';
import leadService, { assignabilityNote } from '../../../api/services/leadService';
import SearchField from '../../../components/common/SearchField';
import { useSnackbar } from 'notistack';
import { useAuth } from '../../../context/AuthContext';
import ApiErrorNotice from '../../../components/common/ApiErrorNotice';
import { gridEmptyOverlay } from '../../../components/common/gridOverlays';
import ViewTabs from '../../../components/layout/ViewTabs';
import { formatDateSafe } from '../../../utils/dates';

const OutstandingRFQsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { userData } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');

  const getUrgencyColor = (dateStr: string | null) => {
    if (!dateStr) return 'text.secondary';
    const deadline = new Date(dateStr);
    const now = new Date();
    const diffDays = Math.ceil((deadline.getTime() - now.getTime()) / (1000 * 60 * 60 * 24));
    if (diffDays < 3) return 'error.main';
    if (diffDays < 7) return 'warning.main';
    return 'success.main';
  };

  // Assignment Dialog State
  const [assignDialogOpen, setAssignDialogOpen] = useState(false);
  const [selectedAcceptedLeadId] = useState<number | null>(null);
  const [assignToUserId, setAssignToUserId] = useState<string>('');
  const [assignComment, setAssignComment] = useState('');

  const isAdminOrManager = userData?.isManager === true || userData?.isSuperAdmin === true;

  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: ['rfqs-outstanding', paginationModel, search],
    queryFn: () => leadService.getOutstandingLeads({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      businessUnitId: userData?.businessUnitId,
      assignedToId: isAdminOrManager ? undefined : userData?.id,
      onlyAssigned: true,
    }),
  });

  const { data: users = [] } = useQuery({
    queryKey: ['assignment-users'],
    queryFn: () => leadService.getUsersForAssignment(userData?.businessUnitId || 0),
    enabled: !!userData.businessUnitId,
  });

  const assignMutation = useMutation({
    mutationFn: (data: { leadId: number; assignedToUserId: number; comment?: string }) => leadService.assignLead(data),
    onSuccess: () => {
      enqueueSnackbar('Lead assigned successfully!', { variant: 'success' });
      setAssignDialogOpen(false);
      setAssignToUserId('');
      setAssignComment('');
      queryClient.invalidateQueries({ queryKey: ['rfqs-outstanding'] });
    },
    onError: (err: any) => enqueueSnackbar(err.response?.data?.error || 'Failed to assign lead', { variant: 'error' }),
  });

  const handleConfirmAssign = () => {
    if (selectedAcceptedLeadId && assignToUserId) {
      assignMutation.mutate({
        leadId: selectedAcceptedLeadId,
        assignedToUserId: Number(assignToUserId),
        comment: assignComment || undefined,
      });
    }
  };

  // Memoised because DataGrid takes a component TYPE here: rebuilding the factory on every
  // render would hand it a new type each time and remount the overlay for no reason.
  const noRowsOverlay = React.useMemo(() => gridEmptyOverlay({
    title: 'No outstanding RFQs',
    message: 'An assigned lead appears here until it has been processed into an RFQ.',
    icon: <ItemsIcon sx={{ fontSize: 48 }} />,
    action: (
      <Button variant="contained" onClick={() => navigate('/procurement/leads/assigned')} sx={{ fontWeight: 700 }}>
        See assigned inquiries
      </Button>
    ),
    filtered: Boolean(search),
    filteredMessage: 'No outstanding RFQ matches this search. Clear it to see the whole queue.',
    filteredAction: (
      <Button variant="outlined" onClick={() => setSearch('')} sx={{ fontWeight: 700 }}>
        Clear the search
      </Button>
    ),
  }), [search, navigate]);

  const columns: GridColDef[] = [
    {
      field: 'rfqno',
      headerName: t('rfq_number'),
      width: 200,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', color: 'primary.main', fontFamily: 'monospace' }}>
            {p.row.rfqno || 'NO RFQ #'}
          </Typography>
          <Chip
            label={p.row.leadSource || "Lead"}
            size="small"
            sx={{ height: 16, fontSize: '0.6rem', fontWeight: 900, bgcolor: 'primary.lighter', color: 'primary.dark', mt: 0.5 }}
          />
        </Box>
      )
    },
    {
      field: 'buyer',
      headerName: 'Buyer And Contact',
      flex: 1,
      minWidth: 200,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', color: 'text.primary', mb: 0.2 }}>
            {p.row.buyersName || 'Unknown Buyer'}
          </Typography>
          <Typography variant="caption" sx={{ color: 'text.secondary', fontSize: '0.7rem', display: 'flex', alignItems: 'center', gap: 0.5 }}>
            <EmailIcon sx={{ fontSize: 12 }} /> {p.row.clientemail}
          </Typography>
        </Box>
      )
    },
    {
      field: 'noOfLineItems',
      headerName: t('line_count'),
      width: 110,
      // A count is a number and belongs on the right, where digits line up against the column
      // edge and magnitudes can be compared down the column. The figures themselves come from
      // the MuiCssBaseline block in context/ThemeContext.tsx, which is the theme that is mounted.
      align: 'right',
      headerAlign: 'right',
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', justifyContent: 'flex-end', height: '100%', width: '100%' }}>
          <ItemsIcon sx={{ fontSize: 16, color: 'text.secondary' }} />
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 800, fontVariantNumeric: 'tabular-nums' }}>{p.row.noOfLineItems || 0}</Typography>
        </Stack>
      )
    },
    // An "AI Confidence — N% Match" column used to sit here. That number is not
    // a measured accuracy and is no longer rendered anywhere in the product.
    {
      field: 'recDate',
      headerName: 'Received',
      width: 120,
      renderCell: (p) => <Typography sx={{ fontSize: '0.8rem', fontWeight: 700, py: 1.5 }}>{formatDateSafe(p.row.recDate)}</Typography>
    },
    {
      field: 'bidClosingDate',
      headerName: 'Deadline',
      width: 120,
      renderCell: (p) => (
        <Typography sx={{ fontSize: '0.85rem', fontWeight: 900, color: getUrgencyColor(p.row.bidClosingDate) }}>
          {formatDateSafe(p.row.bidClosingDate)}
        </Typography>
      )
    },
    {
      field: 'actions',
      headerName: t('actions'),
      width: 64,
      sortable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={1} sx={{ height: '100%', alignItems: 'center' }}>
          <Tooltip title="View Lead">
            <IconButton
              size="small"
              sx={{ color: 'primary.main', bgcolor: 'primary.lighter', '&:hover': { bgcolor: 'primary.light', color: 'white' } }}
              onClick={() => navigate(`/procurement/leads/view/${p.row.id}`)}
            >
              <ViewIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      ),
    },
  ];

  return (
    <Box sx={{ p: 3 }}>
      <Box sx={{ mb: 3, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 950, letterSpacing: '-0.02em', mb: 0.5 }}>{t('outstanding_rfqs')}</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>Accepted leads waiting for further processing</Typography>
        </Box>
        <Button variant="outlined" startIcon={<RefreshIcon />} onClick={() => refetch()} size="small" sx={{ fontWeight: 800 }}>Refresh</Button>
      </Box>

      <ViewTabs primaryKey="rfqs" ariaLabel="RFQ views" />

      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Filter outstanding RFQs..." />
      </Paper>

      <Paper sx={{ height: 'calc(100vh - 240px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        {/* "No outstanding RFQs" is a decision a salesperson acts on, so it must never be what a
            failed request looks like. Matches AllRFQsPage, the sibling screen that already did
            this correctly. */}
        {isError ? (
          <Box sx={{ height: '100%', display: 'grid', placeItems: 'center', p: 3 }}>
            <Box sx={{ maxWidth: 520 }}>
              <ApiErrorNotice
                error={error}
                fallbackMessage="We couldn't load outstanding RFQs. No empty result has been assumed."
                onRetry={() => refetch()}
              />
            </Box>
          </Box>
        ) : <DataGrid
          rows={data?.items ?? []}
          columns={columns}
          rowCount={data?.totalCount ?? 0}
          loading={isLoading}
          slots={{ noRowsOverlay }}
          pageSizeOptions={[10, 25, 50]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          disableRowSelectionOnClick
          getRowId={(r) => r.id}
          rowHeight={85}
        />}
      </Paper>

      <Dialog open={assignDialogOpen} onClose={() => setAssignDialogOpen(false)} fullWidth>
        <DialogTitle sx={{ fontWeight: 900, fontSize: '1rem' }}>Assign Lead</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={3} sx={{ mt: 1 }}>
            <TextField
              select
              fullWidth
              label="Assign To User"
              value={assignToUserId}
              onChange={(e) => setAssignToUserId(e.target.value)}
            >
              {users.map((u) => (
                <MenuItem key={u.id} value={u.id} disabled={!u.isEligibleForAssignment}>
                  {u.fullName} &mdash; {assignabilityNote(u)}
                </MenuItem>
              ))}
            </TextField>
            {users.length > 0 && !users.some((u) => u.isEligibleForAssignment) && (
              <Alert severity="warning" sx={{ borderRadius: 2 }}>
                Nobody in this business unit can currently receive a lead. Governed routing only
                accepts a user who has an effective Sales Rep profile with capacity left, and there
                is no such user right now. Give someone a profile in Sales &gt; Rep directory.
              </Alert>
            )}
            <TextField
              fullWidth
              multiline
              rows={3}
              label="Comment"
              value={assignComment}
              onChange={(e) => setAssignComment(e.target.value)}
            />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setAssignDialogOpen(false)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            onClick={handleConfirmAssign}
            disabled={!assignToUserId || assignMutation.isPending}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Confirm
          </Button>
        </DialogActions>
      </Dialog>

    </Box>
  );
};

export default OutstandingRFQsPage;
