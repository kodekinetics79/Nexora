import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Grid, Stack, Button, Chip,
  Table, TableHead, TableRow, TableCell, TableBody,
  Divider, CircularProgress, IconButton, Card, CardContent
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Edit as EditIcon,
  PictureAsPdf as PdfIcon,
  Email as EmailIcon,
  CheckCircle as AcceptIcon,
  Cancel as RejectIcon,
  Send as SendIcon,
  ShoppingCart as OrderIcon
} from '@mui/icons-material';
import quoteService from '../../../api/services/quoteService';
import orderService from '../../../api/services/orderService';
import { useAuth } from '../../../context/AuthContext';
import dayjs from 'dayjs';
import { toast } from 'react-hot-toast';

const QuoteViewPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { userData } = useAuth();
  const queryClient = useQueryClient();
  const businessUnitId = userData?.businessUnitId || 0;

  const { data: quote, isLoading } = useQuery({
    queryKey: ['quote-detail', id],
    queryFn: () => quoteService.getById(Number(id), businessUnitId),
    enabled: !!id
  });

  const statusMutation = useMutation({
    mutationFn: (status: string) => quoteService.transitionStatus(Number(id), status, userData?.userName || 'System'),
    onSuccess: () => {
      toast.success('Status updated successfully');
      queryClient.invalidateQueries({ queryKey: ['quote-detail', id] });
    }
  });

  const orderMutation = useMutation({
    mutationFn: () => orderService.createFromQuote(Number(id), businessUnitId),
    onSuccess: (data) => {
      toast.success('Quote converted to Order successfully');
      navigate(`/sales/orders/${data.id}`);
    },
    onError: (error: any) => {
      console.error('Order Conversion Error:', error);
      const errorMessage = error?.response?.data?.message || error?.response?.data || error.message || 'An unexpected error occurred';
      toast.error(errorMessage, { duration: 5000 });
    }
  });

  if (isLoading) return <Box sx={{ p: 4, display: 'flex', justifyContent: 'center' }}><CircularProgress /></Box>;
  if (!quote) return <Box sx={{ p: 4 }}>Quote not found</Box>;

  // Manual Calculation for header discount if not already in totalAmount
  // Actually the backend stores TotalAmount as the final grand total.
  // We need to show the breakdown.
  const itemsSubtotal = quote.quoteItems.reduce((sum, i) => sum + (i.quantity * i.unitPrice), 0);
  const itemsDiscounts = quote.quoteItems.reduce((sum, i) => sum + (i.discount || 0), 0);
  const itemsNetTotal = itemsSubtotal - itemsDiscounts;
  const headerDiscount = itemsNetTotal - (quote.totalAmount || 0); // This is an approximation if tax is involved

  return (
    <Box sx={{ p: 3, bgcolor: 'background.default', minHeight: '100vh' }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
            <IconButton onClick={() => navigate('/sales/quotes')} size="small"><BackIcon /></IconButton>
            <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>Quote: {quote.quoteNo}</Typography>
            <Chip label={quote.statusValue} color={quote.statusValue === 'Sent' ? 'success' : quote.statusValue === 'Accepted' ? 'primary' : 'default'} sx={{ fontWeight: 900, height: 28, borderRadius: 1.5 }} />
          </Stack>
          <Typography variant="body2" color="text.secondary">Created on {dayjs(quote.createdDate).format('DD MMM YYYY')} by {quote.createdBy}</Typography>
        </Box>
        <Stack direction="row" spacing={1}>
          <Button 
            variant="outlined" 
            startIcon={<EditIcon />} 
            onClick={() => navigate(`/sales/quotes/edit/${id}`)} 
            disabled={quote.statusValue?.toUpperCase() === 'ORDERED'}
            sx={{ borderRadius: 2 }}
          >
            Edit
          </Button>
          <Button 
            variant="outlined" 
            startIcon={<PdfIcon />} 
            sx={{ borderRadius: 2 }}
          >
            Export PDF
          </Button>
          <Button 
            variant="outlined" 
            startIcon={<EmailIcon />} 
            disabled={quote.statusValue?.toUpperCase() === 'ORDERED'}
            sx={{ borderRadius: 2 }}
          >
            Email
          </Button>
          
          {quote.statusValue === 'Draft' && <Button variant="contained" startIcon={<SendIcon />} onClick={() => statusMutation.mutate('Sent')} sx={{ borderRadius: 2 }}>Finalize</Button>}
          
          {quote.statusValue === 'Sent' && (
            <>
              <Button variant="contained" color="success" startIcon={<AcceptIcon />} onClick={() => statusMutation.mutate('Accepted')} sx={{ borderRadius: 2 }}>Accept</Button>
              <Button variant="contained" color="error" startIcon={<RejectIcon />} onClick={() => statusMutation.mutate('Rejected')} sx={{ borderRadius: 2 }}>Reject</Button>
            </>
          )}

          {quote.statusValue === 'Accepted' && (
            <Button 
                variant="contained" 
                color="primary" 
                startIcon={orderMutation.isPending ? <CircularProgress size={20} color="inherit" /> : <OrderIcon />} 
                onClick={() => orderMutation.mutate()}
                disabled={orderMutation.isPending || quote.statusValue?.toUpperCase() === 'ORDERED'}
                sx={{ borderRadius: 2, fontWeight: 800 }}
            >
              Convert to Order
            </Button>
          )}
        </Stack>
      </Stack>

      <Grid container spacing={3}>
        <Grid size={{ xs: 12, md: 8 }}>
          <Paper sx={{ p: 3, borderRadius: 2, mb: 3, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 2 }}>Customer & Quote Details</Typography>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, sm: 6 }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Customer Name</Typography><Typography sx={{ fontWeight: 700 }}>{quote.customerName || 'N/A'}</Typography></Grid>
              <Grid size={{ xs: 12, sm: 6 }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Email</Typography><Typography sx={{ fontWeight: 700 }}>{quote.customerEmail || 'N/A'}</Typography></Grid>
              <Grid size={{ xs: 12, sm: 6 }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Reference RFQ</Typography><Typography sx={{ fontWeight: 700, color: 'primary.main' }}>{quote.rfqNo || 'None'}</Typography></Grid>
              <Grid size={{ xs: 12, sm: 6 }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Validity</Typography><Typography sx={{ fontWeight: 700 }}>Until {dayjs(quote.validUntil).format('DD MMM YYYY')}</Typography></Grid>
              {(quote.discountValue || 0) > 0 && (
                <Grid size={{ xs: 12, sm: 6 }}>
                  <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Header Discount</Typography>
                  <Typography sx={{ fontWeight: 700, color: 'error.main' }}>
                    {quote.discountTypeName}: {quote.discountValue}
                  </Typography>
                </Grid>
              )}
            </Grid>
            {quote.headerRemarks && <Box sx={{ mt: 3, p: 2, bgcolor: 'grey.50', borderRadius: 1, borderLeft: '4px solid', borderColor: 'primary.main' }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 800 }}>REMARKS</Typography><Typography variant="body2">{quote.headerRemarks}</Typography></Box>}
          </Paper>

          <Paper sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden' }}>
            <Box sx={{ p: 2, borderBottom: '1px solid', borderColor: 'divider', bgcolor: 'grey.50' }}><Typography variant="h6" sx={{ fontWeight: 800 }}>Quoted Items</Typography></Box>
            <Table size="small">
              <TableHead><TableRow sx={{ bgcolor: 'grey.50' }}><TableCell sx={{ fontWeight: 800 }}>#</TableCell><TableCell sx={{ fontWeight: 800 }}>Description</TableCell><TableCell sx={{ fontWeight: 800 }} align="center">Qty</TableCell><TableCell sx={{ fontWeight: 800 }} align="right">Unit Price</TableCell><TableCell sx={{ fontWeight: 800 }} align="right">Discount</TableCell><TableCell sx={{ fontWeight: 800 }} align="right">Total</TableCell></TableRow></TableHead>
              <TableBody>
                {quote.quoteItems.map((item, idx) => (
                  <TableRow key={item.id}>
                    <TableCell>{idx + 1}</TableCell>
                    <TableCell><Typography sx={{ fontWeight: 700, fontSize: '0.85rem' }}>{item.productName || 'Item'}</Typography><Typography variant="caption" color="text.secondary">{item.itemDescription}</Typography></TableCell>
                    <TableCell align="center">{item.quantity}</TableCell>
                    <TableCell align="right">$ {item.unitPrice?.toLocaleString()}</TableCell>
                    <TableCell align="right">
                      {item.discount > 0 ? (
                        <Typography variant="caption" color="error.main" sx={{ fontWeight: 700 }}>
                          - $ {item.discount.toLocaleString()}
                          <br />
                          ({item.discountTypeName})
                        </Typography>
                      ) : '-'}
                    </TableCell>
                    <TableCell align="right" sx={{ fontWeight: 700 }}>$ {item.totalAmount?.toLocaleString()}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, md: 4 }}>
          <Card sx={{ borderRadius: 2, border: '1px solid', borderColor: 'primary.main', boxShadow: 'none' }}>
            <CardContent sx={{ p: 3 }}>
              <Typography variant="h6" sx={{ fontWeight: 800, mb: 3 }}>Financial Summary</Typography>
              <Stack spacing={2}>
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}><Typography color="text.secondary">Gross Subtotal</Typography><Typography sx={{ fontWeight: 700 }}>$ {itemsSubtotal.toLocaleString()}</Typography></Box>
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}><Typography color="text.secondary">Item Discounts</Typography><Typography sx={{ fontWeight: 700, color: 'error.main' }}>- $ {itemsDiscounts.toLocaleString()}</Typography></Box>
                {headerDiscount > 0 && <Box sx={{ display: 'flex', justifyContent: 'space-between' }}><Typography color="text.secondary">Header Discount</Typography><Typography sx={{ fontWeight: 700, color: 'error.main' }}>- $ {headerDiscount.toLocaleString()}</Typography></Box>}
                <Divider />
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}><Typography variant="h5" sx={{ fontWeight: 900 }}>Grand Total</Typography><Typography variant="h5" sx={{ fontWeight: 900, color: 'primary.main' }}>$ {quote.totalAmount?.toLocaleString()}</Typography></Box>
              </Stack>
            </CardContent>
          </Card>
        </Grid>
      </Grid>
    </Box>
  );
};

export default QuoteViewPage;
