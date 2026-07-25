import React, { useState, useMemo } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import {
  Box,
  List,
  ListItem,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Collapse,
  Tooltip,
} from '@mui/material';
import { useTranslation } from 'react-i18next';
import {
  Dashboard as DashboardIcon,
  ReceiptLong as QuotationIcon,
  Assignment as OrderIcon,
  LocalShipping as ShipmentIcon,
  Settings as SetupIcon,
  AdminPanelSettings as SecurityIcon,
  Inventory2 as InventoryIcon,
  Handshake as SupplierIcon,
  People as CustomerIcon,
  ExpandLess,
  ExpandMore,
  FiberManualRecord as BulletIcon,
  TrendingUp as LeadIcon,
  AutoAwesome as CopilotIcon,
  FactCheck as BoqIcon,
  AccountBalance as FinanceIcon,
} from '@mui/icons-material';
import { useAuth } from '../../context/AuthContext';

interface SidebarProps {
  collapsed: boolean;
  onNavigate?: () => void;
}

interface MenuItem {
  key: string;
  label: string;
  icon: React.ReactNode;
  path?: string;
  moduleName?: string;
  activePrefixes?: string[];
  children?: { key: string; label: string; path: string; moduleName?: string; icon?: React.ReactNode; activePrefixes?: string[] }[];
}

