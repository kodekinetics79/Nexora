import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Tooltip, Stack,
} from '@mui/material';
import { type GridColDef, type GridPaginationModel } from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Layers as ItemsIcon,
  Add as AddIcon,
  CloudUpload as UploadIcon,
} from '@mui/icons-material';
import rfqService from '../../../api/services/rfqService';
import SearchField from '../../../components/common/SearchField';
import { useAuth } from '../../../context/AuthContext';
import PageHeader from '../../../components/common/PageHeader';
import DataTable from '../../../components/common/DataTable';
import StatusBadge from '../../../components/common/StatusBadge';

const AllRFQsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { userData } = useAuth();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');

  const { data, isLoading, refetch } = useQuery({
    queryKey: ['rfqs', paginationModel, search],
    queryFn: () => rfqService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      businessUnitId: userData?.businessUnitId || undefined,
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

  const statusColorMap: Record<number, any> = {
    34: { label: 'Draft', color: 'default' },
    35: { label: 'In Progress', color: 'primary' },
    36: { label: 'Completed', color: 'success' },
    37: { label: 'Cancelled', color: 'error' },
  };

  const columns: GridColDef[] = [
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
        const status = statusColorMap[p.row.rfqstatusId] || { label: p.row.rfqstatusValue || 'Unknown', color: 'default' };
        return (
          <StatusBadge label={status.label} tone={status.color === 'success' ? 'success' : status.color === 'error' ? 'error' : status.color === 'primary' ? 'primary' : 'warning'} />
        );
      }
    },
    {
      field: 'actions',
      headerName: t('actions'),
      width: 80,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', height: '100%' }}>
          <Tooltip title="View Details">
            <IconButton
              size="small"
              sx={{ color: 'primary.main', bgcolor: 'primary.lighter', '&:hover': { bgcolor: 'primary.light', color: 'white' } }}
              onClick={() => navigate(`/procurement/rfqs/view/${p.row.id}`)}
            >
              <ViewIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Box>
      )
    },
  ];

  return (
    <Box>
      <PageHeader
        eyebrow="RFQ command center"
        title={t('all_rfqs')}
        subtitle="Manage incoming requests, deadlines, line items, and linked customer opportunities."
        actions={
          <>
          <Button
            variant="outlined"
            startIcon={<UploadIcon />}
          >
            Upload RFQ
          </Button>
          <Button
            variant="contained"
            startIcon={<AddIcon />}
            onClick={() => navigate('/procurement/rfqs/create')}
          >
            Create RFQ
          </Button>
          <Tooltip title="Refresh Data">
            <IconButton onClick={() => refetch()} sx={{ bgcolor: 'background.paper', border: '1px solid', borderColor: 'divider' }}>
              <RefreshIcon />
            </IconButton>
          </Tooltip>
          </>
        }
      />

      {/* Filters */}
      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', flexWrap: 'wrap' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Search by RFQ No, Title, Buyer..." />
      </Paper>

      {/* Grid */}
      <DataTable
          rows={data?.items ?? []}
          columns={columns}
          rowCount={data?.totalItems ?? 0}
          loading={isLoading}
          pageSizeOptions={[10, 25, 50]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          getRowId={(r) => r.id}
          rowHeight={85}
      />
    </Box>
  );
};

export default AllRFQsPage;
