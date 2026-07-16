import type { ComponentProps } from 'react';
import { Stack as MuiStack } from '@mui/material';
import type { SxProps, Theme } from '@mui/material';

type ResponsiveString = string | Partial<Record<'xs' | 'sm' | 'md' | 'lg' | 'xl', string>>;

// This MUI build's `Stack` does not accept `alignItems` / `justifyContent` as
// top-level props (they must live in `sx`). This thin wrapper restores the
// ergonomic prop form used across the console by folding them into `sx`, so
// pages can write `<Stack alignItems="center" justifyContent="space-between">`.
type FlexProps = ComponentProps<typeof MuiStack> & {
  alignItems?: ResponsiveString;
  justifyContent?: ResponsiveString;
};

export default function Flex({ alignItems, justifyContent, sx, ...rest }: FlexProps) {
  const merged: SxProps<Theme> = {
    ...(alignItems !== undefined ? { alignItems } : {}),
    ...(justifyContent !== undefined ? { justifyContent } : {}),
    ...((sx as object) ?? {}),
  } as SxProps<Theme>;
  return <MuiStack sx={merged} {...rest} />;
}
