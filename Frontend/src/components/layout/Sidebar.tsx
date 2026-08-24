import React, { useState, useMemo, useId } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import {
  Box,
  Divider,
  List,
  ListItem,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Collapse,
  Tooltip,
  Typography,
} from '@mui/material';
import { alpha } from '@mui/material/styles';
import { useTranslation } from 'react-i18next';
import {
  ExpandLess,
  ExpandMore,
  FiberManualRecord as BulletIcon,
} from '@mui/icons-material';
import { useAuth } from '../../context/AuthContext';
import { SETUP_ENTRIES } from '../../pages/Setup/setupCatalog';
import {
  ADVANCED_GROUPS,
  ALL_SCREENS_ENTRY,
  PRIMARY_NAV,
  navEntryLabel,
  type NavEntry,
  type PrimaryNavItem,
} from './navCatalog';

interface SidebarProps {
  collapsed: boolean;
  onNavigate?: () => void;
}

/**
 * The rail.
 *
 * It used to be 17 top-level rows over 69 destinations — roughly 1,095px of rail against 836px of
 * laptop viewport, so Customers, Inventory and Setup sat below the fold, and the word "RFQ" first
 * appeared at row six behind Today, Dashboard and Copilot. The pilot answered that with a second
 * hard-coded allow-list that hid 50 of the 69, which left two lists disagreeing about one rail.
 *
 * It is now FIVE rows, read from `navCatalog.tsx` and nothing else:
 *
 *     Inbox · Leads · RFQs · Quotes · Setup
 *
 * Those are the five nouns a rep uses to describe their own job, in the order the work moves
 * through them. Nothing was deleted to get there. Every other screen keeps its route, its
 * permissions, its title and its deep links, and is listed with a description on **All screens**
 * (`/advanced`) — the last row here, kept visually apart because it is a door, not a job. Filters
 * that used to be rail rows ("Sent Quotes", "Draft RFQs") are now tabs on the screen they filter,
 * so the rail names places and the screen names views.
 *
 * The rail has no expand/collapse for the five: a group row that must be opened before its
 * children exist is a second navigation level, and the second level is what made the old rail
 * unscannable.
 */

/**
 * Restores the pre-2026-08 rail for one tenant, granted on that tenant's Modules screen in the
 * platform console — audited, reason-required, no deploy.
 *
 * When granted, the nine relocated groups are rendered inline beneath the five as collapsible
 * rows: the same surface the tenant had before, for a customer who asks for it back. Absence — not
 * granted, bootstrap still loading, platform plane unreadable, or an older server that never
 * reports entitlements — always lands on the five-row rail: the floor, from which every screen
 * stays reachable through All screens, by URL, by deep link and by global search.
 */
export const FULL_NAVIGATION_ENTITLEMENT = 'capability.full-navigation';

interface RailGroup {
  key: string;
  title: string;
  entries: NavEntry[];
}

/** Splits an entry's own query off so it can be compared against the address bar. */
const splitNavPath = (path: string): { pathname: string; params: [string, string][] } => {
  const cut = path.indexOf('?');
  if (cut < 0) return { pathname: path, params: [] };
  return {
    pathname: path.slice(0, cut),
    params: Array.from(new URLSearchParams(path.slice(cut + 1)).entries()),
  };
};

/**
 * Whether an address counts as "on" a rail destination.
 *
 * Query strings matter: seven of the relocated entries address a FILTERED view through one
 * (`?state=requires-sourcing`, `?view=revisions`), and `location.pathname` never carries a query,
 * so a plain path comparison could never light them.
 */
const isPathMatched = (
  path: string,
  location: { pathname: string; search: string },
  prefixes?: string[],
): boolean => {
  const { pathname, params } = splitNavPath(path);
  const pathnameMatches =
    location.pathname === pathname ||
    // The legacy `/procurement`-less aliases are still live routes and still in bookmarks.
    (pathname.startsWith('/procurement') && location.pathname === pathname.replace('/procurement', ''));
  if (pathnameMatches) {
    const current = new URLSearchParams(location.search);
    if (params.length === 0) return true;
    if (params.every(([key, value]) => current.get(key) === value)) return true;
  }
  return prefixes?.some((prefix) => location.pathname.startsWith(prefix)) ?? false;
};

/**
 * Whether a primary row is "here".
 *
 * A primary row owns its own path, every one of its views' paths, and any extra prefixes it
 * declares (a quote detail belongs to Quotes). Unlike the old rail, a filtered view does NOT stand
 * its parent down — the parent is the place and the tab strip on the page says which slice is
 * showing, so there is nowhere for the highlight to go missing.
 */
export const isPrimaryActive = (
  item: PrimaryNavItem,
  location: { pathname: string; search: string },
): boolean => {
  if (isPathMatched(item.path, location, item.activePrefixes)) return true;
  return (item.views ?? []).some((view) => isPathMatched(view.path, location, view.activePrefixes));
};

