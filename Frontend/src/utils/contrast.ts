import { darken, getContrastRatio, getLuminance, lighten } from '@mui/material/styles';

/**
 * WCAG 2.1 contrast helpers (SC 1.4.3 Contrast (Minimum) and SC 1.4.11
 * Non-text Contrast).
 *
 * The brand colour is user-selectable (12 options in the Navbar), so the
 * palette cannot hardcode a foreground colour: white text is unreadable on the
 * lighter brand colours (e.g. "Professional Green" #16a34a gives 3.29:1, well
 * under the 4.5:1 AA floor). These helpers derive the foreground and the
 * light/dark shades from the chosen colour's measured luminance instead.
 */

/** AA minimum for normal-size body text (WCAG 2.1 SC 1.4.3). */
export const AA_TEXT_CONTRAST = 4.5;

/** AA minimum for UI components, borders and graphical objects (SC 1.4.11). */
export const AA_NON_TEXT_CONTRAST = 3;

const LIGHT_FOREGROUND = '#ffffff';
const DARK_FOREGROUND = '#000000';

/**
 * Pick the foreground (white or black) that is actually readable on
 * `background`. Prefers white so the UI keeps its intended look, and only
 * falls back to black when white cannot reach the requested ratio.
 */
export const contrastTextFor = (
  background: string,
  minimumRatio: number = AA_TEXT_CONTRAST,
): string => {
  const lightRatio = getContrastRatio(background, LIGHT_FOREGROUND);
  if (lightRatio >= minimumRatio) return LIGHT_FOREGROUND;

  const darkRatio = getContrastRatio(background, DARK_FOREGROUND);
  if (darkRatio >= minimumRatio) return DARK_FOREGROUND;

  // Neither reaches the target (very mid-tone background) — use the better one.
  return lightRatio >= darkRatio ? LIGHT_FOREGROUND : DARK_FOREGROUND;
};

const SHADE_STEPS = [0.2, 0.15, 0.1, 0.05] as const;

/**
 * Derive a lighter/darker shade of `base` that still keeps `foreground`
 * readable on it. Walks the shade amount down until the ratio clears
 * `minimumRatio`, so hover/active states never silently drop below AA.
 *
 * Replaces the previous `${color}aa` / `${color}ee` string concatenation, which
 * produced 8-digit alpha hexes rather than real shades (an alpha "dark" shade
 * composited over a white page is *lighter* than the base colour, not darker).
 */
export const readableShade = (
  base: string,
  direction: 'light' | 'dark',
  foreground: string,
  minimumRatio: number = AA_TEXT_CONTRAST,
): string => {
  for (const amount of SHADE_STEPS) {
    const shade = direction === 'dark' ? darken(base, amount) : lighten(base, amount);
    if (getContrastRatio(shade, foreground) >= minimumRatio) return shade;
  }
  return base;
};

/**
 * Nudge `color` until it is readable *as text on* `background`, moving away
 * from the background's luminance (darker on light surfaces, lighter on dark).
 *
 * The brand colour doubles as a text colour for outlined/text buttons and
 * accents. Several of the 12 options fail badly in that role — e.g. the default
 * Steel Blue #4682b4 is 3.92:1 on the light page background, and Executive Navy
 * #1e3a8a is 1.40:1 on the dark-mode paper surface.
 */
export const readableOn = (
  color: string,
  background: string,
  minimumRatio: number = AA_TEXT_CONTRAST,
): string => {
  if (getContrastRatio(color, background) >= minimumRatio) return color;

  const backgroundIsLighter = getLuminance(background) > getLuminance(color);
  for (let amount = 0.05; amount <= 0.95; amount += 0.05) {
    const candidate = backgroundIsLighter ? darken(color, amount) : lighten(color, amount);
    if (getContrastRatio(candidate, background) >= minimumRatio) return candidate;
  }
  return backgroundIsLighter ? '#000000' : '#ffffff';
};

export interface AccessiblePaletteColor {
  main: string;
  light: string;
  dark: string;
  contrastText: string;
}

/**
 * Build an AA-compliant palette colour from a single brand colour.
 *
 * - `contrastText` clears 4.5:1 against `main`.
 * - `dark` (used for contained-button hover) also clears 4.5:1 against
 *   `contrastText`.
 * - `light` (used for borders, chips and icon backgrounds) clears the 3:1
 *   non-text floor.
 */
export const buildAccessiblePaletteColor = (main: string): AccessiblePaletteColor => {
  const contrastText = contrastTextFor(main);
  return {
    main,
    light: readableShade(main, 'light', contrastText, AA_NON_TEXT_CONTRAST),
    dark: readableShade(main, 'dark', contrastText, AA_TEXT_CONTRAST),
    contrastText,
  };
};
