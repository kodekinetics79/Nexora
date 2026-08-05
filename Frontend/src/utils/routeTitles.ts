import { matchPath } from 'react-router-dom';

/**
 * Route → page-title map backing WCAG 2.1 SC 2.4.2 (Page Titled).
 *
 * Kept as data rather than ~110 per-page `useDocumentTitle` calls so the title
 * for a route lives next to its path and cannot drift out of sync with a page
 * component that forgot to call the hook. `RouteAnnouncer` (App.tsx) resolves a
 * title here on every navigation.
 *
 * When you add a route to App.tsx or platform/PlatformRoutes.tsx, add its title
 * here too. Unmapped routes fall back to the generic app title.
 */

/** Literal paths — matched first so they always win over parameterised ones. */
const STATIC_ROUTE_TITLES: Readonly<Record<string, string>> = {
  '/login': 'Sign In',

  // Dashboards & intelligence
  '/dashboard': 'Dashboard',
  '/dashboard/team': 'Team Workload',
  '/analytics/deadlines': 'Deadline Board',
  '/analytics/brand-demand': 'Brand Demand',
  '/intelligence/commercial-memory': 'Commercial Memory',

  // Sourcing Copilot
  '/copilot': 'Sourcing Copilot',
  '/copilot/approvals': 'Copilot Approvals',
  '/copilot/activity': 'Copilot Activity',

  // Service BOQs
  '/services/boq': 'Service BOQs',

  // Role "Today" surfaces
  '/sales/today': 'Sales Today',
  '/sourcing/today': 'Sourcing Today',
  '/inventory/today': 'Inventory Today',
  '/executive/today': 'Executive RFQ-to-Revenue',
  '/admin/operations': 'Tenant Admin Operations',

  // Platform governance
  '/admin/platform/taxonomy': 'Taxonomy & Skill Studio',
  '/admin/platform/ai-trust': 'AI Trust Center',
  '/admin/platform/lifecycle': 'Model & Rule Lifecycle',
  '/admin/platform/integrations': 'Integration Hub',
  '/admin/platform/releases': 'Test & Release Center',
  '/admin/platform/archive': 'Commercial Document Archive',
  '/admin/platform/quality': 'Quality Analytics',
  '/admin/platform/retention': 'Storage & Retention',

  // Sales management
  '/sales/actions': 'Human Action Center',
  '/sales/team': 'Team Overview',
  '/sales/reps': 'Sales Reps',
  '/sales/accounts': 'Account Ownership',
  '/sales/routing': 'Routing Queue',
  '/sales/follow-ups': 'Follow-ups',
  '/sales/performance': 'Sales Performance',
  '/sales/exceptions': 'Commercial Exception Center',

  // Quotes, POs, orders, finance, shipments
  '/sales/quotes': 'Quotes',
  '/sales/quotes/create': 'Create Quote',
  '/sales/client-pos': 'Client PO Inbox',
  '/sales/orders': 'Customer Orders',
  '/sales/orders/create': 'Create Order',
  '/sales/finance': 'Accounts Receivable',
  '/sales/shipments': 'Shipments',
  '/sales/shipments/create': 'Create Shipment',

  // RFQs & sourcing
  '/procurement/rfqs/all': 'All RFQs',
  '/procurement/rfqs/draft': 'Draft RFQs',
  '/procurement/rfqs/outstanding': 'Outstanding RFQs',
  '/procurement/supplier-quotes': 'Supplier Quote Inbox',
  '/procurement/commercial-inbox': 'Commercial Inbox',
  '/procurement/handoffs': 'Procurement Handoffs',

  // Setup
  '/setup/master': 'Setup Master',
  '/setup/currency': 'Currencies',
  '/setup/warehouse': 'Warehouses',
  '/setup/uom': 'Units of Measure',
  '/setup/locations': 'Locations',
  '/setup/quote-format': 'Quote Format',
  '/setup/business-unit': 'Business Units',
  '/setup/price-structure': 'Price Structure',
  '/setup/sla': 'Deadlines & Alerts',

  // Security
  '/security/users': 'Users',
  '/security/roles': 'Roles & Permissions',

  // Inventory
  '/inventory/overview': 'Inventory Overview',
  '/inventory/availability': 'Stock Availability',
  '/inventory/warehouses': 'Inventory Warehouses',
  '/inventory/reservations': 'Stock Reservations',
  '/inventory/incoming': 'Incoming Stock',
  '/inventory/movements': 'Stock Movements',
  '/inventory/demand': 'Inventory Demand',
  '/inventory/resources': 'Related Resources',
  '/inventory/products': 'Products',
  '/inventory/categories': 'Product Categories',
  '/inventory/sub-categories': 'Product Sub-Categories',

  // Suppliers & customers
  '/suppliers': 'Suppliers',
  '/suppliers/quoted-items': 'Quoted Items',
  '/suppliers/purchase-orders': 'Purchase Orders',
  '/customers': 'Customers',

  // Extraction review & leads
  '/procurement/extraction/review': 'Extraction Review',
  '/procurement/leads/all': 'All Inquiries',
  '/procurement/leads/intelligence': 'Lead Intelligence',
  '/procurement/leads/outstanding': 'Outstanding Leads',
  '/procurement/leads/assigned': 'Assigned Leads',
  '/procurement/leads/manual-upload': 'Bulk Lead Upload',
  '/procurement/leads/possible-matches': 'Possible Matches',
  '/procurement/leads/duplicates': 'Duplicate Uploads',
  '/procurement/leads/inbound-mail': 'Inbound Mail',
  '/commercial-cases': 'Commercial Case Workspace',

  // Platform Owner console (see platform/PlatformRoutes.tsx)
  '/platform': 'Platform Console',
  '/platform/overview': 'Platform Overview',
  '/platform/tenants': 'Tenants',
  '/platform/pipeline': 'Platform Pipeline',
  '/platform/plans': 'Plans & Feature Flags',
  '/platform/audit': 'Platform Audit Log',
};

