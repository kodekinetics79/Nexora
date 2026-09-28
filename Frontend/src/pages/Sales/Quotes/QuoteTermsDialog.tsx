import React from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import {
  Alert, Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle, MenuItem, Stack, TextField, Typography,
} from '@mui/material';
import rfqService from '../../../api/services/rfqService';
import currencyService from '../../../api/services/currencyService';
import { presentableErrorMessage } from '../../../utils/apiErrors';

const isoDay = (date: Date) => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
const daysFromToday = (days: number) => { const d = new Date(); d.setDate(d.getDate() + days); return isoDay(d); };

export interface QuoteTermsDialogProps {
  open: boolean;
  quoteId: number;
  businessUnitId?: number;
  /** The quote's currency; when missing the rep picks one here. */
  currencyId?: number | null;
  validUntil?: string | null;
  /** The earliest date the buyer accepts (send-readiness buyer terms), used as the default. */
  suggestedValidUntil?: string | null;
  /** The buyer's allowed currencies, preferred when picking a default. */
  allowedCurrencies?: string[];
  onCancel: () => void;
  onSaved: () => void;
}

/**
 * The two facts a quote document cannot be made without — how long the prices hold and the
 * currency — asked for right where the PDF was refused, instead of a toast that says what is
 * missing and leaves the rep to find where to set it (pilot audit UX-06 / P0 #11).
 */
export default function QuoteTermsDialog({
  open, quoteId, businessUnitId, currencyId, validUntil, suggestedValidUntil, allowedCurrencies, onCancel, onSaved,
}: QuoteTermsDialogProps) {
  const [date, setDate] = React.useState('');
  const [currency, setCurrency] = React.useState<number | ''>('');
  const currencies = useQuery({
    queryKey: ['currencies-for-quote', businessUnitId],
    queryFn: async () => (await currencyService.getAll({ businessUnitId, pageNumber: 1, pageSize: 100, isActive: true })).items ?? [],
    enabled: open && !currencyId,
    staleTime: 10 * 60 * 1000,
  });
  const options: { id: number; code: string; isBaseCurrency?: boolean }[] = currencies.data ?? [];

  React.useEffect(() => {
    if (!open) return;
    const existing = validUntil ? validUntil.split('T')[0] : '';
    const suggested = suggestedValidUntil ? suggestedValidUntil.split('T')[0] : '';
    const floor = daysFromToday(30);
    setDate(existing && existing >= isoDay(new Date()) ? existing : suggested > floor ? suggested : floor);
  }, [open, validUntil, suggestedValidUntil]);
  React.useEffect(() => {
    if (!open || currencyId || currency !== '' || options.length === 0) return;
    const allowed = (allowedCurrencies ?? []).map((c) => c.toUpperCase());
    const pick = options.find((c) => c.isBaseCurrency && allowed.includes(c.code.toUpperCase()))
      ?? options.find((c) => allowed.includes(c.code.toUpperCase()))
      ?? options.find((c) => c.isBaseCurrency) ?? options[0];
    setCurrency(pick.id);
  }, [open, currencyId, currency, options, allowedCurrencies]);

  const save = useMutation({
    mutationFn: () => rfqService.saveQuoteTerms(quoteId, { currencyId: currencyId ? null : (currency as number), validUntil: date }),
    onSuccess: () => onSaved(),
  });
  const dateOk = date !== '' && date >= isoDay(new Date());
  const currencyOk = !!currencyId || currency !== '';

  return (
    <Dialog open={open} onClose={save.isPending ? undefined : onCancel} maxWidth="xs" fullWidth>
      <DialogTitle sx={{ fontWeight: 800 }}>Set how long the prices hold</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <TextField type="date" label="Prices valid until" value={date} error={date !== '' && !dateOk}
            onChange={(event) => setDate(event.target.value)}
            slotProps={{ inputLabel: { shrink: true }, htmlInput: { min: isoDay(new Date()), 'aria-label': 'Prices valid until' } }} />
          {suggestedValidUntil && (
            <Typography variant="caption" color="text.secondary">
              Buyer asks for at least {new Date(`${suggestedValidUntil.split('T')[0]}T00:00:00`).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })}.
            </Typography>
          )}
          {!currencyId && (
            <TextField select label="Currency" value={currency} onChange={(event) => setCurrency(Number(event.target.value))}>
              {options.map((c) => <MenuItem key={c.id} value={c.id}>{c.code}</MenuItem>)}
            </TextField>
          )}
          {save.isError && <Alert severity="error">{presentableErrorMessage(save.error, 'The quote could not be updated.')}</Alert>}
        </Stack>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        <Button onClick={onCancel} disabled={save.isPending}>Cancel</Button>
        <Button variant="contained" disabled={!dateOk || !currencyOk || save.isPending}
          startIcon={save.isPending ? <CircularProgress size={16} color="inherit" /> : undefined}
          onClick={() => save.mutate()}>
          Save and download
        </Button>
      </DialogActions>
    </Dialog>
  );
}
