import { useMemo, useState } from 'react';
import { Box, ButtonBase, Stack, Typography, useTheme } from '@mui/material';
import { CATEGORY_OTHER, CATEGORY_PALETTE } from './tokens';
import { NEU_SURFACE, NEU_TRANSITION, neuFocus, neuInset } from './neumorphic';

/**
 * Part of a whole, at a glance: a ring with the total in its middle and a legend that is also the
 * table — every slice named, with its figure and its share.
 *
 * Rules it keeps (dataviz skill): at most six segments, so a seventh item folds into "Other"
 * rather than earning a generated hue; colours follow the ENTITY in fixed order, never its rank on
 * a given day; a 2px clay gap between slices; the text wears text ink, the swatch carries
 * identity. Hover a slice or a row and both light together; click either to pick it.
 */
export interface DoughnutSlice {
  key: string;
  label: string;
  value: number;
}

export interface DoughnutProps {
  slices: DoughnutSlice[];
  /** The figure in the middle, e.g. "42". Defaults to the sum. */
  total?: string;
  /** Under the figure, e.g. "requests". */
  totalLabel: string;
  /** Formats a slice's figure in the legend. */
  format?: (value: number) => string;
  picked?: string | null;
  onPick?: (key: string | null) => void;
  /** Accessible name of the whole chart. */
  label: string;
  size?: number;
}

const MAX_NAMED = 5;
const OTHER = '__other';

const arc = (cx: number, cy: number, r: number, a0: number, a1: number) => {
  const p = (a: number) => [cx + r * Math.cos(a), cy + r * Math.sin(a)];
  const [x0, y0] = p(a0);
  const [x1, y1] = p(a1);
  return `M${x0} ${y0} A${r} ${r} 0 ${a1 - a0 > Math.PI ? 1 : 0} 1 ${x1} ${y1}`;
};

export default function Doughnut({
  slices, total, totalLabel, format = v => v.toLocaleString(), picked = null, onPick, label, size = 150,
}: DoughnutProps) {
  const { palette } = useTheme();
  const mode = palette.mode;
  const [hover, setHover] = useState<string | null>(null);

  const shown = useMemo(() => {
    const positive = slices.filter(s => s.value > 0);
    const sorted = [...positive].sort((a, b) => b.value - a.value);
    // Colour follows the entity: slot by the caller's order, not by today's size.
    const slot = new Map(positive.map((s, i) => [s.key, i]));
    const named = sorted.length > MAX_NAMED + 1 ? sorted.slice(0, MAX_NAMED) : sorted;
    const rest = sorted.slice(named.length);
    const rows = named.map(s => ({ ...s, color: CATEGORY_PALETTE[mode][(slot.get(s.key) ?? 0) % CATEGORY_PALETTE[mode].length] }));
    if (rest.length) {
      rows.push({ key: OTHER, label: `Other (${rest.length})`, value: rest.reduce((n, s) => n + s.value, 0), color: CATEGORY_OTHER[mode] });
    }
    return rows;
  }, [slices, mode]);

  const sum = shown.reduce((n, s) => n + s.value, 0);
  const stroke = Math.round(size * 0.14);
  const r = size / 2 - stroke / 2 - 4;
  const c = size / 2;
  const gap = shown.length > 1 ? 2 / r : 0; // 2px of clay between slices
  const lit = hover ?? picked;

  let angle = -Math.PI / 2;
  const segments = shown.map(s => {
    const sweep = sum > 0 ? (s.value / sum) * Math.PI * 2 : 0;
    const a0 = angle + gap / 2;
    const a1 = angle + sweep - gap / 2;
    angle += sweep;
    return { ...s, d: sweep >= Math.PI * 2 - 1e-6 ? null : arc(c, c, r, a0, Math.max(a0, a1)) };
  });

  const toggle = (key: string) => onPick?.(picked === key ? null : key);

  return (
    <Stack
      direction={{ xs: 'column', sm: 'row' }} spacing={2}
      sx={{ alignItems: 'center', minWidth: 0, '@media (prefers-reduced-motion: reduce)': { '& *': { transition: 'none !important' } } }}
    >
      <Box sx={{ position: 'relative', width: size, height: size, flexShrink: 0 }}>
        <svg width={size} height={size} role="img" aria-label={`${label}: ${shown.map(s => `${s.label} ${format(s.value)}`).join(', ')}`}>
          {sum === 0 && <circle cx={c} cy={c} r={r} fill="none" stroke={palette.divider} strokeWidth={stroke} />}
          {segments.map(s => {
            const dim = lit !== null && lit !== s.key;
            const common = {
              fill: 'none', stroke: s.color, strokeWidth: lit === s.key ? stroke + 4 : stroke, strokeLinecap: 'butt' as const,
              style: { opacity: dim ? 0.35 : 1, transition: 'opacity 160ms, stroke-width 160ms', cursor: onPick ? 'pointer' : 'default' },
              onPointerEnter: () => setHover(s.key),
              onPointerLeave: () => setHover(null),
              onClick: () => toggle(s.key),
            };
            return s.d
              ? <path key={s.key} d={s.d} {...common} />
              : <circle key={s.key} cx={c} cy={c} r={r} {...common} />;
          })}
        </svg>
        <Box sx={{ position: 'absolute', inset: 0, display: 'grid', placeItems: 'center', pointerEvents: 'none' }}>
          <Box sx={{ textAlign: 'center' }}>
            <Typography sx={{ fontSize: size > 130 ? 24 : 19, fontWeight: 800, lineHeight: 1.1, fontVariantNumeric: 'tabular-nums' }}>
              {total ?? format(sum)}
            </Typography>
            <Typography sx={{ fontSize: 11, color: 'text.secondary' }}>{totalLabel}</Typography>
          </Box>
        </Box>
      </Box>

      <Stack spacing={0.25} sx={{ flex: 1, minWidth: 0, width: '100%' }}>
        {shown.map(s => {
          const share = sum > 0 ? Math.round((s.value / sum) * 100) : 0;
          const isPicked = picked === s.key;
          return (
            <ButtonBase
              key={s.key}
              aria-pressed={onPick ? isPicked : undefined}
              disabled={!onPick}
              onClick={() => toggle(s.key)}
              onMouseEnter={() => setHover(s.key)}
              onMouseLeave={() => setHover(null)}
              onFocus={() => setHover(s.key)}
              onBlur={() => setHover(null)}
              sx={{
                ...NEU_TRANSITION, ...neuFocus,
                display: 'grid', gridTemplateColumns: '10px minmax(0,1fr) auto 40px', gap: 1, alignItems: 'center',
                px: 1, py: 0.5, borderRadius: '10px', textAlign: 'left', width: '100%',
                opacity: lit !== null && lit !== s.key ? 0.55 : 1,
                bgcolor: isPicked ? NEU_SURFACE[mode] : 'transparent',
                boxShadow: isPicked ? neuInset(mode, 2) : 'none',
                '&.Mui-disabled': { color: 'inherit' },
              }}
            >
              <Box sx={{ width: 10, height: 10, borderRadius: '3px', bgcolor: s.color }} aria-hidden />
              <Typography noWrap sx={{ fontSize: 13, color: 'text.primary' }}>{s.label}</Typography>
              <Typography sx={{ fontSize: 13, fontWeight: 700, fontVariantNumeric: 'tabular-nums' }}>{format(s.value)}</Typography>
              <Typography sx={{ fontSize: 12, color: 'text.secondary', textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>{share}%</Typography>
            </ButtonBase>
          );
        })}
      </Stack>
    </Stack>
  );
}
