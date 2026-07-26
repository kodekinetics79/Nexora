import { lazy, Suspense } from 'react';
import { Routes, Route, Navigate } from 'react-router-dom';
import { Box, CircularProgress } from '@mui/material';
import MainLayout from './components/layout/MainLayout';
import PermissionGuard from './components/common/PermissionGuard';

// FE-09: route-level code splitting. Each page is loaded on demand so the
// initial bundle only ships the app shell (layout, guards, providers).
const LoginPage = lazy(() => import('./pages/Login/LoginPage'));
const SetupMaster = lazy(() => import('./pages/Setup/SetupMaster'));
const CurrencyPage = lazy(() => import('./pages/Setup/Currency/CurrencyPage'));
const WarehousePage = lazy(() => import('./pages/Setup/Warehouse/WarehousePage'));
const UomPage = lazy(() => import('./pages/Setup/UOM/UomPage'));
const QuoteFormatPage = lazy(() => import('./pages/Setup/QuoteFormat/QuoteFormatPage'));
const LocationMaster = lazy(() => import('./pages/Setup/Location/LocationMaster'));
const UsersPage = lazy(() => import('./pages/Security/Users/UsersPage'));
const RolesPermissionsPage = lazy(() => import('./pages/Security/Roles/RolesPermissionsPage'));
const BusinessUnitPage = lazy(() => import('./pages/Setup/BusinessUnit/BusinessUnitPage'));
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
const LeadIngestionBatchPage = lazy(() => import('./pages/Leads/LeadIngestionBatchPage'));
const PossibleMatchesPage = lazy(() => import('./pages/Leads/PossibleMatchesPage'));
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
const DashboardPage = lazy(() => import('./pages/Dashboard/DashboardPage'));
const TeamWorkloadPage = lazy(() => import('./pages/Dashboard/TeamWorkloadPage'));
const QuotesPage = lazy(() => import('./pages/Sales/Quotes/QuotesPage'));
const CreateQuotePage = lazy(() => import('./pages/Sales/Quotes/CreateQuotePage'));
const QuoteViewPage = lazy(() => import('./pages/Sales/Quotes/QuoteViewPage'));
const EditQuotePage = lazy(() => import('./pages/Sales/Quotes/EditQuotePage'));
const OrderListPage = lazy(() => import('./pages/Sales/Orders/OrderListPage'));
const CreateOrderPage = lazy(() => import('./pages/Sales/Orders/CreateOrderPage'));
const OrderViewPage = lazy(() => import('./pages/Sales/Orders/OrderViewPage'));
const OrderInvoicePage = lazy(() => import('./pages/Sales/Shipments/OrderInvoicePage'));
const AccountsReceivablePage = lazy(() => import('./pages/Sales/Finance/AccountsReceivablePage'));
const ShipmentListPage = lazy(() => import('./pages/Sales/Shipments/ShipmentListPage'));
const CreateShipmentPage = lazy(() => import('./pages/Sales/Shipments/CreateShipmentPage'));
const ShipmentViewPage = lazy(() => import('./pages/Sales/Shipments/ShipmentViewPage'));
const ShipmentInvoicePage = lazy(() => import('./pages/Sales/Shipments/ShipmentInvoicePage'));
const PriceStructurePage = lazy(() => import('./pages/Setup/PriceStructure/PriceStructurePage'));
const SlaSettingsPage = lazy(() => import('./pages/Setup/Sla/SlaSettingsPage'));
const SalesTodayPage = lazy(() => import('./pages/SalesManagement/SalesTodayPage'));
const TeamOverviewPage = lazy(() => import('./pages/SalesManagement/TeamOverviewPage'));
const RepDirectoryPage = lazy(() => import('./pages/SalesManagement/RepDirectoryPage'));
const RepProfilePage = lazy(() => import('./pages/SalesManagement/RepProfilePage'));
const AccountOwnershipPage = lazy(() => import('./pages/SalesManagement/AccountOwnershipPage'));
const RoutingQueuePage = lazy(() => import('./pages/SalesManagement/RoutingQueuePage'));
const FollowUpsPage = lazy(() => import('./pages/SalesManagement/FollowUpsPage'));
const PerformancePage = lazy(() => import('./pages/SalesManagement/PerformancePage'));

// Intelligence surfaces — AI-assisted Lead→RFQ conversion and RFQ smart pricing.
const LeadConvertPage = lazy(() => import('./pages/Intelligence/LeadConvertPage'));
const RfqPricingPage = lazy(() => import('./pages/Intelligence/RfqPricingPage'));

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