const Sidebar: React.FC<SidebarProps> = ({ collapsed, onNavigate }) => {
  const navigate = useNavigate();
  const location = useLocation();
  const { t } = useTranslation();
  const { hasPermission, userData } = useAuth();

  // Mirrors the backend RoleGate rule (role name contains admin/manager). The
  // server still enforces this on the workload endpoint; hiding the entry just
  // avoids showing reps a manager-only page.
  const isManager = /admin|manager/i.test(userData?.roleName ?? '');

  const [openGroups, setOpenGroups] = useState<Record<string, boolean>>({
    'dashboard': location.pathname.startsWith('/dashboard'),
    'rfq_mgmt': location.pathname.includes('/rfqs'),
    'quote_mgmt': location.pathname.includes('/quotes'),
    'setup': location.pathname.includes('/setup'),
    'security': location.pathname.includes('/security'),
    'inventory': location.pathname.includes('/inventory'),
    'supplier_mgmt': location.pathname.includes('/suppliers') || location.pathname.includes('/quoted-items') || location.pathname.includes('/purchase-orders'),
    'lead_mgmt': location.pathname.includes('/leads') || location.pathname.includes('/commercial-cases'),
    'copilot': location.pathname.includes('/copilot'),
  });

  const handleGroupClick = (key: string) => {
    setOpenGroups(prev => ({ ...prev, [key]: !prev[key] }));
  };

  const navigateTo = (path: string) => {
    navigate(path);
    onNavigate?.();
  };

  const menuItems: MenuItem[] = useMemo(() => {
    const rawItems: MenuItem[] = [
      // Managers get a Dashboard group with the WP-B1 Team Workload view;
      // everyone else keeps the familiar one-click Dashboard item.
      isManager
        ? {
            key: 'dashboard',
            label: t('dashboard'),
            icon: <DashboardIcon />,
            moduleName: 'Dashboard',
            children: [
              { key: 'dashboard-overview', label: t('dashboard'), path: '/dashboard', moduleName: 'Dashboard' },
              { key: 'dashboard-team', label: t('team_workload', 'Team Workload'), path: '/dashboard/team', moduleName: 'Dashboard' },
            ],
          }
        : { key: 'dashboard', label: t('dashboard'), icon: <DashboardIcon />, path: '/dashboard', moduleName: 'Dashboard' },
      {
        key: 'copilot',
        // NOTE: these keys are missing from i18n resources, so t('copilot')
        // resolves to the raw key ("copilot") — a truthy string — and the
        // `|| 'Fallback'` pattern never fires. Passing the fallback as
        // i18next's defaultValue renders proper Title Case labels instead.
        label: t('copilot', 'Copilot'),
        icon: <CopilotIcon />,
        moduleName: 'Dashboard',
        activePrefixes: ['/copilot'],
        children: [
          { key: 'copilot-chat', label: t('copilot', 'Copilot'), path: '/copilot', moduleName: 'Dashboard' },
          { key: 'copilot-approvals', label: t('approvals', 'Approvals'), path: '/copilot/approvals', moduleName: 'Dashboard' },
          { key: 'copilot-activity', label: t('activity', 'Activity'), path: '/copilot/activity', moduleName: 'Dashboard' },
        ],
      },
      {
        key: 'lead_mgmt',
        label: t('lead_management'),
        icon: <LeadIcon />,
        moduleName: 'Leads',
        children: [
          { key: 'lead-intelligence', label: 'Lead Intelligence', path: '/procurement/leads/intelligence', moduleName: 'Leads' },
          { key: 'leads-all', label: 'All Inquiries', path: '/procurement/leads/all', moduleName: 'Leads', activePrefixes: ['/procurement/leads/view', '/leads/view'] },
          { key: 'leads-review', label: 'Needs Review', path: '/procurement/extraction/review', moduleName: 'Leads', activePrefixes: ['/procurement/extraction/review'] },
          { key: 'leads-bulk', label: 'Bulk Uploads', path: '/procurement/leads/manual-upload', moduleName: 'Leads', activePrefixes: ['/procurement/leads/ingestion'] },
          { key: 'leads-duplicates', label: 'Duplicates', path: '/procurement/leads/all?view=duplicates', moduleName: 'Leads' },
          { key: 'leads-revisions', label: 'Revisions', path: '/procurement/leads/all?view=revisions', moduleName: 'Leads' },
          { key: 'leads-matches', label: 'Possible Matches', path: '/procurement/leads/possible-matches', moduleName: 'Leads' },
        ]
      },
      {
        key: 'rfq_mgmt',
        label: t('rfq_management'),
        icon: <QuotationIcon />,
        moduleName: 'RFQ Management',
        children: [
          { key: 'rfqs-all', label: t('all_rfqs'), path: '/procurement/rfqs/all', moduleName: 'RFQ Management', activePrefixes: ['/procurement/rfqs/process', '/procurement/rfqs/view', '/rfqs/view', '/rfqs/process'] },
          { key: 'rfqs-draft', label: 'Draft / Needs Review', path: '/procurement/rfqs/draft', moduleName: 'RFQ Management' },
          { key: 'rfqs-ready', label: 'Ready for Quote', path: '/procurement/rfqs/all?state=ready-for-quote', moduleName: 'RFQ Management' },
        ]
      },
      {
        key: 'quote_mgmt',
        label: 'Quote Management',
        icon: <QuotationIcon />,
        moduleName: 'Quotations',
        children: [
          { key: 'quotes-draft', label: 'Draft Quotes', path: '/sales/quotes?state=draft', moduleName: 'Quotations', activePrefixes: ['/sales/quotes/view', '/sales/quotes/edit'] },
          { key: 'quotes-sent', label: 'Sent Quotes', path: '/sales/quotes?state=sent', moduleName: 'Quotations' },
          { key: 'quotes-follow-up', label: 'Follow-up Due', path: '/sales/quotes?state=follow-up', moduleName: 'Quotations' },
          { key: 'quotes-outcomes', label: 'Won / Lost', path: '/sales/quotes?state=outcomes', moduleName: 'Quotations' },
        ],
      },
      // Service BOQs live next to Quotations — a BOQ is priced quote material for
      // service work, so it shares the Quotations module permission.
      { key: 'service-boqs', label: t('service_boqs', 'Service BOQs'), icon: <BoqIcon />, path: '/services/boq', moduleName: 'Quotations', activePrefixes: ['/services/boq'] },
      { key: 'orders', label: t('orders'), icon: <OrderIcon />, path: '/sales/orders', moduleName: 'Orders', activePrefixes: ['/sales/orders'] },
      { key: 'accounts-receivable', label: 'Accounts Receivable', icon: <FinanceIcon />, path: '/sales/finance', moduleName: 'Accounts Receivable', activePrefixes: ['/sales/finance'] },
      { key: 'shipments', label: t('shipments'), icon: <ShipmentIcon />, path: '/sales/shipments', moduleName: 'Shipments', activePrefixes: ['/sales/shipments'] },
      {
        key: 'supplier_mgmt',
        label: t('supplier_management'),
        icon: <SupplierIcon />,
        moduleName: 'Suppliers',
        children: [
          { key: 'suppliers', label: t('suppliers'), path: '/suppliers', moduleName: 'Suppliers', activePrefixes: ['/suppliers/'] },
          { key: 'quoted-items', label: t('quoted_items'), path: '/suppliers/quoted-items', moduleName: 'Supplier History' },
          { key: 'purchase-orders', label: t('purchase_orders'), path: '/suppliers/purchase-orders', moduleName: 'Orders' },
        ]
      },
      { key: 'customers', label: t('customers'), icon: <CustomerIcon />, path: '/customers', moduleName: 'Customers', activePrefixes: ['/customers/'] },
      {
        key: 'inventory',
        label: t('inventory'),
        icon: <InventoryIcon />,
        moduleName: 'Products',
        children: [
          { key: 'products', label: t('products'), path: '/inventory/products', moduleName: 'Products', activePrefixes: ['/inventory/products/'] },
          { key: 'categories', label: t('categories'), path: '/inventory/categories', moduleName: 'Product Categories' },
          { key: 'sub-categories', label: t('sub_categories'), path: '/inventory/sub-categories', moduleName: 'Product Categories' },
        ]
      },
      {
        key: 'security',
        label: t('user_and_access'),
        icon: <SecurityIcon />,
        moduleName: 'Users',
        children: [
          { key: 'users', label: t('users'), path: '/security/users', moduleName: 'Users' },
          { key: 'roles', label: t('roles_and_permissions'), path: '/security/roles', moduleName: 'Roles & Permissions' },
        ]
      },
      {
        key: 'setup',
        label: t('setup_master'),
        icon: <SetupIcon />,
        moduleName: 'Business Units',
        children: [
          { key: 'currency', label: t('currency'), path: '/setup/currency', moduleName: 'Currency' },
          { key: 'warehouse', label: t('warehouse'), path: '/setup/warehouse', moduleName: 'Warehouse' },
          { key: 'master', label: t('master_sub'), path: '/setup/master', moduleName: 'UOM' },
          { key: 'uom', label: t('uom'), path: '/setup/uom', moduleName: 'UOM' },
          { key: 'locations', label: t('locations'), path: '/setup/locations', moduleName: 'Locations' },
          { key: 'quote-format', label: t('quote_format'), path: '/setup/quote-format', moduleName: 'Quote Configuration' },
          { key: 'price-structure', label: 'Price Structure', path: '/setup/price-structure', moduleName: 'UOM' },
          { key: 'sla', label: 'Deadlines & Alerts', path: '/setup/sla', moduleName: 'UOM' },
          { key: 'business-unit', label: t('business_unit'), path: '/setup/business-unit', moduleName: 'Business Units' },
        ]
      },
    ];

    // Filter items based on permissions
    return rawItems.filter(item => {
      if (item.children) {
        // Filter children
        item.children = item.children.filter(child => !child.moduleName || hasPermission(child.moduleName));
        // Only show group if it has at least one visible child
        return item.children.length > 0;
      }
      return !item.moduleName || hasPermission(item.moduleName);
    });
  }, [t, hasPermission, isManager]);

  const renderMenuItem = (item: MenuItem) => {
    const hasChildren = !!item.children;
    const isOpen = openGroups[item.key];

    const isPathMatched = (path: string, prefixes?: string[]) => {
      if (location.pathname === path || (path.startsWith('/procurement') && location.pathname === path.replace('/procurement', ''))) return true;
      if (prefixes && prefixes.some(p => location.pathname.startsWith(p))) return true;
      return false;
    };

    const isSelected = hasChildren
      ? item.children!.some(child => isPathMatched(child.path, child.activePrefixes))
      : item.path ? isPathMatched(item.path, item.activePrefixes) : false;

    return (
      <React.Fragment key={item.key}>
        <ListItem disablePadding sx={{ display: 'block', mb: 0.5 }}>
          <Tooltip title={collapsed ? item.label : ""} placement="right">
            <ListItemButton
              onClick={() => hasChildren ? handleGroupClick(item.key) : navigateTo(item.path!)}
              sx={{
                minHeight: 44,
                justifyContent: collapsed ? 'center' : 'initial',
                px: 2,
                borderRadius: '10px',
                backgroundColor: isSelected ? 'primary.main' : 'transparent',
                color: isSelected ? 'primary.contrastText' : 'text.primary',
                '&:hover': {
                  backgroundColor: isSelected ? 'primary.dark' : 'action.hover',
                  transform: 'translateX(4px)',
                },
                transition: 'all 0.2s cubic-bezier(0.4, 0, 0.2, 1)',
                boxShadow: isSelected ? (theme) => `0 10px 15px -3px ${theme.palette.primary.main}4D` : 'none',
              }}
            >
              <ListItemIcon
                sx={{
                  minWidth: 0,
                  mr: collapsed ? 0 : 1.5,
                  justifyContent: 'center',
                  color: 'inherit',
                  opacity: isSelected ? 1 : 0.7,
                }}
              >
                {React.cloneElement(item.icon as React.ReactElement<any>, { sx: { fontSize: 20 } })}
              </ListItemIcon>
              {!collapsed && (
                <>
                  <ListItemText
                    primary={item.label}
                    slotProps={{
                      primary: { sx: { fontSize: '0.875rem', fontWeight: isSelected ? 600 : 500 } }
                    }}
                  />
                  {hasChildren ? (isOpen ? <ExpandLess /> : <ExpandMore />) : null}
                </>
              )}
            </ListItemButton>
          </Tooltip>
        </ListItem>

        {hasChildren && !collapsed && (
          <Collapse in={isOpen} timeout="auto" unmountOnExit>
            <List component="div" disablePadding>
              {item.children?.map((child) => {
                const isChildSelected = isPathMatched(child.path, child.activePrefixes);
                return (
                  <ListItemButton
                    key={child.key}
                    onClick={() => navigateTo(child.path)}
                    sx={{
                      minHeight: 40,
                      pl: 4,
                      pr: 2,
                      mx: 1,
                      mb: 0.2,
                      borderRadius: 1.5,
                      color: isChildSelected ? 'primary.main' : 'text.secondary',
                      backgroundColor: isChildSelected ? 'rgba(25, 118, 210, 0.08)' : 'transparent',
                      '&:hover': {
                        backgroundColor: 'rgba(0, 0, 0, 0.03)',
                      },
                    }}
                  >
                    <ListItemIcon sx={{ minWidth: 24, color: 'inherit' }}>
                      <BulletIcon sx={{ fontSize: 6 }} />
                    </ListItemIcon>
                    <ListItemText
                      primary={child.label}
                      slotProps={{
                        primary: { sx: { fontSize: '0.8rem', fontWeight: isChildSelected ? 600 : 400 } }
                      }}
                    />
                  </ListItemButton>
                );
              })}
            </List>
          </Collapse>
        )}
      </React.Fragment>
    );
  };

  return (
    <Box sx={{
      overflowY: 'auto',
      overflowX: 'hidden',
      height: '100%',
      pt: 2,
      pb: 4,
      px: 1.5,
    }}>
      <List sx={{ px: 0 }}>
        {menuItems.map(renderMenuItem)}
      </List>
    </Box>
  );
};

export default Sidebar;
