import type { ThemeOptions } from '@mui/material/styles';
import type { PaletteMode } from '@mui/material';

export const radius = {
  xs: 6,
  sm: 8,
  md: 8,
  lg: 8,
  xl: 10,
};

export const shadows = {
  light: {
    sm: '0 1px 2px rgba(15, 23, 42, 0.06)',
    md: '0 14px 35px rgba(15, 23, 42, 0.08), 0 0 0 1px rgba(15, 23, 42, 0.02)',
    lg: '0 24px 70px rgba(15, 23, 42, 0.12), 0 10px 28px rgba(225, 29, 46, 0.08)',
  },
  dark: {
    sm: '0 1px 2px rgba(0, 0, 0, 0.32)',
    md: '0 14px 35px rgba(0, 0, 0, 0.32)',
    lg: '0 24px 70px rgba(0, 0, 0, 0.42)',
  },
};

export const getComponents = (mode: PaletteMode, primaryColor: string): ThemeOptions['components'] => {
  const isDark = mode === 'dark';
  const cardShadow = isDark ? shadows.dark.md : shadows.light.md;
  const gridSurface = isDark ? '#101b2d' : '#ffffff';

  return {
    MuiCssBaseline: {
      styleOverrides: {
        '*': {
          boxSizing: 'border-box',
        },
        html: {
          minWidth: 320,
          minHeight: '100%',
          scrollBehavior: 'smooth',
        },
        body: {
          margin: 0,
          minWidth: 320,
          minHeight: '100vh',
          background: isDark
            ? `radial-gradient(circle at top left, ${primaryColor}29, transparent 32%), #08111f`
            : '#F5F7FB',
          WebkitFontSmoothing: 'antialiased',
          MozOsxFontSmoothing: 'grayscale',
          scrollbarColor: isDark ? '#334155 #08111f' : '#cbd5e1 #f5f7fb',
        },
        '#root': {
          width: '100%',
          minHeight: '100vh',
        },
        '::-webkit-scrollbar': {
          width: 10,
          height: 10,
        },
        '::-webkit-scrollbar-track': {
          backgroundColor: isDark ? '#08111f' : '#f5f7fb',
        },
        '::-webkit-scrollbar-thumb': {
          backgroundColor: isDark ? '#334155' : '#cbd5e1',
          borderRadius: 10,
          border: `2px solid ${isDark ? '#08111f' : '#f5f7fb'}`,
        },
      },
    },
    MuiPaper: {
      defaultProps: {
        elevation: 0,
      },
      styleOverrides: {
        root: {
          backgroundImage: 'none',
          borderRadius: radius.lg,
          border: `1px solid ${isDark ? 'rgba(148,163,184,.14)' : 'rgba(15,23,42,.08)'}`,
          boxShadow: cardShadow,
        },
      },
    },
    MuiCard: {
      defaultProps: {
        elevation: 0,
      },
      styleOverrides: {
        root: {
          borderRadius: radius.lg,
          border: `1px solid ${isDark ? 'rgba(148,163,184,.14)' : 'rgba(15,23,42,.08)'}`,
          boxShadow: cardShadow,
          backgroundImage: 'none',
        },
      },
    },
    MuiButton: {
      defaultProps: {
        disableElevation: true,
      },
      styleOverrides: {
        root: {
          borderRadius: radius.sm,
          minHeight: 38,
          paddingInline: 16,
          transition: 'transform .18s ease, box-shadow .18s ease, background-color .18s ease',
          '&:hover': {
            transform: 'translateY(-1px)',
          },
        },
        contained: {
          '&.MuiButton-containedPrimary': {
            boxShadow: `0 14px 30px ${primaryColor}36`,
            background: `linear-gradient(135deg, ${primaryColor}, #B91C1C)`,
          },
        },
        outlined: {
          borderWidth: 1,
        },
      },
    },
    MuiIconButton: {
      styleOverrides: {
        root: {
          borderRadius: radius.sm,
          transition: 'transform .18s ease, background-color .18s ease',
          '&:hover': {
            transform: 'translateY(-1px)',
          },
        },
      },
    },
    MuiTextField: {
      defaultProps: {
        size: 'small',
      },
    },
    MuiOutlinedInput: {
      styleOverrides: {
        root: {
          borderRadius: radius.sm,
          backgroundColor: isDark ? 'rgba(15, 23, 42, 0.55)' : 'rgba(255, 255, 255, 0.82)',
          transition: 'box-shadow .18s ease, background-color .18s ease',
          '&:hover': {
            backgroundColor: isDark ? 'rgba(15, 23, 42, 0.8)' : '#ffffff',
          },
          '&.Mui-focused': {
            boxShadow: `0 0 0 3px ${primaryColor}1f`,
          },
        },
        notchedOutline: {
          borderColor: isDark ? 'rgba(148,163,184,.18)' : 'rgba(15,23,42,.12)',
        },
      },
    },
    MuiInputLabel: {
      styleOverrides: {
        root: {
          fontWeight: 650,
        },
      },
    },
    MuiChip: {
      styleOverrides: {
        root: {
          borderRadius: radius.xs,
          fontWeight: 750,
        },
      },
    },
    MuiDialog: {
      styleOverrides: {
        paper: {
          borderRadius: radius.xl,
          boxShadow: isDark ? shadows.dark.lg : shadows.light.lg,
        },
      },
    },
    MuiMenu: {
      styleOverrides: {
        paper: {
          borderRadius: radius.md,
          padding: 6,
        },
      },
    },
    MuiTableCell: {
      styleOverrides: {
        root: {
          borderBottomColor: isDark ? 'rgba(148,163,184,.10)' : 'rgba(15,23,42,.07)',
        },
        head: {
          backgroundColor: `${primaryColor} !important`,
          fontSize: 12,
          fontWeight: 850,
          textTransform: 'uppercase',
          letterSpacing: '0.04em',
          color: '#ffffff !important',
          borderBottomColor: `${primaryColor} !important`,
          '& .MuiTypography-root, & .MuiSvgIcon-root': {
            color: '#ffffff !important',
          },
        },
      },
    },
    MuiTableHead: {
      styleOverrides: {
        root: {
          backgroundColor: `${primaryColor} !important`,
          '& .MuiTableCell-head': {
            backgroundColor: `${primaryColor} !important`,
            color: '#ffffff !important',
          },
        },
      },
    },
    MuiDataGrid: {
      styleOverrides: {
        root: {
          border: 0,
          backgroundColor: 'transparent',
          '--DataGrid-rowBorderColor': isDark ? 'rgba(148,163,184,.12)' : 'rgba(15,23,42,.07)',
          '& .MuiDataGrid-columnHeaders': {
            minHeight: '48px !important',
            backgroundColor: `${primaryColor} !important`,
            borderBottom: `1px solid ${primaryColor} !important`,
            color: '#ffffff !important',
          },
          '& .MuiDataGrid-columnHeader, & .MuiDataGrid-topContainer': {
            backgroundColor: `${primaryColor} !important`,
            color: '#ffffff !important',
          },
          '& .MuiDataGrid-main, & .MuiDataGrid-virtualScroller, & .MuiDataGrid-virtualScrollerContent, & .MuiDataGrid-virtualScrollerRenderZone, & .MuiDataGrid-overlayWrapper, & .MuiDataGrid-overlayWrapperInner, & .MuiDataGrid-filler, & .MuiDataGrid-scrollbarFiller': {
            backgroundColor: `${gridSurface} !important`,
          },
          '& .MuiDataGrid-topContainer .MuiDataGrid-filler, & .MuiDataGrid-topContainer .MuiDataGrid-scrollbarFiller': {
            backgroundColor: `${primaryColor} !important`,
          },
          '& .MuiDataGrid-columnHeaderTitle': {
            fontSize: 12,
            fontWeight: 850,
            textTransform: 'uppercase',
            letterSpacing: '0.04em',
            color: '#ffffff !important',
          },
          '& .MuiDataGrid-sortIcon, & .MuiDataGrid-menuIconButton, & .MuiDataGrid-iconButtonContainer, & .MuiDataGrid-columnSeparator, & .MuiDataGrid-columnHeader .MuiSvgIcon-root': {
            color: '#ffffff !important',
            opacity: 0.9,
          },
          '& .MuiDataGrid-columnHeader .MuiIconButton-root, & .MuiDataGrid-menuIconButton, & .MuiDataGrid-sortButton': {
            width: 28,
            height: 28,
            border: '0 !important',
            boxShadow: 'none !important',
            color: '#ffffff !important',
            backgroundColor: 'transparent !important',
            transform: 'none !important',
            '&:hover': {
              backgroundColor: 'rgba(255,255,255,0.14) !important',
            },
            '&:focus, &.Mui-focusVisible': {
              backgroundColor: 'rgba(255,255,255,0.18) !important',
              boxShadow: '0 0 0 2px rgba(255,255,255,0.24) !important',
            },
          },
          '& .MuiDataGrid-overlay': {
            backgroundColor: `${gridSurface} !important`,
            color: isDark ? '#cbd5e1' : '#334155',
          },
          '& .MuiDataGrid-row': {
            transition: 'background-color .16s ease',
            '&:nth-of-type(even)': {
              backgroundColor: isDark ? 'rgba(148,163,184,.025)' : 'rgba(15,23,42,.018)',
            },
            '&:hover': {
              backgroundColor: isDark ? 'rgba(225,29,46,.12)' : 'rgba(225,29,46,.045)',
            },
          },
          '& .MuiDataGrid-cell': {
            borderColor: isDark ? 'rgba(148,163,184,.10)' : 'rgba(15,23,42,.06)',
            outline: 'none !important',
          },
          '& .MuiDataGrid-footerContainer': {
            borderColor: isDark ? 'rgba(148,163,184,.12)' : 'rgba(15,23,42,.07)',
            backgroundColor: isDark ? 'rgba(148,163,184,.035)' : 'rgba(255,255,255,.6)',
          },
          '& .MuiTablePagination-root': {
            color: isDark ? '#cbd5e1' : '#334155',
          },
        },
      },
    },
    MuiSkeleton: {
      styleOverrides: {
        root: {
          borderRadius: radius.sm,
        },
      },
    },
  };
};