const PageLoader = () => (
  <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', minHeight: '60vh', width: '100%' }}>
    <CircularProgress />
  </Box>
);

function App() {
  return (
    <Suspense fallback={<PageLoader />}>
    <Routes>
      <Route path="/" element={<Navigate to="/login" replace />} />
      <Route path="/dashboard" element={<MainLayout><PermissionGuard moduleName="Dashboard" redirect><DashboardPage /></PermissionGuard></MainLayout>} />
      <Route path="/dashboard/team" element={<MainLayout><PermissionGuard moduleName="Dashboard" redirect><TeamWorkloadPage /></PermissionGuard></MainLayout>} />

      {/* Sourcing Copilot Routes */}
      <Route path="/copilot" element={<MainLayout><PermissionGuard moduleName="Dashboard" redirect><CopilotPage /></PermissionGuard></MainLayout>} />
      <Route path="/copilot/approvals" element={<MainLayout><PermissionGuard moduleName="Dashboard" redirect><CopilotApprovalsPage /></PermissionGuard></MainLayout>} />
      <Route path="/copilot/activity" element={<MainLayout><PermissionGuard moduleName="Dashboard" redirect><CopilotActivityPage /></PermissionGuard></MainLayout>} />

      {/* Service BOQ Routes — gated by Quotations (BOQs are priced quote material) */}
      <Route path="/services/boq" element={<MainLayout><PermissionGuard moduleName="Quotations" redirect><BoqListPage /></PermissionGuard></MainLayout>} />
      <Route path="/services/boq/:id" element={<MainLayout><PermissionGuard moduleName="Quotations" redirect><BoqEditorPage /></PermissionGuard></MainLayout>} />

      {/* Sales Routes */}
      <Route path="/sales/today" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><SalesTodayPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/team" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><TeamOverviewPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/reps" element={<MainLayout><PermissionGuard moduleName="Users" redirect><RepDirectoryPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/reps/:userId" element={<MainLayout><PermissionGuard moduleName="Users" redirect><RepProfilePage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/accounts" element={<MainLayout><PermissionGuard moduleName="Customers" redirect><AccountOwnershipPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/routing" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><RoutingQueuePage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/follow-ups" element={<MainLayout><PermissionGuard moduleName="Quotations" redirect><FollowUpsPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/performance" element={<MainLayout><PermissionGuard moduleName="Dashboard" redirect><PerformancePage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/quotes" element={<MainLayout><PermissionGuard moduleName="Quotations" redirect><QuotesPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/quotes/create" element={<MainLayout><PermissionGuard moduleName="Quotations" action="create" redirect><CreateQuotePage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/quotes/view/:id" element={<MainLayout><PermissionGuard moduleName="Quotations" redirect><QuoteViewPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/quotes/edit/:id" element={<MainLayout><PermissionGuard moduleName="Quotations" action="edit" redirect><EditQuotePage /></PermissionGuard></MainLayout>} />
      
      <Route path="/sales/orders" element={<MainLayout><PermissionGuard moduleName="Orders" redirect><OrderListPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/orders/create" element={<MainLayout><PermissionGuard moduleName="Orders" action="create" redirect><CreateOrderPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/orders/edit/:id" element={<MainLayout><PermissionGuard moduleName="Orders" action="edit" redirect><CreateOrderPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/orders/:id" element={<MainLayout><PermissionGuard moduleName="Orders" redirect><OrderViewPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/finance" element={<MainLayout><PermissionGuard moduleName="Accounts Receivable" redirect><AccountsReceivablePage /></PermissionGuard></MainLayout>} />
      
      <Route path="/sales/shipments" element={<MainLayout><PermissionGuard moduleName="Shipments" redirect><ShipmentListPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/shipments/create" element={<MainLayout><PermissionGuard moduleName="Shipments" action="create" redirect><CreateShipmentPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/shipments/from-order/:id" element={<MainLayout><PermissionGuard moduleName="Shipments" action="create" redirect><CreateShipmentPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/shipments/edit/:id" element={<MainLayout><PermissionGuard moduleName="Shipments" action="edit" redirect><CreateShipmentPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/shipments/:id" element={<MainLayout><PermissionGuard moduleName="Shipments" redirect><ShipmentViewPage /></PermissionGuard></MainLayout>} />
      
      {/* Sales Invoices/Documents */}
      <Route path="/sales/orders/invoice/:id" element={<PermissionGuard moduleName="Orders" redirect><OrderInvoicePage /></PermissionGuard>} />
      <Route path="/sales/shipments/invoice/:id" element={<PermissionGuard moduleName="Shipments" redirect><ShipmentInvoicePage /></PermissionGuard>} />
      
      <Route path="/orders" element={<Navigate to="/sales/orders" replace />} />
      <Route path="/quotations" element={<Navigate to="/sales/quotes" replace />} />
      
      {/* RFQ Routes */}
      <Route path="/procurement/rfqs" element={<Navigate to="/procurement/rfqs/all" replace />} />
      <Route path="/procurement/rfqs/all" element={<MainLayout><PermissionGuard moduleName="RFQ Management" redirect><AllRFQsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/draft" element={<MainLayout><PermissionGuard moduleName="RFQ Management" redirect><DraftRFQsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/outstanding" element={<MainLayout><PermissionGuard moduleName="RFQ Management" redirect><OutstandingRFQsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/process/:id" element={<MainLayout><PermissionGuard moduleName="RFQ Management" action="edit" redirect><ProcessRFQPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/view/:id" element={<MainLayout><PermissionGuard moduleName="RFQ Management" redirect><ViewRFQPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/:id/pricing" element={<MainLayout><PermissionGuard moduleName="RFQ Management" redirect><RfqPricingPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/rfqs/:rfqId/sourcing" element={<MainLayout><PermissionGuard moduleName="RFQ Management" redirect><SourcingWorkbenchPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/sourcing" element={<Navigate to="/procurement/sourcing-cases" replace />} />
      <Route path="/procurement/sourcing-cases" element={<Navigate to="/procurement/rfqs/all?state=requires-sourcing" replace />} />
      <Route path="/procurement/sourcing-cases/:caseId" element={<MainLayout><PermissionGuard moduleName="RFQ Management" redirect><SourcingCasePage /></PermissionGuard></MainLayout>} />
      <Route path="/rfqs/view/:id" element={<MainLayout><PermissionGuard moduleName="RFQ Management" redirect><ViewRFQPage /></PermissionGuard></MainLayout>} />
      <Route path="/rfqs" element={<Navigate to="/procurement/rfqs/all" replace />} />
      
      {/* Setup Routes */}
      <Route path="/setup/master" element={<MainLayout><PermissionGuard moduleName="UOM" redirect><SetupMaster /></PermissionGuard></MainLayout>} />
      <Route path="/setup/currency" element={<MainLayout><PermissionGuard moduleName="Currency" redirect><CurrencyPage /></PermissionGuard></MainLayout>} />
      <Route path="/setup/warehouse" element={<MainLayout><PermissionGuard moduleName="Warehouse" redirect><WarehousePage /></PermissionGuard></MainLayout>} />
      <Route path="/setup/uom" element={<MainLayout><PermissionGuard moduleName="UOM" redirect><UomPage /></PermissionGuard></MainLayout>} />
      <Route path="/setup/locations" element={<MainLayout><PermissionGuard moduleName="Locations" redirect><LocationMaster /></PermissionGuard></MainLayout>} />
      <Route path="/setup/quote-format" element={<MainLayout><PermissionGuard moduleName="Quote Configuration" redirect><QuoteFormatPage /></PermissionGuard></MainLayout>} />
      <Route path="/setup/business-unit" element={<MainLayout><PermissionGuard moduleName="Business Units" redirect><BusinessUnitPage /></PermissionGuard></MainLayout>} />
      <Route path="/setup/price-structure" element={<MainLayout><PermissionGuard moduleName="UOM" redirect><PriceStructurePage /></PermissionGuard></MainLayout>} />
      {/* SLA & alert policy (WP-A2). Guarded by the generic setup module ("UOM"),
          matching /setup/master and /setup/price-structure. */}
      <Route path="/setup/sla" element={<MainLayout><PermissionGuard moduleName="UOM" redirect><SlaSettingsPage /></PermissionGuard></MainLayout>} />

      {/* Security Routes */}
      <Route path="/security/users" element={<MainLayout><PermissionGuard moduleName="Users" redirect><UsersPage /></PermissionGuard></MainLayout>} />
      <Route path="/security/roles" element={<MainLayout><PermissionGuard moduleName="Roles & Permissions" redirect><RolesPermissionsPage /></PermissionGuard></MainLayout>} />

      {/* Inventory Routes */}
      <Route path="/inventory/overview" element={<MainLayout><PermissionGuard moduleName="Products" redirect><InventoryOverviewPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/availability" element={<MainLayout><PermissionGuard moduleName="Products" redirect><AvailabilityPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/warehouses" element={<MainLayout><PermissionGuard moduleName="Products" redirect><WarehousesPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/reservations" element={<MainLayout><PermissionGuard moduleName="Products" redirect><ReservationsPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/incoming" element={<MainLayout><PermissionGuard moduleName="Products" redirect><IncomingPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/movements" element={<MainLayout><PermissionGuard moduleName="Products" redirect><MovementsPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/demand" element={<MainLayout><PermissionGuard moduleName="Products" redirect><DemandPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/resources" element={<MainLayout><PermissionGuard moduleName="Products" redirect><RelatedResourcesPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/products" element={<MainLayout><PermissionGuard moduleName="Products" redirect><ProductsPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/products/:id" element={<MainLayout><PermissionGuard moduleName="Products" redirect><ProductDetailPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/categories" element={<MainLayout><PermissionGuard moduleName="Product Categories" redirect><ProductCategoryPage /></PermissionGuard></MainLayout>} />
      <Route path="/inventory/sub-categories" element={<MainLayout><PermissionGuard moduleName="Product Categories" redirect><ProductSubCategoryPage /></PermissionGuard></MainLayout>} />
      
      {/* Supplier Routes */}
      <Route path="/suppliers" element={<MainLayout><PermissionGuard moduleName="Suppliers" redirect><SuppliersPage /></PermissionGuard></MainLayout>} />
      <Route path="/suppliers/:id" element={<MainLayout><PermissionGuard moduleName="Suppliers" redirect><SupplierDetailPage /></PermissionGuard></MainLayout>} />
      <Route path="/suppliers/quoted-items" element={<MainLayout><PermissionGuard moduleName="Supplier History" redirect><QuotedItemsPage /></PermissionGuard></MainLayout>} />
      <Route path="/suppliers/purchase-orders" element={<MainLayout><PermissionGuard moduleName="Orders" redirect><PurchaseOrdersPage /></PermissionGuard></MainLayout>} />
      
      {/* Customer Routes */}
      <Route path="/customers" element={<MainLayout><PermissionGuard moduleName="Customers" redirect><CustomersPage /></PermissionGuard></MainLayout>} />
      <Route path="/customers/:id" element={<MainLayout><PermissionGuard moduleName="Customers" redirect><CustomerDetailPage /></PermissionGuard></MainLayout>} />

      {/* Extraction Review Routes */}
      <Route path="/procurement/extraction/review" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><ExtractionReviewPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/extraction/review/:id" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><ExtractionReviewDetailPage /></PermissionGuard></MainLayout>} />

      {/* Lead Management Routes */}
      <Route path="/procurement/leads/all" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><LeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/intelligence" element={<MainLayout><PermissionGuard moduleName="Leads" action="create" redirect><ManualUploadLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/outstanding" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><OutstandingLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/assigned" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><AssignedLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/manual-upload" element={<MainLayout><PermissionGuard moduleName="Leads" action="create" redirect><ManualUploadLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/ingestion/:batchId" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><LeadIngestionBatchPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/possible-matches" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><PossibleMatchesPage /></PermissionGuard></MainLayout>} />
      {/* Customer 1 / Customer 2 folder-upload prototype removed from intake. Redirect legacy links to manual upload. */}
      <Route path="/procurement/leads/folder-upload" element={<Navigate to="/procurement/leads/manual-upload" replace />} />
      <Route path="/procurement/leads/view/:id" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><LeadDetailPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/:id/convert" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><LeadConvertPage /></PermissionGuard></MainLayout>} />
      <Route path="/commercial-cases/:id?" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><CommercialCaseWorkspacePage /></PermissionGuard></MainLayout>} />
      
      {/* Short Lead Routes */}
      <Route path="/leads/all" element={<Navigate to="/procurement/leads/all" replace />} />
      <Route path="/leads/outstanding" element={<Navigate to="/procurement/leads/outstanding" replace />} />
      <Route path="/leads/assigned" element={<Navigate to="/procurement/leads/assigned" replace />} />
      <Route path="/leads/manual-upload" element={<Navigate to="/procurement/leads/manual-upload" replace />} />
      <Route path="/leads/folder-upload" element={<Navigate to="/procurement/leads/manual-upload" replace />} />
      <Route path="/leads/view/:id" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><LeadDetailPage /></PermissionGuard></MainLayout>} />
      <Route path="/leads" element={<Navigate to="/procurement/leads/all" replace />} />

      {/* Platform Owner console — owner-only control plane above tenants */}
      <Route path="/platform/*" element={<PlatformRoutes />} />

      <Route path="/login" element={<LoginPage />} />
      <Route path="*" element={<Box sx={{ p: 4 }}>404 Not Found</Box>} />
    </Routes>
    </Suspense>
  );
}

export default App;
