import React, { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Stack, Chip, Button, Tooltip, Alert, Divider,
  Table, TableBody, TableCell, TableContainer, TableHead, TableRow, ToggleButton,
  ToggleButtonGroup,
} from '@mui/material';
import { Refresh as RefreshIcon, Insights as BrandIcon } from '@mui/icons-material';
import dashboardService, { type BrandDemandRowDTO } from '../../api/services/dashboardService';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { LoadingState, ErrorState, EmptyState } from '../../platform/components/States';

// ---------------------------------------------------------------------------
// Brand demand concentration — which manufacturers customers actually ask for.
//
// The only analysis on the pilot surface that tells the owner something he does
// not already know, and it needs nothing the tenant lacks: no customer identity,
// no catalog, no FX, no lifecycle events. Just LeadItems grouped by normalised
// manufacturer.
//
// Two rules the presentation holds to:
//   1. Every magnitude carries its denominator, inline.
//   2. Lines and units are ranked separately and never mixed on one axis. A
//      manufacturer with 16,356 units on 16 lines and one with 143 lines of
//      singles are different facts; averaging them into a "score" would invent
//      a number. Documents are shown beside both, because concentration read
//      off lines alone is misleading when two documents supply most of them.
// ---------------------------------------------------------------------------

type Measure = 'lines' | 'quantity' | 'documents';

const MEASURE_META: Record<Measure, { label: string; axis: string; help: string }> = {
  lines: { label: 'Lines', axis: 'lines requested', help: 'How many requested line items name this manufacturer.' },
  quantity: { label: 'Units', axis: 'units requested', help: 'Total quantity requested across those lines. Units are not comparable across products.' },
  documents: { label: 'Documents', axis: 'documents', help: 'How many separate customer documents name this manufacturer. The most honest weight.' },
};

const TOP_N = 15;

const measureValue = (row: BrandDemandRowDTO, measure: Measure): number => {
  if (measure === 'quantity') return row.totalQuantity ?? 0;
  if (measure === 'documents') return row.documents;
  return row.lines;
};

