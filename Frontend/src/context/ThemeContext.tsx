import React, { createContext, useContext, useState, useMemo } from 'react';
import type { ReactNode } from 'react';
import { ThemeProvider, createTheme } from '@mui/material';
import { alpha } from '@mui/material/styles';
import type { PaletteMode } from '@mui/material';
import {
  AA_NON_TEXT_CONTRAST,
  AA_TEXT_CONTRAST,
  buildAccessiblePaletteColor,
  readableOn,
} from '../utils/contrast';

interface ThemeContextType {
  mode: PaletteMode;
  setMode: (mode: PaletteMode) => void;
  primaryColor: string;
  setPrimaryColor: (color: string) => void;
}

const ThemeContext = createContext<ThemeContextType | undefined>(undefined);

export const ThemeContextProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  const [mode, setModeState] = useState<PaletteMode>(() => {
    return (localStorage.getItem('themeMode') as PaletteMode) || 'light';
  });
  const [primaryColor, setPrimaryColorState] = useState(() => {
    return localStorage.getItem('primaryColor') || '#4682B4';
  });

  const setPrimaryColor = (color: string) => {
    setPrimaryColorState(color);
    localStorage.setItem('primaryColor', color);
  };

  const setMode = (newMode: PaletteMode) => {
    setModeState(newMode);
    localStorage.setItem('themeMode', newMode);
  };

  const theme = useMemo(() => {
    const primaryPalette = buildAccessiblePaletteColor(primaryColor);

    // Worst-case surface the brand colour is drawn *on* as text: in light mode
    // the off-white page background, in dark mode the lighter paper surface.
    const worstCaseSurface = mode === 'dark' ? '#1e293b' : '#f8fafc';
    // Text/outlined buttons paint `primary.main` as their label. Several brand
    // colours fail AA in that role (Steel Blue 3.92:1 on the light page;
    // Executive Navy 1.40:1 on dark paper), so derive a readable variant.
    const primaryOnSurface = readableOn(primaryColor, worstCaseSurface, AA_TEXT_CONTRAST);
    const primaryBorderOnSurface = readableOn(primaryColor, worstCaseSurface, AA_NON_TEXT_CONTRAST);

    return createTheme({
    palette: {
      mode,
      // WCAG 2.1 SC 1.4.3. The brand colour is user-selectable (12 options in
      // the Navbar), so the foreground has to be derived from the chosen
      // colour's luminance rather than hardcoded: white on "Professional
      // Green" (#16a34a) is only 3.29:1, and white on the default "Steel Blue"
      // (#4682b4) is 4.10:1 — both below the 4.5:1 AA floor. The old
      // `${primaryColor}aa` / `${primaryColor}ee` light/dark values were alpha
      // hexes, not real shades (the "dark" one composited *lighter* than main
      // over a white page).
      primary: primaryPalette,
      secondary: {
        main: '#0ea5e9', // Sky Blue
      },
      // Make MUI's own contrastText derivation (secondary/error/warning/...)
      // target AA body text instead of its 3:1 default.
      contrastThreshold: AA_TEXT_CONTRAST,
      background: {
        default: mode === 'dark' ? '#0f172a' : '#f8fafc',
        paper: mode === 'dark' ? '#1e293b' : '#ffffff',
      },
      text: {
        primary: mode === 'dark' ? '#f1f5f9' : '#0f172a',
        secondary: mode === 'dark' ? '#94a3b8' : '#64748b',
      },
      divider: mode === 'dark' ? 'rgba(148, 163, 184, 0.1)' : 'rgba(100, 116, 139, 0.1)',
    },
    typography: {
      fontFamily: '"Outfit", "Inter", sans-serif',
      h1: { fontWeight: 800, letterSpacing: '-0.02em' },
      h2: { fontWeight: 800, letterSpacing: '-0.02em' },
      h3: { fontWeight: 800, letterSpacing: '-0.02em' },
      h4: { fontWeight: 700, letterSpacing: '-0.01em' },
      h5: { fontWeight: 700 },
      h6: { fontWeight: 700 },
      button: { textTransform: 'none', fontWeight: 600 },
    },
    shape: {
      borderRadius: 12,
    },
    components: {
      MuiButton: {
        styleOverrides: {
          root: {
            borderRadius: 10,
            padding: '8px 16px',
            boxShadow: 'none',
            '&:hover': {
              boxShadow: `0 4px 12px ${alpha(primaryColor, 0.2)}`,
            },
          },
        },
        variants: [
          {
            props: { variant: 'contained', color: 'primary' },
            style: {
              // The gradient used to fade to `${primaryColor}dd` — a 87%-alpha
              // stop that composites *lighter* over the page, so the right-hand
              // side of every primary CTA sat below 4.5:1 against its label.
              // Both stops are now opaque shades that clear AA against
              // primary.contrastText.
              background: `linear-gradient(135deg, ${primaryPalette.main} 0%, ${primaryPalette.dark} 100%)`,
              color: primaryPalette.contrastText,
            },
          },
          {
            // Outlined/text buttons render the brand colour *as text* on the
            // page surface, where several brand colours fall under 4.5:1
            // (SC 1.4.3). The border keeps the 3:1 non-text floor (SC 1.4.11).
            props: { variant: 'outlined', color: 'primary' },
            style: {
              color: primaryOnSurface,
              borderColor: primaryBorderOnSurface,
            },
          },
          {
            props: { variant: 'text', color: 'primary' },
            style: { color: primaryOnSurface },
          },
        ],
      },
      MuiPaper: {
        styleOverrides: {
          root: {
            backgroundImage: 'none',
            boxShadow: mode === 'dark' 
              ? '0 4px 6px -1px rgba(0, 0, 0, 0.2), 0 2px 4px -1px rgba(0, 0, 0, 0.1)' 
              : '0 4px 6px -1px rgba(0, 0, 0, 0.05), 0 2px 4px -1px rgba(0, 0, 0, 0.02)',
            border: '1px solid',
            borderColor: mode === 'dark' ? 'rgba(255, 255, 255, 0.05)' : 'rgba(0, 0, 0, 0.05)',
          },
        },
      },
      MuiCard: {
        styleOverrides: {
          root: {
            borderRadius: 16,
            padding: '24px',
          },
        },
      },
      MuiTextField: {
        styleOverrides: {
          root: {
            '& .MuiOutlinedInput-root': {
              borderRadius: 10,
              backgroundColor: mode === 'dark' ? 'rgba(255, 255, 255, 0.02)' : 'rgba(0, 0, 0, 0.01)',
              '&:hover .MuiOutlinedInput-notchedOutline': {
                borderColor: primaryColor,
              },
            },
          },
        },
      },
      MuiCssBaseline: {
        styleOverrides: {
          // Figures in a column have to line up.
          //
          // Nothing in this product used tabular figures — every money column, quantity column
          // and count rendered in proportional digits, so "1,240" and "998" did not align down a
          // column and a reader could not compare magnitudes by eye. Scoped to the places a
          // number actually lives: right-aligned table cells and right-aligned grid cells, plus
          // an explicit opt-in class. Deliberately NOT on MuiTypography's root, which would apply
          // it to prose and would be a design change rather than a fix.
          //
          // It lives HERE, and not in `src/theme.ts`, because src/theme.ts is imported by
          // nothing: main.tsx mounts ThemeContextProvider, and the theme it renders is the
          // createTheme call in this file. An override written into theme.ts compiles, reads
          // correctly, and never loads.
          '.MuiTableCell-root.MuiTableCell-alignRight, .MuiDataGrid-cell--textRight, .tabular-nums': {
            fontVariantNumeric: 'tabular-nums',
          },
          body: {
            scrollbarColor: mode === 'dark' ? '#334155 #0f172a' : '#cbd5e1 #f8fafc',
            '&::-webkit-scrollbar, & *::-webkit-scrollbar': {
              width: '8px',
              height: '8px',
            },
            '&::-webkit-scrollbar-track, & *::-webkit-scrollbar-track': {
              backgroundColor: mode === 'dark' ? 'rgba(15, 23, 42, 0.5)' : 'rgba(248, 250, 252, 0.5)',
            },
            '&::-webkit-scrollbar-thumb, & *::-webkit-scrollbar-thumb': {
              borderRadius: '8px',
              backgroundColor: mode === 'dark' ? '#334155' : '#cbd5e1',
              border: '2px solid',
              borderColor: mode === 'dark' ? '#0f172a' : '#f8fafc',
            },
            '&::-webkit-scrollbar-thumb:hover, & *::-webkit-scrollbar-thumb:hover': {
              backgroundColor: mode === 'dark' ? '#475569' : '#94a3b8',
            },
          },
          // SC 2.2.2 / SC 2.3.3 mitigation — several surfaces (notably the
          // login page) run infinite decorative animations. Honour the OS
          // "reduce motion" setting globally.
          '@media (prefers-reduced-motion: reduce)': {
            '*, *::before, *::after': {
              animationDuration: '0.01ms !important',
              animationIterationCount: '1 !important',
              transitionDuration: '0.01ms !important',
              scrollBehavior: 'auto !important',
            },
          },
        },
      },
    },
    });
  }, [mode, primaryColor]);

  return (
    <ThemeContext.Provider value={{ mode, setMode, primaryColor, setPrimaryColor }}>
      <ThemeProvider theme={theme}>
        {children}
      </ThemeProvider>
    </ThemeContext.Provider>
  );
};

export const useAppTheme = () => {
  const context = useContext(ThemeContext);
  if (!context) throw new Error('useAppTheme must be used within ThemeContextProvider');
  return context;
};
