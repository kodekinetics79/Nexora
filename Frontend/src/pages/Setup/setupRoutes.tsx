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

// Screens Setup governs that live outside the `/setup` URL space — the former "User & Access" and
// "Platform Governance" rails.
const UsersPage = lazy(() => import('../Security/Users/UsersPage'));
const RolesPermissionsPage = lazy(() => import('../Security/Roles/RolesPermissionsPage'));
const TaxonomySkillStudioPage = lazy(() => import('../PlatformGovernance/TaxonomySkillStudioPage'));
const AiTrustCenterPage = lazy(() => import('../PlatformGovernance/AiTrustCenterPage'));
const LifecycleStudioPage = lazy(() => import('../PlatformGovernance/LifecycleStudioPage'));
const IntegrationHubPage = lazy(() => import('../PlatformGovernance/IntegrationHubPage'));
const ReleaseCenterPage = lazy(() => import('../PlatformGovernance/ReleaseCenterPage'));
const CommercialDocumentArchivePage = lazy(() => import('../PlatformGovernance/CommercialDocumentArchivePage'));
const QualityAnalyticsPage = lazy(() => import('../PlatformGovernance/QualityAnalyticsPage'));
const StorageRetentionPage = lazy(() => import('../PlatformGovernance/StorageRetentionPage'));

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
 *
 * `path` is relative to `/setup`; screens Setup governs at other addresses are in
 * `SETUP_ADOPTED_ROUTES` below.
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

/**
 * Setup screens mounted outside `/setup`, adopted into its chrome without moving their URLs.
 *
 * These were two rails of their own — "User & Access" and "Platform Governance" — which is why
 * Users, Roles & Permissions, Integration Hub and Storage & Retention each appeared twice in the
 * navigation: once as a rail row, once as a card in the Setup hub. They are one place now.
 *
 * Their paths are deliberately unchanged. `/security/users` and `/admin/platform/*` are in
 * bookmarks, in the a11y spec's title assertions and in the e2e suite; renaming a URL to tidy an
 * information architecture spends other people's links to buy nothing the reader can see. App.tsx
 * mounts these under the same `SetupShell` as the rest, so they carry the breadcrumb and the jump
 * field and read as part of Setup regardless of the address bar.
 */
export const SETUP_ADOPTED_ROUTES: SetupRoute[] = [
  { path: '/security/users', moduleName: 'Users', component: UsersPage },
  { path: '/security/roles', moduleName: 'Roles & Permissions', component: RolesPermissionsPage },
  { path: '/admin/platform/taxonomy', moduleName: 'Users', component: TaxonomySkillStudioPage },
  { path: '/admin/platform/ai-trust', moduleName: 'Users', component: AiTrustCenterPage },
  { path: '/admin/platform/lifecycle', moduleName: 'Users', component: LifecycleStudioPage },
  { path: '/admin/platform/integrations', moduleName: 'Users', component: IntegrationHubPage },
  { path: '/admin/platform/releases', moduleName: 'Users', component: ReleaseCenterPage },
  { path: '/admin/platform/archive', moduleName: 'Users', component: CommercialDocumentArchivePage },
  { path: '/admin/platform/quality', moduleName: 'Users', component: QualityAnalyticsPage },
  { path: '/admin/platform/retention', moduleName: 'Users', component: StorageRetentionPage },
];
