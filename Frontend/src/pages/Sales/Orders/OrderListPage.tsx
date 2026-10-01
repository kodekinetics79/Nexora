import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Table, TableHead, TableRow, TableCell,
  TableBody, TableContainer, IconButton, Button, Stack, Chip, TextField,
  InputAdornment, Tooltip, CircularProgress, Grid, Alert
} from '@mui/material';
import { CloudUpload as UploadIcon, Refresh as RefreshIcon } from '@mui/icons-material';
import {
  Visibility as ViewIcon,
  Search as SearchIcon,
  Assignment as OrderIcon,
  LocalShipping as ShipmentIcon,
  Receipt as InvoiceIcon
} from '@mui/icons-material';
import { useAuth } from '../../../context/AuthContext';
import orderService, { type OrderDTO } from '../../../api/services/orderService';
import ExportExcelButton, { type ExportColumn } from '../../../components/common/ExportExcelButton';
import dayjs from 'dayjs';

import PermissionGuard from '../../../components/common/PermissionGuard';
import InvoiceFromOrderDialog from './InvoiceFromOrderDialog';
import { formatMoney } from '../../../utils/currency';
import { RELEASE_SCOPE } from '../../../config/releaseScope';
import ClientPoUploadFlow from '../ClientPurchaseOrders/ClientPoUploadFlow';

const day = (value?: string | null) => (value ? dayjs(value).format('DD MMM YYYY') : '');

const ORDER_EXPORT_COLUMNS: ExportColumn<OrderDTO>[] = [
  { header: 'Order #', value: (o) => o.orderNo || o.orderNumber },
  { header: 'Client PO #', value: (o) => o.clientPoNumber },
  { header: 'Nexora Serial', value: (o) => o.nexoraSerial },
  { header: 'Date', value: (o) => day(o.orderDate) },
  { header: 'Delivery date', value: (o) => day(o.deliveryDate) },
  { header: 'Customer', value: (o) => o.customerName },
  { header: 'Quote #', value: (o) => o.quoteNo },
  { header: 'RFQ #', value: (o) => o.rfqNo },
  { header: 'Lead #', value: (o) => o.leadNo },
  { header: 'Status', value: (o) => o.status },
  { header: 'Currency', value: (o) => o.currencyCode },
  { header: 'Subtotal', value: (o) => o.subTotal },
  { header: 'Discount', value: (o) => o.discountAmount },
  { header: 'Tax', value: (o) => o.taxAmount },
  { header: 'Total', value: (o) => o.totalAmount },
  { header: 'Notes', value: (o) => o.notes },
  { header: 'Terms and conditions', value: (o) => o.termsAndConditions },
];

