import type { AnimationEvent, ReactNode } from 'react';
import { Box, Typography } from '@mui/material';
import {
  DescriptionOutlined,
  SmartToyOutlined,
  InsightsOutlined,
  GppGoodOutlined,
} from '@mui/icons-material';
import { NO_BACKDROP_FILTER, SOLID_CARD_BG, TEXT_HI, TEXT_MID } from './tokens';
import { EASE_OUT, REDUCED_MOTION, chipBreathe, tileIn } from './motion';

/**
 * Bento feature tiles: glass cards with per-tile colored icon chips that
 * explain the product in plain language. Layout is a deliberate 2-column
 * bento — a wide horizontal hero tile (text left, format chips right, so the
 * width is used, not left empty), a matched pair, and a wide slim strip.
 * Single column below the login card on mobile. No focusables inside.
 *
 * Choreography ("First Light / Depth Stack"):
 * - Entrance: TL→BR stagger, 60ms apart from 240ms (compressed ~30% <900px),
 *   opacity/translateY/scale only, will-change dropped on animationend.
 * - Hover: translateY(-4px) scale(1.015) rotateX(1.5deg) under a per-tile
 *   perspective(900px), 200ms out / 300ms return; icon-chip glow 0.6→1;
 *   border brightens. On touch the same treatment runs as a press state.
 * - Idle: the chip's glow pseudo-layer breathes ±3% on a 7s cycle, phase
 *   offset 0.9s per tile (sub-perceptual).
 */

interface Tile {
  icon: ReactNode;
  title: string;
  body: string;
  /** Chip hue triplet "r, g, b" — soft colored fill behind a bright icon. */
  hue: string;
  /** Bright icon color on the soft chip. */
  iconColor: string;
  /** Spans both bento columns on ≥sm. */
  wide?: boolean;
  /** Renders as a compact horizontal strip. */
  slim?: boolean;
  /** Extra content that fills the right half of the wide hero tile. */
  chips?: string[];
}

const TILES: Tile[] = [
  {
    icon: <DescriptionOutlined />,
    title: 'Reads any RFQ document',
    body: 'Parsed into clean, structured line items in seconds.',
    hue: '56, 189, 248',
    iconColor: '#7DD3FC',
    wide: true,
    chips: ['PDF', 'XLSX', 'Email', 'Scan'],
  },
  {
    icon: <SmartToyOutlined />,
    title: 'Copilot, not autopilot',
    body: 'Sources, compares, and quotes — you approve every step.',
    hue: '167, 139, 250',
    iconColor: '#C4B5FD',
  },
  {
    icon: <InsightsOutlined />,
    title: 'Pricing that shows its work',
    body: 'Recommendations with the reasoning laid out.',
    hue: '34, 211, 238',
    iconColor: '#67E8F9',
  },
  {
    icon: <GppGoodOutlined />,
    title: 'Guardrailed and audited',
    body: 'Every AI action is policy-checked and logged.',
    hue: '52, 211, 153',
    iconColor: '#6EE7B7',
    wide: true,
    slim: true,
  },
];

