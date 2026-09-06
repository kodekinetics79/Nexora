import { describe, expect, it } from 'vitest';
import { EXECUTIVE_LANDING_ROUTE, landingRouteFor } from './LoginPage';
import { INBOX_ROOT } from '../../components/layout/navCatalog';

/**
 * Where a user lands after signing in.
 *
 * Two earlier answers to this question both failed, and the fixtures below are why.
 *
 * Login first called `navigate('/analytics/deadlines')` unconditionally. That route is gated on
 * the Leads module, and `TenantBaselineCatalog` grants Leads to only two of the four seeded
 * starter roles — so a pilot tenant's Procurement Officer and Finance Officer signed in
 * successfully and landed on "Access Denied" as their first screen.
 *
 * The second answer was a fallback table: seven candidate destinations, each with its own module
 * gate, taking the first the user could open. Nobody landed on a denial any more, but four roles
 * got four different first screens, and every one of the seven could only show work that had
 * already become a lead.
 *
 * The answer now is two screens, chosen by TIER rather than by module. Everyone who works deals
 * lands on `/inbox`, which is module-agnostic and asks for each of its queues separately.
 * Manager-tier users (manager, admin by rank, owner) land on the executive view, and only when
 * its Dashboard gate will open for them — so the one thing that must never come back, a first
 * screen the user cannot open, still cannot.
 *
 * The fixtures are the actual seeded roles, not invented ones.
 */

const view = (moduleName: string) => ({ moduleName, canView: true });

// As seeded by Platform/Services/TenantBaselineCatalog.cs.
const SALES_REP = [view('Leads'), view('Quotations'), view('Customers')];
const SALES_MANAGER = [view('Leads'), view('Dashboard'), view('Quotations')];
// Transcribed verbatim from the catalog: Read()/Work()/Own() all set canView = true.
const PROCUREMENT_OFFICER = [
  view('Dashboard'), view('Suppliers'), view('Supplier History'), view('Products'),
  view('Product Categories'), view('Orders'), view('Currencies'), view('Exchange Rates'),
];
const FINANCE_OFFICER = [
  view('Dashboard'), view('Customers'), view('Orders'), view('Accounts Receivable'),
  view('Customer Payments'), view('Bank Accounts'),
];

const rep = (permissions: ReturnType<typeof view>[]) => ({ permissions });
const manager = (permissions: ReturnType<typeof view>[]) => ({ isManager: true, permissions });

describe('post-login landing', () => {
  it('sends every non-manager seeded role to the same work queue', () => {
    for (const role of [SALES_REP, SALES_MANAGER, PROCUREMENT_OFFICER, FINANCE_OFFICER]) {
      expect(landingRouteFor(rep(role))).toBe(INBOX_ROOT);
    }
  });

  it('does not strand a Procurement Officer or a Finance Officer on Access Denied', () => {
    // The original defect, kept as a named test so it cannot come back quietly. A Dashboard grant
    // alone is not a reason to skip the work queue: these officers work deals, they do not read
    // the board.
    expect(landingRouteFor(rep(PROCUREMENT_OFFICER))).not.toBe('/analytics/deadlines');
    expect(landingRouteFor(rep(FINANCE_OFFICER))).not.toBe('/analytics/deadlines');
    expect(landingRouteFor(rep(PROCUREMENT_OFFICER))).toBe(INBOX_ROOT);
  });

  it('lands a user with no view grant anywhere on a screen that explains itself', () => {
    // After authentication, `/inbox` tells this person which modules they are missing and where to
    // ask for them, which is the right answer to "I have no module access at all".
    expect(landingRouteFor(rep([]))).toBe(INBOX_ROOT);
  });

  it('lands a sales manager on the executive view', () => {
    expect(landingRouteFor(manager(SALES_MANAGER))).toBe(EXECUTIVE_LANDING_ROUTE);
  });

  it('lands the owner and an administrator by rank on the executive view without a permission row', () => {
    // `hasPermission` grants these tiers every module outright, so the Dashboard gate opens.
    expect(landingRouteFor({ isSuperAdmin: true, permissions: [] })).toBe(EXECUTIVE_LANDING_ROUTE);
    expect(landingRouteFor({ hasModuleAuthorityByRank: true, permissions: [] })).toBe(EXECUTIVE_LANDING_ROUTE);
  });

  it('never sends a manager to a screen the Dashboard gate would refuse', () => {
    // A manager whose role lost its Dashboard grant, or holds an all-false row, stays on the Inbox.
    expect(landingRouteFor(manager([view('Leads'), view('Quotations')]))).toBe(INBOX_ROOT);
    expect(landingRouteFor(manager([{ moduleName: 'Dashboard', canView: false }]))).toBe(INBOX_ROOT);
    expect(landingRouteFor(manager([]))).toBe(INBOX_ROOT);
  });

  it('matches the Dashboard grant the way the permission check does', () => {
    // `hasPermission` trims and lower-cases the module name; the gate would open, so the landing
    // must agree with it rather than second-guess the payload's casing.
    expect(landingRouteFor(manager([{ moduleName: '  dashboard ', canView: true }]))).toBe(EXECUTIVE_LANDING_ROUTE);
  });
});