const OrderListPage: React.FC = () => {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { userData, hasPermission } = useAuth();
  // Orders are confirmed from the Client PO Inbox, which is a Customer Awards screen. A button
  // there for a reader without that grant lands on Access Denied — and the guarded widget used to
  // render the whole Access Denied panel in this page's header. Hide the door; name the key.
  const canReviewClientPos = hasPermission('Customer Awards');
  const canUploadClientPo = hasPermission('Customer Awards', 'create')
    && hasPermission('Orders', 'create');
  const askForAwardsAccess = 'Ask your administrator for Customer Awards and Client Orders create access to upload a Client PO.';
  const businessUnitId = userData?.businessUnitId || 0;
  const [searchTerm, setSearchTerm] = useState('');
  const [uploadOpen, setUploadOpen] = useState(false);
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
      <Stack
        direction={{ xs: 'column', md: 'row' }}
        spacing={2}
        sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', md: 'center' }, mb: 3 }}
      >
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, display: 'flex', alignItems: 'center', gap: 1 }}>
            <OrderIcon color="primary" /> Client Orders
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Client commitments created from a PO matched to the submitted quote.
          </Typography>
        </Box>
        <Stack
          direction={{ xs: 'column', sm: 'row' }}
          spacing={1.5}
          sx={{ alignItems: { xs: 'stretch', sm: 'center' } }}
        >
          <ExportExcelButton name="Client-Orders" columns={ORDER_EXPORT_COLUMNS} loadRows={async () => orders} />
          {canReviewClientPos && (
            <Button variant="text" onClick={() => navigate('/sales/client-pos')}>Review Client POs</Button>
          )}
          {canUploadClientPo ? (
            <Button variant="contained" startIcon={<UploadIcon />} onClick={() => setUploadOpen(true)}>
              Upload Client PO
            </Button>
          ) : !canReviewClientPos ? (
            <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 320, textAlign: 'right' }}>
              {askForAwardsAccess}
            </Typography>
          ) : null}
        </Stack>
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

      <TableContainer component={Paper} sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <Table size="small" sx={{ minWidth: 980 }}>
          <TableHead>
            <TableRow sx={{ bgcolor: 'grey.50' }}>
              <TableCell sx={{ fontWeight: 700 }}>Order #</TableCell>
              <TableCell sx={{ fontWeight: 700 }}>Date</TableCell>
              <TableCell sx={{ fontWeight: 700 }}>Customer</TableCell>
              <TableCell sx={{ fontWeight: 700 }}>Client PO</TableCell>
              <TableCell sx={{ fontWeight: 700 }}>Submitted quote</TableCell>
              <TableCell sx={{ fontWeight: 700 }} align="right">Amount</TableCell>
              <TableCell sx={{ fontWeight: 700 }} align="center">Status</TableCell>
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
                    : canUploadClientPo
                      ? <Button variant="contained" startIcon={<UploadIcon />} sx={{ mt: 2, fontWeight: 700 }} onClick={() => setUploadOpen(true)}>Upload Client PO</Button>
                      : <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>{askForAwardsAccess}</Typography>}
                </TableCell>
              </TableRow>
            ) : (
              orders.map((order) => (
                <TableRow key={order.id} hover>
                  <TableCell sx={{ fontWeight: 600 }}>{order.orderNo || order.orderNumber}</TableCell>
                  <TableCell>{dayjs(order.orderDate).format('DD MMM YYYY')}</TableCell>
                  <TableCell>{order.customerName}</TableCell>
                  <TableCell>{order.clientPoNumber || 'Not linked'}</TableCell>
                  <TableCell>{order.quoteNo || 'Not linked'}</TableCell>
                  <TableCell align="right" sx={{ fontWeight: 700 }}>{formatMoney(order.totalAmount, order.currencyCode)}</TableCell>
                  <TableCell align="center">
                    <Chip label={order.status} size="small" color={getStatusColor(order.status) as any} variant="filled" sx={{ fontWeight: 600, minWidth: 80 }} />
                  </TableCell>
                  <TableCell align="center">
                    <Stack direction="row" spacing={1} sx={{ justifyContent: 'center' }}>
                      <Tooltip title="View Order">
                        <IconButton size="small" color="primary" onClick={() => navigate(`/sales/orders/${order.id}`)}><ViewIcon fontSize="small" /></IconButton>
                      </Tooltip>
                      {RELEASE_SCOPE.receivables && <PermissionGuard moduleName="Accounts Receivable" action="create">
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
                      </PermissionGuard>}
                      {RELEASE_SCOPE.fulfilment && <PermissionGuard moduleName="Shipments" action="create">
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
                      </PermissionGuard>}
                    </Stack>
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </TableContainer>

      {RELEASE_SCOPE.receivables && invoicing && (
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

      <ClientPoUploadFlow
        open={uploadOpen}
        onClose={() => setUploadOpen(false)}
        onCompleted={async (result) => {
          await queryClient.invalidateQueries({ queryKey: ['orders-list'] });
          await queryClient.invalidateQueries({ queryKey: ['client-purchase-orders'] });
          if (result.order) navigate(`/sales/orders/${result.order.id}`);
        }}
      />
    </Box>
  );
};

export default OrderListPage;
