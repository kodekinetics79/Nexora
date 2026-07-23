import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Grid, Stack, Button, Chip,
  Table, TableHead, TableRow, TableCell, TableBody,
  Divider, CircularProgress, IconButton, Card, CardContent, Tooltip, Alert
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Edit as EditIcon,
  PictureAsPdf as PdfIcon,
  Email as EmailIcon,
  Send as SendIcon,
  ShoppingCart as OrderIcon,
  EmojiEvents as OutcomeIcon,
  MarkEmailRead as RespondedIcon,
  ContentCopy as ReviseIcon
} from '@mui/icons-material';
import quoteService from '../../../api/services/quoteService';
import QuoteOutcomeDialog from './QuoteOutcomeDialog';
import EmailPromptDialog from '../../../components/common/EmailPromptDialog';
import { CustomerAwardDialog, type CustomerAwardQuote } from './customer-awards';
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

  // WP-B4 revisions-lite: chain facts drive the "Rev n" chip + Revise button.
  const { data: revisionInfo } = useQuery({
    queryKey: ['quote-revisions', id],
    queryFn: () => quoteService.getRevisionInfo(Number(id)),
    enabled: !!id
  });

  const reviseMutation = useMutation({
    mutationFn: () => quoteService.revise(Number(id)),
    onSuccess: (draft) => {
      toast.success(`Revision ${draft.quoteNo} created — you are now editing the new draft`);
      queryClient.invalidateQueries({ queryKey: ['quote-revisions', id] });
      queryClient.invalidateQueries({ queryKey: ['quotes'] });
      navigate(`/sales/quotes/edit/${draft.id}`);
    },
    onError: (error: any) => {
      const message = error?.response?.data?.message || 'This quote cannot be revised.';
      toast.error(message, { duration: 6000 });
    }
  });

  // WP-B3: quote-send with below-floor hold awareness.
  const [emailOpen, setEmailOpen] = React.useState(false);
  const [holdInfo, setHoldInfo] = React.useState<string | null>(null);
  const sendMutation = useMutation({
    mutationFn: (recipientEmail: string) => quoteService.sendEmail(Number(id), recipientEmail),
    onSuccess: (result) => {
      setEmailOpen(false);
      if (result.held) {
        setHoldInfo(result.message || null);
        toast('Sent for approval — pricing is below your floor. Track it in Approvals.', { icon: '⏳', duration: 6000 });
      } else {
        toast.success('Quote emailed to the customer');
        queryClient.invalidateQueries({ queryKey: ['quote-detail', id] });
      }
    },
    onError: () => toast.error('Failed to send the quote email')
  });

  // WP-A4: outcome capture + "customer responded" stamp.
  const [outcomeOpen, setOutcomeOpen] = React.useState(false);
  const respondedMutation = useMutation({
    mutationFn: () => quoteService.markResponded(Number(id)),
    onSuccess: () => {
      toast.success('Marked as responded');
      queryClient.invalidateQueries({ queryKey: ['quote-detail', id] });
    },
    onError: () => toast.error('Failed to mark as responded')
  });

  const [awardOpen, setAwardOpen] = React.useState(false);

  if (isLoading) return <Box sx={{ p: 4, display: 'flex', justifyContent: 'center' }}><CircularProgress /></Box>;
  if (!quote) return <Box sx={{ p: 4 }}>Quote not found</Box>;

  // Manual Calculation for header discount if not already in totalAmount
  // Actually the backend stores TotalAmount as the final grand total.
  // We need to show the breakdown.
  const itemsSubtotal = quote.quoteItems.reduce((sum, i) => sum + (i.quantity * i.unitPrice), 0);
  const itemsDiscounts = quote.quoteItems.reduce((sum, i) => sum + (i.discount || 0), 0);
  const itemsNetTotal = itemsSubtotal - itemsDiscounts;
  const headerDiscount = itemsNetTotal - (quote.totalAmount || 0); // This is an approximation if tax is involved
  const awardQuote: CustomerAwardQuote | null = quote.commercialCaseId && quote.customerId && quote.currencyId
    ? {
        id: quote.id,
        quoteNo: quote.quoteNo,
        version: quote.version,
        commercialCaseId: quote.commercialCaseId,
        customerId: quote.customerId,
        currencyId: quote.currencyId,
        currencyCode: quote.currencyCode,
        lines: quote.quoteItems.map((item) => ({
          id: item.id,
          productId: item.productId,
          productName: item.productName,
          description: item.itemDescription || item.productName || `Quote line ${item.id}`,
          quantity: item.quantity,
          unitPrice: item.unitPrice,
        })),
      }
    : null;

  return (
    <Box sx={{ p: 3, bgcolor: 'background.default', minHeight: '100vh' }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
            <IconButton onClick={() => navigate('/sales/quotes')} size="small"><BackIcon /></IconButton>
            <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>Quote: {quote.quoteNo}</Typography>
            <Chip label={quote.statusValue} color={quote.statusValue === 'Sent' ? 'success' : quote.statusValue === 'Accepted' ? 'primary' : 'default'} sx={{ fontWeight: 900, height: 28, borderRadius: 1.5 }} />
            {revisionInfo && revisionInfo.revisionNo > 1 && revisionInfo.revisionOfQuoteNo && (
              <Tooltip title={`This quote is revision ${revisionInfo.revisionNo} and replaces ${revisionInfo.revisionOfQuoteNo}`}>
                <Chip
                  label={`Rev ${revisionInfo.revisionNo} · replaces ${revisionInfo.revisionOfQuoteNo}`}
                  color="info"
                  variant="outlined"
                  size="small"
                  onClick={revisionInfo.revisionOfQuoteId ? () => navigate(`/sales/quotes/view/${revisionInfo.revisionOfQuoteId}`) : undefined}
                  sx={{ fontWeight: 900, height: 28, borderRadius: 1.5 }}
                />
              </Tooltip>
            )}
            {revisionInfo?.supersededByQuoteNo && (
              <Tooltip title="A newer revision replaces this quote — open it">
                <Chip
                  label={`Superseded by ${revisionInfo.supersededByQuoteNo}`}
                  color="warning"
                  variant="outlined"
                  size="small"
                  onClick={() => navigate(`/sales/quotes/view/${revisionInfo.supersededByQuoteId}`)}
                  sx={{ fontWeight: 900, height: 28, borderRadius: 1.5 }}
                />
              </Tooltip>
            )}
            {quote.outcomeOn && (
              <Tooltip title={[quote.outcomeReasonName, quote.outcomeNote].filter(Boolean).join(' — ') || 'No reason recorded'}>
                <Chip
                  label={(quote.statusCode || '').toUpperCase() === 'ACCEPTED' || (quote.statusCode || '').toUpperCase() === 'ORDERED'
                    ? 'Won' : (quote.statusCode || '').toUpperCase() === 'REJECTED' ? 'Lost' : 'Expired'}
                  color={(quote.statusCode || '').toUpperCase() === 'ACCEPTED' || (quote.statusCode || '').toUpperCase() === 'ORDERED'
                    ? 'success' : (quote.statusCode || '').toUpperCase() === 'REJECTED' ? 'error' : 'default'}
                  sx={{ fontWeight: 900, height: 28, borderRadius: 1.5 }}
                />
              </Tooltip>
            )}
            {quote.isStale && !quote.respondedOn && (
              <Chip
                label={`Stale · no reply for ${quote.daysSinceSent ?? '?'} days`}
                color="warning"
                variant="outlined"
                sx={{ fontWeight: 900, height: 28, borderRadius: 1.5 }}
              />
            )}
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
            onClick={() => setEmailOpen(true)}
            sx={{ borderRadius: 2 }}
          >
            Email
          </Button>

          {revisionInfo?.canRevise && (
            <Tooltip title="Create a new draft revision of this quote (the original stays untouched)">
              <Button
                variant="outlined"
                color="info"
                startIcon={reviseMutation.isPending ? <CircularProgress size={18} /> : <ReviseIcon />}
                onClick={() => reviseMutation.mutate()}
                disabled={reviseMutation.isPending}
                sx={{ borderRadius: 2, fontWeight: 800 }}
              >
                Revise
              </Button>
            </Tooltip>
          )}

          {quote.statusValue === 'Draft' && <Button variant="contained" startIcon={<SendIcon />} onClick={() => statusMutation.mutate('Sent')} sx={{ borderRadius: 2 }}>Finalize</Button>}

          {quote.statusValue === 'Sent' && (
            <>
              {!quote.respondedOn && (
                <Button
                  variant="outlined"
                  startIcon={respondedMutation.isPending ? <CircularProgress size={18} /> : <RespondedIcon />}
                  onClick={() => respondedMutation.mutate()}
                  disabled={respondedMutation.isPending}
                  sx={{ borderRadius: 2 }}
                >
                  Customer responded
                </Button>
              )}
              <Button
                variant="contained"
                color="warning"
                startIcon={<OutcomeIcon />}
                onClick={() => setOutcomeOpen(true)}
                sx={{ borderRadius: 2, fontWeight: 800 }}
              >
                Record outcome
              </Button>
            </>
          )}

          {quote.statusValue === 'Accepted' && (
            <Button 
                variant="contained" 
                color="primary" 
                startIcon={<OrderIcon />}
                onClick={() => setAwardOpen(true)}
                disabled={!awardQuote || quote.statusValue?.toUpperCase() === 'ORDERED'}
                sx={{ borderRadius: 2, fontWeight: 800 }}
            >
              Capture customer award
            </Button>
          )}
        </Stack>
      </Stack>

      {holdInfo !== null && (
        <Alert
          severity="info"
          onClose={() => setHoldInfo(null)}
          action={
            <Button color="inherit" size="small" sx={{ fontWeight: 800 }} onClick={() => navigate('/copilot/approvals')}>
              Open Approvals
            </Button>
          }
          sx={{ mb: 3, borderRadius: 2, fontWeight: 600 }}
        >
          Sent for approval — pricing is below your floor. Track it in Approvals.
          {holdInfo ? ` (${holdInfo})` : ''}
        </Alert>
      )}

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

      <QuoteOutcomeDialog
        open={outcomeOpen}
        onClose={() => setOutcomeOpen(false)}
        quoteId={Number(id)}
        quoteNo={quote.quoteNo}
        invalidateKeys={[['quote-detail', id], ['quotes']]}
      />

      <CustomerAwardDialog
        open={awardOpen}
        quote={awardQuote}
        onClose={() => setAwardOpen(false)}
        onCompleted={(result) => {
          setAwardOpen(false);
          if (result.order) navigate(`/sales/orders/${result.order.id}`);
        }}
      />

      <EmailPromptDialog
        open={emailOpen}
        title={`Email quote ${quote.quoteNo}`}
        initialEmail={quote.customerEmail || ''}
        loading={sendMutation.isPending}
        businessUnitId={businessUnitId}
        customerId={quote.customerId ?? null}
        onCancel={() => setEmailOpen(false)}
        onConfirm={(email) => sendMutation.mutate(email)}
      />
    </Box>
  );
};

export default QuoteViewPage;
