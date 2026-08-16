import { lazy, type LazyExoticComponent, type ComponentType } from 'react';

const SetupMaster = lazy(() => import('./SetupMaster'));
const CurrencyPage = lazy(() => import('./Currency/CurrencyPage'));
const WarehousePage = lazy(() => import('./Warehouse/WarehousePage'));
const UomPage = lazy(() => import('./UOM/UomPage'));
const LocationMaster = lazy(() => import('./Location/LocationMaster'));
const QuoteFormatPage = lazy(() => import('./QuoteFormat/QuoteFormatPage'));
const BusinessUnitPage = lazy(() => import('./BusinessUnit/BusinessUnitPage'));
const PriceStructurePage = lazy(() => import('./PriceStructure/PriceStructurePage'));
const SlaSettingsPage = lazy(() => import('./Sla/SlaSettingsPage'));
const ScheduledReportsPage = lazy(() => import('./Reporting/ScheduledReportsPage'));
const CommercialPolicyPage = lazy(() => import('./CommercialPolicy/CommercialPolicyPage'));
const MailboxPage = lazy(() => import('./Mailbox/MailboxPage'));
const RoutingRulesPage = lazy(() => import('./RoutingRules/RoutingRulesPage'));
const CustomFieldsPage = lazy(() => import('./CustomFields/CustomFieldsPage'));

export interface SetupRoute {
  /** Path relative to `/setup`. */
  path: string;
  /** Module the route's `PermissionGuard` checks. Must match the catalogue entry's `moduleName`. */
  moduleName: string;
  component: LazyExoticComponent<ComponentType>;
}

/**
 * Every screen under `/setup`, as data.
 *
 * App.tsx maps over this to build the route tree, and `setupCatalog.test.ts` compares it against
 * the catalogue. Routes and catalogue used to be two hand-maintained lists in different files,
 * which is how a screen ends up routed but unreachable — or reachable under two names.
 */
export const SETUP_ROUTES: SetupRoute[] = [
  { path: 'master', moduleName: 'UOM', component: SetupMaster },
  { path: 'currency', moduleName: 'Currency', component: CurrencyPage },
  { path: 'warehouse', moduleName: 'Warehouse', component: WarehousePage },
  { path: 'uom', moduleName: 'UOM', component: UomPage },
  { path: 'locations', moduleName: 'Locations', component: LocationMaster },
  { path: 'quote-format', moduleName: 'Quote Configuration', component: QuoteFormatPage },
  { path: 'business-unit', moduleName: 'Business Units', component: BusinessUnitPage },
  { path: 'price-structure', moduleName: 'UOM', component: PriceStructurePage },
  // SLA & alert policy (WP-A2). Guarded by the generic setup module ("UOM"), matching
  // /setup/master and /setup/price-structure.
  { path: 'sla', moduleName: 'UOM', component: SlaSettingsPage },
  // FR-DSH-06 scheduled report delivery. Guarded by "Dashboard" because that is the module the
  // reporting endpoints check, and because the reports carry dashboard content — the write side
  // additionally requires a manager role at the API.
  { path: 'scheduled-reports', moduleName: 'Dashboard', component: ScheduledReportsPage },
  { path: 'commercial-policy', moduleName: 'UOM', component: CommercialPolicyPage },
  // Mailbox administration. Guarded by "Email & SMTP" — the module the supplier-email screen
  // already uses — rather than the generic setup module, because these rows hold stored
  // credentials and decide where customer-facing mail is sent from.
  { path: 'mailboxes', moduleName: 'Email & SMTP', component: MailboxPage },
  // RFQ routing rules (FR-RFQ-07). Guarded by "Customers" because that is the module the
  // underlying commercial-routing endpoints check for both reading a customer's routing profile
  // and creating ownership/identifier rows.
  { path: 'routing-rules', moduleName: 'Customers', component: RoutingRulesPage },
  // AA-01 · tenant-defined custom fields. Guarded by the generic setup module ("UOM"), matching
  // /setup/master and /setup/sla; the API additionally requires a manager role and edit permission
  // on the module the field attaches to.
  { path: 'custom-fields', moduleName: 'UOM', component: CustomFieldsPage },
];
