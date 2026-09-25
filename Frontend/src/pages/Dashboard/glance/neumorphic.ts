import type { PaletteMode } from '@mui/material';

/**
 * The dashboard's soft, pressed-clay material (owner request 2026-09-25: "neumorphic touch").
 *
 * Neumorphism only reads when an object is the SAME colour as the ground it sits on and is shaped
 * purely by two shadows — a light one up-left, a dark one down-right. So the dashboard lays its own
 * clay ground (`NEU_SURFACE`) under the bands rather than relying on the app canvas and its brass
 * washes, and every raised or pressed object on the screen is that one colour.
 *
 * It is a variation of the Graphite & Brass tactile direction, not a replacement: the shadows are
 * graphite-warm rather than grey, brass stays the only accent, and pressed-in always means "this is
 * what is in force" — the chosen period key, a seal the period governs, a pip not yet earned.
 *
 * Neumorphism's known failure is low-contrast edges. Two guards: text never sits on anything but
 * the clay (so the theme's AA text tokens still hold — checked in neumorphic.test.ts), and each
 * raised object keeps a faint hairline so its edge survives on a bad monitor.
 */
export const NEU_SURFACE: Readonly<Record<PaletteMode, string>> = Object.freeze({
  light: '#EEECE7',
  dark: '#1A1D23',
});

const LIGHT = Object.freeze({
  light: 'rgba(255, 255, 255, 0.95)',
  dark: 'rgba(255, 255, 255, 0.045)',
});

const SHADE = Object.freeze({
  light: 'rgba(146, 136, 118, 0.42)',
  dark: 'rgba(0, 0, 0, 0.55)',
});

const HAIRLINE = Object.freeze({
  light: 'rgba(255, 255, 255, 0.6)',
  dark: 'rgba(255, 255, 255, 0.04)',
});

/** Distance of the shadow pair; blur is twice it. */
export type NeuDepth = 2 | 4 | 6 | 8 | 12;

export const neuRaised = (mode: PaletteMode, depth: NeuDepth = 8): string =>
  `${-depth}px ${-depth}px ${depth * 2}px ${LIGHT[mode]}, ${depth}px ${depth}px ${depth * 2}px ${SHADE[mode]}`;

export const neuInset = (mode: PaletteMode, depth: NeuDepth = 4): string =>
  `inset ${depth}px ${depth}px ${depth * 2}px ${SHADE[mode]}, inset ${-depth}px ${-depth}px ${depth * 2}px ${LIGHT[mode]}`;

/** A raised slab: same colour as the ground, lit from the top-left. */
export const neuSlab = (mode: PaletteMode, depth: NeuDepth = 8) => ({
  backgroundColor: NEU_SURFACE[mode],
  backgroundImage: 'none',
  border: '1px solid',
  borderColor: HAIRLINE[mode],
  boxShadow: neuRaised(mode, depth),
});

/** A pressed well: the same clay, pushed in. */
export const neuWell = (mode: PaletteMode, depth: NeuDepth = 4) => ({
  backgroundColor: NEU_SURFACE[mode],
  border: '1px solid transparent',
  boxShadow: neuInset(mode, depth),
});

/** Shadow motion only, and none at all for readers who asked for less. */
export const NEU_TRANSITION = {
  transition: 'box-shadow 180ms ease, transform 180ms ease',
  '@media (prefers-reduced-motion: reduce)': { transition: 'none' },
} as const;
