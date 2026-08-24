import { lazy, Suspense } from 'react';
import { Routes, Route, Navigate } from 'react-router-dom';
import { Box, CircularProgress, Typography } from '@mui/material';
import MainLayout from './components/layout/MainLayout';
import PermissionGuard from './components/common/PermissionGuard';
import RouteAnnouncer from './components/layout/RouteAnnouncer';
import useDocumentTitle from './hooks/useDocumentTitle';
import { SETUP_ROUTES, SETUP_ADOPTED_ROUTES } from './pages/Setup/setupRoutes';

// FE-09: route-level code splitting. Each page is loaded on demand so the
// initial bundle only ships the app shell (layout, guards, providers).
const LoginPage = lazy(() => import('./pages/Login/LoginPage'));
// The `/setup` shell (breadcrumb + jump field) and the hub it fronts. Both split like every other
// route: nothing outside Setup pays for them. The screens below them are declared as data in
// `pages/Setup/setupRoutes.tsx` and mapped into the route tree further down.
const SetupShell = lazy(() => import('./pages/Setup/SetupShell'));
const SetupHubPage = lazy(() => import('./pages/Setup/SetupHubPage'));
// The landing screen: one prioritised queue of everything waiting on this user, built from the
// endpoints the individual queue screens already read. It replaced `/analytics/deadlines`, which
// could only show enquiries and whose only outbound link was to a single lead.
const InboxPage = lazy(() => import('./pages/Inbox/InboxPage'));
// The directory of every screen the five-row rail no longer carries. Nothing is deleted to shrink
// the rail; it is listed here, grouped and described, with its route unchanged.
const AllScreensPage = lazy(() => import('./pages/Advanced/AllScreensPage'));
const ProductsPage = lazy(() => import('./pages/Inventory/ProductsPage'));
const ProductDetailPage = lazy(() => import('./pages/Inventory/ProductDetailPage'));
const ProductCategoryPage = lazy(() => import('./pages/Inventory/ProductCategoryPage'));
const ProductSubCategoryPage = lazy(() => import('./pages/Inventory/ProductSubCategoryPage'));
const InventoryOverviewPage = lazy(() => import('./pages/Inventory/Commercial/InventoryOverviewPage'));
const AvailabilityPage = lazy(() => import('./pages/Inventory/Commercial/AvailabilityPage'));
const WarehousesPage = lazy(() => import('./pages/Inventory/Commercial/WarehousesPage'));
const ReservationsPage = lazy(() => import('./pages/Inventory/Commercial/ReservationsPage'));
const IncomingPage = lazy(() => import('./pages/Inventory/Commercial/IncomingPage'));
const MovementsPage = lazy(() => import('./pages/Inventory/Commercial/MovementsPage'));
const DemandPage = lazy(() => import('./pages/Inventory/Commercial/DemandPage'));
const RelatedResourcesPage = lazy(() => import('./pages/Inventory/Commercial/RelatedResourcesPage'));
const StockLevelsPage = lazy(() => import('./pages/Inventory/Commercial/StockLevelsPage'));
const ReorderAlertsPage = lazy(() => import('./pages/Inventory/Commercial/ReorderAlertsPage'));
const CountVariancePage = lazy(() => import('./pages/Inventory/Commercial/CountVariancePage'));
const StockAgeingPage = lazy(() => import('./pages/Inventory/Commercial/StockAgeingPage'));
// Gate 5 / FR-MTR-01..05 — material lots, certificates, quarantine and where-used trace.
const LotsPage = lazy(() => import('./pages/Inventory/Traceability/LotsPage'));
const LotDetailPage = lazy(() => import('./pages/Inventory/Traceability/LotDetailPage'));
const OrderTracePage = lazy(() => import('./pages/Inventory/Traceability/OrderTracePage'));
const SuppliersPage = lazy(() => import('./pages/Suppliers/SuppliersPage'));
const SupplierDetailPage = lazy(() => import('./pages/Suppliers/SupplierDetailPage'));
const QuotedItemsPage = lazy(() => import('./pages/Suppliers/QuotedItemsPage'));
const PurchaseOrdersPage = lazy(() => import('./pages/Suppliers/PurchaseOrdersPage'));
const CustomersPage = lazy(() => import('./pages/Customers/CustomersPage'));
const CustomerDetailPage = lazy(() => import('./pages/Customers/CustomerDetailPage'));
const LeadsPage = lazy(() => import('./pages/Leads/LeadsPage'));
const OutstandingLeadsPage = lazy(() => import('./pages/Leads/OutstandingLeadsPage'));
const AssignedLeadsPage = lazy(() => import('./pages/Leads/AssignedLeadsPage'));
const ManualUploadLeadsPage = lazy(() => import('./pages/Leads/ManualUploadLeadsPage'));
const WatchedFoldersPage = lazy(() => import('./pages/Leads/WatchedFoldersPage'));
const LeadIngestionBatchPage = lazy(() => import('./pages/Leads/LeadIngestionBatchPage'));
const PossibleMatchesPage = lazy(() => import('./pages/Leads/PossibleMatchesPage'));
const DuplicateUploadsPage = lazy(() => import('./pages/Leads/DuplicateUploadsPage'));
const InboundMailTriagePage = lazy(() => import('./pages/Leads/InboundMailTriagePage'));
const LeadDetailPage = lazy(() => import('./pages/Leads/LeadDetailPage'));
const CommercialCaseWorkspacePage = lazy(() => import('./pages/CommercialCases/CommercialCaseWorkspacePage'));
const ExtractionReviewPage = lazy(() => import('./pages/ExtractionReview/ExtractionReviewPage'));
const ExtractionReviewDetailPage = lazy(() => import('./pages/ExtractionReview/ExtractionReviewDetailPage'));
const AllRFQsPage = lazy(() => import('./pages/Procurement/RFQs/AllRFQsPage'));
const DraftRFQsPage = lazy(() => import('./pages/Procurement/RFQs/DraftRFQsPage'));
const OutstandingRFQsPage = lazy(() => import('./pages/Procurement/RFQs/OutstandingRFQsPage'));
const ProcessRFQPage = lazy(() => import('./pages/Procurement/RFQs/ProcessRFQPage'));
const ViewRFQPage = lazy(() => import('./pages/Procurement/RFQs/ViewRFQPage'));
const SourcingWorkbenchPage = lazy(() => import('./pages/Procurement/Sourcing/SourcingWorkbenchPage'));
const SourcingCasePage = lazy(() => import('./pages/Procurement/Sourcing/SourcingCasePage'));
const SupplierQuoteInboxPage = lazy(() => import('./pages/Procurement/SupplierQuotes/SupplierQuoteInboxPage'));
const SupplierQuoteReviewPage = lazy(() => import('./pages/Procurement/SupplierQuotes/SupplierQuoteReviewPage'));
const CommercialInboxPage = lazy(() => import('./pages/Procurement/SupplierQuotes/CommercialInboxPage'));
const ProcurementHandoffsPage = lazy(() => import('./pages/Procurement/Handoffs/ProcurementHandoffsPage'));
const DashboardPage = lazy(() => import('./pages/Dashboard/DashboardPage'));
const TeamWorkloadPage = lazy(() => import('./pages/Dashboard/TeamWorkloadPage'));
const DeadlineBoardPage = lazy(() => import('./pages/Analytics/DeadlineBoardPage'));
const BrandDemandPage = lazy(() => import('./pages/Analytics/BrandDemandPage'));
const QuotesPage = lazy(() => import('./pages/Sales/Quotes/QuotesPage'));
const CreateQuotePage = lazy(() => import('./pages/Sales/Quotes/CreateQuotePage'));
const QuoteViewPage = lazy(() => import('./pages/Sales/Quotes/QuoteViewPage'));
const EditQuotePage = lazy(() => import('./pages/Sales/Quotes/EditQuotePage'));
const ClientPurchaseOrderInboxPage = lazy(() => import('./pages/Sales/ClientPurchaseOrders/ClientPurchaseOrderInboxPage'));
const ClientPurchaseOrderReviewPage = lazy(() => import('./pages/Sales/ClientPurchaseOrders/ClientPurchaseOrderReviewPage'));
const OrderListPage = lazy(() => import('./pages/Sales/Orders/OrderListPage'));
const CreateOrderPage = lazy(() => import('./pages/Sales/Orders/CreateOrderPage'));
const OrderViewPage = lazy(() => import('./pages/Sales/Orders/OrderViewPage'));
const AccountsReceivablePage = lazy(() => import('./pages/Sales/Finance/AccountsReceivablePage'));
const ShipmentListPage = lazy(() => import('./pages/Sales/Shipments/ShipmentListPage'));
const CreateShipmentPage = lazy(() => import('./pages/Sales/Shipments/CreateShipmentPage'));
const ShipmentViewPage = lazy(() => import('./pages/Sales/Shipments/ShipmentViewPage'));
const ShipmentInvoicePage = lazy(() => import('./pages/Sales/Shipments/ShipmentInvoicePage'));
const SalesTodayPage = lazy(() => import('./pages/SalesManagement/SalesTodayPage'));
const TeamOverviewPage = lazy(() => import('./pages/SalesManagement/TeamOverviewPage'));
const RepDirectoryPage = lazy(() => import('./pages/SalesManagement/RepDirectoryPage'));
const RepProfilePage = lazy(() => import('./pages/SalesManagement/RepProfilePage'));
const AccountOwnershipPage = lazy(() => import('./pages/SalesManagement/AccountOwnershipPage'));
const RoutingQueuePage = lazy(() => import('./pages/SalesManagement/RoutingQueuePage'));
const FollowUpsPage = lazy(() => import('./pages/SalesManagement/FollowUpsPage'));
const PerformancePage = lazy(() => import('./pages/SalesManagement/PerformancePage'));
const CommercialExceptionCenterPage = lazy(() => import('./pages/SalesManagement/CommercialExceptionCenterPage'));
const SourcingTodayPage = lazy(() => import('./pages/Today/SourcingTodayPage'));
const TenantAdminOperationsPage = lazy(() => import('./pages/Today/TenantAdminOperationsPage'));

