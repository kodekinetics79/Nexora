import { Box } from '@mui/material';
import { keyframes } from '@emotion/react';
import { PAGE_BG } from './tokens';

/**
 * Ambient aurora field for the pre-auth stage: three blurred radial blobs in
 * the brand hue family, drifting on 28–44s cycles. Pure CSS, transform-only
 * animation (the blur is static), fully disabled under prefers-reduced-motion.
 * Fixed + self-clipping so it never affects scroll height.
 *
 * Blob opacities were trimmed slightly (0.34→0.30, 0.30→0.26, 0.18→0.14)
 * when the pointer-following Spotlight was layered above this field, so the
 * combined brightness stays calm — the spotlight now provides the local
 * lift the brighter blobs used to.
 */

const driftA = keyframes`
  0%, 100% { transform: translate3d(-6%, -4%, 0) scale(1); }
  50%      { transform: translate3d(9%, 7%, 0) scale(1.18); }
`;

const driftB = keyframes`
  0%, 100% { transform: translate3d(5%, 8%, 0) scale(1.1); }
  50%      { transform: translate3d(-8%, -6%, 0) scale(0.94); }
`;

const driftC = keyframes`
  0%, 100% { transform: translate3d(4%, -6%, 0) scale(0.96); }
  50%      { transform: translate3d(-6%, 8%, 0) scale(1.14); }
`;

const REDUCED_MOTION = '@media (prefers-reduced-motion: reduce)';

const blobBaseSx = {
  position: 'absolute',
  borderRadius: '50%',
  filter: 'blur(110px)',
  willChange: 'transform',
  [REDUCED_MOTION]: { animation: 'none' },
} as const;

const AuroraBackdrop = () => (
  <Box
    aria-hidden="true"
    sx={{
      position: 'fixed',
      inset: 0,
      zIndex: 0,
      overflow: 'hidden',
      pointerEvents: 'none',
      background: `radial-gradient(120% 90% at 72% 18%, #0C1526 0%, ${PAGE_BG} 55%, #05080F 100%)`,
    }}
  >
    {/* Accent blob — upper right, behind the glass card */}
    <Box
      sx={{
        ...blobBaseSx,
        width: '52vmax',
        height: '52vmax',
        top: '-18%',
        right: '-12%',
        background: 'radial-gradient(circle at 35% 35%, rgba(70, 130, 180, 0.30) 0%, transparent 68%)',
        animation: `${driftA} 32s ease-in-out infinite`,
      }}
    />
    {/* Deep steel blob — lower left */}
    <Box
      sx={{
        ...blobBaseSx,
        width: '58vmax',
        height: '58vmax',
        bottom: '-28%',
        left: '-16%',
        background: 'radial-gradient(circle at 60% 40%, rgba(46, 94, 143, 0.26) 0%, transparent 66%)',
        animation: `${driftB} 44s ease-in-out infinite`,
      }}
    />
    {/* Light steel blob — center drift */}
    <Box
      sx={{
        ...blobBaseSx,
        width: '40vmax',
        height: '40vmax',
        top: '30%',
        left: '32%',
        background: 'radial-gradient(circle at 50% 50%, rgba(110, 168, 212, 0.14) 0%, transparent 70%)',
        animation: `${driftC} 38s ease-in-out infinite`,
      }}
    />
    {/* Static dot-grid texture, faded toward the edges */}
    <Box
      sx={{
        position: 'absolute',
        inset: 0,
        backgroundImage: 'radial-gradient(rgba(255, 255, 255, 0.05) 1px, transparent 1px)',
        backgroundSize: '28px 28px',
        maskImage: 'radial-gradient(90% 80% at 50% 45%, black 40%, transparent 100%)',
        WebkitMaskImage: 'radial-gradient(90% 80% at 50% 45%, black 40%, transparent 100%)',
      }}
    />
  </Box>
);

export default AuroraBackdrop;
