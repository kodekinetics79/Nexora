import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Grid, Stack, Button, Divider, Table,
  TableHead, TableRow, TableCell, TableBody, Chip, Card, CardContent,
  CircularProgress
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  ReceiptLong as ReceivableIcon,
  Email as EmailIcon,
  LocalShipping as ShipmentIcon
} from '@mui/icons-material';
import { useAuth } from '../../../context/AuthContext';
import orderService from '../../../api/services/orderService';
import dayjs from 'dayjs';

const OrderViewPage: React.FC = () => {
  const { id } = useParams();
  const navigate = useNavigate();
  const { userData, hasPermission } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;

  const { data: order, isLoading } = useQuery({
    queryKey: ['order-details', id, businessUnitId],
    queryFn: () => orderService.getById(Number(id), businessUnitId),
    enabled: !!id && !isNaN(Number(id)),
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
  if (!order) return <Box sx={{ p: 3 }}><Typography color="error">Order not found.</Typography></Box>;

  return (
    <Box sx={{ p: 2, bgcolor: 'background.default', minHeight: '100vh' }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5 }}>
            <Button startIcon={<BackIcon />} onClick={() => navigate('/sales/orders')} sx={{ color: 'text.secondary', textTransform: 'none' }}>Back to Orders</Button>
          </Stack>
          <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}>
            <Typography variant="h5" sx={{ fontWeight: 900 }}>Order #{order.orderNo || order.orderNumber}</Typography>
            <Chip label={order.status} size="small" color={getStatusColor(order.status) as any} sx={{ fontWeight: 700 }} />
          </Stack>
        </Box>
        <Stack direction="row" spacing={1.5}>
          {/* There is deliberately no "Print Invoice" action here. A tax invoice is the numbered,
              persisted document the finance subsystem issues — not a rendering of the order.
              This links to the governed AR register instead; it is not order-filtered yet. */}
          {hasPermission('Accounts Receivable', 'view') && (
            <Button variant="outlined" startIcon={<ReceivableIcon />} size="small" onClick={() => navigate('/sales/finance')}>Accounts Receivable</Button>
          )}
          <Button variant="outlined" startIcon={<EmailIcon />} size="small">Email</Button>
          {!order.hasShipments && !['Shipped', 'Delivered', 'Cancelled'].includes(order.status) && (
             <Button 
                variant="contained" 
                startIcon={<ShipmentIcon />} 
                color="secondary" 
                size="small"
                onClick={() => navigate(`/sales/shipments/create?orderId=${order.id}`)}
              >
                Create Shipment
              </Button>
          )}
        </Stack>
      </Stack>

      <Grid container spacing={2}>
        <Grid size={{ xs: 12, lg: 8 }}>
          <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', mb: 2 }}>
            <Grid container spacing={3}>
              <Grid size={{ xs: 12, md: 6 }}>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Customer Details</Typography>
                <Typography variant="subtitle1" sx={{ fontWeight: 800, mt: 1 }}>{order.customerName}</Typography>
                <Typography variant="body2" color="text.secondary">Contact Info from Master</Typography>
              </Grid>
              <Grid size={{ xs: 12, md: 3 }}>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Order Date</Typography>
                <Typography variant="body2" sx={{ fontWeight: 600, mt: 1 }}>{dayjs(order.orderDate).format('DD MMM YYYY')}</Typography>
              </Grid>
              <Grid size={{ xs: 12, md: 3 }}>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Expected Delivery</Typography>
                <Typography variant="body2" sx={{ fontWeight: 600, mt: 1 }}>{order.deliveryDate ? dayjs(order.deliveryDate).format('DD MMM YYYY') : 'Not Set'}</Typography>
              </Grid>
              {order.quoteNo && (
                <Grid size={{ xs: 12, md: 6 }}>
                    <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Converted From Quote</Typography>
                    <Typography variant="body2" sx={{ fontWeight: 600, mt: 1, color: 'primary.main', cursor: 'pointer' }} onClick={() => navigate(`/sales/quotes/view/${order.quoteId}`)}>
                        {order.quoteNo}
                    </Typography>
                </Grid>
              )}
            </Grid>
          </Paper>

          <Paper sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden' }}>
            <Box sx={{ p: 2, bgcolor: 'grey.50', borderBottom: '1px solid', borderColor: 'divider' }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>ORDER LINE ITEMS</Typography>
            </Box>
            <Table size="small">
              <TableHead>
                <TableRow sx={{ bgcolor: 'grey.100' }}>
                  <TableCell sx={{ fontWeight: 800 }}>Product / Description</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="center">Qty</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Unit Price</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Discount</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Total</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {order.items.map((item) => (
                  <TableRow key={item.id}>
                    <TableCell>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>{item.productName}</Typography>
                      <Typography variant="caption" color="text.secondary">{item.description}</Typography>
                    </TableCell>
                    <TableCell align="center">{item.quantity}</TableCell>
                    <TableCell align="right">$ {item.unitPrice.toLocaleString()}</TableCell>
                    <TableCell align="right">$ {item.discount.toLocaleString()}</TableCell>
                    <TableCell align="right" sx={{ fontWeight: 700 }}>$ {item.totalAmount.toLocaleString()}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, lg: 4 }}>
          <Card sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', mb: 2 }}>
            <CardContent>
              <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 2 }}>FINANCIAL SUMMARY</Typography>
              <Stack spacing={1.5}>
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                  <Typography variant="body2" color="text.secondary">Subtotal</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 600 }}>$ {order.subTotal.toLocaleString()}</Typography>
                </Box>
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                  <Typography variant="body2" color="text.secondary">Total Discount</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 600, color: 'error.main' }}>- $ {order.discountAmount.toLocaleString()}</Typography>
                </Box>
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                  <Typography variant="body2" color="text.secondary">Tax Amount</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 600 }}>$ {order.taxAmount.toLocaleString()}</Typography>
                </Box>
                <Divider />
                <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <Typography variant="subtitle1" sx={{ fontWeight: 800 }}>Grand Total</Typography>
                  <Typography variant="h6" sx={{ fontWeight: 900, color: 'primary.main' }}>$ {order.totalAmount.toLocaleString()}</Typography>
                </Box>
              </Stack>
            </CardContent>
          </Card>

          <Card sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
            <CardContent>
              <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 2 }}>PAYMENT STATUS</Typography>
              <Stack spacing={2}>
                <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <Typography variant="body2">Paid Amount</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 700, color: 'success.main' }}>$ {order.paidAmount.toLocaleString()}</Typography>
                </Box>
                <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <Typography variant="body2">Balance</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 700, color: 'error.main' }}>$ {order.balanceAmount.toLocaleString()}</Typography>
                </Box>
                <Chip 
                    label={order.paymentStatus?.toUpperCase() || 'UNPAID'} 
                    color={order.paymentStatus === 'Paid' ? 'success' : 'warning'} 
                    variant="filled" 
                    sx={{ fontWeight: 800 }} 
                />
              </Stack>
            </CardContent>
          </Card>
        </Grid>
      </Grid>
    </Box>
  );
};

export default OrderViewPage;
