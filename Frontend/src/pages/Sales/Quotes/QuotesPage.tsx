import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Tooltip, Stack, Alert, Snackbar
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  PictureAsPdf as PdfIcon,
  Email as EmailIcon,
  Delete as DeleteIcon,
  Add as AddIcon,
} from '@mui/icons-material';
import quoteService from '../../../api/services/quoteService';
import SearchField from '../../../components/common/SearchField';
import { useAuth } from '../../../context/AuthContext';
import dayjs from 'dayjs';

const QuotesPage: React.FC = () => {
  const { t: _t } = useTranslation();
  const navigate = useNavigate();
  const { userData } = useAuth();
  const queryClient = useQueryClient();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');
  const [snackbar, setSnackbar] = useState<{ open: boolean; message: string; severity: 'success' | 'error' }>({
    open: false,
    message: '',
    severity: 'success'
  });

  const { data, isLoading, refetch } = useQuery({
    queryKey: ['quotes', paginationModel, search],
    queryFn: () => quoteService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      businessUnitId: userData?.businessUnitId || undefined,
    }),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => quoteService.delete(id, userData?.businessUnitId),
    onSuccess: () => {
      setSnackbar({ open: true, message: 'Quote deleted successfully', severity: 'success' });
      queryClient.invalidateQueries({ queryKey: ['quotes'] });
    },
    onError: () => {
      setSnackbar({ open: true, message: 'Failed to delete quote', severity: 'error' });
    }
  });

  const handleDownloadPdf = async (id: number, quoteNo: string) => {
    try {
      const blob = await quoteService.downloadPdf(id);
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `Quote_${quoteNo}.pdf`;
      document.body.appendChild(a);
      a.click();
      window.URL.revokeObjectURL(url);
    } catch (error) {
      setSnackbar({ open: true, message: 'Failed to download PDF', severity: 'error' });
    }
  };

  const columns: GridColDef[] = [
    {
      field: 'quoteNo',
      headerName: 'Quote Number',
      width: 180,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', color: 'primary.main', fontFamily: 'monospace', letterSpacing: '-0.02em' }}>
            {p.row.quoteNo}
          </Typography>
          {p.row.rfqNo && (
            <Typography variant="caption" sx={{ color: 'text.secondary', fontSize: '0.7rem' }}>
              Ref: {p.row.rfqNo}
            </Typography>
          )}
        </Box>
      )
    },
    {
      field: 'customerName',
      headerName: 'Customer',
      flex: 1,
      minWidth: 200,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', color: 'text.primary' }}>
            {p.row.customerName || 'Walk-in Customer'}
          </Typography>
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            {p.row.customerEmail || 'No email provided'}
          </Typography>
        </Box>
      )
    },
    {
      field: 'totalAmount',
      headerName: 'Total Amount',
      width: 150,
      renderCell: (p) => (
        <Typography sx={{ fontWeight: 800, color: 'text.primary', fontSize: '0.9rem' }}>
          {p.row.currencyCode || '$'} {p.value?.toLocaleString(undefined, { minimumFractionDigits: 2 })}
        </Typography>
      )
    },
    {
      field: 'quoteDate',
      headerName: 'Date',
      width: 150,
      valueFormatter: (p: any) => dayjs(p).format('DD MMM YYYY')
    },
    {
      field: 'statusValue',
      headerName: 'Status',
      width: 120,
      renderCell: (p) => (
        <Chip
          label={p.value}
          size="small"
          sx={{ 
            fontWeight: 900, 
            height: 24, 
            fontSize: '0.7rem',
            bgcolor: p.value === 'Sent' ? 'success.lighter' : 'action.hover',
            color: p.value === 'Sent' ? 'success.main' : 'text.secondary',
            border: '1px solid',
            borderColor: p.value === 'Sent' ? 'success.light' : 'divider'
          }}
        />
      )
    },
    {
      field: 'actions',
      headerName: 'Actions',
      width: 180,
      sortable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', height: '100%' }}>
          <Tooltip title="View">
            <IconButton size="small" onClick={() => navigate(`/sales/quotes/view/${p.row.id}`)}>
              <ViewIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Download PDF">
            <IconButton size="small" onClick={() => handleDownloadPdf(p.row.id, p.row.quoteNo)}>
              <PdfIcon fontSize="small" color="error" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Email">
            <IconButton size="small">
              <EmailIcon fontSize="small" color="primary" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Delete">
            <IconButton size="small" onClick={() => {
              if (window.confirm('Are you sure you want to delete this quote?')) {
                deleteMutation.mutate(p.row.id);
              }
            }}>
              <DeleteIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      )
    },
  ];

  return (
    <Box sx={{ p: 3, bgcolor: 'background.default', minHeight: '100vh' }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em', mb: 0.5 }}>
            Quote Management
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Manage sales quotations and customer offers
          </Typography>
        </Box>
        <Stack direction="row" spacing={2}>
          <Button
            variant="contained"
            startIcon={<AddIcon />}
            onClick={() => navigate('/sales/quotes/create')}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Create Quote
          </Button>
          <Tooltip title="Refresh Data">
            <IconButton onClick={() => refetch()} sx={{ bgcolor: 'white', boxShadow: 1 }}>
              <RefreshIcon />
            </IconButton>
          </Tooltip>
        </Stack>
      </Stack>

      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Search by Quote No, Customer, RFQ No..." />
      </Paper>

      <Paper sx={{ height: 'calc(100vh - 240px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid
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
          rowHeight={70}
        />
      </Paper>

      <Snackbar 
        open={snackbar.open} 
        autoHideDuration={6000} 
        onClose={() => setSnackbar({ ...snackbar, open: false })}
      >
        <Alert severity={snackbar.severity} sx={{ width: '100%' }}>
          {snackbar.message}
        </Alert>
      </Snackbar>
    </Box>
  );
};

export default QuotesPage;
