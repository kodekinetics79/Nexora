import type { ReactNode } from 'react';
import { Box, ButtonBase, Paper, Stack, Tooltip, Typography } from '@mui/material';
import { ArrowForwardRounded as GoIcon } from '@mui/icons-material';

/**
 * One headline figure, as an object on the desk.
 *
 * A hero tile is the executive view's unit of "one glance": the figure, what it is a figure OF
 * (its denominator or scope, never omitted), a sparkline when a series exists, and the way to the
 * records behind it. It is a pressable key, not a card — the whole surface opens the drill-down —
 * so it carries the theme's tactile depth: a glass face with the brass hairline, a lift on hover,
 * a press on click. Under "reduce motion" it stays put.
 *
 * Honesty rules, inherited from the KPI tiles this replaces: when a value is not available the
 * tile says so in the server's words and shows no number at all — never a dash that reads like
 * zero, never a figure with a silently narrower scope than its label.
 */
export interface HeroTileProps {
  label: string;
  /** The formatted figure. Null renders the unavailable state. */
  value: string | null;
  /** What the figure counts, and over what — the denominator sentence. */
  basis: string;
  /** Server-stated reason when `value` is null. */
  unavailableReason?: string | null;
  /** Optional series, oldest → newest, drawn as a sparkline. */
  series?: number[] | null;
  seriesLabel?: string;
  definition?: string;
  icon?: ReactNode;
  /** Where the records behind the figure live. Absent means the tile is read-only. */
  onOpen?: () => void;
  openLabel?: string;
  index?: number;
}

const Sparkline = ({ points, label }: { points: number[]; label?: string }) => {
  const w = 120, h = 36, pad = 3;
  const max = Math.max(...points, 1), min = Math.min(...points, 0);
  const span = max - min || 1;
  const step = points.length > 1 ? (w - pad * 2) / (points.length - 1) : 0;
  const xy = points.map((p, i) => [pad + i * step, h - pad - ((p - min) / span) * (h - pad * 2)] as const);
  const line = xy.map(([x, y], i) => `${i === 0 ? 'M' : 'L'}${x.toFixed(1)} ${y.toFixed(1)}`).join(' ');
  const area = `${line} L${xy[xy.length - 1][0].toFixed(1)} ${h - pad} L${pad} ${h - pad} Z`;
  const last = xy[xy.length - 1];
  return (
    <svg
      width={w} height={h} viewBox={`0 0 ${w} ${h}`} role="img"
      aria-label={label ? `${label}: ${points.join(', ')}` : undefined}
      aria-hidden={label ? undefined : true}
      style={{ display: 'block', overflow: 'visible' }}
    >
      <defs>
        <linearGradient id="nx-spark-fill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#e0a100" stopOpacity="0.42" />
          <stop offset="1" stopColor="#e0a100" stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={area} fill="url(#nx-spark-fill)" />
      <path d={line} fill="none" stroke="#c9931a" strokeWidth="1.8" strokeLinejoin="round" strokeLinecap="round" />
      <circle cx={last[0]} cy={last[1]} r="2.6" fill="#fff1c9" stroke="#c9931a" strokeWidth="1.2" />
    </svg>
  );
};

export default function HeroTile({
  label, value, basis, unavailableReason, series, seriesLabel, definition, icon, onOpen, openLabel, index = 0,
}: HeroTileProps) {
  const available = value !== null;
  const body = (
    <Stack sx={{ height: '100%', p: 2, minWidth: 0, alignItems: 'stretch', textAlign: 'left' }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="overline" sx={{ fontWeight: 800, letterSpacing: '0.08em', lineHeight: 1.4, color: 'text.secondary' }}>
          {label}
        </Typography>
        {icon && <Box sx={{ color: 'primary.main', display: 'grid', placeItems: 'center', opacity: 0.9 }}>{icon}</Box>}
      </Stack>
      {available ? (
        <Typography
          component="p"
          sx={{
            mt: 0.75, fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700,
            fontSize: { xs: 30, md: 36 }, lineHeight: 1.05, letterSpacing: '-0.02em',
            fontVariantNumeric: 'tabular-nums', color: 'text.primary', wordBreak: 'break-word',
          }}
        >
          {value}
        </Typography>
      ) : (
        <Typography component="p" sx={{ mt: 0.75, fontSize: 20, fontWeight: 700, color: 'text.secondary', lineHeight: 1.2 }}>
          Not available
        </Typography>
      )}
      <Tooltip title={definition ?? ''} placement="top-start" disableHoverListener={!definition}>
        <Typography variant="body2" sx={{ mt: 0.5, color: 'text.secondary', lineHeight: 1.35 }}>
          {available ? basis : (unavailableReason ?? basis)}
        </Typography>
      </Tooltip>
      <Box sx={{ flexGrow: 1 }} />
      <Stack direction="row" sx={{ mt: 1.25, alignItems: 'flex-end', justifyContent: 'space-between', gap: 1 }}>
        {series && series.length > 1 ? <Sparkline points={series} label={seriesLabel} /> : <span />}
        {onOpen && (
          <Typography variant="caption" sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5, fontWeight: 700, color: 'primary.main', whiteSpace: 'nowrap' }}>
            {openLabel ?? 'Open'} <GoIcon sx={{ fontSize: 16 }} />
          </Typography>
        )}
      </Stack>
    </Stack>
  );

  const surface = {
    height: '100%', minHeight: 176, borderRadius: 3, overflow: 'hidden',
    transition: 'transform 200ms cubic-bezier(0.2, 0.7, 0.2, 1), box-shadow 200ms ease-out',
    '@media (prefers-reduced-motion: reduce)': { transition: 'none' },
  } as const;

  return (
    <Paper
      component="article"
      variant="outlined"
      className="nx-glass nx-enter"
      data-decorative-motion="true"
      style={{ animationDelay: `${Math.min(index, 8) * 40}ms` }}
      aria-label={label}
      sx={{
        ...surface,
        p: 0,
        ...(onOpen ? {
          '&:hover': {
            transform: 'translateY(-4px)',
            boxShadow: (t) => `inset 0 1px 0 rgba(255,255,255,${t.palette.mode === 'dark' ? 0.1 : 0.9}), 0 26px 48px -24px rgba(15,18,24,${t.palette.mode === 'dark' ? 0.95 : 0.45}), 0 12px 24px -16px rgba(201,147,26,0.55)`,
          },
          '&:active': { transform: 'translateY(1px)' },
          '@media (prefers-reduced-motion: reduce)': { '&:hover, &:active': { transform: 'none' } },
        } : {}),
      }}
    >
      {onOpen ? (
        <ButtonBase
          onClick={onOpen}
          aria-label={`${label}: ${available ? value : 'not available'}. ${openLabel ?? 'Open'}`}
          sx={{ display: 'block', width: '100%', height: '100%', textAlign: 'left', borderRadius: 3,
            '&.Mui-focusVisible': { outline: (t) => `3px solid ${t.palette.primary.main}`, outlineOffset: -3 } }}
        >
          {body}
        </ButtonBase>
      ) : body}
    </Paper>
  );
}
