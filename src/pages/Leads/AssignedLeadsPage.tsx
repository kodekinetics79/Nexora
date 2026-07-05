import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, IconButton,
  Tooltip, Chip, Stack,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Email as EmailIcon,
  Layers as ItemsIcon,
  Person as UserIcon,
} from '@mui/icons-material';
import leadService from '../../api/services/leadService';
import SearchField from '../../components/common/SearchField';

const AssignedLeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');

  const formatDate = (dateStr: string | null) => {
    if (!dateStr) return '—';
    const d = new Date(dateStr);
    return isNaN(d.getTime()) ? '—' : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  const getUrgencyColor = (dateStr: string | null) => {
    if (!dateStr) return 'text.secondary';
    const deadline = new Date(dateStr);
    const now = new Date();
    const diffDays = Math.ceil((deadline.getTime() - now.getTime()) / (1000 * 60 * 60 * 24));
    if (diffDays < 3) return 'error.main';
    if (diffDays < 7) return 'warning.main';
    return 'success.main';
  };

  const { data, isLoading, refetch } = useQuery({
    queryKey: ['leads-assigned', paginationModel, search],
    queryFn: () => leadService.getAssignedLeads({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
    }),
  });

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
      field: 'assignee',
      headerName: 'Assigned Specialist',
      width: 180,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <UserIcon sx={{ fontSize: 14, color: p.row.assignedToFullName ? 'primary.main' : 'error.main' }} />
            <Typography sx={{ fontWeight: 800, fontSize: '0.85rem', color: p.row.assignedToFullName ? 'text.primary' : 'error.main' }}>
              {p.row.assignedToFullName || 'Unassigned'}
            </Typography>
          </Stack>
          <Typography variant="caption" sx={{ fontSize: '0.65rem', fontWeight: 700, color: 'text.disabled', textTransform: 'uppercase' }}>
            Since: {formatDate(p.row.assignedOn)}
          </Typography>
        </Box>
      )
    },
    {
      field: 'recDate',
      headerName: t('date'),
      width: 120,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontSize: '0.8rem', fontWeight: 700 }}>{formatDate(p.row.recDate)}</Typography>
          <Typography variant="caption" sx={{ fontSize: '0.65rem', fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase' }}>Accepted: {formatDate(p.row.acceptedDate)}</Typography>
        </Box>
      )
    },
    {
      field: 'bidClosingDate',
      headerName: 'Deadline',
      width: 120,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 900, color: getUrgencyColor(p.row.bidClosingDate) }}>
            {formatDate(p.row.bidClosingDate)}
          </Typography>
          <Typography variant="caption" sx={{ fontSize: '0.65rem', fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase' }}>Submission</Typography>
        </Box>
      )
    },
    {
      field: 'aiconfidence',
      headerName: 'AI Match',
      width: 140,
      renderCell: (p) => {
        const val = (p.row.aiconfidence || 0) * 100;
        const color = val > 80 ? 'success' : val > 50 ? 'warning' : 'error';
        return (
          <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%', width: '100%' }}>
            <Typography sx={{ fontSize: '0.7rem', fontWeight: 900, color: `${color}.main`, mb: 0.5 }}>
              {Math.round(val)}% Accurate
            </Typography>
            <Box sx={{ height: 4, width: '100%', bgcolor: 'action.hover', borderRadius: 2, overflow: 'hidden' }}>
              <Box sx={{ height: '100%', width: `${val}%`, bgcolor: `${color}.main` }} />
            </Box>
          </Box>
        );
      }
    },
    {
      field: 'itemCount',
      headerName: t('invoice_items'),
      width: 80,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', height: '100%' }}>
          <ItemsIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 800 }}>{p.row.itemCount || 0}</Typography>
        </Stack>
      )
    },
    {
      field: 'actions',
      headerName: 'Details',
      width: 100,
      sortable: false,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', height: '100%' }}>
          <Tooltip title="View Detailed Intelligence">
            <IconButton
              size="small"
              sx={{ color: 'primary.main', bgcolor: 'primary.lighter', '&:hover': { bgcolor: 'primary.light', color: 'white' } }}
              onClick={() => navigate(`/leads/view/${p.row.id}`)}
            >
              <ViewIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Box>
      ),
    },
  ];

  return (
    <Box sx={{ width: '100%', p: 2 }}>
      {/* Header */}
      <Box sx={{ mb: 3, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 950, letterSpacing: '-0.02em', mb: 0.5 }}>{t('assigned_leads')}</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>Leads currently being processed by the sales team</Typography>
        </Box>
        <Button variant="outlined" startIcon={<RefreshIcon />} onClick={() => refetch()} size="small" sx={{ fontWeight: 800 }}>Refresh Dashboard</Button>
      </Box>

      {/* Search */}
      <Paper sx={{ p: 2, mb: 2, borderRadius: 3, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Filter assigned leads..." />
      </Paper>

      {/* Grid */}
      <Paper sx={{ width: '100%', borderRadius: 3, overflow: 'hidden', border: '1px solid', borderColor: 'divider', boxShadow: '0 4px 20px rgba(0,0,0,0.03)' }}>
        <DataGrid
          autoHeight
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
          rowHeight={95}
          sx={{
            border: 'none',
            '& .MuiDataGrid-columnHeaders': {
              bgcolor: 'action.hover',
              borderBottom: '1px solid',
              borderColor: 'divider',
            },
            '& .MuiDataGrid-cell': {
              borderBottom: '1px solid',
              borderColor: 'action.hover',
            }
          }}
        />
      </Paper>
    </Box>
  );
};

export default AssignedLeadsPage;
