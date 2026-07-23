import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Table, TableHead, TableRow, TableCell,
  TableBody, IconButton, Button, Stack, Chip, TextField,
  InputAdornment, Tooltip, CircularProgress, Grid, Alert
} from '@mui/material';
import { Refresh as RefreshIcon } from '@mui/icons-material';
import {
  Add as AddIcon,
  Visibility as ViewIcon,
  Search as SearchIcon,
  Assignment as OrderIcon,
  LocalShipping as ShipmentIcon,
  Receipt as InvoiceIcon
} from '@mui/icons-material';
import { useAuth } from '../../../context/AuthContext';
import orderService from '../../../api/services/orderService';
import dayjs from 'dayjs';
import { useSnackbar } from 'notistack';
import commercialFinanceService from '../../../api/services/commercialFinanceService';

import PermissionGuard from '../../../components/common/PermissionGuard';

const OrderListPage: React.FC = () => {
  const navigate = useNavigate();
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;
  const [searchTerm, setSearchTerm] = useState('');
  const { enqueueSnackbar } = useSnackbar();
  const createInvoice = useMutation({
    mutationFn: (orderId: number) => commercialFinanceService.createInvoiceFromOrder(orderId),
    onSuccess: (document) => navigate(`/sales/finance?documentId=${document.id}`),
    onError: (error: any) => enqueueSnackbar(error.response?.data?.detail ?? 'Invoice draft could not be created', { variant: 'error' }),
  });

  const { data: orders = [], isLoading, isError, refetch } = useQuery({
    queryKey: ['orders-list', businessUnitId, searchTerm],
    queryFn: () => orderService.getAll({ businessUnitId, search: searchTerm }),
  });

  const getStatusColor = (status: string) => {
    switch (status?.toUpperCase()) {
      case 'DRAFT': return 'default';
      case 'CONFIRMED': return 'primary';
      case 'COMPLETED': return 'success';
      case 'CANCELLED': return 'error';
      default: return 'default';
    }
  };

  const getPaymentStatusColor = (status: string) => {
    switch (status?.toUpperCase()) {
      case 'UNPAID': return 'error';
      case 'PARTIAL': return 'warning';
      case 'PAID': return 'success';
      default: return 'default';
    }
  };

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', p: 5 }}><CircularProgress /></Box>;

  if (isError) return (
    <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 2, p: 5, textAlign: 'center' }}>
      <Alert severity="error" sx={{ borderRadius: 2, maxWidth: 480 }}>
        We couldn't load orders. The service may be temporarily unavailable.
      </Alert>
      <Button variant="contained" startIcon={<RefreshIcon />} onClick={() => refetch()} sx={{ fontWeight: 700 }}>
        Retry
      </Button>
    </Box>
  );

  return (
    <Box sx={{ p: 2 }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, display: 'flex', alignItems: 'center', gap: 1 }}>
            <OrderIcon color="primary" /> Sales Orders
          </Typography>
          <Typography variant="body2" color="text.secondary">Manage customer orders and conversions</Typography>
        </Box>
        <PermissionGuard moduleName="Orders" action="create">
          <Button variant="contained" startIcon={<AddIcon />} onClick={() => navigate('/sales/orders/create')}>
            Create Order
          </Button>
        </PermissionGuard>
      </Stack>

      <Paper sx={{ p: 2, mb: 3, borderRadius: 2, boxShadow: 'none', border: '1px solid', borderColor: 'divider' }}>
        <Grid container spacing={2} sx={{ alignItems: 'center' }}>
          <Grid size={{ xs: 12, md: 6 }}>
            <TextField
              fullWidth
              size="small"
              placeholder="Search by Order # or Customer..."
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              slotProps={{
                input: {
                  startAdornment: <InputAdornment position="start"><SearchIcon fontSize="small" /></InputAdornment>
                }
              }}
            />
          </Grid>
        </Grid>
      </Paper>

      <Paper sx={{ borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <Table size="small">
          <TableHead>
            <TableRow sx={{ bgcolor: 'grey.50' }}>
              <TableCell sx={{ fontWeight: 700 }}>Order #</TableCell>
              <TableCell sx={{ fontWeight: 700 }}>Date</TableCell>
              <TableCell sx={{ fontWeight: 700 }}>Customer</TableCell>
              <TableCell sx={{ fontWeight: 700 }}>Quote #</TableCell>
              <TableCell sx={{ fontWeight: 700 }} align="right">Amount</TableCell>
              <TableCell sx={{ fontWeight: 700 }} align="center">Status</TableCell>
              <TableCell sx={{ fontWeight: 700 }} align="center">Payment</TableCell>
              <TableCell sx={{ fontWeight: 700 }} align="center">Actions</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {orders.length === 0 ? (
              <TableRow><TableCell colSpan={8} align="center" sx={{ py: 5 }}>No orders found.</TableCell></TableRow>
            ) : (
              orders.map((order) => (
                <TableRow key={order.id} hover>
                  <TableCell sx={{ fontWeight: 600 }}>{order.orderNo || order.orderNumber}</TableCell>
                  <TableCell>{dayjs(order.orderDate).format('DD MMM YYYY')}</TableCell>
                  <TableCell>{order.customerName}</TableCell>
                  <TableCell>{order.quoteNo || '-'}</TableCell>
                  <TableCell align="right" sx={{ fontWeight: 700 }}>$ {order.totalAmount.toLocaleString()}</TableCell>
                  <TableCell align="center">
                    <Chip label={order.status} size="small" color={getStatusColor(order.status) as any} variant="filled" sx={{ fontWeight: 600, minWidth: 80 }} />
                  </TableCell>
                  <TableCell align="center">
                    <Chip label={order.paymentStatus || 'UNPAID'} size="small" color={getPaymentStatusColor(order.paymentStatus || 'UNPAID') as any} variant="outlined" sx={{ fontWeight: 600, minWidth: 80 }} />
                  </TableCell>
                  <TableCell align="center">
                    <Stack direction="row" spacing={1} sx={{ justifyContent: 'center' }}>
                      <Tooltip title="View Order">
                        <IconButton size="small" color="primary" onClick={() => navigate(`/sales/orders/${order.id}`)}><ViewIcon fontSize="small" /></IconButton>
                      </Tooltip>
                      <PermissionGuard moduleName="Accounts Receivable" action="create">
                        <Tooltip title="Create invoice draft">
                          <IconButton
                            size="small"
                            color="info"
                            disabled={createInvoice.isPending}
                            onClick={() => createInvoice.mutate(order.id)}
                          >
                            <InvoiceIcon fontSize="small" />
                          </IconButton>
                        </Tooltip>
                      </PermissionGuard>
                      <PermissionGuard moduleName="Shipments" action="create">
                        {!order.hasShipments && !['Shipped', 'Delivered', 'Cancelled'].includes(order.status) && (
                          <Tooltip title="Create Shipment">
                            <IconButton 
                              size="small" 
                              color="secondary" 
                              onClick={() => navigate(`/sales/shipments/create?orderId=${order.id}`)}
                            >
                              <ShipmentIcon fontSize="small" />
                            </IconButton>
                          </Tooltip>
                        )}
                      </PermissionGuard>
                    </Stack>
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </Paper>
    </Box>
  );
};

export default OrderListPage;
