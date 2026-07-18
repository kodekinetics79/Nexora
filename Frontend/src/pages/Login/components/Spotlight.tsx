import React, { useEffect, useRef, useState } from 'react';
import { Box } from '@mui/material';
import { keyframes } from '@emotion/react';

/**
 * Pointer-following spotlight, grafted from the cinematic-split concept and
 * recolored/retuned for the aurora stage.
 *
 * Two light layers (a steel-blue key light and a larger, fainter cool sheen)
 * gently follow the pointer. Because this layers ON TOP of the aurora field,
 * the disc opacities are deliberately lower than the original concept
 * (key 0.08 vs 0.13, sheen 0.03 vs 0.045) so the two systems never read as
 * busy together. Implementation notes:
 *
 * - The layers are fixed-size radial-gradient discs moved with
 *   `transform: translate3d(var(--spot-x), var(--spot-y), 0)` only — no
 *   layout, no repaint of the gradient itself; the CSS `transition` on
 *   `transform` interpolates on the compositor and gives the light its
 *   gentle lag (the sheen trails on a longer duration for depth).
 * - `mousemove` writes are rAF-throttled: at most one CSS custom-property
 *   write per frame, straight onto the DOM node — the React tree never
 *   re-renders while the pointer moves. The stage rect is cached and
 *   refreshed on resize/scroll.
 * - Touch-first devices (`hover: none` / `pointer: coarse`) get no pointer
 *   tracking — instead a very slow autonomous drift. Under
 *   `prefers-reduced-motion` the drift is disabled entirely (matching the
 *   aurora's motion policy) and the light parks at a static position.
 * - The whole layer is `aria-hidden` and `pointer-events: none`; it is
 *   lighting, not UI. Hidden below the `lg` two-column layout, where the
 *   aurora alone carries the stage.
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
  const layerRef = useRef<HTMLDivElement | null>(null);
  const [mode, setMode] = useState<'interactive' | 'drift'>('interactive');

  useEffect(() => {
    const layer = layerRef.current;
    const stage = layer ? layer.parentElement : null;
    if (!layer || !stage) return;

    // Touch-first and reduced-motion users get the autonomous mode
    // (drift for touch, fully static under reduced motion — see sx below).
    const wantsDrift =
      window.matchMedia('(prefers-reduced-motion: reduce)').matches ||
      window.matchMedia('(hover: none)').matches ||
      window.matchMedia('(pointer: coarse)').matches;
    if (wantsDrift) {
      setMode('drift');
      return;
    }

    let rect = stage.getBoundingClientRect();

    // Park the light near the headline until the first pointer move.
    layer.style.setProperty('--spot-x', `${rect.width * 0.42}px`);
    layer.style.setProperty('--spot-y', `${rect.height * 0.36}px`);

    let frame: number | null = null;
    let pointerX = 0;
    let pointerY = 0;

    const onMove = (event: MouseEvent) => {
      pointerX = event.clientX;
      pointerY = event.clientY;
      if (frame !== null) return; // rAF throttle: one style write per frame
      frame = requestAnimationFrame(() => {
        frame = null;
        layer.style.setProperty('--spot-x', `${pointerX - rect.left}px`);
        layer.style.setProperty('--spot-y', `${pointerY - rect.top}px`);
      });
    };

    // The rect is viewport-relative, so refresh the cache when either the
    // viewport size or the scroll position changes (short viewports scroll).
    const onInvalidate = () => {
      rect = stage.getBoundingClientRect();
    };

    stage.addEventListener('mousemove', onMove);
    window.addEventListener('resize', onInvalidate);
    window.addEventListener('scroll', onInvalidate, { passive: true });
    return () => {
      stage.removeEventListener('mousemove', onMove);
      window.removeEventListener('resize', onInvalidate);
      window.removeEventListener('scroll', onInvalidate);
      if (frame !== null) cancelAnimationFrame(frame);
    };
  }, []);

  const isDrift = mode === 'drift';

  return (
    <Box
      ref={layerRef}
      aria-hidden="true"
      sx={{
        position: 'absolute',
        inset: 0,
        overflow: 'hidden',
        pointerEvents: 'none',
        // Below lg the layout stacks and the aurora alone carries the stage.
        display: { xs: 'none', lg: 'block' },
        '--spot-x': '38vw',
        '--spot-y': '34vh',
      }}
    >
      {/* Steel-blue key light (ACCENT_SOFT hue #8FBEDF, kept faint over the aurora) */}
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
            'radial-gradient(circle closest-side, rgba(143, 190, 223, 0.08) 0%, rgba(143, 190, 223, 0.03) 45%, rgba(143, 190, 223, 0) 72%)',
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
      {/* Trailing cool sheen (ACCENT_TINT hue #C9DFF0) */}
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
            'radial-gradient(circle closest-side, rgba(201, 223, 240, 0.03) 0%, rgba(201, 223, 240, 0) 70%)',
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
