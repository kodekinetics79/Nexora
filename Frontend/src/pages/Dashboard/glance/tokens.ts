import type { PaletteMode } from '@mui/material';
import { useTheme } from '@mui/material/styles';
import { readableOn } from '../../../utils/contrast';

/**
 * The glance screen's series palette.
 *
 * These five colours passed a CVD/contrast validation pass (ΔE 11.2 under protanopia and
 * deuteranopia, every value ≥3:1 against the light surface), so they are fixed values rather than
 * anything derived from the selectable brand colour: a reader who cannot separate two marks cannot
 * read the chart at all, and that must not depend on which brass someone picked in settings.
 *
 * The meanings are load-bearing and the bands rely on them being the same everywhere:
 *   brassMark   still yours to act on, or newly won
 *   brassBrand  rims, glints, seals — chrome, never a value
 *   graphite    settled, or plain volume
 *   oxide       late, lost, or never found out
 *   muted       a secondary series that must not compete
 * Brass never means merely "large". Text wears text tokens, never a series colour.
 *
 * They are published as CSS custom properties, the way `nx-glass` is published as a class: the
 * inline SVG the funnel and the sparklines are hand-rolled from can then paint straight from
 * `var(--nx-series-...)` and follow the theme without threading the mode through every prop. The
 * typed accessors exist for the places a literal is genuinely required — recharts props that are
 * read as values rather than painted as CSS, and any colour we interpolate.
 */
export type SeriesToken = 'brassMark' | 'brassBrand' | 'graphite' | 'oxide' | 'muted';

export const SERIES_PALETTE: Readonly<Record<SeriesToken, Readonly<Record<PaletteMode, string>>>> = Object.freeze({
  brassMark: Object.freeze({ light: '#9A6F12', dark: '#D9AE55' }),
  brassBrand: Object.freeze({ light: '#C9931A', dark: '#E3BE71' }),
  graphite: Object.freeze({ light: '#30363D', dark: '#8E99A5' }),
  oxide: Object.freeze({ light: '#A33D2B', dark: '#DE7C67' }),
  muted: Object.freeze({ light: '#68727E', dark: '#7E8894' }),
});

export const SERIES_VAR: Readonly<Record<SeriesToken, string>> = Object.freeze({
  brassMark: '--nx-series-brass-mark',
  brassBrand: '--nx-series-brass-brand',
  graphite: '--nx-series-graphite',
  oxide: '--nx-series-oxide',
  muted: '--nx-series-muted',
});

const SERIES_TOKENS = Object.keys(SERIES_PALETTE) as SeriesToken[];

/** The literal hex for a token in a known mode. */
export const seriesColor = (token: SeriesToken, mode: PaletteMode): string => SERIES_PALETTE[token][mode];

/** `var(--nx-series-…)`, for SVG attributes and sx values that should follow the theme by themselves. */
export const seriesVar = (token: SeriesToken): string => `var(${SERIES_VAR[token]})`;

/**
 * The custom-property block for one mode, shaped for a `GlobalStyles`/`MuiCssBaseline` `:root`
 * entry. Kept as data rather than a component so the theme could adopt it later without a second
 * definition of the palette existing anywhere.
 */
export const glanceCssVariables = (mode: PaletteMode): Record<string, string> => {
  const vars: Record<string, string> = {};
  for (const token of SERIES_TOKENS) vars[SERIES_VAR[token]] = SERIES_PALETTE[token][mode];
  // The seal's tinted ground and the band hairline are chrome derived from the brand brass, so
  // they belong with the series vars rather than in each band's sx: every seal on the screen has
  // to be the same ground or "filled vs outlined" stops being legible before it is read.
  vars['--nx-glance-seal-ground'] = mode === 'dark' ? 'rgba(227, 190, 113, 0.18)' : 'rgba(201, 147, 26, 0.14)';
  vars['--nx-glance-seal-rim'] = mode === 'dark' ? 'rgba(227, 190, 113, 0.55)' : 'rgba(201, 147, 26, 0.55)';
  // The seal is set in brass, and 12px brass has to clear AA as text — the series values are
  // validated for marks (3:1), not for type. Derived the same way ThemeContext derives its brand
  // ink, against the worst-case surface glass composites to, and aiming slightly above 4.5 because
  // the glass shell lands near-white rather than on white.
  vars['--nx-glance-seal-ink'] = readableOn(SERIES_PALETTE.brassBrand[mode], mode === 'dark' ? '#1b1f26' : '#ffffff', 4.9);
  return vars;
};

/** The five literals for the mode currently rendering. */
export const useSeriesColors = (): Record<SeriesToken, string> => {
  const { palette } = useTheme();
  const mode = palette.mode;
  return SERIES_TOKENS.reduce((acc, token) => {
    acc[token] = SERIES_PALETTE[token][mode];
    return acc;
  }, {} as Record<SeriesToken, string>);
};
