import type { PaletteMode } from '@mui/material';
import { readableOn } from '../../../utils/contrast';

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
  light: 'rgba(150, 137, 116, 0.66)',
  dark: 'rgba(0, 0, 0, 0.8)',
});


/** Distance of the shadow pair; blur is twice it. */
export type NeuDepth = 2 | 3 | 4 | 6 | 8 | 12 | 16 | 20;

/**
 * The crisp rim that makes an edge read as dense rather than hazy: a hard 1px lit bevel on the
 * top-left and a hard 1px shade on the bottom-right, inside the shape. The soft pair gives the lift;
 * the rim gives the outline.
 */
const RIM = Object.freeze({
  light: { lit: 'rgba(255, 255, 255, 0.95)', shade: 'rgba(120, 108, 90, 0.38)' },
  dark: { lit: 'rgba(255, 255, 255, 0.10)', shade: 'rgba(0, 0, 0, 0.65)' },
});

// Blur is 1.5× the distance (it was 2×): a tighter shadow pair keeps the silhouette dense.
const blur = (depth: number) => Math.round(depth * 1.5);

export const neuRaised = (mode: PaletteMode, depth: NeuDepth = 8): string =>
  `inset 1px 1px 0 ${RIM[mode].lit}, inset -1px -1px 0 ${RIM[mode].shade}, `
  + `${-depth}px ${-depth}px ${blur(depth)}px ${LIGHT[mode]}, ${depth}px ${depth}px ${blur(depth)}px ${SHADE[mode]}`;

export const neuInset = (mode: PaletteMode, depth: NeuDepth = 4): string =>
  `inset 1px 1px 0 ${RIM[mode].shade}, inset -1px -1px 0 ${RIM[mode].lit}, `
  + `inset ${depth}px ${depth}px ${blur(depth)}px ${SHADE[mode]}, inset ${-depth}px ${-depth}px ${blur(depth)}px ${LIGHT[mode]}`;

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
    ? 'drop-shadow(-2px -2px 2px rgba(255, 255, 255, 0.08)) drop-shadow(3px 3px 3px rgba(0, 0, 0, 0.8))'
    : 'drop-shadow(-2px -2px 2px rgba(255, 255, 255, 1)) drop-shadow(3px 3px 3px rgba(150, 137, 116, 0.7))',
});

/**
 * A text colour that the theme derived against WHITE paper, re-derived against the clay.
 *
 * The clay is darker than paper, so brand and status inks that clear 4.5:1 on white (the brass link
 * at 4.6, the success green) drop just under it here — axe caught 4.41 and 4.23 in CI. Anything on
 * the dashboard that is set in a theme ink goes through this instead of trusting the global value.
 */
export const clayInk = (color: string, mode: PaletteMode): string => readableOn(color, NEU_SURFACE[mode], 4.6);

/**
 * Descendant rules for the dashboard ground: every MUI text surface that paints in a palette ink is
 * repainted in its clay-safe version. Scoped to the dashboard, and two classes deep so it outranks
 * the theme's own single-class rules without `!important`.
 */
export const clayInkOverrides = (palette: {
  mode: PaletteMode;
  primary: { main: string };
  success: { main: string };
  error: { main: string };
  warning: { main: string };
  info: { main: string };
}) => {
  const ink = (c: string) => clayInk(c, palette.mode);
  const rules: Record<string, Record<string, string>> = {
    '& .MuiButton-text.MuiButton-colorPrimary, & .MuiButton-outlined.MuiButton-colorPrimary, & .MuiLink-root, & .MuiIconButton-colorPrimary': {
      color: ink(palette.primary.main),
    },
  };
  for (const tone of ['primary', 'success', 'error', 'warning', 'info'] as const) {
    const cap = tone[0].toUpperCase() + tone.slice(1);
    rules[`& .MuiChip-outlined.MuiChip-color${cap}`] = { color: ink(palette[tone].main), borderColor: ink(palette[tone].main) };
  }
  return rules;
};

/**
 * The keyboard focus ring for anything pressable on the clay. Soft shadows cannot carry focus —
 * a raised key and a focused key look the same — so focus is a solid ring in the body ink, offset
 * clear of the shadow, and it only shows for keyboard focus.
 */
export const neuFocus = {
  '&.Mui-focusVisible, &:focus-visible': {
    outline: '2px solid',
    outlineColor: 'text.primary',
    outlineOffset: 3,
  },
} as const;
