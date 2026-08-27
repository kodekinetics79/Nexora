import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import dayjs from 'dayjs';
import {
  Alert,
  Box,
  Chip,
  CircularProgress,
  Collapse,
  Link,
  Paper,
  Stack,
  Typography,
} from '@mui/material';
import dashboardService from '../../api/services/dashboardService';
import { formatMoney } from '../../utils/currency';

/**
 * FR-DSH-02 — value-weighted gross margin on accepted quotes.
 *
 * This panel replaces a figure that was wrong three ways at once: it costed quote lines from a
 * product-card column that holds a bare last-purchase price rather than a landed cost, it averaged
 * per-line PERCENTAGES so a one-unit line counted as much as a ten-thousand-unit line, and it had
 * no date or outcome filter so drafts and lost bids were in the sample.
 *
 * Two rules govern what is rendered here:
 *  - **No number without evidence.** When the server says `unavailable` the panel shows the reason
 *    it gives, verbatim. It never falls back to a dash that reads like a loading state, and it
 *    never invents a confidence or precision the server did not state.
 *  - **The denominator is always visible.** Sample size, coverage and every exclusion are on the
 *    same card as the percentage, because a margin over 8% of the book is a different claim from a
 *    margin over all of it.
 */
export default function GrossMarginPanel({ from, to }: { from: string; to: string }) {
  const [showEvidence, setShowEvidence] = useState(false);

  const query = useQuery({
    queryKey: ['dashboard', 'gross-margin', from, to],
    queryFn: () =>
      dashboardService.getGrossMargin({
        from: dayjs(from).toISOString(),
        to: dayjs(to).toISOString(),
      }),
    retry: 1,
  });

  if (query.isLoading) {
    return (
      <Paper variant="outlined" sx={{ p: 2, minHeight: 140, display: 'grid', placeItems: 'center' }}>
        <CircularProgress size={24} aria-label="Loading gross margin" />
      </Paper>
    );
  }

  if (query.isError || !query.data) {
    return (
      <Paper variant="outlined" sx={{ p: 2 }}>
        <Alert severity="warning" sx={{ mb: 0 }}>
          The gross margin could not be loaded. This is a request failure, not a statement that the
          margin is zero.
        </Alert>
      </Paper>
    );
  }

  const data = query.data;
  const available = data.status === 'available';
  const coverage =
    data.acceptedQuoteLines > 0
      ? Math.round((data.sampleLines / data.acceptedQuoteLines) * 1000) / 10
      : null;

  return (
    <Paper variant="outlined" sx={{ p: { xs: 1.5, sm: 2 } }}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={1}
        sx={{ justifyContent: 'space-between', alignItems: { sm: 'baseline' } }}
      >
        <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>
          Gross margin on accepted quotes
        </Typography>
        <Typography variant="caption" color="text.secondary">
          {dayjs(data.periodFrom).format('DD MMM YYYY')} – {dayjs(data.periodTo).format('DD MMM YYYY')}{' '}
          {data.periodBoundary}
        </Typography>
      </Stack>

      {available ? (
        <>
          <Typography variant="h3" sx={{ fontWeight: 900, mt: 1 }}>
            {data.marginPercent?.toLocaleString(undefined, { maximumFractionDigits: 1 })}%
          </Typography>
          <Typography variant="body2" color="text.secondary">
            {formatMoney(data.revenueTotal, data.currencyCode)} accepted against{' '}
            {formatMoney(data.costTotal, data.currencyCode)} of landed cost.
          </Typography>
        </>
      ) : (
        <>
          <Typography variant="h5" sx={{ fontWeight: 900, mt: 1, color: 'text.secondary' }}>
            Unavailable
          </Typography>
          {/* The server's words, not ours. A client-invented explanation that contradicts the
              server is how a real gap turns into a plausible-sounding non-answer. */}
          <Alert severity="info" sx={{ mt: 1 }}>
            {data.reason}
          </Alert>
        </>
      )}

      {data.costBasisNote && (
        <Alert
          severity={data.linesOnPriorCostBasis > 0 && data.linesOnCurrentCostBasis > 0 ? 'warning' : 'info'}
          sx={{ mt: 1.5 }}
        >
          {data.costBasisNote}
          {data.marginPercentCurrentBasisOnly !== null && (
            <Box sx={{ mt: 0.5, fontWeight: 700 }}>
              On the current cost basis alone:{' '}
              {data.marginPercentCurrentBasisOnly.toLocaleString(undefined, { maximumFractionDigits: 1 })}%
            </Box>
          )}
        </Alert>
      )}

      <Stack direction="row" spacing={1} sx={{ mt: 1.5, flexWrap: 'wrap', gap: 0.5 }}>
        <Chip size="small" variant="outlined" label={`${data.sampleLines} line(s) sampled`} />
        <Chip size="small" variant="outlined" label={`${data.sampleQuotes} accepted quote(s)`} />
        {coverage !== null && (
          <Chip
            size="small"
            variant="outlined"
            color={coverage < 100 ? 'warning' : 'default'}
            label={`${coverage}% of accepted lines`}
          />
        )}
      </Stack>

      <Link
        component="button"
        type="button"
        variant="caption"
        onClick={() => setShowEvidence((open) => !open)}
        sx={{ mt: 1.5, display: 'block' }}
      >
        {showEvidence ? 'Hide' : 'Show'} what this figure rests on
      </Link>

      <Collapse in={showEvidence}>
        <Box component="dl" sx={{ mt: 1, mb: 0, display: 'grid', gridTemplateColumns: 'auto 1fr', gap: 0.5 }}>
          <Typography component="dt" variant="caption" color="text.secondary">
            Accepted means
          </Typography>
          <Typography component="dd" variant="caption" sx={{ m: 0 }}>
            {data.acceptedDefinition}
          </Typography>

          <Typography component="dt" variant="caption" color="text.secondary">
            Cost source
          </Typography>
          <Typography component="dd" variant="caption" sx={{ m: 0 }}>
            The landed unit cost recorded on the sourcing decision each customer price was built
            from, multiplied by that decision&apos;s quantity. Not the product card&apos;s cost.
          </Typography>

          <Typography component="dt" variant="caption" color="text.secondary">
            Accepted quote lines in period
          </Typography>
          <Typography component="dd" variant="caption" sx={{ m: 0 }}>
            {data.acceptedQuoteLines}
          </Typography>

          <Typography component="dt" variant="caption" color="text.secondary">
            Excluded: no sourcing decision
          </Typography>
          <Typography component="dd" variant="caption" sx={{ m: 0 }}>
            {data.linesWithoutSourcingEvidence} line(s) — their price cannot be traced to a cost, so
            they are left out rather than costed from elsewhere.
          </Typography>

          <Typography component="dt" variant="caption" color="text.secondary">
            Excluded: no acceptance date
          </Typography>
          <Typography component="dd" variant="caption" sx={{ m: 0 }}>
            {data.quotesExcludedForMissingAcceptanceDate} accepted quote(s) belong to no period.
          </Typography>

          {data.costBasisChangedOn && (
            <>
              <Typography component="dt" variant="caption" color="text.secondary">
                Cost basis changed
              </Typography>
              <Typography component="dd" variant="caption" sx={{ m: 0 }}>
                {dayjs(data.costBasisChangedOn).format('DD MMM YYYY')} —{' '}
                {data.linesOnPriorCostBasis} line(s) before, {data.linesOnCurrentCostBasis} after.
              </Typography>
            </>
          )}
        </Box>
      </Collapse>
    </Paper>
  );
}
