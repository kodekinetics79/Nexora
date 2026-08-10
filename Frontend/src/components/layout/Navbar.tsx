import React from 'react';
import {
  AppBar,
  Toolbar,
  IconButton,
  Typography,
  Box,
  Avatar,
  ButtonBase,
  Menu,
  MenuItem,
  ListItemIcon,
  Divider,
  Tooltip,
  ListItemText,
  InputBase,
  Popper,
  Paper,
  ClickAwayListener,
  List,
  ListItemButton,
  Chip,
  CircularProgress,
  Alert,
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
import searchService, {
  ENTITY_LABELS,
  MIN_SEARCH_LENGTH,
  routeForHit,
  type GlobalSearchResponse,
} from '../../api/services/searchService';

interface NavbarProps {
  onToggleSidebar: () => void;
  drawerWidth: number;
  /** Drives `aria-expanded` on the sidebar toggle (SC 4.1.2). */
  sidebarExpanded?: boolean;
  /** id of the `<nav>` the toggle controls, for `aria-controls`. */
  sidebarId?: string;
}

const PROFILE_BUTTON_ID = 'account-menu-button';
const PROFILE_MENU_ID = 'account-menu';
const COLOR_MENU_ID = 'color-theme-menu';
const SEARCH_RESULTS_ID = 'global-search-results';

const Navbar: React.FC<NavbarProps> = ({ onToggleSidebar, drawerWidth, sidebarExpanded, sidebarId }) => {
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

  // FR-DSH-04 — a real cross-entity search.
  //
  // What used to be here: a ten-entry keyword->route table, no network request, and an
  // unmatched term navigating silently to /dashboard. A search for a customer that does not
  // exist was indistinguishable from a search for one that does — the wiring contract's
  // failure #7, a control that reports success while doing nothing. Every branch below now
  // ends in a STATED outcome: results, "no results", or the server's own error.
  const [results, setResults] = React.useState<GlobalSearchResponse | null>(null);
  const [searching, setSearching] = React.useState(false);
  const [searchError, setSearchError] = React.useState<string | null>(null);
  const [searchOpen, setSearchOpen] = React.useState(false);
  const searchAnchorRef = React.useRef<HTMLDivElement>(null);
  const trimmedQuery = searchValue.trim();

  React.useEffect(() => {
    if (trimmedQuery.length < MIN_SEARCH_LENGTH) {
      setResults(null);
      setSearchError(null);
      setSearching(false);
      return;
    }

    const controller = new AbortController();
    // Debounced, so a typed word costs one query rather than one per keystroke.
    const timer = window.setTimeout(async () => {
      setSearching(true);
      setSearchError(null);
      try {
        const response = await searchService.search({ q: trimmedQuery, limit: 5 }, controller.signal);
        setResults(response);
        setSearchOpen(true);
      } catch (error) {
        if (controller.signal.aborted) return;
        setResults(null);
        // The server's message is surfaced verbatim; the client invents no copy that could
        // contradict it, and a failure is never rendered as "no results".
        const detail = (error as { response?: { data?: unknown } })?.response?.data;
        setSearchError(
          typeof detail === 'string' && detail.trim()
            ? detail
            : 'Search is unavailable right now. Nothing was searched.',
        );
        setSearchOpen(true);
      } finally {
        if (!controller.signal.aborted) setSearching(false);
      }
    }, 250);

    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [trimmedQuery]);

  const openHit = (path: string) => {
    setSearchOpen(false);
    setSearchValue('');
    searchInputRef.current?.blur();
    navigate(path);
  };

  const handleQuickSearch = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Escape') {
      setSearchOpen(false);
      return;
    }
    if (e.key !== 'Enter') return;
    e.preventDefault();
    // Enter opens the first result. When there is none it does NOTHING and the panel keeps
    // saying so — it does not navigate somewhere plausible.
    const first = results?.hits?.[0];
    if (first) openHit(routeForHit(first));
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
            aria-label={sidebarExpanded ? 'Collapse navigation menu' : 'Expand navigation menu'}
            aria-expanded={sidebarExpanded}
            aria-controls={sidebarId}
            sx={{ mr: 2 }}
          >
            <MenuIcon />
          </IconButton>
          <ClickAwayListener onClickAway={() => setSearchOpen(false)}>
            <Box sx={{ display: { xs: 'none', md: 'block' } }}>
              <Box
                ref={searchAnchorRef}
                sx={{
                  display: 'flex',
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
                <SearchIcon aria-hidden sx={{ color: 'text.secondary', mr: 1, fontSize: 18 }} />
                <InputBase
                  inputRef={searchInputRef}
                  value={searchValue}
                  onChange={(e) => setSearchValue(e.target.value)}
                  onKeyDown={handleQuickSearch}
                  onFocus={() => { if (results || searchError) setSearchOpen(true); }}
                  placeholder="Search customers, suppliers, products, documents..."
                  type="search"
                  // Placeholders are not a reliable accessible name (SC 4.1.2) —
                  // they disappear on input and are ignored by some AT.
                  inputProps={{
                    'aria-label':
                      'Search customers, suppliers, products, enquiries, quotes, orders and shipments',
                    'aria-expanded': searchOpen,
                    'aria-controls': SEARCH_RESULTS_ID,
                    role: 'combobox',
                    'aria-autocomplete': 'list',
                  }}
                  sx={{
                    flex: 1,
                    fontSize: '0.875rem',
                    color: 'text.primary',
                    '& input::placeholder': { color: 'text.secondary', opacity: 0.7 },
                  }}
                />
                {searching ? (
                  <CircularProgress size={14} aria-label="Searching" sx={{ ml: 1 }} />
                ) : (
                  <Box aria-hidden sx={{ ml: 'auto', px: 0.8, py: 0.2, backgroundColor: 'action.hover', borderRadius: 1, border: '1px solid', borderColor: 'divider' }}>
                    <Typography variant="caption" sx={{ fontWeight: 700, fontSize: 10, opacity: 0.8 }}>⌘ K</Typography>
                  </Box>
                )}
              </Box>

              <Popper
                open={searchOpen && trimmedQuery.length >= MIN_SEARCH_LENGTH}
                anchorEl={searchAnchorRef.current}
                placement="bottom-start"
                sx={{ zIndex: (theme) => theme.zIndex.modal }}
              >
                <Paper
                  id={SEARCH_RESULTS_ID}
                  elevation={8}
                  sx={{ mt: 1, width: 460, maxHeight: 480, overflowY: 'auto', borderRadius: 3, border: '1px solid', borderColor: 'divider' }}
                >
                  {searchError && (
                    // A failed request is NOT rendered as "no results" — the two mean opposite
                    // things and conflating them is how a broken search looks like an empty estate.
                    <Alert severity="error" sx={{ borderRadius: 0 }}>{searchError}</Alert>
                  )}

                  {!searchError && results && results.hits.length === 0 && (
                    <Box sx={{ p: 2 }}>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>
                        Nothing matches “{results.query}”.
                      </Typography>
                      <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                        Searched {results.searchedEntities.length} record types. Nothing was opened.
                      </Typography>
                    </Box>
                  )}

                  {!searchError && results && results.hits.length > 0 && (
                    <List dense role="listbox" aria-label="Search results" sx={{ py: 0.5 }}>
                      {results.hits.map((hit) => (
                        <ListItemButton
                          key={`${hit.entity}-${hit.id}`}
                          role="option"
                          onClick={() => openHit(routeForHit(hit))}
                          sx={{ alignItems: 'flex-start', gap: 1 }}
                        >
                          <Chip
                            size="small"
                            label={ENTITY_LABELS[hit.entity]}
                            sx={{ fontWeight: 700, fontSize: 10, height: 20, mt: 0.25 }}
                          />
                          <Box sx={{ minWidth: 0, flex: 1 }}>
                            <Typography variant="body2" sx={{ fontWeight: 700 }} noWrap>
                              {hit.title}
                            </Typography>
                            <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }} noWrap>
                              {hit.subtitle || 'No secondary reference recorded'}
                              {hit.status ? ` · ${hit.status}` : ''}
                            </Typography>
                            {/* Why this row is here. Without it a hit on a VAT number next to a
                                name search reads as a bug. */}
                            <Typography variant="caption" sx={{ color: 'text.disabled', display: 'block' }}>
                              matched on {hit.matchedOn}
                            </Typography>
                          </Box>
                        </ListItemButton>
                      ))}
                    </List>
                  )}

                  {/* Stated gaps. A shorter answer with no explanation is indistinguishable from
                      "nothing matched", which is the defect this whole control replaces. */}
                  {!searchError && results && (results.notes.length > 0 || results.deniedEntities.length > 0 || results.truncated.length > 0) && (
                    <Box sx={{ px: 2, py: 1, borderTop: '1px solid', borderColor: 'divider' }}>
                      {results.truncated.length > 0 && (
                        <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary' }}>
                          More {results.truncated.map((e) => ENTITY_LABELS[e].toLowerCase()).join(', ')} results exist than are shown.
                        </Typography>
                      )}
                      {results.deniedEntities.length > 0 && (
                        <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary' }}>
                          Not searched — you do not have access to: {results.deniedEntities.map((e) => ENTITY_LABELS[e]).join(', ')}.
                        </Typography>
                      )}
                      {results.notes.map((note) => (
                        <Typography key={note} variant="caption" sx={{ display: 'block', color: 'text.secondary' }}>
                          {note}
                        </Typography>
                      ))}
                    </Box>
                  )}
                </Paper>
              </Popper>
            </Box>
          </ClickAwayListener>
        </Box>

        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
          {/* FE-10: language switcher hidden for the pilot — page bodies are not
              fully translated, so switching languages would produce a mixed
              English/localized UI. The app is locked to English (see i18n.ts). */}
          {/* A string Tooltip title becomes the child's aria-label in MUI, so
              making it state-specific also fixes the accessible name (SC 4.1.2). */}
          <Tooltip title={mode === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}>
            <IconButton color="inherit" onClick={() => setMode(mode === 'dark' ? 'light' : 'dark')} sx={{ backgroundColor: 'action.hover', width: 40, height: 40, borderRadius: 2 }}>
              {mode === 'dark' ? <SunIcon sx={{ fontSize: 20 }} /> : <MoonIcon sx={{ fontSize: 20 }} />}
            </IconButton>
          </Tooltip>

          <Divider orientation="vertical" flexItem sx={{ mx: 1.5, height: 24, my: 'auto' }} />

          {/* Was a click-only <Box>: mouse users could open the account menu but
              keyboard users had no way to reach or activate it (SC 2.1.1 /
              SC 4.1.2). ButtonBase renders a real <button> with native focus,
              Enter/Space activation and a focus ring. */}
          <ButtonBase
            id={PROFILE_BUTTON_ID}
            onClick={handleProfileClick}
            aria-haspopup="menu"
            aria-expanded={Boolean(anchorEl)}
            aria-controls={anchorEl ? PROFILE_MENU_ID : undefined}
            aria-label={`Account menu — ${displayName || 'User'}, ${userData.roleName || 'Member'}`}
            sx={{
              display: 'flex',
              alignItems: 'center',
              gap: 1.5,
              ml: 1,
              px: 0.75,
              py: 0.5,
              borderRadius: 2,
              textAlign: 'left',
              '&:focus-visible': {
                outline: (theme) => `3px solid ${theme.palette.primary.main}`,
                outlineOffset: 2,
              },
            }}
          >
            <Avatar
              sx={{
                width: 36,
                height: 36,
                bgcolor: 'primary.main',
                // MUI Avatar defaults its text to background.default, which is
                // only 3.92:1 on the default brand colour (SC 1.4.3).
                color: 'primary.contrastText',
                fontWeight: 700,
                fontSize: 14,
                boxShadow: `0 0 0 2px ${mode === 'dark' ? '#0f172a' : '#fff'}, 0 0 0 4px ${primaryColor}44`
              }}
            >
              {initials || <Person aria-hidden sx={{ fontSize: 18 }} />}
            </Avatar>
            <Box aria-hidden sx={{ display: { xs: 'none', sm: 'block' }, textAlign: 'left' }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 800, lineHeight: 1.2, fontSize: '0.85rem' }}>
                {displayName || 'User'}
              </Typography>
              <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 600, fontSize: '0.75rem', opacity: 0.7 }}>
                {userData.roleName || 'Member'}
              </Typography>
            </Box>
          </ButtonBase>
        </Box>

        <Menu
          id={PROFILE_MENU_ID}
          anchorEl={anchorEl}
          open={Boolean(anchorEl)}
          onClose={handleClose}
          transformOrigin={{ horizontal: 'right', vertical: 'top' }}
          anchorOrigin={{ horizontal: 'right', vertical: 'bottom' }}
          slotProps={{
            list: { 'aria-labelledby': PROFILE_BUTTON_ID },
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

          <MenuItem
            onClick={handleColorMenuOpen}
            aria-haspopup="menu"
            aria-expanded={Boolean(colorMenuAnchor)}
            aria-controls={colorMenuAnchor ? COLOR_MENU_ID : undefined}
            sx={{ borderRadius: 2, py: 1.5 }}
          >
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
          id={COLOR_MENU_ID}
          aria-label="Select brand color"
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
              // The current choice was conveyed only by background colour and
              // font weight (SC 1.4.1). menuitemradio + aria-checked exposes it
              // to assistive tech as a real selected state.
              role="menuitemradio"
              aria-checked={primaryColor === option.color}
              selected={primaryColor === option.color}
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
                aria-hidden
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
                <Box aria-hidden sx={{ width: 6, height: 6, borderRadius: '50%', bgcolor: 'success.main', ml: 1 }} />
              )}
            </MenuItem>
          ))}
        </Menu>
      </Toolbar>
    </AppBar>
  );
};

export default Navbar;

