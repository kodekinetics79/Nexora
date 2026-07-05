import React from 'react';
import { useTranslation } from 'react-i18next';
import {
  AppBar,
  Avatar,
  Badge,
  Box,
  Divider,
  IconButton,
  ListItemIcon,
  ListItemText,
  Menu,
  MenuItem,
  Toolbar,
  Tooltip,
  Typography,
  alpha,
  useTheme,
} from '@mui/material';
import {
  Add as AddIcon,
  ChatBubbleOutlined as ChatIcon,
  DarkMode as MoonIcon,
  LightMode as SunIcon,
  Logout,
  Menu as MenuIcon,
  NotificationsNone as NotificationsIcon,
  Palette as PaletteIcon,
  Person,
  Search as SearchIcon,
  StarBorder as StarIcon,
} from '@mui/icons-material';
import { useAppTheme } from '../../context/ThemeContext';
import { useAuth } from '../../context/AuthContext';

interface NavbarProps {
  onToggleSidebar: () => void;
  drawerWidth: number;
}

const languages = [
  { code: 'en', name: 'English', short: 'EN' },
  { code: 'ar', name: 'Arabic', short: 'AR' },
  { code: 'ur', name: 'Urdu', short: 'UR' },
  { code: 'es', name: 'Spanish', short: 'ES' },
  { code: 'fr', name: 'French', short: 'FR' },
  { code: 'de', name: 'German', short: 'DE' },
];

const colorOptions = [
  { name: 'Oracle Red', color: '#E11D2E' },
  { name: 'Dark Red', color: '#B91C1C' },
  { name: 'Navy Ledger', color: '#0F1B2D' },
  { name: 'Procurement Teal', color: '#0f766e' },
  { name: 'Financial Green', color: '#16a34a' },
  { name: 'Graphite', color: '#3f3f46' },
];

