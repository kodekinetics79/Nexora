import { useQuery } from '@tanstack/react-query';
import dayjs from 'dayjs';
import dashboardService, { type PipelineAnalyticsDTO } from '../../../api/services/dashboardService';
import type { BandSeal } from './BandShell';
import { scopeWords } from './scopeWords';

/**
 * The one aggregate bands 2 and 3 are both readings of.
 *
 * They ask for it under the same key, so react-query serves both from one cache entry and makes
 * one request. That is not the composite "load the dashboard" fetch this screen refuses: each band
 * still owns its loading, empty and failure states and prints its own alert, and the endpoint
 * going down costs the reader exactly these two bands rather than the screen.
 *
 * The seal is built here for the same reason the query is: both bands must state the SAME window
 * and the SAME scope, because they are the same figures cut two ways, and two bands disagreeing
 * about whose numbers they are would be the exact failure the seal exists to prevent.
 */
export const usePipelineAnalytics = (from: string, to: string, errorLabel: string) =>
  useQuery({
    queryKey: ['glance', 'pipeline-analytics', from, to],
    queryFn: () => dashboardService.getPipelineAnalytics({ from, to }),
    retry: 1,
    meta: { silenceGlobalError: true, errorLabel },
  });

/**
 * A UTC instant as the calendar day it falls on, in the reader's words.
 *
 * The endpoint states its window as two UTC instants while the rest of this screen works in plain
 * YYYY-MM-DD. Formatting the instant through the local zone would move the date for every reader
 * west of UTC — a thirty-day window would seal as ending the day before it does — so the day is
 * taken in UTC and only then handed to dayjs for wording.
 */
const utcDay = (iso: string | null | undefined, offsetMs = 0): string | null => {
  if (!iso) return null;
  const at = Date.parse(iso);
  if (Number.isNaN(at)) return null;
  const day = dayjs(new Date(at + offsetMs).toISOString().slice(0, 10));
  return day.isValid() ? day.format('D MMM') : null;
};

/**
 * The seal both bands wear.
 *
 * `windowTo` is the EXCLUSIVE end of the window, so the last day the figures actually cover is the
 * day the instant one millisecond before it falls on. Sealing the exclusive bound itself would
 * claim a day of trading the band did not count.
 *
 * `governed` is drawn from `funnelScope` rather than from the fact that we sent dates: a band that
 * asked for a window and was answered all-time would otherwise wear a filled seal — "this band
 * follows the period you choose above" — over figures the period control never reached.
 */
export const pipelineSeal = (data: PipelineAnalyticsDTO | undefined): BandSeal => {
  const windowed = data?.funnelScope === 'window';
  const from = utcDay(data?.windowFrom);
  const to = utcDay(data?.windowTo, -1);
  return {
    // Whose numbers, from this endpoint's own roleScope and never borrowed from a neighbouring
    // band. 'tenant' is company-wide and must never be headed as anything personal.
    scope: scopeWords(data?.roleScope?.scope),
    window: windowed && from && to ? `${from} – ${to}` : 'All time',
    generatedAt: data?.generatedAt ?? null,
    governed: windowed,
  };
};
