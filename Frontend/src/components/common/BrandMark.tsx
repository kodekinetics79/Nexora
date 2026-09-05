import React, { useId } from 'react';

/**
 * The Nexora mark, drawn as vectors so it scales from a 16px favicon to a login hero without a
 * raster in sight (the previous logo.svg wrapped a 267 KB PNG).
 *
 * Form: a bevelled tile of Governed Cobalt deepening to Evidence Navy, carrying an N built from
 * three ascending planes — evidence rising to a governed decision — in paper white turning to
 * Verified Teal at the leading edge. Depth comes from a real extrusion under the planes and a
 * top-left highlight on the tile, not from a drop shadow alone.
 *
 * `tint` lets the tile take a brand colour the tenant picked; the mark itself stays white/teal so
 * it reads on any of the twelve selectable brand colours.
 */
export interface BrandMarkProps {
  size?: number;
  tint?: string;          // tile base colour; defaults to Governed Cobalt
  tintDeep?: string;      // tile bottom colour; defaults to Evidence Navy
  title?: string;         // accessible name; empty string hides the mark from AT
  raised?: boolean;       // ambient shadow under the tile (off inside flat contexts)
}

const BrandMark: React.FC<BrandMarkProps> = ({
  size = 40, tint = '#075dcc', tintDeep = '#08172a', title = 'Nexora', raised = true,
}) => {
  const id = useId().replace(/:/g, '');
  const tile = `nx-tile-${id}`, bevel = `nx-bevel-${id}`, plane = `nx-plane-${id}`,
    extrude = `nx-extrude-${id}`, gloss = `nx-gloss-${id}`, shadow = `nx-shadow-${id}`;
  return (
    <svg
      width={size} height={size} viewBox="0 0 64 64" role={title ? 'img' : undefined}
      aria-label={title || undefined} aria-hidden={title ? undefined : true}
      style={{ display: 'block', flexShrink: 0, overflow: 'visible' }}
    >
      <defs>
        <linearGradient id={tile} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor={tint} />
          <stop offset="1" stopColor={tintDeep} />
        </linearGradient>
        <linearGradient id={bevel} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#ffffff" stopOpacity="0.30" />
          <stop offset="0.35" stopColor="#ffffff" stopOpacity="0.08" />
          <stop offset="1" stopColor="#000000" stopOpacity="0.28" />
        </linearGradient>
        <linearGradient id={plane} x1="0" y1="1" x2="1" y2="0">
          <stop offset="0" stopColor="#f8fafc" />
          <stop offset="0.55" stopColor="#e6fffb" />
          <stop offset="1" stopColor="#20c7b5" />
        </linearGradient>
        <linearGradient id={extrude} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#0b3f86" />
          <stop offset="1" stopColor="#041a34" />
        </linearGradient>
        <linearGradient id={gloss} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#ffffff" stopOpacity="0.24" />
          <stop offset="1" stopColor="#ffffff" stopOpacity="0" />
        </linearGradient>
        <filter id={shadow} x="-30%" y="-30%" width="160%" height="170%">
          <feDropShadow dx="0" dy="6" stdDeviation="5" floodColor={tintDeep} floodOpacity="0.38" />
        </filter>
      </defs>
      {/* tile */}
      <g filter={raised ? `url(#${shadow})` : undefined}>
        <rect x="2" y="2" width="60" height="60" rx="15" fill={`url(#${tile})`} />
        <rect x="2" y="2" width="60" height="60" rx="15" fill={`url(#${bevel})`} />
        <rect x="3" y="3" width="58" height="58" rx="14" fill="none" stroke="#ffffff" strokeOpacity="0.22" />
      </g>
      {/* extrusion: the same three planes, dropped and darkened */}
      <g transform="translate(0 3.2)" fill={`url(#${extrude})`}>
        <path d="M15 46 L15 20 L23 20 L23 46 Z" />
        <path d="M23 20 L31 20 L47 44 L47 46 L39 46 Z" />
        <path d="M41 46 L41 20 L49 20 L49 46 Z" />
      </g>
      {/* the N: two uprights and one rising diagonal, leading edge in teal */}
      <g fill={`url(#${plane})`}>
        <path d="M15 46 L15 20 L23 20 L23 46 Z" />
        <path d="M23 20 L31 20 L47 44 L47 46 L39 46 Z" />
        <path d="M41 46 L41 20 L49 20 L49 46 Z" />
      </g>
      {/* gloss across the top third of the tile */}
      <path d="M17 2 H47 A15 15 0 0 1 62 17 V24 C50 30 14 30 2 24 V17 A15 15 0 0 1 17 2 Z" fill={`url(#${gloss})`} />
    </svg>
  );
};

export default BrandMark;
