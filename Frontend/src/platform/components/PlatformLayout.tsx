import { useState } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import {
  AppBar,
  Avatar,
  Box,
  Chip,
  Divider,
  Drawer,
  IconButton,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Toolbar,
  Tooltip,
  Typography,
  useMediaQuery,
  useTheme,
} from '@mui/material';
import {
  ArrowBack as ArrowBackIcon,
  DarkMode as MoonIcon,
  Dns as PipelineIcon,
  Insights as OverviewIcon,
  Logout as ExitIcon,
  ManageAccounts as UsersIcon,
  Menu as MenuIcon,
  Paid as BillingIcon,
  ReceiptLong as AuditIcon,
  MarkEmailReadOutlined as EmailIcon,
  Shield as SecurityIcon,
  LockOutlined as PolicyIcon,
  SupportAgent as SupportIcon,
  Tune as PlansIcon,
  Workspaces as TenantsIcon,
  LightMode as SunIcon,
  Bolt as BoltIcon,
} from '@mui/icons-material';
import { useAppTheme } from '../../context/ThemeContext';
import { usePlatformAuth } from '../auth/usePlatformAuth';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import type { PlatformPermissions } from '../auth/permissions';
import SkipLink, { MAIN_CONTENT_ID } from '../../components/layout/SkipLink';
import PlatformMfaEnforcementBanner from './PlatformMfaEnforcementBanner';

/** Referenced by the mobile drawer toggle's `aria-controls`. */
const PLATFORM_NAV_ID = 'platform-sidebar';

/**
 * Each entry names the permission the SERVER requires of the screen behind it, not a guess.
 *
 * This used to be a single `requiresTenantAdmin` flag, which meant only Support was gated and
 * every other screen was advertised to every role. Three of them are not open to every role:
 *   /platform/users     — `PlatformUsersController` is `[Authorize(Policy = Owner)]` at class
 *                         level, list included, so SupportAdmin, BillingAdmin and ReadOnlyOps
 *                         were being offered a screen that could only ever refuse them.
 *   /platform/billing   — `PlatformBillingController` is class-level `Platform.Billing`, so
 *                         SupportAdmin and ReadOnlyOps were in the same position.
 *   /platform/security  — its impersonation-session read is `Platform.Impersonate`, and that
 *                         one is not even soft-landed: a BillingAdmin got a red error panel
 *                         with a Retry button that can never succeed.
 * `visible` returning undefined means "every platform role", which is the honest default for
 * the screens whose reads really are open to the whole plane.
 */
const NAV: {
  to: string;
  label: string;
  icon: React.ReactElement;
  visible?: (permissions: PlatformPermissions) => boolean;
}[] = [
  { to: '/platform/overview', label: 'Overview', icon: <OverviewIcon /> },
  { to: '/platform/tenants', label: 'Tenants', icon: <TenantsIcon /> },
  { to: '/platform/pipeline', label: 'Pipeline', icon: <PipelineIcon /> },
  { to: '/platform/plans', label: 'Plans', icon: <PlansIcon /> },
  { to: '/platform/users', label: 'Users', icon: <UsersIcon />, visible: (p) => p.isOwner },
  { to: '/platform/billing', label: 'Billing', icon: <BillingIcon />, visible: (p) => p.canAdministerBilling },
  { to: '/platform/support', label: 'Support', icon: <SupportIcon />, visible: (p) => p.canAdministerTenants },
  { to: '/platform/email', label: 'Email', icon: <EmailIcon /> },
  { to: '/platform/security', label: 'Security', icon: <SecurityIcon />, visible: (p) => p.canImpersonate },
  // The page's full read model is Owner-only on the server. Other operators still receive the
  // effective-policy banner on every route, but must not be sent to a screen whose first request
  // is guaranteed to return 403.
  { to: '/platform/security/authentication', label: 'Platform Authentication', icon: <PolicyIcon />, visible: (p) => p.isOwner },
  { to: '/platform/audit', label: 'Audit Log', icon: <AuditIcon /> },
];

const DRAWER_WIDTH = 264;

