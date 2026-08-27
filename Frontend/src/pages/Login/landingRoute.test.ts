import { describe, expect, it } from 'vitest';
import { landingRouteFor } from './LoginPage';
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
 * already become a lead — not a document that had just arrived, not a supplier that had just
 * replied, not a quote waiting to be sent.
 *
 * The answer now is one authenticated, module-agnostic screen for everybody: `/inbox`, which asks
 * for each of its queues separately. The permission problem cannot recur, because there is nothing
 * left to branch on; the route's auth gate independently keeps the tenant shell signed-in only.
 *
 * The fixtures are the actual seeded roles, not invented ones. They are kept because they are the
 * evidence that this is not a regression: each of them must reach a screen that renders.
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
  it('sends every seeded role to the same work queue', () => {
    for (const role of [SALES_REP, SALES_MANAGER, PROCUREMENT_OFFICER, FINANCE_OFFICER]) {
      expect(landingRouteFor(false, role)).toBe(INBOX_ROOT);
    }
  });

  it('does not strand a Procurement Officer or a Finance Officer on Access Denied', () => {
    // The original defect, kept as a named test so it cannot come back quietly.
    expect(landingRouteFor(false, PROCUREMENT_OFFICER)).not.toBe('/analytics/deadlines');
    expect(landingRouteFor(false, FINANCE_OFFICER)).not.toBe('/analytics/deadlines');
  });

  it('lands a user with no view grant anywhere on a screen that explains itself', () => {
    // After authentication, `/inbox` tells this person which modules they are missing and where to
    // ask for them, which is the right answer to "I have no module access at all".
    expect(landingRouteFor(false, [])).toBe(INBOX_ROOT);
  });

  it('lands a super administrator on the same queue as everybody else', () => {
    expect(landingRouteFor(true, [])).toBe(INBOX_ROOT);
  });

  it('cannot be steered by the permission payload at all', () => {
    // The whole point: no branch, so no shape of permissions — revoked, mis-cased, empty — can
    // produce a first screen the user is not allowed to open.
    const revoked = [{ moduleName: 'Leads', canView: false }];
    expect(landingRouteFor(false, revoked)).toBe(INBOX_ROOT);
    expect(landingRouteFor(false, [{ moduleName: '  dashboard ', canView: true }])).toBe(INBOX_ROOT);
  });
});
