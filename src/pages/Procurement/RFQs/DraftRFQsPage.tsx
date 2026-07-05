import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Tooltip, Stack,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  CheckCircle as ApproveIcon,
  Delete as DeleteIcon,
  ElectricBolt as ProcessIcon,
  Layers as ItemsIcon,
} from '@mui/icons-material';
import rfqService from '../../../api/services/rfqService';
import SearchField from '../../../components/common/SearchField';
import EmailPromptDialog from '../../../components/common/EmailPromptDialog';
import { useSnackbar } from 'notistack';
import { useAuth } from '../../../context/AuthContext';

const DraftRFQsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { userData } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');

  // Approval Dialog State
  const [approvalDialogOpen, setApprovalDialogOpen] = useState(false);
  const [selectedRfq, setSelectedRfq] = useState<any>(null);

  const { data, isLoading } = useQuery({
    queryKey: ['rfqs-draft', paginationModel, search],
    queryFn: () => rfqService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      rfqStatusId: 34, // Draft
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
    mutationFn: (id: number) => rfqService.delete(id, userData?.businessUnitId || 0),
    onSuccess: () => {
      enqueueSnackbar('RFQ Deleted', { variant: 'info' });
      queryClient.invalidateQueries({ queryKey: ['rfqs-draft'] });
    },
    onError: () => enqueueSnackbar('Failed to delete RFQ', { variant: 'error' }),
  });

  const handleApproveClick = (rfq: any) => {
    setSelectedRfq(rfq);
    setApprovalDialogOpen(true);
  };

  const formatDate = (dateStr: string | null) => {
    if (!dateStr) return '—';
    const d = new Date(dateStr);
    return isNaN(d.getTime()) ? '—' : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  const columns: GridColDef[] = [
    {
      field: 'rfqno',
      headerName: t('rfq_management'),
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
      headerName: t('invoice_items'),
      width: 80,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', height: '100%' }}>
          <ItemsIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 800 }}>{p.row.noOfLineItems || p.value || 0}</Typography>
        </Stack>
      )
    },
    {
      field: 'recDate',
      headerName: t('date'),
      width: 120,
      renderCell: (p) => <Typography sx={{ fontSize: '0.8rem', fontWeight: 700, py: 1.5 }}>{formatDate(p.row.recDate)}</Typography>
    },
    {
      field: 'bidClosingDate',
      headerName: 'Deadline',
      width: 120,
      renderCell: (p) => <Typography sx={{ fontSize: '0.8rem', fontWeight: 700, py: 1.5, color: 'error.main' }}>{formatDate(p.row.bidClosingDate)}</Typography>
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
          <Tooltip title="Approve & Send">
            <IconButton size="small" sx={{ color: 'success.main', bgcolor: 'success.lighter' }} onClick={() => handleApproveClick(p.row)}><ApproveIcon fontSize="small" /></IconButton>
          </Tooltip>
          {!p.row.leadId && (
            <Tooltip title="Delete">
              <IconButton size="small" sx={{ color: 'error.main', bgcolor: 'error.lighter' }} onClick={() => deleteMutation.mutate(p.row.id)}><DeleteIcon fontSize="small" /></IconButton>
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
          onClick={() => navigate('/procurement/rfqs/outstanding')}
          sx={{ fontWeight: 800, borderRadius: 2 }}
        >
          Process Outstanding Leads
        </Button>
      </Stack>

      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Search draft RFQs..." />
      </Paper>

      <Paper sx={{ width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid
          autoHeight
          rows={data?.items ?? []}
          columns={columns}
          rowCount={data?.totalItems ?? 0}
          loading={isLoading}
          pageSizeOptions={[10, 25, 50]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          disableRowSelectionOnClick
          getRowId={(r) => r.id}
          rowHeight={85}
        />
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
    </Box>
  );
};

export default DraftRFQsPage;
