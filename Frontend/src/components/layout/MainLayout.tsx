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
  const isMobile = useMediaQuery(theme.breakpoints.down('sm'));

  const toggleSidebar = () => {
    if (isMobile) setMobileOpen((open) => !open);
    else setCollapsed((value) => !value);
  };

  const currentWidth = collapsed ? collapsedWidth : drawerWidth;
  // On mobile the sidebar is shown/hidden; on desktop it is expanded/collapsed.
  const sidebarExpanded = isMobile ? mobileOpen : !collapsed;

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
          width: { sm: currentWidth }, 
          flexShrink: { sm: 0 },
          transition: (theme) => theme.transitions.create('width', {
            easing: theme.transitions.easing.sharp,
            duration: theme.transitions.duration.enteringScreen,
          }),
        }}
      >
        <Drawer
          variant="temporary"
          open={mobileOpen}
          onClose={() => setMobileOpen(false)}
          ModalProps={{ keepMounted: true }}
          sx={{
            display: { xs: 'block', sm: 'none' },
            '& .MuiDrawer-paper': { boxSizing: 'border-box', width: drawerWidth, bgcolor: 'background.default' },
          }}
        >
          <Toolbar sx={{ px: 2.5, mb: 1 }}><Branding showText fontSize={20} logoSize={32} /></Toolbar>
          <Sidebar collapsed={false} onNavigate={() => setMobileOpen(false)} />
        </Drawer>
        <Drawer
          variant="permanent"
          sx={{
            display: { xs: 'none', sm: 'block' },
            '& .MuiDrawer-paper': { 
              boxSizing: 'border-box', 
              width: currentWidth,
              borderRight: '1px solid',
              borderColor: 'divider',
              backgroundColor: 'background.default',
              transition: (theme) => theme.transitions.create('width', {
                easing: theme.transitions.easing.sharp,
                duration: theme.transitions.duration.enteringScreen,
              }),
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
          width: { sm: `calc(100% - ${currentWidth}px)` },
          minWidth: 0,
          maxWidth: '100%',
          boxSizing: 'border-box',
          overflowX: 'hidden',
          minHeight: '100vh',
          backgroundColor: 'background.default',
          transition: (theme) => theme.transitions.create(['width', 'margin'], {
            easing: theme.transitions.easing.sharp,
            duration: theme.transitions.duration.enteringScreen,
          }),
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
