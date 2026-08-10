import React, { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Alert, AlertTitle, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography,
} from '@mui/material';
import commercialFinanceService, {
  type ReceivableDocument,
} from '../../../api/services/commercialFinanceService';
import deliveryService from '../../../api/services/deliveryService';
import orderService from '../../../api/services/orderService';
import { presentableErrorMessage, toPresentableError } from '../../../utils/apiErrors';
import {
  CAP_REASON_LABEL,
  buildInvoiceLineDrafts,
  explainInvoiceConflict,
  invoiceIdempotencyKey,
  linesOverCap,
  newInvoiceSessionNonce,
  sumIssuedInvoicedQuantities,
  toInvoiceLineCommands,
  type InvoiceConflictExplanation,
  type InvoiceLineDraft,
} from './invoiceFromOrder';

/**
 * Gate 7 / FR-DLM-02 — invoicing what the customer actually accepted.
 *
 * The order-to-cash spine ended here. `POST /orders/{id}/invoices` was reachable from exactly one
 * icon, that icon posted `lines: null`, and the server reads a null line set as "bill the whole
 * ordered quantity". So the normal outcome of a delivery — a customer signing for fewer units than
 * left the warehouse — could not be invoiced at all: the accepted-quantity ceiling refused it,
 * correctly, and there was no screen in the product that could ask for the smaller number.
 *
 * This is that screen. Three rules govern it:
 *
 *  1. **The cap is a number on the page, not a hidden clamp.** Every row shows what was ordered,
 *     what left, what the customer took, what is already billed, and the resulting ceiling. Typing
 *     above the ceiling is not silently corrected — the field turns red, says which number it
 *     breached, and the submit button is refused. Silently rewriting an operator's figure on a
 *     document that becomes a legal invoice is how nobody notices the invoice is wrong.
 *  2. **A zero ceiling is a state with a name.** A line with nothing accepted is shown, in place,
 *     saying whether nothing has shipped, nothing has been signed for, or everything accepted is
 *     already on an issued invoice. It is never hidden and never a blank cell that reads like a
 *     loading skeleton.
 *  3. **The server is the authority and speaks in its own words.** Every refusal is printed
 *     verbatim. The only thing this screen adds is the product name behind the order line id the
 *     server quoted, because the receivable module has no product names and the operator has no
 *     line ids.
 */

interface Props {
  orderId: number;
  orderNo: string;
  businessUnitId: number;
  onClose: () => void;
  onCreated: (document: ReceivableDocument) => void;
}

/**
 * Wiring-contract failure #11 is a price input that stripped a currency prefix that was not there,
 * producing `NaN`, which serialised to `null` and posted a line with no price. Nothing here is
 * allowed to reach that state: the raw text is kept, parsed explicitly, and a value that is not a
 * finite number is reported as unreadable rather than coerced to zero.
 */
const parseQuantityInput = (text: string): number | null => {
  const trimmed = text.trim();
  if (trimmed === '') return 0;
  const value = Number(trimmed);
  if (!Number.isFinite(value) || value < 0) return null;
  return value;
};

