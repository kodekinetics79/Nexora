import React, { createContext, useContext, useState, useMemo } from 'react';
import type { ReactNode } from 'react';
import { ThemeProvider, createTheme } from '@mui/material';
import type { PaletteMode } from '@mui/material';

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

  const theme = useMemo(() => createTheme({
    palette: {
      mode,
      primary: {
        main: primaryColor,
        light: `${primaryColor}aa`,
        dark: `${primaryColor}ee`,
        contrastText: '#ffffff',
      },
      secondary: {
        main: '#0ea5e9', // Sky Blue
      },
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
              boxShadow: `0 4px 12px ${primaryColor}33`,
            },
          },
        },
        variants: [
          {
            props: { variant: 'contained', color: 'primary' },
            style: {
              background: `linear-gradient(135deg, ${primaryColor} 0%, ${primaryColor}dd 100%)`,
            },
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
        },
      },
    },
  }), [mode, primaryColor]);

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
