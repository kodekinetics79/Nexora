import React, { useEffect, useState } from 'react';
import { Box } from '@mui/material';
import { keyframes } from '@emotion/react';

/**
 * Pointer-following spotlight, retuned for the aurora stage.
 *
 * Two light layers (an electric-sky key light and a larger, fainter cool
 * sheen) gently follow the pointer. Since the "Depth Stack" refactor this
 * component owns NO event listeners: the page's single pointer system
 * (useDepthStage) writes `--spot-x` / `--spot-y` (raw px) onto the stage
 * element and the discs simply read the inherited vars. The CSS `transition`
 * on `transform` interpolates on the compositor and gives the light its
 * gentle lag (the sheen trails on a longer duration for depth). The container
 * also carries the +4px parallax translate (y at 0.6×) from `--px`/`--py`.
 *
 * Touch-first devices (`hover: none` / `pointer: coarse`) get no pointer
 * tracking — instead a very slow autonomous drift. Under
 * `prefers-reduced-motion` the drift is disabled entirely (matching the
 * aurora's motion policy) and the light parks at a static position.
 * The whole layer is `aria-hidden` and `pointer-events: none`; it is
 * lighting, not UI. Hidden below the `lg` two-column layout, where the
 * aurora alone carries the stage.
 */

const KEY_DRIFT = keyframes`
  0%   { transform: translate3d(10vw, 24vh, 0); }
  33%  { transform: translate3d(26vw, 58vh, 0); }
  66%  { transform: translate3d(8vw, 72vh, 0); }
  100% { transform: translate3d(10vw, 24vh, 0); }
`;

const SHEEN_DRIFT = keyframes`
  0%   { transform: translate3d(22vw, 62vh, 0); }
  40%  { transform: translate3d(6vw, 26vh, 0); }
  70%  { transform: translate3d(26vw, 42vh, 0); }
  100% { transform: translate3d(22vw, 62vh, 0); }
`;

const Spotlight: React.FC = () => {
  const [mode, setMode] = useState<'interactive' | 'drift'>('interactive');

  useEffect(() => {
    // Touch-first and reduced-motion users get the autonomous mode
    // (drift for touch, fully static under reduced motion — see sx below).
    const wantsDrift =
      window.matchMedia('(prefers-reduced-motion: reduce)').matches ||
      window.matchMedia('(hover: none)').matches ||
      window.matchMedia('(pointer: coarse)').matches;
    if (wantsDrift) setMode('drift');
  }, []);

  const isDrift = mode === 'drift';

  return (
    <Box
      aria-hidden="true"
      sx={{
        position: 'absolute',
        inset: 0,
        overflow: 'hidden',
        pointerEvents: 'none',
        // Below lg the layout stacks and the aurora alone carries the stage.
        display: { xs: 'none', lg: 'block' },
        // Near parallax layer: +4px × the lerped pointer offset (y at 0.6×).
        transform: 'translate3d(calc(var(--px, 0) * 4px), calc(var(--py, 0) * 2.4px), 0)',
        // Park position until the pointer system's first write.
        '--spot-x': '38vw',
        '--spot-y': '34vh',
      }}
    >
      {/* Electric-sky key light (ACCENT hue #38BDF8) */}
      <Box
        sx={{
          position: 'absolute',
          top: 0,
          left: 0,
          width: 680,
          height: 680,
          marginTop: '-340px',
          marginLeft: '-340px',
          borderRadius: '50%',
          willChange: 'transform',
          background:
            'radial-gradient(circle closest-side, rgba(125, 211, 252, 0.16) 0%, rgba(56, 189, 248, 0.06) 45%, rgba(56, 189, 248, 0) 72%)',
          ...(isDrift
            ? {
                transform: 'translate3d(26vw, 40vh, 0)',
                animation: `${KEY_DRIFT} 52s ease-in-out infinite`,
                '@media (prefers-reduced-motion: reduce)': {
                  animation: 'none',
                },
              }
            : {
                transform: 'translate3d(var(--spot-x), var(--spot-y), 0)',
                transition: 'transform 0.55s cubic-bezier(0.22, 1, 0.36, 1)',
              }),
        }}
      />
      {/* Trailing cool sheen (ACCENT_TINT hue #BAE6FD) */}
      <Box
        sx={{
          position: 'absolute',
          top: 0,
          left: 0,
          width: 1080,
          height: 1080,
          marginTop: '-540px',
          marginLeft: '-540px',
          borderRadius: '50%',
          willChange: 'transform',
          background:
            'radial-gradient(circle closest-side, rgba(186, 230, 253, 0.06) 0%, rgba(186, 230, 253, 0) 70%)',
          ...(isDrift
            ? {
                transform: 'translate3d(14vw, 52vh, 0)',
                animation: `${SHEEN_DRIFT} 68s ease-in-out infinite`,
                '@media (prefers-reduced-motion: reduce)': {
                  animation: 'none',
                },
              }
            : {
                transform: 'translate3d(var(--spot-x), var(--spot-y), 0)',
                transition: 'transform 1.05s cubic-bezier(0.22, 1, 0.36, 1)',
              }),
        }}
      />
    </Box>
  );
};

export default Spotlight;
