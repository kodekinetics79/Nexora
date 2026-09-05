import React, { useId } from 'react';

/**
 * The Nexora mark: an extruded N, standing on its own.
 *
 * Three strokes — two uprights and a rising diagonal — cut as one solid block. The face is brass
 * with a facet of light across its upper-left; the block is extruded down and to the right in
 * graphite so it reads as an object with weight, and it floats over a soft ground shadow. A slow
 * glint crosses the face every few seconds; it stops under the OS "reduce motion" setting.
 *
 * Drawn as vectors so it scales from the 16px browser tab to the sign-in hero without a raster.
 * `face` lets the block take a brand colour the tenant picked; the extrusion stays graphite.
 */
export interface BrandMarkProps {
  size?: number;
  face?: string;          // brass by default; the shaded end is derived, so any brand colour works
  title?: string;         // accessible name; empty string hides the mark from AT
  raised?: boolean;       // ground shadow under the block
  animated?: boolean;     // the glint
}

// The three strokes of the N, on a 64-unit canvas.
const STROKES = [
  'M12 48 L12 16 L22 16 L22 48 Z',
  'M22 16 L32 16 L52 44 L52 48 L42 48 L22 22 Z',
  'M42 48 L42 16 L52 16 L52 48 Z',
];

const BrandMark: React.FC<BrandMarkProps> = ({
  size = 40, face = '#e0a100', title = 'Nexora', raised = true, animated = true,
}) => {
  const id = useId().replace(/:/g, '');
  const gFace = `nx-face-${id}`, gSide = `nx-side-${id}`, gFacet = `nx-facet-${id}`, gShade = `nx-shade-${id}`,
    clip = `nx-clip-${id}`, blur = `nx-blur-${id}`;
  const depth = 5;
  return (
    <svg
      width={size} height={size} viewBox="0 0 64 64" role={title ? 'img' : undefined}
      aria-label={title || undefined} aria-hidden={title ? undefined : true}
      data-decorative-motion={animated ? 'true' : undefined}
      style={{ display: 'block', flexShrink: 0, overflow: 'visible' }}
    >
      <defs>
        <linearGradient id={gFace} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#fff1c9" />
          <stop offset="0.42" stopColor={face} />
          <stop offset="1" stopColor={face} />
        </linearGradient>
        <linearGradient id={gShade} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0.45" stopColor="#000000" stopOpacity="0" />
          <stop offset="1" stopColor="#000000" stopOpacity="0.34" />
        </linearGradient>
        <linearGradient id={gSide} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#3a4050" />
          <stop offset="1" stopColor="#0f1218" />
        </linearGradient>
        <linearGradient id={gFacet} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#ffffff" stopOpacity="0.34" />
          <stop offset="0.5" stopColor="#ffffff" stopOpacity="0" />
        </linearGradient>
        <clipPath id={clip}>{STROKES.map((d) => <path key={d} d={d} />)}</clipPath>
        <filter id={blur} x="-40%" y="-40%" width="180%" height="180%"><feGaussianBlur stdDeviation="2.2" /></filter>
      </defs>
      {raised && <ellipse cx="34" cy="56" rx="22" ry="4" fill="#0f1218" opacity="0.35" filter={`url(#${blur})`} />}
      {/* extrusion: the block stepped down-right, one unit at a time, so every edge is solid */}
      {Array.from({ length: depth }, (_, i) => depth - i).map((o) => (
        <g key={o} transform={`translate(${o} ${o})`} fill={`url(#${gSide})`}>
          {STROKES.map((d) => <path key={d} d={d} />)}
        </g>
      ))}
      {/* face */}
      <g fill={`url(#${gFace})`}>{STROKES.map((d) => <path key={d} d={d} />)}</g>
      {/* facet of light across the upper-left of the face */}
      <g clipPath={`url(#${clip})`}>
        <rect x="0" y="0" width="64" height="64" fill={`url(#${gShade})`} />
        <rect x="0" y="0" width="64" height="64" fill={`url(#${gFacet})`} />
        {animated && (
          <rect x="-24" y="0" width="16" height="64" fill="#ffffff" opacity="0.55"
            transform="skewX(-20)" className="nx-glint" />
        )}
      </g>
      {/* hairline along the top edges so the face separates from the extrusion */}
      <g fill="none" stroke="#fff8e6" strokeOpacity="0.55" strokeWidth="0.8">
        <path d="M12 16 H22" /><path d="M22 16 H32" /><path d="M42 16 H52" />
      </g>
    </svg>
  );
};

export default BrandMark;