const Sidebar: React.FC<SidebarProps> = ({ collapsed, onNavigate }) => {
  const navigate = useNavigate();
  const location = useLocation();
  const { t } = useTranslation();
  const { userData, hasPermission, hasEntitlement } = useAuth();
  const isManager = userData.isManager === true;
  // Two Sidebars are mounted at once (mobile drawer + permanent drawer), so aria-controls targets
  // must be unique per instance.
  const instanceId = useId();

  const [openGroups, setOpenGroups] = useState<Record<string, boolean>>({});

  const navigateTo = (path: string) => {
    navigate(path);
    onNavigate?.();
  };

  /**
   * Setup keeps its rail row when the user can open at least one screen inside it. The rule reads
   * from the setup catalogue — one register, no second list of setup screens to forget to update.
   */
  const setupIsReachable = useMemo(
    () => SETUP_ENTRIES.some((entry) => !entry.moduleName || hasPermission(entry.moduleName)),
    [hasPermission],
  );

  const primaryRows = useMemo(
    () =>
      PRIMARY_NAV.filter((item) => {
        if (item.key === 'setup') return setupIsReachable;
        return !item.moduleName || hasPermission(item.moduleName);
      }),
    [hasPermission, setupIsReachable],
  );

  /**
   * Only rendered for a tenant granted the full-navigation entitlement. Permission filtering runs
   * first and decides what a user MAY open; the entitlement only decides what the rail SHOWS.
   */
  const advancedRows: RailGroup[] = useMemo(() => {
    if (!hasEntitlement(FULL_NAVIGATION_ENTITLEMENT)) return [];
    return ADVANCED_GROUPS.map((group) => ({
      key: group.key,
      title: group.title,
      entries: group.entries.filter(
        (entry) =>
          (!entry.managerOnly || isManager) && (!entry.moduleName || hasPermission(entry.moduleName)),
      ),
    })).filter((group) => group.entries.length > 0);
  }, [hasEntitlement, hasPermission, isManager]);

  const rowSx = (isSelected: boolean) => ({
    minHeight: 44,
    justifyContent: collapsed ? 'center' : 'initial',
    px: 2,
    borderRadius: '10px',
    color: 'text.primary',
    '&:hover': { backgroundColor: 'action.hover', transform: 'translateX(4px)' },
    // `&.Mui-selected` keeps our colours ahead of MUI's default selected styling in the cascade.
    '&.Mui-selected': {
      backgroundColor: 'primary.main',
      color: 'primary.contrastText',
      boxShadow: (theme: any) => `0 10px 15px -3px ${alpha(theme.palette.primary.main, 0.3)}`,
    },
    '&.Mui-selected:hover': {
      backgroundColor: 'primary.dark',
      color: 'primary.contrastText',
      transform: 'translateX(4px)',
    },
    '&.Mui-focusVisible': {
      outline: (theme: any) => `3px solid ${theme.palette.primary.main}`,
      outlineOffset: 2,
    },
    transition: 'all 0.2s cubic-bezier(0.4, 0, 0.2, 1)',
    opacity: isSelected ? 1 : undefined,
  });

  const renderLeafRow = (
    key: string,
    label: string,
    icon: React.ReactNode,
    path: string,
    isSelected: boolean,
    description?: string,
  ) => (
    <ListItem key={key} disablePadding sx={{ display: 'block', mb: 0.5 }}>
      {/* Collapsed, the label is not rendered — the tooltip carries the name. Expanded, it carries
          the one-line description, which is the only place the rail can say what a row is for. */}
      <Tooltip title={collapsed ? label : (description ?? '')} placement="right">
        <ListItemButton
          onClick={() => navigateTo(path)}
          selected={isSelected}
          // Selected state was previously conveyed by background colour alone (SC 1.4.1 / 4.1.2).
          aria-current={isSelected ? 'page' : undefined}
          // Collapsed leaves an icon-only control — give it an explicit name (SC 4.1.2).
          aria-label={collapsed ? label : undefined}
          sx={rowSx(isSelected)}
        >
          <ListItemIcon
            sx={{
              minWidth: 0,
              mr: collapsed ? 0 : 1.5,
              justifyContent: 'center',
              color: 'inherit',
              opacity: isSelected ? 1 : 0.7,
            }}
          >
            {React.cloneElement(icon as React.ReactElement<any>, { sx: { fontSize: 20 } })}
          </ListItemIcon>
          {!collapsed && (
            <ListItemText
              primary={label}
              slotProps={{ primary: { sx: { fontSize: '0.9rem', fontWeight: isSelected ? 700 : 600 } } }}
            />
          )}
        </ListItemButton>
      </Tooltip>
    </ListItem>
  );

  const renderAdvancedGroup = (group: RailGroup) => {
    const isOpen = openGroups[group.key] === true;
    const isSelected = group.entries.some((entry) => isPathMatched(entry.path, location));
    // The child list only exists in the DOM while expanded (unmountOnExit) and is never rendered
    // while the rail is collapsed — only advertise aria-expanded/aria-controls for a real region.
    const hasCollapsibleGroup = !collapsed;
    const groupListId = `${instanceId}-group-${group.key}`;

    return (
      <React.Fragment key={group.key}>
        <ListItem disablePadding sx={{ display: 'block', mb: 0.5 }}>
          <Tooltip title={collapsed ? group.title : ''} placement="right">
            <ListItemButton
              onClick={() => setOpenGroups((prev) => ({ ...prev, [group.key]: !prev[group.key] }))}
              selected={isSelected}
              aria-expanded={hasCollapsibleGroup ? isOpen : undefined}
              aria-controls={hasCollapsibleGroup && isOpen ? groupListId : undefined}
              aria-label={collapsed ? group.title : undefined}
              sx={rowSx(isSelected)}
            >
              <ListItemIcon
                sx={{
                  minWidth: 0,
                  mr: collapsed ? 0 : 1.5,
                  justifyContent: 'center',
                  color: 'inherit',
                  opacity: isSelected ? 1 : 0.7,
                }}
              >
                {React.cloneElement(group.entries[0].icon as React.ReactElement<any>, {
                  sx: { fontSize: 20 },
                })}
              </ListItemIcon>
              {!collapsed && (
                <>
                  <ListItemText
                    primary={group.title}
                    slotProps={{ primary: { sx: { fontSize: '0.875rem', fontWeight: isSelected ? 600 : 500 } } }}
                  />
                  {isOpen ? <ExpandLess /> : <ExpandMore />}
                </>
              )}
            </ListItemButton>
          </Tooltip>
        </ListItem>

        {hasCollapsibleGroup && (
          <Collapse in={isOpen} timeout="auto" unmountOnExit>
            <List component="div" disablePadding id={groupListId} aria-label={group.title}>
              {group.entries.map((entry) => {
                const isChildSelected = isPathMatched(entry.path, location);
                return (
                  <ListItemButton
                    key={entry.key}
                    onClick={() => navigateTo(entry.path)}
                    selected={isChildSelected}
                    aria-current={isChildSelected ? 'page' : undefined}
                    sx={{
                      minHeight: 40,
                      pl: 4,
                      pr: 2,
                      mx: 1,
                      mb: 0.2,
                      borderRadius: 1.5,
                      color: 'text.secondary',
                      '&:hover': { backgroundColor: 'action.hover' },
                      // A selected child used primary.main as 0.8rem text, which drops under 4.5:1
                      // for the lighter brand colours (SC 1.4.3). Body colour plus weight, a tinted
                      // background and an accent bar instead.
                      '&.Mui-selected': {
                        color: 'text.primary',
                        backgroundColor: (theme) => alpha(theme.palette.primary.main, 0.14),
                        boxShadow: (theme) => `inset 3px 0 0 0 ${theme.palette.primary.main}`,
                      },
                      '&.Mui-selected:hover': {
                        backgroundColor: (theme) => alpha(theme.palette.primary.main, 0.22),
                      },
                      '&.Mui-focusVisible': {
                        outline: (theme) => `3px solid ${theme.palette.primary.main}`,
                        outlineOffset: -1,
                      },
                    }}
                  >
                    <ListItemIcon sx={{ minWidth: 24, color: 'inherit' }}>
                      <BulletIcon sx={{ fontSize: 6 }} />
                    </ListItemIcon>
                    <ListItemText
                      primary={navEntryLabel(entry, t)}
                      slotProps={{ primary: { sx: { fontSize: '0.8rem', fontWeight: isChildSelected ? 600 : 400 } } }}
                    />
                  </ListItemButton>
                );
              })}
            </List>
          </Collapse>
        )}
      </React.Fragment>
    );
  };

  const allScreensSelected = location.pathname.startsWith(ALL_SCREENS_ENTRY.path);

  return (
    <Box sx={{ overflowY: 'auto', overflowX: 'hidden', height: '100%', pt: 2, pb: 4, px: 1.5 }}>
      <List sx={{ px: 0 }}>
        {primaryRows.map((item) =>
          renderLeafRow(
            item.key,
            navEntryLabel(item, t),
            item.icon,
            item.path,
            isPrimaryActive(item, location),
            item.description,
          ),
        )}
      </List>

      {advancedRows.length > 0 && (
        <>
          <Divider sx={{ my: 1.5 }} />
          {!collapsed && (
            <Typography
              variant="caption"
              sx={{
                px: 2,
                pb: 0.5,
                display: 'block',
                fontWeight: 800,
                letterSpacing: '0.06em',
                textTransform: 'uppercase',
                color: 'text.disabled',
              }}
            >
              Advanced
            </Typography>
          )}
          <List sx={{ px: 0 }}>{advancedRows.map(renderAdvancedGroup)}</List>
        </>
      )}

      <Divider sx={{ my: 1.5 }} />
      <List sx={{ px: 0 }}>
        {renderLeafRow(
          ALL_SCREENS_ENTRY.key,
          ALL_SCREENS_ENTRY.label,
          ALL_SCREENS_ENTRY.icon,
          ALL_SCREENS_ENTRY.path,
          allScreensSelected,
          ALL_SCREENS_ENTRY.description,
        )}
      </List>
    </Box>
  );
};

export default Sidebar;
