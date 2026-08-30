import React, { useState } from 'react';
import { Box, Drawer, Toolbar, CssBaseline, useMediaQuery, useTheme } from '@mui/material';
import Sidebar from './Sidebar';
import Navbar from './Navbar';
import Branding from '../common/Branding';
import SkipLink, { MAIN_CONTENT_ID } from './SkipLink';
import ImpersonationBanner from './ImpersonationBanner';

const drawerWidth = 280;
const collapsedWidth = 88;

/** Referenced by the Navbar toggle's `aria-controls`. */
export const SIDEBAR_NAV_ID = 'app-sidebar';

interface MainLayoutProps {
  children: React.ReactNode;
}

const MainLayout: React.FC<MainLayoutProps> = ({ children }) => {
  const [collapsed, setCollapsed] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);
  const theme = useTheme();
  // The full rail needs enough room for both the navigation and an operational
  // workspace. Tablets and compact laptops use the same overlay pattern as
  // phones so a 280px rail never consumes half of the working canvas.
  const hasPersistentNavigation = useMediaQuery(theme.breakpoints.up('lg'));

  const toggleSidebar = () => {
    if (!hasPersistentNavigation) setMobileOpen((open) => !open);
    else setCollapsed((value) => !value);
  };

  const currentWidth = collapsed ? collapsedWidth : drawerWidth;
  const sidebarExpanded = hasPersistentNavigation ? !collapsed : mobileOpen;

  return (
    <Box sx={{ display: 'flex' }}>
      <CssBaseline />

      {/* SC 2.4.1 — first tab stop on every authenticated page. */}
      <SkipLink />

      <Navbar
        onToggleSidebar={toggleSidebar}
        drawerWidth={currentWidth}
        sidebarExpanded={sidebarExpanded}
        sidebarId={SIDEBAR_NAV_ID}
      />

      <Box
        component="nav"
        id={SIDEBAR_NAV_ID}
        aria-label="Main"
        sx={{
          width: hasPersistentNavigation ? currentWidth : 0,
          flexShrink: 0,
        }}
      >
        {hasPersistentNavigation ? (
          <Drawer
            variant="permanent"
            sx={{
              '& .MuiDrawer-paper': {
                boxSizing: 'border-box',
                width: currentWidth,
                borderRight: '1px solid',
                borderColor: 'divider',
                backgroundColor: 'background.default',
                overflowX: 'hidden',
              },
            }}
            open
          >
            <Toolbar sx={{ px: collapsed ? 1 : 2.5, display: 'flex', justifyContent: collapsed ? 'center' : 'flex-start', mb: 2 }}>
              <Branding showText={!collapsed} fontSize={20} logoSize={32} />
            </Toolbar>
            <Sidebar collapsed={collapsed} onRequestExpand={() => setCollapsed(false)} />
          </Drawer>
        ) : (
          <Drawer
            variant="temporary"
            open={mobileOpen}
            onClose={() => setMobileOpen(false)}
            ModalProps={{ keepMounted: true }}
            sx={{
              '& .MuiDrawer-paper': {
                boxSizing: 'border-box',
                width: 'min(320px, calc(100vw - 48px))',
                bgcolor: 'background.default',
              },
            }}
          >
            <Toolbar sx={{ px: 2.5, mb: 1 }}><Branding showText fontSize={20} logoSize={32} /></Toolbar>
            <Sidebar collapsed={false} onNavigate={() => setMobileOpen(false)} />
          </Drawer>
        )}
      </Box>

      <Box
        component="main"
        id={MAIN_CONTENT_ID}
        // tabIndex -1 makes the landmark programmatically focusable so the skip
        // link and RouteAnnouncer can move focus here (SC 2.4.1 / SC 2.4.3)
        // without adding it to the natural tab order.
        tabIndex={-1}
        sx={{
          flexGrow: 1,
          '&:focus': { outline: 'none' },
          '&:focus-visible': {
            outline: (theme) => `3px solid ${theme.palette.primary.main}`,
            outlineOffset: -3,
          },
          p: 1.5, 
          width: hasPersistentNavigation ? `calc(100% - ${currentWidth}px)` : '100%',
          minWidth: 0,
          maxWidth: '100%',
          boxSizing: 'border-box',
          overflowX: 'hidden',
          minHeight: '100vh',
          backgroundColor: 'background.default',
        }}
      >
        <Toolbar />
        {children}
      </Box>

      {/* Fixed on every tenant page while a platform impersonation session is
          active; renders nothing otherwise. */}
      <ImpersonationBanner />
    </Box>
  );
};

export default MainLayout;
