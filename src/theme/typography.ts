import type { ThemeOptions } from '@mui/material/styles';

export const typography: ThemeOptions['typography'] = {
  fontFamily: '"Inter", "Outfit", "Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif',
  h1: { fontWeight: 800, letterSpacing: 0, lineHeight: 1.05 },
  h2: { fontWeight: 800, letterSpacing: 0, lineHeight: 1.1 },
  h3: { fontWeight: 800, letterSpacing: 0, lineHeight: 1.15 },
  h4: { fontWeight: 800, letterSpacing: 0, lineHeight: 1.2 },
  h5: { fontWeight: 750, letterSpacing: 0, lineHeight: 1.25 },
  h6: { fontWeight: 750, letterSpacing: 0, lineHeight: 1.3 },
  subtitle1: { fontWeight: 700, letterSpacing: 0 },
  subtitle2: { fontWeight: 700, letterSpacing: 0 },
  body1: { letterSpacing: 0, lineHeight: 1.65 },
  body2: { letterSpacing: 0, lineHeight: 1.55 },
  button: { textTransform: 'none', fontWeight: 750, letterSpacing: 0 },
  overline: { fontWeight: 800, letterSpacing: '0.08em' },
};
