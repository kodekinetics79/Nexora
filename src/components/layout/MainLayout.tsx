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
  const theme = useTheme();
  const isMobile = useMediaQuery(theme.breakpoints.down('sm'));
  const [collapsed, setCollapsed] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);

  const toggleSidebar = () => {
    if (isMobile) {
      setMobileOpen((value) => !value);
    } else {
      setCollapsed(!collapsed);
    }
  };

  const currentWidth = collapsed ? collapsedWidth : drawerWidth;

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh', bgcolor: 'background.default' }}>
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
          sx={{
            display: { xs: 'block', sm: 'none' },
            '& .MuiDrawer-paper': {
              boxSizing: 'border-box',
              width: drawerWidth,
              borderRight: 0,
              p: 1,
              backgroundColor: '#0F1B2D',
              backgroundImage: 'linear-gradient(180deg, #0A1424 0%, #0F1B2D 48%, #111827 100%)',
              color: '#E5E7EB',
            },
          }}
          ModalProps={{ keepMounted: true }}
        >
          <Toolbar sx={{ px: 2, display: 'flex', justifyContent: 'flex-start', mb: 1 }}>
            <Branding showText fontSize={20} logoSize={32} inverse />
          </Toolbar>
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
              borderColor: 'rgba(255,255,255,0.08)',
              backgroundColor: '#0F1B2D',
              backgroundImage: 'linear-gradient(180deg, #0A1424 0%, #0F1B2D 48%, #111827 100%)',
              color: '#E5E7EB',
              transition: (theme) => theme.transitions.create('width', {
                easing: theme.transitions.easing.sharp,
                duration: theme.transitions.duration.enteringScreen,
              }),
              overflowX: 'hidden',
              p: 1,
            },
          }}
          open
        >
          <Toolbar sx={{ px: collapsed ? 1 : 2, display: 'flex', justifyContent: collapsed ? 'center' : 'flex-start', mb: 1 }}>
            <Branding showText={!collapsed} fontSize={20} logoSize={32} inverse />
          </Toolbar>
          <Sidebar collapsed={collapsed} />
        </Drawer>
      </Box>

      <Box
        component="main"
        sx={{ 
          flexGrow: 1, 
          px: { xs: 1.5, md: 2.5 },
          pb: { xs: 2, md: 3 },
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
        <Box
          sx={{
            maxWidth: 1680,
            mx: 'auto',
            py: { xs: 1.5, md: 2.5 },
            animation: 'pageFade .24s ease both',
            '@keyframes pageFade': {
              from: { opacity: 0, transform: 'translateY(6px)' },
              to: { opacity: 1, transform: 'translateY(0)' },
            },
          }}
        >
          {children}
        </Box>
      </Box>
    </Box>
  );
};

export default MainLayout;
