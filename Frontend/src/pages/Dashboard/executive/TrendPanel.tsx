import { Alert, Box, Paper, Skeleton, Stack, Typography, useTheme } from '@mui/material';
import {
  Bar,
  BarChart,
  CartesianGrid,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import type { MonthlyTrendDTO } from '../../../api/services/dashboardService';
import { formatMoney } from '../../../utils/currency';

/**
 * Six months of volume and value: requests priced as graphite bars above, order value as a brass
 * line below, on a shared month axis. The two answer the two questions a director asks in one
 * breath — are we busier, and is it turning into money.
 *
 * <p><b>Two charts, not one with two axes.</b> These were drawn as a single composed chart with a
 * count scale on the left and a money scale on the right. A dual-axis chart has no true crossing
 * point: where the line sits relative to the bars is decided by the two axis ranges, not by the
 * business, so "value is pulling ahead of volume" can be produced or erased by a rendering choice
 * nobody made deliberately. On an executive screen, where the whole purpose is that a director
 * acts on the shape, that is not a stylistic preference. Stacking the two panels keeps the
 * at-a-glance comparison — same months, same widths, aligned columns — and every vertical position
 * on each panel now means exactly one thing.</p>
 *
 * The series is the server's monthly aggregate for this tenant. When it cannot be read the panel
 * says so; it never draws a flat line that reads as "nothing happened".
 */
export interface TrendPanelProps {
  trend?: MonthlyTrendDTO[];
  loading?: boolean;
  unavailable?: string | null;
  currencyCode?: string | null;
}

const compact = (n: number) => new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(n);

/**
 * Identical on both panels so the two plot areas start at the same x. Aligned columns are what
 * makes stacked small multiples readable as one picture; a few pixels of drift and the reader is
 * comparing October against November without noticing.
 */
const AXIS_WIDTH = 52;
const MARGIN = { top: 8, right: 10, left: 0, bottom: 0 } as const;

export default function TrendPanel({ trend, loading, unavailable, currencyCode }: TrendPanelProps) {
  const theme = useTheme();
  const dark = theme.palette.mode === 'dark';
  const grid = dark ? 'rgba(163,169,181,0.16)' : 'rgba(42,47,58,0.12)';
  const axis = theme.palette.text.secondary;
  const rows = (trend ?? []).map((m) => ({ ...m, label: m.month.length > 7 ? m.month.slice(0, 7) : m.month }));
  const empty = !loading && !unavailable && rows.every((r) => r.count === 0 && r.value === 0);

  const tooltipStyle = {
    background: dark ? '#1b1f26' : '#ffffff',
    border: `1px solid ${dark ? 'rgba(170,176,190,0.24)' : 'rgba(95,102,115,0.2)'}`,
    borderRadius: 10,
    color: theme.palette.text.primary,
    fontSize: 12,
  };
  const cursorFill = dark ? 'rgba(255,255,255,0.04)' : 'rgba(42,47,58,0.06)';

  return (
    <Paper variant="outlined" className="nx-glass" sx={{ p: { xs: 1.5, sm: 2 }, borderRadius: 3, height: '100%' }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', justifyContent: 'space-between', flexWrap: 'wrap', gap: 1 }}>
        <Typography variant="subtitle1" sx={{ fontWeight: 900 }}>Six months, volume and value</Typography>
        <Typography variant="caption" color="text.secondary">
          Same months, one scale each
        </Typography>
      </Stack>
      {loading ? (
        <Skeleton variant="rounded" height={220} sx={{ mt: 1.5, borderRadius: 2 }} />
      ) : unavailable ? (
        <Alert severity="info" sx={{ mt: 1.5 }}>{unavailable}</Alert>
      ) : empty ? (
        <Alert severity="info" sx={{ mt: 1.5 }}>
          No requests or orders were recorded in the last six months.
        </Alert>
      ) : (
        <>
          {/* Each panel is titled rather than legended: one series apiece, so the title IS the
              identity and a legend box would only repeat it. */}
          <Typography variant="caption" sx={{ display: 'block', mt: 1, fontWeight: 800, color: 'text.secondary' }}>
            Requests priced
          </Typography>
          <Box sx={{ height: 116 }} aria-label="Monthly requests priced">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={rows} margin={MARGIN}>
                <CartesianGrid strokeDasharray="3 3" stroke={grid} vertical={false} />
                {/* The month labels are carried once, on the lower panel — repeating them here
                    would spend a quarter of a short chart restating what is directly below it. */}
                <XAxis dataKey="label" hide />
                <YAxis
                  stroke={axis} fontSize={11} tickLine={false} axisLine={false}
                  allowDecimals={false} width={AXIS_WIDTH}
                />
                <Tooltip
                  cursor={{ fill: cursorFill }}
                  contentStyle={tooltipStyle}
                  formatter={(value) => [String(value ?? 0), 'Requests priced']}
                />
                <Bar dataKey="count" name="Requests priced" fill="url(#nx-trend-bar)" radius={[6, 6, 0, 0]} maxBarSize={38} />
                <defs>
                  <linearGradient id="nx-trend-bar" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0" stopColor={dark ? '#7d8597' : '#3a4050'} />
                    <stop offset="1" stopColor={dark ? '#454c5e' : '#1f232c'} />
                  </linearGradient>
                </defs>
              </BarChart>
            </ResponsiveContainer>
          </Box>

          <Typography variant="caption" sx={{ display: 'block', mt: 0.5, fontWeight: 800, color: 'text.secondary' }}>
            Order value{currencyCode ? ` · ${currencyCode}` : ''}
          </Typography>
          <Box sx={{ height: 132 }} aria-label="Monthly order value">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={rows} margin={MARGIN}>
                <CartesianGrid strokeDasharray="3 3" stroke={grid} vertical={false} />
                <XAxis dataKey="label" stroke={axis} fontSize={11} tickLine={false} axisLine={false} />
                <YAxis
                  stroke={axis} fontSize={11} tickLine={false} axisLine={false}
                  tickFormatter={compact} width={AXIS_WIDTH}
                />
                <Tooltip
                  cursor={{ stroke: axis, strokeDasharray: '3 3' }}
                  contentStyle={tooltipStyle}
                  formatter={(value) => [
                    currencyCode ? formatMoney(Number(value ?? 0), currencyCode) : compact(Number(value ?? 0)),
                    'Order value',
                  ]}
                />
                <Line
                  type="monotone" dataKey="value" name="Order value" stroke="#c9931a" strokeWidth={2.5}
                  dot={{ r: 3, fill: '#fff1c9', stroke: '#c9931a' }} activeDot={{ r: 5 }}
                />
              </LineChart>
            </ResponsiveContainer>
          </Box>
        </>
      )}
    </Paper>
  );
}
