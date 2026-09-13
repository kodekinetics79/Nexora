import { Suspense } from 'react';
import { Routes, Route, Navigate } from 'react-router-dom';
import { Box, CircularProgress } from '@mui/material';
import TenantShell from './components/layout/TenantShell';
import lazyWithRetry from './utils/lazyWithRetry';
import PermissionGuard, { RequireAuth, RequireManager } from './components/common/PermissionGuard';
import RouteAnnouncer from './components/layout/RouteAnnouncer';
import { SETUP_ROUTES, SETUP_ADOPTED_ROUTES } from './pages/Setup/setupRoutes';

// FE-09: route-level code splitting. Each page is loaded on demand so the
// initial bundle only ships the app shell (layout, guards, providers).
const LoginPage = lazyWithRetry(() => import('./pages/Login/LoginPage'));
// The `/setup` shell (breadcrumb + jump field) and the hub it fronts. Both split like every other
// route: nothing outside Setup pays for them. The screens below them are declared as data in
// `pages/Setup/setupRoutes.tsx` and mapped into the route tree further down.
const SetupShell = lazyWithRetry(() => import('./pages/Setup/SetupShell'));
const SetupHubPage = lazyWithRetry(() => import('./pages/Setup/SetupHubPage'));
const NotFoundPage = lazyWithRetry(() => import('./pages/NotFoundPage'));
// The landing screen: one prioritised queue of everything waiting on this user, built from the
// endpoints the individual queue screens already read. It replaced `/analytics/deadlines`, which
// could only show enquiries and whose only outbound link was to a single lead.
const InboxPage = lazyWithRetry(() => import('./pages/Inbox/InboxPage'));
// The directory of every screen the five-row rail no longer carries. Nothing is deleted to shrink
// the rail; it is listed here, grouped and described, with its route unchanged.
const AllScreensPage = lazyWithRetry(() => import('./pages/Advanced/AllScreensPage'));
const ProductsPage = lazyWithRetry(() => import('./pages/Inventory/ProductsPage'));
const ProductDetailPage = lazyWithRetry(() => import('./pages/Inventory/ProductDetailPage'));
const ProductCategoryPage = lazyWithRetry(() => import('./pages/Inventory/ProductCategoryPage'));
const ProductSubCategoryPage = lazyWithRetry(() => import('./pages/Inventory/ProductSubCategoryPage'));
const InventoryOverviewPage = lazyWithRetry(() => import('./pages/Inventory/Commercial/InventoryOverviewPage'));
const AvailabilityPage = lazyWithRetry(() => import('./pages/Inventory/Commercial/AvailabilityPage'));
const WarehousesPage = lazyWithRetry(() => import('./pages/Inventory/Commercial/WarehousesPage'));
const ReservationsPage = lazyWithRetry(() => import('./pages/Inventory/Commercial/ReservationsPage'));
const IncomingPage = lazyWithRetry(() => import('./pages/Inventory/Commercial/IncomingPage'));
const MovementsPage = lazyWithRetry(() => import('./pages/Inventory/Commercial/MovementsPage'));
const DemandPage = lazyWithRetry(() => import('./pages/Inventory/Commercial/DemandPage'));
const RelatedResourcesPage = lazyWithRetry(() => import('./pages/Inventory/Commercial/RelatedResourcesPage'));
const StockLevelsPage = lazyWithRetry(() => import('./pages/Inventory/Commercial/StockLevelsPage'));
const ReorderAlertsPage = lazyWithRetry(() => import('./pages/Inventory/Commercial/ReorderAlertsPage'));
const CountVariancePage = lazyWithRetry(() => import('./pages/Inventory/Commercial/CountVariancePage'));
const StockAgeingPage = lazyWithRetry(() => import('./pages/Inventory/Commercial/StockAgeingPage'));
// Gate 5 / FR-MTR-01..05 — material lots, certificates, quarantine and where-used trace.
const LotsPage = lazyWithRetry(() => import('./pages/Inventory/Traceability/LotsPage'));
const LotDetailPage = lazyWithRetry(() => import('./pages/Inventory/Traceability/LotDetailPage'));
const OrderTracePage = lazyWithRetry(() => import('./pages/Inventory/Traceability/OrderTracePage'));
const SuppliersPage = lazyWithRetry(() => import('./pages/Suppliers/SuppliersPage'));
const SupplierDetailPage = lazyWithRetry(() => import('./pages/Suppliers/SupplierDetailPage'));
const QuotedItemsPage = lazyWithRetry(() => import('./pages/Suppliers/QuotedItemsPage'));
const PurchaseOrdersPage = lazyWithRetry(() => import('./pages/Suppliers/PurchaseOrdersPage'));
const CustomersPage = lazyWithRetry(() => import('./pages/Customers/CustomersPage'));
const CustomerDetailPage = lazyWithRetry(() => import('./pages/Customers/CustomerDetailPage'));
const LeadsPage = lazyWithRetry(() => import('./pages/Leads/LeadsPage'));
const OutstandingLeadsPage = lazyWithRetry(() => import('./pages/Leads/OutstandingLeadsPage'));
const AssignedLeadsPage = lazyWithRetry(() => import('./pages/Leads/AssignedLeadsPage'));
const ManualUploadLeadsPage = lazyWithRetry(() => import('./pages/Leads/ManualUploadLeadsPage'));
const WatchedFoldersPage = lazyWithRetry(() => import('./pages/Leads/WatchedFoldersPage'));
const LeadIngestionBatchPage = lazyWithRetry(() => import('./pages/Leads/LeadIngestionBatchPage'));
const PossibleMatchesPage = lazyWithRetry(() => import('./pages/Leads/PossibleMatchesPage'));
const DuplicateUploadsPage = lazyWithRetry(() => import('./pages/Leads/DuplicateUploadsPage'));
const InboundMailTriagePage = lazyWithRetry(() => import('./pages/Leads/InboundMailTriagePage'));
const LeadDetailPage = lazyWithRetry(() => import('./pages/Leads/LeadDetailPage'));
const DecidePage = lazyWithRetry(() => import('./pages/Leads/Decide/DecidePage'));
const LeadConvertRedirectPage = lazyWithRetry(() => import('./pages/Leads/Workbench/LeadConvertRedirectPage'));
const CommercialCaseWorkspacePage = lazyWithRetry(() => import('./pages/CommercialCases/CommercialCaseWorkspacePage'));
const ExtractionReviewPage = lazyWithRetry(() => import('./pages/ExtractionReview/ExtractionReviewPage'));
const ExtractionReviewDetailPage = lazyWithRetry(() => import('./pages/ExtractionReview/ExtractionReviewDetailPage'));
const AllRFQsPage = lazyWithRetry(() => import('./pages/Procurement/RFQs/AllRFQsPage'));
const DraftRFQsPage = lazyWithRetry(() => import('./pages/Procurement/RFQs/DraftRFQsPage'));
const ViewRFQPage = lazyWithRetry(() => import('./pages/Procurement/RFQs/ViewRFQPage'));
const SourcingWorkbenchPage = lazyWithRetry(() => import('./pages/Procurement/Sourcing/SourcingWorkbenchPage'));
const SourcingCasePage = lazyWithRetry(() => import('./pages/Procurement/Sourcing/SourcingCasePage'));
const SupplierQuoteInboxPage = lazyWithRetry(() => import('./pages/Procurement/SupplierQuotes/SupplierQuoteInboxPage'));
const SupplierQuoteReviewPage = lazyWithRetry(() => import('./pages/Procurement/SupplierQuotes/SupplierQuoteReviewPage'));
const CommercialInboxPage = lazyWithRetry(() => import('./pages/Procurement/SupplierQuotes/CommercialInboxPage'));
const ProcurementHandoffsPage = lazyWithRetry(() => import('./pages/Procurement/Handoffs/ProcurementHandoffsPage'));
const DashboardPage = lazyWithRetry(() => import('./pages/Dashboard/DashboardPage'));
const TeamWorkloadPage = lazyWithRetry(() => import('./pages/Dashboard/TeamWorkloadPage'));
const DeadlineBoardPage = lazyWithRetry(() => import('./pages/Analytics/DeadlineBoardPage'));
const BrandDemandPage = lazyWithRetry(() => import('./pages/Analytics/BrandDemandPage'));
const QuotesPage = lazyWithRetry(() => import('./pages/Sales/Quotes/QuotesPage'));
const CreateQuotePage = lazyWithRetry(() => import('./pages/Sales/Quotes/CreateQuotePage'));
const QuoteViewPage = lazyWithRetry(() => import('./pages/Sales/Quotes/QuoteViewPage'));
const EditQuotePage = lazyWithRetry(() => import('./pages/Sales/Quotes/EditQuotePage'));
const ClientPurchaseOrderInboxPage = lazyWithRetry(() => import('./pages/Sales/ClientPurchaseOrders/ClientPurchaseOrderInboxPage'));
const ClientPurchaseOrderReviewPage = lazyWithRetry(() => import('./pages/Sales/ClientPurchaseOrders/ClientPurchaseOrderReviewPage'));
const OrderListPage = lazyWithRetry(() => import('./pages/Sales/Orders/OrderListPage'));
const CreateOrderPage = lazyWithRetry(() => import('./pages/Sales/Orders/CreateOrderPage'));
const OrderViewPage = lazyWithRetry(() => import('./pages/Sales/Orders/OrderViewPage'));
const AccountsReceivablePage = lazyWithRetry(() => import('./pages/Sales/Finance/AccountsReceivablePage'));
const ShipmentListPage = lazyWithRetry(() => import('./pages/Sales/Shipments/ShipmentListPage'));
const CreateShipmentPage = lazyWithRetry(() => import('./pages/Sales/Shipments/CreateShipmentPage'));
const ShipmentViewPage = lazyWithRetry(() => import('./pages/Sales/Shipments/ShipmentViewPage'));
const ShipmentInvoicePage = lazyWithRetry(() => import('./pages/Sales/Shipments/ShipmentInvoicePage'));
const SalesTodayPage = lazyWithRetry(() => import('./pages/SalesManagement/SalesTodayPage'));
const TeamOverviewPage = lazyWithRetry(() => import('./pages/SalesManagement/TeamOverviewPage'));
const RepDirectoryPage = lazyWithRetry(() => import('./pages/SalesManagement/RepDirectoryPage'));
const RepProfilePage = lazyWithRetry(() => import('./pages/SalesManagement/RepProfilePage'));
const AccountOwnershipPage = lazyWithRetry(() => import('./pages/SalesManagement/AccountOwnershipPage'));
const RoutingQueuePage = lazyWithRetry(() => import('./pages/SalesManagement/RoutingQueuePage'));
const FollowUpsPage = lazyWithRetry(() => import('./pages/SalesManagement/FollowUpsPage'));
const PerformancePage = lazyWithRetry(() => import('./pages/SalesManagement/PerformancePage'));
const CommercialExceptionCenterPage = lazyWithRetry(() => import('./pages/SalesManagement/CommercialExceptionCenterPage'));
const SourcingTodayPage = lazyWithRetry(() => import('./pages/Today/SourcingTodayPage'));
const TenantAdminOperationsPage = lazyWithRetry(() => import('./pages/Today/TenantAdminOperationsPage'));

