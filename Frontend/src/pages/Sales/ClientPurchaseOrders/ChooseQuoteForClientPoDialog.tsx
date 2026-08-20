import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Dialog, DialogContent, DialogTitle, IconButton,
  Paper, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Typography,
} from '@mui/material';
import { Close, FindInPage, Search } from '@mui/icons-material';
import quoteService, { type QuoteDTO } from '../../../api/services/quoteService';
import type { CustomerAwardQuote } from '../Quotes/customer-awards';

/**
 * The quotations a customer purchase order may be filed against, as the SERVER defines them.
 *
 * `CustomerAwardApplicationService.LoadEligibleQuoteAsync` accepts SENT, ACCEPTED or ORDERED, plus
 * the two legacy status ids that predate the setup codes, and refuses anything that has been
 * superseded by a revision. The list endpoint already drops ORDERED and withdrawn quotes, so what
 * is left to mirror here is the status rule.
 *
 * Mirrored rather than invented: a picker that offers a quote the server will refuse teaches people
 * that the error is random, and one that hides a quote the server would accept sends them back to
 * re-keying the PO by hand on the quote screen. Where the two ever disagree the server still wins —
 * this only decides what is worth showing.
 */
const LEGACY_ELIGIBLE_STATUS_IDS = [43, 44];
const ELIGIBLE_STATUSES = ['SENT', 'ACCEPTED', 'ORDERED'];

const isEligible = (quote: QuoteDTO) => {
  const status = (quote.statusCode || quote.statusValue || '').toUpperCase();
  const statusAllows = ELIGIBLE_STATUSES.includes(status)
    || LEGACY_ELIGIBLE_STATUS_IDS.includes(Number(quote.statusId));
  // The identity triple the create-PO command requires. A quote missing any of it cannot carry a
  // purchase order into the spine, so offering it would produce a 400 nobody can act on.
  return statusAllows && Boolean(quote.commercialCaseId && quote.customerId && quote.currencyId);
};

/**
 * Builds the shape `CustomerAwardWorkspace` needs from a full quote read.
 *
 * The list response has the identity fields but not the lines, so the chosen quote is re-read by
 * id. Returning null rather than a partly-filled object is deliberate: the workspace compares the
 * buyer's document against these lines, and a quote whose lines did not load would compare it
 * against nothing and report an exact match.
 */
const toAwardQuote = (quote: QuoteDTO): CustomerAwardQuote | null => {
  if (!quote.commercialCaseId || !quote.customerId || !quote.currencyId) return null;
  return {
    id: quote.id,
    quoteNo: quote.quoteNo,
    version: quote.version,
    commercialCaseId: quote.commercialCaseId,
    customerId: quote.customerId,
    currencyId: quote.currencyId,
    currencyCode: quote.currencyCode,
    lines: (quote.quoteItems ?? []).map((item: any) => ({
      id: item.id,
      productId: item.productId,
      productName: item.productName,
      description: item.itemDescription || item.productName || `Quote line ${item.id}`,
      quantity: item.quantity,
      unitPrice: item.unitPrice,
    })),
  };
};

export interface ChooseQuoteForClientPoDialogProps {
  open: boolean;
  onClose: () => void;
  onChosen: (quote: CustomerAwardQuote) => void;
}

/**
 * "Upload the client PO and hook it to the quote", started from the Client PO Inbox.
 *
 * The capture workspace — and with it the document upload — was reachable only from a single quote's
 * view page, behind a button that appears when that quote is Accepted. So the screen named after the
 * capability was the one screen that could not perform it: a user holding a buyer's PDF had to know
 * which quotation it answered, find it, open it, and press a button named something else. This is
 * the same workspace reached from the document's own side, which is the side the user is on.
 *
 * It chooses the quote and nothing more. Everything downstream — the extraction, the buyer-figure
 * capture, the award, the sales order — is the existing `CustomerAwardDialog`, so there is exactly
 * one implementation of the matching rules and one set of gates.
 */
