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
  Stack,
  ClickAwayListener,
  List,
  ListItemButton,
  Chip,
  CircularProgress,
  Alert,
  Dialog,
  DialogContent,
  DialogTitle,
  useMediaQuery,
  useTheme,
} from '@mui/material';
import {
  Menu as MenuIcon,
  Search as SearchIcon,
  LightMode as SunIcon,
  DarkMode as MoonIcon,
  Logout,
  Person,
  Language,
  Close as CloseIcon,
} from '@mui/icons-material';
import { useNavigate } from 'react-router-dom';
import { useAppTheme } from '../../context/ThemeContext';
import { useAuth } from '../../context/AuthContext';
import searchService, {
  ENTITY_LABELS,
  MIN_SEARCH_LENGTH,
  routeForHit,
  type GlobalSearchResponse,
  type SearchEntity,
  type SearchHit,
} from '../../api/services/searchService';
import {
  clearRecentSearchHits,
  loadRecentSearchHits,
  rememberSearchHit,
} from './globalSearchHistory';
import { GLOBAL_SEARCH_FAILURE_MESSAGE, GLOBAL_SEARCH_LABEL } from './globalSearchPresentation';

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
const MOBILE_SEARCH_RESULTS_ID = 'mobile-global-search-results';
const SEARCH_ENTITY_ORDER = Object.keys(ENTITY_LABELS) as SearchEntity[];
const ENTITY_GROUP_LABELS: Record<SearchEntity, string> = {
  customer: 'Customers',
  supplier: 'Suppliers',
  product: 'Products',
  lead: 'Enquiries',
  rfq: 'RFQs',
  quote: 'Quotes',
  order: 'Orders',
  shipment: 'Shipments',
};

const visuallyHidden = {
  position: 'absolute',
  width: 1,
  height: 1,
  p: 0,
  m: -1,
  overflow: 'hidden',
  clip: 'rect(0 0 0 0)',
  whiteSpace: 'nowrap',
  border: 0,
} as const;

interface IndexedSearchHit {
  hit: SearchHit;
  index: number;
}

const groupSearchHits = (hits: SearchHit[]): Array<{ entity: SearchEntity; items: IndexedSearchHit[] }> => {
  const byEntity = new Map<SearchEntity, SearchHit[]>();
  for (const hit of hits) {
    const current = byEntity.get(hit.entity);
    if (current) current.push(hit);
    else byEntity.set(hit.entity, [hit]);
  }

  let index = 0;
  return SEARCH_ENTITY_ORDER.flatMap((entity) => {
    const entityHits = byEntity.get(entity);
    if (!entityHits?.length) return [];
    const items = entityHits.map((hit) => ({ hit, index: index++ }));
    return [{ entity, items }];
  });
};

const optionId = (resultsId: string, hit: SearchHit) =>
  `${resultsId}-${hit.entity}-${hit.id}`;