// Intelligence surfaces — AI-assisted Lead→RFQ conversion and RFQ smart pricing.
const RfqPricingPage = lazyWithRetry(() => import('./pages/Intelligence/RfqPricingPage'));
const CommercialMemoryPage = lazyWithRetry(() => import('./pages/Intelligence/CommercialMemoryPage'));
const HumanActionCenterPage = lazyWithRetry(() => import('./pages/PlatformGovernance/HumanActionCenterPage'));

// Service RFQ → BOQ engine — drafted bills of quantities for service work.
const BoqListPage = lazyWithRetry(() => import('./pages/Boq/BoqListPage'));
const BoqEditorPage = lazyWithRetry(() => import('./pages/Boq/BoqEditorPage'));

// Sourcing Copilot — conversational autonomous-agent console (flagship surface).
const CopilotPage = lazyWithRetry(() => import('./pages/Copilot/CopilotPage'));
const CopilotApprovalsPage = lazyWithRetry(() => import('./pages/Copilot/ApprovalsPage'));
const CopilotActivityPage = lazyWithRetry(() => import('./pages/Copilot/ActivityPage'));

// Platform Owner console (ADR-0005). Self-contained `/platform/*` tree with its
// own guard + layout; see src/platform/.
const PlatformRoutes = lazyWithRetry(() => import('./platform/PlatformRoutes'));

