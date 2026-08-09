import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Tooltip, Stack, Alert,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Layers as ItemsIcon,
  CloudUpload as UploadIcon,
} from '@mui/icons-material';
import rfqService from '../../../api/services/rfqService';
import SearchField from '../../../components/common/SearchField';
import { useAuth } from '../../../context/AuthContext';

const AllRFQsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const readiness = searchParams.get('state') || undefined;
  const { userData, hasPermission } = useAuth();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['rfqs', paginationModel, search, readiness],
    queryFn: () => rfqService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      businessUnitId: userData?.businessUnitId || undefined,
      readiness,
    }),
  });

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
      field: 'nexoraSerial',
      headerName: 'Nexora Serial',
      width: 180,
      // No fallback through the lead or RFQ. The API used to substitute the parent's case when
      // this document carried none, so a document outside the commercial case displayed one
      // anyway. A blank here is real, and "Not linked" says so rather than reading as a
      // still-loading cell.
      valueGetter: (_value, row) => row.nexoraSerial || '',
      renderCell: (p) => (
        <Typography sx={{ fontWeight: 800, fontFamily: 'monospace', fontSize: '0.8rem', color: p.value ? 'primary.main' : 'warning.main' }}>
          {p.value || 'Not linked'}
        </Typography>
      ),
    },
    {
      field: 'rfqno',
      headerName: t('rfq_management'),
      width: 180,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', color: 'primary.main', fontFamily: 'monospace', letterSpacing: '-0.02em', mb: 0.2 }}>
            {p.row.rfqno || `RFQ-${p.row.id}`}
          </Typography>
          {p.row.leadId && (
            <Box sx={{ display: 'flex' }}>
              <Chip
                label="From Lead"
                size="small"
                sx={{ height: 16, fontSize: '0.6rem', fontWeight: 900, bgcolor: 'warning.lighter', color: 'warning.dark' }}
              />
            </Box>
          )}
        </Box>
      )
    },
    {
      field: 'buyer',
      headerName: 'Buyer And Customer',
      flex: 1,
      minWidth: 200,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', color: 'text.primary', mb: 0.2 }}>
            {p.row.buyersName || 'Unknown Buyer'}
          </Typography>
          <Typography variant="caption" sx={{ color: 'text.secondary', fontSize: '0.7rem', display: 'flex', alignItems: 'center', gap: 0.5 }}>
            {p.row.customerName || 'No Customer Linked'}
          </Typography>
        </Box>
      )
    },
    {
      field: 'noOfLineItems',
      headerName: 'RFQ Lines',
      width: 130,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', height: '100%' }}>
          <ItemsIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 800 }}>{p.value || 0}</Typography>
        </Stack>
      )
    },
    {
      field: 'timelines',
      headerName: t('date'),
      width: 260,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', height: '100%', gap: 2 }}>
          <Box>
            <Typography sx={{ fontSize: '0.65rem', fontWeight: 900, color: 'text.disabled', textTransform: 'uppercase' }}>Received</Typography>
            <Typography sx={{ fontSize: '0.8rem', fontWeight: 700 }}>{formatDate(p.row.recDate)}</Typography>
          </Box>
          <Box sx={{ borderLeft: '1px solid', borderColor: 'divider', height: 24 }} />
          <Box>
            <Typography sx={{ fontSize: '0.65rem', fontWeight: 900, color: 'text.disabled', textTransform: 'uppercase' }}>Deadline</Typography>
            <Typography sx={{ fontSize: '0.85rem', fontWeight: 900, color: getUrgencyColor(p.row.bidClosingDate) }}>
              {formatDate(p.row.bidClosingDate)}
            </Typography>
          </Box>
        </Box>
      )
    },
    {
      field: 'rfqstatusValue',
      headerName: t('status'),
      width: 120,
      renderCell: (p) => {
        const status = { label: p.row.rfqstatusValue || 'Unknown', color: 'default' as const };
        return (
          <Chip
            label={status.label}
            size="small"
            color={status.color}
            sx={{ fontWeight: 900, height: 22, fontSize: '0.7rem', borderRadius: 1.5 }}
          />
        );
      }
    },
    {
      field: 'actions',
      headerName: t('actions'),
      width: 80,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', height: '100%' }}>
          <Button size="small" startIcon={<ViewIcon />} onClick={() => navigate(`/procurement/rfqs/view/${p.row.id}`)}>
            Open RFQ
          </Button>
        </Box>
      )
    },
  ];

  return (
    <Box sx={{ p: 3, bgcolor: 'background.default', minHeight: '100vh' }}>
      {/* Header Section */}
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em', mb: 0.5 }}>
            {t('all_rfqs')}
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Manage and track all Request for Quotations
          </Typography>
        </Box>
        <Stack direction="row" spacing={2}>
          {hasPermission('RFQ Management', 'create') && <Button
            variant="outlined"
            startIcon={<UploadIcon />}
            onClick={() => navigate('/procurement/leads/manual-upload')}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Ingest RFQ
          </Button>}
          <Tooltip title="Refresh Data">
            <IconButton onClick={() => refetch()} sx={{ bgcolor: 'white', boxShadow: 1 }}>
              <RefreshIcon />
            </IconButton>
          </Tooltip>
        </Stack>
      </Stack>

      {/* Filters */}
      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Search Nexora Serial, RFQ, customer or buyer" />
      </Paper>

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 240px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        {isError ? (
          <Box sx={{ height: '100%', display: 'grid', placeItems: 'center', p: 3 }}>
            <Stack spacing={2} sx={{ alignItems: 'center', maxWidth: 480 }}>
              <Alert severity="error">We couldn't load RFQs. No empty result has been assumed.</Alert>
              <Button variant="contained" startIcon={<RefreshIcon />} onClick={() => refetch()}>Retry</Button>
            </Stack>
          </Box>
        ) : <DataGrid
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
        />}
      </Paper>
    </Box>
  );
};

export default AllRFQsPage;
