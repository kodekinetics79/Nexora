import { Box } from '@mui/material';
import { keyframes } from '@emotion/react';
import { PAGE_BG } from './tokens';
import { EASE_OUT, fadeIn } from './motion';

/**
 * Aurora light field for the pre-auth stage.
 *
 * Two systems of light:
 *  1. A static base wash baked into the container background — three large
 *     radial gradients (blue / violet / cyan) that guarantee the stage is
 *     never flat black even before the animated layer paints.
 *  2. Three drifting blurred blobs in the same hue family with real
 *     luminance, moving on 30–46s transform-only cycles (the blur is static),
 *     fully disabled under prefers-reduced-motion.
 *
 * A line grid (masked to the center) and an edge vignette sit on top to give
 * the light structure and keep the corners quiet. Fixed + self-clipping so it
 * never affects scroll height.
 */

// Idle drift per choreography spec: ±40px elliptical wander + scale 1↔1.08,
// ease-in-out ALTERNATE on 26/34/42s periods. Opacity stays static.
const driftA = keyframes`
  0%   { transform: translate3d(-40px, -22px, 0) scale(1); }
  50%  { transform: translate3d(8px, 30px, 0) scale(1.05); }
  100% { transform: translate3d(40px, -14px, 0) scale(1.08); }
`;

const driftB = keyframes`
  0%   { transform: translate3d(36px, 24px, 0) scale(1.08); }
  50%  { transform: translate3d(-10px, -32px, 0) scale(1.03); }
  100% { transform: translate3d(-40px, 18px, 0) scale(1); }
`;

const driftC = keyframes`
  0%   { transform: translate3d(24px, -34px, 0) scale(1); }
  50%  { transform: translate3d(-30px, 6px, 0) scale(1.08); }
  100% { transform: translate3d(18px, 38px, 0) scale(1.02); }
`;

const REDUCED_MOTION = '@media (prefers-reduced-motion: reduce)';

const blobBaseSx = {
  position: 'absolute',
  borderRadius: '50%',
  filter: 'blur(90px)',
  willChange: 'transform',
  [REDUCED_MOTION]: { animation: 'none' },
} as const;

const AuroraBackdrop = () => (
  <Box
    aria-hidden="true"
    sx={{
      position: 'fixed',
      // Oversized so the parallax translate never exposes an edge.
      inset: '-12px',
      zIndex: 0,
      overflow: 'hidden',
      pointerEvents: 'none',
      // Far parallax layer: −6px × the lerped pointer offset (y at 0.6×).
      transform: 'translate3d(calc(var(--px, 0) * -6px), calc(var(--py, 0) * -3.6px), 0)',
      // Entrance: stage light fades up over 600ms. Reduced motion: static rest.
      animation: `${fadeIn} 600ms ${EASE_OUT} both`,
      [REDUCED_MOTION]: { animation: 'none' },
      // Static base wash: light lives in the corners (top-right blue behind
      // the card, bottom-left violet under the tiles); the center stays deep
      // so the glows read as glows, not as a bright fog.
      background: `
        radial-gradient(65% 60% at 82% 8%, rgba(37, 99, 235, 0.44) 0%, transparent 56%),
        radial-gradient(55% 50% at 6% 98%, rgba(124, 58, 237, 0.36) 0%, transparent 58%),
        radial-gradient(40% 35% at 18% 2%, rgba(34, 211, 238, 0.16) 0%, transparent 60%),
        linear-gradient(180deg, #081028 0%, ${PAGE_BG} 55%, #04060E 100%)
      `,
    }}
  >
    {/* Electric-blue blob — upper right, directly behind the glass card */}
    <Box
      sx={{
        ...blobBaseSx,
        width: '52vmax',
        height: '52vmax',
        top: '-18%',
        right: '-10%',
        background:
          'radial-gradient(circle at 42% 38%, rgba(56, 189, 248, 0.48) 0%, rgba(37, 99, 235, 0.26) 42%, transparent 66%)',
        animation: `${driftA} 26s ease-in-out infinite alternate`,
      }}
    />
    {/* Violet blob — lower left, under the bento tiles */}
    <Box
      sx={{
        ...blobBaseSx,
        width: '54vmax',
        height: '54vmax',
        bottom: '-28%',
        left: '-18%',
        background:
          'radial-gradient(circle at 60% 40%, rgba(139, 92, 246, 0.42) 0%, rgba(124, 58, 237, 0.22) 45%, transparent 66%)',
        animation: `${driftB} 34s ease-in-out infinite alternate`,
      }}
    />
    {/* Cyan glint — upper left, a cool edge of light near the headline */}
    <Box
      sx={{
        ...blobBaseSx,
        width: '32vmax',
        height: '32vmax',
        top: '-6%',
        left: '4%',
        background:
          'radial-gradient(circle at 50% 50%, rgba(34, 211, 238, 0.22) 0%, transparent 66%)',
        animation: `${driftC} 42s ease-in-out infinite alternate`,
      }}
    />
    {/* Horizon beam along the top edge */}
    <Box
      sx={{
        position: 'absolute',
        top: 0,
        left: '10%',
        right: '10%',
        // NOTE: sx `height: 1` means 100% in MUI system units — this must
        // stay an explicit px string to remain a hairline.
        height: '1px',
        background:
          'linear-gradient(90deg, transparent 0%, rgba(125, 211, 252, 0.55) 30%, rgba(186, 230, 253, 0.85) 50%, rgba(139, 92, 246, 0.45) 72%, transparent 100%)',
        boxShadow: '0 0 32px 3px rgba(96, 165, 250, 0.45)',
      }}
    />
    {/* Fine line grid, masked to the lit center */}
    <Box
      sx={{
        position: 'absolute',
        inset: 0,
        backgroundImage: `
          linear-gradient(rgba(148, 183, 255, 0.07) 1px, transparent 1px),
          linear-gradient(90deg, rgba(148, 183, 255, 0.07) 1px, transparent 1px)
        `,
        backgroundSize: '56px 56px',
        maskImage: 'radial-gradient(85% 75% at 55% 40%, black 30%, transparent 95%)',
        WebkitMaskImage: 'radial-gradient(85% 75% at 55% 40%, black 30%, transparent 95%)',
      }}
    />
    {/* Edge vignette: keeps corners quiet, focuses the composition */}
    <Box
      sx={{
        position: 'absolute',
        inset: 0,
        background: 'radial-gradient(120% 105% at 50% 40%, transparent 48%, rgba(2, 4, 11, 0.60) 100%)',
      }}
    />
  </Box>
);

export default AuroraBackdrop;
