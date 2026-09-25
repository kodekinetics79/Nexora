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
 * the clay (so the theme's AA text tokens still hold — checked in neumorphic.test.ts), and the
 * shadow pair is deep enough (≥12px on slabs) that an edge is carried by shape rather than a line.
 */
export const NEU_SURFACE: Readonly<Record<PaletteMode, string>> = Object.freeze({
  light: '#EEECE7',
  dark: '#1A1D23',
});

const LIGHT = Object.freeze({
  light: 'rgba(255, 255, 255, 1)',
  dark: 'rgba(255, 255, 255, 0.065)',
});

const SHADE = Object.freeze({
  light: 'rgba(160, 148, 128, 0.58)',
  dark: 'rgba(0, 0, 0, 0.72)',
});


/** Distance of the shadow pair; blur is twice it. */
export type NeuDepth = 2 | 3 | 4 | 6 | 8 | 12 | 16 | 20;

export const neuRaised = (mode: PaletteMode, depth: NeuDepth = 8): string =>
  `${-depth}px ${-depth}px ${depth * 2}px ${LIGHT[mode]}, ${depth}px ${depth}px ${depth * 2}px ${SHADE[mode]}`;

export const neuInset = (mode: PaletteMode, depth: NeuDepth = 4): string =>
  `inset ${depth}px ${depth}px ${depth * 2}px ${SHADE[mode]}, inset ${-depth}px ${-depth}px ${depth * 2}px ${LIGHT[mode]}`;

/** A raised slab: same colour as the ground, lit from the top-left. */
export const neuSlab = (mode: PaletteMode, depth: NeuDepth = 8) => ({
  backgroundColor: NEU_SURFACE[mode],
  backgroundImage: 'none',
  border: '1px solid transparent',
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

/** A soft emboss for display type on the clay: lit edge up-left, shade down-right. */
export const neuEmboss = (mode: PaletteMode): string =>
  mode === 'dark'
    ? '-1px -1px 1px rgba(255, 255, 255, 0.06), 2px 2px 3px rgba(0, 0, 0, 0.8)'
    : '-1px -1px 1px rgba(255, 255, 255, 1), 2px 2px 3px rgba(160, 148, 128, 0.45)';

/** A raised, pill-shaped key: links and buttons on the clay. */
export const neuKey = (mode: PaletteMode) => ({
  ...NEU_TRANSITION,
  backgroundColor: NEU_SURFACE[mode],
  border: '1px solid transparent',
  borderRadius: 999,
  boxShadow: neuRaised(mode, 4),
  '&:hover': { backgroundColor: NEU_SURFACE[mode], boxShadow: neuRaised(mode, 6) },
  '&:active': { boxShadow: neuInset(mode, 3) },
});

/** A coloured chart mark extruded from the clay: soft pair outside, a lit bevel inside. */
export const neuBar = (mode: PaletteMode): string =>
  `${neuRaised(mode, 3)}, inset 1px 1px 1px rgba(255, 255, 255, ${mode === 'dark' ? 0.18 : 0.4}), inset -1px -1px 2px rgba(0, 0, 0, 0.18)`;

/**
 * The shadows published as custom properties on the band and the page, for the few places that
 * style from a plain object rather than a theme callback (list rows, SVG wrappers).
 */
export const neuCssVariables = (mode: PaletteMode): Record<string, string> => ({
  '--nx-neu-surface': NEU_SURFACE[mode],
  '--nx-neu-raised-sm': neuRaised(mode, 3),
  '--nx-neu-raised-md': neuRaised(mode, 6),
  '--nx-neu-inset-sm': neuInset(mode, 3),
  // The same soft pair for SVG marks, which cannot take a box-shadow.
  '--nx-neu-drop': mode === 'dark'
    ? 'drop-shadow(-2px -2px 3px rgba(255, 255, 255, 0.06)) drop-shadow(3px 3px 4px rgba(0, 0, 0, 0.7))'
    : 'drop-shadow(-2px -2px 3px rgba(255, 255, 255, 1)) drop-shadow(3px 3px 4px rgba(160, 148, 128, 0.6))',
});
