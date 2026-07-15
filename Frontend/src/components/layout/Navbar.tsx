import React from 'react';
import {
  AppBar,
  Toolbar,
  IconButton,
  Typography,
  Box,
  Avatar,
  Menu,
  MenuItem,
  ListItemIcon,
  Divider,
  Tooltip,
  ListItemText,
  InputBase,
} from '@mui/material';
import {
  Menu as MenuIcon,
  Search as SearchIcon,
  LightMode as SunIcon,
  DarkMode as MoonIcon,
  Logout,
  Person,
  Language,
} from '@mui/icons-material';
import { useNavigate } from 'react-router-dom';
import { useAppTheme } from '../../context/ThemeContext';
import { useAuth } from '../../context/AuthContext';

interface NavbarProps {
  onToggleSidebar: () => void;
  drawerWidth: number;
}

const Navbar: React.FC<NavbarProps> = ({ onToggleSidebar, drawerWidth }) => {
  const { mode, setMode, primaryColor, setPrimaryColor } = useAppTheme();
  const { userData, logout } = useAuth();
  const navigate = useNavigate();
  const searchInputRef = React.useRef<HTMLInputElement>(null);
  const [searchValue, setSearchValue] = React.useState('');
  const [anchorEl, setAnchorEl] = React.useState<null | HTMLElement>(null);
  const [colorMenuAnchor, setColorMenuAnchor] = React.useState<null | HTMLElement>(null);

  // FE-14: never fall back to a hardcoded person's name/role — derive a label
  // and initials from the real userData, with a neutral generic fallback while
  // userData is briefly empty (e.g. during auth bootstrap).
  const displayName = userData.userName?.trim() || '';
  const initials = displayName
    ? displayName
        .split(/\s+/)
        .map((part) => part.charAt(0))
        .slice(0, 2)
        .join('')
        .toUpperCase()
    : '';

  // Quick-jump destinations for the command bar (⌘K / Ctrl+K to focus, Enter to jump).
  const quickNav: { keywords: string[]; path: string }[] = [
    { keywords: ['lead', 'leads'], path: '/procurement/leads/all' },
    { keywords: ['rfq', 'rfqs'], path: '/procurement/rfqs/all' },
    { keywords: ['quote', 'quotes', 'quotation', 'quotations'], path: '/sales/quotes' },
    { keywords: ['order', 'orders'], path: '/sales/orders' },
    { keywords: ['shipment', 'shipments', 'logistics'], path: '/sales/shipments' },
    { keywords: ['product', 'products', 'inventory'], path: '/inventory/products' },
    { keywords: ['supplier', 'suppliers'], path: '/suppliers' },
    { keywords: ['customer', 'customers'], path: '/customers' },
    { keywords: ['user', 'users'], path: '/security/users' },
    { keywords: ['dashboard', 'home', 'overview'], path: '/dashboard' },
  ];

  const handleQuickSearch = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== 'Enter') return;
    const query = searchValue.trim().toLowerCase();
    if (!query) return;
    const match = quickNav.find((entry) =>
      entry.keywords.some((k) => k === query || k.includes(query) || query.includes(k))
    );
    navigate(match ? match.path : '/dashboard');
    setSearchValue('');
    searchInputRef.current?.blur();
  };

  React.useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        searchInputRef.current?.focus();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  const handleProfileClick = (event: React.MouseEvent<HTMLElement>) => {
    setAnchorEl(event.currentTarget);
  };

  const handleColorMenuOpen = (event: React.MouseEvent<HTMLElement>) => {
    setColorMenuAnchor(event.currentTarget);
  };

  const handleClose = () => {
    setAnchorEl(null);
    setColorMenuAnchor(null);
  };

  const colorOptions = [
    { name: 'Executive Navy', color: '#1e3a8a' },
    { name: 'Corporate Blue', color: '#0056b3' },
    { name: 'Trust Blue', color: '#2563eb' },
    { name: 'Steel Blue', color: '#4682b4' },
    { name: 'Slate Gray', color: '#475569' },
    { name: 'Charcoal', color: '#334155' },
    { name: 'Graphite', color: '#3f3f46' },
    { name: 'Banking Teal', color: '#0f766e' },
    { name: 'Financial Green', color: '#0d9488' },
    { name: 'Professional Green', color: '#16a34a' },
    { name: 'Subtle Burgundy', color: '#831843' },
    { name: 'Crimson', color: '#be123c' },
  ];

  return (
    <AppBar
      position="fixed"
      sx={{
        width: { sm: `calc(100% - ${drawerWidth}px)` },
        ml: { sm: `${drawerWidth}px` },
        boxShadow: 'none',
        backgroundColor: mode === 'dark' ? 'rgba(15, 23, 42, 0.8)' : 'rgba(255, 255, 255, 0.8)',
        backdropFilter: 'blur(12px)',
        borderBottom: '1px solid',
        borderColor: 'divider',
        color: 'text.primary',
        zIndex: (theme) => theme.zIndex.drawer + 1,
        transition: (theme) => theme.transitions.create(['width', 'margin'], {
          easing: theme.transitions.easing.sharp,
          duration: theme.transitions.duration.leavingScreen,
        }),
      }}
    >
      <Toolbar sx={{ justifyContent: 'space-between' }}>
        <Box sx={{ display: 'flex', alignItems: 'center' }}>
          <IconButton
            color="inherit"
            edge="start"
            onClick={onToggleSidebar}
            sx={{ mr: 2 }}
          >
            <MenuIcon />
          </IconButton>
          <Box
            sx={{
              display: { xs: 'none', md: 'flex' },
              alignItems: 'center',
              backgroundColor: mode === 'dark' ? 'rgba(255, 255, 255, 0.05)' : 'rgba(0, 0, 0, 0.03)',
              px: 2,
              py: 0.5,
              borderRadius: 2,
              width: 320,
              border: '1px solid',
              borderColor: 'divider',
            }}
          >
            <SearchIcon sx={{ color: 'text.secondary', mr: 1, fontSize: 18 }} />
            <InputBase
              inputRef={searchInputRef}
              value={searchValue}
              onChange={(e) => setSearchValue(e.target.value)}
              onKeyDown={handleQuickSearch}
              placeholder="Search anything..."
              sx={{
                flex: 1,
                fontSize: '0.875rem',
                color: 'text.primary',
                '& input::placeholder': { color: 'text.secondary', opacity: 0.7 },
              }}
            />
            <Box sx={{ ml: 'auto', px: 0.8, py: 0.2, backgroundColor: 'action.hover', borderRadius: 1, border: '1px solid', borderColor: 'divider' }}>
              <Typography variant="caption" sx={{ fontWeight: 700, fontSize: 10, opacity: 0.8 }}>⌘ K</Typography>
            </Box>
          </Box>
        </Box>

        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
          {/* FE-10: language switcher hidden for the pilot — page bodies are not
              fully translated, so switching languages would produce a mixed
              English/localized UI. The app is locked to English (see i18n.ts). */}
          <Tooltip title="Toggle Theme">
            <IconButton color="inherit" onClick={() => setMode(mode === 'dark' ? 'light' : 'dark')} sx={{ backgroundColor: 'action.hover', width: 40, height: 40, borderRadius: 2 }}>
              {mode === 'dark' ? <SunIcon sx={{ fontSize: 20 }} /> : <MoonIcon sx={{ fontSize: 20 }} />}
            </IconButton>
          </Tooltip>

          <Divider orientation="vertical" flexItem sx={{ mx: 1.5, height: 24, my: 'auto' }} />

          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, cursor: 'pointer', ml: 1 }} onClick={handleProfileClick}>
            <Avatar
              sx={{
                width: 36,
                height: 36,
                bgcolor: 'primary.main',
                fontWeight: 700,
                fontSize: 14,
                boxShadow: `0 0 0 2px ${mode === 'dark' ? '#0f172a' : '#fff'}, 0 0 0 4px ${primaryColor}44`
              }}
            >
              {initials || <Person sx={{ fontSize: 18 }} />}
            </Avatar>
            <Box sx={{ display: { xs: 'none', sm: 'block' }, textAlign: 'left' }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 800, lineHeight: 1.2, fontSize: '0.85rem' }}>
                {displayName || 'User'}
              </Typography>
              <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 600, fontSize: '0.75rem', opacity: 0.7 }}>
                {userData.roleName || 'Member'}
              </Typography>
            </Box>
          </Box>
        </Box>

        <Menu
          anchorEl={anchorEl}
          open={Boolean(anchorEl)}
          onClose={handleClose}
          transformOrigin={{ horizontal: 'right', vertical: 'top' }}
          anchorOrigin={{ horizontal: 'right', vertical: 'bottom' }}
          slotProps={{
            paper: {
              sx: {
                mt: 1.5,
                minWidth: 240,
                boxShadow: '0 10px 40px rgba(0,0,0,0.2)',
                borderRadius: 4,
                p: 1,
                border: '1px solid',
                borderColor: 'divider',
              }
            }
          }}
        >
          <MenuItem onClick={() => { handleClose(); navigate('/dashboard'); }} sx={{ borderRadius: 2, py: 1.5 }}>
            <ListItemIcon><Person fontSize="small" sx={{ opacity: 0.7 }} /></ListItemIcon>
            <ListItemText
              primary="Account Profile"
              slotProps={{
                primary: { variant: 'body2', sx: { fontWeight: 600 } }
              }}
            />
          </MenuItem>

          <MenuItem onClick={handleColorMenuOpen} sx={{ borderRadius: 2, py: 1.5 }}>
            <ListItemIcon><Language fontSize="small" sx={{ opacity: 0.7 }} /></ListItemIcon>
            <Box sx={{ flex: 1 }}>
              <ListItemText
                primary="Color Theme"
                slotProps={{
                  primary: { variant: 'body2', sx: { fontWeight: 600 } }
                }}
              />
              <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 800, display: 'block', mt: -0.5 }}>
                {colorOptions.find(c => c.color === primaryColor)?.name || 'Custom'}
              </Typography>
            </Box>
          </MenuItem>

          <Divider sx={{ my: 1, opacity: 0.5 }} />

          <MenuItem onClick={() => { handleClose(); logout(); }} sx={{ borderRadius: 2, py: 1.5, color: 'error.main' }}>
            <ListItemIcon><Logout fontSize="small" color="error" /></ListItemIcon>
            <ListItemText
              primary="Log Out Session"
              slotProps={{
                primary: { variant: 'body2', sx: { fontWeight: 700 } }
              }}
            />
          </MenuItem>
        </Menu>

        {/* Color Selection Sub-Menu */}
        <Menu
          anchorEl={colorMenuAnchor}
          open={Boolean(colorMenuAnchor)}
          onClose={handleClose}
          anchorOrigin={{ horizontal: 'left', vertical: 'top' }}
          transformOrigin={{ horizontal: 'right', vertical: 'top' }}
          slotProps={{
            paper: {
              sx: {
                mr: 2,
                minWidth: 220,
                maxHeight: 480,
                boxShadow: '0 10px 40px rgba(0,0,0,0.2)',
                borderRadius: 4,
                p: 1,
                border: '1px solid',
                borderColor: 'divider',
                overflowY: 'auto'
              }
            }
          }}
        >
          <Typography variant="overline" sx={{ px: 2, py: 1, display: 'block', fontWeight: 800, opacity: 0.6, fontSize: '0.65rem' }}>
            SELECT VARIANT
          </Typography>
          {colorOptions.map((option) => (
            <MenuItem
              key={option.color}
              onClick={() => {
                setPrimaryColor(option.color);
                handleClose();
              }}
              sx={{
                borderRadius: 2,
                py: 1,
                backgroundColor: primaryColor === option.color ? 'action.selected' : 'transparent',
              }}
            >
              <Box
                sx={{
                  width: 12,
                  height: 12,
                  borderRadius: '50%',
                  backgroundColor: option.color,
                  mr: 2,
                  boxShadow: `0 0 0 2px ${option.color}33`
                }}
              />
              <ListItemText
                primary={option.name}
                slotProps={{
                  primary: {
                    variant: 'body2',
                    sx: {
                      fontWeight: primaryColor === option.color ? 800 : 600,
                      color: primaryColor === option.color ? 'text.primary' : 'text.secondary'
                    }
                  }
                }}
              />
              {primaryColor === option.color && (
                <Box sx={{ width: 6, height: 6, borderRadius: '50%', bgcolor: 'success.main', ml: 1 }} />
              )}
            </MenuItem>
          ))}
        </Menu>
      </Toolbar>
    </AppBar>
  );
};

export default Navbar;