const InvoiceFromOrderDialog: React.FC<Props> = ({
  orderId, orderNo, businessUnitId, onClose, onCreated,
}) => {
  // One nonce per opening. See invoiceIdempotencyKey for why the key needs both this and the
  // quantities.
  const [sessionNonce] = useState(newInvoiceSessionNonce);
  const [rawQuantities, setRawQuantities] = useState<Record<number, string> | null>(null);
  const [conflict, setConflict] = useState<InvoiceConflictExplanation | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const orderQuery = useQuery({
    queryKey: ['order-for-invoice', orderId, businessUnitId],
    queryFn: () => orderService.getById(orderId, businessUnitId),
    enabled: orderId > 0,
  });

  /** FR-DLM-02. The existing endpoint. Awarded, despatched, awaiting, accepted and refused. */
  const deliveredQuery = useQuery({
    queryKey: ['delivered-quantities', orderId],
    queryFn: () => deliveryService.getDeliveredQuantities(orderId),
    enabled: orderId > 0,
  });

  /**
   * What is already on an ISSUED invoice. Scoped to this customer rather than pulling the whole
   * receivable ledger, and counted by `sumIssuedInvoicedQuantities` with exactly the server's own
   * predicate. A failure here is a named gap below, not a silent zero.
   */
  const customerId = orderQuery.data?.customerId;
  const issuedQuery = useQuery({
    queryKey: ['receivable-documents-for-invoice', customerId],
    queryFn: () => commercialFinanceService.getDocuments({ customerId, status: 'Issued' }),
    enabled: (customerId ?? 0) > 0,
  });

  /**
   * Either the read failed, or there is no customer to scope it to. Both mean the same thing to the
   * operator — the ceilings below are pre-billing — and both are said out loud rather than left to
   * look like "nothing has been invoiced".
   */
  const alreadyInvoicedUnknown = issuedQuery.isError
    || (orderQuery.isSuccess && !((customerId ?? 0) > 0));

  const drafts: InvoiceLineDraft[] = useMemo(() => {
    if (!orderQuery.data || !deliveredQuery.data) return [];
    return buildInvoiceLineDrafts(
      deliveredQuery.data,
      orderQuery.data.items ?? [],
      alreadyInvoicedUnknown || !issuedQuery.data
        ? null
        : sumIssuedInvoicedQuantities(issuedQuery.data, orderId),
    );
  }, [orderQuery.data, deliveredQuery.data, issuedQuery.data, alreadyInvoicedUnknown, orderId]);

  // Pre-filled with the accepted quantity: invoicing exactly what the customer took is the sensible
  // default, and it is still a value the operator sees and can change before anything is posted.
  const quantitiesText: Record<number, string> = useMemo(() => {
    if (rawQuantities) return rawQuantities;
    return Object.fromEntries(drafts.map((draft) => [draft.orderItemId, String(draft.cap)]));
  }, [rawQuantities, drafts]);

  const setQuantity = (orderItemId: number, text: string) => {
    setConflict(null);
    setRawQuantities({ ...quantitiesText, [orderItemId]: text });
  };

  const unreadable = drafts
    .filter((draft) => parseQuantityInput(quantitiesText[draft.orderItemId] ?? '') === null);

  const quantities = useMemo(() => new Map(drafts.map((draft) => [
    draft.orderItemId,
    parseQuantityInput(quantitiesText[draft.orderItemId] ?? '') ?? 0,
  ])), [drafts, quantitiesText]);

  const overCap = linesOverCap(drafts, quantities);
  const commands = toInvoiceLineCommands(drafts, quantities);
  const totalUnits = commands.reduce((sum, line) => sum + line.quantity, 0);
  const canSubmit = commands.length > 0 && overCap.length === 0 && unreadable.length === 0
    && !submitting;

  const submit = async () => {
    setSubmitting(true);
    setConflict(null);
    try {
      const document = await commercialFinanceService.createInvoiceFromOrder(
        orderId, commands, invoiceIdempotencyKey(orderId, sessionNonce, commands),
      );
      onCreated(document);
    } catch (error) {
      const presented = toPresentableError(error, {
        fallbackMessage: 'The invoice draft was refused.',
      });
      setConflict(explainInvoiceConflict(presented.message, drafts, quantities));
    } finally {
      setSubmitting(false);
    }
  };

  // The issued-document read is part of the ceiling, so the table is not drawn until it settles.
  // Drawing it early would show a ceiling of "everything accepted" for a beat, which is a number
  // an operator can read, believe and act on before it silently tightens under them.
  const loading = orderQuery.isLoading || deliveredQuery.isLoading || issuedQuery.isLoading;
  const loadFailed = orderQuery.isError || deliveredQuery.isError;

  return (
    <Dialog open fullWidth maxWidth="lg" onClose={onClose}>
      <DialogTitle sx={{ fontWeight: 800 }}>
        Invoice what the customer accepted — order {orderNo}
      </DialogTitle>
      <DialogContent dividers>
        {loading && (
          <Box sx={{ display: 'flex', justifyContent: 'center', p: 4 }}><CircularProgress /></Box>
        )}

        {loadFailed && (
          <Alert severity="error">
            <AlertTitle>The delivered quantities could not be loaded</AlertTitle>
            {presentableErrorMessage(
              orderQuery.error ?? deliveredQuery.error,
              'Without them there is no ceiling to invoice against, so nothing can be billed from here yet.',
            )}
          </Alert>
        )}

        {!loading && !loadFailed && (
          <Stack spacing={2}>
            <Typography variant="body2" color="text.secondary">
              Only units the customer has <strong>signed for</strong> can be invoiced. Units that
              left the warehouse and have not been confirmed are with the carrier, not with the
              customer, and the server refuses to bill them.
            </Typography>

            {alreadyInvoicedUnknown && (
              <Alert severity="warning">
                <AlertTitle>Already-invoiced quantities could not be read</AlertTitle>
                The ceilings below are what the customer accepted in total, before anything that is
                already on an issued invoice. If a line has been billed before, the server will
                refuse it and say by how much.
              </Alert>
            )}

            {conflict && (
              <Alert severity="error" onClose={() => setConflict(null)}>
                <AlertTitle>This invoice was refused</AlertTitle>
                {conflict.context && (
                  <Typography variant="body2" sx={{ fontWeight: 700, mb: 0.5 }}>
                    {conflict.context}
                  </Typography>
                )}
                <Typography variant="body2">{conflict.serverDetail}</Typography>
              </Alert>
            )}

            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={{ fontWeight: 800 }}>Item</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Ordered</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Despatched</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Awaiting confirmation</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Accepted</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Already invoiced</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Can invoice now</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Invoice quantity</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {drafts.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={8} align="center" sx={{ py: 4 }}>
                      The delivery ledger holds no active awarded line for this order, so there is
                      nothing to invoice.
                    </TableCell>
                  </TableRow>
                )}
                {drafts.map((draft) => {
                  const text = quantitiesText[draft.orderItemId] ?? '';
                  const parsed = parseQuantityInput(text);
                  const isOver = parsed !== null && parsed > draft.cap;
                  const isBlocked = draft.cap <= 0;
                  return (
                    <TableRow
                      key={draft.orderItemId}
                      sx={conflict?.orderItemId === draft.orderItemId
                        ? { bgcolor: 'error.light' } : undefined}
                    >
                      <TableCell>
                        <Typography variant="body2" sx={{ fontWeight: 600 }}>
                          {draft.productName}
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                          Order line {draft.orderItemId}
                        </Typography>
                      </TableCell>
                      <TableCell align="right">{draft.orderedQuantity}</TableCell>
                      <TableCell align="right">{draft.despatchedQuantity}</TableCell>
                      <TableCell align="right">{draft.awaitingConfirmationQuantity}</TableCell>
                      <TableCell align="right" sx={{ fontWeight: 700 }}>
                        {draft.acceptedQuantity}
                      </TableCell>
                      <TableCell align="right">
                        {draft.alreadyInvoicedQuantity === null
                          ? <Typography variant="caption" color="text.secondary">Not known</Typography>
                          : draft.alreadyInvoicedQuantity}
                      </TableCell>
                      <TableCell align="right">
                        {/* The cap, as a visible number. Never only an input `max`. */}
                        <Typography variant="body2" sx={{ fontWeight: 800 }}>{draft.cap}</Typography>
                      </TableCell>
                      <TableCell align="right" sx={{ width: 220 }}>
                        {isBlocked ? (
                          <Chip
                            size="small"
                            color="warning"
                            variant="outlined"
                            label={CAP_REASON_LABEL[draft.capReason]}
                            sx={{ fontWeight: 700 }}
                          />
                        ) : (
                          <TextField
                            size="small"
                            type="number"
                            value={text}
                            error={isOver || parsed === null}
                            label={`Max ${draft.cap}`}
                            slotProps={{
                              htmlInput: {
                                min: 0,
                                max: draft.cap,
                                step: 'any',
                                'aria-label': `Invoice quantity for ${draft.productName}`,
                              },
                              inputLabel: { shrink: true },
                            }}
                            helperText={
                              parsed === null
                                ? 'That is not a quantity.'
                                : isOver
                                  ? `Above the ${draft.cap} the customer has accepted and not yet been billed for.`
                                  : ' '
                            }
                            onChange={(event) => setQuantity(draft.orderItemId, event.target.value)}
                          />
                        )}
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>

            {overCap.length > 0 && (
              <Alert severity="error">
                <AlertTitle>One or more lines are above their ceiling</AlertTitle>
                {overCap.map((draft) => (
                  <Typography key={draft.orderItemId} variant="body2">
                    {draft.productName} (order line {draft.orderItemId}) may be invoiced for at most{' '}
                    {draft.cap}. Nothing has been changed for you — correct it, or record the
                    delivery that covers the difference.
                  </Typography>
                ))}
              </Alert>
            )}

            <Typography variant="body2" sx={{ fontWeight: 700 }}>
              {commands.length === 0
                ? 'No line has a quantity to invoice.'
                : `${totalUnits} unit(s) across ${commands.length} line(s).`}
            </Typography>
          </Stack>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={!canSubmit} onClick={() => { void submit(); }}>
          Create invoice draft
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default InvoiceFromOrderDialog;
