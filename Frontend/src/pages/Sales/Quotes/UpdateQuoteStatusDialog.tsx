import React from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle, Divider,
  FormControlLabel, MenuItem, Radio, RadioGroup, Stack, TextField, Typography,
} from '@mui/material';
import { toast } from 'react-hot-toast';
import quoteService, {
  type QuoteCountsAs, type QuoteDTO, type QuoteOutcome, type QuoteStatusCatalog,
} from '../../../api/services/quoteService';
import { presentableErrorMessage } from '../../../utils/apiErrors';

/** What the rep picked, encoded as one radio value. */
type Choice =
  | { kind: 'replied' }
  | { kind: 'step'; id: number }
  | { kind: 'ending'; outcome: QuoteOutcome; endingId: number | null };

const encode = (choice: Choice) =>
  choice.kind === 'replied' ? 'replied'
    : choice.kind === 'step' ? `step:${choice.id}`
      : `ending:${choice.outcome}:${choice.endingId ?? ''}`;

const decode = (value: string): Choice | null => {
  if (value === 'replied') return { kind: 'replied' };
  const [kind, a, b] = value.split(':');
  if (kind === 'step') return { kind: 'step', id: Number(a) };
  if (kind === 'ending') return { kind: 'ending', outcome: a as QuoteOutcome, endingId: b ? Number(b) : null };
  return null;
};

const OUTCOMES: { outcome: QuoteOutcome; countsAs: QuoteCountsAs; label: string; radio: 'success' | 'error' | 'default' }[] = [
  { outcome: 'won', countsAs: 'WON', label: 'Won', radio: 'success' },
  { outcome: 'lost', countsAs: 'LOST', label: 'Lost', radio: 'error' },
  { outcome: 'expired', countsAs: 'EXPIRED', label: 'Expired', radio: 'default' },
];

const EMPTY: QuoteStatusCatalog = { steps: [], endings: [], reasons: [] };

export interface UpdateQuoteStatusDialogProps {
  open: boolean;
  quote: QuoteDTO | null;
  onClose: () => void;
  /** Query keys to refresh after a save (the list and the page differ). */
  invalidateKeys?: unknown[][];
}

/**
 * The one window for a quote that is with the customer: where it is now (the client's own steps,
 * or simply "Customer replied") and how it ended (Won / Lost / Expired, and the client's own
 * endings under each). Nothing is picked when it opens.
 *
 * It replaces two buttons ("Customer responded", "Record outcome") and a dialog that opened with
 * "We won it" already chosen.
 */
