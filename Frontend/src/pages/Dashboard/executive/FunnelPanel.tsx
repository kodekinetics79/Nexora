import { useId, useState } from 'react';
import { Alert, Box, Chip, Paper, Skeleton, Stack, Tooltip, Typography, useTheme } from '@mui/material';
import type { PipelineAnalyticsDTO, PipelineStageDTO } from '../../../api/services/dashboardService';
import { formatMoney } from '../../../utils/currency';

/**
 * The funnel: requests received → accepted to work on → quoted → won.
 *
 * Drawn as four solid bars that narrow with the count, each one a key: hover lifts it, click opens
 * the records it counts. The numbers are the server's own funnel — all-time, and it says so — and
 * a stage whose value cannot be stated in one currency shows its count with the server's reason
 * instead of a partial sum.
 *
 * Between the bars, the conversion from one stage to the next is written in words a director
 * reads at a glance ("41% of requests were accepted"), because a funnel without its ratios is a
 * bar chart.
 */
export interface FunnelPanelProps {
  data?: PipelineAnalyticsDTO;
  loading?: boolean;
  error?: boolean;
  /** The server answered 403: the funnel is a manager's view, and this reader is not one. */
  forbidden?: boolean;
  onStage?: (stage: PipelineStageDTO) => void;
}

const pct = (n: number, d: number) => (d > 0 ? Math.round((n / d) * 100) : null);

export default function FunnelPanel({ data, loading, error, forbidden, onStage }: FunnelPanelProps) {
  const theme = useTheme();
  const dark = theme.palette.mode === 'dark';
  const gradId = useId().replace(/:/g, '');
  const [hot, setHot] = useState<string | null>(null);

  const stages = data?.funnel ?? [];
  const top = Math.max(1, ...stages.map((s) => s.count));
  const W = 640, H = 176, ROW = 40, GAP = 6, LABEL_W = 132;

  return (
    <Paper variant="outlined" className="nx-glass" sx={{ p: { xs: 1.5, sm: 2 }, borderRadius: 3, height: '100%' }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', justifyContent: 'space-between', flexWrap: 'wrap', gap: 1 }}>
        <Typography variant="subtitle1" sx={{ fontWeight: 900 }}>Request to revenue</Typography>
        {data && (
          <Chip size="small" variant="outlined" label={data.funnelScope === 'all_time' ? 'All time' : data.funnelScope} />
        )}
      </Stack>

      {loading ? (
        <Stack spacing={1} sx={{ mt: 2 }}>
          {Array.from({ length: 4 }, (_, i) => <Skeleton key={i} variant="rounded" height={34} sx={{ borderRadius: 1.5 }} />)}
        </Stack>
      ) : forbidden ? (
        <Alert severity="info" sx={{ mt: 1.5 }}>
          The funnel is available to managers and administrators.
        </Alert>
      ) : error || !data ? (
        <Alert severity="warning" sx={{ mt: 1.5 }}>
          The funnel could not be loaded. This is a request failure, not an empty pipeline.
        </Alert>
      ) : (
        <>
          <Box sx={{ mt: 1.5, overflowX: 'auto' }}>
            <svg
              viewBox={`0 0 ${W} ${H}`}
              width="100%"
              height={H}
              role="img"
              aria-label={`Funnel: ${stages.map((s) => `${s.label} ${s.count}`).join(', ')}`}
              style={{ display: 'block', minWidth: 360, overflow: 'visible' }}
            >
              <defs>
                <linearGradient id={`g-${gradId}`} x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0" stopColor={dark ? '#6b7385' : '#3a4050'} />
                  <stop offset="1" stopColor={dark ? '#454c5e' : '#1f232c'} />
                </linearGradient>
                <linearGradient id={`b-${gradId}`} x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0" stopColor="#fff1c9" />
                  <stop offset="0.35" stopColor="#e0a100" />
                  <stop offset="1" stopColor="#a87a12" />
                </linearGradient>
                <filter id={`s-${gradId}`} x="-10%" y="-40%" width="120%" height="200%">
                  <feDropShadow dx="0" dy="3" stdDeviation="3" floodColor="#0f1218" floodOpacity={dark ? 0.7 : 0.28} />
                </filter>
              </defs>
              {stages.map((s, i) => {
                const y = i * (ROW + GAP);
                const width = Math.max((W - LABEL_W) * (s.count / top), 10);
                const won = s.key === 'won';
                const lifted = hot === s.key;
                const prev = i > 0 ? stages[i - 1] : null;
                const ratio = prev ? pct(s.count, prev.count) : null;
                const valueText = s.value !== null ? formatMoney(s.value, s.valueCurrency) : (s.valueUnavailableReason ?? 'value not stated');
                return (
                  <g
                    key={s.key}
                    transform={`translate(0 ${y})`}
                    onMouseEnter={() => setHot(s.key)}
                    onMouseLeave={() => setHot(null)}
                    onClick={() => onStage?.(s)}
                    style={{ cursor: onStage ? 'pointer' : 'default' }}
                    role={onStage ? 'button' : undefined}
                    tabIndex={onStage ? 0 : undefined}
                    onKeyDown={(e) => { if (onStage && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); onStage(s); } }}
                    aria-label={`${s.label}: ${s.count}, ${valueText}${ratio !== null ? `, ${ratio}% of ${prev?.label.toLowerCase()}` : ''}`}
                  >
                    <title>{`${s.label}: ${s.count} · ${valueText}`}</title>
                    <text x="0" y={ROW / 2 + 5} fontSize="13" fontWeight="700" fill={theme.palette.text.primary}>{s.label}</text>
                    {ratio !== null && (
                      <text x="0" y={ROW / 2 + 20} fontSize="10.5" fill={theme.palette.text.secondary}>{ratio}% of previous</text>
                    )}
                    <g style={{ transform: lifted ? 'translateY(-2px)' : 'none', transition: 'transform 160ms ease-out' }} filter={`url(#s-${gradId})`}>
                      <rect x={LABEL_W} y="4" width={width} height={ROW - 8} rx="7" fill={won ? `url(#b-${gradId})` : `url(#g-${gradId})`} />
                      <rect x={LABEL_W} y="4" width={width} height="1.5" rx="1" fill="rgba(255,255,255,0.35)" />
                    </g>
                    <text
                      x={LABEL_W + width + 10} y={ROW / 2 + 5} fontSize="14" fontWeight="800"
                      fill={theme.palette.text.primary} style={{ fontVariantNumeric: 'tabular-nums' }}
                    >
                      {s.count.toLocaleString('en-US')}
                    </text>
                    <text x={LABEL_W + width + 10} y={ROW / 2 + 20} fontSize="10.5" fill={theme.palette.text.secondary}>
                      {valueText.length > 34 ? `${valueText.slice(0, 33)}…` : valueText}
                    </text>
                  </g>
                );
              })}
            </svg>
          </Box>

          {data.lossReasons.length > 0 && (
            <Box sx={{ mt: 1 }}>
              <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.secondary', letterSpacing: '0.06em', textTransform: 'uppercase' }}>
                Why bids were lost
              </Typography>
              <Stack direction="row" sx={{ flexWrap: 'wrap', gap: 0.75, mt: 0.5 }}>
                {data.lossReasons.slice(0, 6).map((r) => (
                  <Tooltip key={r.reason} title={r.value !== null ? formatMoney(r.value, r.valueCurrency) : (r.valueUnavailableReason ?? '')}>
                    <Chip size="small" variant="outlined" label={`${r.reason} · ${r.count}`} />
                  </Tooltip>
                ))}
              </Stack>
            </Box>
          )}
        </>
      )}
    </Paper>
  );
}
