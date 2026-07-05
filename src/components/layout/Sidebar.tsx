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
  Typography,
  alpha,
  useTheme,
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
  excludeActivePrefixes?: string[];
  children?: {
    key: string;
    label: string;
    path: string;
    moduleName?: string;
    icon?: React.ReactNode;
    activePrefixes?: string[];
    excludeActivePrefixes?: string[];
  }[];
}

const Sidebar: React.FC<SidebarProps> = ({ collapsed, onNavigate }) => {
  const theme = useTheme();
  const navigate = useNavigate();
  const location = useLocation();
  const { t } = useTranslation();
  const { hasPermission } = useAuth();
  const sidebarText = 'rgba(226, 232, 240, 0.9)';
  const sidebarMuted = 'rgba(148, 163, 184, 0.92)';
  const sidebarHover = 'rgba(255, 255, 255, 0.075)';
  
  const [openGroups, setOpenGroups] = useState<Record<string, boolean>>({
    'rfq_mgmt': location.pathname.includes('/rfqs'),
    'setup': location.pathname.includes('/setup'),
    'security': location.pathname.includes('/security'),
    'inventory': location.pathname.includes('/inventory'),
    'supplier_mgmt': location.pathname.includes('/suppliers') || location.pathname.includes('/quoted-items') || location.pathname.includes('/purchase-orders'),
    'lead_mgmt': location.pathname.includes('/leads'),
  });

  const handleGroupClick = (key: string) => {
    setOpenGroups(prev => ({ ...prev, [key]: !prev[key] }));
  };

  const menuItems: MenuItem[] = useMemo(() => {
    const rawItems: MenuItem[] = [
      { key: 'dashboard', label: t('dashboard'), icon: <DashboardIcon />, path: '/dashboard', moduleName: 'Dashboard' },
      {
        key: 'lead_mgmt',
        label: t('lead_management'),
        icon: <LeadIcon />,
        moduleName: 'Leads',
        children: [
          { key: 'leads-all', label: t('leads'), path: '/procurement/leads/all', moduleName: 'Leads', activePrefixes: ['/procurement/leads/view', '/leads/view'] },
          { key: 'leads-outstanding', label: t('outstanding_leads'), path: '/procurement/leads/outstanding', moduleName: 'Leads' },
          { key: 'leads-assigned', label: t('assigned_leads'), path: '/procurement/leads/assigned', moduleName: 'Leads' },
          { key: 'leads-manual', label: t('manual_upload'), path: '/procurement/leads/manual-upload', moduleName: 'Leads' },
          { key: 'leads-folder', label: t('upload_folder_leads'), path: '/procurement/leads/folder-upload', moduleName: 'Leads' },
        ]
      },
      {
        key: 'rfq_mgmt',
        label: t('rfq_management'),
        icon: <QuotationIcon />,
        moduleName: 'RFQ Management',
        children: [
          { key: 'rfqs-all', label: t('all_rfqs'), path: '/procurement/rfqs/all', moduleName: 'RFQ Management', activePrefixes: ['/procurement/rfqs/process', '/procurement/rfqs/view', '/rfqs/view', '/rfqs/process'] },
          { key: 'rfqs-draft', label: t('draft_rfqs'), path: '/procurement/rfqs/draft', moduleName: 'RFQ Management' },
          { key: 'rfqs-outstanding', label: t('outstanding_rfqs'), path: '/procurement/rfqs/outstanding', moduleName: 'RFQ Management' },
        ]
      },
      { key: 'quotes', label: t('quotations'), icon: <QuotationIcon />, path: '/sales/quotes', moduleName: 'Quotations', activePrefixes: ['/sales/quotes'] },
      { key: 'orders', label: t('orders'), icon: <OrderIcon />, path: '/sales/orders', moduleName: 'Orders', activePrefixes: ['/sales/orders'] },
      { key: 'shipments', label: t('shipments'), icon: <ShipmentIcon />, path: '/sales/shipments', moduleName: 'Shipments', activePrefixes: ['/sales/shipments'] },
      {
        key: 'supplier_mgmt',
        label: t('supplier_management'),
        icon: <SupplierIcon />,
        moduleName: 'Suppliers',
        children: [
          {
            key: 'suppliers',
            label: t('suppliers'),
            path: '/suppliers',
            moduleName: 'Suppliers',
            activePrefixes: ['/suppliers/'],
            excludeActivePrefixes: ['/suppliers/quoted-items', '/suppliers/purchase-orders'],
          },
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
  }, [t, hasPermission]);

  const renderMenuItem = (item: MenuItem) => {
    const hasChildren = !!item.children;
    const isOpen = openGroups[item.key];

    const isPathMatched = (path: string, prefixes?: string[], excludePrefixes?: string[]) => {
      if (excludePrefixes?.some((prefix) => location.pathname === prefix || location.pathname.startsWith(`${prefix}/`))) return false;
      if (location.pathname === path || (path.startsWith('/procurement') && location.pathname === path.replace('/procurement', ''))) return true;
      if (prefixes && prefixes.some(p => location.pathname.startsWith(p))) return true;
      return false;
    };

    const isSelected = hasChildren
      ? item.children!.some(child => isPathMatched(child.path, child.activePrefixes, child.excludeActivePrefixes))
      : item.path ? isPathMatched(item.path, item.activePrefixes, item.excludeActivePrefixes) : false;

    return (
      <React.Fragment key={item.key}>
        <ListItem disablePadding sx={{ display: 'block', mb: 0.5 }}>
          <Tooltip title={collapsed ? item.label : ""} placement="right">
            <ListItemButton
              onClick={() => {
                if (hasChildren) {
                  handleGroupClick(item.key);
                } else {
                  navigate(item.path!);
                  onNavigate?.();
                }
              }}
              sx={{
                minHeight: 42,
                justifyContent: collapsed ? 'center' : 'initial',
                px: collapsed ? 1 : 1.5,
                borderRadius: 2,
                background: isSelected
                  ? `linear-gradient(135deg, ${theme.palette.primary.main}, ${theme.palette.primary.dark})`
                  : 'transparent',
                color: isSelected ? theme.palette.primary.contrastText : sidebarText,
                '&:hover': {
                  background: isSelected
                    ? `linear-gradient(135deg, ${theme.palette.primary.dark}, ${theme.palette.primary.main})`
                    : sidebarHover,
                  transform: collapsed ? 'translateY(-1px)' : 'translateX(3px)',
                },
                transition: 'all 0.2s cubic-bezier(0.4, 0, 0.2, 1)',
                boxShadow: isSelected ? `0 18px 34px -18px ${theme.palette.primary.main}` : 'none',
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
                      primary: { sx: { fontSize: '0.85rem', fontWeight: isSelected ? 800 : 700 } }
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
                const isChildSelected = isPathMatched(child.path, child.activePrefixes, child.excludeActivePrefixes);
                return (
                  <ListItemButton
                    key={child.key}
                    onClick={() => {
                      navigate(child.path);
                      onNavigate?.();
                    }}
                    sx={{
                      minHeight: 36,
                      pl: 4.5,
                      pr: 2,
                      mx: 0.75,
                      mb: 0.2,
                      borderRadius: 1.5,
                      color: isChildSelected ? '#fff' : sidebarMuted,
                      backgroundColor: isChildSelected ? alpha(theme.palette.primary.main, 0.22) : 'transparent',
                      border: '1px solid',
                      borderColor: isChildSelected ? alpha(theme.palette.primary.main, 0.35) : 'transparent',
                      boxShadow: isChildSelected ? `inset 3px 0 0 ${theme.palette.primary.main}` : 'none',
                      '&:hover': {
                        backgroundColor: isChildSelected ? alpha(theme.palette.primary.main, 0.28) : sidebarHover,
                        color: '#fff',
                      },
                    }}
                  >
                    <ListItemIcon sx={{ minWidth: 24, color: 'inherit' }}>
                      <BulletIcon sx={{ fontSize: 6 }} />
                    </ListItemIcon>
                    <ListItemText
                      primary={child.label}
                      slotProps={{
                        primary: { sx: { fontSize: '0.78rem', fontWeight: isChildSelected ? 800 : 650 } }
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
      pt: 1,
      pb: 2,
      px: 0.5,
      scrollbarWidth: 'none',
      msOverflowStyle: 'none',
      '&::-webkit-scrollbar': {
        width: 0,
        height: 0,
        display: 'none',
      },
    }}>
      {!collapsed ? (
        <Box
          sx={{
            mx: 0.5,
            mb: 1.5,
            p: 1.5,
            borderRadius: 2,
            border: '1px solid',
            borderColor: 'rgba(255,255,255,0.08)',
            background: `linear-gradient(135deg, ${alpha(theme.palette.primary.main, 0.22)}, rgba(255,255,255,0.04))`,
            boxShadow: `inset 0 1px 0 rgba(255,255,255,0.08), 0 18px 40px ${alpha('#000', 0.16)}`,
          }}
        >
          <Typography variant="caption" sx={{ color: sidebarMuted, fontWeight: 800, textTransform: 'uppercase' }}>
            Procurement OS
          </Typography>
          <Typography variant="body2" sx={{ color: '#fff', fontWeight: 850, mt: 0.25 }}>
            Live RFQ workspace
          </Typography>
        </Box>
      ) : null}
      <List sx={{ px: 0 }}>
        {menuItems.map(renderMenuItem)}
      </List>
    </Box>
  );
};

export default Sidebar;
