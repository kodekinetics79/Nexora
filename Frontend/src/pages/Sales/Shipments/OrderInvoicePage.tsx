import React, { useRef } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Grid, Divider, Table, TableHead,
  TableRow, TableCell, TableBody, CircularProgress, Stack
} from '@mui/material';
import orderService from '../../../api/services/orderService';
import { useAuth } from '../../../context/AuthContext';
import dayjs from 'dayjs';

const OrderInvoicePage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;
  const printRef = useRef<HTMLDivElement>(null);

  const { data: order, isLoading } = useQuery({
    queryKey: ['order-invoice', id],
    queryFn: () => orderService.getById(Number(id), businessUnitId),
    enabled: !!id,
  });

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', p: 5 }}><CircularProgress /></Box>;
  if (!order) return <Typography color="error">Order not found</Typography>;

  return (
    <Box sx={{ bgcolor: 'white', minHeight: '100vh', display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
      {/* Invoice Content */}
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
              NEXORA <span style={{ fontWeight: 300, color: '#333' }}>ENTERPRISE</span>
            </Typography>
            <Typography variant="body2" sx={{ color: 'text.secondary' }}>
              123 Enterprise Way, Industrial Park<br />
              London, United Kingdom, EC1A 1BB<br />
              T: +44 (0) 20 7946 0000<br />
              E: billing@nexora.com
            </Typography>
          </Grid>
          <Grid size={{ xs: 6 }} sx={{ textAlign: 'right' }}>
            <Typography variant="h3" sx={{ fontWeight: 900, color: 'grey.300', mb: 2 }}>INVOICE</Typography>
            <Stack spacing={0.5}>
              <Typography variant="body2"><strong>Invoice #:</strong> INV-{order.orderNo?.split('-').pop()}</Typography>
              <Typography variant="body2"><strong>Date:</strong> {dayjs().format('DD MMM YYYY')}</Typography>
              <Typography variant="body2"><strong>Order #:</strong> {order.orderNo}</Typography>
              <Typography variant="body2"><strong>Customer ID:</strong> CUST-{order.customerId}</Typography>
            </Stack>
          </Grid>
        </Grid>

        {/* Bill To / Ship To */}
        <Grid container spacing={4} sx={{ mb: 6 }}>
          <Grid size={{ xs: 6 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 1, color: 'text.secondary', textTransform: 'uppercase' }}>Bill To</Typography>
            <Typography variant="h6" sx={{ fontWeight: 700 }}>{order.customerName}</Typography>
            <Typography variant="body2" sx={{ color: 'text.secondary', mt: 0.5 }}>
              Industrial Area, Phase II<br />
              Warehouse B-4, Gate 2<br />
              Tax ID: GB123456789
            </Typography>
          </Grid>
          <Grid size={{ xs: 6 }} sx={{ textAlign: 'right' }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 1, color: 'text.secondary', textTransform: 'uppercase' }}>Ship To</Typography>
            <Typography variant="h6" sx={{ fontWeight: 700 }}>{order.customerName}</Typography>
            <Typography variant="body2" sx={{ color: 'text.secondary', mt: 0.5 }}>
              {order.notes || 'Same as Billing Address'}<br />
              Attn: Receiving Department
            </Typography>
          </Grid>
        </Grid>

        {/* Items Table */}
        <Table sx={{ mb: 4 }}>
          <TableHead>
            <TableRow sx={{ borderTop: '2px solid black', borderBottom: '2px solid black' }}>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }}>S.NO</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }}>DESCRIPTION</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }} align="right">QTY</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }} align="right">UNIT PRICE</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }} align="right">TOTAL</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {order.items.map((item, index) => (
              <TableRow key={index} sx={{ '& td': { borderBottom: '1px solid #eee', py: 2 } }}>
                <TableCell>{index + 1}</TableCell>
                <TableCell>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>{item.productName}</Typography>
                  <Typography variant="caption" color="text.secondary">{item.description || 'Enterprise Grade Product'}</Typography>
                </TableCell>
                <TableCell align="right">{item.quantity}</TableCell>
                <TableCell align="right">$ {item.unitPrice.toLocaleString()}</TableCell>
                <TableCell align="right" sx={{ fontWeight: 700 }}>$ {item.totalAmount.toLocaleString()}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>

        {/* Totals Section */}
        <Grid container spacing={4}>
          <Grid size={{ xs: 7 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 1 }}>NOTES & TERMS</Typography>
            <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block', mb: 1 }}>
              1. Please include invoice number on your check/remittance.<br />
              2. Payment is due within 30 days of invoice date.<br />
              3. Returns subject to 15% restocking fee.<br />
              4. All goods remain property of Nexora until paid in full.
            </Typography>
            <Box sx={{ mt: 4 }}>
              <Typography variant="body2" sx={{ fontWeight: 700 }}>Authorized Signature</Typography>
              <Box sx={{ width: '200px', borderBottom: '1px solid black', mt: 4 }} />
            </Box>
          </Grid>
          <Grid size={{ xs: 5 }}>
            <Stack spacing={1.5}>
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2">Subtotal</Typography>
                <Typography variant="body2" sx={{ fontWeight: 600 }}>$ {(order.totalAmount - (order.totalAmount * 0.1)).toLocaleString()}</Typography>
              </Box>
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2">Discount (0%)</Typography>
                <Typography variant="body2" sx={{ fontWeight: 600 }}>$ 0.00</Typography>
              </Box>
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2">Tax (VAT 10%)</Typography>
                <Typography variant="body2" sx={{ fontWeight: 600 }}>$ {(order.totalAmount * 0.1).toLocaleString()}</Typography>
              </Box>
              <Divider sx={{ borderBottomWidth: 2, borderColor: 'black' }} />
              <Box sx={{ display: 'flex', justifyContent: 'space-between', pt: 1 }}>
                <Typography variant="h6" sx={{ fontWeight: 900 }}>GRAND TOTAL</Typography>
                <Typography variant="h6" sx={{ fontWeight: 900, color: 'primary.main' }}>$ {order.totalAmount.toLocaleString()}</Typography>
              </Box>
              <Box sx={{ mt: 2, p: 2, bgcolor: 'grey.50', borderRadius: 1, textAlign: 'center' }}>
                <Typography variant="caption" sx={{ fontWeight: 700, color: 'primary.main', textTransform: 'uppercase' }}>
                  Thank you for your business!
                </Typography>
              </Box>
            </Stack>
          </Grid>
        </Grid>
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

export default OrderInvoicePage;
