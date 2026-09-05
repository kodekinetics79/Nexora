import { Alert, Box, Paper, Skeleton, Stack, Typography, useTheme } from '@mui/material';
import {
  Bar,
  CartesianGrid,
  ComposedChart,
  Line,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import type { MonthlyTrendDTO } from '../../../api/services/dashboardService';

/**
 * Six months of volume and value: requests priced (RFQs created) as graphite bars, order value as
 * a brass line. The two answer the two questions a director asks in one breath — are we busier,
 * and is it turning into money.
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

export default function TrendPanel({ trend, loading, unavailable, currencyCode }: TrendPanelProps) {
  const theme = useTheme();
  const dark = theme.palette.mode === 'dark';
  const grid = dark ? 'rgba(163,169,181,0.16)' : 'rgba(42,47,58,0.12)';
  const axis = theme.palette.text.secondary;
  const rows = (trend ?? []).map((m) => ({ ...m, label: m.month.length > 7 ? m.month.slice(0, 7) : m.month }));
  const empty = !loading && !unavailable && rows.every((r) => r.count === 0 && r.value === 0);

  return (
    <Paper variant="outlined" className="nx-glass" sx={{ p: { xs: 1.5, sm: 2 }, borderRadius: 3, height: '100%' }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', justifyContent: 'space-between', flexWrap: 'wrap', gap: 1 }}>
        <Typography variant="subtitle1" sx={{ fontWeight: 900 }}>Six months, volume and value</Typography>
        <Typography variant="caption" color="text.secondary">
          Bars: requests priced · Line: order value{currencyCode ? ` (${currencyCode})` : ''}
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
        <Box sx={{ mt: 1, height: 232 }} aria-label="Monthly requests and order value">
          <ResponsiveContainer width="100%" height="100%">
            <ComposedChart data={rows} margin={{ top: 12, right: 8, left: -10, bottom: 0 }}>
              <defs>
                <linearGradient id="nx-trend-bar" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0" stopColor={dark ? '#7d8597' : '#3a4050'} />
                  <stop offset="1" stopColor={dark ? '#454c5e' : '#1f232c'} />
                </linearGradient>
              </defs>
              <CartesianGrid strokeDasharray="3 3" stroke={grid} vertical={false} />
              <XAxis dataKey="label" stroke={axis} fontSize={11} tickLine={false} axisLine={false} />
              <YAxis yAxisId="count" stroke={axis} fontSize={11} tickLine={false} axisLine={false} allowDecimals={false} />
              <YAxis yAxisId="value" orientation="right" stroke={axis} fontSize={11} tickLine={false} axisLine={false} tickFormatter={compact} width={48} />
              <Tooltip
                cursor={{ fill: dark ? 'rgba(255,255,255,0.04)' : 'rgba(42,47,58,0.06)' }}
                contentStyle={{
                  background: dark ? '#1b1f26' : '#ffffff', border: `1px solid ${dark ? 'rgba(170,176,190,0.24)' : 'rgba(95,102,115,0.2)'}`,
                  borderRadius: 10, color: theme.palette.text.primary, fontSize: 12,
                }}
                formatter={(value, name) =>
                  name === 'Order value' ? [compact(Number(value ?? 0)), name] : [String(value ?? 0), String(name ?? '')]}
              />
              <Bar yAxisId="count" dataKey="count" name="Requests priced" fill="url(#nx-trend-bar)" radius={[6, 6, 0, 0]} maxBarSize={38} />
              <Line yAxisId="value" type="monotone" dataKey="value" name="Order value" stroke="#c9931a" strokeWidth={2.5} dot={{ r: 3, fill: '#fff1c9', stroke: '#c9931a' }} activeDot={{ r: 5 }} />
            </ComposedChart>
          </ResponsiveContainer>
        </Box>
      )}
    </Paper>
  );
}