// Intelligence surfaces — AI-assisted Lead→RFQ conversion and RFQ smart pricing.
const LeadConvertPage = lazy(() => import('./pages/Intelligence/LeadConvertPage'));
const RfqPricingPage = lazy(() => import('./pages/Intelligence/RfqPricingPage'));
const CommercialMemoryPage = lazy(() => import('./pages/Intelligence/CommercialMemoryPage'));
const HumanActionCenterPage = lazy(() => import('./pages/PlatformGovernance/HumanActionCenterPage'));

// Service RFQ → BOQ engine — drafted bills of quantities for service work.
const BoqListPage = lazy(() => import('./pages/Boq/BoqListPage'));
const BoqEditorPage = lazy(() => import('./pages/Boq/BoqEditorPage'));

// Sourcing Copilot — conversational autonomous-agent console (flagship surface).
const CopilotPage = lazy(() => import('./pages/Copilot/CopilotPage'));
const CopilotApprovalsPage = lazy(() => import('./pages/Copilot/ApprovalsPage'));
const CopilotActivityPage = lazy(() => import('./pages/Copilot/ActivityPage'));

// Platform Owner console (ADR-0005). Self-contained `/platform/*` tree with its
// own guard + layout; see src/platform/.
const PlatformRoutes = lazy(() => import('./platform/PlatformRoutes'));

