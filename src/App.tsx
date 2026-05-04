import { Routes, Route, Navigate } from 'react-router-dom';
import MainLayout from './components/layout/MainLayout';
import LoginPage from './pages/Login/LoginPage';
import SetupMaster from './pages/Setup/SetupMaster';
import CurrencyPage from './pages/Setup/Currency/CurrencyPage';
import WarehousePage from './pages/Setup/Warehouse/WarehousePage';
import UomPage from './pages/Setup/UOM/UomPage';
import QuoteFormatPage from './pages/Setup/QuoteFormat/QuoteFormatPage';
import LocationMaster from './pages/Setup/Location/LocationMaster';
import UsersPage from './pages/Security/Users/UsersPage';
import RolesPermissionsPage from './pages/Security/Roles/RolesPermissionsPage';
import BusinessUnitPage from './pages/Setup/BusinessUnit/BusinessUnitPage';
import ProductsPage from './pages/Inventory/ProductsPage';
import ProductDetailPage from './pages/Inventory/ProductDetailPage';
import ProductCategoryPage from './pages/Inventory/ProductCategoryPage';
import ProductSubCategoryPage from './pages/Inventory/ProductSubCategoryPage';
import SuppliersPage from './pages/Suppliers/SuppliersPage';
import SupplierDetailPage from './pages/Suppliers/SupplierDetailPage';
import QuotedItemsPage from './pages/Suppliers/QuotedItemsPage';
import PurchaseOrdersPage from './pages/Suppliers/PurchaseOrdersPage';
import CustomersPage from './pages/Customers/CustomersPage';
import CustomerDetailPage from './pages/Customers/CustomerDetailPage';
import LeadsPage from './pages/Leads/LeadsPage';
import OutstandingLeadsPage from './pages/Leads/OutstandingLeadsPage';
import AssignedLeadsPage from './pages/Leads/AssignedLeadsPage';
import ManualUploadLeadsPage from './pages/Leads/ManualUploadLeadsPage';
import FolderUploadLeadsPage from './pages/Leads/FolderUploadLeadsPage';
import LeadDetailPage from './pages/Leads/LeadDetailPage';
import AllRFQsPage from './pages/Procurement/RFQs/AllRFQsPage';
import DraftRFQsPage from './pages/Procurement/RFQs/DraftRFQsPage';
import OutstandingRFQsPage from './pages/Procurement/RFQs/OutstandingRFQsPage';
import ProcessRFQPage from './pages/Procurement/RFQs/ProcessRFQPage';
import ViewRFQPage from './pages/Procurement/RFQs/ViewRFQPage';
import DashboardPage from './pages/Dashboard/DashboardPage';
import QuotesPage from './pages/Sales/Quotes/QuotesPage';
import CreateQuotePage from './pages/Sales/Quotes/CreateQuotePage';
import QuoteViewPage from './pages/Sales/Quotes/QuoteViewPage';
import EditQuotePage from './pages/Sales/Quotes/EditQuotePage';
import OrderListPage from './pages/Sales/Orders/OrderListPage';
import CreateOrderPage from './pages/Sales/Orders/CreateOrderPage';
import OrderViewPage from './pages/Sales/Orders/OrderViewPage';
import OrderInvoicePage from './pages/Sales/Shipments/OrderInvoicePage';
import ShipmentListPage from './pages/Sales/Shipments/ShipmentListPage';
import CreateShipmentPage from './pages/Sales/Shipments/CreateShipmentPage';
import ShipmentViewPage from './pages/Sales/Shipments/ShipmentViewPage';
import { Box } from '@mui/material';
import ShipmentInvoicePage from './pages/Sales/Shipments/ShipmentInvoicePage';
import PermissionGuard from './components/common/PermissionGuard';

function App() {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/dashboard" replace />} />
      <Route path="/dashboard" element={<MainLayout><PermissionGuard moduleName="Dashboard" redirect><DashboardPage /></PermissionGuard></MainLayout>} />
      
      {/* Sales Routes */}
      <Route path="/sales/quotes" element={<MainLayout><PermissionGuard moduleName="Quotations" redirect><QuotesPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/quotes/create" element={<MainLayout><PermissionGuard moduleName="Quotations" action="create" redirect><CreateQuotePage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/quotes/view/:id" element={<MainLayout><PermissionGuard moduleName="Quotations" redirect><QuoteViewPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/quotes/edit/:id" element={<MainLayout><PermissionGuard moduleName="Quotations" action="edit" redirect><EditQuotePage /></PermissionGuard></MainLayout>} />
      
      <Route path="/sales/orders" element={<MainLayout><PermissionGuard moduleName="Orders" redirect><OrderListPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/orders/create" element={<MainLayout><PermissionGuard moduleName="Orders" action="create" redirect><CreateOrderPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/orders/edit/:id" element={<MainLayout><PermissionGuard moduleName="Orders" action="edit" redirect><CreateOrderPage /></PermissionGuard></MainLayout>} />
      <Route path="/sales/orders/:id" element={<MainLayout><PermissionGuard moduleName="Orders" redirect><OrderViewPage /></PermissionGuard></MainLayout>} />
      
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

      {/* Security Routes */}
      <Route path="/security/users" element={<MainLayout><PermissionGuard moduleName="Users" redirect><UsersPage /></PermissionGuard></MainLayout>} />
      <Route path="/security/roles" element={<MainLayout><PermissionGuard moduleName="Roles & Permissions" redirect><RolesPermissionsPage /></PermissionGuard></MainLayout>} />

      {/* Inventory Routes */}
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

      {/* Lead Management Routes */}
      <Route path="/procurement/leads/all" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><LeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/outstanding" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><OutstandingLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/assigned" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><AssignedLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/manual-upload" element={<MainLayout><PermissionGuard moduleName="Leads" action="create" redirect><ManualUploadLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/folder-upload" element={<MainLayout><PermissionGuard moduleName="Leads" action="create" redirect><FolderUploadLeadsPage /></PermissionGuard></MainLayout>} />
      <Route path="/procurement/leads/view/:id" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><LeadDetailPage /></PermissionGuard></MainLayout>} />
      
      {/* Short Lead Routes */}
      <Route path="/leads/all" element={<Navigate to="/procurement/leads/all" replace />} />
      <Route path="/leads/outstanding" element={<Navigate to="/procurement/leads/outstanding" replace />} />
      <Route path="/leads/assigned" element={<Navigate to="/procurement/leads/assigned" replace />} />
      <Route path="/leads/manual-upload" element={<Navigate to="/procurement/leads/manual-upload" replace />} />
      <Route path="/leads/folder-upload" element={<Navigate to="/procurement/leads/folder-upload" replace />} />
      <Route path="/leads/view/:id" element={<MainLayout><PermissionGuard moduleName="Leads" redirect><LeadDetailPage /></PermissionGuard></MainLayout>} />
      <Route path="/leads" element={<Navigate to="/procurement/leads/all" replace />} />

      <Route path="/login" element={<LoginPage />} />
      <Route path="*" element={<Box sx={{ p: 4 }}>404 Not Found</Box>} />
    </Routes>
  );
}

export default App;
