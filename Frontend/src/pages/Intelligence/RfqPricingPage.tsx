import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Stack, Chip, TextField,
  CircularProgress, Alert, Breadcrumbs, Link, Collapse, InputAdornment, Divider,
} from '@mui/material';
import {
  AutoAwesome as SparkleIcon,
  ArrowBack as BackIcon,
  NavigateNext as NextIcon,
  ExpandMore as ExpandIcon,
  ExpandLess as CollapseIcon,
  ErrorOutlined as AttentionIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import intelligenceService from '../../api/services/intelligenceService';
import type { PriceSignal, PriceSignalSource } from '../../api/services/intelligenceService';
import { ConfidenceChip, formatMoney, parseUserNumber } from './common';

// Friendly names for where a suggestion came from — no jargon.
const SOURCE_LABELS: Record<PriceSignalSource, string> = {
  priceList: 'Your price list',
  recentQuote: 'A recent quote you won',
  supplierQuote: 'Supplier quotes for this RFQ',
  purchaseHistory: 'What you paid before',
  productMaster: 'Product list price',
};

const sourceLabel = (source: PriceSignalSource): string =>
  SOURCE_LABELS[source] ?? 'Something we found';

const SignalRow: React.FC<{ signal: PriceSignal; currency: string | null }> = ({ signal, currency }) => (
  <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 2, py: 0.75 }}>
    <Box>
      <Typography sx={{ fontSize: '0.8rem', fontWeight: 800 }}>
        {sourceLabel(signal.source)}
      </Typography>
      {(signal.label || signal.detail) && (
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
          {[signal.label, signal.detail].filter(Boolean).join(' — ')}
        </Typography>
      )}
    </Box>
    <Typography sx={{ fontSize: '0.8rem', fontWeight: 800, whiteSpace: 'nowrap' }}>
      {formatMoney(signal.value, currency)}
    </Typography>
  </Box>
);

const RfqPricingPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const rfqId = Number(id);

  const { data: preview, isLoading, isError, refetch } = useQuery({
    queryKey: ['rfq-price-preview', rfqId],
    queryFn: () => intelligenceService.getPricePreview(rfqId),
    enabled: !!id && Number.isFinite(rfqId),
  });

  // Editable unit prices, keyed by rfqItemId, kept as strings while typing.
  const [prices, setPrices] = React.useState<Record<number, string>>({});
  const [expanded, setExpanded] = React.useState<Record<number, boolean>>({});

  React.useEffect(() => {
    if (!preview) return;
    setPrices((prev) => {
      if (Object.keys(prev).length > 0) return prev;
      const next: Record<number, string> = {};
      for (const line of preview.lines) {
        next[line.rfqItemId] = line.recommendedUnitPrice != null ? String(line.recommendedUnitPrice) : '';
      }
      return next;
    });
  }, [preview]);

  const applyMutation = useMutation({
    mutationFn: () => {
      const lines = (preview?.lines ?? [])
        .map((line) => ({ rfqItemId: line.rfqItemId, unitPrice: parseUserNumber(prices[line.rfqItemId] ?? '') }))
        .filter((l): l is { rfqItemId: number; unitPrice: number } => l.unitPrice != null);
      return intelligenceService.applyPricing(rfqId, { lines });
    },
    onSuccess: () => {
      enqueueSnackbar('Pricing applied — ready to quote', { variant: 'success' });
      navigate(`/procurement/rfqs/view/${rfqId}`);
    },
    onError: (error: any) => {
      // WP-B3: a 409 with queuedForApproval means nothing was applied — the
      // below-floor prices are parked in the Approvals inbox for a manager.
      const data = error?.response?.data;
      if (error?.response?.status === 409 && data?.queuedForApproval) {
        enqueueSnackbar(
          data.message || 'Sent for approval — pricing is below your floor. Track it in Approvals.',
          { variant: 'info', autoHideDuration: 8000 },
        );
        return;
      }
      enqueueSnackbar("Couldn't apply the pricing. Your edits are still here — please try again.", { variant: 'error' });
    },
  });

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', height: '60vh', gap: 2 }}>
        <CircularProgress />
        <Typography color="text.secondary" sx={{ fontWeight: 600 }}>
          Working out prices from your history…
        </Typography>
      </Box>
    );
  }

  if (isError || !preview) {
    return (
      <Box sx={{ p: 4, maxWidth: 640, mx: 'auto' }}>
        <Alert severity="error" sx={{ borderRadius: 2, mb: 2 }}>
          We couldn't put pricing together for this RFQ right now. Please try again in a moment.
        </Alert>
        <Stack direction="row" spacing={1.5}>
          <Button variant="contained" onClick={() => refetch()} sx={{ fontWeight: 800, borderRadius: 2 }}>
            Try again
          </Button>
          <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate(`/procurement/rfqs/view/${rfqId}`)} sx={{ fontWeight: 800, borderRadius: 2 }}>
            Back to RFQ
          </Button>
        </Stack>
      </Box>
    );
  }

  if (preview.lines.length === 0) {
    return (
      <Box sx={{ p: 4, maxWidth: 640, mx: 'auto', textAlign: 'center' }}>
        <SparkleIcon sx={{ fontSize: 48, color: 'text.disabled', mb: 1 }} />
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 1 }}>Nothing to price yet</Typography>
        <Typography color="text.secondary" sx={{ mb: 3 }}>
          This RFQ has no line items, so there's nothing to suggest prices for.
        </Typography>
        <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate(`/procurement/rfqs/view/${rfqId}`)} sx={{ fontWeight: 800, borderRadius: 2 }}>
          Back to RFQ
        </Button>
      </Box>
    );
  }

  const currency = preview.currency;

  // Live total: sum of qty × typed price for every line where both are known.
  const liveTotal = preview.lines.reduce((sum, line) => {
    const price = parseUserNumber(prices[line.rfqItemId] ?? '');
    if (price == null || line.quantity == null) return sum;
    return sum + price * line.quantity;
  }, 0);

  const pricedCount = preview.lines.filter((l) => parseUserNumber(prices[l.rfqItemId] ?? '') != null).length;
  const hasInvalidPrice = preview.lines.some((l) => {
    const raw = prices[l.rfqItemId] ?? '';
    return raw.trim() !== '' && parseUserNumber(raw) == null;
  });
  const canApply = pricedCount > 0 && !hasInvalidPrice && !applyMutation.isPending;

  return (
    <Box sx={{ p: 3, pb: 12, maxWidth: 1100, mx: 'auto' }}>
      <Breadcrumbs separator={<NextIcon sx={{ fontSize: 14 }} />} sx={{ mb: 2 }}>
        <Link component="button" variant="caption" onClick={() => navigate('/procurement/rfqs/all')} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
          RFQ Management
        </Link>
        <Link component="button" variant="caption" onClick={() => navigate(`/procurement/rfqs/view/${rfqId}`)} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
          RFQ #{preview.rfqId}
        </Link>
        <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 900, textTransform: 'uppercase' }}>
          Smart pricing
        </Typography>
      </Breadcrumbs>

      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 3, gap: 2, flexWrap: 'wrap' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 950, letterSpacing: '-0.02em', mb: 0.5 }}>
            Here's my suggested pricing
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
            Each price comes from your own history. Adjust anything, then apply.
          </Typography>
        </Box>
        <ConfidenceChip score={preview.overallConfidence} />
      </Box>

      <Stack spacing={2}>
        {preview.lines.map((line, idx) => {
          const raw = prices[line.rfqItemId] ?? '';
          const typedPrice = parseUserNumber(raw);
          const priceInvalid = raw.trim() !== '' && typedPrice == null;
          const belowFloor = typedPrice != null && line.floorUnitPrice != null && typedPrice < line.floorUnitPrice;
          const lineTotal = typedPrice != null && line.quantity != null ? typedPrice * line.quantity : null;
          const isOpen = expanded[line.rfqItemId] ?? false;

          return (
            <Paper
              key={line.rfqItemId}
              sx={{
                p: 2.5,
                borderRadius: 3,
                border: '1px solid',
                borderColor: line.needsAttention ? 'warning.main' : 'divider',
                bgcolor: line.needsAttention ? 'rgba(255, 167, 38, 0.06)' : 'background.paper',
              }}
            >
              {/* Top row: description + confidence */}
              <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 2, mb: 1, flexWrap: 'wrap' }}>
                <Box sx={{ minWidth: 0 }}>
                  <Typography sx={{ fontWeight: 800, fontSize: '0.9rem' }}>
                    {line.description || `Line ${idx + 1}`}
                  </Typography>
                  <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>
                    {line.quantity != null ? `Quantity: ${line.quantity}` : 'Quantity not set'}
                  </Typography>
                </Box>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                  {line.needsAttention && (
                    <Chip
                      icon={<AttentionIcon sx={{ fontSize: 14 }} />}
                      label="Double-check this one"
                      size="small"
                      color="warning"
                      sx={{ fontWeight: 800, fontSize: '0.65rem' }}
                    />
                  )}
                  <ConfidenceChip score={line.confidence} />
                </Stack>
              </Box>

              {line.rationale && (
                <Typography sx={{ fontSize: '0.83rem', color: 'text.secondary', fontWeight: 600, mb: 1.5 }}>
                  {line.rationale}
                </Typography>
              )}

              <Divider sx={{ mb: 2 }} />

              {/* Price editor row */}
              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ alignItems: { sm: 'flex-start' } }}>
                <TextField
                  size="small"
                  label="Unit price"
                  value={raw}
                  onChange={(e) => setPrices((prev) => ({ ...prev, [line.rfqItemId]: e.target.value }))}
                  error={priceInvalid}
                  helperText={
                    priceInvalid
                      ? 'Please enter a number.'
                      : belowFloor
                        ? `Below ${formatMoney(line.floorUnitPrice, currency)} — you could lose money on this line.`
                        : line.floorUnitPrice != null
                          ? `Try not to go below ${formatMoney(line.floorUnitPrice, currency)}.`
                          : raw.trim() === ''
                            ? 'No suggestion — leave blank to skip this line.'
                            : ' '
                  }
                  slotProps={{
                    input: {
                      inputMode: 'decimal',
                      startAdornment: currency
                        ? <InputAdornment position="start">{currency}</InputAdornment>
                        : undefined,
                    },
                    formHelperText: { sx: belowFloor && !priceInvalid ? { color: 'warning.dark', fontWeight: 700 } : undefined },
                  }}
                  sx={{ width: { xs: '100%', sm: 220 } }}
                />

                <Box sx={{ pt: 0.5 }}>
                  <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', fontSize: '0.65rem' }}>
                    Margin
                  </Typography>
                  <Typography sx={{ fontWeight: 800, fontSize: '0.85rem' }}>
                    {line.marginPct != null ? `${Math.round(line.marginPct)}%` : '—'}
                  </Typography>
                </Box>

                <Box sx={{ pt: 0.5, ml: { sm: 'auto' }, textAlign: { sm: 'right' } }}>
                  <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', fontSize: '0.65rem' }}>
                    Line total
                  </Typography>
                  <Typography sx={{ fontWeight: 900, fontSize: '0.95rem', color: 'primary.main' }}>
                    {formatMoney(lineTotal, currency)}
                  </Typography>
                </Box>
              </Stack>

              {/* Expandable explanation */}
              {line.signals.length > 0 && (
                <>
                  <Button
                    size="small"
                    onClick={() => setExpanded((prev) => ({ ...prev, [line.rfqItemId]: !isOpen }))}
                    startIcon={isOpen ? <CollapseIcon /> : <ExpandIcon />}
                    aria-expanded={isOpen}
                    sx={{ mt: 1, fontWeight: 800, textTransform: 'none', color: 'text.secondary' }}
                  >
                    How I got this
                  </Button>
                  <Collapse in={isOpen}>
                    <Box sx={{ mt: 1, p: 1.5, borderRadius: 2, bgcolor: 'action.hover' }}>
                      {line.signals.map((signal, sIdx) => (
                        <SignalRow key={sIdx} signal={signal} currency={currency} />
                      ))}
                    </Box>
                  </Collapse>
                </>
              )}
            </Paper>
          );
        })}
      </Stack>

      {/* Sticky footer: live total + apply */}
      <Paper
        elevation={8}
        sx={{
          position: 'sticky',
          bottom: 16,
          mt: 3,
          p: 2,
          borderRadius: 3,
          border: '1px solid',
          borderColor: 'divider',
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          gap: 2,
          flexWrap: 'wrap',
          zIndex: 10,
        }}
      >
        <Box>
          <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', fontSize: '0.65rem', display: 'block' }}>
            Total with these prices
          </Typography>
          <Typography sx={{ fontWeight: 950, fontSize: '1.25rem', color: 'success.main' }}>
            {formatMoney(liveTotal, currency)}
          </Typography>
          <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>
            {pricedCount === preview.lines.length
              ? `All ${preview.lines.length} lines priced.`
              : `${pricedCount} of ${preview.lines.length} lines priced — blank lines will be skipped.`}
          </Typography>
        </Box>
        <Stack direction="row" spacing={1.5}>
          <Button
            variant="outlined"
            onClick={() => navigate(`/procurement/rfqs/view/${rfqId}`)}
            disabled={applyMutation.isPending}
            sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
          >
            Cancel
          </Button>
          <Button
            variant="contained"
            startIcon={applyMutation.isPending ? <CircularProgress size={18} color="inherit" /> : <SparkleIcon />}
            onClick={() => applyMutation.mutate()}
            disabled={!canApply}
            sx={{ fontWeight: 800, borderRadius: 2, px: 4 }}
          >
            {applyMutation.isPending ? 'Applying…' : 'Apply pricing'}
          </Button>
        </Stack>
      </Paper>
    </Box>
  );
};

export default RfqPricingPage;
