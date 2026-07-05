import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Box, Typography, Paper, Chip, IconButton,
  Tooltip, Stack, CircularProgress, Button, Divider, TextField, InputAdornment
} from '@mui/material';
import {
  DataGrid, type GridColDef
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Edit as EditIcon,
  LocalShipping as ShipmentIcon,
  Search as SearchIcon,
  Receipt as InvoiceIcon
} from '@mui/icons-material';
import shipmentService from '../../../api/services/shipmentService';
import { useAuth } from '../../../context/AuthContext';
import dayjs from 'dayjs';

import PermissionGuard from '../../../components/common/PermissionGuard';
import { Add as AddIcon } from '@mui/icons-material';

const ShipmentListPage: React.FC = () => {
  const navigate = useNavigate();
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;

  const [searchQuery, setSearchQuery] = React.useState('');

  const { data: shipments = [], isLoading, refetch } = useQuery({
    queryKey: ['shipments', businessUnitId],
    queryFn: () => shipmentService.getAll({ businessUnitId }),
  });

  const filteredShipments = React.useMemo(() => {
    return shipments.filter(s => 
      s.shipmentNo?.toLowerCase().includes(searchQuery.toLowerCase()) ||
      s.orderNo?.toLowerCase().includes(searchQuery.toLowerCase()) ||
      s.carrier?.toLowerCase().includes(searchQuery.toLowerCase()) ||
      s.trackingNumber?.toLowerCase().includes(searchQuery.toLowerCase())
    );
  }, [shipments, searchQuery]);

  const columns: GridColDef[] = [
    {
      field: 'shipmentNo',
      headerName: 'Shipment ID',
      minWidth: 180,
      flex: 1,
      renderCell: (p) => (
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', height: '100%' }}>
          <Box sx={{ p: 0.8, bgcolor: 'primary.50', borderRadius: 1.5, display: 'flex' }}>
            <ShipmentIcon sx={{ fontSize: '1.1rem', color: 'primary.main' }} />
          </Box>
          <Typography sx={{ fontWeight: 800, color: 'text.primary', fontSize: '0.85rem', letterSpacing: '0.02em' }}>
            {p.value}
          </Typography>
        </Stack>
      )
    },
    {
      field: 'orderNo',
      headerName: 'Order Ref',
      width: 180,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', height: '100%' }}>
          <Typography 
            sx={{ 
              fontWeight: 700, 
              fontSize: '0.85rem', 
              color: 'primary.main', 
              cursor: 'pointer',
              padding: '4px 10px',
              bgcolor: 'primary.50',
              borderRadius: 1,
              '&:hover': { bgcolor: 'primary.100' } 
            }}
            onClick={(e) => {
              e.stopPropagation();
              navigate(`/sales/orders/${p.row.orderId}`);
            }}
          >
            {p.value}
          </Typography>
        </Box>
      )
    },
    {
      field: 'shipmentDate',
      headerName: 'Ship Date',
      width: 140,
      renderCell: (p) => (
        <Typography sx={{ fontSize: '0.85rem', fontWeight: 500 }}>
          {dayjs(p.value).format('DD MMM YYYY')}
        </Typography>
      )
    },
    {
      field: 'carrier',
      headerName: 'Carrier & Service',
      width: 220,
      flex: 1.5,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', color: 'text.primary', lineHeight: 1.1 }}>{p.value || 'N/A'}</Typography>
          <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 600, mt: 0.2 }}>{p.row.serviceLevel || '-'}</Typography>
        </Box>
      )
    },
    {
      field: 'trackingNumber',
      headerName: 'Tracking Info',
      width: 200,
      flex: 1,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 800, fontSize: '0.8rem', fontFamily: 'JetBrains Mono, monospace', color: 'text.primary', lineHeight: 1.1 }}>
            {p.value || 'NOT ASSIGNED'}
          </Typography>
          
        </Box>
      )
    },
    {
      field: 'estimatedDeliveryDate',
      headerName: 'Est. Delivery',
      width: 150,
      renderCell: (p) => (
        <Typography sx={{ 
          fontSize: '0.85rem', 
          fontWeight: 700,
          color: dayjs(p.value).isBefore(dayjs()) && p.row.status !== 'Delivered' ? 'error.main' : 'text.primary' 
        }}>
          {p.value ? dayjs(p.value).format('DD MMM YYYY') : '-'}
        </Typography>
      )
    },
    {
      field: 'shippingCost',
      headerName: 'Cost',
      width: 120,
      align: 'right',
      headerAlign: 'right',
      renderCell: (p) => (
        <Typography sx={{ fontWeight: 800, fontSize: '0.85rem', color: 'success.main' }}>
          {p.value ? `$${p.value.toLocaleString(undefined, { minimumFractionDigits: 2 })}` : '-'}
        </Typography>
      )
    },
    {
      field: 'status',
      headerName: 'Status',
      width: 150,
      renderCell: (p) => {
        let color: any = 'default';
        if (p.value === 'Delivered') color = 'success';
        if (p.value === 'Shipped' || p.value === 'In Transit') color = 'primary';
        if (p.value === 'Pending' || p.value === 'Processing') color = 'warning';
        if (p.value === 'Cancelled' || p.value === 'Delayed') color = 'error';
        
        return (
          <Chip
            label={p.value?.toUpperCase()}
            size="small"
            color={color}
            sx={{ fontWeight: 900, height: 24, fontSize: '0.7rem', borderRadius: 1.5, px: 1 }}
          />
        );
      }
    },
    {
      field: 'actions',
      headerName: '',
      width: 140,
      sortable: false,
      align: 'right',
      renderCell: (p) => (
        <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end', alignItems: 'center', height: '100%' }}>
          <Tooltip title="Print Packing Slip">
            <IconButton size="small" color="info" onClick={() => window.open(`/sales/shipments/invoice/${p.row.id}`, '_blank')}>
              <InvoiceIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="View Detail">
            <IconButton size="small" onClick={() => navigate(`/sales/shipments/${p.row.id}`)} sx={{ color: 'text.secondary' }}>
              <ViewIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          <PermissionGuard moduleName="Shipments" action="edit">
            <Tooltip title="Edit">
              <IconButton size="small" color="primary" onClick={() => navigate(`/sales/shipments/edit/${p.row.id}`)}>
                <EditIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          </PermissionGuard>
        </Stack>
      )
    }
  ];

  return (
    <Box sx={{ p: 3, bgcolor: 'background.default', minHeight: '100vh' }}>
      {/* Header Section */}
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'flex-start', mb: 3 }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.03em', mb: 0.5, color: 'text.primary' }}>
            LOGISTICS & SHIPMENTS
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 500 }}>
            Track and manage outbound logistics across all business units
          </Typography>
        </Box>
        <Stack direction="row" spacing={2}>
          <PermissionGuard moduleName="Shipments" action="create">
            <Button 
              variant="contained" 
              startIcon={<AddIcon />} 
              onClick={() => navigate('/sales/shipments/create')}
              sx={{ borderRadius: 2, textTransform: 'none', fontWeight: 700 }}
            >
              New Shipment
            </Button>
          </PermissionGuard>
          <Tooltip title="Refresh Logistics Data">
            <Button 
              variant="outlined" 
              startIcon={<RefreshIcon />} 
              onClick={() => refetch()}
              sx={{ borderRadius: 2, textTransform: 'none', fontWeight: 700 }}
            >
              Refresh
            </Button>
          </Tooltip>
        </Stack>
      </Stack>

      {/* Filter Toolbar */}
      <Paper sx={{ p: 2, mb: 2, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', display: 'flex', gap: 2, alignItems: 'center' }}>
        <TextField
          size="small"
          placeholder="Search by Shipment #, Order #, or Carrier..."
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          sx={{ width: 400 }}
          slotProps={{
            input: {
              startAdornment: (
                <InputAdornment position="start">
                  <SearchIcon fontSize="small" />
                </InputAdornment>
              ),
            }
          }}
        />
        <Divider orientation="vertical" flexItem />
        <Stack direction="row" spacing={1}>
           <Chip label="All Shipments" color="primary" onClick={() => {}} sx={{ fontWeight: 700 }} />
           <Chip label="In Transit" variant="outlined" onClick={() => {}} sx={{ fontWeight: 700 }} />
           <Chip label="Delivered" variant="outlined" onClick={() => {}} sx={{ fontWeight: 700 }} />
           <Chip label="Overdue" variant="outlined" color="error" onClick={() => {}} sx={{ fontWeight: 700 }} />
        </Stack>
      </Paper>

      {/* Grid Container */}
      <Paper sx={{ width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        {isLoading ? (
          <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', alignItems: 'center', height: '100%', gap: 2 }}>
            <CircularProgress size={40} thickness={4} />
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>LOADING LOGISTICS DATA...</Typography>
          </Box>
        ) : (
          <DataGrid
            autoHeight
            rows={filteredShipments}
            columns={columns}
            pageSizeOptions={[10, 25, 50]}
            initialState={{
              pagination: { paginationModel: { pageSize: 10 } },
            }}
            disableRowSelectionOnClick
            getRowId={(r) => r.id}
            rowHeight={75}
            sx={{
              border: 'none',
              '& .MuiDataGrid-columnHeaders': {
                bgcolor: 'grey.50',
                borderBottom: '1px solid',
                borderColor: 'divider',
              },
              '& .MuiDataGrid-columnHeaderTitle': {
                fontWeight: 800,
                fontSize: '0.75rem',
                textTransform: 'uppercase',
                letterSpacing: '0.05em',
                color: 'text.secondary',
              },
              '& .MuiDataGrid-cell': {
                borderBottom: '1px solid',
                borderColor: 'grey.100',
                display: 'flex',
                alignItems: 'center',
              },
              '& .MuiDataGrid-row:hover': {
                bgcolor: 'primary.50',
                cursor: 'pointer'
              }
            }}
          />
        )}
      </Paper>
    </Box>
  );
};

export default ShipmentListPage;
