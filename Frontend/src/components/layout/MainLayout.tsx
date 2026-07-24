import React, { useState } from 'react';
import { Box, Drawer, Toolbar, CssBaseline, useMediaQuery, useTheme } from '@mui/material';
import Sidebar from './Sidebar';
import Navbar from './Navbar';
import Branding from '../common/Branding';

const drawerWidth = 280;
const collapsedWidth = 88;

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

  return (
    <Box sx={{ display: 'flex' }}>
      <CssBaseline />
      
      <Navbar onToggleSidebar={toggleSidebar} drawerWidth={currentWidth} />

      <Box
        component="nav"
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
          <Sidebar collapsed={collapsed} />
        </Drawer>
      </Box>

      <Box
        component="main"
        sx={{ 
          flexGrow: 1, 
          p: 1.5, 
          width: { sm: `calc(100% - ${currentWidth}px)` },
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
    </Box>
  );
};

export default MainLayout;
