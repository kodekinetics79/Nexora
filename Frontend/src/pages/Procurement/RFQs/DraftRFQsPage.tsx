import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Tooltip, Stack, Dialog, DialogActions, DialogContent, DialogTitle,
  CircularProgress,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Delete as DeleteIcon,
  ElectricBolt as ProcessIcon,
  Layers as ItemsIcon,
} from '@mui/icons-material';
import rfqService from '../../../api/services/rfqService';
import SearchField from '../../../components/common/SearchField';
import EmailPromptDialog from '../../../components/common/EmailPromptDialog';
import { useSnackbar } from 'notistack';
import { useAuth } from '../../../context/AuthContext';
import ApiErrorNotice from '../../../components/common/ApiErrorNotice';
import { gridEmptyOverlay } from '../../../components/common/gridOverlays';
import ViewTabs from '../../../components/layout/ViewTabs';
import { formatDateSafe } from '../../../utils/dates';
import { commercialActionPermissions } from '../../../utils/commercialActionPermissions';

const DraftRFQsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { userData, hasPermission } = useAuth();
  const commercialAccess = commercialActionPermissions(hasPermission);
  const canDeleteDraftRfq = commercialAccess.canDeleteDraftRfq;
  const { enqueueSnackbar } = useSnackbar();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');

  // Approval Dialog State
  const [approvalDialogOpen, setApprovalDialogOpen] = useState(false);
  const [selectedRfq, setSelectedRfq] = useState<any>(null);
  const [deleteTarget, setDeleteTarget] = useState<{ id: number; reference: string } | null>(null);

  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: ['rfqs-draft', paginationModel, search],
    queryFn: () => rfqService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      rfqStatusCode: 'DRAFT',
      businessUnitId: userData?.businessUnitId || undefined,
    }),
  });

  const approveMutation = useMutation({
    mutationFn: (payload: { id: number; approvedBy: string; email?: string; subject?: string; body?: string; customerId?: number }) =>
      rfqService.approve(payload.id, payload.approvedBy, payload.email, payload.subject, payload.body, payload.customerId),
    onSuccess: () => {
      enqueueSnackbar('RFQ Approved and Sent successfully!', { variant: 'success' });
      setApprovalDialogOpen(false);
      setSelectedRfq(null);
      queryClient.invalidateQueries({ queryKey: ['rfqs-draft'] });
    },
    onError: () => enqueueSnackbar('Failed to approve RFQ', { variant: 'error' }),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => {
      // The dialog may have been open when an authority refresh failed or the grant was revoked.
      // Re-check at the destructive boundary; the API remains the final authority after this.
      if (!hasPermission('RFQ Management', 'delete')) {
        throw new Error('Current RFQ delete permission is required. The draft was not deleted.');
      }
      return rfqService.delete(id, userData?.businessUnitId || 0);
    },
    onSuccess: () => {
      enqueueSnackbar('RFQ Deleted', { variant: 'info' });
      setDeleteTarget(null);
      queryClient.invalidateQueries({ queryKey: ['rfqs-draft'] });
    },
    onError: () => enqueueSnackbar('The draft RFQ was not deleted.', { variant: 'error' }),
  });

  React.useEffect(() => {
    if (!canDeleteDraftRfq && deleteTarget !== null) {
      // A stale or revoked snapshot must close an already-open destructive affordance rather than
      // letting an old confirmation survive until somebody presses Enter.
      setDeleteTarget(null);
    }
  }, [canDeleteDraftRfq, deleteTarget]);

  // Memoised because DataGrid takes a component TYPE here: rebuilding the factory on every
  // render would hand it a new type each time and remount the overlay for no reason.
  const noRowsOverlay = React.useMemo(() => gridEmptyOverlay({
    title: 'No draft RFQs',
    message: 'A committed participation decision promotes approved Bid lines here as a formal draft RFQ for review.',
    icon: <ItemsIcon sx={{ fontSize: 48 }} />,
    // A clear queue with no way forward is still a dead end: the reader now knows there is nothing
    // to approve and has to work out on their own where drafts come from.
    action: (
      <Button variant="contained" onClick={() => navigate('/procurement/leads/all')} sx={{ fontWeight: 700 }}>
        See all inquiries
      </Button>
    ),
    filtered: Boolean(search),
    filteredMessage: 'No draft RFQ matches this search. Clear it to see every draft.',
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
      width: 180,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', color: 'primary.main', fontFamily: 'monospace', mb: 0.2 }}>
            {p.row.rfqno || `RFQ-${p.row.id}`}
          </Typography>
          <Box sx={{ display: 'flex' }}>
            <Chip
              label={p.row.leadId ? "From Lead" : "Manual"}
              size="small"
              sx={{ height: 16, fontSize: '0.6rem', fontWeight: 900, bgcolor: p.row.leadId ? 'warning.lighter' : 'info.lighter', color: p.row.leadId ? 'warning.dark' : 'info.dark' }}
            />
          </Box>
        </Box>
      )
    },
    {
      field: 'buyer',
      headerName: 'Buyer Details',
      flex: 1,
      minWidth: 200,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', color: 'text.primary', mb: 0.2 }}>
            {p.row.buyersName || 'Unknown Buyer'}
          </Typography>
          <Typography variant="caption" sx={{ color: 'text.secondary', fontSize: '0.7rem' }}>
            {p.row.customerEmail || p.row.leadEmail || 'No Email'}
          </Typography>
        </Box>
      )
    },
    {
      field: 'noOfLineItems',
      headerName: t('line_count'),
      width: 90,
      align: 'right',
      headerAlign: 'right',
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', justifyContent: 'flex-end', height: '100%', width: '100%' }}>
          <ItemsIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 800, fontVariantNumeric: 'tabular-nums' }}>{p.row.noOfLineItems || p.value || 0}</Typography>
        </Stack>
      )
    },
    {
      field: 'recDate',
      headerName: t('date'),
      width: 120,
      renderCell: (p) => <Typography sx={{ fontSize: '0.8rem', fontWeight: 700, py: 1.5 }}>{formatDateSafe(p.row.recDate)}</Typography>
    },
    {
      field: 'bidClosingDate',
      headerName: 'Deadline',
      width: 120,
      renderCell: (p) => <Typography sx={{ fontSize: '0.8rem', fontWeight: 700, py: 1.5, color: 'error.main' }}>{formatDateSafe(p.row.bidClosingDate)}</Typography>
    },
    {
      field: 'actions',
      headerName: t('actions'),
      width: 150,
      sortable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={1} sx={{ height: '100%', alignItems: 'center' }}>
          <Tooltip title="View">
            <IconButton size="small" onClick={() => navigate(`/procurement/rfqs/view/${p.row.id}`)}><ViewIcon fontSize="small" /></IconButton>
          </Tooltip>
          <Tooltip title="Open lifecycle">
            <IconButton size="small" sx={{ color: 'success.main', bgcolor: 'success.lighter' }} onClick={() => navigate(`/procurement/rfqs/view/${p.row.id}`)}><ProcessIcon fontSize="small" /></IconButton>
          </Tooltip>
          {!p.row.leadId && canDeleteDraftRfq && (
            <Tooltip title="Delete">
              <IconButton
                aria-label={`Delete draft RFQ ${p.row.rfqno || `RFQ-${p.row.id}`}`}
                size="small"
                sx={{ color: 'error.main', bgcolor: 'error.lighter' }}
                onClick={() => {
                  deleteMutation.reset();
                  setDeleteTarget({ id: p.row.id, reference: p.row.rfqno || `RFQ-${p.row.id}` });
                }}
              >
                <DeleteIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          )}
        </Stack>
      ),
    },
  ];

  return (
    <Box sx={{ p: 3 }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em', mb: 0.5 }}>
            {t('draft_rfqs')}
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Review and approve pending RFQ drafts
          </Typography>
        </Box>
        <Button
          variant="contained"
          color="primary"
          startIcon={<ProcessIcon />}
          onClick={() => navigate('/procurement/leads/assigned')}
          sx={{ fontWeight: 800, borderRadius: 2 }}
        >
          Review Assigned Leads
        </Button>
      </Stack>

      {/* One tab strip across the RFQ views, so Drafts reads as a view of the RFQ list rather than
          a destination of its own. It used to be a separate rail row. */}
      <ViewTabs primaryKey="rfqs" ariaLabel="RFQ views" />

      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Search draft RFQs..." />
      </Paper>

      <Paper sx={{ height: 'calc(100vh - 240px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        {/* See AllRFQsPage: an empty grid is a claim about the queue, and a failed request is
            not entitled to make it. */}
        {isError ? (
          <Box sx={{ height: '100%', display: 'grid', placeItems: 'center', p: 3 }}>
            <Box sx={{ maxWidth: 520 }}>
              <ApiErrorNotice
                error={error}
                fallbackMessage="We couldn't load draft RFQs. No empty result has been assumed."
                onRetry={() => refetch()}
              />
            </Box>
          </Box>
        ) : <DataGrid
          rows={data?.items ?? []}
          columns={columns}
          rowCount={data?.totalItems ?? 0}
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

      {selectedRfq && (
        <EmailPromptDialog
          open={approvalDialogOpen}
          initialEmail={selectedRfq.customerEmail || selectedRfq.leadEmail}
          initialSubject={`Quote for RFQ #${selectedRfq.rfqno}`}
          initialBody={`Dear Customer,\n\nPlease find the quote for your RFQ #${selectedRfq.rfqno} attached.\n\nBest Regards,\n${userData?.userName}`}
          businessUnitId={userData?.businessUnitId || 0}
          customerId={selectedRfq.customerId}
          loading={approveMutation.isPending}
          onCancel={() => setApprovalDialogOpen(false)}
          onConfirm={(email, subject, body, customerId) => {
            approveMutation.mutate({
              id: selectedRfq.id,
              approvedBy: userData?.userName || 'System',
              email,
              subject,
              body,
              customerId
            });
          }}
        />
      )}

      <Dialog
        open={deleteTarget !== null}
        onClose={() => {
          if (!deleteMutation.isPending) {
            setDeleteTarget(null);
            deleteMutation.reset();
          }
        }}
        aria-labelledby="delete-draft-rfq-title"
        aria-describedby="delete-draft-rfq-description"
        fullWidth
        maxWidth="xs"
      >
        <DialogTitle id="delete-draft-rfq-title" sx={{ fontWeight: 900 }}>
          Delete draft RFQ {deleteTarget?.reference}?
        </DialogTitle>
        <DialogContent dividers>
          <Typography id="delete-draft-rfq-description" variant="body2">
            This permanently deletes this manual draft. It cannot be undone. Lead-promoted RFQs
            cannot be deleted here because their governed promotion lineage must be retained.
          </Typography>
          {deleteMutation.isError && (
            <ApiErrorNotice
              error={deleteMutation.error}
              fallbackMessage="The draft RFQ was not deleted. Nothing changed — review your access or try again."
              sx={{ mt: 2 }}
            />
          )}
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button
            color="inherit"
            disabled={deleteMutation.isPending}
            onClick={() => {
              setDeleteTarget(null);
              deleteMutation.reset();
            }}
          >
            Cancel
          </Button>
          <Button
            variant="contained"
            color="error"
            disabled={!deleteTarget || deleteMutation.isPending}
            startIcon={deleteMutation.isPending ? <CircularProgress size={16} color="inherit" /> : <DeleteIcon />}
            onClick={() => deleteTarget && deleteMutation.mutate(deleteTarget.id)}
          >
            {deleteMutation.isPending ? 'Deleting…' : 'Delete draft permanently'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default DraftRFQsPage;
