import type { ReactNode } from 'react';
import { Link as RouterLink, useLocation } from 'react-router-dom';
import { Box, Stack, Tab, Tabs, Typography } from '@mui/material';
import { confirmLeavingUnsavedWork } from '../../hooks/unsavedWorkRegistry';

const screens = [
  { label: 'Products', path: '/inventory/products' },
  { label: 'Incoming & receipts', path: '/inventory/incoming' },
  { label: 'Stock activity', path: '/inventory/movements' },
  { label: 'Traceability', path: '/inventory/lots' },
];

export default function ProductsWorkspaceNav() {
  const { pathname } = useLocation();
  const current = screens.find((screen) => pathname.startsWith(screen.path))?.path
    ?? '/inventory/products';

  return (
    <Box sx={{ mb: 2, overflowX: 'auto', borderBottom: '1px solid', borderColor: 'divider' }}>
      <Tabs
        value={current}
        variant="scrollable"
        scrollButtons="auto"
        allowScrollButtonsMobile
        aria-label="Product workspace screens"
      >
        {screens.map((screen) => (
          <Tab
            key={screen.path}
            component={RouterLink}
            to={screen.path}
            value={screen.path}
            label={screen.label}
            onClick={(event) => {
              if (!confirmLeavingUnsavedWork()) event.preventDefault();
            }}
            sx={{ minHeight: 44, px: 1.5, mr: 1, textTransform: 'none', fontWeight: 600 }}
          />
        ))}
      </Tabs>
    </Box>
  );
}

interface ProductsWorkspaceShellProps {
  title: string;
  subtitle?: string;
  count?: number;
  actions?: ReactNode;
  children: ReactNode;
  bottomPadding?: number;
}

/** One header, one tab strip and one content rhythm for every Products sub-feature. */
export function ProductsWorkspaceShell({
  title,
  subtitle,
  count,
  actions,
  children,
  bottomPadding = 3,
}: ProductsWorkspaceShellProps) {
  return (
    <Box sx={{ width: '100%', mx: 'auto', p: { xs: 1, sm: 2, md: 3 }, pb: bottomPadding, minWidth: 0, bgcolor: 'background.paper', minHeight: 'calc(100dvh - 88px)',
      '& > .MuiStack-root .MuiButton-root': { borderRadius: '4px', fontSize: '0.875rem', boxShadow: 'none', backgroundImage: 'none', transform: 'none' },
    }}>
      <Stack
        direction={{ xs: 'column', md: 'row' }}
        spacing={1.5}
        sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', md: 'center' }, mb: 1.5 }}
      >
        <Box sx={{ minWidth: 0 }}>
          <Stack direction="row" spacing={2} sx={{ alignItems: 'baseline' }}>
            <Typography component="h1" variant="h5" sx={{ fontWeight: 700, fontSize: { xs: 26, sm: 32 }, lineHeight: 1.25 }}>{title}</Typography>
            {count !== undefined && <Typography variant="body2" color="text.secondary">{count.toLocaleString()} {count === 1 ? 'product' : 'products'}</Typography>}
          </Stack>
          {subtitle && <Typography variant="body2" color="text.secondary">{subtitle}</Typography>}
        </Box>
        {actions}
      </Stack>
      <ProductsWorkspaceNav />
      {children}
    </Box>
  );
}

export function ProductsWorkspaceToolbar({ children }: { children: ReactNode }) {
  return (
    <Box
      sx={{
        mb: 1.5,
        display: 'flex',
        flexWrap: 'wrap',
        gap: 1.5,
        alignItems: 'center',
        '&& .MuiOutlinedInput-root': { borderRadius: '4px', backgroundColor: 'background.paper', minHeight: 44 },
      }}
    >
      {children}
    </Box>
  );
}
