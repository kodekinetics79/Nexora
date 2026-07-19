import { keyframes } from '@emotion/react';

/**
 * "First Light / Depth Stack" choreography primitives (creative-panel spec).
 * Entrance is pure CSS keyframes + animation-delay constants; every keyframe
 * animates transform/opacity only (compositor-friendly). All entrance
 * animations use `backwards` fill so the element's BASE styles (including the
 * tilt CSS vars on the card) take over the moment the animation ends.
 */

export const EASE_OUT = 'cubic-bezier(0.16, 1, 0.3, 1)';
export const EASE_SPRING = 'cubic-bezier(0.34, 1.56, 0.64, 1)';
export const REDUCED_MOTION = '@media (prefers-reduced-motion: reduce)';

/** Simple fade (aurora entrance, particle canvas, reduced-motion page fade). */
export const fadeIn = keyframes`
  from { opacity: 0; }
  to   { opacity: 1; }
`;

/** Eyebrow / headline / subline entrance. */
export const riseIn = keyframes`
  from { opacity: 0; transform: translateY(24px); }
  to   { opacity: 1; transform: translateY(0); }
`;

/** Bento tile entrance. */
export const tileIn = keyframes`
  from { opacity: 0; transform: translateY(18px) scale(0.97); }
  to   { opacity: 1; transform: translateY(0) scale(1); }
`;

/** Card Z-settle (runs under the card wrapper's perspective: 1200px). */
export const cardMove = keyframes`
  from { transform: translateZ(-60px) translateY(16px); }
  to   { transform: translateZ(0) translateY(0); }
`;

/** One-shot 30° light sweep across the stage. Peak opacity 0.5 mid-travel. */
export const stageSweep = keyframes`
  0%   { transform: translateX(-70%); opacity: 0; }
  45%  { opacity: 0.5; }
  100% { transform: translateX(70%); opacity: 0; }
`;

/** Card border light: one gradient trace via background-position, then gone. */
export const borderTrace = keyframes`
  0%   { background-position: 250% 0; opacity: 0; }
  15%  { opacity: 1; }
  85%  { opacity: 1; }
  100% { background-position: -150% 0; opacity: 0; }
`;

/** Error shake on the glass card body (not the entrance-animated frame). */
export const cardShake = keyframes`
  0%   { transform: translateX(0); }
  25%  { transform: translateX(-4px); }
  50%  { transform: translateX(4px); }
  75%  { transform: translateX(-2px); }
  100% { transform: translateX(0); }
`;

/** Alert entrance. */
export const alertIn = keyframes`
  from { opacity: 0; transform: translateY(8px); }
  to   { opacity: 1; transform: translateY(0); }
`;

/** CTA ambient glow pulse (opacity of the glow pseudo-layer only). */
export const glowPulse = keyframes`
  0%, 100% { opacity: 0.55; }
  50%      { opacity: 0.75; }
`;

/** Icon-chip breathing: ±3% around 0.6, sub-perceptual. */
export const chipBreathe = keyframes`
  0%, 100% { opacity: 0.57; }
  50%      { opacity: 0.63; }
`;

/** Loading sheen sweep across the CTA (background-position only). */
export const sheenSweep = keyframes`
  from { background-position: 250% 0, 0 0; }
  to   { background-position: -150% 0, 0 0; }
`;

/** Password-reveal icon: cross-fade + rotateY 90→0 on the icon only. */
export const iconFlip = keyframes`
  0%   { opacity: 0; transform: rotateY(90deg); }
  75%  { opacity: 1; }
  100% { opacity: 1; transform: rotateY(0deg); }
`;

/** Org-chooser shared-axis: outgoing content. */
export const swapOut = keyframes`
  from { opacity: 1; transform: translateY(0); }
  to   { opacity: 0; transform: translateY(-12px); }
`;

/** Org-chooser shared-axis: incoming content. */
export const swapIn = keyframes`
  from { opacity: 0; transform: translateY(12px); }
  to   { opacity: 1; transform: translateY(0); }
`;

/** Reduced-motion chooser swap: opacity only. */
export const swapFade = keyframes`
  from { opacity: 0; }
  to   { opacity: 1; }
`;

/** Height reveal for the swapped form region (grid-template-rows 0fr→1fr). */
export const rowsIn = keyframes`
  from { grid-template-rows: 0fr; }
  to   { grid-template-rows: 1fr; }
`;