const BentoTiles = () => (
  <Box
    component="ul"
    // will-change discipline: promoted only for the entrance, released the
    // moment each tile's entrance animation completes (event bubbles here).
    onAnimationEnd={(e: AnimationEvent<HTMLUListElement>) => {
      const t = e.target as HTMLElement;
      if (t.tagName === 'LI') t.style.willChange = 'auto';
    }}
    sx={{
      listStyle: 'none',
      m: 0,
      p: 0,
      display: 'grid',
      gap: 1.5,
      gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, minmax(0, 1fr))' },
    }}
  >
    {TILES.map((tile, index) => {
      const horizontal = Boolean(tile.slim || tile.chips);
      const hoverTransform = 'perspective(900px) translateY(-4px) scale(1.015) rotateX(1.5deg)';
      return (
        <Box
          component="li"
          key={tile.title}
          style={{ ['--i' as string]: String(index), willChange: 'transform' }}
          sx={{
            position: 'relative',
            borderRadius: '18px',
            p: tile.slim ? 2 : 2.5,
            gridColumn: { sm: tile.wide ? 'span 2' : 'auto' },
            display: 'flex',
            flexDirection: horizontal ? { xs: 'column', sm: 'row' } : 'column',
            alignItems: horizontal ? { xs: 'flex-start', sm: 'center' } : 'flex-start',
            gap: horizontal ? 2 : 1.5,
            // Glass with a whisper of the tile's own hue in the fill + border.
            backgroundColor: 'rgba(13, 21, 44, 0.42)',
            backgroundImage: `linear-gradient(135deg, rgba(${tile.hue}, 0.10) 0%, rgba(${tile.hue}, 0.02) 55%)`,
            backdropFilter: 'blur(16px) saturate(150%)',
            WebkitBackdropFilter: 'blur(16px) saturate(150%)',
            border: `1px solid rgba(${tile.hue}, 0.22)`,
            boxShadow: 'inset 0 1px 0 rgba(255, 255, 255, 0.08)',
            [NO_BACKDROP_FILTER]: { backgroundColor: SOLID_CARD_BG },
            // Entrance: 60ms TL→BR stagger from 240ms (compressed on mobile).
            animation: `${tileIn} 420ms ${EASE_OUT} both`,
            animationDelay: {
              xs: 'calc(168ms + var(--i) * 42ms)',
              md: 'calc(240ms + var(--i) * 60ms)',
            },
            // Hover/press: 200ms out, 300ms return.
            transition: `transform 300ms ${EASE_OUT}, border-color 300ms ${EASE_OUT}, box-shadow 300ms ${EASE_OUT}`,
            '&:hover, &:active': {
              transform: hoverTransform,
              transition: `transform 200ms ${EASE_OUT}, border-color 200ms ${EASE_OUT}, box-shadow 200ms ${EASE_OUT}`,
              borderColor: 'rgba(186, 210, 255, 0.35)',
              boxShadow: `inset 0 1px 0 rgba(255, 255, 255, 0.10), 0 12px 36px -14px rgba(${tile.hue}, 0.40)`,
            },
            '&:hover .nx-chip-glow, &:active .nx-chip-glow': {
              animation: 'none',
              opacity: 1,
            },
            [REDUCED_MOTION]: {
              animation: 'none',
              transition: 'none',
              '&:hover, &:active': { transform: 'none' },
            },
          }}
        >
          <Box
            sx={{
              display: 'flex',
              alignItems: horizontal ? 'center' : 'flex-start',
              flexDirection: horizontal ? 'row' : 'column',
              gap: horizontal ? 2 : 1.5,
              flex: 1,
              minWidth: 0,
            }}
          >
            <Box
              aria-hidden="true"
              sx={{
                position: 'relative',
                width: 42,
                height: 42,
                borderRadius: '12px',
                flexShrink: 0,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                background: `linear-gradient(135deg, rgba(${tile.hue}, 0.30) 0%, rgba(${tile.hue}, 0.14) 100%)`,
                border: `1px solid rgba(${tile.hue}, 0.45)`,
                color: tile.iconColor,
                '& svg': { fontSize: 22, position: 'relative' },
              }}
            >
              {/* Glow layer: breathes ±3% on a 7s cycle (0.9s/tile phase
                  offset), snaps to full strength on tile hover/press. */}
              <Box
                className="nx-chip-glow"
                sx={{
                  position: 'absolute',
                  inset: 0,
                  borderRadius: 'inherit',
                  boxShadow: `0 6px 18px -6px rgba(${tile.hue}, 0.60)`,
                  opacity: 0.6,
                  animation: `${chipBreathe} 7s ease-in-out infinite`,
                  animationDelay: 'calc(var(--i) * 0.9s)',
                  transition: 'opacity 200ms ease',
                  [REDUCED_MOTION]: { animation: 'none' },
                }}
              />
              {tile.icon}
            </Box>
            <Box sx={{ minWidth: 0 }}>
              <Typography sx={{ fontSize: 15, fontWeight: 700, letterSpacing: '-0.01em', color: TEXT_HI, mb: 0.25 }}>
                {tile.title}
              </Typography>
              <Typography sx={{ fontSize: 13.5, color: TEXT_MID, lineHeight: 1.5 }}>
                {tile.body}
              </Typography>
            </Box>
          </Box>

          {/* Format chips fill the right half of the wide hero tile */}
          {tile.chips && (
            <Box
              aria-hidden="true"
              sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', flexShrink: 0 }}
            >
              {tile.chips.map((chip) => (
                <Box
                  key={chip}
                  sx={{
                    px: 1.25,
                    py: 0.5,
                    borderRadius: '999px',
                    fontSize: 11.5,
                    fontWeight: 700,
                    letterSpacing: '0.06em',
                    textTransform: 'uppercase',
                    color: tile.iconColor,
                    backgroundColor: `rgba(${tile.hue}, 0.12)`,
                    border: `1px solid rgba(${tile.hue}, 0.35)`,
                  }}
                >
                  {chip}
                </Box>
              ))}
            </Box>
          )}
        </Box>
      );
    })}
  </Box>
);

export default BentoTiles;
