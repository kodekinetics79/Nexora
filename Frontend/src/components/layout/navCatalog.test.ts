// Vite's `?raw` import, not node:fs — the suite runs with `types: ["vite/client"]` and no node
// types, and this keeps the assertion running against the REAL router source rather than a
// transcription of it that would drift the first time a route moved.
import appSource from '../../App.tsx?raw';
import { describe, expect, it } from 'vitest';
import {
  ADVANCED_ENTRIES,
  ADVANCED_GROUPS,
  ALL_SCREENS_ENTRY,
  PRIMARY_NAV,
  PRIMARY_VIEWS,
  navEntryMatches,
  pathnameOf,
} from './navCatalog';
import { SETUP_ENTRIES } from '../../pages/Setup/setupCatalog';
import { SETUP_ROUTES, SETUP_ADOPTED_ROUTES } from '../../pages/Setup/setupRoutes';

/**
 * The rail went from 17 top-level rows over 69 destinations to 5 rows.
 *
 * The property that matters more than the count is the one this file exists to pin: NOTHING WAS
 * DELETED TO ACHIEVE IT. Every destination the old rail carried is still reachable — as one of the
 * five, as a tab on one of the five, as a Setup entry, or as a card on All screens — and every one
 * of those paths is a live route in `App.tsx`.
 *
 * `OLD_RAIL_DESTINATIONS` below is transcribed from the rail as it stood before this change
 * (Sidebar.tsx @ f69e99f, and the `FULL_RAIL` fixture in the pilotRail test it replaces). It is a
 * frozen historical record, not a description of the current code: if a future change drops one of
 * these screens, the test fails and the person dropping it has to say so out loud.
 */

/** Every literal `path="…"` the router declares, plus the setup paths it maps in as data. */
const declaredRoutes = new Set<string>([
  ...Array.from(appSource.matchAll(/path="([^"]+)"/g)).map((match) => match[1]),
  ...SETUP_ROUTES.map((route) => `/setup/${route.path}`),
  ...SETUP_ADOPTED_ROUTES.map((route) => route.path),
]);

/**
 * Whether the router can serve this pathname, allowing for `:param` segments and for a trailing
 * optional one (`/commercial-cases/:id?` serves `/commercial-cases` as well).
 */
const routeExists = (pathname: string): boolean => {
  if (declaredRoutes.has(pathname)) return true;
  const segments = pathname.split('/');
  return Array.from(declaredRoutes).some((route) => {
    const routeSegments = route.split('/');
    const required = routeSegments.filter((segment) => !segment.endsWith('?'));
    if (segments.length < required.length || segments.length > routeSegments.length) return false;
    return segments.every(
      (segment, index) => routeSegments[index].startsWith(':') || routeSegments[index] === segment,
    );
  });
};

/**
 * The 69 destinations the pre-change rail carried, by the label a rep would have clicked.
 * Value = the path it went to.
 */
const OLD_RAIL_DESTINATIONS: Record<string, string> = {
  'Sales Rep Today': '/sales/today',
  'Sales Manager Control Tower': '/sales/team',
  'Sourcing Today': '/sourcing/today',
  'Inventory Today': '/inventory/today',
  'Executive RFQ-to-Revenue': '/executive/today',
  'Tenant Admin Operations': '/admin/operations',
  'Deadline Board': '/analytics/deadlines',
  'Brand Demand': '/analytics/brand-demand',
  Dashboard: '/dashboard',
  'Team Workload': '/dashboard/team',
  Copilot: '/copilot',
  'Copilot Approvals': '/copilot/approvals',
  'Copilot Activity': '/copilot/activity',
  'Sales Reps': '/sales/reps',
  'Account Ownership': '/sales/accounts',
  'Routing Queue': '/sales/routing',
  'Follow-ups': '/sales/follow-ups',
  Performance: '/sales/performance',
  'Commercial Exceptions': '/sales/exceptions',
  'Human Actions': '/sales/actions',
  'Commercial Memory': '/intelligence/commercial-memory',
  'Team Overview': '/sales/team',
  'All Inquiries': '/procurement/leads/all',
  'Needs Review': '/procurement/extraction/review',
  'Upload Documents': '/procurement/leads/manual-upload',
  'Inbound Mail': '/procurement/leads/inbound-mail',
  'Watched Folders': '/procurement/leads/watched-folders',
  Duplicates: '/procurement/leads/duplicates',
  Revisions: '/procurement/leads/all?view=revisions',
  'Possible Matches': '/procurement/leads/possible-matches',
  'All RFQs': '/procurement/rfqs/all',
  'Draft / Needs Review': '/procurement/rfqs/draft',
  'Ready for Quote': '/procurement/rfqs/all?state=ready-for-quote',
  'Draft Quotes': '/sales/quotes?state=draft',
  'Sent Quotes': '/sales/quotes?state=sent',
  'Follow-up Due': '/sales/quotes?state=follow-up',
  'Won / Lost': '/sales/quotes?state=outcomes',
  'Service BOQs': '/services/boq',
  'Client PO Inbox': '/sales/client-pos',
  'Customer Orders': '/sales/orders',
  'Procurement Handoffs': '/procurement/handoffs',
  'Accounts Receivable': '/sales/finance',
  Shipments: '/sales/shipments',
  Suppliers: '/suppliers',
  'Sourcing Cases': '/procurement/rfqs/all?state=requires-sourcing',
  'Supplier Quote Inbox': '/procurement/supplier-quotes',
  'Commercial Inbox': '/procurement/commercial-inbox',
  'Quoted Items': '/suppliers/quoted-items',
  'Purchase Orders': '/suppliers/purchase-orders',
  Customers: '/customers',
  'Inventory Overview': '/inventory/overview',
  Availability: '/inventory/availability',
  'Inventory Warehouses': '/inventory/warehouses',
  Reservations: '/inventory/reservations',
  Incoming: '/inventory/incoming',
  Movements: '/inventory/movements',
  Demand: '/inventory/demand',
  'Stock Levels': '/inventory/levels',
  'Reorder Alerts': '/inventory/reorder-alerts',
  'Count Variance': '/inventory/count-variance',
  'Stock Ageing': '/inventory/ageing',
  'Related Resources': '/inventory/resources',
  'Lots & Traceability': '/inventory/lots',
  'Where-Used Trace': '/inventory/order-trace',
  Products: '/inventory/products',
  Categories: '/inventory/categories',
  'Sub-Categories': '/inventory/sub-categories',
  Setup: '/setup',
};

