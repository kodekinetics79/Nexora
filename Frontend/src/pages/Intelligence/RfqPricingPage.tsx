import React from 'react';
import { useParams, useNavigate, Link as RouterLink } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
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
import { useAuth } from '../../context/AuthContext';
import intelligenceService from '../../api/services/intelligenceService';
import type { PriceSignal, PriceSignalSource } from '../../api/services/intelligenceService';
import { ConfidenceChip, formatMoney, parseUserNumber } from './common';

// Friendly names for where a suggestion came from — no jargon.
const SOURCE_LABELS: Record<PriceSignalSource, string> = {
  recentQuote: 'A recent quote you won',
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
  const { hasPermission } = useAuth();
  const rfqId = Number(id);

  const { data: preview, isLoading, isError, refetch } = useQuery({
    queryKey: ['rfq-price-preview', rfqId],
    queryFn: () => intelligenceService.getPricePreview(rfqId),
    enabled: !!id && Number.isFinite(rfqId),
  });

  // Editable unit prices, keyed by rfqItemId, kept as strings while typing.
  const [prices, setPrices] = React.useState<Record<number, string>>({});
  const [expanded, setExpanded] = React.useState<Record<number, boolean>>({});
  const initializedRfqId = React.useRef<number | null>(null);

  React.useEffect(() => {
    if (!preview || initializedRfqId.current === preview.rfqId) return;
    initializedRfqId.current = preview.rfqId;
    const next: Record<number, string> = {};
    for (const line of preview.lines) {
      next[line.rfqItemId] = line.recommendedUnitPrice && line.recommendedUnitPrice > 0
        ? String(line.recommendedUnitPrice)
        : '';
    }
    setPrices(next);
    setExpanded({});
  }, [preview]);

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

  // ── Where pricing is ACTUALLY done ─────────────────────────────────────────
  // This page is shadow-only by design: the server refuses POST apply-pricing outright
  // (PricingIntelligenceController returns 409, PricingEngine.ApplyPricingAsync throws), so the one
  // thing this screen owes the reader is the next click. Authoritative pricing happens in the RFQ's
  // Sourcing workspace — approve a supplier's quote, then "Price customer quote" writes the customer
  // line from that supplier's approved landed cost.
  //
  // Both conditions below MIRROR the destination rather than guessing at it, so the link can never
  // promise a control the user will not find when they arrive:
  //   • permission — SourcingWorkbenchPage gates its "Price customer quote" button on
  //     Supplier History:edit AND Quotations:edit, the same pair
  //     SupplierQuoteInboxController.ApplyCustomerPricing requires. Without both it is a dead end,
  //     so we state what is missing instead of linking.
  //   • award — a line's floorUnitPrice is READ from CustomerQuoteSourcingDecision, so a non-null
  //     floor anywhere on this RFQ is proof an award has been recorded. No floor on any line means
  //     nothing has been awarded yet and there is no approved cost to price against.
  const canSetPrices = hasPermission('Supplier History', 'edit') && hasPermission('Quotations', 'edit');
  const hasAwardedCost = preview.lines.some((line) => line.floorUnitPrice != null);
  const sourcingPath = `/procurement/rfqs/${rfqId}/sourcing`;
  const pricingDestination = !canSetPrices
    ? 'You cannot set prices yourself: that needs "Can Edit" on both Quotations and Supplier History. '
      + 'Ask an administrator to add them, then price this RFQ from its Sourcing workspace.'
    : hasAwardedCost
      ? 'Real prices are set in this RFQ\'s Sourcing workspace: open the awarded supplier line, choose '
        + '"Price customer quote", and enter your margin over the supplier\'s approved cost.'
      : 'No supplier has been awarded on this RFQ yet, so there is no approved cost to price against. '
        + 'Approve a supplier quote in this RFQ\'s Sourcing workspace first — you can set the customer '
        + 'price there straight afterwards.';

  const liveTotals = preview.lines.reduce<Record<string, number>>((totals, line) => {
    const price = parseUserNumber(prices[line.rfqItemId] ?? '');
    if (price == null || price <= 0 || line.quantity == null || line.quantity <= 0 || !line.currency) return totals;
    totals[line.currency] = (totals[line.currency] ?? 0) + price * line.quantity;
    return totals;
  }, {});

  const pricedCount = preview.lines.filter((line) => {
    const price = parseUserNumber(prices[line.rfqItemId] ?? '');
    return price != null && price > 0 && line.quantity != null && line.quantity > 0 && !!line.currency;
  }).length;
  const hasInvalidPrice = preview.lines.some((l) => {
    const raw = prices[l.rfqItemId] ?? '';
    const price = parseUserNumber(raw);
    return raw.trim() !== '' && (price == null || price <= 0);
  });
  const hasUnresolvedCurrency = preview.lines.some((line) => !line.currency);
  const hasInvalidQuantity = preview.lines.some((line) => line.quantity == null || line.quantity <= 0);

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
          <Typography variant="h5" sx={{ fontWeight: 950, letterSpacing: 0, mb: 0.5 }}>
            Shadow pricing workspace
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
            Compare evidence-backed price scenarios without changing the RFQ or Customer Quote.
          </Typography>
        </Box>
        <ConfidenceChip score={preview.overallConfidence} />
      </Box>

      <Alert severity="info" sx={{ mb: 2 }}>
        {preview.applyBlocker} What-if edits are temporary and are discarded when you leave this page.
        <Box sx={{ mt: 1.5 }}>
          <Typography variant="body2" sx={{ fontWeight: 700 }}>
            {pricingDestination}
          </Typography>
          {canSetPrices && (
            <Button
              component={RouterLink}
              to={sourcingPath}
              size="small"
              variant="outlined"
              endIcon={<NextIcon />}
              sx={{ mt: 1, fontWeight: 800, borderRadius: 2, textTransform: 'none' }}
            >
              {hasAwardedCost ? 'Go to Sourcing to set prices' : 'Go to Sourcing to award a supplier'}
            </Button>
          )}
        </Box>
      </Alert>

      <Stack spacing={2}>
        {preview.lines.map((line, idx) => {
          const currency = line.currency;
          const raw = prices[line.rfqItemId] ?? '';
          const typedPrice = parseUserNumber(raw);
          const priceInvalid = raw.trim() !== '' && (typedPrice == null || typedPrice <= 0);
          // A price and a floor can only be compared when they are the same money. The floor is
          // the AWARDED SUPPLIER'S landed cost, which may be quoted in a different currency from
          // the RFQ line, so comparing the bare numbers would be a number compared without its
          // unit. When they differ the server converts through an approved FX rate at the send
          // gate; here we say so rather than guess.
          const floorComparable =
            line.floorUnitPrice != null &&
            line.floorCurrency != null &&
            currency != null &&
            line.floorCurrency.toUpperCase() === currency.toUpperCase();
          const belowFloor = floorComparable && typedPrice != null && typedPrice < line.floorUnitPrice!;
          const lineTotal = typedPrice != null && typedPrice > 0 && line.quantity != null && line.quantity > 0
            ? typedPrice * line.quantity
            : null;
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
                  label="What-if unit price"
                  value={raw}
                  onChange={(e) => setPrices((prev) => ({ ...prev, [line.rfqItemId]: e.target.value }))}
                  error={priceInvalid}
                  helperText={
                    priceInvalid
                      ? 'Enter a price greater than zero.'
                      : belowFloor
                        ? `Below ${formatMoney(line.floorUnitPrice, line.floorCurrency)} — this is under what the goods cost you.`
                        : floorComparable
                          ? `Cost floor is ${formatMoney(line.floorUnitPrice, line.floorCurrency)} — the awarded supplier's landed cost.`
                          : line.floorUnitPrice != null
                            ? `Cost floor is ${formatMoney(line.floorUnitPrice, line.floorCurrency)} while this line is priced in ${currency ?? 'an unidentified currency'}; an approved exchange rate is needed before the two can be compared. The send will be held until then.`
                            : 'No cost floor established — no supplier award has been recorded for this line, so there is nothing to check this price against.'
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
                    Suggested margin
                  </Typography>
                  {/* marginPct is already a PERCENT (20 means 20%), so it is not scaled again. */}
                  <Typography sx={{ fontWeight: 800, fontSize: '0.85rem' }}>
                    {line.marginPct != null ? `${Math.round(line.marginPct)}%` : 'No cost floor'}
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
                      aria-controls={`pricing-evidence-${line.rfqItemId}`}
                    sx={{ mt: 1, fontWeight: 800, textTransform: 'none', color: 'text.secondary' }}
                  >
                    How I got this
                  </Button>
                  <Collapse in={isOpen}>
                    <Box id={`pricing-evidence-${line.rfqItemId}`} role="region" aria-label={`Pricing evidence for ${line.description || `line ${idx + 1}`}`} sx={{ mt: 1, p: 1.5, borderRadius: 2, bgcolor: 'action.hover' }}>
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
            Shadow totals by currency
          </Typography>
          <Stack direction="row" spacing={2} useFlexGap sx={{ flexWrap: 'wrap' }}>
            {Object.entries(liveTotals).map(([currencyCode, total]) => (
              <Typography key={currencyCode} sx={{ fontWeight: 950, fontSize: '1.1rem', color: 'success.main' }}>
                {formatMoney(total, currencyCode)}
              </Typography>
            ))}
            {Object.keys(liveTotals).length === 0 && <Typography sx={{ fontWeight: 800 }}>No priced currency totals</Typography>}
          </Stack>
          <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>
            {hasInvalidPrice ? 'Correct invalid prices to recalculate.' : hasInvalidQuantity
              ? 'Lines without a positive quantity are excluded.' : hasUnresolvedCurrency
              ? 'Lines without verified currency are excluded.' : pricedCount === preview.lines.length
              ? `All ${preview.lines.length} lines priced.`
              : `${pricedCount} of ${preview.lines.length} lines are included in shadow totals.`}
          </Typography>
        </Box>
        <Stack direction="row" spacing={1.5}>
          <Button
            variant="outlined"
            onClick={() => navigate(`/procurement/rfqs/view/${rfqId}`)}
            sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
          >
            Return to RFQ
          </Button>
        </Stack>
      </Paper>
    </Box>
  );
};

export default RfqPricingPage;
