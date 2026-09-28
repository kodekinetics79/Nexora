import { useState } from 'react';
import { Stack, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import dayjs from 'dayjs';
import axiosInstance from '../../../api/axiosInstance';
import type { BrandDemandDTO, BrandDemandRowDTO } from '../../../api/services/dashboardService';
import { toPresentableError } from '../../../utils/apiErrors';
import BandShell, { type BandSeal } from './BandShell';
import Doughnut, { type DoughnutSlice } from './Doughnut';
import { useEscapeWithin } from './useEscapeWithin';

/**
 * What customers ask for — the makers named on incoming request lines, as one ring.
 *
 * The ring is drawn over the lines that NAME a maker, because that is the only whole a brand can
 * be a share of. Lines that name none are not a slice: a grey "unknown" wedge would read as a
 * brand of its own. They are stated once, under the chart, so the reader still sees how much of
 * the book the ring does not cover.
 */
export interface BrandsBandProps {
  /** Inclusive first day of the selected window, YYYY-MM-DD. */
  from: string;
  /** Inclusive last day of the selected window, YYYY-MM-DD. */
  to: string;
  index?: number;
  step?: string;
}

/** Enough rows that the ring's "Other" is the real tail, not whatever the server cut off. */
const TOP_N = 200;
/** The makers beyond TOP_N, if a tenant ever has that many. Never a colour of its own. */
const BEYOND = '__beyond';

/**
 * Called here rather than through dashboardService.getBrandDemand: that one maps a 403 to null,
 * and this band has to tell "not yours to see" apart from "nothing yet". The server compares
 * `to` with <=, so the last day is sent as its final second to keep the window inclusive.
 */
const fetchBrandDemand = async (from: string, to: string): Promise<BrandDemandDTO> => {
  const r = await axiosInstance.get<BrandDemandDTO>('/api/brand-demand', {
    params: { from, to: `${to}T23:59:59`, topN: TOP_N },
  });
  return { ...r.data, rows: r.data?.rows ?? [] };
};

const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`;

const day = (value: string) => {
  const d = dayjs(value);
  return d.isValid() ? d.format('D MMM') : value;
};

/** Company-wide, always: the endpoint reads the whole tenant and the reader's role only gates it. */
const brandsSeal = (from: string, to: string, data: BrandDemandDTO | undefined): BandSeal => ({
  scope: 'Company-wide',
  window: `${day(from)} – ${day(to)}`,
  generatedAt: data?.generatedAt ?? null,
  governed: true,
});

/** "Siemens: 120 lines across 14 documents (18% of lines that name a maker)." */
const drillSentence = (row: BrandDemandRowDTO, named: number): string => {
  const pct = named > 0 ? Math.round((row.lines / named) * 100) : 0;
  return `${row.manufacturer}: ${plural(row.lines, 'line', 'lines')} across ${plural(row.documents, 'document', 'documents')} (${pct}% of lines that name a maker).`;
};

export default function BrandsBand({ from, to, index = 0, step }: BrandsBandProps) {
  const query = useQuery({
    queryKey: ['glance', 'brand-demand', from, to],
    queryFn: () => fetchBrandDemand(from, to),
    retry: (count, error) => toPresentableError(error, { context: 'list' }).status !== 403 && count < 1,
    meta: { silenceGlobalError: true, errorLabel: 'the brands customers ask for' },
  });
  const [picked, setPicked] = useState<string | null>(null);
  const escapeRef = useEscapeWithin<HTMLDivElement>(picked !== null, () => setPicked(null));

  const data = query.data;
  const presented = query.isError ? toPresentableError(query.error, { context: 'list' }) : null;
  const forbidden = presented?.status === 403
    ? 'What customers ask for is shown to managers and admins only.'
    : null;

  const rows = data?.rows ?? [];
  const named = data?.linesWithManufacturer ?? 0;
  const unnamed = data?.linesWithoutManufacturer ?? 0;
  const beyond = Math.max(0, named - rows.reduce((sum, row) => sum + row.lines, 0));
  const slices: DoughnutSlice[] = [
    ...rows.map((row) => ({ key: row.normalizedKey, label: row.manufacturer, value: row.lines })),
    ...(beyond > 0 ? [{ key: BEYOND, label: 'Other makers', value: beyond }] : []),
  ];
  const pickedRow = rows.find((row) => row.normalizedKey === picked) ?? null;
  const isEmpty = named === 0;

  return (
    <BandShell
      title="What customers ask for"
      step={step}
      index={index}
      minHeight={240}
      hint="Each slice is a maker, sized by how many request lines name it. Lines that name no maker are left out of the ring."
      loading={query.isLoading}
      error={presented && !forbidden ? presented.message : null}
      forbidden={forbidden}
      onRetry={() => void query.refetch()}
      seal={brandsSeal(from, to, data)}
      detailsTo="/analytics/brand-demand"
    >
      <Stack ref={escapeRef} spacing={1} sx={{ flexGrow: 1, minWidth: 0, justifyContent: 'center' }}>
        <Doughnut
          slices={slices}
          total={named.toLocaleString()}
          totalLabel="lines"
          picked={picked}
          // "Other" has no single maker to describe, so picking it only lights the ring.
          onPick={setPicked}
          label="Request lines by maker"
        />

        {pickedRow && (
          <Typography variant="body2" data-testid="brands-drill" aria-live="polite" sx={{ fontWeight: 600, lineHeight: 1.4 }}>
            {drillSentence(pickedRow, named)}
          </Typography>
        )}

        {isEmpty ? (
          <Typography variant="caption" data-testid="brands-empty" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
            No request line in this window names a maker yet.
          </Typography>
        ) : null}

        {unnamed > 0 && (
          <Typography variant="caption" data-testid="brands-unnamed" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
            {plural(unnamed, 'line', 'lines')} named no maker.
          </Typography>
        )}
      </Stack>
    </BandShell>
  );
}
