import React from 'react';
import { Box, Typography } from '@mui/material';
import { TrendingDown, TrendingUp } from '@mui/icons-material';
import { Area, AreaChart, ResponsiveContainer, Tooltip } from 'recharts';
import { useAppTheme } from '../../../context/ThemeContext';
import type { MonthlyTrendDTO, StatTrendDTO } from '../../../api/services/dashboardService';
import GlassCard, { CardSkeleton, CardTitle } from './GlassCard';
import { SERIES_HUE, formatCount, formatMoney, glassTokens } from './dashboardTheme';

interface TrendTilesProps {
  volumeTrend?: MonthlyTrendDTO[];
  rfqsTrend?: StatTrendDTO;
  ordersTrend?: StatTrendDTO;
  isLoading: boolean;
}

const DeltaChip: React.FC<{ trend?: StatTrendDTO }> = ({ trend }) => {
  if (!trend || !trend.value) return null;
  const Icon = trend.isUp ? TrendingUp : TrendingDown;
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, mt: 0.25 }}>
      <Box
        sx={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: 0.4,
          px: 0.7,
          py: 0.1,
          borderRadius: 1,
          bgcolor: (t) => (trend.isUp ? `${t.palette.success.main}1f` : `${t.palette.error.main}1f`),
          color: trend.isUp ? 'success.main' : 'error.main',
        }}
      >
        <Icon sx={{ fontSize: 13 }} aria-hidden />
        <Typography variant="caption" sx={{ fontWeight: 700, lineHeight: 1.6 }}>
          {trend.value}
        </Typography>
      </Box>
      <Typography variant="caption" sx={{ color: 'text.disabled' }}>
        vs prior 30 days
      </Typography>
    </Box>
  );
};

interface SparkTileProps {
  label: string;
  value: string;
  trend?: StatTrendDTO;
  data: { month: string; y: number }[];
  format: (n: number) => string;
  mode: 'light' | 'dark';
}

const SparkTile: React.FC<SparkTileProps> = ({ label, value, trend, data, format, mode }) => {
  const hue = SERIES_HUE[mode];
  const t = glassTokens(mode);
  const gradientId = `spark-${label.replace(/\W/g, '')}`;
  return (
    <Box sx={{ minWidth: 0 }}>
      <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.secondary', display: 'block' }}>
        {label}
      </Typography>
      <Typography variant="h5" sx={{ fontWeight: 800, lineHeight: 1.25, color: 'text.primary' }}>
        {value}
      </Typography>
      <DeltaChip trend={trend} />
      <Box sx={{ height: 56, mt: 1 }}>
        <ResponsiveContainer width="100%" height="100%">
          <AreaChart data={data} margin={{ top: 6, right: 6, bottom: 2, left: 6 }}>
            <defs>
              <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor={hue} stopOpacity={0.18} />
                <stop offset="100%" stopColor={hue} stopOpacity={0.02} />
              </linearGradient>
            </defs>
            <Tooltip
              cursor={{ stroke: hue, strokeWidth: 1 }}
              formatter={(v) => [format(Number(v)), label]}
              labelFormatter={(m) => String(m)}
              contentStyle={{
                backgroundColor: t.tooltipBg,
                border: `1px solid ${t.tooltipBorder}`,
                borderRadius: 10,
                fontSize: 12,
                fontWeight: 600,
              }}
            />
            <Area
              type="monotone"
              dataKey="y"
              stroke={hue}
              strokeWidth={2}
              fill={`url(#${gradientId})`}
              dot={false}
              activeDot={{ r: 4, strokeWidth: 2, stroke: t.tooltipBg }}
              isAnimationActive={false}
            />
          </AreaChart>
        </ResponsiveContainer>
      </Box>
    </Box>
  );
};

/**
 * Compact 6-month activity trend: RFQs created and order value, each as its own
 * single-series sparkline tile (never two scales on one plot).
 */
const TrendTiles: React.FC<TrendTilesProps> = ({ volumeTrend, rfqsTrend, ordersTrend, isLoading }) => {
  const { mode } = useAppTheme();
  const points = volumeTrend ?? [];
  const rfqTotal = points.reduce((sum, p) => sum + p.count, 0);
  const orderTotal = points.reduce((sum, p) => sum + p.value, 0);

  return (
    <GlassCard label="Activity trend">
      <CardTitle title="Last 6 months" subtitle="How busy the pipeline has been" />
      {isLoading && points.length === 0 ? (
        <CardSkeleton rows={1} rowHeight={120} />
      ) : points.length === 0 ? (
        <Typography variant="body2" sx={{ color: 'text.secondary', py: 2 }}>
          Activity will show up here once your first leads arrive.
        </Typography>
      ) : (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' }, gap: 2.5 }}>
          <SparkTile
            label="New RFQs"
            value={formatCount(rfqTotal)}
            trend={rfqsTrend}
            data={points.map((p) => ({ month: p.month, y: p.count }))}
            format={formatCount}
            mode={mode}
          />
          <SparkTile
            label="Order value"
            value={formatMoney(orderTotal)}
            trend={ordersTrend}
            data={points.map((p) => ({ month: p.month, y: p.value }))}
            format={formatMoney}
            mode={mode}
          />
        </Box>
      )}
    </GlassCard>
  );
};

export default TrendTiles;