function SidebarContent({ onNavigate }: { onNavigate?: () => void }) {
  const { mode } = useAppTheme();
  const permissions = usePlatformPermissions();
  return (
    <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      <Toolbar sx={{ px: 2.5 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
          <Box
            sx={{
              width: 36,
              height: 36,
              borderRadius: 2,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              background: 'linear-gradient(135deg, #6366f1 0%, #0ea5e9 100%)',
              color: '#fff',
              boxShadow: '0 6px 16px rgba(99,102,241,0.4)',
            }}
          >
            <BoltIcon sx={{ fontSize: 22 }} />
          </Box>
          <Box sx={{ lineHeight: 1 }}>
            <Typography sx={{ fontWeight: 900, fontSize: 17, letterSpacing: '-0.5px' }}>NEXORA</Typography>
            <Typography sx={{ fontWeight: 700, fontSize: 9.5, letterSpacing: '0.16em', color: 'primary.main', textTransform: 'uppercase' }}>
              Platform Console
            </Typography>
          </Box>
        </Box>
      </Toolbar>

      <Box sx={{ px: 2, pb: 1 }}>
        {/* The operator's real tier, not a decoration. Which controls this console offers
            is decided by this value, so misreporting it would be misreporting authority. */}
        <Chip
          size="small"
          label={
            permissions.role
              ? `${permissions.role.toUpperCase()} · scope=platform`
              : 'ROLE NOT RECOGNISED · read-only'
          }
          sx={{
            width: '100%',
            justifyContent: 'flex-start',
            fontWeight: 700,
            fontSize: '0.65rem',
            letterSpacing: 0.4,
            bgcolor: mode === 'dark' ? 'rgba(99,102,241,0.15)' : 'rgba(99,102,241,0.1)',
            color: '#6366f1',
            border: '1px solid rgba(99,102,241,0.25)',
          }}
        />
      </Box>

      <List sx={{ px: 1.5, flex: 1 }}>
        {NAV.filter((item) => !item.visible || item.visible(permissions)).map((item) => (
          <ListItemButton
            key={item.to}
            component={NavLink}
            to={item.to}
            onClick={onNavigate}
            sx={{
              borderRadius: 2,
              mb: 0.5,
              minHeight: 46,
              color: 'text.secondary',
              transition: 'all 0.18s ease',
              '&:hover': { bgcolor: 'action.hover', transform: 'translateX(3px)' },
              '&.active': {
                color: 'primary.contrastText',
                bgcolor: 'primary.main',
                boxShadow: (t) => `0 8px 18px -6px ${t.palette.primary.main}`,
                '& .MuiListItemIcon-root': { color: 'primary.contrastText' },
              },
            }}
          >
            <ListItemIcon sx={{ minWidth: 38, color: 'inherit' }}>{item.icon}</ListItemIcon>
            <ListItemText primary={item.label} slotProps={{ primary: { sx: { fontSize: '0.9rem', fontWeight: 600 } } }} />
          </ListItemButton>
        ))}
      </List>

      <Box sx={{ p: 2 }}>
        <Typography variant="caption" sx={{ color: 'text.secondary', opacity: 0.7 }}>
          ADR-0005 · control plane
        </Typography>
      </Box>
    </Box>
  );
}

export default function PlatformLayout() {
  const theme = useTheme();
  const { mode, setMode } = useAppTheme();
  const { platformUser, platformLogout } = usePlatformAuth();
  const navigate = useNavigate();
  const isMobile = useMediaQuery(theme.breakpoints.down('md'));
  const [mobileOpen, setMobileOpen] = useState(false);

  const displayName = platformUser?.name ?? platformUser?.email ?? 'Operator';
  const initials = displayName
    .split(/[\s@.]+/)
    .filter(Boolean)
    .map((p) => p.charAt(0))
    .slice(0, 2)
    .join('')
    .toUpperCase() || 'OP';

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh', bgcolor: 'background.default' }}>
      {/* SC 2.4.1 — first tab stop, lets keyboard users bypass the nav rail. */}
      <SkipLink />

      {/* Sidebar */}
      <Box
        component="nav"
        id={PLATFORM_NAV_ID}
        aria-label="Platform console"
        sx={{ width: { md: DRAWER_WIDTH }, flexShrink: { md: 0 } }}
      >
        <Drawer
          variant={isMobile ? 'temporary' : 'permanent'}
          open={isMobile ? mobileOpen : true}
          onClose={() => setMobileOpen(false)}
          ModalProps={{ keepMounted: true }}
          sx={{
            '& .MuiDrawer-paper': {
              width: DRAWER_WIDTH,
              boxSizing: 'border-box',
              borderRight: '1px solid',
              borderColor: 'divider',
              bgcolor: 'background.paper',
            },
          }}
        >
          <SidebarContent onNavigate={() => setMobileOpen(false)} />
        </Drawer>
      </Box>

      {/* Main column */}
      <Box sx={{ flexGrow: 1, width: { md: `calc(100% - ${DRAWER_WIDTH}px)` }, display: 'flex', flexDirection: 'column' }}>
        <AppBar
          position="sticky"
          elevation={0}
          sx={{
            bgcolor: mode === 'dark' ? 'rgba(15,23,42,0.8)' : 'rgba(255,255,255,0.8)',
            backdropFilter: 'blur(12px)',
            borderBottom: '1px solid',
            borderColor: 'divider',
            color: 'text.primary',
          }}
        >
          <Toolbar sx={{ gap: 1 }}>
            {isMobile && (
              <IconButton
                edge="start"
                onClick={() => setMobileOpen(true)}
                aria-label="Open platform navigation"
                aria-expanded={mobileOpen}
                aria-controls={PLATFORM_NAV_ID}
                sx={{ mr: 1 }}
              >
                <MenuIcon />
              </IconButton>
            )}
            <Tooltip title="Back to tenant app">
              <IconButton onClick={() => navigate('/dashboard')} sx={{ bgcolor: 'action.hover', borderRadius: 2 }}>
                <ArrowBackIcon fontSize="small" />
              </IconButton>
            </Tooltip>
            <Typography variant="subtitle1" sx={{ fontWeight: 800, ml: 0.5, display: { xs: 'none', sm: 'block' } }}>
              Operator Control Plane
            </Typography>

            <Box sx={{ flexGrow: 1 }} />

            <Tooltip title={mode === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}>
              <IconButton onClick={() => setMode(mode === 'dark' ? 'light' : 'dark')} sx={{ bgcolor: 'action.hover', borderRadius: 2 }}>
                {mode === 'dark' ? <SunIcon fontSize="small" /> : <MoonIcon fontSize="small" />}
              </IconButton>
            </Tooltip>

            <Divider orientation="vertical" flexItem sx={{ mx: 1, my: 1.5 }} />

            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.25 }}>
              {/* color: MUI's Avatar default (background.default) is only
                  3.92:1 on the default brand colour — SC 1.4.3. */}
              <Avatar sx={{ width: 34, height: 34, bgcolor: 'primary.main', color: 'primary.contrastText', fontSize: 13, fontWeight: 800 }}>{initials}</Avatar>
              <Box sx={{ display: { xs: 'none', sm: 'block' }, lineHeight: 1.1 }}>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>
                  {displayName}
                </Typography>
                <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                  {platformUser?.role ?? 'Role not recognised'}
                </Typography>
              </Box>
            </Box>

            <Tooltip title="Sign out of platform console">
              <IconButton onClick={platformLogout} color="error" sx={{ ml: 0.5 }}>
                <ExitIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          </Toolbar>
        </AppBar>

        {/* Above the main region and outside <Outlet />, so it is present on EVERY platform
            screen for as long as enforcement is off — not only on the page that turned it off. */}
        <PlatformMfaEnforcementBanner />

        <Box
          component="main"
          id={MAIN_CONTENT_ID}
          tabIndex={-1}
          sx={{
            flexGrow: 1,
            p: { xs: 2, md: 3 },
            maxWidth: 1600,
            width: '100%',
            mx: 'auto',
            '&:focus': { outline: 'none' },
            '&:focus-visible': {
              outline: (t) => `3px solid ${t.palette.primary.main}`,
              outlineOffset: -3,
            },
          }}
        >
          <Outlet />
        </Box>
      </Box>
    </Box>
  );
}
