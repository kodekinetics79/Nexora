import type { ReactNode } from 'react';
import { Box, Paper, Typography } from '@mui/material';
import { TrendingDown, TrendingFlat, TrendingUp } from '@mui/icons-material';
import { useAppTheme } from '../../context/ThemeContext';

interface StatTileProps {
  label: string;
  value: ReactNode;
  icon: ReactNode;
  /** Accent color for the icon chip and glow. Defaults to theme primary. */
  color?: string;
  /** Signed percentage delta vs prior period; omit to hide the trend row. */
  deltaPct?: number;
  /** Overrides the automatic sign-based trend direction. */
  trend?: 'up' | 'down' | 'flat';
  caption?: string;
}

export default function StatTile({ label, value, icon, color, deltaPct, trend, caption }: StatTileProps) {
  const { mode, primaryColor } = useAppTheme();
  const accent = color ?? primaryColor;

  const direction: 'up' | 'down' | 'flat' =
    trend ?? (deltaPct == null ? 'flat' : deltaPct > 0 ? 'up' : deltaPct < 0 ? 'down' : 'flat');
  const trendColor = direction === 'up' ? '#10b981' : direction === 'down' ? '#ef4444' : '#94a3b8';
  const TrendIcon = direction === 'up' ? TrendingUp : direction === 'down' ? TrendingDown : TrendingFlat;

  return (
    <Paper
      sx={{
        p: 2.5,
        borderRadius: 3,
        height: '100%',
        display: 'flex',
        flexDirection: 'column',
        gap: 1.5,
        transition: 'transform 0.2s ease, box-shadow 0.2s ease',
        '&:hover': {
          transform: 'translateY(-3px)',
          boxShadow: mode === 'dark' ? `0 12px 32px ${accent}22` : `0 12px 32px ${accent}1f`,
          borderColor: `${accent}40`,
        },
      }}
    >
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Typography variant="overline" sx={{ color: 'text.secondary', fontWeight: 700, letterSpacing: 0.8, lineHeight: 1.4 }}>
          {label}
        </Typography>
        <Box
          sx={{
            width: 40,
            height: 40,
            borderRadius: 2,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            bgcolor: `${accent}18`,
            color: accent,
            flexShrink: 0,
          }}
        >
          {icon}
        </Box>
      </Box>

      <Typography variant="h4" sx={{ fontWeight: 800, lineHeight: 1.1, letterSpacing: '-0.02em' }}>
        {value}
      </Typography>

      {(deltaPct != null || caption) && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mt: 'auto' }}>
          {deltaPct != null && (
            <Box sx={{ display: 'flex', alignItems: 'center', color: trendColor, fontWeight: 700 }}>
              <TrendIcon sx={{ fontSize: 16, mr: 0.3 }} />
              <Typography variant="caption" sx={{ fontWeight: 700 }}>
                {deltaPct > 0 ? '+' : ''}
                {deltaPct.toFixed(1)}%
              </Typography>
            </Box>
          )}
          {caption && (
            <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 500 }}>
              {caption}
            </Typography>
          )}
        </Box>
      )}
    </Paper>
  );
}
