import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Table, TableHead, TableRow, TableCell,
  TableBody, IconButton, Button, Stack, Chip, TextField,
  InputAdornment, Tooltip, CircularProgress, Grid, Alert
} from '@mui/material';
import { Refresh as RefreshIcon } from '@mui/icons-material';
import {
  Visibility as ViewIcon,
  Search as SearchIcon,
  Assignment as OrderIcon,
  LocalShipping as ShipmentIcon,
  Receipt as InvoiceIcon
} from '@mui/icons-material';
import { useAuth } from '../../../context/AuthContext';
import orderService from '../../../api/services/orderService';
import dayjs from 'dayjs';

import PermissionGuard from '../../../components/common/PermissionGuard';
import InvoiceFromOrderDialog from './InvoiceFromOrderDialog';
import { formatMoney } from '../../../utils/currency';

const OrderListPage: React.FC = () => {
  const navigate = useNavigate();
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;
  const [searchTerm, setSearchTerm] = useState('');
  /**
   * Gate 7 / FR-DLM-02. This icon used to fire the invoice call straight off the row, with
   * `lines: null`, which the server expands to the full ORDERED quantity — so after any short
   * delivery it was a guaranteed 409 against the accepted-quantity ceiling and the product had no
   * other way in. It now opens the line-level screen, which is the only place an invoice is
   * composed.
   */
  const [invoicing, setInvoicing] = useState<{ id: number; orderNo: string } | null>(null);

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
        <PermissionGuard moduleName="Customer Awards" action="view">
          <Button variant="contained" onClick={() => navigate('/sales/client-pos')}>
            Open Client PO Inbox
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
              // Was the four words "No orders found." in a table cell — identical whether the
              // business has never taken an order or a search matched nothing, and with no way
              // forward from either.
              <TableRow>
                <TableCell colSpan={8} align="center" sx={{ py: 5 }}>
                  <Typography sx={{ fontWeight: 800 }}>
                    {searchTerm ? 'No order matches this search' : 'No customer orders yet'}
                  </Typography>
                  <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, maxWidth: 460, mx: 'auto' }}>
                    {searchTerm
                      ? 'Clear the search to see every order.'
                      : 'An order is created when a customer purchase order is matched against a quote you sent.'}
                  </Typography>
                  {searchTerm
                    ? <Button variant="outlined" sx={{ mt: 2, fontWeight: 700 }} onClick={() => setSearchTerm('')}>Clear the search</Button>
                    : <Button variant="contained" sx={{ mt: 2, fontWeight: 700 }} onClick={() => navigate('/sales/client-pos')}>Open the client PO inbox</Button>}
                </TableCell>
              </TableRow>
            ) : (
              orders.map((order) => (
                <TableRow key={order.id} hover>
                  <TableCell sx={{ fontWeight: 600 }}>{order.orderNo || order.orderNumber}</TableCell>
                  <TableCell>{dayjs(order.orderDate).format('DD MMM YYYY')}</TableCell>
                  <TableCell>{order.customerName}</TableCell>
                  <TableCell>{order.quoteNo || '-'}</TableCell>
                  <TableCell align="right" sx={{ fontWeight: 700 }}>{formatMoney(order.totalAmount, order.currencyCode)}</TableCell>
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
                        <Tooltip title="Invoice what the customer accepted">
                          <IconButton
                            size="small"
                            color="info"
                            aria-label={`Invoice order ${order.orderNo || order.orderNumber}`}
                            onClick={() => setInvoicing({
                              id: order.id,
                              orderNo: order.orderNo || order.orderNumber || String(order.id),
                            })}
                          >
                            <InvoiceIcon fontSize="small" />
                          </IconButton>
                        </Tooltip>
                      </PermissionGuard>
                      <PermissionGuard moduleName="Shipments" action="create">
                        {!['SHIPPED', 'DELIVERED', 'CANCELLED'].includes(
                          order.status.replaceAll('_', '').toUpperCase(),
                        ) && (
                          <Tooltip title={order.hasShipments ? 'Create next shipment' : 'Create shipment'}>
                            <IconButton 
                              size="small" 
                              color="secondary" 
                              aria-label={`${order.hasShipments ? 'Create next shipment' : 'Create shipment'} for ${order.orderNo || order.orderNumber}`}
                              onClick={() => navigate(`/sales/shipments/from-order/${order.id}`)}
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

      {invoicing && (
        <InvoiceFromOrderDialog
          orderId={invoicing.id}
          orderNo={invoicing.orderNo}
          businessUnitId={businessUnitId}
          onClose={() => setInvoicing(null)}
          onCreated={(document) => {
            setInvoicing(null);
            navigate(`/sales/finance?documentId=${document.id}`);
          }}
        />
      )}
    </Box>
  );
};

export default OrderListPage;
