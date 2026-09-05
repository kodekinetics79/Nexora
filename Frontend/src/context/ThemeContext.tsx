import React, { createContext, useContext, useState, useMemo } from 'react';
import type { ReactNode } from 'react';
import { ThemeProvider, createTheme } from '@mui/material';
import type { PaletteMode } from '@mui/material';
import { alpha } from '@mui/material/styles';
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

    // Tactile depth — owner decision of 2026-09-05 ("3D" actions, a glass shell; see DESIGN.md
    // › Elevation & Depth). Every value derives from the mode and from the chosen brand colour,
    // so all twelve selectable brand colours keep the same finish. Depth is painted as
    // brand-agnostic light/shade overlays rather than lightened/darkened shades, which keeps
    // primary.contrastText's AA ratio intact at every point of the gradient: the lightest stop
    // is a 14% white wash, which on the palest permitted brand colour still clears 4.5:1.
    const isDark = mode === 'dark';
    const glassFill = isDark ? 'rgba(15, 23, 42, 0.58)' : 'rgba(255, 255, 255, 0.58)';
    const glassEdge = isDark ? 'rgba(148, 163, 184, 0.22)' : 'rgba(100, 116, 139, 0.18)';
    const glassHighlight = isDark ? 'rgba(255, 255, 255, 0.06)' : 'rgba(255, 255, 255, 0.85)';
    const glass = {
      backgroundColor: glassFill,
      backgroundImage: 'none',
      backdropFilter: 'blur(18px) saturate(140%)',
      WebkitBackdropFilter: 'blur(18px) saturate(140%)',
      border: '1px solid',
      borderColor: glassEdge,
      boxShadow: `inset 0 1px 0 ${glassHighlight}, 0 1px 2px rgba(8, 23, 42, ${isDark ? 0.5 : 0.06}), 0 14px 36px -20px rgba(8, 23, 42, ${isDark ? 0.85 : 0.3})`,
    };
    // A solid control with a lit top edge, a shaded bottom edge and a lift; the press state
    // drops it by a pixel and swaps the lift for an inner shadow, so the click reads by hand.
    const tactile = {
      backgroundImage: 'linear-gradient(180deg, rgba(255,255,255,0.14) 0%, rgba(255,255,255,0) 52%, rgba(0,0,0,0.14) 100%)',
      boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.28), inset 0 -1px 0 rgba(0,0,0,0.22), 0 1px 2px rgba(8,23,42,0.28)',
    };
    const tactileHover = {
      backgroundImage: 'linear-gradient(180deg, rgba(255,255,255,0.20) 0%, rgba(255,255,255,0.02) 52%, rgba(0,0,0,0.16) 100%)',
      transform: 'translateY(-1px)',
    };
    const tactileActive = {
      backgroundImage: 'linear-gradient(180deg, rgba(0,0,0,0.08) 0%, rgba(0,0,0,0.02) 60%, rgba(255,255,255,0.06) 100%)',
      boxShadow: 'inset 0 2px 4px rgba(0,0,0,0.30), 0 1px 1px rgba(8,23,42,0.18)',
      transform: 'translateY(1px)',
    };
    const reducedMotion = {
      '@media (prefers-reduced-motion: reduce)': {
        transition: 'none',
        '&:hover, &:active': { transform: 'none' },
      },
    };

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
      // MUI's light defaults are designed primarily as filled-control colours,
      // not as small text on white. Nexora also uses these semantic tokens for
      // deadline labels and compact chips, where the stock warning (#ed6c02)
      // and info (#0288d1) colours miss the 4.5:1 text floor.
      warning: {
        main: mode === 'dark' ? '#fdba74' : '#9a3412',
      },
      info: {
        main: mode === 'dark' ? '#38bdf8' : '#0369a1',
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
        // `text.disabled` is also used by this application for explanatory
        // captions (for example "More workspaces" and "No deadline"). MUI's
        // default 38%-black resolves to #9a9b9c on the page — 2.66:1. Keep
        // these captions readable; genuinely disabled native controls remain
        // exposed as disabled independently of their colour.
        disabled: mode === 'dark' ? '#94a3b8' : '#64748b',
      },
      divider: mode === 'dark' ? 'rgba(148, 163, 184, 0.1)' : 'rgba(100, 116, 139, 0.1)',
    },
    typography: {
      fontFamily: '"Source Sans 3", system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
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
            minHeight: 44,
            borderRadius: 8,
            padding: '9px 16px',
            boxShadow: 'none',
            transition: 'transform 120ms ease-out, box-shadow 160ms ease-out, background-color 160ms ease-out, background-image 160ms ease-out',
            '&:hover': {
              boxShadow: 'none',
            },
            '&.Mui-focusVisible': {
              outline: '3px solid',
              outlineColor: primaryBorderOnSurface,
              outlineOffset: 2,
            },
            ...reducedMotion,
          },
        },
        variants: [
          {
            // Every solid button is tactile; the primary variant below adds its brand lift.
            props: { variant: 'contained' },
            style: {
              ...tactile,
              '&:hover': tactileHover,
              '&:active': tactileActive,
              '&.Mui-disabled': { backgroundImage: 'none', boxShadow: 'none', transform: 'none' },
            },
          },
          {
            props: { variant: 'contained', color: 'primary' },
            style: {
              // The solid colour is opaque and clears AA against primary.contrastText; the
              // overlays above only lighten or darken it by a bounded amount.
              backgroundColor: primaryPalette.main,
              color: primaryPalette.contrastText,
              boxShadow: `${tactile.boxShadow}, 0 8px 18px -8px ${alpha(primaryPalette.main, 0.75)}`,
              '&:hover': {
                ...tactileHover,
                backgroundColor: primaryPalette.dark,
                boxShadow: `${tactile.boxShadow}, 0 12px 22px -8px ${alpha(primaryPalette.main, 0.8)}`,
              },
              '&:active': { ...tactileActive, backgroundColor: primaryPalette.dark },
            },
          },
          {
            // Outlined/text buttons render the brand colour *as text* on the
            // page surface, where several brand colours fall under 4.5:1
            // (SC 1.4.3). The border keeps the 3:1 non-text floor (SC 1.4.11).
            // The face is a faint brand-tinted glass so it sits on the canvas as an object.
            props: { variant: 'outlined', color: 'primary' },
            style: {
              color: primaryOnSurface,
              borderColor: primaryBorderOnSurface,
              backgroundColor: alpha(primaryPalette.main, isDark ? 0.10 : 0.05),
              boxShadow: isDark ? 'none' : 'inset 0 1px 0 rgba(255,255,255,0.6)',
              '&:hover': { backgroundColor: alpha(primaryPalette.main, isDark ? 0.16 : 0.10), transform: 'translateY(-1px)' },
              '&:active': { transform: 'translateY(1px)' },
            },
          },
          {
            props: { variant: 'text', color: 'primary' },
            style: { color: primaryOnSurface },
          },
        ],
      },
      MuiTab: {
        styleOverrides: {
          root: {
            '&.Mui-selected': { color: primaryOnSurface },
          },
        },
      },
      MuiLink: {
        styleOverrides: {
          root: { color: primaryOnSurface },
        },
      },
      MuiPaper: {
        styleOverrides: {
          root: {
            backgroundImage: 'none',
            boxShadow: 'none',
            border: '1px solid',
            borderColor: mode === 'dark' ? 'rgba(148, 163, 184, 0.16)' : 'rgba(100, 116, 139, 0.16)',
            // Opt-in glass for the shell and summary tiles. Data surfaces (tables, forms) stay
            // opaque paper: nothing should shimmer behind a figure someone is reading.
            '&.nx-glass': {
              ...glass,
              position: 'relative',
              overflow: 'hidden',
              // A hairline of the two brand colours along the lit top edge: the one crafted
              // detail that tells a glass tile apart from a paper card at a glance.
              '&::before': {
                content: '""',
                position: 'absolute',
                top: 0, left: 0, right: 0, height: 2,
                background: 'linear-gradient(90deg, #075dcc 0%, #20c7b5 100%)',
                opacity: isDark ? 0.85 : 0.7,
              },
            },
          },
        },
      },
      MuiDialog: {
        styleOverrides: {
          // Temporary surfaces float: glass, but opaque enough that the page behind never
          // competes with the form in front.
          paper: {
            ...glass,
            backgroundColor: isDark ? 'rgba(15, 23, 42, 0.88)' : 'rgba(255, 255, 255, 0.90)',
            boxShadow: `inset 0 1px 0 ${glassHighlight}, 0 32px 80px -28px rgba(8, 23, 42, ${isDark ? 0.9 : 0.55})`,
          },
        },
      },
      MuiMenu: {
        styleOverrides: {
          paper: {
            ...glass,
            backgroundColor: isDark ? 'rgba(15, 23, 42, 0.90)' : 'rgba(255, 255, 255, 0.90)',
          },
        },
      },
      MuiPopover: {
        styleOverrides: {
          paper: {
            ...glass,
            backgroundColor: isDark ? 'rgba(15, 23, 42, 0.90)' : 'rgba(255, 255, 255, 0.90)',
          },
        },
      },
      MuiTooltip: {
        styleOverrides: {
          tooltip: {
            backgroundColor: isDark ? 'rgba(30, 41, 59, 0.92)' : 'rgba(8, 23, 42, 0.92)',
            backdropFilter: 'blur(10px)',
            boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.12), 0 8px 24px -12px rgba(8,23,42,0.6)',
            fontSize: 13,
          },
        },
      },
      MuiChip: {
        styleOverrides: {
          filled: {
            boxShadow: isDark ? 'inset 0 1px 0 rgba(255,255,255,0.08)' : 'inset 0 1px 0 rgba(255,255,255,0.45), inset 0 -1px 0 rgba(0,0,0,0.06)',
          },
        },
      },
      MuiCard: {
        styleOverrides: {
          root: {
            borderRadius: 12,
            padding: '16px',
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
            // The canvas has weather: two soft washes of cobalt and teal fixed behind the
            // page, which is what the glass surfaces above it refract. Without them glass is
            // just a lighter grey.
            backgroundColor: isDark ? '#0f172a' : '#f8fafc',
            backgroundImage: isDark
              ? 'radial-gradient(1100px 640px at 6% -12%, rgba(7, 93, 204, 0.34), transparent 62%), radial-gradient(900px 540px at 102% -4%, rgba(32, 199, 181, 0.16), transparent 58%), radial-gradient(800px 500px at 90% 110%, rgba(7, 93, 204, 0.16), transparent 60%)'
              : 'radial-gradient(1100px 640px at 6% -12%, rgba(7, 93, 204, 0.18), transparent 62%), radial-gradient(900px 540px at 102% -4%, rgba(32, 199, 181, 0.16), transparent 58%), radial-gradient(800px 500px at 90% 110%, rgba(7, 93, 204, 0.10), transparent 60%)',
            backgroundAttachment: 'fixed',
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
            'html:focus-within': {
              scrollBehavior: 'auto !important',
            },
            '[data-decorative-motion="true"]': {
              animation: 'none !important',
              transition: 'none !important',
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