/** Every path the new navigation offers, from all four surfaces. */
const reachablePaths = new Set<string>([
  ...PRIMARY_NAV.map((item) => item.path),
  ...PRIMARY_VIEWS.map((view) => view.path),
  ...ADVANCED_ENTRIES.map((entry) => entry.path),
  ...SETUP_ENTRIES.map((entry) => entry.path),
  ALL_SCREENS_ENTRY.path,
]);

describe('the rail was cut to five rows', () => {
  it('carries exactly five primary destinations', () => {
    expect(PRIMARY_NAV).toHaveLength(5);
    expect(PRIMARY_NAV.map((item) => item.key)).toEqual([
      'inbox',
      'leads',
      'rfqs',
      'quotes',
      'setup',
    ]);
  });

  it('opens on the work queue, not on a chooser of modules', () => {
    expect(PRIMARY_NAV[0].path).toBe('/inbox');
  });

  it('accounts for all 69 old destinations across four surfaces', () => {
    // These are the numbers the before/after rests on, so they are asserted rather than counted by
    // hand: 5 rail rows + 15 tabs + 59 directory cards + 25 Setup entries + the All-screens door.
    expect(PRIMARY_NAV).toHaveLength(5);
    expect(PRIMARY_VIEWS).toHaveLength(15);
    expect(ADVANCED_ENTRIES).toHaveLength(59);
    expect(ADVANCED_GROUPS).toHaveLength(9);
    expect(SETUP_ENTRIES).toHaveLength(25);
  });

  it('keeps Lead decision work in Leads and out of the formal RFQ views', () => {
    const leadViews = PRIMARY_NAV.find((item) => item.key === 'leads')?.views ?? [];
    const rfqViews = PRIMARY_NAV.find((item) => item.key === 'rfqs')?.views ?? [];

    expect(rfqViews.map((view) => view.label)).toEqual([
      'All RFQs',
      'Drafts',
      'Ready for quote',
    ]);
    expect(rfqViews.map((view) => view.path)).not.toContain('/procurement/rfqs/outstanding');
    expect(leadViews.find((view) => view.key === 'leads-assigned')).toMatchObject({
      path: '/procurement/leads/assigned',
      activePrefixes: ['/procurement/leads/'],
    });
  });

  it('keeps old Outstanding RFQ bookmarks as a redirect, not a second Lead queue', () => {
    expect(appSource).toContain(
      '<Route path="/procurement/rfqs/outstanding" element={<Navigate to="/procurement/leads/assigned" replace />} />',
    );
    expect(appSource).not.toContain('OutstandingRFQsPage');
  });

  it('gives every primary destination at most ONE level of tabs', () => {
    // A `NavView` has no `views` of its own by type. This asserts the shape stays that way: the
    // moment a view could carry views, the rail has grown a second level again.
    for (const item of PRIMARY_NAV) {
      for (const view of item.views ?? []) {
        expect(Object.keys(view)).not.toContain('views');
      }
    }
  });
});