/** Parameterised paths, tried in order after the static lookup misses. */
const DYNAMIC_ROUTE_TITLES: ReadonlyArray<readonly [string, string]> = [
  ['/services/boq/:id', 'BOQ Editor'],
  ['/sales/reps/:userId', 'Sales Rep Profile'],
  ['/sales/quotes/view/:id', 'View Quote'],
  ['/sales/quotes/edit/:id', 'Edit Quote'],
  ['/sales/client-pos/:clientPoId', 'Client PO Review'],
  ['/sales/orders/invoice/:id', 'Order Invoice'],
  ['/sales/orders/edit/:id', 'Edit Order'],
  ['/sales/orders/:id', 'Order Details'],
  ['/sales/shipments/invoice/:id', 'Shipment Invoice'],
  ['/sales/shipments/from-order/:id', 'Create Shipment from Order'],
  ['/sales/shipments/edit/:id', 'Edit Shipment'],
  ['/sales/shipments/:id', 'Shipment Details'],
  ['/procurement/rfqs/process/:id', 'Process RFQ'],
  ['/procurement/rfqs/view/:id', 'View RFQ'],
  ['/procurement/rfqs/:id/pricing', 'RFQ Smart Pricing'],
  ['/procurement/rfqs/:rfqId/sourcing', 'Sourcing Workbench'],
  ['/procurement/sourcing-cases/:caseId', 'Sourcing Case'],
  ['/procurement/supplier-quotes/:supplierQuoteId', 'Supplier Quote Review'],
  ['/rfqs/view/:id', 'View RFQ'],
  ['/inventory/products/:id', 'Product Details'],
  ['/suppliers/:id', 'Supplier Details'],
  ['/customers/:id', 'Customer Details'],
  ['/procurement/extraction/review/:id', 'Extraction Review Detail'],
  ['/procurement/leads/ingestion/:batchId', 'Lead Ingestion Batch'],
  ['/procurement/leads/view/:id', 'Lead Details'],
  ['/procurement/leads/:id/convert', 'Convert Lead to RFQ'],
  ['/leads/view/:id', 'Lead Details'],
  ['/commercial-cases/:id', 'Commercial Case Workspace'],
  ['/platform/tenants/:id', 'Tenant Details'],
];

const stripTrailingSlash = (pathname: string): string =>
  pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;

/**
 * Resolve the human-readable page title for a pathname.
 * Returns `null` when the route is unmapped (including genuine 404s) so the
 * caller can decide on the fallback.
 */
export const resolveRouteTitle = (pathname: string): string | null => {
  const normalized = stripTrailingSlash(pathname);

  const exact = STATIC_ROUTE_TITLES[normalized];
  if (exact) return exact;

  for (const [pattern, title] of DYNAMIC_ROUTE_TITLES) {
    if (matchPath(pattern, normalized)) return title;
  }

  return null;
};

export default resolveRouteTitle;
