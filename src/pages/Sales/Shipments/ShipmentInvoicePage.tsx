import React, { useRef } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Grid, Divider, Table, TableHead,
  TableRow, TableCell, TableBody, Stack, CircularProgress
} from '@mui/material';
import {
  LocalShipping as ShippingIcon,
} from '@mui/icons-material';
import shipmentService from '../../../api/services/shipmentService';
import orderService from '../../../api/services/orderService';
import { useAuth } from '../../../context/AuthContext';
import dayjs from 'dayjs';

const ShipmentInvoicePage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;
  const printRef = useRef<HTMLDivElement>(null);

  const { data: shipment, isLoading: isLoadingShipment } = useQuery({
    queryKey: ['shipment-packing-slip', id],
    queryFn: () => shipmentService.getById(Number(id), businessUnitId),
    enabled: !!id,
  });

  const { data: order } = useQuery({
    queryKey: ['order-details', shipment?.orderId],
    queryFn: () => orderService.getById(Number(shipment?.orderId), businessUnitId),
    enabled: !!shipment?.orderId,
  });


  if (isLoadingShipment) return <Box sx={{ display: 'flex', justifyContent: 'center', p: 5 }}><CircularProgress /></Box>;
  if (!shipment) return <Typography color="error">Shipment not found</Typography>;

  return (
    <Box sx={{ bgcolor: 'white', minHeight: '100vh', display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
      {/* Document Content */}
      <Box 
        ref={printRef}
        sx={{ 
          p: { xs: 3, md: 6 }, 
          width: '100%',
          maxWidth: '1000px', 
          bgcolor: 'white',
          '@media print': {
            p: 0,
            maxWidth: 'none',
          }
        }}
      >
        {/* Company Header */}
        <Grid container spacing={4} sx={{ mb: 6 }}>
          <Grid size={{ xs: 6 }}>
            <Typography variant="h4" sx={{ fontWeight: 900, color: 'primary.main', mb: 1 }}>
              NEXORA <span style={{ fontWeight: 300, color: '#333' }}>LOGISTICS</span>
            </Typography>
            <Typography variant="body2" sx={{ color: 'text.secondary' }}>
              123 Enterprise Way, Industrial Park<br />
              Logistics Hub, Zone A<br />
              T: +44 (0) 20 7946 0000<br />
              E: logistics@nexora.com
            </Typography>
          </Grid>
          <Grid size={{ xs: 6 }} sx={{ textAlign: 'right' }}>
            <Typography variant="h3" sx={{ fontWeight: 900, color: 'grey.300', mb: 2 }}>DELIVERY NOTE</Typography>
            <Stack spacing={0.5}>
              <Typography variant="body2"><strong>Shipment #:</strong> {shipment.shipmentNo}</Typography>
              <Typography variant="body2"><strong>Ship Date:</strong> {dayjs(shipment.shipmentDate).format('DD MMM YYYY')}</Typography>
              <Typography variant="body2"><strong>Order #:</strong> {shipment.orderNo}</Typography>
              <Typography variant="body2"><strong>Carrier:</strong> {shipment.carrier || 'Internal'}</Typography>
            </Stack>
          </Grid>
        </Grid>

        {/* Logistic Summary */}
        <Box sx={{ mb: 6, p: 3, bgcolor: 'primary.50', borderRadius: 2, display: 'flex', alignItems: 'center', gap: 3 }}>
          <ShippingIcon sx={{ fontSize: '3rem', color: 'primary.main' }} />
          <Box>
            <Typography variant="subtitle1" sx={{ fontWeight: 800, color: 'primary.main' }}>
              LOGISTICS TRACKING
            </Typography>
            <Typography variant="body2">
              <strong>Tracking Number:</strong> {shipment.trackingNumber || 'PENDING'} | 
              <strong> Service:</strong> {shipment.serviceLevel || 'Standard'} |
              <strong> Est. Delivery:</strong> {shipment.estimatedDeliveryDate ? dayjs(shipment.estimatedDeliveryDate).format('DD MMM YYYY') : 'TBD'}
            </Typography>
          </Box>
        </Box>

        {/* Bill To / Ship To */}
        <Grid container spacing={4} sx={{ mb: 6 }}>
          <Grid size={{ xs: 6 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 1, color: 'text.secondary', textTransform: 'uppercase' }}>Consignee (Deliver To)</Typography>
            <Typography variant="h6" sx={{ fontWeight: 700 }}>{order?.customerName || 'Standard Customer'}</Typography>
            <Typography variant="body2" sx={{ color: 'text.secondary', mt: 0.5 }}>
              {shipment.shippingAddress || 'No Address Provided'}<br />
              Attn: Warehouse Manager
            </Typography>
          </Grid>
          <Grid size={{ xs: 6 }} sx={{ textAlign: 'right' }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 1, color: 'text.secondary', textTransform: 'uppercase' }}>Shipment Details</Typography>
            <Typography variant="body2">
              <strong>Total Weight:</strong> TBD kg<br />
              <strong>Total Packages:</strong> 1 Box / Pallet<br />
              <strong>Shipment Status:</strong> {shipment.status?.toUpperCase()}
            </Typography>
          </Grid>
        </Grid>

        {/* Items Table */}
        <Table sx={{ mb: 6 }}>
          <TableHead>
            <TableRow sx={{ borderTop: '2px solid black', borderBottom: '2px solid black' }}>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }}>S.NO</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }}>PRODUCT DESCRIPTION</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }} align="right">ORDER QTY</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }} align="right">SHIP QTY</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }} align="right">REMAINING</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {shipment.items.map((item, index) => (
              <TableRow key={index} sx={{ '& td': { borderBottom: '1px solid #eee', py: 2 } }}>
                <TableCell>{index + 1}</TableCell>
                <TableCell>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>{item.productName}</Typography>
                  <Typography variant="caption" color="text.secondary">{item.notes || 'Industrial Quality Component'}</Typography>
                </TableCell>
                <TableCell align="right">{item.quantity}</TableCell>
                <TableCell align="right" sx={{ fontWeight: 700 }}>{item.quantity}</TableCell>
                <TableCell align="right">0</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>

        {/* Delivery Confirmation Area */}
        <Grid container spacing={8}>
          <Grid size={{ xs: 6 }}>
            <Box sx={{ p: 3, border: '1px dashed grey', borderRadius: 2 }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 4 }}>SHIPPER'S SIGNATURE</Typography>
              <Divider sx={{ borderBottomWidth: 1, borderColor: 'black', mb: 1 }} />
              <Typography variant="caption">Dispatch Officer Name & Date</Typography>
            </Box>
          </Grid>
          <Grid size={{ xs: 6 }}>
            <Box sx={{ p: 3, border: '1px dashed grey', borderRadius: 2 }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 4 }}>RECEIVER'S SIGNATURE / STAMP</Typography>
              <Divider sx={{ borderBottomWidth: 1, borderColor: 'black', mb: 1 }} />
              <Typography variant="caption">Confirmed received in good condition</Typography>
            </Box>
          </Grid>
        </Grid>

        {/* Footer */}
        <Box sx={{ mt: 8, pt: 2, borderTop: '1px solid #eee', textAlign: 'center' }}>
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            This is a computer generated document and does not require a physical signature for validity.<br />
            Nexora Logistics - Excellence in Industrial Supply Chain
          </Typography>
        </Box>
      </Box>

      {/* Global CSS for Print */}
      <style>
        {`
          @media print {
            body { background: white !important; margin: 0; padding: 0; }
            .print-none { display: none !important; }
            header, footer, nav { display: none !important; }
          }
        `}
      </style>
    </Box>
  );
};

export default ShipmentInvoicePage;