// Account activation for an invited founding administrator. Public by
// necessity — the person opening it has no session yet — and outside MainLayout
// because the app shell renders navigation for a workspace they cannot enter.
const ActivateAccountPage = lazy(() => import('./pages/Activation/ActivateAccountPage'));

// Self-service password recovery. Public for the same reason activation is —
// somebody who cannot sign in cannot be asked to sign in first — and outside
// MainLayout for the same reason: the app shell renders navigation for a
// workspace they cannot enter yet.
const ForgotPasswordPage = lazy(() => import('./pages/PasswordReset/ForgotPasswordPage'));
const ResetPasswordPage = lazy(() => import('./pages/PasswordReset/ResetPasswordPage'));

const PageLoader = () => (
  <Box
    role="status"
    aria-live="polite"
    sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', minHeight: '60vh', width: '100%' }}
  >
    <CircularProgress aria-label="Loading page" />
  </Box>
);

/**
 * 404 view. Sets its own title (the route is by definition not in
 * `routeTitles.ts`) and provides the page's `<h1>` — SC 2.4.2 / SC 1.3.1.
 */
const NotFoundPage = () => {
  useDocumentTitle('Page Not Found');
  return (
    <Box component="main" id="main-content" tabIndex={-1} sx={{ p: 4 }}>
      <Typography variant="h4" component="h1" gutterBottom>
        Page not found
      </Typography>
      <Typography variant="body1" color="text.secondary">
        The page you requested does not exist. Check the address, or use the navigation menu to
        continue.
      </Typography>
    </Box>
  );
};

