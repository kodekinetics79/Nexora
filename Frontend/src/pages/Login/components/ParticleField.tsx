import { useEffect, useRef, useState } from 'react';
import { Box } from '@mui/material';
import { EASE_OUT, fadeIn } from './motion';

/**
 * Idle-life particle canvas — sits between the aurora and the content layer.
 *
 * 45 particles desktop / 24 on coarse-pointer or <900px viewports, drawn from
 * three 8px pre-rendered radial-gradient sprites (70% #7DD3FC, 20% #22D3EE,
 * 10% white — NO shadowBlur). Each particle: 1–2.5px scale, base alpha
 * .15–.5 with a ±.15 sine fade on a 4–9s period, upward drift 6–14px/s with
 * ±4px sway, wrapping at the edges. globalAlpha is scaled ×0.85 overall.
 * DPR capped at 2 (1.5 coarse). One rAF, one draw pass, zero per-frame
 * allocation. Pauses on document.hidden; halves its count on the
 * "nexora:degrade" frame-guard event. Unmounted under prefers-reduced-motion.
 */

const MAX = 45;

const makeSprite = (r: number, g: number, b: number): HTMLCanvasElement => {
  const c = document.createElement('canvas');
  c.width = 8;
  c.height = 8;
  const x = c.getContext('2d')!;
  const grad = x.createRadialGradient(4, 4, 0, 4, 4, 4);
  grad.addColorStop(0, `rgba(${r}, ${g}, ${b}, 1)`);
  grad.addColorStop(0.55, `rgba(${r}, ${g}, ${b}, 0.45)`);
  grad.addColorStop(1, `rgba(${r}, ${g}, ${b}, 0)`);
  x.fillStyle = grad;
  x.fillRect(0, 0, 8, 8);
  return c;
};

const ParticleField = () => {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  // Decided once at mount; reduced-motion users never get the canvas at all.
  const [enabled] = useState(
    () => !window.matchMedia('(prefers-reduced-motion: reduce)').matches,
  );

  useEffect(() => {
    if (!enabled) return;
    const canvas = canvasRef.current;
    const ctx = canvas?.getContext('2d');
    if (!canvas || !ctx) return;

    const coarse =
      window.matchMedia('(hover: none)').matches ||
      window.matchMedia('(pointer: coarse)').matches ||
      window.matchMedia('(max-width: 899px)').matches;
    let count = coarse ? 24 : MAX;
    const dpr = Math.min(window.devicePixelRatio || 1, coarse ? 1.5 : 2);

    let w = 0;
    let h = 0;
    const resize = () => {
      w = window.innerWidth;
      h = window.innerHeight;
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    };
    resize();

    const sprites = [makeSprite(125, 211, 252), makeSprite(34, 211, 238), makeSprite(255, 255, 255)];

    // All per-particle state preallocated — nothing is created per frame.
    const xs = new Float32Array(MAX);
    const ys = new Float32Array(MAX);
    const size = new Float32Array(MAX);
    const vy = new Float32Array(MAX);
    const baseA = new Float32Array(MAX);
    const fadeW = new Float32Array(MAX);
    const phase = new Float32Array(MAX);
    const swayAmp = new Float32Array(MAX);
    const swayW = new Float32Array(MAX);
    const sprite = new Uint8Array(MAX);
    for (let i = 0; i < MAX; i++) {
      xs[i] = Math.random() * w;
      ys[i] = Math.random() * h;
      size[i] = 1 + Math.random() * 1.5; // 1–2.5px
      vy[i] = 6 + Math.random() * 8; // 6–14px/s upward
      baseA[i] = 0.15 + Math.random() * 0.35; // .15–.5
      fadeW[i] = (Math.PI * 2) / (4000 + Math.random() * 5000); // 4–9s
      phase[i] = Math.random() * Math.PI * 2;
      swayAmp[i] = 1 + Math.random() * 3; // ≤4px
      swayW[i] = (Math.PI * 2) / (3000 + Math.random() * 4000);
      const r = Math.random();
      sprite[i] = r < 0.7 ? 0 : r < 0.9 ? 1 : 2;
    }

    let rafId: number | null = null;
    let last = performance.now();

    const tick = (t: number) => {
      rafId = null;
      const dt = Math.min((t - last) / 1000, 0.1);
      last = t;
      ctx.clearRect(0, 0, w, h);
      for (let i = 0; i < count; i++) {
        ys[i] -= vy[i] * dt;
        if (ys[i] < -6) {
          ys[i] = h + 6;
          xs[i] = Math.random() * w;
        }
        const a = (baseA[i] + 0.15 * Math.sin(t * fadeW[i] + phase[i])) * 0.85;
        if (a <= 0.01) continue;
        ctx.globalAlpha = a > 1 ? 1 : a;
        const d = size[i] * 2; // drawn diameter incl. soft falloff
        ctx.drawImage(
          sprites[sprite[i]],
          xs[i] + Math.sin(t * swayW[i] + phase[i]) * swayAmp[i] - d / 2,
          ys[i] - d / 2,
          d,
          d,
        );
      }
      if (!document.hidden) rafId = requestAnimationFrame(tick);
    };

    const kick = () => {
      if (rafId === null && !document.hidden) {
        last = performance.now();
        rafId = requestAnimationFrame(tick);
      }
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
    const onDegrade = () => {
      count = Math.max(1, Math.floor(count / 2));
    };

    window.addEventListener('resize', resize);
    document.addEventListener('visibilitychange', onVisibility);
    window.addEventListener('nexora:degrade', onDegrade);
    kick();

    return () => {
      window.removeEventListener('resize', resize);
      document.removeEventListener('visibilitychange', onVisibility);
      window.removeEventListener('nexora:degrade', onDegrade);
      if (rafId !== null) cancelAnimationFrame(rafId);
    };
  }, [enabled]);

  if (!enabled) return null;

  return (
    <Box
      component="canvas"
      ref={canvasRef}
      aria-hidden="true"
      sx={{
        position: 'fixed',
        inset: 0,
        width: '100%',
        height: '100%',
        zIndex: 0,
        pointerEvents: 'none',
        // Entrance: fades in 900–1200ms (630ms start on compressed mobile).
        animation: `${fadeIn} 300ms ${EASE_OUT} both`,
        animationDelay: { xs: '630ms', md: '900ms' },
      }}
    />
  );
};

export default ParticleField;
