import type { ReactNode } from 'react';
import { Box, Typography } from '@mui/material';
import {
  DescriptionOutlined,
  SmartToyOutlined,
  InsightsOutlined,
  GppGoodOutlined,
} from '@mui/icons-material';
import { ACCENT_SOFT, TEXT_HI, TEXT_MID, glassSx } from './tokens';

/**
 * Bento feature tiles: small glass cards that quietly explain the product in
 * plain language. Asymmetric on desktop (wide / pair / slim strip), a single
 * column below the login card on mobile.
 */

interface Tile {
  icon: ReactNode;
  title: string;
  body: string;
  /** Spans both bento columns on ≥sm. */
  wide?: boolean;
  /** Renders as a compact horizontal strip. */
  slim?: boolean;
}

const TILES: Tile[] = [
  {
    icon: <DescriptionOutlined />,
    title: 'Reads any RFQ document',
    body: 'Emails, PDFs, scans, spreadsheets — parsed into clean, structured line items.',
    wide: true,
  },
  {
    icon: <SmartToyOutlined />,
    title: 'A copilot, not an autopilot',
    body: 'Sources, compares, and quotes — with your approval at every step.',
  },
  {
    icon: <InsightsOutlined />,
    title: 'Pricing that shows its work',
    body: 'Smart recommendations with the reasoning laid out.',
  },
  {
    icon: <GppGoodOutlined />,
    title: 'Guardrailed and audited',
    body: 'Every AI action is policy-checked and logged.',
    wide: true,
    slim: true,
  },
];

const BentoTiles = () => (
  <Box
    component="ul"
    sx={{
      listStyle: 'none',
      m: 0,
      p: 0,
      display: 'grid',
      gap: 2,
      gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, minmax(0, 1fr))' },
    }}
  >
    {TILES.map((tile) => (
      <Box
        component="li"
        key={tile.title}
        sx={{
          ...glassSx,
          backgroundColor: 'rgba(13, 22, 40, 0.46)',
          backdropFilter: 'blur(14px) saturate(140%)',
          WebkitBackdropFilter: 'blur(14px) saturate(140%)',
          borderRadius: '20px',
          p: 2.5,
          gridColumn: { sm: tile.wide ? 'span 2' : 'auto' },
          display: 'flex',
          flexDirection: tile.slim ? 'row' : 'column',
          alignItems: tile.slim ? 'center' : 'flex-start',
          gap: tile.slim ? 1.75 : 1.5,
          transition: 'transform 0.25s ease, border-color 0.25s ease',
          '&:hover': {
            transform: 'translateY(-3px)',
            borderColor: 'rgba(143, 190, 223, 0.35)',
          },
          '@media (prefers-reduced-motion: reduce)': {
            transition: 'none',
            '&:hover': { transform: 'none' },
          },
        }}
      >
        <Box
          aria-hidden="true"
          sx={{
            width: 38,
            height: 38,
            borderRadius: '11px',
            flexShrink: 0,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            backgroundColor: 'rgba(70, 130, 180, 0.18)',
            border: '1px solid rgba(70, 130, 180, 0.32)',
            color: ACCENT_SOFT,
            '& svg': { fontSize: 20 },
          }}
        >
          {tile.icon}
        </Box>
        <Box>
          <Typography sx={{ fontSize: 14.5, fontWeight: 700, color: TEXT_HI, mb: tile.slim ? 0.25 : 0.5 }}>
            {tile.title}
          </Typography>
          <Typography sx={{ fontSize: 13, color: TEXT_MID, lineHeight: 1.55 }}>
            {tile.body}
          </Typography>
        </Box>
      </Box>
    ))}
  </Box>
);

export default BentoTiles;
