import { useState } from 'react';
import { Box, Link, Stack, Typography } from '@mui/material';
import { Link as RouterLink } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import commercialLearningService, { type CustomerCommercialMemory } from '../../../api/services/commercialLearningService';
import { toPresentableError } from '../../../utils/apiErrors';
import BandShell, { type BandSeal } from './BandShell';
import Doughnut, { type DoughnutSlice } from './Doughnut';
import ChartMenu from './ChartMenu';
import { useChartChoice } from './chartPrefs';
import { useEscapeWithin } from './useEscapeWithin';

/**
 * Who we quote — the customers our work goes to, as one ring.
 *
 * The figures are the commercial memory's, which keeps no period: every quote the company has
 * ever written, tenant-wide. So the seal is outlined and says "All time", and the period control
 * above never reaches this band — a filled seal here would claim a window the numbers ignore.
 * The server returns the 50 customers with the most quotes; the ring is those 50.
 */
export interface CustomersBandProps {
  index?: number;
  step?: string;
}

const LIMIT = 50;

const MEASURES = ['quotes', 'inquiries', 'won'] as const;
type Measure = (typeof MEASURES)[number];
const MEASURE_OPTIONS = [
  { value: 'quotes' as const, label: 'Quotes' },
  { value: 'inquiries' as const, label: 'Requests' },
  { value: 'won' as const, label: 'Won' },
];
/** What the ring's centre says under its total. */
const CENTRE: Readonly<Record<Measure, string>> = { quotes: 'quotes', inquiries: 'requests', won: 'won' };

const measureOf = (c: CustomerCommercialMemory, measure: Measure): number =>
  measure === 'quotes' ? c.quoteCount : measure === 'inquiries' ? c.inquiryCount : c.wonCount;

const SEAL: BandSeal = { scope: 'Company-wide', window: 'All time', generatedAt: null, governed: false };

const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`;

/** "Saudi Aramco: 14 quotes, 5 won, 6 lost, 3 open." */
const drillSentence = (c: CustomerCommercialMemory): string =>
  `${c.customerName}: ${plural(c.quoteCount, 'quote', 'quotes')}, ${c.wonCount.toLocaleString()} won, `
  + `${c.lostCount.toLocaleString()} lost, ${c.pendingCount.toLocaleString()} open.`;

export default function CustomersBand({ index = 0, step }: CustomersBandProps) {
  const query = useQuery({
    queryKey: ['glance', 'customer-memory', LIMIT],
    queryFn: () => commercialLearningService.getCustomers(LIMIT),
    retry: (count, error) => toPresentableError(error, { context: 'list' }).status !== 403 && count < 1,
    meta: { silenceGlobalError: true, errorLabel: 'the customers we quote' },
  });
  const [measure, setMeasure] = useChartChoice('customers', MEASURES, 'quotes');
  const [picked, setPicked] = useState<string | null>(null);
  const escapeRef = useEscapeWithin<HTMLDivElement>(picked !== null, () => setPicked(null));

  const presented = query.isError ? toPresentableError(query.error, { context: 'list' }) : null;
  const forbidden = presented?.status === 403
    ? 'Who we quote is shown to people who can see both customers and quotes.'
    : null;

  const customers = query.data ?? [];
  const slices: DoughnutSlice[] = customers.map((c) => ({
    key: String(c.customerId), label: c.customerName, value: measureOf(c, measure),
  }));
  const total = slices.reduce((sum, s) => sum + s.value, 0);
  const pickedCustomer = customers.find((c) => String(c.customerId) === picked) ?? null;

  return (
    <BandShell
      title="Who we quote"
      step={step}
      index={index}
      minHeight={240}
      hint="Each slice is a customer. All time and company-wide: the period above does not change this band."
      loading={query.isLoading}
      error={presented && !forbidden ? presented.message : null}
      forbidden={forbidden}
      onRetry={() => void query.refetch()}
      seal={SEAL}
      detailsTo="/intelligence/commercial-memory"
    >
      <Stack ref={escapeRef} spacing={1} sx={{ flexGrow: 1, minWidth: 0 }}>
        <Box sx={{ display: 'flex', justifyContent: 'flex-end' }}>
          <ChartMenu
            label="Ring shows"
            value={measure}
            options={MEASURE_OPTIONS}
            onChange={(next) => { setMeasure(next); setPicked(null); }}
          />
        </Box>

        <Doughnut
          slices={slices}
          total={total.toLocaleString()}
          totalLabel={CENTRE[measure]}
          picked={picked}
          onPick={setPicked}
          label={`Customers by ${CENTRE[measure]}`}
        />

        {pickedCustomer && (
          <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', flexWrap: 'wrap' }}>
            <Typography variant="body2" data-testid="customers-drill" aria-live="polite" sx={{ fontWeight: 600, lineHeight: 1.4 }}>
              {drillSentence(pickedCustomer)}
            </Typography>
            <Link
              component={RouterLink}
              to={`/customers/${pickedCustomer.customerId}`}
              underline="hover"
              sx={{ fontSize: 13, fontWeight: 700, color: 'var(--nx-glance-seal-ink)', whiteSpace: 'nowrap' }}
            >
              Open customer
            </Link>
          </Stack>
        )}

        {total === 0 && (
          <Typography variant="caption" data-testid="customers-empty" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
            {customers.length === 0
              ? 'No quote has gone to a customer yet.'
              : measure === 'won' ? 'No customer has given us a win yet.' : `No ${CENTRE[measure]} yet.`}
          </Typography>
        )}
      </Stack>
    </BandShell>
  );
}
