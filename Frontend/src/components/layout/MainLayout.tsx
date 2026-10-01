import React, { useEffect, useRef, useState } from 'react';
import { Box, Drawer, Toolbar, CssBaseline, useMediaQuery, useTheme } from '@mui/material';
import { useLocation } from 'react-router-dom';
import Sidebar from './Sidebar';
import Navbar from './Navbar';
import Branding from '../common/Branding';
import SkipLink, { MAIN_CONTENT_ID } from './SkipLink';
import ImpersonationBanner from './ImpersonationBanner';

const drawerWidth = 260;
const collapsedWidth = 72;
/** Owner ruling 2026-09-27: the menu starts closed and opens while the pointer rests on it. */
const PINNED_KEY = 'nexora.nav.pinned';
const OPEN_DELAY_MS = 250;
const CLOSE_DELAY_MS = 350;

const readPinned = (): boolean => {
  try { return window.localStorage.getItem(PINNED_KEY) === 'true'; } catch { return false; }
};
const writePinned = (value: boolean) => {
  try { window.localStorage.setItem(PINNED_KEY, String(value)); } catch { /* the choice just is not remembered */ }
};

/** Referenced by the Navbar toggle's `aria-controls`. */
export const SIDEBAR_NAV_ID = 'app-sidebar';

interface MainLayoutProps {
  children: React.ReactNode;
}

const MainLayout: React.FC<MainLayoutProps> = ({ children }) => {
  const location = useLocation();
  const [pinned, setPinned] = useState(readPinned);
  const [peek, setPeek] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);
  const timer = useRef<number | undefined>(undefined);
  const theme = useTheme();
  // Hover opening is for a mouse; a touch screen opens the menu with the toggle only.
  const canHover = useMediaQuery('(hover: hover) and (pointer: fine)', { noSsr: true });
  // The full rail needs enough room for both the navigation and an operational
  // workspace. Tablets and compact laptops use the same overlay pattern as
  // phones so a 280px rail never consumes half of the working canvas.
  const hasPersistentNavigation = useMediaQuery(theme.breakpoints.up('lg'), { noSsr: true });

  const clearTimer = () => window.clearTimeout(timer.current);
  useEffect(() => clearTimer, []);

  const toggleSidebar = () => {
    if (!hasPersistentNavigation) { setMobileOpen((open) => !open); return; }
    clearTimer();
    setPeek(false);
    setPinned((value) => { writePinned(!value); return !value; });
    // Pages that measure their own height (the RFQ lines box) re-measure after the width changes.
    window.setTimeout(() => window.dispatchEvent(new Event('resize')), 260);
  };

  const openSoon = () => {
    if (pinned || !canHover) return;
    clearTimer();
    timer.current = window.setTimeout(() => setPeek(true), OPEN_DELAY_MS);
  };
  const closeSoon = () => {
    clearTimer();
    if (peek) timer.current = window.setTimeout(() => setPeek(false), CLOSE_DELAY_MS);
  };

  const expanded = pinned || peek;
  const collapsed = !expanded;
  // The page keeps the closed width while the menu is only peeking; the open menu floats over it.
  const reservedWidth = pinned ? drawerWidth : collapsedWidth;
  const paperWidth = expanded ? drawerWidth : collapsedWidth;
  const sidebarExpanded = hasPersistentNavigation ? pinned : mobileOpen;

  return (
    <Box sx={{ display: 'flex' }}>
      <CssBaseline />

      {/* SC 2.4.1 — first tab stop on every authenticated page. */}
      <SkipLink />

      <Navbar
        onToggleSidebar={toggleSidebar}
        drawerWidth={reservedWidth}
        sidebarExpanded={sidebarExpanded}
        sidebarId={SIDEBAR_NAV_ID}
      />

      {/* The pointer, focus and Escape handlers only open and close the menu; every control inside
          is a real button, so the landmark itself needs no role. */}
      {/* eslint-disable-next-line jsx-a11y/no-static-element-interactions */}
      <Box
        component="nav"
        id={SIDEBAR_NAV_ID}
        aria-label="Main"
        sx={{
          width: hasPersistentNavigation ? reservedWidth : 0,
          flexShrink: 0,
        }}
        onMouseEnter={openSoon}
        onMouseLeave={closeSoon}
        onFocus={() => { if (!pinned) { clearTimer(); setPeek(true); } }}
        onBlur={(event: React.FocusEvent<HTMLElement>) => {
          if (!event.currentTarget.contains(event.relatedTarget as Node | null)) closeSoon();
        }}
        onKeyDown={(event: React.KeyboardEvent) => {
          if (event.key === 'Escape' && peek) { clearTimer(); setPeek(false); }
        }}
      >
        {hasPersistentNavigation ? (
          <Drawer
            variant="permanent"
            sx={{
              '& .MuiDrawer-paper': {
                boxSizing: 'border-box',
                width: paperWidth,
                transition: 'width 180ms ease-out',
                '@media (prefers-reduced-motion: reduce)': { transition: 'none' },
                zIndex: (t) => (peek && !pinned ? t.zIndex.drawer + 2 : undefined),
                boxShadow: peek && !pinned ? '0 16px 40px -12px rgba(15,18,24,0.35)' : undefined,
                borderRight: '1px solid',
                borderColor: 'divider',
                // The rail is glass over the canvas washes; its rows are the solid objects.
                // Floating over the page it must be opaque enough to read; resting, it is glass.
                backgroundColor: (theme) => theme.palette.mode === 'dark'
                  ? (peek && !pinned ? 'rgba(15, 23, 42, 0.94)' : 'rgba(15, 23, 42, 0.58)')
                  : (peek && !pinned ? 'rgba(255, 255, 255, 0.94)' : 'rgba(255, 255, 255, 0.58)'),
                backdropFilter: 'blur(18px) saturate(140%)',
                WebkitBackdropFilter: 'blur(18px) saturate(140%)',
                overflowX: 'hidden',
              },
            }}
            open
          >
            <Toolbar sx={{ px: collapsed ? 1 : 2.5, display: 'flex', justifyContent: collapsed ? 'center' : 'flex-start' }}>
              <Branding showText={!collapsed} fontSize={20} logoSize={32} />
            </Toolbar>
            <Sidebar
              collapsed={collapsed}
              onRequestExpand={() => { clearTimer(); setPeek(true); }}
              onNavigate={() => { clearTimer(); setPeek(false); }}
            />
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
          width: hasPersistentNavigation ? `calc(100% - ${reservedWidth}px)` : '100%',
          minWidth: 0,
          maxWidth: '100%',
          boxSizing: 'border-box',
          overflowX: 'hidden',
          minHeight: '100vh',
          // Transparent: the body paints the canvas and its washes; painting it again here
          // would flatten every glass surface on the page.
          backgroundColor: 'transparent',
        }}
      >
        <Toolbar />
        {/* The shell stays mounted while the working paper changes. Keying this small boundary by
            pathname gives every real navigation one short, consistent arrival without replaying
            motion for search, filters or pagination changes on the same screen. */}
        <Box key={location.pathname} className="nx-route-enter" sx={{ minWidth: 0 }}>
          {children}
        </Box>
      </Box>

      {/* Fixed on every tenant page while a platform impersonation session is
          active; renders nothing otherwise. */}
      <ImpersonationBanner />
    </Box>
  );
};

export default MainLayout;
