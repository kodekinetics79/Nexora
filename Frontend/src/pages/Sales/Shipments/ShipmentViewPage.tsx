import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Paper,  Grid, Stack, Button, Divider, Table,
  TableHead, TableRow, TableCell, TableBody, Chip, Card, CardContent,
  CircularProgress, Breadcrumbs, Link
} from '@mui/material';
import {
  Print as PrintIcon,
  Edit as EditIcon,
  LocalShipping as ShippingIcon,
  History as HistoryIcon,
  Download as DownloadIcon,
  Payment as PaymentIcon,
  Assignment as OrderIcon
} from '@mui/icons-material';
import { useAuth } from '../../../context/AuthContext';
import shipmentService from '../../../api/services/shipmentService';
import { DELIVERY_STATUS_LABEL } from '../../../api/services/deliveryService';
import DeliveryConfirmationPanel from './DeliveryConfirmationPanel';
import orderService from '../../../api/services/orderService';
import dayjs from 'dayjs';

const ShipmentViewPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;

  const { data: shipment, isLoading: isLoadingShipment } = useQuery({
    queryKey: ['shipment-details', id],
    queryFn: () => shipmentService.getById(Number(id), businessUnitId),
    enabled: !!id,
  });

  const { data: order, isLoading: isLoadingOrder } = useQuery({
    queryKey: ['order-details', shipment?.orderId],
    queryFn: () => orderService.getById(Number(shipment?.orderId), businessUnitId),
    enabled: !!shipment?.orderId,
  });

  if (isLoadingShipment || (shipment && isLoadingOrder)) {
    return <Box sx={{ display: 'flex', justifyContent: 'center', p: 5 }}><CircularProgress /></Box>;
  }

  if (!shipment) return <Box sx={{ p: 3 }}><Typography color="error">Shipment not found.</Typography></Box>;

  const getStatusColor = (status: string) => {
    switch (status?.toUpperCase()) {
      case 'DELIVERED': return 'success';
      case 'SHIPPED':
      case 'IN TRANSIT': return 'primary';
      case 'PENDING': return 'warning';
      default: return 'default';
    }
  };

  return (
    <Box sx={{ p: 2, bgcolor: 'background.default', minHeight: '100vh' }}>
      {/* Header Section */}
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Breadcrumbs sx={{ mb: 0.5 }}>
            <Link component="button" variant="caption" onClick={() => navigate('/sales/shipments')} underline="hover" color="inherit">Shipments</Link>
            <Typography variant="caption" color="text.primary">{shipment.shipmentNo}</Typography>
          </Breadcrumbs>
          <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}>
            <ShippingIcon color="primary" />
            <Typography variant="h5" sx={{ fontWeight: 900 }}>Shipment {shipment.shipmentNo}</Typography>
            {/*
              Two chips, because there are two facts. The first is the tenant's own picklist label
              (SetupMaster / 'ShipmentStatus'), which nothing constrains — hence the fall-through to
              grey. The second is the GOVERNED delivery status (FR-DLM-05), which every guard,
              ledger and document in the product actually depends on. Showing only the first is what
              made "delivered" mean whatever an operator had typed.
            */}
            <Chip label={shipment.status} size="small" color={getStatusColor(shipment.status) as any} sx={{ fontWeight: 700 }} />
            <Chip
              label={DELIVERY_STATUS_LABEL[shipment.deliveryStatus] ?? shipment.deliveryStatus}
              size="small"
              variant="outlined"
              color={
                shipment.deliveryStatus === 'DELIVERED' ? 'success'
                  : shipment.deliveryStatus === 'DELIVERY_EXCEPTION' ? 'warning'
                    : shipment.deliveryStatus === 'CANCELLED' ? 'default' : 'primary'
              }
              sx={{ fontWeight: 700 }}
            />
          </Stack>
        </Box>
        <Stack direction="row" spacing={1.5}>
          <Button variant="outlined" startIcon={<PrintIcon />} size="small" onClick={() => window.open(`/sales/shipments/invoice/${shipment.id}`, '_blank')}>Print Packing Slip</Button>
          {shipment.labelUrl && (
            <Button variant="outlined" startIcon={<DownloadIcon />} size="small" href={shipment.labelUrl} target="_blank">Shipping Label</Button>
          )}
          <Button variant="contained" startIcon={<EditIcon />} size="small" onClick={() => navigate(`/sales/shipments/edit/${shipment.id}`)}>
            Edit Shipment
          </Button>
        </Stack>
      </Stack>

      <Grid container spacing={2}>
        <Grid size={{ xs: 12, lg: 8 }}>
          {/* FR-DLM-03, FR-DLM-05, FR-DLM-07 — the delivery ladder, the POD and the shortfall. */}
          <DeliveryConfirmationPanel
            shipmentId={shipment.id}
            orderId={shipment.orderId}
            deliveryStatus={shipment.deliveryStatus}
            items={shipment.items}
          />

          {/* General Information */}
          <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', mb: 2 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 3, color: 'primary.main', display: 'flex', alignItems: 'center', gap: 1 }}>
              GENERAL INFORMATION
            </Typography>
            <Grid container spacing={3}>
              <Grid size={{ xs: 12, md: 4 }}>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Order Number</Typography>
                <Typography variant="body2" sx={{ fontWeight: 700, mt: 0.5, color: 'primary.main', cursor: 'pointer' }} onClick={() => navigate(`/sales/orders/${shipment.orderId}`)}>
                  {shipment.orderNo}
                </Typography>
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Shipment Date</Typography>
                <Typography variant="body2" sx={{ fontWeight: 600, mt: 0.5 }}>{dayjs(shipment.shipmentDate).format('DD MMM YYYY')}</Typography>
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Carrier</Typography>
                <Typography variant="body2" sx={{ fontWeight: 600, mt: 0.5 }}>{shipment.carrier || 'Not Set'}</Typography>
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Service Level</Typography>
                <Typography variant="body2" sx={{ fontWeight: 600, mt: 0.5 }}>{shipment.serviceLevel || 'Standard'}</Typography>
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Tracking Number</Typography>
                <Typography variant="body2" sx={{ fontWeight: 700, mt: 0.5, letterSpacing: '0.05em' }}>{shipment.trackingNumber || '-'}</Typography>
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Est. Delivery</Typography>
                <Typography variant="body2" sx={{ fontWeight: 600, mt: 0.5 }}>{shipment.estimatedDeliveryDate ? dayjs(shipment.estimatedDeliveryDate).format('DD MMM YYYY') : '-'}</Typography>
              </Grid>
            </Grid>
          </Paper>

          {/* Shipment Items */}
          <Paper sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden', mb: 2 }}>
            <Box sx={{ p: 2, bgcolor: 'grey.50', borderBottom: '1px solid', borderColor: 'divider' }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>SHIPMENT LINE ITEMS</Typography>
            </Box>
            <Table size="small">
              <TableHead>
                <TableRow sx={{ bgcolor: 'grey.100' }}>
                  <TableCell sx={{ fontWeight: 800 }}>Product Name</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Quantity Shipped</TableCell>
                  <TableCell sx={{ fontWeight: 800 }}>Notes</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {shipment.items.map((item) => (
                  <TableRow key={item.id}>
                    <TableCell sx={{ fontWeight: 600 }}>{item.productName}</TableCell>
                    <TableCell align="right" sx={{ fontWeight: 700, color: 'primary.main' }}>{item.quantity}</TableCell>
                    <TableCell sx={{ color: 'text.secondary', fontStyle: 'italic' }}>{item.notes || '-'}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Paper>

          {/* Status History */}
          <Paper sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden' }}>
            <Box sx={{ p: 2, bgcolor: 'grey.50', borderBottom: '1px solid', borderColor: 'divider', display: 'flex', alignItems: 'center', gap: 1 }}>
              <HistoryIcon fontSize="small" />
              <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>STATUS TRACKING HISTORY</Typography>
            </Box>
            <Table size="small">
              <TableHead>
                <TableRow sx={{ bgcolor: 'grey.100' }}>
                  <TableCell sx={{ fontWeight: 800 }}>Date & Time</TableCell>
                  <TableCell sx={{ fontWeight: 800 }}>Status</TableCell>
                  <TableCell sx={{ fontWeight: 800 }}>Changed By</TableCell>
                  <TableCell sx={{ fontWeight: 800 }}>Notes</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {shipment.statusHistory?.map((history) => (
                  <TableRow key={history.id}>
                    <TableCell>{dayjs(history.changedOn).format('DD MMM YYYY HH:mm')}</TableCell>
                    <TableCell>
                      <Chip label={history.newStatus} size="small" variant="outlined" sx={{ fontWeight: 700, height: 20, fontSize: '0.65rem' }} />
                    </TableCell>
                    <TableCell>{history.changedBy}</TableCell>
                    <TableCell sx={{ fontSize: '0.8rem' }}>{history.notes}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, lg: 4 }}>
          {/* Order Summary Linked to this shipment */}
          {order && (
            <Card sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', mb: 2 }}>
              <CardContent>
                <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 2, display: 'flex', alignItems: 'center', gap: 1 }}>
                  <OrderIcon fontSize="small" /> ORDER SUMMARY
                </Typography>
                <Stack spacing={1.5}>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                    <Typography variant="body2" color="text.secondary">Customer</Typography>
                    <Typography variant="body2" sx={{ fontWeight: 700 }}>{order.customerName}</Typography>
                  </Box>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                    <Typography variant="body2" color="text.secondary">Order Total</Typography>
                    <Typography variant="body2" sx={{ fontWeight: 700 }}>$ {order.totalAmount.toLocaleString()}</Typography>
                  </Box>
                  <Divider />
                  <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 800, display: 'flex', alignItems: 'center', gap: 0.5 }}>
                    <PaymentIcon sx={{ fontSize: 14 }} /> PAYMENT STATUS
                  </Typography>
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
                    size="small"
                    sx={{ fontWeight: 800, borderRadius: 1 }} 
                  />
                </Stack>
              </CardContent>
            </Card>
          )}

          {/* Delivery & Address */}
          <Card sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
            <CardContent>
              <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 2 }}>SHIPPING DESTINATION</Typography>
              <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap', color: 'text.secondary', mb: 1 }}>
                {shipment.shippingAddress || 'No address specified'}
              </Typography>
              {/* FR-DLM-01. The governed region, or a visible statement that there is none. */}
              <Typography variant="body2" sx={{ mb: 2 }}>
                <strong>Region:</strong>{' '}
                {shipment.deliveryRegionName
                  ? [shipment.deliveryCityName, shipment.deliveryRegionName].filter(Boolean).join(', ')
                  : <Box component="span" sx={{ fontStyle: 'italic', color: 'text.disabled' }}>
                      Not mapped to a governed region
                    </Box>}
              </Typography>
              
              <Divider sx={{ my: 2 }} />
              
              <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 1 }}>NOTES</Typography>
              <Typography variant="body2" sx={{ fontStyle: 'italic', color: 'text.secondary' }}>
                {shipment.notes || 'No internal notes'}
              </Typography>
            </CardContent>
          </Card>
        </Grid>
      </Grid>
    </Box>
  );
};

export default ShipmentViewPage;