export default function ChooseQuoteForClientPoDialog({
  open, onClose, onChosen,
}: ChooseQuoteForClientPoDialogProps) {
  const [search, setSearch] = useState('');
  const [loadingId, setLoadingId] = useState<number | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const query = useQuery({
    queryKey: ['client-po-quote-picker', search],
    queryFn: () => quoteService.getAll({ search: search.trim() || undefined, pageSize: 50 }),
    enabled: open,
  });

  const eligible = (query.data?.items ?? []).filter(isEligible);

  const choose = async (quoteId: number) => {
    setLoadError(null);
    setLoadingId(quoteId);
    try {
      const full = await quoteService.getById(quoteId);
      const awardQuote = toAwardQuote(full);
      if (!awardQuote || awardQuote.lines.length === 0) {
        setLoadError('This quotation has no priced lines to match a purchase order against.');
        return;
      }
      onChosen(awardQuote);
    } catch {
      setLoadError('The quotation could not be opened. Nothing has been saved.');
    } finally {
      setLoadingId(null);
    }
  };

  return <Dialog open={open} onClose={onClose} fullWidth maxWidth="md">
    <DialogTitle sx={{ pr: 7, fontWeight: 800 }}>
      Which quotation is this Client PO answering?
      <IconButton aria-label="Close quote picker" onClick={onClose} sx={{ position: 'absolute', top: 10, right: 10 }}>
        <Close />
      </IconButton>
    </DialogTitle>
    <DialogContent dividers>
      <Typography color="text.secondary" sx={{ mb: 2 }}>
        The purchase order is filed against this quotation and takes its commercial case and Nexora
        Serial, so the sales order it becomes stays on the same case as the lead it started from.
      </Typography>

      <TextField
        fullWidth
        size="small"
        label="Search by quote number, Nexora Serial, or customer"
        value={search}
        onChange={(event) => setSearch(event.target.value)}
        slotProps={{ input: { startAdornment: <Search sx={{ mr: 1, color: 'text.secondary' }} /> } }}
        sx={{ mb: 2 }}
      />

      {loadError && <Alert severity="error" sx={{ mb: 2 }}>{loadError}</Alert>}
      {query.isLoading && <Box sx={{ minHeight: 200, display: 'grid', placeItems: 'center' }}><CircularProgress /></Box>}
      {query.isError && <Alert severity="error" action={<Button color="inherit" onClick={() => query.refetch()}>Retry</Button>}>
        Quotations could not be loaded. No empty result has been assumed.
      </Alert>}

      {query.data && eligible.length === 0 && <Paper variant="outlined" sx={{ p: 4, textAlign: 'center' }}>
        <FindInPage color="disabled" sx={{ fontSize: 40 }} />
        <Typography sx={{ fontWeight: 700, mt: 1 }}>No quotation here can take a Client PO</Typography>
        <Typography variant="body2" color="text.secondary">
          A purchase order can only be filed against a quotation that has been sent or accepted and
          that carries a commercial case, a customer and a currency.
        </Typography>
      </Paper>}

      {eligible.length > 0 && <TableContainer component={Paper} variant="outlined">
        <Table size="small">
          <TableHead><TableRow>
            <TableCell>Quote</TableCell><TableCell>Commercial lineage</TableCell>
            <TableCell>Customer</TableCell><TableCell>Status</TableCell>
            <TableCell align="right">Action</TableCell>
          </TableRow></TableHead>
          <TableBody>{eligible.map((quote) => <TableRow hover key={quote.id}>
            <TableCell><Typography sx={{ fontWeight: 800 }}>{quote.quoteNo}</Typography></TableCell>
            <TableCell sx={{ fontFamily: 'monospace', fontWeight: 700 }}>
              {quote.nexoraSerial || quote.commercialCaseReference || 'No serial'}
            </TableCell>
            <TableCell>{quote.customerName}</TableCell>
            <TableCell><Chip size="small" label={quote.statusValue} /></TableCell>
            <TableCell align="right">
              <Button
                variant="contained"
                size="small"
                disabled={loadingId !== null}
                onClick={() => choose(quote.id)}
              >
                {loadingId === quote.id ? 'Opening' : 'Upload PO'}
              </Button>
            </TableCell>
          </TableRow>)}</TableBody>
        </Table>
      </TableContainer>}

      <Stack sx={{ mt: 2 }}>
        <Typography variant="caption" color="text.secondary">
          Showing the most recent 50 quotations matching the search. Narrow the search if the one you
          need is not listed.
        </Typography>
      </Stack>
    </DialogContent>
  </Dialog>;
}
