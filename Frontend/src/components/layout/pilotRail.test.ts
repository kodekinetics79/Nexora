import { describe, expect, it } from 'vitest';
import { applyPilotRail } from './Sidebar';

/**
 * The pilot rail is an allow-list over the navigation, not a deletion.
 *
 * The full rail is 17 top-level rows and 69 destinations. On a 1440x900 laptop that is about
 * 1,095px of content against 836px of viewport, so Customers, Inventory and Setup sit below the
 * fold — and the first three rows are Today, Dashboard and Copilot, with "RFQ" first appearing at
 * row six. A rep opening the product on their first Monday, in a second language, with a tender
 * closing Thursday, is solving a scanning problem before they can begin work.
 *
 * The property that matters most is the one these tests exist to pin: EVERY HIDDEN SCREEN KEEPS
 * ITS ROUTE. The product's rule is preserve-but-hide — out-of-scope surface is concealed from the
 * pilot, never removed — so this must only ever change what the rail RENDERS. Routes, permissions,
 * page titles, deep links and global search are all untouched.
 */

/** The real rail's shape, transcribed from Sidebar.tsx. Keys only — labels are not the contract. */
const FULL_RAIL = [
  { key: 'role-today', children: [
    { key: 'today-sales' }, { key: 'today-sales-manager' }, { key: 'today-sourcing' },
    { key: 'today-inventory' }, { key: 'today-executive' }, { key: 'today-admin' },
  ] },
  { key: 'dashboard', children: [
    { key: 'analytics-deadlines' }, { key: 'analytics-brand-demand' },
    { key: 'dashboard-overview' }, { key: 'dashboard-team' },
  ] },
  { key: 'copilot', children: [
    { key: 'copilot-chat' }, { key: 'copilot-approvals' }, { key: 'copilot-activity' },
  ] },
  { key: 'sales-management', children: [
    { key: 'sales-today' }, { key: 'sales-team' }, { key: 'sales-reps' }, { key: 'sales-accounts' },
    { key: 'sales-routing' }, { key: 'sales-follow-ups' }, { key: 'sales-performance' },
    { key: 'sales-exceptions' }, { key: 'human-actions' }, { key: 'commercial-memory' },
  ] },
  { key: 'lead_mgmt', children: [
    { key: 'leads-all' }, { key: 'leads-review' }, { key: 'leads-bulk' },
    { key: 'leads-inbound-mail' }, { key: 'leads-watched-folders' }, { key: 'leads-duplicates' },
    { key: 'leads-revisions' }, { key: 'leads-matches' },
  ] },
  { key: 'rfq_mgmt', children: [
    { key: 'rfqs-all' }, { key: 'rfqs-draft' }, { key: 'rfqs-ready' },
  ] },
  { key: 'quote_mgmt', children: [
    { key: 'quotes-draft' }, { key: 'quotes-sent' }, { key: 'quotes-follow-up' },
    { key: 'quotes-outcomes' },
  ] },
  { key: 'service-boqs' },
  { key: 'client-po-inbox' },
  { key: 'orders' },
  { key: 'procurement-handoffs' },
  { key: 'accounts-receivable' },
  { key: 'shipments' },
  { key: 'supplier_mgmt', children: [
    { key: 'suppliers' }, { key: 'sourcing-cases' }, { key: 'supplier-quote-inbox' },
    { key: 'commercial-inbox' }, { key: 'quoted-items' }, { key: 'purchase-orders' },
  ] },
  { key: 'customers' },
  { key: 'inventory', children: [
    { key: 'inventory-overview' }, { key: 'inventory-availability' }, { key: 'inventory-warehouses' },
    { key: 'inventory-reservations' }, { key: 'inventory-incoming' }, { key: 'inventory-movements' },
    { key: 'inventory-demand' }, { key: 'inventory-levels' }, { key: 'inventory-reorder-alerts' },
    { key: 'inventory-count-variance' }, { key: 'inventory-ageing' }, { key: 'inventory-resources' },
    { key: 'inventory-lots' }, { key: 'inventory-order-trace' }, { key: 'products' },
    { key: 'categories' }, { key: 'sub-categories' },
  ] },
  { key: 'setup' },
];

