import { createTheme, type ThemeOptions } from '@mui/material/styles';
import type { PaletteMode } from '@mui/material';
import type {} from '@mui/x-data-grid/themeAugmentation';
import { getPalette } from './palette';
import { getComponents } from './components';
import { typography } from './typography';

export const createAppTheme = (mode: PaletteMode, primaryColor: string) => {
  const options: ThemeOptions = {
    palette: getPalette(mode, primaryColor),
    typography,
    shape: {
      borderRadius: 10,
    },
    spacing: 8,
    components: getComponents(mode, primaryColor),
  };

  return createTheme(options);
};
