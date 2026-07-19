import { useEffect } from 'react';
import type { RefObject } from 'react';

/**
 * "Depth Stack" pointer system — the ONLY pointermove listener on the page.
 *
 * One rAF loop lerps a normalized pointer offset (−1…1 from viewport center,
 * 0.08/frame) and writes CSS custom properties via setProperty — the React
 * tree never re-renders:
 *
 *   stage:  --px --py    (parallax drivers: aurora ×−6, spotlight ×+4,
 *                         bento ×+3, card wrapper ×+5; y at 0.6×)
 *           --spot-x --spot-y  (raw px for the pointer spotlight)
 *   card:   --tiltX --tiltY    (±4°, 1:1 while inside card bounds+80px)
 *           --mx --my          (specular highlight position, %)
 *
 * On leaving the card zone the tilt springs home via a 600ms transition; while
 * tracking the transition is removed for 1:1 response. The loop stops when the
 * lerp settles (<0.001) and on visibilitychange. Frame guard: two consecutive
 * rAF deltas >34ms during the entrance window degrade the scene (halve
 * particles via the "nexora:degrade" event, hide the specular via .nx-degrade).
 *
 * Disabled entirely under prefers-reduced-motion, coarse pointers, and <900px.
 */

const SPRING_HOME = 'transform 600ms cubic-bezier(0.34, 1.56, 0.64, 1)';
const TILT_MAX = 4;
const ZONE = 80;
const LERP = 0.08;
const ENTRANCE_MS = 1600;

const clamp1 = (v: number) => Math.max(-1, Math.min(1, v));

export default function useDepthStage(
  stageRef: RefObject<HTMLElement | null>,
  tiltRef: RefObject<HTMLElement | null>,
): void {
  useEffect(() => {
    const stage = stageRef.current;
    const tilt = tiltRef.current;
    if (!stage || !tilt) return;
    if (
      window.matchMedia('(prefers-reduced-motion: reduce)').matches ||
      window.matchMedia('(hover: none)').matches ||
      window.matchMedia('(pointer: coarse)').matches ||
      window.matchMedia('(max-width: 899px)').matches
    ) {
      return;
    }

    let rafId: number | null = null;
    let rawX = window.innerWidth / 2;
    let rawY = window.innerHeight * 0.38;
    let px = 0;
    let py = 0;
    let tracking = false;
    let cardRect = tilt.getBoundingClientRect();
    let lastT = 0;
    let slowFrames = 0;
    let degraded = false;
    const start = performance.now();

    const invalidate = () => {
      cardRect = tilt.getBoundingClientRect();
    };

    const onTiltSettled = () => {
      tilt.style.willChange = 'auto';
    };

    const frame = (t: number) => {
      rafId = null;

      // Frame guard, entrance window only.
      if (!degraded && lastT !== 0 && t - start < ENTRANCE_MS) {
        if (t - lastT > 34) {
          slowFrames += 1;
          if (slowFrames >= 2) {
            degraded = true;
            stage.classList.add('nx-degrade');
            window.dispatchEvent(new Event('nexora:degrade'));
          }
        } else {
          slowFrames = 0;
        }
      }
      lastT = t;

      const tx = clamp1((rawX / window.innerWidth) * 2 - 1);
      const ty = clamp1((rawY / window.innerHeight) * 2 - 1);
      px += (tx - px) * LERP;
      py += (ty - py) * LERP;
      stage.style.setProperty('--px', px.toFixed(4));
      stage.style.setProperty('--py', py.toFixed(4));
      stage.style.setProperty('--spot-x', `${rawX.toFixed(1)}px`);
      stage.style.setProperty('--spot-y', `${rawY.toFixed(1)}px`);

      const inside =
        rawX > cardRect.left - ZONE &&
        rawX < cardRect.right + ZONE &&
        rawY > cardRect.top - ZONE &&
        rawY < cardRect.bottom + ZONE;

      if (inside) {
        if (!tracking) {
          tracking = true;
          tilt.style.transition = 'none';
          tilt.style.willChange = 'transform';
          tilt.removeEventListener('transitionend', onTiltSettled);
        }
        const nx = clamp1(((rawX - cardRect.left) / cardRect.width) * 2 - 1);
        const ny = clamp1(((rawY - cardRect.top) / cardRect.height) * 2 - 1);
        tilt.style.setProperty('--tiltY', `${(nx * TILT_MAX).toFixed(2)}deg`);
        tilt.style.setProperty('--tiltX', `${(-ny * TILT_MAX).toFixed(2)}deg`);
        tilt.style.setProperty('--mx', `${(((rawX - cardRect.left) / cardRect.width) * 100).toFixed(1)}%`);
        tilt.style.setProperty('--my', `${(((rawY - cardRect.top) / cardRect.height) * 100).toFixed(1)}%`);
      } else if (tracking) {
        tracking = false;
        tilt.style.transition = SPRING_HOME;
        tilt.style.setProperty('--tiltX', '0deg');
        tilt.style.setProperty('--tiltY', '0deg');
        tilt.addEventListener('transitionend', onTiltSettled, { once: true });
      }

      const settled =
        Math.abs(tx - px) < 0.001 &&
        Math.abs(ty - py) < 0.001 &&
        t - start > ENTRANCE_MS;
      if (!settled && !document.hidden) {
        rafId = requestAnimationFrame(frame);
      }
    };

    const kick = () => {
      if (rafId === null && !document.hidden) {
        rafId = requestAnimationFrame(frame);
      }
    };

    const onMove = (event: PointerEvent) => {
      rawX = event.clientX;
      rawY = event.clientY;
      kick();
    };

    const onVisibility = () => {
      if (document.hidden) {
        if (rafId !== null) {
          cancelAnimationFrame(rafId);
          rafId = null;
        }
      } else {
        kick();
      }
    };

    window.addEventListener('pointermove', onMove, { passive: true });
    window.addEventListener('resize', invalidate);
    window.addEventListener('scroll', invalidate, { passive: true });
    document.addEventListener('visibilitychange', onVisibility);
    kick(); // keep the frame guard sampling through the entrance

    return () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('resize', invalidate);
      window.removeEventListener('scroll', invalidate);
      document.removeEventListener('visibilitychange', onVisibility);
      tilt.removeEventListener('transitionend', onTiltSettled);
      if (rafId !== null) cancelAnimationFrame(rafId);
    };
  }, [stageRef, tiltRef]);
}
