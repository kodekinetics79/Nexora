import { createTheme } from '@mui/material/styles';

const theme = createTheme({
  palette: {
    mode: 'light',
    primary: {
      main: '#4682B4', // Steel Blue
    },
    secondary: {
      main: '#dc004e',
    },
    background: {
      default: '#f5f5f5',
      paper: '#ffffff',
    },
  },
  typography: {
    fontFamily: '"Inter", "Roboto", "Helvetica", "Arial", sans-serif',
    h1: { fontWeight: 700 },
    h2: { fontWeight: 700 },
    h3: { fontWeight: 600 },
  },
  components: {
    // Figures in a column have to line up.
    //
    // Nothing in this product used tabular figures — every money column, quantity column and
    // count rendered in proportional digits, so "1,240" and "998" did not align down a column
    // and a reader could not compare magnitudes by eye. This is scoped to the places a number
    // actually lives: right-aligned table cells and right-aligned grid cells, plus an explicit
    // opt-in class. It is deliberately NOT on MuiTypography's root, which would apply it to
    // prose and would be a design change rather than a fix.
    MuiCssBaseline: {
      styleOverrides: {
        '.MuiTableCell-root.MuiTableCell-alignRight, .MuiDataGrid-cell--textRight, .tabular-nums': {
          fontVariantNumeric: 'tabular-nums',
        },
      },
    },
    MuiButton: {
      styleOverrides: {
        root: {
          textTransform: 'none',
          borderRadius: 8,
        },
      },
    },
    MuiPaper: {
      styleOverrides: {
        root: {
          borderRadius: 12,
          boxShadow: '0px 4px 20px rgba(0, 0, 0, 0.05)',
        },
      },
    },
  },
});

export default theme;