export default function UpdateQuoteStatusDialog({ open, quote, onClose, invalidateKeys = [['quotes']] }: UpdateQuoteStatusDialogProps) {
  const queryClient = useQueryClient();
  const [value, setValue] = React.useState('');
  const [reasonCode, setReasonCode] = React.useState('');
  const [note, setNote] = React.useState('');

  const current = quote?.subStatusKind === 'STEP' && quote.subStatusId ? `step:${quote.subStatusId}` : '';
  React.useEffect(() => {
    if (!open) return;
    setValue(current);
    setReasonCode('');
    setNote('');
  }, [open, current]);

  const catalog = useQuery({
    queryKey: ['quote-status-catalog'],
    queryFn: () => quoteService.getStatusCatalog(),
    enabled: open,
    staleTime: 5 * 60 * 1000,
  });
  // The reasons that existed before client statuses, so the window still works if the catalog
  // cannot be read.
  const legacyReasons = useQuery({
    queryKey: ['quote-outcome-reasons'],
    queryFn: () => quoteService.getOutcomeReasons(),
    enabled: open && catalog.isError,
    staleTime: 5 * 60 * 1000,
  });
  const statuses = catalog.data ?? EMPTY;
  const choice = decode(value);
  const ending = choice?.kind === 'ending' ? choice : null;
  const countsAs = ending ? OUTCOMES.find((o) => o.outcome === ending.outcome)!.countsAs : null;

  const reasons = React.useMemo(() => {
    if (!countsAs) return [];
    if (catalog.isSuccess) {
      return statuses.reasons
        .filter((r) => r.isActive && !r.isSystem)
        .filter((r) => (r.for ? r.for === countsAs : countsAs !== 'WON'))
        .map((r) => ({ code: r.code, label: r.name }));
    }
    return countsAs === 'WON' ? [] : (legacyReasons.data ?? []).filter((r) => r.code !== 'AUTO_EXPIRED');
  }, [countsAs, catalog.isSuccess, statuses.reasons, legacyReasons.data]);
  const reasonRequired = countsAs === 'LOST' || countsAs === 'EXPIRED';

  const save = useMutation({
    mutationFn: async () => {
      if (!quote || !choice) return;
      if (choice.kind === 'replied') return quoteService.markResponded(quote.id);
      if (choice.kind === 'step') return quoteService.setStep(quote.id, choice.id);
      return quoteService.setOutcome(quote.id, choice.outcome, reasonCode || undefined, note || undefined, choice.endingId);
    },
    onSuccess: () => {
      toast.success(`${quote?.quoteNo ?? 'Quote'} updated`);
      invalidateKeys.forEach((key) => queryClient.invalidateQueries({ queryKey: key }));
      onClose();
    },
  });

  // A refusal from an earlier opening (possibly for another quote on the list) never carries over.
  const resetSave = save.reset;
  React.useEffect(() => { if (open) resetSave(); }, [open, quote?.id, resetSave]);

  const steps = statuses.steps.filter((s) => s.isActive);
  const ready = Boolean(choice) && value !== current && (!reasonRequired || reasonCode !== '');

  return (
    <Dialog open={open} onClose={save.isPending ? undefined : onClose} fullWidth maxWidth="xs">
      <DialogTitle sx={{ fontWeight: 800 }}>Update {quote?.quoteNo ?? 'quote'}</DialogTitle>
      <DialogContent dividers>
        <RadioGroup value={value} onChange={(event) => { setValue(event.target.value); setReasonCode(''); }}>
          <Typography variant="overline" color="text.secondary" sx={{ fontWeight: 700 }}>With the customer</Typography>
          {!quote?.respondedOn && (
            <FormControlLabel value="replied" control={<Radio />} label="Customer replied" />
          )}
          {steps.map((step) => (
            <FormControlLabel key={step.id} value={`step:${step.id}`} control={<Radio />} label={step.name} />
          ))}
          {catalog.isLoading && <CircularProgress size={16} sx={{ my: 1 }} />}

          <Divider sx={{ my: 1.5 }} />
          <Typography variant="overline" color="text.secondary" sx={{ fontWeight: 700 }}>How it ended</Typography>
          {OUTCOMES.map((o) => (
            <React.Fragment key={o.outcome}>
              <FormControlLabel
                value={encode({ kind: 'ending', outcome: o.outcome, endingId: null })}
                control={<Radio color={o.radio} />}
                label={<Typography sx={{ fontWeight: 700 }}>{o.label}</Typography>}
              />
              {statuses.endings.filter((e) => e.isActive && e.countsAs === o.countsAs).map((e) => (
                <FormControlLabel
                  key={e.id}
                  value={encode({ kind: 'ending', outcome: o.outcome, endingId: e.id })}
                  control={<Radio color={o.radio} size="small" />}
                  label={e.name}
                  sx={{ ml: 3 }}
                />
              ))}
            </React.Fragment>
          ))}
        </RadioGroup>

        {ending && (
          <Stack spacing={2} sx={{ mt: 2 }}>
            {(reasonRequired || reasons.length > 0) && (
              <TextField
                select
                fullWidth
                size="small"
                label={reasonRequired ? 'Reason' : 'Reason (optional)'}
                required={reasonRequired}
                value={reasonCode}
                onChange={(event) => setReasonCode(event.target.value)}
              >
                {reasons.map((r) => <MenuItem key={r.code} value={r.code}>{r.label}</MenuItem>)}
              </TextField>
            )}
            <TextField
              fullWidth
              size="small"
              multiline
              minRows={2}
              label="Note (optional)"
              value={note}
              onChange={(event) => setNote(event.target.value.slice(0, 500))}
            />
          </Stack>
        )}

        {save.isError && (
          <Alert severity="error" sx={{ mt: 2 }}>{presentableErrorMessage(save.error, 'The quote could not be updated.')}</Alert>
        )}
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={onClose} color="inherit" disabled={save.isPending}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!ready || save.isPending}
          onClick={() => save.mutate()}
          startIcon={save.isPending ? <CircularProgress size={16} color="inherit" /> : undefined}
          sx={{ fontWeight: 800 }}
        >
          Save
        </Button>
      </DialogActions>
    </Dialog>
  );
}
