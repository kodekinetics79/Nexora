import React from 'react';
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  FormControl,
  FormControlLabel,
  FormLabel,
  Radio,
  RadioGroup,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import quoteService, {
  type PriceAttestationSource,
  type QuotePriceAttestationStatus,
} from '../../../api/services/quoteService';

interface PriceConfirmationDialogProps {
  open: boolean;
  quoteId: number;
  quoteNo?: string;
  recipientEmail: string;
  /** true while the confirm + send round trip is in flight. */
  submitting?: boolean;
  onCancel: () => void;
  onConfirm: (source: PriceAttestationSource, sourceReference: string) => void;
}

const SOURCE_OPTIONS: { value: PriceAttestationSource; label: string; referenceLabel: string; helper: string }[] = [
  {
    value: 'SALES_MANAGER',
    label: 'My sales manager gave me these prices',
    referenceLabel: 'Name of the sales manager',
    helper: 'Record who gave you the prices, so the quote can be checked against them later.',
  },
  {
    value: 'SUPPLIER_QUOTE',
    label: 'I took these prices from a supplier quote',
    referenceLabel: 'Supplier quote reference',
    helper: 'Record the supplier quote number the prices came from, for example SQ-4471.',
  },
];

const formatPrice = (value: number, currencyCode?: string | null) => {
  const amount = Number(value || 0).toLocaleString(undefined, {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
  return currencyCode ? `${currencyCode} ${amount}` : amount;
};

/**
 * Decision Register R5 — confirm the price source before a quote goes to the customer.
 *
 * This dialog is a courtesy, not the control: the send endpoint refuses any quote whose
 * current prices are not covered by a recorded confirmation, and a later price edit voids
 * the confirmation server-side. The dialog exists so the rep sees exactly what they are
 * about to quote, and gets the chance to catch a mistake before the customer does.
 */
const PriceConfirmationDialog: React.FC<PriceConfirmationDialogProps> = ({
  open,
  quoteId,
  quoteNo,
  recipientEmail,
  submitting = false,
  onCancel,
  onConfirm,
}) => {
  const [source, setSource] = React.useState<PriceAttestationSource | ''>('');
  const [reference, setReference] = React.useState('');
  const [touched, setTouched] = React.useState(false);

  const { data, isLoading, isError, refetch } = useQuery<QuotePriceAttestationStatus>({
    queryKey: ['quote-price-attestation', quoteId],
    queryFn: () => quoteService.getPriceAttestation(quoteId),
    enabled: open && quoteId > 0,
    staleTime: 0,
  });

  React.useEffect(() => {
    if (open) {
      setSource('');
      setReference('');
      setTouched(false);
    }
  }, [open, quoteId]);

  const selected = SOURCE_OPTIONS.find((option) => option.value === source);
  const referenceMissing = reference.trim().length === 0;
  const canSubmit = Boolean(source) && !referenceMissing && !submitting && !isLoading;

  const handleSubmit = () => {
    setTouched(true);
    if (!source || referenceMissing) return;
    onConfirm(source, reference.trim());
  };

  const lines = data?.currentLines ?? [];
  const total = lines.reduce((sum, line) => sum + Number(line.quantity || 0) * Number(line.unitPrice || 0), 0);

  return (
    <Dialog open={open} onClose={submitting ? undefined : onCancel} maxWidth="md" fullWidth>
      <DialogTitle sx={{ fontWeight: 900 }}>
        Confirm the prices before sending
      </DialogTitle>
      <DialogContent dividers>
        <Typography variant="body2" sx={{ mb: 2 }}>
          {quoteNo ? `Quote ${quoteNo}` : 'This quote'} is about to be emailed to{' '}
          <strong>{recipientEmail}</strong>. Check every price below, then record where the
          prices came from. If anything is wrong, cancel and correct the quote first.
        </Typography>

        {data?.supersededByPriceChange && data.confirmedBy && (
          <Alert severity="warning" sx={{ mb: 2 }}>
            {data.confirmedBy} confirmed the prices on{' '}
            {new Date(data.confirmedOn as string).toLocaleString()}, but a price has changed since.
            The prices must be confirmed again before this quote can be sent.
          </Alert>
        )}

        {isError && (
          <Alert
            severity="error"
            sx={{ mb: 2 }}
            action={<Button color="inherit" onClick={() => refetch()}>Retry</Button>}
          >
            We couldn't load the prices on this quote.
          </Alert>
        )}

        {isLoading ? (
          <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}>
            <CircularProgress size={28} />
          </Box>
        ) : (
          <Box sx={{ overflowX: 'auto', mb: 3 }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={{ fontWeight: 700 }}>Item</TableCell>
                  <TableCell align="right" sx={{ fontWeight: 700 }}>Quantity</TableCell>
                  <TableCell align="right" sx={{ fontWeight: 700 }}>Unit price</TableCell>
                  <TableCell align="right" sx={{ fontWeight: 700 }}>Line total</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {lines.map((line) => (
                  <TableRow key={line.quoteItemId}>
                    <TableCell>{line.itemDescription || `Line ${line.quoteItemId}`}</TableCell>
                    <TableCell align="right">{Number(line.quantity || 0).toLocaleString()}</TableCell>
                    <TableCell align="right" sx={{ fontWeight: 700 }}>
                      {formatPrice(line.unitPrice, data?.currencyCode)}
                    </TableCell>
                    <TableCell align="right">
                      {formatPrice(Number(line.quantity || 0) * Number(line.unitPrice || 0), data?.currencyCode)}
                    </TableCell>
                  </TableRow>
                ))}
                {lines.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={4}>
                      <Typography variant="body2" color="text.secondary">
                        This quote has no priced lines.
                      </Typography>
                    </TableCell>
                  </TableRow>
                )}
              </TableBody>
            </Table>
            {lines.length > 0 && (
              <Typography variant="subtitle2" sx={{ mt: 1, textAlign: 'right', fontWeight: 900 }}>
                Total of the lines above: {formatPrice(total, data?.currencyCode)}
              </Typography>
            )}
          </Box>
        )}

        <Divider sx={{ mb: 2 }} />

        <Stack spacing={2}>
          <FormControl>
            <FormLabel sx={{ fontWeight: 700, mb: 0.5 }}>Where did these prices come from?</FormLabel>
            <RadioGroup value={source} onChange={(event) => setSource(event.target.value as PriceAttestationSource)}>
              {SOURCE_OPTIONS.map((option) => (
                <FormControlLabel
                  key={option.value}
                  value={option.value}
                  control={<Radio />}
                  label={option.label}
                  disabled={submitting}
                />
              ))}
            </RadioGroup>
            {touched && !source && (
              <Typography variant="caption" color="error">
                Choose where the prices came from.
              </Typography>
            )}
          </FormControl>

          <TextField
            label={selected?.referenceLabel || 'Price source reference'}
            value={reference}
            onChange={(event) => setReference(event.target.value)}
            disabled={!source || submitting}
            required
            fullWidth
            slotProps={{ htmlInput: { maxLength: 200 } }}
            error={touched && Boolean(source) && referenceMissing}
            helperText={
              touched && Boolean(source) && referenceMissing
                ? 'This is required — it is what makes the price checkable later.'
                : selected?.helper || 'Choose a price source first.'
            }
          />
        </Stack>
      </DialogContent>
      <DialogActions sx={{ px: 3, py: 2 }}>
        <Button onClick={onCancel} disabled={submitting}>
          Cancel
        </Button>
        <Button
          variant="contained"
          onClick={handleSubmit}
          disabled={!canSubmit}
          startIcon={submitting ? <CircularProgress size={16} color="inherit" /> : undefined}
        >
          {submitting ? 'Sending…' : 'Confirm prices and send'}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default PriceConfirmationDialog;
