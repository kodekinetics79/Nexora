import React, { useMemo } from 'react';
import { Tabs, Tab } from '@mui/material';
import { useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '../../context/AuthContext';
import { PRIMARY_NAV, pathnameOf, type NavView } from './navCatalog';

/**
 * The one level of tabs a primary destination is allowed.
 *
 * The rail used to carry these: "Draft Quotes", "Sent Quotes", "Follow-up Due" and "Won / Lost"
 * were four rail rows pointing at one screen with four query strings, and "All RFQs / Drafts /
 * Ready for quote" were three rows a rep reads as views of one formal-RFQ list. A row per filter is
 * how a twelve-row rail becomes a sixty-nine-destination one — and it hides the fact that they are
 * views OF something rather than places of their own.
 *
 * So the filter lives on the screen, as tabs, and the rail carries the screen. The tabs navigate
 * (they are addresses, so a bookmark and the back button keep working) and they are ONE level:
 * a tab panel here never contains another tab strip.
 */

/** Splits an entry's own query off so it can be compared against the address bar. */
const splitPath = (path: string): { pathname: string; params: [string, string][] } => {
  const cut = path.indexOf('?');
  if (cut < 0) return { pathname: path, params: [] };
  return {
    pathname: path.slice(0, cut),
    params: Array.from(new URLSearchParams(path.slice(cut + 1)).entries()),
  };
};

/**
 * Which view an address is on.
 *
 * A bare view and a filtered view can share a pathname — "All RFQs" and "Ready for quote" are both
 * `/procurement/rfqs/all`. Lighting the bare tab on the filtered address tells the rep they are
 * looking at everything while the grid shows a subset, so the bare tab stands down whenever the
 * address carries a filter key one of its siblings claims.
 */
export const activeViewKey = (
  views: readonly NavView[],
  pathname: string,
  search: string,
): string | undefined => {
  const current = new URLSearchParams(search);

  const claimedKeys = new Map<string, Set<string>>();
  for (const view of views) {
    const { pathname: viewPath, params } = splitPath(view.path);
    if (params.length === 0) continue;
    const set = claimedKeys.get(viewPath) ?? new Set<string>();
    params.forEach(([key]) => set.add(key));
    claimedKeys.set(viewPath, set);
  }

  // A filtered view wins over its bare sibling, so look for one first.
  const filtered = views.find((view) => {
    const { pathname: viewPath, params } = splitPath(view.path);
    if (params.length === 0 || viewPath !== pathname) return false;
    return params.every(([key, value]) => current.get(key) === value);
  });
  if (filtered) return filtered.key;

  const bare = views.find((view) => {
    const { pathname: viewPath, params } = splitPath(view.path);
    if (params.length > 0 || viewPath !== pathname) return false;
    const claimed = claimedKeys.get(viewPath);
    const showingASubset = claimed ? Array.from(claimed).some((key) => !!current.get(key)) : false;
    return !showingASubset;
  });
  if (bare) return bare.key;

  // Detail pages (a quote, an RFQ, an ingestion batch) belong to the list they were opened from.
  return views.find((view) => view.activePrefixes?.some((prefix) => pathname.startsWith(prefix)))?.key;
};

interface ViewTabsProps {
  /** The primary destination whose views to render — `PRIMARY_NAV[].key`. */
  primaryKey: string;
  /** Screen-reader name for the strip. Defaults to the destination's label. */
  ariaLabel?: string;
}

/**
 * Renders the tab strip for one primary destination, filtered to the views this user may open.
 *
 * Renders nothing when fewer than two views survive the permission filter — a single tab is a
 * label pretending to be a control.
 */
const ViewTabs: React.FC<ViewTabsProps> = ({ primaryKey, ariaLabel }) => {
  const navigate = useNavigate();
  const location = useLocation();
  const { hasPermission } = useAuth();

  const item = PRIMARY_NAV.find((entry) => entry.key === primaryKey);

  const views = useMemo(
    () => (item?.views ?? []).filter((view) => !view.moduleName || hasPermission(view.moduleName)),
    [item, hasPermission],
  );

  if (views.length < 2) return null;

  const active = activeViewKey(views, location.pathname, location.search);

  return (
    <Tabs
      value={active ?? false}
      onChange={(_event, key: string) => {
        const view = views.find((candidate) => candidate.key === key);
        if (view) navigate(view.path);
      }}
      variant="scrollable"
      scrollButtons="auto"
      allowScrollButtonsMobile
      aria-label={ariaLabel ?? `${item?.label ?? ''} views`}
      sx={{ borderBottom: '1px solid', borderColor: 'divider', mb: 2, minHeight: 44 }}
    >
      {views.map((view) => (
        <Tab
          key={view.key}
          value={view.key}
          label={view.label}
          sx={{ textTransform: 'none', fontWeight: 700, minHeight: 44 }}
        />
      ))}
    </Tabs>
  );
};

export { pathnameOf };
export default ViewTabs;