const Navbar: React.FC<NavbarProps> = ({ onToggleSidebar, drawerWidth, sidebarExpanded, sidebarId }) => {
  const { mode, setMode, primaryColor, setPrimaryColor } = useAppTheme();
  const { userData, logout } = useAuth();
  const navigate = useNavigate();
  const muiTheme = useTheme();
  const desktopSearch = useMediaQuery(muiTheme.breakpoints.up('md'));
  const desktopSearchInputRef = React.useRef<HTMLInputElement>(null);
  const mobileSearchInputRef = React.useRef<HTMLInputElement>(null);
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
  const [mobileSearchOpen, setMobileSearchOpen] = React.useState(false);
  const [activeOptionIndex, setActiveOptionIndex] = React.useState(-1);
  const [recentHits, setRecentHits] = React.useState<SearchHit[]>([]);
  const searchAnchorRef = React.useRef<HTMLDivElement>(null);
  const trimmedQuery = searchValue.trim();
  const historyScope = React.useMemo(() => ({
    businessUnitId: userData.businessUnitId,
    userId: userData.id,
  }), [userData.businessUnitId, userData.id]);
  const showingRecent = trimmedQuery.length === 0;
  const displayedHits = showingRecent ? recentHits : (results?.hits ?? []);
  const groupedHits = React.useMemo(() => groupSearchHits(displayedHits), [displayedHits]);
  const selectableHits = React.useMemo(
    () => groupedHits.flatMap((group) => group.items.map((item) => item.hit)),
    [groupedHits],
  );
  const activeHit = activeOptionIndex >= 0 ? selectableHits[activeOptionIndex] : undefined;

  const refreshRecentHits = React.useCallback(() => {
    const history = loadRecentSearchHits(historyScope);
    setRecentHits(history);
    return history;
  }, [historyScope]);

  React.useEffect(() => {
    refreshRecentHits();
  }, [refreshRecentHits]);

  React.useEffect(() => {
    setActiveOptionIndex(-1);
  }, [trimmedQuery, results, recentHits]);

  React.useEffect(() => {
    if (trimmedQuery.length < MIN_SEARCH_LENGTH) {
      setResults(null);
      setSearchError(null);
      setSearching(false);
      return;
    }

    setResults(null);
    setSearchError(null);
    setSearching(true);
    const controller = new AbortController();
    // Debounced, so a typed word costs one query rather than one per keystroke.
    const timer = window.setTimeout(async () => {
      try {
        const response = await searchService.search({ q: trimmedQuery, limit: 5 }, controller.signal);
        setResults(response);
        setSearchOpen(true);
      } catch (error) {
        if (controller.signal.aborted) return;
        setResults(null);
        // API and gateway details can contain internal infrastructure or authorization data.
        // Keep the failure truthful without exposing those details in a global UI surface.
        setSearchError(GLOBAL_SEARCH_FAILURE_MESSAGE);
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

  const openHit = (hit: SearchHit) => {
    setRecentHits(rememberSearchHit(historyScope, hit));
    setSearchOpen(false);
    setMobileSearchOpen(false);
    setActiveOptionIndex(-1);
    setSearchValue('');
    desktopSearchInputRef.current?.blur();
    mobileSearchInputRef.current?.blur();
    navigate(routeForHit(hit));
  };

  const handleQuickSearch = (e: React.KeyboardEvent<HTMLInputElement>) => {
    const optionCount = selectableHits.length;
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        setSearchOpen(true);
        setActiveOptionIndex((current) => optionCount === 0 ? -1 : current < optionCount - 1 ? current + 1 : 0);
        break;
      case 'ArrowUp':
        e.preventDefault();
        setSearchOpen(true);
        setActiveOptionIndex((current) => optionCount === 0 ? -1 : current > 0 ? current - 1 : optionCount - 1);
        break;
      case 'Home':
        if (optionCount === 0) return;
        e.preventDefault();
        setSearchOpen(true);
        setActiveOptionIndex(0);
        break;
      case 'End':
        if (optionCount === 0) return;
        e.preventDefault();
        setSearchOpen(true);
        setActiveOptionIndex(optionCount - 1);
        break;
      case 'Enter': {
        if (optionCount === 0) return;
        e.preventDefault();
        openHit(selectableHits[activeOptionIndex >= 0 ? activeOptionIndex : 0]);
        break;
      }
      case 'Escape':
        e.preventDefault();
        setSearchOpen(false);
        setActiveOptionIndex(-1);
        if (!desktopSearch) setMobileSearchOpen(false);
        break;
      default:
        break;
    }
  };

  const handleSearchFocus = () => {
    if (showingRecent) refreshRecentHits();
    setSearchOpen(true);
  };

  const clearHistory = () => {
    clearRecentSearchHits(historyScope);
    setRecentHits([]);
    setActiveOptionIndex(-1);
  };

  const openMobileSearch = React.useCallback(() => {
    refreshRecentHits();
    setMobileSearchOpen(true);
    setSearchOpen(true);
  }, [refreshRecentHits]);

  const closeMobileSearch = () => {
    setMobileSearchOpen(false);
    setSearchOpen(false);
    setActiveOptionIndex(-1);
  };

  const setMobileSearchInput = React.useCallback((node: HTMLInputElement | null) => {
    mobileSearchInputRef.current = node;
    if (node && mobileSearchOpen) node.focus();
  }, [mobileSearchOpen]);

  React.useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        if (desktopSearch) {
          setSearchOpen(true);
          desktopSearchInputRef.current?.focus();
        } else {
          openMobileSearch();
        }
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [desktopSearch, openMobileSearch]);

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

  const liveSearchMessage = searching
    ? 'Searching Nexora.'
    : searchError
      ? `Search failed. ${searchError}`
      : trimmedQuery.length >= MIN_SEARCH_LENGTH && results
        ? `${results.hits.length} search ${results.hits.length === 1 ? 'result' : 'results'} shown.`
        : showingRecent && searchOpen
          ? `${recentHits.length} recently opened ${recentHits.length === 1 ? 'record' : 'records'} shown.`
          : '';

  const renderSearchInput = (
    inputRef: React.Ref<HTMLInputElement>,
    resultsId: string,
  ) => (
    <InputBase
      inputRef={inputRef}
      value={searchValue}
      onChange={(event) => {
        setSearchValue(event.target.value);
        setActiveOptionIndex(-1);
      }}
      onKeyDown={handleQuickSearch}
      onFocus={handleSearchFocus}
      placeholder={`${GLOBAL_SEARCH_LABEL}…`}
      type="search"
      inputProps={{
        'aria-label': 'Search customers, suppliers, products, enquiries, quotes, orders and shipments',
        'aria-expanded': searchOpen,
        'aria-controls': resultsId,
        'aria-activedescendant': activeHit ? optionId(resultsId, activeHit) : undefined,
        role: 'combobox',
        'aria-autocomplete': 'list',
      }}
      sx={{
        flex: 1,
        minWidth: 0,
        fontSize: '0.875rem',
        color: 'text.primary',
        '& input::placeholder': { color: 'text.secondary', opacity: 0.8 },
      }}
    />
  );

  const renderSearchPanel = (resultsId: string, mobile = false) => (
    <Paper
      elevation={mobile ? 0 : 8}
      sx={{
        mt: mobile ? 1.5 : 1,
        width: mobile ? '100%' : 460,
        maxWidth: '100%',
        maxHeight: mobile ? 'none' : 480,
        overflowY: 'auto',
        borderRadius: mobile ? 2 : 3,
        border: '1px solid',
        borderColor: 'divider',
      }}
    >
      {showingRecent ? (
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25 }}>
          <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>Recently opened</Typography>
          {recentHits.length > 0 ? (
            <ButtonBase
              onClick={clearHistory}
              sx={{
                color: 'primary.main',
                borderRadius: 1,
                px: 1,
                minHeight: 44,
                fontSize: '0.75rem',
                fontWeight: 700,
              }}
            >
              Clear history
            </ButtonBase>
          ) : null}
        </Stack>
      ) : null}

      {showingRecent && recentHits.length === 0 ? (
        <Typography variant="body2" color="text.secondary" sx={{ px: 2, pb: 2 }}>
          No recently opened records in this session.
        </Typography>
      ) : null}

      {!showingRecent && trimmedQuery.length < MIN_SEARCH_LENGTH ? (
        <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
          Enter at least {MIN_SEARCH_LENGTH} characters to search.
        </Typography>
      ) : null}

      {searchError ? <Alert severity="error" sx={{ borderRadius: 0 }}>{searchError}</Alert> : null}

      {!searchError && searching ? (
        <Stack direction="row" spacing={1} role="status" sx={{ alignItems: 'center', p: 2 }}>
          <CircularProgress size={18} />
          <Typography variant="body2" color="text.secondary">Searching Nexora…</Typography>
        </Stack>
      ) : null}

      {!searchError && !searching && results && results.hits.length === 0 ? (
        <Box sx={{ p: 2 }}>
          <Typography variant="body2" sx={{ fontWeight: 700 }}>
            Nothing matches “{results.query}”.
          </Typography>
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            Searched {results.searchedEntities.length} record types. Nothing was opened.
          </Typography>
        </Box>
      ) : null}

      <Box id={resultsId} role="listbox" aria-label={showingRecent ? 'Recently opened records' : 'Search results'}>
        {!searchError && !searching && groupedHits.map((group) => {
          const headingId = `${resultsId}-${group.entity}-heading`;
          return (
            <Box key={group.entity} role="group" aria-labelledby={headingId} sx={{ borderTop: '1px solid', borderColor: 'divider' }}>
              <Typography id={headingId} variant="caption" sx={{ display: 'block', px: 2, pt: 1.25, pb: 0.5, fontWeight: 900, color: 'text.secondary', textTransform: 'uppercase', letterSpacing: '0.04em' }}>
                {ENTITY_GROUP_LABELS[group.entity]}
              </Typography>
              <List dense disablePadding role="presentation">
                {group.items.map(({ hit, index }) => (
                  <ListItemButton
                    id={optionId(resultsId, hit)}
                    key={`${hit.entity}-${hit.id}`}
                    role="option"
                    aria-selected={activeOptionIndex === index}
                    selected={activeOptionIndex === index}
                    onMouseMove={() => setActiveOptionIndex(index)}
                    onClick={() => openHit(hit)}
                    sx={{ alignItems: 'flex-start', gap: 1, px: 2, minHeight: 44 }}
                  >
                    <Chip size="small" label={ENTITY_LABELS[hit.entity]} sx={{ fontWeight: 700, fontSize: 10, height: 20, mt: 0.25 }} />
                    <Box sx={{ minWidth: 0, flex: 1 }}>
                      <Typography variant="body2" sx={{ fontWeight: 700 }} noWrap>{hit.title}</Typography>
                      <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }} noWrap>
                        {hit.subtitle || 'No secondary reference recorded'}{hit.status ? ` · ${hit.status}` : ''}
                      </Typography>
                      <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>
                        matched on {hit.matchedOn}
                      </Typography>
                    </Box>
                  </ListItemButton>
                ))}
              </List>
            </Box>
          );
        })}
      </Box>

      {!showingRecent && !searchError && !searching && results
        && (results.notes.length > 0 || results.deniedEntities.length > 0 || results.truncated.length > 0) ? (
          <Box sx={{ px: 2, py: 1, borderTop: '1px solid', borderColor: 'divider' }}>
            {results.truncated.length > 0 ? (
              <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary' }}>
                More {results.truncated.map((entity) => ENTITY_LABELS[entity].toLowerCase()).join(', ')} results exist than are shown.
              </Typography>
            ) : null}
            {results.deniedEntities.length > 0 ? (
              <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary' }}>
                Not searched — you do not have access to: {results.deniedEntities.map((entity) => ENTITY_LABELS[entity]).join(', ')}.
              </Typography>
            ) : null}
            {results.notes.map((note) => (
              <Typography key={note} variant="caption" sx={{ display: 'block', color: 'text.secondary' }}>{note}</Typography>
            ))}
          </Box>
        ) : null}
    </Paper>
  );

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
        // The rail is persistent only at the desktop shell breakpoint. Below
        // 1200px it overlays the page, so the top bar always keeps the full
        // viewport width and cannot collide with account/search controls.
        width: { lg: `calc(100% - ${drawerWidth}px)` },
        ml: { lg: `${drawerWidth}px` },
        // Glass over the canvas washes: the page scrolls under it and stays legible above it.
        boxShadow: mode === 'dark'
          ? 'inset 0 1px 0 rgba(255,255,255,0.05), 0 10px 30px -22px rgba(0,0,0,0.9)'
          : 'inset 0 1px 0 rgba(255,255,255,0.9), 0 10px 30px -24px rgba(8,23,42,0.35)',
        backgroundColor: mode === 'dark' ? 'rgba(15, 23, 42, 0.66)' : 'rgba(255, 255, 255, 0.62)',
        backdropFilter: 'blur(18px) saturate(140%)',
        WebkitBackdropFilter: 'blur(18px) saturate(140%)',
        borderBottom: '1px solid',
        borderColor: 'divider',
        color: 'text.primary',
        zIndex: (theme) => theme.zIndex.drawer + 1,
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
            sx={{ mr: { xs: 0.5, sm: 1, md: 2 }, width: 44, height: 44 }}
          >
            <MenuIcon />
          </IconButton>
          {desktopSearch ? (
            <ClickAwayListener onClickAway={() => { setSearchOpen(false); setActiveOptionIndex(-1); }}>
              <Box>
                <Box
                  ref={searchAnchorRef}
                  data-search-field="desktop"
                  sx={{
                    display: 'flex',
                    alignItems: 'center',
                    backgroundColor: mode === 'dark' ? 'rgba(255, 255, 255, 0.05)' : 'rgba(0, 0, 0, 0.03)',
                    px: 2,
                    py: 0.5,
                    borderRadius: 2,
                    width: { md: 260, lg: 320 },
                    border: '1px solid',
                    borderColor: 'divider',
                    '&:focus-within': {
                      outline: '3px solid',
                      outlineColor: 'primary.main',
                      outlineOffset: 2,
                      borderColor: 'primary.main',
                    },
                  }}
                >
                  <SearchIcon aria-hidden sx={{ color: 'text.secondary', mr: 1, fontSize: 18 }} />
                  {renderSearchInput(desktopSearchInputRef, SEARCH_RESULTS_ID)}
                  {searching ? (
                    <CircularProgress size={14} aria-label="Searching" sx={{ ml: 1 }} />
                  ) : (
                    <Box aria-hidden sx={{ ml: 'auto', px: 0.8, py: 0.2, backgroundColor: 'action.hover', borderRadius: 1, border: '1px solid', borderColor: 'divider' }}>
                      <Typography variant="caption" sx={{ fontWeight: 700, fontSize: 10, color: 'text.primary' }}>⌘ K</Typography>
                    </Box>
                  )}
                </Box>
                <Popper open={searchOpen} anchorEl={searchAnchorRef.current} placement="bottom-start" sx={{ zIndex: (theme) => theme.zIndex.modal }}>
                  {renderSearchPanel(SEARCH_RESULTS_ID)}
                </Popper>
              </Box>
            </ClickAwayListener>
          ) : (
            <Tooltip title="Search Nexora">
              <IconButton
                color="inherit"
                onClick={openMobileSearch}
                aria-label="Open global search"
                aria-haspopup="dialog"
                sx={{ width: 44, height: 44 }}
              >
                <SearchIcon />
              </IconButton>
            </Tooltip>
          )}

          <Box role="status" aria-live="polite" aria-atomic="true" sx={visuallyHidden}>
            {liveSearchMessage}
          </Box>

          <Dialog
            fullScreen
            open={mobileSearchOpen}
            onClose={closeMobileSearch}
            disableAutoFocus
            aria-labelledby="mobile-search-title"
            slotProps={{
              transition: { onEntered: () => mobileSearchInputRef.current?.focus() },
            }}
          >
            <DialogTitle id="mobile-search-title" component="div" sx={{ px: 2, py: 1.5 }}>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <Typography variant="h6" component="h2" sx={{ flex: 1, fontWeight: 800 }}>{GLOBAL_SEARCH_LABEL}</Typography>
                <IconButton aria-label="Close search" onClick={closeMobileSearch} sx={{ width: 44, height: 44 }}><CloseIcon /></IconButton>
              </Stack>
            </DialogTitle>
            <DialogContent dividers sx={{ p: 1.5 }}>
              <Box
                data-search-field="mobile"
                sx={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 1,
                  px: 1.5,
                  py: 1,
                  border: '1px solid',
                  borderColor: 'divider',
                  borderRadius: 2,
                  bgcolor: 'action.hover',
                  '&:focus-within': {
                    outline: '3px solid',
                    outlineColor: 'primary.main',
                    outlineOffset: 2,
                    borderColor: 'primary.main',
                  },
                }}
              >
                <SearchIcon aria-hidden color="action" />
                {renderSearchInput(setMobileSearchInput, MOBILE_SEARCH_RESULTS_ID)}
                {searching ? <CircularProgress size={18} aria-label="Searching" /> : null}
              </Box>
              {searchOpen ? renderSearchPanel(MOBILE_SEARCH_RESULTS_ID, true) : null}
            </DialogContent>
          </Dialog>
        </Box>

        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
          {/* FE-10: language switcher hidden for the pilot — page bodies are not
              fully translated, so switching languages would produce a mixed
              English/localized UI. The app is locked to English (see i18n.ts). */}
          {/* A string Tooltip title becomes the child's aria-label in MUI, so
              making it state-specific also fixes the accessible name (SC 4.1.2). */}
          <Tooltip title={mode === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}>
            <IconButton color="inherit" onClick={() => setMode(mode === 'dark' ? 'light' : 'dark')} sx={{ backgroundColor: 'action.hover', width: 44, height: 44, borderRadius: 2 }}>
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
            <Box aria-hidden sx={{ display: { xs: 'none', lg: 'block' }, textAlign: 'left' }}>
              <Typography noWrap variant="subtitle2" sx={{ fontWeight: 800, lineHeight: 1.2, fontSize: '0.85rem' }}>
                {displayName || 'User'}
              </Typography>
              <Typography noWrap variant="caption" sx={{ color: 'text.secondary', fontWeight: 600, fontSize: '0.75rem', opacity: 0.7 }}>
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
              primary="Workspace home"
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

          <MenuItem onClick={() => { clearHistory(); handleClose(); logout(); }} sx={{ borderRadius: 2, py: 1.5, color: 'error.main' }}>
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
