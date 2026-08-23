import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import {
  Alert, AlertTitle, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogContentText, DialogTitle, Paper, Stack, Table, TableBody,
  TableCell, TableContainer, TableHead, TableRow, TextField, Typography,
} from '@mui/material';
import { ArrowBack, Description, OpenInNew, ShoppingCartCheckout } from '@mui/icons-material';
import customerAwardService, {
  createCustomerAwardCommandIdentity,
  type ClientPurchaseOrderMatchLine,
} from '../../../api/services/customerAwardService';
import CommercialProcessingEvidence from '../../../components/common/CommercialProcessingEvidence';
import { statusLabel } from '../../../utils/statusLabels';
import { formatMoney } from '../../../utils/currency';

// See utils/currency.ts: one implementation, and one that cannot throw on a bad code.
const money = (value: number | null | undefined, currency: string) =>
  value == null ? 'Not provided' : formatMoney(value, currency);

/**
 * FR-COM-04, wiring contract: "the unit is stated wherever a number could be money, weight, time or
 * quantity".
 *
 * A quantity with no unit beside it is the reason a PO for 10 boxes and a quote for 10 each read as
 * an exact match on this very screen. A document that stated no unit shows that as a visible gap —
 * it must never borrow the other column's unit to look complete.
 */
const quantityWithUnit = (quantity: number | null | undefined, unit: string | null | undefined) => {
  if (quantity == null) return '-';
  return unit ? `${quantity} ${unit}` : `${quantity} (no unit stated)`;
};

const apiErrorMessage = (error: unknown, fallback: string): string => {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;
  if (data && typeof data === 'object') {
    const problem = data as { detail?: string; message?: string; title?: string };
    return problem.detail || problem.message || problem.title || fallback;
  }
  return error instanceof Error ? error.message : fallback;
};

/**
 * `"{awardId}:{lineId}:{CODE}"` as the server writes it, split back out for display against a line.
 * The award segment is already scoped by the server to the award on this screen.
 */
const differencesForLine = (keys: string[] | null | undefined, lineId: number) =>
  (keys ?? [])
    .map((key) => key.split(':'))
    .filter((parts) => parts.length === 3 && parts[1] === String(lineId))
    .map((parts) => parts[2]);

type ReasonedAction = 'accept' | 'cancel-po';

export default function ClientPurchaseOrderReviewPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const id = Number(useParams().clientPoId);
  const [reasonFor, setReasonFor] = useState<ReasonedAction | null>(null);
  const [reason, setReason] = useState('');
  const query = useQuery({
    queryKey: ['client-purchase-order-match', id],
    queryFn: () => customerAwardService.getPurchaseOrderMatch(id),
    enabled: Number.isInteger(id) && id > 0,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['client-purchase-order-match', id] });
    await queryClient.invalidateQueries({ queryKey: ['client-purchase-orders'] });
  };

  /**
   * FR-COM-04. The only way past the conversion gate: a named person, with a reason, on the record.
   * The server recomputes the differences and refuses an acceptance of nothing, so this cannot
   * become a button people press by reflex on a clean order.
   */
  const acceptMutation = useMutation({
    mutationFn: (given: string) => customerAwardService.acceptPurchaseOrderDifferences(
      id,
      { expectedVersion: query.data!.version, customerAwardId: query.data!.awardId!, reason: given },
      createCustomerAwardCommandIdentity('accept-differences'),
    ),
    onSuccess: refresh,
  });

  /** FR-COM-02. The way back on the buyer's document, which previously had none. */
  const cancelPoMutation = useMutation({
    mutationFn: (given: string) => customerAwardService.cancelPurchaseOrder(
      id,
      { expectedVersion: query.data!.version, reason: given },
      createCustomerAwardCommandIdentity('cancel-po'),
    ),
    onSuccess: refresh,
  });

  const convertMutation = useMutation({
    mutationFn: () => customerAwardService.convertToOrder(
      query.data!.awardId!,
      { expectedVersion: query.data!.awardVersion! },
      createCustomerAwardCommandIdentity('convert'),
    ),
    onSuccess: refresh,
  });

  if (query.isLoading) return <Box sx={{ minHeight: '60vh', display: 'grid', placeItems: 'center' }}><CircularProgress /></Box>;
  if (query.isError || !query.data) return <Box sx={{ p: 3 }}><Alert severity="error">The Client PO match could not be loaded.</Alert></Box>;
  const match = query.data;
  const blocking = match.blockingDifferences ?? [];
  const accepted = match.acceptedDifferences ?? [];
  const cancelled = match.header.status === 'CANCELLED';
  const canAccept = Boolean(match.awardId) && blocking.length > 0;
  const canConvert = Boolean(match.awardId) && match.awardVersion != null && blocking.length === 0
    && match.awardStatus === 'CONFIRMED' && !match.header.customerOrderId;
  // A live award is a commitment with its own reason and its own quantity ledger, so the server
  // refuses to withdraw the PO underneath it. Saying so here rather than offering a button that
  // can only 409.
  const liveAward = Boolean(match.awardStatus) && match.awardStatus !== 'CANCELLED';
  const busy = acceptMutation.isPending || cancelPoMutation.isPending || convertMutation.isPending;

  const submitReason = () => {
    const given = reason.trim();
    if (!given) return;
    if (reasonFor === 'accept') acceptMutation.mutate(given);
    if (reasonFor === 'cancel-po') cancelPoMutation.mutate(given);
    setReasonFor(null);
    setReason('');
  };

  const lineDecision = (line: ClientPurchaseOrderMatchLine) => {
    const blockedHere = differencesForLine(blocking, line.customerPurchaseOrderLineId);
    const acceptedHere = differencesForLine(accepted, line.customerPurchaseOrderLineId);
    return <TableCell>
      <Chip size="small" color={line.differences.length ? 'warning' : 'success'} label={statusLabel(line.matchStatus)} />
      {line.differences.map((difference) => <Typography
        key={difference}
        variant="caption"
        sx={{ display: 'block', mt: 0.5, fontWeight: blockedHere.includes(difference) ? 800 : 400 }}
        color={blockedHere.includes(difference) ? 'error.main' : acceptedHere.includes(difference) ? 'success.main' : undefined}
      >
        {statusLabel(difference)}
        {blockedHere.includes(difference) ? ' — blocks the order' : acceptedHere.includes(difference) ? ' — accepted' : ''}
      </Typography>)}
    </TableCell>;
  };

  return <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1600, mx: 'auto' }}>
    <Button startIcon={<ArrowBack />} onClick={() => navigate('/sales/client-pos')} sx={{ mb: 1 }}>Client PO Inbox</Button>
    <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', mb: 3 }}>
      <Box>
        <Typography variant="h4" component="h1" sx={{ fontWeight: 800 }}>{match.header.externalPoNumber}</Typography>
        <Typography color="text.secondary">{match.header.customerName} · {match.header.nexoraSerial} · {match.currencyCode}</Typography>
      </Box>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
        <Chip color={match.header.discrepancyCount > 0 ? 'warning' : 'success'} label={statusLabel(match.header.matchOutcome)} />
        {match.header.quoteId && <Button startIcon={<Description />} onClick={() => navigate(`/sales/quotes/view/${match.header.quoteId}`)}>Customer Quote</Button>}
        {match.header.customerOrderId && <Button variant="contained" startIcon={<ShoppingCartCheckout />} onClick={() => navigate(`/sales/orders/${match.header.customerOrderId}`)}>Customer Order</Button>}
      </Stack>
    </Stack>

    <CommercialProcessingEvidence resource="client-purchase-orders" id={match.header.id} />

    {cancelled && <Alert severity="info" sx={{ mb: 2 }}>
      <AlertTitle>This Client PO was withdrawn</AlertTitle>
      {match.cancellationReason || 'No reason was recorded.'}
    </Alert>}

    {blocking.length > 0 ? <Alert severity="error" sx={{ mb: 2 }}>
      <AlertTitle>{`${blocking.length} difference${blocking.length === 1 ? '' : 's'} must be accepted before a sales order can be raised`}</AlertTitle>
      The buyer&apos;s purchase order states a price, part or unit that is not the one we quoted. A sales
      order raised on it would be priced, described and measured from our quotation, not from theirs.
      Accept the differences with a reason, or cancel the award and recapture the PO.
    </Alert> : match.header.discrepancyCount > 0 ? <Alert severity="warning" sx={{ mb: 2 }}>
      Review the highlighted differences against the selected Customer Quote revision. Previous evidence remains unchanged.
    </Alert> : <Alert severity="success" sx={{ mb: 2 }}>
      Every accepted Client PO line reconciles to the selected Customer Quote values.
    </Alert>}

    {(acceptMutation.isError || cancelPoMutation.isError || convertMutation.isError) && <Alert severity="error" sx={{ mb: 2 }}>
      {apiErrorMessage(
        acceptMutation.error ?? cancelPoMutation.error ?? convertMutation.error,
        'The request was refused.',
      )}
    </Alert>}

    <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={3}>
        <Box><Typography variant="caption" color="text.secondary">CLIENT PO</Typography><Typography sx={{ fontWeight: 800 }}>{match.header.internalNumber}</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">CUSTOMER QUOTE</Typography><Typography sx={{ fontWeight: 800 }}>{match.header.quoteNumber || 'Review required'}</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">AWARD</Typography><Typography sx={{ fontWeight: 800 }}>{match.awardNumber || 'Not accepted'} · {statusLabel(match.awardStatus || 'REVIEW')}</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">PO DATE</Typography><Typography sx={{ fontWeight: 800 }}>{new Date(match.poDate).toLocaleDateString()}</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">STATUS</Typography><Typography sx={{ fontWeight: 800 }}>{statusLabel(match.header.status)}</Typography></Box>
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
          <TableCell align="right">
            {quantityWithUnit(line.orderedQuantity, line.purchaseOrderUomCode)} / {line.acceptedQuantity ?? 0}
          </TableCell>
          <TableCell align="right">{quantityWithUnit(line.quotedQuantity, line.quotedUomCode)}</TableCell>
          <TableCell align="right">{money(line.purchaseOrderUnitPrice, match.currencyCode)}</TableCell>
          <TableCell align="right">{money(line.quotedUnitPrice, match.currencyCode)}</TableCell>
          {lineDecision(line)}
        </TableRow>)}</TableBody>
      </Table>
    </TableContainer>

    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ mt: 2, flexWrap: 'wrap' }}>
      <Button endIcon={<OpenInNew />} onClick={() => navigate(`/customers/${match.customerId}`)}>Open customer record</Button>
      {canAccept && <Button
        color="warning"
        variant="contained"
        disabled={busy}
        onClick={() => { setReason(''); setReasonFor('accept'); }}
      >
        {`Accept ${blocking.length} difference${blocking.length === 1 ? '' : 's'}`}
      </Button>}
      {canConvert && <Button
        variant="contained"
        startIcon={<ShoppingCartCheckout />}
        disabled={busy}
        onClick={() => convertMutation.mutate()}
      >
        Create sales order
      </Button>}
      {!cancelled && <Button
        color="error"
        disabled={busy || liveAward}
        title={liveAward ? 'Cancel the award before withdrawing the purchase order.' : undefined}
        onClick={() => { setReason(''); setReasonFor('cancel-po'); }}
      >
        Cancel this Client PO
      </Button>}
    </Stack>
    {!cancelled && liveAward && <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
      This Client PO cannot be withdrawn while award {match.awardNumber} is {statusLabel(match.awardStatus || '')}.
      Cancel the award first; if it has already become a sales order, that order has to be reversed.
    </Typography>}

    <Dialog open={reasonFor !== null} onClose={() => setReasonFor(null)} fullWidth maxWidth="sm">
      <DialogTitle>{reasonFor === 'accept' ? 'Accept these differences' : 'Cancel this Client PO'}</DialogTitle>
      <DialogContent>
        <DialogContentText sx={{ mb: 2 }}>
          {reasonFor === 'accept'
            ? 'The sales order will be raised from our quotation, not from the buyer’s figures. Your name and this reason are recorded against the award.'
            : 'The purchase order is withdrawn and the reason is stored on the record. This cannot be undone.'}
        </DialogContentText>
        <TextField
          fullWidth
          multiline
          minRows={2}
          label="Reason"
          value={reason}
          onChange={(event) => setReason(event.target.value)}
          slotProps={{ htmlInput: { maxLength: 500 } }}
        />
      </DialogContent>
      <DialogActions>
        <Button onClick={() => setReasonFor(null)}>Back</Button>
        <Button variant="contained" disabled={!reason.trim()} onClick={submitReason}>
          {reasonFor === 'accept' ? 'Accept and record' : 'Cancel the PO'}
        </Button>
      </DialogActions>
    </Dialog>
  </Box>;
}
