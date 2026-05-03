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
import { Box, Typography, Paper } from '@mui/material';

function App() {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/dashboard" replace />} />
      <Route path="/dashboard" element={
        <MainLayout>
          <Typography variant="h4" gutterBottom sx={{ fontWeight: 'bold' }}>Dashboard</Typography>
          <Paper sx={{ p: 3 }}>
            <Typography variant="body1">
              Welcome to the new RFQ Automation Dashboard. This version is built with Vite, React 19, and MUI 6.
            </Typography>
          </Paper>
        </MainLayout>
      } />
      <Route path="/procurement/rfqs" element={<Navigate to="/procurement/rfqs/all" replace />} />
      <Route path="/procurement/rfqs/all" element={<MainLayout><AllRFQsPage /></MainLayout>} />
      <Route path="/procurement/rfqs/draft" element={<MainLayout><DraftRFQsPage /></MainLayout>} />
      <Route path="/procurement/rfqs/outstanding" element={<MainLayout><OutstandingRFQsPage /></MainLayout>} />
      <Route path="/procurement/rfqs/process/:id" element={<MainLayout><ProcessRFQPage /></MainLayout>} />
      <Route path="/procurement/rfqs/view/:id" element={<MainLayout><ViewRFQPage /></MainLayout>} />
      <Route path="/rfqs/view/:id" element={<MainLayout><ViewRFQPage /></MainLayout>} />
      <Route path="/rfqs" element={<Navigate to="/procurement/rfqs/all" replace />} />
      <Route path="/setup/master" element={<MainLayout><SetupMaster /></MainLayout>} />
      <Route path="/setup/currency" element={<MainLayout><CurrencyPage /></MainLayout>} />
      <Route path="/setup/warehouse" element={<MainLayout><WarehousePage /></MainLayout>} />
      <Route path="/setup/uom" element={<MainLayout><UomPage /></MainLayout>} />
      <Route path="/setup/locations" element={<MainLayout><LocationMaster /></MainLayout>} />
      <Route path="/setup/quote-format" element={<MainLayout><QuoteFormatPage /></MainLayout>} />
      <Route path="/setup/business-unit" element={<MainLayout><BusinessUnitPage /></MainLayout>} />

      {/* Security Routes */}
      <Route path="/security/users" element={<MainLayout><UsersPage /></MainLayout>} />
      <Route path="/security/roles" element={<MainLayout><RolesPermissionsPage /></MainLayout>} />

      {/* Inventory Routes */}
      <Route path="/inventory/products" element={<MainLayout><ProductsPage /></MainLayout>} />
      <Route path="/inventory/products/:id" element={<MainLayout><ProductDetailPage /></MainLayout>} />
      <Route path="/inventory/categories" element={<MainLayout><ProductCategoryPage /></MainLayout>} />
      <Route path="/inventory/sub-categories" element={<MainLayout><ProductSubCategoryPage /></MainLayout>} />
      {/* Supplier Routes */}
      <Route path="/suppliers" element={<MainLayout><SuppliersPage /></MainLayout>} />
      <Route path="/suppliers/:id" element={<MainLayout><SupplierDetailPage /></MainLayout>} />
      <Route path="/suppliers/quoted-items" element={<MainLayout><QuotedItemsPage /></MainLayout>} />
      <Route path="/suppliers/purchase-orders" element={<MainLayout><PurchaseOrdersPage /></MainLayout>} />
      {/* Customer Routes */}
      <Route path="/customers" element={<MainLayout><CustomersPage /></MainLayout>} />
      <Route path="/customers/:id" element={<MainLayout><CustomerDetailPage /></MainLayout>} />

      {/* Lead Management Routes */}
      <Route path="/procurement/leads/all" element={<MainLayout><LeadsPage /></MainLayout>} />
      <Route path="/procurement/leads/outstanding" element={<MainLayout><OutstandingLeadsPage /></MainLayout>} />
      <Route path="/procurement/leads/assigned" element={<MainLayout><AssignedLeadsPage /></MainLayout>} />
      <Route path="/procurement/leads/manual-upload" element={<MainLayout><ManualUploadLeadsPage /></MainLayout>} />
      <Route path="/procurement/leads/folder-upload" element={<MainLayout><FolderUploadLeadsPage /></MainLayout>} />
      <Route path="/procurement/leads/view/:id" element={<MainLayout><LeadDetailPage /></MainLayout>} />
      <Route path="/leads" element={<Navigate to="/procurement/leads/all" replace />} />

      <Route path="/login" element={<LoginPage />} />
      <Route path="*" element={<Box sx={{ p: 4 }}>404 Not Found</Box>} />
    </Routes>
  );
}

export default App;
