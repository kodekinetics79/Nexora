import React from 'react';
import { Box, Skeleton, Typography } from '@mui/material';
import type { SxProps, Theme } from '@mui/material';
import { useAppTheme } from '../../../context/ThemeContext';
import { glassTokens } from './dashboardTheme';

interface GlassCardProps {
  children: React.ReactNode;
  /** Accessible name for the section landmark. */
  label: string;
  sx?: SxProps<Theme>;
}

/**
 * Frosted-glass bento card. Falls back to a solid surface when the browser
 * cannot do backdrop-filter, so content never sits on a transparent card.
 */
const GlassCard: React.FC<GlassCardProps> = ({ children, label, sx }) => {
  const { mode } = useAppTheme();
  const t = glassTokens(mode);

  return (
    <Box
      component="section"
      aria-label={label}
      sx={{
        position: 'relative',
        borderRadius: 4,
        border: '1px solid',
        borderColor: t.border,
        backgroundColor: t.solidBg,
        boxShadow: t.shadow,
        p: { xs: 2, md: 2.5 },
        minWidth: 0,
        '@supports ((backdrop-filter: blur(1px)) or (-webkit-backdrop-filter: blur(1px)))': {
          backgroundColor: t.glassBg,
          backdropFilter: 'blur(16px) saturate(140%)',
          WebkitBackdropFilter: 'blur(16px) saturate(140%)',
        },
        ...(Array.isArray(sx) ? sx : [sx]),
      }}
    >
      {children}
    </Box>
  );
};

/** Uniform card header: quiet overline title + optional action on the right. */
export const CardTitle: React.FC<{ title: string; subtitle?: string; action?: React.ReactNode }> = ({
  title,
  subtitle,
  action,
}) => (
  <Box sx={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: 1, mb: 1.75 }}>
    <Box sx={{ minWidth: 0 }}>
      <Typography
        component="h2"
        variant="overline"
        sx={{ display: 'block', fontWeight: 800, letterSpacing: '0.09em', color: 'text.secondary', lineHeight: 1.6 }}
      >
        {title}
      </Typography>
      {subtitle && (
        <Typography variant="body2" sx={{ color: 'text.secondary', mt: 0.25 }}>
          {subtitle}
        </Typography>
      )}
    </Box>
    {action}
  </Box>
);

/** First-paint placeholder rows; refetches keep previous data (no flash). */
export const CardSkeleton: React.FC<{ rows?: number; rowHeight?: number }> = ({ rows = 3, rowHeight = 34 }) => (
  <Box aria-hidden sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
    {Array.from({ length: rows }, (_, i) => (
      <Skeleton key={i} variant="rounded" height={rowHeight} sx={{ borderRadius: 2 }} />
    ))}
  </Box>
);

export default GlassCard;