describe('nothing was deleted to get there', () => {
  it('keeps every one of the 69 destinations the old rail carried', () => {
    const missing = Object.entries(OLD_RAIL_DESTINATIONS)
      .filter(([, path]) => !reachablePaths.has(path))
      .map(([label, path]) => `${label} (${path})`);

    expect(missing).toEqual([]);
  });

  it('still serves every one of those destinations from the router', () => {
    const unrouted = Object.entries(OLD_RAIL_DESTINATIONS)
      .filter(([, path]) => !routeExists(pathnameOf(path)))
      .map(([label, path]) => `${label} (${path})`);

    expect(unrouted).toEqual([]);
  });

  it('lists a real route behind every card on All screens', () => {
    const broken = ADVANCED_ENTRIES.filter((entry) => !routeExists(pathnameOf(entry.path))).map(
      (entry) => `${entry.label} -> ${entry.path}`,
    );

    expect(broken).toEqual([]);
  });

  it('lists a real route behind every primary row and every tab', () => {
    const broken = [
      ...PRIMARY_NAV.map((item) => ({ label: item.label, path: item.path })),
      ...PRIMARY_VIEWS.map((view) => ({ label: view.label, path: view.path })),
      { label: ALL_SCREENS_ENTRY.label, path: ALL_SCREENS_ENTRY.path },
    ]
      .filter(({ path }) => !routeExists(pathnameOf(path)))
      .map(({ label, path }) => `${label} -> ${path}`);

    expect(broken).toEqual([]);
  });
});

describe('one door per destination', () => {
  it('does not list a primary destination on All screens as well', () => {
    // Users and Integration Hub were each in the navigation twice before Setup Master absorbed
    // them. Repeating Leads or Quotes on the directory page would rebuild that defect.
    const primaryPaths = new Set(PRIMARY_NAV.map((item) => item.path));
    const duplicated = ADVANCED_ENTRIES.filter((entry) => primaryPaths.has(entry.path));

    expect(duplicated.map((entry) => entry.path)).toEqual([]);
  });

  it('does not repeat a Setup screen on All screens', () => {
    const setupPaths = new Set(SETUP_ENTRIES.map((entry) => entry.path));
    const duplicated = ADVANCED_ENTRIES.filter((entry) => setupPaths.has(entry.path));

    expect(duplicated.map((entry) => entry.path)).toEqual([]);
  });

  it('has no duplicate key anywhere in the catalogue', () => {
    const keys = [
      ...PRIMARY_NAV.map((item) => item.key),
      ...PRIMARY_VIEWS.map((view) => view.key),
      ...ADVANCED_ENTRIES.map((entry) => entry.key),
    ];

    expect(new Set(keys).size).toBe(keys.length);
  });

  it('addresses each relocated screen exactly once', () => {
    const paths = ADVANCED_ENTRIES.map((entry) => entry.path);
    expect(new Set(paths).size).toBe(paths.length);
  });

  it('may index a screen that is also a tab, because an index is not a second door', () => {
    // Inbound Mail is a tab of the Inbox AND a card here. That is deliberate and is not the
    // duplication the rule forbids: the directory is one destination whose cards are search
    // results, the way Setup Master lists screens its own breadcrumb also reaches. What the rule
    // forbids is a second RAIL ROW for the same screen — asserted by the two tests above.
    const viewPaths = new Set(PRIMARY_VIEWS.map((view) => view.path));
    const indexed = ADVANCED_ENTRIES.filter((entry) => viewPaths.has(entry.path));

    expect(indexed.length).toBeGreaterThan(0);
    // Each of those still carries its own key, so React and the tests can tell them apart.
    for (const entry of indexed) {
      expect(PRIMARY_VIEWS.some((view) => view.key === entry.key)).toBe(false);
    }
  });
});

describe('every entry is written for a person who does not know our words', () => {
  it('describes what each relocated screen decides', () => {
    for (const entry of ADVANCED_ENTRIES) {
      expect(entry.description.length).toBeGreaterThan(20);
      // A description that is the label again teaches nothing.
      expect(entry.description.toLowerCase()).not.toBe(entry.label.toLowerCase());
    }
  });

  it('describes what each primary destination is for', () => {
    for (const item of PRIMARY_NAV) {
      expect(item.description.length).toBeGreaterThan(20);
    }
  });

  it('gives every group a caption saying why its screens sit together', () => {
    for (const group of ADVANCED_GROUPS) {
      expect(group.caption.length).toBeGreaterThan(10);
      expect(group.entries.length).toBeGreaterThan(0);
    }
  });

  it('finds a screen by a word a rep would type rather than our label', () => {
    const find = (query: string) =>
      ADVANCED_ENTRIES.filter((entry) => navEntryMatches(entry, query)).map((entry) => entry.key);

    // "old stock" is Stock Ageing; "vendor" is Suppliers; "chase" is Follow-ups.
    expect(find('old stock')).toContain('inventory-ageing');
    expect(find('vendor')).toContain('suppliers');
    expect(find('chase')).toContain('sales-follow-ups');
  });

  it('matches on every term, not on any of them', () => {
    // A one-term-matches search turns "stock ageing" into every stock screen.
    expect(navEntryMatches(ADVANCED_ENTRIES.find((e) => e.key === 'suppliers')!, 'vendor zzz')).toBe(false);
  });
});
