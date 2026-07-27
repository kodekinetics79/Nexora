import { useQuery } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import {
  Alert, Box, Button, Chip, CircularProgress, Paper, Stack, Table, TableBody,
  TableCell, TableContainer, TableHead, TableRow, Typography,
} from '@mui/material';
import { ArrowBack, Description, OpenInNew, ShoppingCartCheckout } from '@mui/icons-material';
import customerAwardService from '../../../api/services/customerAwardService';
import CommercialProcessingEvidence from '../../../components/common/CommercialProcessingEvidence';

const readable = (value: string) => value.replaceAll('_', ' ');
const money = (value: number | null | undefined, currency: string) =>
  value == null ? 'Not provided' : new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(value);

export default function ClientPurchaseOrderReviewPage() {
  const navigate = useNavigate();
  const id = Number(useParams().clientPoId);
  const query = useQuery({
    queryKey: ['client-purchase-order-match', id],
    queryFn: () => customerAwardService.getPurchaseOrderMatch(id),
    enabled: Number.isInteger(id) && id > 0,
  });

  if (query.isLoading) return <Box sx={{ minHeight: '60vh', display: 'grid', placeItems: 'center' }}><CircularProgress /></Box>;
  if (query.isError || !query.data) return <Box sx={{ p: 3 }}><Alert severity="error">The Client PO match could not be loaded.</Alert></Box>;
  const match = query.data;

  return <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1600, mx: 'auto' }}>
    <Button startIcon={<ArrowBack />} onClick={() => navigate('/sales/client-pos')} sx={{ mb: 1 }}>Client PO Inbox</Button>
    <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', mb: 3 }}>
      <Box>
        <Typography variant="h4" component="h1" sx={{ fontWeight: 800 }}>{match.header.externalPoNumber}</Typography>
        <Typography color="text.secondary">{match.header.customerName} · {match.header.nexoraSerial} · {match.currencyCode}</Typography>
      </Box>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
        <Chip color={match.header.discrepancyCount > 0 ? 'warning' : 'success'} label={readable(match.header.matchOutcome)} />
        {match.header.quoteId && <Button startIcon={<Description />} onClick={() => navigate(`/sales/quotes/view/${match.header.quoteId}`)}>Customer Quote</Button>}
        {match.header.customerOrderId && <Button variant="contained" startIcon={<ShoppingCartCheckout />} onClick={() => navigate(`/sales/orders/${match.header.customerOrderId}`)}>Customer Order</Button>}
      </Stack>
    </Stack>

    <CommercialProcessingEvidence resource="client-purchase-orders" id={match.header.id} />

    {match.header.discrepancyCount > 0 ? <Alert severity="warning" sx={{ mb: 2 }}>
      Review the highlighted differences against the selected Customer Quote revision. Previous evidence remains unchanged.
    </Alert> : <Alert severity="success" sx={{ mb: 2 }}>
      Every accepted Client PO line reconciles to the selected Customer Quote values.
    </Alert>}

    <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={3}>
        <Box><Typography variant="caption" color="text.secondary">CLIENT PO</Typography><Typography sx={{ fontWeight: 800 }}>{match.header.internalNumber}</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">CUSTOMER QUOTE</Typography><Typography sx={{ fontWeight: 800 }}>{match.header.quoteNumber || 'Review required'}</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">AWARD</Typography><Typography sx={{ fontWeight: 800 }}>{match.awardNumber || 'Not accepted'} · {readable(match.awardStatus || 'REVIEW')}</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">PO DATE</Typography><Typography sx={{ fontWeight: 800 }}>{new Date(match.poDate).toLocaleDateString()}</Typography></Box>
      </Stack>
    </Paper>

    <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto' }}>
      <Table size="small" sx={{ minWidth: 1050 }}>
        <TableHead><TableRow>
          <TableCell>PO line</TableCell><TableCell>Client PO description</TableCell><TableCell>Customer Quote line</TableCell>
          <TableCell align="right">PO / accepted qty</TableCell><TableCell align="right">Quote qty</TableCell>
          <TableCell align="right">PO price</TableCell><TableCell align="right">Quote price</TableCell>
          <TableCell>Decision</TableCell>
        </TableRow></TableHead>
        <TableBody>{match.lines.map((line) => <TableRow key={line.customerPurchaseOrderLineId} sx={{ bgcolor: line.differences.length ? 'warning.50' : undefined }}>
          <TableCell sx={{ fontWeight: 800 }}>{line.externalLineReference}</TableCell>
          <TableCell>{line.purchaseOrderDescription}</TableCell><TableCell>{line.quoteDescription || 'No matched Quote line'}</TableCell>
          <TableCell align="right">{line.orderedQuantity} / {line.acceptedQuantity ?? 0}</TableCell>
          <TableCell align="right">{line.quotedQuantity ?? '-'}</TableCell>
          <TableCell align="right">{money(line.purchaseOrderUnitPrice, match.currencyCode)}</TableCell>
          <TableCell align="right">{money(line.quotedUnitPrice, match.currencyCode)}</TableCell>
          <TableCell><Chip size="small" color={line.differences.length ? 'warning' : 'success'} label={readable(line.matchStatus)} />{line.differences.map((difference) => <Typography key={difference} variant="caption" sx={{ display: 'block', mt: 0.5 }}>{readable(difference)}</Typography>)}</TableCell>
        </TableRow>)}</TableBody>
      </Table>
    </TableContainer>

    <Button sx={{ mt: 2 }} endIcon={<OpenInNew />} onClick={() => navigate(`/customers/${match.customerId}`)}>Open customer record</Button>
  </Box>;
}