const BrandDemandPage: React.FC = () => {
  const [measure, setMeasure] = useState<Measure>('lines');

  const demand = useQuery({
    queryKey: ['brand-demand'],
    queryFn: () => dashboardService.getBrandDemand(),
    staleTime: 300_000,
    retry: false,
  });

  const model = useMemo(() => {
    const rows = demand.data?.rows ?? [];
    const ranked = [...rows].sort((a, b) => measureValue(b, measure) - measureValue(a, measure));
    const top = ranked.slice(0, TOP_N);
    const rest = ranked.slice(TOP_N);
    const max = top.length > 0 ? measureValue(top[0], measure) : 0;
    const restTotal = rest.reduce((sum, row) => sum + measureValue(row, measure), 0);
    const grandTotal = ranked.reduce((sum, row) => sum + measureValue(row, measure), 0);
    return { ranked, top, rest, max, restTotal, grandTotal };
  }, [demand.data, measure]);

  const data = demand.data;

  return (
    <Box sx={{ p: { xs: 1.5, md: 3 }, maxWidth: 1280, mx: 'auto' }}>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { md: 'flex-end' }, mb: 2.5 }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 900 }}>Brand demand</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
            Which manufacturers your customers are asking for, grouped from the line items on their own documents.
          </Typography>
        </Box>
        <Button
          variant="outlined"
          startIcon={<RefreshIcon />}
          onClick={() => void demand.refetch()}
          disabled={demand.isFetching}
          sx={{ fontWeight: 800, borderRadius: 2 }}
        >
          Refresh
        </Button>
      </Stack>

      {demand.isLoading ? (
        <LoadingState label="Grouping line items by manufacturer…" />
      ) : demand.isError ? (
        <ErrorState
          message={presentableErrorMessage(demand.error, 'Brand demand could not be loaded. Nothing was changed — try again.')}
          onRetry={() => void demand.refetch()}
        />
      ) : !data ? (
        // The endpoint is not deployed yet. Say that, rather than showing an
        // empty chart that reads as "no demand".
        <EmptyState
          title="Brand demand is not available yet"
          message="This analysis is served by an endpoint that has not shipped to this environment. No figure is shown in its place."
          icon={<BrandIcon sx={{ fontSize: 44 }} />}
        />
      ) : model.ranked.length === 0 ? (
        <EmptyState
          title="No manufacturer recorded on any line"
          message={`${data.linesWithoutManufacturer.toLocaleString()} of ${data.totalLines.toLocaleString()} extracted line${data.totalLines === 1 ? '' : 's'} carry no manufacturer name, so there is nothing to group.`}
          icon={<BrandIcon sx={{ fontSize: 44 }} />}
        />
      ) : (
        <>
          {/* Coverage first: the reader must know what share of lines this
              analysis can even see before reading any ranking off it. */}
          <Paper variant="outlined" sx={{ p: 2, borderRadius: 2, mb: 2.5 }}>
            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(4, 1fr)' }, gap: 2 }}>
              <Box>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 800, textTransform: 'uppercase', fontSize: '0.63rem' }}>Lines with a manufacturer</Typography>
                <Typography sx={{ fontWeight: 900, fontSize: '1.5rem', lineHeight: 1.2 }}>
                  {data.linesWithManufacturer.toLocaleString()}
                </Typography>
                <Typography variant="caption" color="text.secondary">of {data.totalLines.toLocaleString()} lines</Typography>
              </Box>
              <Box>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 800, textTransform: 'uppercase', fontSize: '0.63rem' }}>Distinct manufacturers</Typography>
                <Typography sx={{ fontWeight: 900, fontSize: '1.5rem', lineHeight: 1.2 }}>
                  {data.distinctManufacturers.toLocaleString()}
                </Typography>
                <Typography variant="caption" color="text.secondary">after name normalisation</Typography>
              </Box>
              <Box>
                {/* The API exposes no total document count — only a per-manufacturer one — so
                    the complement of the coverage figure is shown instead of inventing a total. */}
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 800, textTransform: 'uppercase', fontSize: '0.63rem' }}>Lines with no manufacturer</Typography>
                <Typography sx={{ fontWeight: 900, fontSize: '1.5rem', lineHeight: 1.2 }}>
                  {data.linesWithoutManufacturer.toLocaleString()}
                </Typography>
                <Typography variant="caption" color="text.secondary">excluded, not redistributed</Typography>
              </Box>
              <Box>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 800, textTransform: 'uppercase', fontSize: '0.63rem' }}>Top {Math.min(TOP_N, model.ranked.length)} share</Typography>
                <Typography sx={{ fontWeight: 900, fontSize: '1.5rem', lineHeight: 1.2 }}>
                  {(model.grandTotal - model.restTotal).toLocaleString()}
                </Typography>
                <Typography variant="caption" color="text.secondary">
                  of {model.grandTotal.toLocaleString()} {MEASURE_META[measure].axis}
                </Typography>
              </Box>
            </Box>
          </Paper>

          <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 2, flexWrap: 'wrap' }}>
            <Typography variant="body2" sx={{ fontWeight: 800 }}>Rank by</Typography>
            <ToggleButtonGroup
              size="small"
              exclusive
              value={measure}
              onChange={(_event, next: Measure | null) => next && setMeasure(next)}
              aria-label="Rank manufacturers by"
            >
              {(Object.keys(MEASURE_META) as Measure[]).map((key) => (
                <ToggleButton key={key} value={key} sx={{ textTransform: 'none', fontWeight: 800, px: 2 }}>
                  {MEASURE_META[key].label}
                </ToggleButton>
              ))}
            </ToggleButtonGroup>
            <Typography variant="caption" color="text.secondary">{MEASURE_META[measure].help}</Typography>
          </Stack>

          {/* Ranked bars. One measure, one axis, one hue — length carries the
              magnitude, colour carries nothing, and every bar is labelled with
              its own value so the chart is readable without the axis. */}
          <Paper variant="outlined" sx={{ p: { xs: 1.5, md: 2.5 }, borderRadius: 2, mb: 2.5 }}>
            <Typography sx={{ fontWeight: 900, mb: 0.5 }}>
              Top {model.top.length} manufacturers by {MEASURE_META[measure].label.toLowerCase()}
            </Typography>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
              {MEASURE_META[measure].axis} · {model.grandTotal.toLocaleString()} in total across {model.ranked.length.toLocaleString()} manufacturers
            </Typography>
            <Stack spacing={1.25} component="ul" sx={{ listStyle: 'none', p: 0, m: 0 }}>
              {model.top.map((row) => {
                const value = measureValue(row, measure);
                const width = model.max > 0 ? Math.max((value / model.max) * 100, 0.6) : 0;
                return (
                  <Box key={row.manufacturer} component="li">
                    <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', justifyContent: 'space-between', mb: 0.4 }}>
                      <Tooltip title={row.variants > 1 ? `${row.variants.toLocaleString()} raw spellings folded into this row` : 'No alternate spellings recorded'}>
                        <Typography sx={{ fontWeight: 700, fontSize: '0.82rem', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          {row.manufacturer}
                        </Typography>
                      </Tooltip>
                      <Typography sx={{ fontWeight: 900, fontSize: '0.82rem', whiteSpace: 'nowrap', color: 'text.primary' }}>
                        {value.toLocaleString()}
                        <Typography component="span" variant="caption" sx={{ color: 'text.secondary', fontWeight: 600, ml: 0.75 }}>
                          {row.documents} doc{row.documents === 1 ? '' : 's'} · {row.lines.toLocaleString()} line{row.lines === 1 ? '' : 's'}
                        </Typography>
                      </Typography>
                    </Stack>
                    <Box
                      role="img"
                      aria-label={`${row.manufacturer}: ${value.toLocaleString()} ${MEASURE_META[measure].axis} across ${row.documents} document${row.documents === 1 ? '' : 's'}`}
                      sx={{ height: 8, bgcolor: 'action.hover', borderRadius: '4px', overflow: 'hidden' }}
                    >
                      <Box sx={{ height: '100%', width: `${width}%`, bgcolor: 'primary.main', borderRadius: '4px' }} />
                    </Box>
                  </Box>
                );
              })}
            </Stack>
            {model.rest.length > 0 && (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 2 }}>
                {model.rest.length.toLocaleString()} further manufacturers account for the remaining{' '}
                {model.restTotal.toLocaleString()} {MEASURE_META[measure].axis}. They are listed in the table below.
              </Typography>
            )}
          </Paper>

          {/* The table view. Required so the ranking is never colour- or
              length-only, and so the other two measures stay readable. */}
          <TableContainer component={Paper} variant="outlined" sx={{ borderRadius: 2 }}>
            <Table size="small" aria-label="Manufacturer demand, all measures">
              <TableHead>
                <TableRow>
                  <TableCell>Manufacturer</TableCell>
                  <TableCell align="right">Lines</TableCell>
                  <TableCell align="right">Units</TableCell>
                  <TableCell align="right">Documents</TableCell>
                  <TableCell>Spellings folded in</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {model.ranked.map((row) => (
                  <TableRow key={row.manufacturer} hover>
                    <TableCell sx={{ fontWeight: 700 }}>{row.manufacturer}</TableCell>
                    <TableCell align="right">{row.lines.toLocaleString()}</TableCell>
                    <TableCell align="right">
                      {row.totalQuantity == null ? <Typography variant="caption" color="text.disabled">Not recorded</Typography> : row.totalQuantity.toLocaleString()}
                    </TableCell>
                    <TableCell align="right">{row.documents.toLocaleString()}</TableCell>
                    <TableCell>
                      {/* The API returns the NUMBER of raw spellings folded in, not the spellings
                          themselves, so the count is stated rather than a list of chips faked from it. */}
                      {row.variants > 1 ? (
                        <Chip size="small" variant="outlined" label={`${row.variants.toLocaleString()} spellings`} sx={{ height: 18, fontSize: '0.62rem' }} />
                      ) : (
                        <Typography variant="caption" color="text.disabled">One spelling</Typography>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>

          <Divider sx={{ my: 2.5 }} />
          <Stack spacing={0.75}>
            <Typography variant="caption" color="text.secondary">
              Counted from {data.linesWithManufacturer.toLocaleString()} of {data.totalLines.toLocaleString()} extracted line
              {data.totalLines === 1 ? '' : 's'}, folded from {data.distinctRawSpellings.toLocaleString()} raw spelling
              {data.distinctRawSpellings === 1 ? '' : 's'}. Lines with no manufacturer are excluded, not redistributed.
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Units are summed as written on each document; they are not converted between packs, reels or pieces, so unit totals are
              comparable within a manufacturer but not across them.
            </Typography>
            {data.generatedAt && (
              <Typography variant="caption" color="text.disabled">
                Generated {new Date(data.generatedAt).toLocaleString()}
              </Typography>
            )}
          </Stack>
          {/* Small-sample caveat. Keyed on the lines actually counted, because the API
              publishes no total document count to key it on. */}
          {data.linesWithManufacturer > 0 && data.linesWithManufacturer < 50 && (
            <Alert severity="info" sx={{ mt: 2 }}>
              This ranking rests on {data.linesWithManufacturer.toLocaleString()} line
              {data.linesWithManufacturer === 1 ? '' : 's'}. A single large enquiry can dominate it — read the Documents
              column before treating any brand as a trend.
            </Alert>
          )}
        </>
      )}
    </Box>
  );
};

export default BrandDemandPage;
