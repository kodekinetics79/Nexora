import type { PaletteMode } from '@mui/material';

export const brandPalette = {
  red: '#E11D2E',
  darkRed: '#B91C1C',
  navy: '#0F1B2D',
  sidebarDark: '#111827',
  blue: '#1557B0',
  brightBlue: '#2563EB',
  cyan: '#38BDF8',
  teal: '#0f766e',
  green: '#16a34a',
  amber: '#d97706',
  danger: '#DC2626',
  violet: '#7c3aed',
  slate: '#475569',
};

export const getPalette = (mode: PaletteMode, primaryColor: string) => {
  const isDark = mode === 'dark';

  return {
    mode,
    primary: {
      main: primaryColor,
      light: isDark ? '#F87171' : '#EF4444',
      dark: '#B91C1C',
      contrastText: '#ffffff',
    },
    secondary: {
      main: brandPalette.teal,
      light: '#5eead4',
      dark: '#115e59',
      contrastText: '#ffffff',
    },
    success: {
      main: brandPalette.green,
      light: '#dcfce7',
      dark: '#166534',
    },
    warning: {
      main: brandPalette.amber,
      light: '#fef3c7',
      dark: '#92400e',
    },
    error: {
      main: brandPalette.danger,
      light: '#fee2e2',
      dark: '#991b1b',
    },
    info: {
      main: brandPalette.cyan,
      light: '#cffafe',
      dark: '#155e75',
    },
    background: {
      default: isDark ? '#08111f' : '#F5F7FB',
      paper: isDark ? '#101b2d' : '#FFFFFF',
    },
    text: {
      primary: isDark ? '#f8fafc' : '#0f172a',
      secondary: isDark ? '#9aa8bc' : '#64748b',
      disabled: isDark ? '#667085' : '#94a3b8',
    },
    divider: isDark ? 'rgba(148, 163, 184, 0.14)' : '#E5E7EB',
    action: {
      hover: isDark ? 'rgba(148, 163, 184, 0.09)' : 'rgba(15, 23, 42, 0.045)',
      selected: isDark ? 'rgba(225, 29, 46, 0.2)' : 'rgba(225, 29, 46, 0.09)',
      disabledBackground: isDark ? 'rgba(148, 163, 184, 0.08)' : 'rgba(15, 23, 42, 0.06)',
    },
  } as const;
};

export const surface = {
  light: {
    shell: '#eef3f9',
    elevated: 'rgba(255, 255, 255, 0.86)',
    subtle: 'rgba(15, 23, 42, 0.035)',
  },
  dark: {
    shell: '#060d18',
    elevated: 'rgba(16, 27, 45, 0.84)',
    subtle: 'rgba(148, 163, 184, 0.07)',
  },
};
