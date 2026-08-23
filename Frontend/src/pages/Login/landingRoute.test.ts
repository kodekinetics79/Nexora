import { describe, expect, it } from 'vitest';
import { landingRouteFor } from './LoginPage';

/**
 * Where a user lands after signing in.
 *
 * Login used to call `navigate('/analytics/deadlines')` unconditionally. That route is gated on
 * the Leads module, and TenantBaselineCatalog grants Leads to only two of the four seeded starter
 * roles. A pilot tenant's Procurement Officer and Finance Officer therefore signed in
 * successfully and landed on "Access Denied" as their first screen — while both hold Read on
 * Dashboard, so a working destination existed the whole time.
 *
 * The fixtures below are the actual seeded roles, not invented ones.
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

describe('post-login landing', () => {
  it('keeps the deadline board for roles that can actually open it', () => {
    expect(landingRouteFor(false, SALES_REP)).toBe('/analytics/deadlines');
    expect(landingRouteFor(false, SALES_MANAGER)).toBe('/analytics/deadlines');
  });

  it('does not strand a Procurement Officer on Access Denied', () => {
    const landing = landingRouteFor(false, PROCUREMENT_OFFICER);
    expect(landing).not.toBe('/analytics/deadlines');
    expect(landing).toBe('/dashboard');
  });

  it('does not strand a Finance Officer on Access Denied', () => {
    const landing = landingRouteFor(false, FINANCE_OFFICER);
    expect(landing).not.toBe('/analytics/deadlines');
    expect(landing).toBe('/dashboard');
  });

  it('falls through to a module the user does hold when the earlier ones are absent', () => {
    expect(landingRouteFor(false, [view('Suppliers')])).toBe('/suppliers');
    expect(landingRouteFor(false, [view('Quotations')])).toBe('/sales/quotes');
  });

  it('treats a permission row without canView as no access', () => {
    // A revoke-everything row exists but grants nothing; read is an explicit grant.
    const revoked = [{ moduleName: 'Leads', canView: false }, view('Dashboard')];
    expect(landingRouteFor(false, revoked)).toBe('/dashboard');
  });

  it('sends a user with no view grant anywhere somewhere that names the missing grant', () => {
    // PermissionGuard on this route says which grant to ask for, which is the right answer
    // to "I have no access at all" — better than a blank screen.
    expect(landingRouteFor(false, [])).toBe('/analytics/deadlines');
  });

  it('lands a super administrator on the work queue', () => {
    expect(landingRouteFor(true, [])).toBe('/analytics/deadlines');
  });

  it('is case- and whitespace-insensitive about module names', () => {
    expect(landingRouteFor(false, [{ moduleName: '  dashboard ', canView: true }])).toBe('/dashboard');
  });
});