const Navbar: React.FC<NavbarProps> = ({ onToggleSidebar, drawerWidth }) => {
  const theme = useTheme();
  const { mode, setMode, primaryColor, setPrimaryColor } = useAppTheme();
  const { userData, logout } = useAuth();
  const { i18n } = useTranslation();
  const [langAnchor, setLangAnchor] = React.useState<null | HTMLElement>(null);
  const [profileAnchor, setProfileAnchor] = React.useState<null | HTMLElement>(null);
  const [colorAnchor, setColorAnchor] = React.useState<null | HTMLElement>(null);

  const closeMenus = () => {
    setLangAnchor(null);
    setProfileAnchor(null);
    setColorAnchor(null);
  };

  const handleLanguageChange = (lang: string) => {
    i18n.changeLanguage(lang);
    document.dir = lang === 'ar' || lang === 'ur' ? 'rtl' : 'ltr';
    closeMenus();
  };

  const currentLanguage = languages.find((lang) => lang.code === i18n.language) || languages[0];

  return (
    <AppBar
      position="fixed"
      sx={{
        width: { sm: `calc(100% - ${drawerWidth}px)` },
        ml: { sm: `${drawerWidth}px` },
        boxShadow: 'none',
        bgcolor: mode === 'dark' ? 'rgba(15, 27, 45, 0.88)' : 'rgba(255, 255, 255, 0.9)',
        backdropFilter: 'blur(22px)',
        borderBottom: '1px solid',
        borderColor: mode === 'dark' ? 'rgba(255,255,255,0.08)' : '#E5E7EB',
        color: 'text.primary',
        zIndex: (theme) => theme.zIndex.drawer + 1,
        transition: (theme) => theme.transitions.create(['width', 'margin']),
      }}
    >
      <Toolbar sx={{ minHeight: { xs: 64, md: 68 }, px: { xs: 1.5, md: 3 }, justifyContent: 'space-between', gap: 2 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', minWidth: 0 }}>
          <IconButton
            color="inherit"
            edge="start"
            onClick={onToggleSidebar}
              sx={{
                mr: 1.5,
                bgcolor: mode === 'dark' ? alpha(theme.palette.common.white, 0.06) : '#fff',
                border: '1px solid',
                borderColor: 'divider',
                boxShadow: mode === 'dark' ? 'none' : '0 8px 22px rgba(15,23,42,0.06)',
                '&:hover': {
                  borderColor: alpha(theme.palette.primary.main, 0.35),
                  color: 'primary.main',
                  bgcolor: alpha(theme.palette.primary.main, 0.06),
                },
              }}
          >
            <MenuIcon />
          </IconButton>

          <Box
            sx={{
              display: { xs: 'none', md: 'flex' },
              alignItems: 'center',
              gap: 1,
              width: { md: 360, lg: 500 },
              px: 1.5,
              py: 0.75,
              borderRadius: 999,
              border: '1px solid',
              borderColor: mode === 'dark' ? alpha(theme.palette.common.white, 0.1) : '#E5E7EB',
              bgcolor: mode === 'dark' ? alpha(theme.palette.common.white, 0.04) : '#F8FAFC',
              boxShadow: mode === 'dark' ? 'none' : 'inset 0 1px 0 rgba(255,255,255,.8), 0 10px 26px rgba(15,23,42,0.04)',
              '&:focus-within': {
                borderColor: alpha(theme.palette.primary.main, 0.38),
                boxShadow: `0 0 0 4px ${alpha(theme.palette.primary.main, 0.08)}`,
              },
            }}
          >
            <SearchIcon sx={{ color: 'primary.main', fontSize: 19 }} />
            <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 650, flex: 1 }}>
              Search RFQs, Leads, Suppliers, Customers...
            </Typography>
            <Box sx={{ px: 0.75, py: 0.2, borderRadius: 1, border: '1px solid', borderColor: 'divider', bgcolor: 'background.paper' }}>
              <Typography variant="caption" sx={{ fontSize: 10, fontWeight: 850 }}>
                Ctrl K
              </Typography>
            </Box>
          </Box>
        </Box>

        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
          {[
            { title: 'Favorites', icon: <StarIcon sx={{ fontSize: 20 }} /> },
            { title: 'Quick create', icon: <AddIcon sx={{ fontSize: 21 }} /> },
            { title: 'Messages', icon: <ChatIcon sx={{ fontSize: 20 }} /> },
          ].map((item) => (
            <Tooltip title={item.title} key={item.title}>
              <IconButton
                color="inherit"
                sx={{
                  display: { xs: 'none', lg: 'inline-flex' },
                  bgcolor: mode === 'dark' ? alpha(theme.palette.common.white, 0.05) : '#fff',
                  border: '1px solid',
                  borderColor: 'divider',
                  width: 40,
                  height: 40,
                  '&:hover': { color: 'primary.main', borderColor: alpha(theme.palette.primary.main, 0.32), bgcolor: alpha(theme.palette.primary.main, 0.06) },
                }}
              >
                {item.icon}
              </IconButton>
            </Tooltip>
          ))}

          <Tooltip title="Switch language">
            <IconButton color="inherit" onClick={(event) => setLangAnchor(event.currentTarget)} sx={{ bgcolor: mode === 'dark' ? alpha(theme.palette.common.white, 0.05) : '#fff', border: '1px solid', borderColor: 'divider', width: 40, height: 40, '&:hover': { color: 'primary.main', borderColor: alpha(theme.palette.primary.main, 0.32), bgcolor: alpha(theme.palette.primary.main, 0.06) } }}>
              <Typography variant="caption" sx={{ fontWeight: 900 }}>
                {currentLanguage.short}
              </Typography>
            </IconButton>
          </Tooltip>

          <Tooltip title="Toggle theme">
            <IconButton color="inherit" onClick={() => setMode(mode === 'dark' ? 'light' : 'dark')} sx={{ bgcolor: mode === 'dark' ? alpha(theme.palette.common.white, 0.05) : '#fff', border: '1px solid', borderColor: 'divider', width: 40, height: 40, '&:hover': { color: 'primary.main', borderColor: alpha(theme.palette.primary.main, 0.32), bgcolor: alpha(theme.palette.primary.main, 0.06) } }}>
              {mode === 'dark' ? <SunIcon sx={{ fontSize: 20 }} /> : <MoonIcon sx={{ fontSize: 20 }} />}
            </IconButton>
          </Tooltip>

          <Tooltip title="Notifications">
            <IconButton color="inherit" sx={{ bgcolor: mode === 'dark' ? alpha(theme.palette.common.white, 0.05) : '#fff', border: '1px solid', borderColor: 'divider', width: 40, height: 40, '&:hover': { color: 'primary.main', borderColor: alpha(theme.palette.primary.main, 0.32), bgcolor: alpha(theme.palette.primary.main, 0.06) } }}>
              <Badge color="error" variant="dot">
                <NotificationsIcon sx={{ fontSize: 20 }} />
              </Badge>
            </IconButton>
          </Tooltip>

          <Divider orientation="vertical" flexItem sx={{ mx: { xs: 0.5, md: 1 }, height: 24, my: 'auto' }} />

          <Box
            onClick={(event) => setProfileAnchor(event.currentTarget)}
            sx={{
              display: 'flex',
              alignItems: 'center',
              gap: 1.25,
              cursor: 'pointer',
              p: 0.5,
              borderRadius: 2,
              '&:hover': { bgcolor: alpha(theme.palette.primary.main, 0.06) },
            }}
          >
            <Avatar
              sx={{
                width: 36,
                height: 36,
                background: `linear-gradient(135deg, ${theme.palette.primary.main}, ${theme.palette.primary.dark})`,
                fontWeight: 850,
                fontSize: 14,
                boxShadow: `0 0 0 3px ${primaryColor}24`,
              }}
            >
              {userData.userName?.charAt(0) || 'U'}
            </Avatar>
            <Box sx={{ display: { xs: 'none', sm: 'block' }, textAlign: 'left' }}>
              <Typography variant="subtitle2" sx={{ lineHeight: 1.2, fontSize: '0.84rem' }}>
                {userData.userName || 'User'}
              </Typography>
              <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>
                {userData.roleName || 'Operator'}
              </Typography>
            </Box>
          </Box>
        </Box>

        <Menu anchorEl={langAnchor} open={Boolean(langAnchor)} onClose={closeMenus}>
          {languages.map((lang) => (
            <MenuItem
              key={lang.code}
              selected={currentLanguage.code === lang.code}
              onClick={() => handleLanguageChange(lang.code)}
              sx={{ borderRadius: 1.5, minWidth: 170 }}
            >
              <Typography sx={{ mr: 1.5, fontSize: 12, fontWeight: 900, color: 'primary.main' }}>{lang.short}</Typography>
              <ListItemText primary={lang.name} slotProps={{ primary: { variant: 'body2', sx: { fontWeight: 750 } } }} />
            </MenuItem>
          ))}
        </Menu>

        <Menu anchorEl={profileAnchor} open={Boolean(profileAnchor)} onClose={closeMenus}>
          <MenuItem onClick={closeMenus} sx={{ borderRadius: 1.5, minWidth: 240 }}>
            <ListItemIcon>
              <Person fontSize="small" />
            </ListItemIcon>
            <ListItemText primary="Account Profile" slotProps={{ primary: { variant: 'body2', sx: { fontWeight: 750 } } }} />
          </MenuItem>
          <MenuItem onClick={(event) => setColorAnchor(event.currentTarget)} sx={{ borderRadius: 1.5 }}>
            <ListItemIcon>
              <PaletteIcon fontSize="small" />
            </ListItemIcon>
            <ListItemText
              primary="Color Theme"
              secondary={colorOptions.find((item) => item.color === primaryColor)?.name || 'Custom'}
              slotProps={{
                primary: { variant: 'body2', sx: { fontWeight: 750 } },
                secondary: { variant: 'caption', sx: { fontWeight: 750, color: 'primary.main' } },
              }}
            />
          </MenuItem>
          <Divider sx={{ my: 1 }} />
          <MenuItem
            onClick={() => {
              closeMenus();
              logout();
            }}
            sx={{ borderRadius: 1.5, color: 'error.main' }}
          >
            <ListItemIcon>
              <Logout fontSize="small" color="error" />
            </ListItemIcon>
            <ListItemText primary="Log Out Session" slotProps={{ primary: { variant: 'body2', sx: { fontWeight: 800 } } }} />
          </MenuItem>
        </Menu>

        <Menu anchorEl={colorAnchor} open={Boolean(colorAnchor)} onClose={closeMenus} anchorOrigin={{ horizontal: 'left', vertical: 'top' }}>
          {colorOptions.map((option) => (
            <MenuItem
              key={option.color}
              selected={primaryColor === option.color}
              onClick={() => {
                setPrimaryColor(option.color);
                closeMenus();
              }}
              sx={{ borderRadius: 1.5, minWidth: 210 }}
            >
              <Box sx={{ width: 12, height: 12, borderRadius: '50%', bgcolor: option.color, mr: 1.5 }} />
              <ListItemText primary={option.name} slotProps={{ primary: { variant: 'body2', sx: { fontWeight: 750 } } }} />
            </MenuItem>
          ))}
        </Menu>
      </Toolbar>
    </AppBar>
  );
};

export default Navbar;