function App() {
  return (
    <>
    {/* Per-route document title, focus reset, scroll reset and a polite
        route-change announcement — SC 2.4.2 / 2.4.3 / 4.1.3. */}
    <RouteAnnouncer />
    <Suspense fallback={<PageLoader />}>
    <Routes>
      <Route path="/" element={<Navigate to="/login" replace />} />

      {/* The landing screen. Deliberately UNGUARDED at the route: it is the first screen after
          sign-in, and a user whose grants are still loading — or who holds none — must land on a
          page that explains that rather than on "Access Denied". Each queue inside asks for its own
          module and is simply not requested when the grant is absent. */}
      <Route path="/inbox" element={<MainLayout><InboxPage /></MainLayout>} />
      {/* Every screen the rail relocated, grouped and searchable. Ungated for the same reason the
          Setup hub is: the cards are filtered by permission, so an unpermitted user sees a stated
          reason instead of a denial. */}
      <Route path="/advanced" element={<MainLayout><AllScreensPage /></MainLayout>} />
      {/* Old landing addresses. `/analytics/deadlines` stays a live screen (it is listed under
          Dashboards & analytics); these two are the shortcuts people type. */}
      <Route path="/home" element={<Navigate to="/inbox" replace />} />
      <Route path="/today" element={<Navigate to="/inbox" replace />} />

      <Route path="/dashboard" element={<MainLayout><PermissionGuard moduleName="Dashboard"><DashboardPage /></PermissionGuard></MainLayout>} />
      <Route path="/dashboard/team" element={<MainLayout><PermissionGuard moduleName="Dashboard"><TeamWorkloadPage /></PermissionGuard></MainLayout>} />
      {/* Analytics built only on data the tenant actually holds. The deadline
          board is the landing surface for the pilot: /dashboard's KPIs are
          insufficient-data for a new tenant and must not be the first screen. */}
      <Route path="/analytics/deadlines" element={<MainLayout><PermissionGuard moduleName="Leads"><DeadlineBoardPage /></PermissionGuard></MainLayout>} />
      <Route path="/analytics/brand-demand" element={<MainLayout><PermissionGuard moduleName="Leads"><BrandDemandPage /></PermissionGuard></MainLayout>} />
      <Route path="/intelligence/commercial-memory" element={<MainLayout><PermissionGuard moduleName="Dashboard"><PermissionGuard moduleName="Quotations"><CommercialMemoryPage /></PermissionGuard></PermissionGuard></MainLayout>} />

      {/* Sourcing Copilot Routes */}
      <Route path="/copilot" element={<MainLayout><PermissionGuard moduleName="Dashboard"><CopilotPage /></PermissionGuard></MainLayout>} />
      <Route path="/copilot/approvals" element={<MainLayout><PermissionGuard moduleName="Dashboard"><CopilotApprovalsPage /></PermissionGuard></MainLayout>} />
      <Route path="/copilot/activity" element={<MainLayout><PermissionGuard moduleName="Dashboard"><CopilotActivityPage /></PermissionGuard></MainLayout>} />

      {/* Service BOQ Routes — gated by Quotations (BOQs are priced quote material) */}
      <Route path="/services/boq" element={<MainLayout><PermissionGuard moduleName="Quotations"><BoqListPage /></PermissionGuard></MainLayout>} />
      <Route path="/services/boq/:id" element={<MainLayout><PermissionGuard moduleName="Quotations"><BoqEditorPage /></PermissionGuard></MainLayout>} />

      {/* Sales Routes */}
      <Route path="/sales/today" element={<MainLayout><PermissionGuard moduleName="Leads"><SalesTodayPage /></PermissionGuard></MainLayout>} />
      <Route path="/sourcing/today" element={<MainLayout><PermissionGuard moduleName="Supplier History"><SourcingTodayPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/today" element={<MainLayout><PermissionGuard moduleName="Products"><InventoryOverviewPage /></PermissionGuard></MainLayout>} />
      <Route path="/executive/today" element={<MainLayout><PermissionGuard moduleName="Dashboard"><DashboardPage /></PermissionGuard></MainLayout>} />
      <Route path="/admin/operations" element={<MainLayout><PermissionGuard moduleName="Users"><TenantAdminOperationsPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/actions" element={<MainLayout><PermissionGuard moduleName="Leads"><HumanActionCenterPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/team" element={<MainLayout><PermissionGuard moduleName="Leads"><TeamOverviewPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/reps" element={<MainLayout><PermissionGuard moduleName="Users"><RepDirectoryPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/reps/:userId" element={<MainLayout><PermissionGuard moduleName="Users"><RepProfilePage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/accounts" element={<MainLayout><PermissionGuard moduleName="Customers"><AccountOwnershipPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/routing" element={<MainLayout><PermissionGuard moduleName="Leads"><RoutingQueuePage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/follow-ups" element={<MainLayout><PermissionGuard moduleName="Quotations"><FollowUpsPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/performance" element={<MainLayout><PermissionGuard moduleName="Dashboard"><PerformancePage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/exceptions" element={<MainLayout><PermissionGuard moduleName="Leads"><CommercialExceptionCenterPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/quotes" element={<MainLayout><PermissionGuard moduleName="Quotations"><QuotesPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/quotes/create" element={<MainLayout><PermissionGuard moduleName="Quotations" action="create"><CreateQuotePage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/quotes/view/:id" element={<MainLayout><PermissionGuard moduleName="Quotations"><QuoteViewPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/quotes/edit/:id" element={<MainLayout><PermissionGuard moduleName="Quotations" action="edit"><EditQuotePage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/client-pos" element={<MainLayout><PermissionGuard moduleName="Customer Awards"><ClientPurchaseOrderInboxPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/client-pos/:clientPoId" element={<MainLayout><PermissionGuard moduleName="Customer Awards"><ClientPurchaseOrderReviewPage /></PermissionGuard></MainLayout>} />
      
      <Route path="/sales/orders" element={<MainLayout><PermissionGuard moduleName="Orders"><OrderListPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/orders/create" element={<MainLayout><PermissionGuard moduleName="Orders" action="create"><CreateOrderPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/orders/edit/:id" element={<MainLayout><PermissionGuard moduleName="Orders" action="edit"><CreateOrderPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/orders/:id" element={<MainLayout><PermissionGuard moduleName="Orders"><OrderViewPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/finance" element={<MainLayout><PermissionGuard moduleName="Accounts Receivable"><AccountsReceivablePage /></PermissionGuard></MainLayout>} />
      
      <Route path="/sales/shipments" element={<MainLayout><PermissionGuard moduleName="Shipments"><ShipmentListPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/shipments/create" element={<MainLayout><PermissionGuard moduleName="Shipments" action="create"><CreateShipmentPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/shipments/from-order/:id" element={<MainLayout><PermissionGuard moduleName="Shipments" action="create"><CreateShipmentPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/shipments/edit/:id" element={<MainLayout><PermissionGuard moduleName="Shipments" action="edit"><CreateShipmentPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/shipments/:id" element={<MainLayout><PermissionGuard moduleName="Shipments"><ShipmentViewPage /></PermissionGuard></MainLayout>} />
      
      {/* Sales Invoices/Documents */}
      {/* No order-level tax-invoice route: the governed AR document is issued by the finance
          subsystem and is reached via /sales/finance. A page that renders an order as an
          "invoice" is not the issued document and must not exist before the ZATCA gate. */}
      <Route path="/sales/shipments/invoice/:id" element={<PermissionGuard moduleName="Shipments"><ShipmentInvoicePage /></PermissionGuard>} />
      
      <Route path="/orders" element={<Navigate to="/sales/orders" replace />} />
      <Route path="/quotations" element={<Navigate to="/sales/quotes" replace />} />
      
      {/* RFQ Routes */}
      <Route path="/procurement/rfqs" element={<Navigate to="/procurement/rfqs/all" replace />} />
      <Route path="/procurement/rfqs/all" element={<MainLayout><PermissionGuard moduleName="RFQ Management"><AllRFQsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/draft" element={<MainLayout><PermissionGuard moduleName="RFQ Management"><DraftRFQsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/outstanding" element={<MainLayout><PermissionGuard moduleName="RFQ Management"><OutstandingRFQsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/process/:id" element={<MainLayout><PermissionGuard moduleName="RFQ Management" action="edit"><ProcessRFQPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/view/:id" element={<MainLayout><PermissionGuard moduleName="RFQ Management"><ViewRFQPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/:id/pricing" element={<MainLayout><PermissionGuard moduleName="RFQ Management"><PermissionGuard moduleName="Quotations"><RfqPricingPage /></PermissionGuard></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/:rfqId/sourcing" element={<MainLayout><PermissionGuard moduleName="RFQ Management"><SourcingWorkbenchPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/sourcing" element={<Navigate to="/procurement/sourcing-cases" replace />} />
      <Route path="/procurement/sourcing-cases" element={<Navigate to="/procurement/rfqs/all?state=requires-sourcing" replace />} />
      <Route path="/procurement/sourcing-cases/:caseId" element={<MainLayout><PermissionGuard moduleName="RFQ Management"><SourcingCasePage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/supplier-quotes" element={<MainLayout><PermissionGuard moduleName="Supplier History"><SupplierQuoteInboxPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/supplier-quotes/:supplierQuoteId" element={<MainLayout><PermissionGuard moduleName="Supplier History"><SupplierQuoteReviewPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/commercial-inbox" element={<MainLayout><PermissionGuard moduleName="Supplier History"><CommercialInboxPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/handoffs" element={<MainLayout><PermissionGuard moduleName="Orders"><ProcurementHandoffsPage /></PermissionGuard></MainLayout>} />
      <Route path="/rfqs/view/:id" element={<MainLayout><PermissionGuard moduleName="RFQ Management"><ViewRFQPage /></PermissionGuard></MainLayout>} />
      <Route path="/rfqs" element={<Navigate to="/procurement/rfqs/all" replace />} />
      
      {/* Setup Routes — one shell over a nested tree, so `/setup` is a real place (the hub) and
          every screen below it inherits the breadcrumb and the jump field. What each screen is,
          and which group it belongs to, is declared once in `pages/Setup/setupCatalog.tsx`;
          `setupCatalog.test.ts` fails if a route here is missing from it or listed twice. */}
      <Route path="/setup" element={<MainLayout><SetupShell /></MainLayout>}>
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
      <Route element={<MainLayout><SetupShell /></MainLayout>}>
        {SETUP_ADOPTED_ROUTES.map(({ path, moduleName, component: Screen }) => (
          <Route
            key={path}
            path={path}
            element={<PermissionGuard moduleName={moduleName}><Screen /></PermissionGuard>}
          />
        ))}
      </Route>

      {/* Inventory Routes */}
      <Route path="/inventory/overview" element={<MainLayout><PermissionGuard moduleName="Products"><InventoryOverviewPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/availability" element={<MainLayout><PermissionGuard moduleName="Products"><AvailabilityPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/warehouses" element={<MainLayout><PermissionGuard moduleName="Products"><WarehousesPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/reservations" element={<MainLayout><PermissionGuard moduleName="Products"><ReservationsPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/incoming" element={<MainLayout><PermissionGuard moduleName="Products"><IncomingPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/movements" element={<MainLayout><PermissionGuard moduleName="Products"><MovementsPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/demand" element={<MainLayout><PermissionGuard moduleName="Products"><DemandPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/resources" element={<MainLayout><PermissionGuard moduleName="Products"><RelatedResourcesPage /></PermissionGuard></MainLayout>} />
      {/* FR-INV-04/05/06. Four screens that had no interface at all: minimum/maximum levels and the
          reorder alert ledger, and the two reports whose endpoints were complete and unreachable. */}
      <Route path="/inventory/levels" element={<MainLayout><PermissionGuard moduleName="Products"><StockLevelsPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/reorder-alerts" element={<MainLayout><PermissionGuard moduleName="Products"><ReorderAlertsPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/count-variance" element={<MainLayout><PermissionGuard moduleName="Products"><CountVariancePage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/ageing" element={<MainLayout><PermissionGuard moduleName="Products"><StockAgeingPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/lots" element={<MainLayout><PermissionGuard moduleName="Products"><LotsPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/lots/:lotId" element={<MainLayout><PermissionGuard moduleName="Products"><LotDetailPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/order-trace" element={<MainLayout><PermissionGuard moduleName="Products"><PermissionGuard moduleName="Orders"><OrderTracePage /></PermissionGuard></PermissionGuard></MainLayout>} />
      <Route path="/inventory/order-trace/:orderId" element={<MainLayout><PermissionGuard moduleName="Products"><PermissionGuard moduleName="Orders"><OrderTracePage /></PermissionGuard></PermissionGuard></MainLayout>} />
      <Route path="/inventory/products" element={<MainLayout><PermissionGuard moduleName="Products"><ProductsPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/products/:id" element={<MainLayout><PermissionGuard moduleName="Products"><ProductDetailPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/categories" element={<MainLayout><PermissionGuard moduleName="Product Categories"><ProductCategoryPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/sub-categories" element={<MainLayout><PermissionGuard moduleName="Product Categories"><ProductSubCategoryPage /></PermissionGuard></MainLayout>} />
      
      {/* Supplier Routes */}
      <Route path="/suppliers" element={<MainLayout><PermissionGuard moduleName="Suppliers"><SuppliersPage /></PermissionGuard></MainLayout>} />
      <Route path="/suppliers/:id" element={<MainLayout><PermissionGuard moduleName="Suppliers"><SupplierDetailPage /></PermissionGuard></MainLayout>} />
      <Route path="/suppliers/quoted-items" element={<MainLayout><PermissionGuard moduleName="Supplier History"><QuotedItemsPage /></PermissionGuard></MainLayout>} />
      <Route path="/suppliers/purchase-orders" element={<MainLayout><PermissionGuard moduleName="Orders"><PurchaseOrdersPage /></PermissionGuard></MainLayout>} />
      
      {/* Customer Routes */}
      <Route path="/customers" element={<MainLayout><PermissionGuard moduleName="Customers"><CustomersPage /></PermissionGuard></MainLayout>} />
      <Route path="/customers/:id" element={<MainLayout><PermissionGuard moduleName="Customers"><CustomerDetailPage /></PermissionGuard></MainLayout>} />

      {/* Extraction Review Routes */}
      <Route path="/procurement/extraction/review" element={<MainLayout><PermissionGuard moduleName="Leads"><ExtractionReviewPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/extraction/review/:id" element={<MainLayout><PermissionGuard moduleName="Leads"><ExtractionReviewDetailPage /></PermissionGuard></MainLayout>} />

      {/* Lead Management Routes */}
      <Route path="/procurement/leads/all" element={<MainLayout><PermissionGuard moduleName="Leads"><LeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/intelligence" element={<MainLayout><PermissionGuard moduleName="Leads" action="create"><ManualUploadLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/outstanding" element={<MainLayout><PermissionGuard moduleName="Leads"><OutstandingLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/assigned" element={<MainLayout><PermissionGuard moduleName="Leads"><AssignedLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/manual-upload" element={<MainLayout><PermissionGuard moduleName="Leads" action="create"><ManualUploadLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/ingestion/:batchId" element={<MainLayout><PermissionGuard moduleName="Leads"><LeadIngestionBatchPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/possible-matches" element={<MainLayout><PermissionGuard moduleName="Leads"><PossibleMatchesPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/duplicates" element={<MainLayout><PermissionGuard moduleName="Leads"><DuplicateUploadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/inbound-mail" element={<MainLayout><PermissionGuard moduleName="Leads"><InboundMailTriagePage /></PermissionGuard></MainLayout>} />
      {/*
        The watched-folder intake channel (FolderService, swept by EmailBackgroundService). The old
        route redirected to manual upload, which left a channel that really runs on the server with
        no operator surface at all.
      */}
      <Route path="/procurement/leads/watched-folders" element={<MainLayout><PermissionGuard moduleName="Leads"><WatchedFoldersPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/folder-upload" element={<Navigate to="/procurement/leads/watched-folders" replace />} />
      <Route path="/procurement/leads/view/:id" element={<MainLayout><PermissionGuard moduleName="Leads"><LeadDetailPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/:id/convert" element={<MainLayout><PermissionGuard moduleName="Leads"><LeadConvertPage /></PermissionGuard></MainLayout>} />
      <Route path="/commercial-cases/:id?" element={<MainLayout><PermissionGuard moduleName="Leads"><CommercialCaseWorkspacePage /></PermissionGuard></MainLayout>} />
      
      {/* Short Lead Routes */}
      <Route path="/leads/all" element={<Navigate to="/procurement/leads/all" replace />} />
      <Route path="/leads/outstanding" element={<Navigate to="/procurement/leads/outstanding" replace />} />
      <Route path="/leads/assigned" element={<Navigate to="/procurement/leads/assigned" replace />} />
      <Route path="/leads/manual-upload" element={<Navigate to="/procurement/leads/manual-upload" replace />} />
      <Route path="/leads/folder-upload" element={<Navigate to="/procurement/leads/watched-folders" replace />} />
      <Route path="/leads/watched-folders" element={<Navigate to="/procurement/leads/watched-folders" replace />} />
      <Route path="/leads/view/:id" element={<MainLayout><PermissionGuard moduleName="Leads"><LeadDetailPage /></PermissionGuard></MainLayout>} />
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
