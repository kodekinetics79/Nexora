import React from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle,
  Divider, Stack, TextField, Typography,
} from '@mui/material';
import dayjs from 'dayjs';
import { toast } from 'react-hot-toast';
import quoteService from '../../../api/services/quoteService';
import { presentableErrorMessage } from '../../../utils/apiErrors';

interface ExtendValidityDialogProps {
  open: boolean;
  onClose: () => void;
  quoteId: number | null;
  quoteNo?: string;
  /** The quote's current validity date, when it has one. */
  currentValidUntil?: string | null;
  /** Extra query keys to invalidate after saving (list/detail pages differ). */
  invalidateKeys?: unknown[][];
}

const MAX_REASON = 500;

/**
 * Decision Register R7 — hold an already-issued quote's price open for longer.
 *
 * Gulf tender validity is set by the BUYER, commonly 90–120 days and extendable on request,
 * so "can you hold your price for another two weeks" is the single most common thing a buyer
 * says. Before this the rep's only move was to revise the quote, which increments the revision
 * number and reads to the customer as a brand-new commercial offer.
 *
 * The reason is mandatory because R7 makes it mandatory: it is recorded against the quote and
 * shown back here, so the person explaining the bid six weeks later can read why the date moved.
 * Nothing else about the offer changes — same prices, same lines, same revision number.
 */
const ExtendValidityDialog: React.FC<ExtendValidityDialogProps> = ({
  open, onClose, quoteId, quoteNo, currentValidUntil, invalidateKeys = [['quotes']],
}) => {
  const queryClient = useQueryClient();
  const [validUntil, setValidUntil] = React.useState('');
  const [reason, setReason] = React.useState('');
  const [touched, setTouched] = React.useState(false);

  // The earliest date that is genuinely an extension: tomorrow, or the day after the date the
  // customer already holds. Mirrors the server rule so the rep is not told "no" after typing.
  const minDate = React.useMemo(() => {
    const tomorrow = dayjs().add(1, 'day');
    const afterCurrent = currentValidUntil ? dayjs(currentValidUntil).add(1, 'day') : tomorrow;
    return (afterCurrent.isAfter(tomorrow) ? afterCurrent : tomorrow).format('YYYY-MM-DD');
  }, [currentValidUntil]);

  React.useEffect(() => {
    if (open) {
      setValidUntil('');
      setReason('');
      setTouched(false);
    }
  }, [open]);

  const { data: history = [], isLoading: historyLoading } = useQuery({
    queryKey: ['quote-validity-extensions', quoteId],
    queryFn: () => quoteService.getValidityExtensions(quoteId!),
    enabled: open && Boolean(quoteId),
    staleTime: 0,
  });

  const reasonMissing = reason.trim().length === 0;
  const dateMissing = validUntil.trim().length === 0;
  const dateTooEarly = !dateMissing && validUntil < minDate;

  const extendMutation = useMutation({
    mutationFn: () => quoteService.extendValidity(quoteId!, validUntil, reason.trim()),
    onSuccess: (result) => {
      toast.success(
        `Validity extended to ${dayjs(result.validUntil).format('DD MMM YYYY')}. `
        + `Quote ${result.quoteNo} is unchanged otherwise — still revision ${result.revisionNo}.`,
      );
      invalidateKeys.forEach((key) => queryClient.invalidateQueries({ queryKey: key }));
      queryClient.invalidateQueries({ queryKey: ['quote-validity-extensions', quoteId] });
      onClose();
    },
    onError: (error: unknown) => {
      toast.error(presentableErrorMessage(error, 'The validity could not be extended.'), { duration: 6000 });
    },
  });

  const handleSubmit = () => {
    setTouched(true);
    if (reasonMissing || dateMissing || dateTooEarly) return;
    extendMutation.mutate();
  };

  return (
    <Dialog
      open={open}
      onClose={extendMutation.isPending ? undefined : onClose}
      fullWidth
      maxWidth="sm"
    >
      <DialogTitle sx={{ fontWeight: 800 }}>
        Extend validity{quoteNo ? ` — ${quoteNo}` : ''}
      </DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          <Typography variant="body2" color="text.secondary">
            {currentValidUntil
              ? `This quote currently holds until ${dayjs(currentValidUntil).format('DD MMM YYYY')}.`
              : 'This quote was issued without a validity date.'}
            {' '}Extending moves only the expiry. The prices, the lines and the revision number
            stay exactly as the customer already has them.
          </Typography>

          <TextField
            fullWidth
            size="small"
            type="date"
            label="New validity date"
            value={validUntil}
            onChange={(e) => setValidUntil(e.target.value)}
            slotProps={{ inputLabel: { shrink: true }, htmlInput: { min: minDate } }}
            error={touched && (dateMissing || dateTooEarly)}
            helperText={
              touched && dateMissing
                ? 'Choose the new date the quote stays valid until.'
                : touched && dateTooEarly
                  ? `Choose a date on or after ${dayjs(minDate).format('DD MMM YYYY')} — an earlier date would shorten the offer, not extend it.`
                  : undefined
            }
          />

          <TextField
            fullWidth
            size="small"
            multiline
            minRows={2}
            required
            label="Why is it being extended?"
            placeholder="Buyer asked us to hold pricing while the technical evaluation completes"
            value={reason}
            onChange={(e) => setReason(e.target.value.slice(0, MAX_REASON))}
            error={touched && reasonMissing}
            helperText={
              touched && reasonMissing
                ? 'A reason is required — it is recorded against the quote and stays readable afterwards.'
                : `${reason.length}/${MAX_REASON} · recorded against the quote and readable afterwards`
            }
          />

          {history.length > 0 && (
            <>
              <Divider />
              <Typography variant="caption" sx={{ fontWeight: 800, textTransform: 'uppercase' }} color="text.secondary">
                Previously extended
              </Typography>
              <Stack spacing={1}>
                {history.map((entry) => (
                  <Alert key={entry.id} severity="info" variant="outlined" sx={{ py: 0.5 }}>
                    <Typography variant="body2" sx={{ fontWeight: 700 }}>
                      {entry.previousValidUntil
                        ? `${dayjs(entry.previousValidUntil).format('DD MMM YYYY')} → ${dayjs(entry.newValidUntil).format('DD MMM YYYY')}`
                        : `Set to ${dayjs(entry.newValidUntil).format('DD MMM YYYY')}`}
                    </Typography>
                    <Typography variant="body2">{entry.reason}</Typography>
                    <Typography variant="caption" color="text.secondary">
                      {entry.extendedBy} · {dayjs(entry.extendedOn).format('DD MMM YYYY HH:mm')}
                    </Typography>
                  </Alert>
                ))}
              </Stack>
            </>
          )}
          {historyLoading && <Typography variant="caption" color="text.secondary">Loading earlier extensions…</Typography>}
        </Stack>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={onClose} color="inherit" disabled={extendMutation.isPending}>Cancel</Button>
        <Button
          variant="contained"
          onClick={handleSubmit}
          disabled={!quoteId || extendMutation.isPending}
          startIcon={extendMutation.isPending ? <CircularProgress size={16} color="inherit" /> : undefined}
          sx={{ fontWeight: 800 }}
        >
          Extend validity
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default ExtendValidityDialog;