// Account activation for an invited founding administrator. Public by
// necessity — the person opening it has no session yet — and outside MainLayout
// because the app shell renders navigation for a workspace they cannot enter.
const ActivateAccountPage = lazyWithRetry(() => import('./pages/Activation/ActivateAccountPage'));

// Self-service password recovery. Public for the same reason activation is —
// somebody who cannot sign in cannot be asked to sign in first — and outside
// MainLayout for the same reason: the app shell renders navigation for a
// workspace they cannot enter yet.
const ForgotPasswordPage = lazyWithRetry(() => import('./pages/PasswordReset/ForgotPasswordPage'));
const ResetPasswordPage = lazyWithRetry(() => import('./pages/PasswordReset/ResetPasswordPage'));

const PageLoader = () => (
  <Box
    role="status"
    aria-live="polite"
    sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', minHeight: '60vh', width: '100%' }}
  >
    <CircularProgress aria-label="Loading page" />
  </Box>
);

function App() {
  return (
    <>
    {/* Per-route document title, focus reset, scroll reset and a polite
        route-change announcement — SC 2.4.2 / 2.4.3 / 4.1.3. */}
    <RouteAnnouncer />
    <Suspense fallback={<PageLoader />}>
    <Routes>
      <Route path="/" element={<Navigate to="/login" replace />} />

      {/* The landing screen is module-agnostic, not authentication-agnostic. A signed-in user whose
          grants are still loading — or who holds none — reaches the explanatory Inbox instead of
          Access Denied; a signed-out visitor must never receive the tenant shell. Each queue inside
          asks for its own module and is simply not requested when the grant is absent. */}
      <Route path="/inbox" element={<TenantShell><InboxPage /></TenantShell>} />
      {/* Every screen the rail relocated, grouped and searchable. The cards are permission-filtered,
          but the directory itself still belongs inside the authenticated tenant boundary. */}
      <Route path="/advanced" element={<TenantShell><AllScreensPage /></TenantShell>} />
      {/* Old landing addresses. `/analytics/deadlines` stays a live screen (it is listed under
          Dashboards & analytics); these two are the shortcuts people type. */}
      <Route path="/home" element={<RequireAuth><Navigate to="/inbox" replace /></RequireAuth>} />
      <Route path="/today" element={<RequireAuth><Navigate to="/inbox" replace /></RequireAuth>} />

      <Route path="/dashboard" element={<TenantShell><PermissionGuard moduleName="Dashboard"><DashboardPage /></PermissionGuard></TenantShell>} />
      <Route path="/dashboard/team" element={<TenantShell><PermissionGuard moduleName="Dashboard"><TeamWorkloadPage /></PermissionGuard></TenantShell>} />
      {/* Analytics built only on data the tenant actually holds. The deadline
          board is the landing surface for the pilot: /dashboard's KPIs are
          insufficient-data for a new tenant and must not be the first screen. */}
      <Route path="/analytics/deadlines" element={<TenantShell><PermissionGuard moduleName="Leads"><DeadlineBoardPage /></PermissionGuard></TenantShell>} />
      <Route path="/analytics/brand-demand" element={<TenantShell><PermissionGuard moduleName="Leads"><BrandDemandPage /></PermissionGuard></TenantShell>} />
      <Route path="/intelligence/commercial-memory" element={<TenantShell><PermissionGuard moduleName="Dashboard"><PermissionGuard moduleName="Quotations"><CommercialMemoryPage /></PermissionGuard></PermissionGuard></TenantShell>} />

      {/* Sourcing Copilot Routes */}
      <Route path="/copilot" element={<TenantShell><PermissionGuard moduleName="Dashboard"><CopilotPage /></PermissionGuard></TenantShell>} />
      <Route path="/copilot/approvals" element={<TenantShell><PermissionGuard moduleName="Dashboard"><CopilotApprovalsPage /></PermissionGuard></TenantShell>} />
      <Route path="/copilot/activity" element={<TenantShell><PermissionGuard moduleName="Dashboard"><CopilotActivityPage /></PermissionGuard></TenantShell>} />

      {/* Service BOQ Routes — gated by Quotations (BOQs are priced quote material) */}
      <Route path="/services/boq" element={<TenantShell><PermissionGuard moduleName="Quotations"><BoqListPage /></PermissionGuard></TenantShell>} />
      <Route path="/services/boq/:id" element={<TenantShell><PermissionGuard moduleName="Quotations"><BoqEditorPage /></PermissionGuard></TenantShell>} />

      {/* Sales Routes */}
      <Route path="/sales/today" element={<TenantShell><PermissionGuard moduleName="Leads"><SalesTodayPage /></PermissionGuard></TenantShell>} />
      <Route path="/sourcing/today" element={<TenantShell><PermissionGuard moduleName="Supplier History"><SourcingTodayPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/today" element={<TenantShell><PermissionGuard moduleName="Products"><InventoryOverviewPage /></PermissionGuard></TenantShell>} />
      <Route path="/executive/today" element={<TenantShell><PermissionGuard moduleName="Dashboard"><DashboardPage /></PermissionGuard></TenantShell>} />
      <Route path="/admin/operations" element={<TenantShell><PermissionGuard moduleName="Users"><TenantAdminOperationsPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/actions" element={<TenantShell><PermissionGuard moduleName="Leads"><HumanActionCenterPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/team" element={<TenantShell><RequireManager><PermissionGuard moduleName="Leads"><TeamOverviewPage /></PermissionGuard></RequireManager></TenantShell>} />
      <Route path="/sales/reps" element={<TenantShell><RequireManager><PermissionGuard moduleName="Users"><RepDirectoryPage /></PermissionGuard></RequireManager></TenantShell>} />
      <Route path="/sales/reps/:userId" element={<TenantShell><RequireManager><PermissionGuard moduleName="Users"><RepProfilePage /></PermissionGuard></RequireManager></TenantShell>} />
      <Route path="/sales/accounts" element={<TenantShell><PermissionGuard moduleName="Customers"><AccountOwnershipPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/routing" element={<TenantShell><PermissionGuard moduleName="Leads"><RoutingQueuePage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/follow-ups" element={<TenantShell><PermissionGuard moduleName="Quotations"><FollowUpsPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/performance" element={<TenantShell><PermissionGuard moduleName="Dashboard"><PerformancePage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/exceptions" element={<TenantShell><PermissionGuard moduleName="Leads"><CommercialExceptionCenterPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/quotes" element={<TenantShell><PermissionGuard moduleName="Quotations"><QuotesPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/quotes/create" element={<TenantShell><PermissionGuard moduleName="Quotations" page action="create"><CreateQuotePage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/quotes/view/:id" element={<TenantShell><PermissionGuard moduleName="Quotations"><QuoteViewPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/quotes/edit/:id" element={<TenantShell><PermissionGuard moduleName="Quotations" page action="edit"><EditQuotePage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/client-pos" element={<TenantShell><PermissionGuard moduleName="Customer Awards"><ClientPurchaseOrderInboxPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/client-pos/:clientPoId" element={<TenantShell><PermissionGuard moduleName="Customer Awards"><ClientPurchaseOrderReviewPage /></PermissionGuard></TenantShell>} />
      
      <Route path="/sales/orders" element={<TenantShell><PermissionGuard moduleName="Orders"><OrderListPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/orders/create" element={<TenantShell><PermissionGuard moduleName="Orders" page action="create"><CreateOrderPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/orders/edit/:id" element={<TenantShell><PermissionGuard moduleName="Orders" page action="edit"><CreateOrderPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/orders/:id" element={<TenantShell><PermissionGuard moduleName="Orders"><OrderViewPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/finance" element={<TenantShell><PermissionGuard moduleName="Accounts Receivable"><AccountsReceivablePage /></PermissionGuard></TenantShell>} />
      
      <Route path="/sales/shipments" element={<TenantShell><PermissionGuard moduleName="Shipments"><ShipmentListPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/shipments/create" element={<TenantShell><PermissionGuard moduleName="Shipments" page action="create"><CreateShipmentPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/shipments/from-order/:id" element={<TenantShell><PermissionGuard moduleName="Shipments" page action="create"><CreateShipmentPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/shipments/edit/:id" element={<TenantShell><PermissionGuard moduleName="Shipments" page action="edit"><CreateShipmentPage /></PermissionGuard></TenantShell>} />
      <Route path="/sales/shipments/:id" element={<TenantShell><PermissionGuard moduleName="Shipments"><ShipmentViewPage /></PermissionGuard></TenantShell>} />
      
      {/* Sales Invoices/Documents */}
      {/* No order-level tax-invoice route: the governed AR document is issued by the finance
          subsystem and is reached via /sales/finance. A page that renders an order as an
          "invoice" is not the issued document and must not exist before the ZATCA gate. */}
      <Route path="/sales/shipments/invoice/:id" element={<PermissionGuard moduleName="Shipments"><ShipmentInvoicePage /></PermissionGuard>} />
      
      <Route path="/orders" element={<Navigate to="/sales/orders" replace />} />
      <Route path="/quotations" element={<Navigate to="/sales/quotes" replace />} />
      
      {/* RFQ Routes */}
      <Route path="/procurement/rfqs" element={<Navigate to="/procurement/rfqs/all" replace />} />
      <Route path="/procurement/rfqs/all" element={<TenantShell><PermissionGuard moduleName="RFQ Management"><AllRFQsPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/rfqs/draft" element={<TenantShell><PermissionGuard moduleName="RFQ Management"><DraftRFQsPage /></PermissionGuard></TenantShell>} />
      {/* This address used to render assigned Leads under an RFQ heading. Keep old bookmarks alive,
          but send them to the canonical Lead queue; its Leads permission guard owns access. */}
      <Route path="/procurement/rfqs/outstanding" element={<Navigate to="/procurement/leads/assigned" replace />} />
      {/* Tombstone the former direct Lead -> RFQ creator. Old bookmarks and notifications must
          enter the governed Lead decision workbench; this address never renders a second creation
          experience. The destination route owns its normal Leads permission gate. */}
      <Route path="/procurement/rfqs/process/:id" element={<LeadConvertRedirectPage />} />
      <Route path="/procurement/rfqs/view/:id" element={<TenantShell><PermissionGuard moduleName="RFQ Management"><ViewRFQPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/rfqs/:id/pricing" element={<TenantShell><PermissionGuard moduleName="RFQ Management"><PermissionGuard moduleName="Quotations"><RfqPricingPage /></PermissionGuard></PermissionGuard></TenantShell>} />
      <Route path="/procurement/rfqs/:rfqId/sourcing" element={<TenantShell><PermissionGuard moduleName="RFQ Management"><SourcingWorkbenchPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/sourcing" element={<Navigate to="/procurement/sourcing-cases" replace />} />
      <Route path="/procurement/sourcing-cases" element={<Navigate to="/procurement/rfqs/all?state=requires-sourcing" replace />} />
      <Route path="/procurement/sourcing-cases/:caseId" element={<TenantShell><PermissionGuard moduleName="RFQ Management"><SourcingCasePage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/supplier-quotes" element={<TenantShell><PermissionGuard moduleName="Supplier History"><SupplierQuoteInboxPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/supplier-quotes/:supplierQuoteId" element={<TenantShell><PermissionGuard moduleName="Supplier History"><SupplierQuoteReviewPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/commercial-inbox" element={<TenantShell><PermissionGuard moduleName="Supplier History"><CommercialInboxPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/handoffs" element={<TenantShell><PermissionGuard moduleName="Orders"><ProcurementHandoffsPage /></PermissionGuard></TenantShell>} />
      <Route path="/rfqs/view/:id" element={<TenantShell><PermissionGuard moduleName="RFQ Management"><ViewRFQPage /></PermissionGuard></TenantShell>} />
      <Route path="/rfqs" element={<Navigate to="/procurement/rfqs/all" replace />} />
      
      {/* Setup Routes — one shell over a nested tree, so `/setup` is a real place (the hub) and
          every screen below it inherits the breadcrumb and the jump field. What each screen is,
          and which group it belongs to, is declared once in `pages/Setup/setupCatalog.tsx`;
          `setupCatalog.test.ts` fails if a route here is missing from it or listed twice. */}
      <Route path="/setup" element={<TenantShell><SetupShell /></TenantShell>}>
        <Route index element={<SetupHubPage />} />
        {SETUP_ROUTES.map(({ path, moduleName, component: Screen }) => (
          <Route
            key={path}
            path={path}
            element={<PermissionGuard moduleName={moduleName}><Screen /></PermissionGuard>}
          />
        ))}
      </Route>

      {/* The screens Setup governs at their own addresses — the former "User & Access" and
          "Platform Governance" rails. A pathless layout route puts them under the same shell as
          /setup/*, so they carry Setup's breadcrumb and jump field while keeping the URLs that
          bookmarks and the e2e suite already point at. */}
      <Route element={<TenantShell><SetupShell /></TenantShell>}>
        {SETUP_ADOPTED_ROUTES.map(({ path, moduleName, component: Screen }) => (
          <Route
            key={path}
            path={path}
            element={<PermissionGuard moduleName={moduleName}><Screen /></PermissionGuard>}
          />
        ))}
      </Route>

      {/* Inventory Routes */}
      <Route path="/inventory/overview" element={<TenantShell><PermissionGuard moduleName="Products"><InventoryOverviewPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/availability" element={<TenantShell><PermissionGuard moduleName="Products"><AvailabilityPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/warehouses" element={<TenantShell><PermissionGuard moduleName="Products"><WarehousesPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/reservations" element={<TenantShell><PermissionGuard moduleName="Products"><ReservationsPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/incoming" element={<TenantShell><PermissionGuard moduleName="Products"><IncomingPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/movements" element={<TenantShell><PermissionGuard moduleName="Products"><MovementsPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/demand" element={<TenantShell><PermissionGuard moduleName="Products"><DemandPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/resources" element={<TenantShell><PermissionGuard moduleName="Products"><RelatedResourcesPage /></PermissionGuard></TenantShell>} />
      {/* FR-INV-04/05/06. Four screens that had no interface at all: minimum/maximum levels and the
          reorder alert ledger, and the two reports whose endpoints were complete and unreachable. */}
      <Route path="/inventory/levels" element={<TenantShell><PermissionGuard moduleName="Products"><StockLevelsPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/reorder-alerts" element={<TenantShell><PermissionGuard moduleName="Products"><ReorderAlertsPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/count-variance" element={<TenantShell><PermissionGuard moduleName="Products"><CountVariancePage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/ageing" element={<TenantShell><PermissionGuard moduleName="Products"><StockAgeingPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/lots" element={<TenantShell><PermissionGuard moduleName="Products"><LotsPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/lots/:lotId" element={<TenantShell><PermissionGuard moduleName="Products"><LotDetailPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/order-trace" element={<TenantShell><PermissionGuard moduleName="Products"><PermissionGuard moduleName="Orders"><OrderTracePage /></PermissionGuard></PermissionGuard></TenantShell>} />
      <Route path="/inventory/order-trace/:orderId" element={<TenantShell><PermissionGuard moduleName="Products"><PermissionGuard moduleName="Orders"><OrderTracePage /></PermissionGuard></PermissionGuard></TenantShell>} />
      <Route path="/inventory/products" element={<TenantShell><PermissionGuard moduleName="Products"><ProductsPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/products/:id" element={<TenantShell><PermissionGuard moduleName="Products"><ProductDetailPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/categories" element={<TenantShell><PermissionGuard moduleName="Product Categories"><ProductCategoryPage /></PermissionGuard></TenantShell>} />
      <Route path="/inventory/sub-categories" element={<TenantShell><PermissionGuard moduleName="Product Categories"><ProductSubCategoryPage /></PermissionGuard></TenantShell>} />
      
      {/* Supplier Routes */}
      <Route path="/suppliers" element={<TenantShell><PermissionGuard moduleName="Suppliers"><SuppliersPage /></PermissionGuard></TenantShell>} />
      <Route path="/suppliers/:id" element={<TenantShell><PermissionGuard moduleName="Suppliers"><SupplierDetailPage /></PermissionGuard></TenantShell>} />
      <Route path="/suppliers/quoted-items" element={<TenantShell><PermissionGuard moduleName="Supplier History"><QuotedItemsPage /></PermissionGuard></TenantShell>} />
      <Route path="/suppliers/purchase-orders" element={<TenantShell><PermissionGuard moduleName="Orders"><PurchaseOrdersPage /></PermissionGuard></TenantShell>} />
      
      {/* Customer Routes */}
      <Route path="/customers" element={<TenantShell><PermissionGuard moduleName="Customers"><CustomersPage /></PermissionGuard></TenantShell>} />
      <Route path="/customers/:id" element={<TenantShell><PermissionGuard moduleName="Customers"><CustomerDetailPage /></PermissionGuard></TenantShell>} />

      {/* Extraction Review Routes */}
      <Route path="/procurement/extraction/review" element={<TenantShell><PermissionGuard moduleName="Leads"><ExtractionReviewPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/extraction/review/:id" element={<TenantShell><PermissionGuard moduleName="Leads"><ExtractionReviewDetailPage /></PermissionGuard></TenantShell>} />

      {/* Lead Management Routes */}
      <Route path="/procurement/leads/all" element={<TenantShell><PermissionGuard moduleName="Leads"><LeadsPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/leads/intelligence" element={<TenantShell><PermissionGuard moduleName="Leads" page action="create"><ManualUploadLeadsPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/leads/outstanding" element={<TenantShell><PermissionGuard moduleName="Leads"><OutstandingLeadsPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/leads/assigned" element={<TenantShell><PermissionGuard moduleName="Leads"><AssignedLeadsPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/leads/manual-upload" element={<TenantShell><PermissionGuard moduleName="Leads" page action="create"><ManualUploadLeadsPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/leads/ingestion/:batchId" element={<TenantShell><PermissionGuard moduleName="Leads"><LeadIngestionBatchPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/leads/possible-matches" element={<TenantShell><PermissionGuard moduleName="Leads"><PossibleMatchesPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/leads/duplicates" element={<TenantShell><PermissionGuard moduleName="Leads"><DuplicateUploadsPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/leads/inbound-mail" element={<TenantShell><PermissionGuard moduleName="Leads"><InboundMailTriagePage /></PermissionGuard></TenantShell>} />
      {/*
        The watched-folder intake channel (FolderService, swept by EmailBackgroundService). The old
        route redirected to manual upload, which left a channel that really runs on the server with
        no operator surface at all.
      */}
      <Route path="/procurement/leads/watched-folders" element={<TenantShell><PermissionGuard moduleName="Leads"><WatchedFoldersPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/leads/folder-upload" element={<Navigate to="/procurement/leads/watched-folders" replace />} />
      <Route path="/procurement/leads/view/:id" element={<TenantShell><PermissionGuard moduleName="Leads"><LeadDetailPage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/leads/:id/workbench" element={<TenantShell><PermissionGuard moduleName="Leads"><DecidePage /></PermissionGuard></TenantShell>} />
      <Route path="/procurement/leads/:id/convert" element={<TenantShell><PermissionGuard moduleName="Leads"><LeadConvertRedirectPage /></PermissionGuard></TenantShell>} />
      <Route path="/commercial-cases/:id?" element={<TenantShell><PermissionGuard moduleName="Leads"><CommercialCaseWorkspacePage /></PermissionGuard></TenantShell>} />
      
      {/* Short Lead Routes */}
      <Route path="/leads/all" element={<Navigate to="/procurement/leads/all" replace />} />
      <Route path="/leads/outstanding" element={<Navigate to="/procurement/leads/outstanding" replace />} />
      <Route path="/leads/assigned" element={<Navigate to="/procurement/leads/assigned" replace />} />
      <Route path="/leads/manual-upload" element={<Navigate to="/procurement/leads/manual-upload" replace />} />
      <Route path="/leads/folder-upload" element={<Navigate to="/procurement/leads/watched-folders" replace />} />
      <Route path="/leads/watched-folders" element={<Navigate to="/procurement/leads/watched-folders" replace />} />
      <Route path="/leads/view/:id" element={<TenantShell><PermissionGuard moduleName="Leads"><LeadDetailPage /></PermissionGuard></TenantShell>} />
      <Route path="/leads" element={<Navigate to="/procurement/leads/all" replace />} />

      {/* Platform Owner console — owner-only control plane above tenants */}
      <Route path="/platform/*" element={<PlatformRoutes />} />

      <Route path="/login" element={<LoginPage />} />
      <Route path="/activate/:token" element={<ActivateAccountPage />} />
      {/* Path segment, not a query parameter — the emailed link is built to
          match this exactly (TenantOnboarding:ResetPasswordPath). A mismatch
          here 404s in the SPA and the customer sees a blank page. */}
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      <Route path="/reset-password/:token" element={<ResetPasswordPage />} />
      <Route path="*" element={<NotFoundPage />} />
    </Routes>
    </Suspense>
    </>
  );
}

export default App;