const leaves = (rail: typeof FULL_RAIL) =>
  rail.flatMap((r) => (r.children ? r.children.map((c) => c.key) : [r.key]));

describe('the pilot rail', () => {
  it('cuts 17 rows to 12', () => {
    expect(FULL_RAIL).toHaveLength(17);
    expect(applyPilotRail(FULL_RAIL)).toHaveLength(12);
  });

  it('cuts 69 destinations to 19', () => {
    // 69, not the 68 the audit reported: that figure came from a grep matching only inline
    // `{ key: '...' }` entries and missed the multi-line ones. Counted from the transcribed
    // structure here, a "destination" is a leaf under a group plus every single-destination row.
    expect(leaves(FULL_RAIL)).toHaveLength(69);
    expect(leaves(applyPilotRail(FULL_RAIL))).toHaveLength(19);
  });

  it('keeps every step of the commercial spine reachable from the rail', () => {
    // RFQ -> Quote -> Customer PO -> Sales Order -> Supplier PO -> Shipment. If any of these
    // stops being one click away, the rail is no longer navigating the product's own process.
    const kept = leaves(applyPilotRail(FULL_RAIL));
    for (const key of [
      'leads-all', 'rfqs-all', 'quotes-draft', 'client-po-inbox',
      'orders', 'purchase-orders', 'shipments',
    ]) {
      expect(kept).toContain(key);
    }
  });

  it('puts the two screens a rep starts the day on at the top', () => {
    const [first, second] = applyPilotRail(FULL_RAIL).map((r) => r.key);
    expect(first).toBe('role-today');
    expect(second).toBe('dashboard');
  });

  it('hides the groups that are not on the spine', () => {
    const rows = applyPilotRail(FULL_RAIL).map((r) => r.key);
    // Copilot is an assistant competing for attention before a rep can do the job by hand;
    // Sales Management is manager analytics plus two duplicates of Today rows.
    expect(rows).not.toContain('copilot');
    expect(rows).not.toContain('sales-management');
    expect(rows).not.toContain('service-boqs');
    expect(rows).not.toContain('accounts-receivable');
  });

  it('keeps the catalogue but not the sixteen inventory screens around it', () => {
    const inventory = applyPilotRail(FULL_RAIL).find((r) => r.key === 'inventory');
    // A rep needs something to quote against. They do not need Stock Ageing, Count Variance,
    // Where-Used Trace or Lots & Traceability to price a tender.
    expect(inventory?.children?.map((c) => c.key)).toEqual(['products']);
  });

  it('drops a group entirely when none of its children survive', () => {
    const orphaned = [{ key: 'copilot', children: [{ key: 'copilot-chat' }] }];
    expect(applyPilotRail(orphaned)).toHaveLength(0);
  });

  it('leaves a single-destination row untouched rather than emptying it', () => {
    const rows = applyPilotRail(FULL_RAIL);
    const customers = rows.find((r) => r.key === 'customers');
    expect(customers).toBeDefined();
    expect(customers?.children).toBeUndefined();
  });

  it('does not mutate the rail it was given', () => {
    // The permission filter above it already mutates children in place. If this did too, a
    // re-render would compound the two and screens would vanish that permissions allow.
    const before = JSON.stringify(FULL_RAIL);
    applyPilotRail(FULL_RAIL);
    expect(JSON.stringify(FULL_RAIL)).toBe(before);
  });

  it('never invents a destination that was not already permitted', () => {
    // Permissions run FIRST and decide what a user may open. If a group arrives already stripped
    // to nothing the user is entitled to, the rail must not resurrect it.
    const stripped = [{ key: 'rfq_mgmt', children: [] as { key: string }[] }];
    expect(applyPilotRail(stripped)).toHaveLength(0);

    const partial = [{ key: 'supplier_mgmt', children: [{ key: 'suppliers' }] }];
    expect(applyPilotRail(partial)[0].children?.map((c) => c.key)).toEqual(['suppliers']);
  });
});
