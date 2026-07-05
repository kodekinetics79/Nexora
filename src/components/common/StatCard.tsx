import React from 'react';
import { Avatar, Box, Card, Stack, Typography, alpha, useTheme } from '@mui/material';
import { TrendingDown, TrendingUp } from '@mui/icons-material';

interface StatCardProps {
  title: React.ReactNode;
  value: React.ReactNode;
  icon?: React.ReactNode;
  color?: string;
  trend?: 'up' | 'down' | 'neutral';
  trendValue?: React.ReactNode;
  caption?: React.ReactNode;
}

const StatCard: React.FC<StatCardProps> = ({ title, value, icon, color, trend = 'neutral', trendValue, caption }) => {
  const theme = useTheme();
  const accent = color || theme.palette.primary.main;
  const trendColor = trend === 'down' ? theme.palette.error.main : trend === 'up' ? theme.palette.success.main : theme.palette.text.secondary;

  return (
    <Card
      sx={{
        p: 2.5,
        height: '100%',
        position: 'relative',
        overflow: 'hidden',
        transition: 'transform .2s ease, box-shadow .2s ease, border-color .2s ease',
        '&:before': {
          content: '""',
          position: 'absolute',
          inset: 0,
          pointerEvents: 'none',
          background: `linear-gradient(135deg, ${alpha(accent, 0.14)}, transparent 42%)`,
        },
        '&:hover': {
          transform: 'translateY(-3px)',
          borderColor: alpha(accent, 0.34),
        },
      }}
    >
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'flex-start', position: 'relative' }}>
        <Box>
          <Typography variant="overline" color="text.secondary">
            {title}
          </Typography>
          <Typography variant="h4" sx={{ mt: 0.75, fontWeight: 850 }}>
            {value}
          </Typography>
        </Box>
        {icon ? (
          <Avatar sx={{ bgcolor: alpha(accent, 0.12), color: accent, width: 46, height: 46, borderRadius: 2 }}>
            {icon}
          </Avatar>
        ) : null}
      </Stack>
      {(trendValue || caption) ? (
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mt: 2.5, position: 'relative' }}>
          {trendValue ? (
            <Box
              sx={{
                display: 'inline-flex',
                alignItems: 'center',
                gap: 0.5,
                px: 0.75,
                py: 0.25,
                borderRadius: 1,
                bgcolor: alpha(trendColor, 0.12),
                color: trendColor,
              }}
            >
              {trend === 'down' ? <TrendingDown sx={{ fontSize: 14 }} /> : <TrendingUp sx={{ fontSize: 14 }} />}
              <Typography variant="caption" sx={{ fontWeight: 800 }}>
                {trendValue}
              </Typography>
            </Box>
          ) : null}
          {caption ? (
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 650 }}>
              {caption}
            </Typography>
          ) : null}
        </Stack>
      ) : null}
    </Card>
  );
};

export default StatCard;
