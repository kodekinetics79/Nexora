import React, { useState } from 'react';
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
} from '@mui/icons-material';

interface SidebarProps {
  collapsed: boolean;
}

interface MenuItem {
  key: string;
  label: string;
  icon: React.ReactNode;
  path?: string;
  activePrefixes?: string[];
  children?: { key: string; label: string; path: string; icon?: React.ReactNode; activePrefixes?: string[] }[];
}

const Sidebar: React.FC<SidebarProps> = ({ collapsed }) => {
  const navigate = useNavigate();
  const location = useLocation();
  const { t } = useTranslation();
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

  const menuItems: MenuItem[] = [
    { key: 'dashboard', label: t('dashboard'), icon: <DashboardIcon />, path: '/dashboard' },

    { key: 'quotations', label: t('quotations'), icon: <QuotationIcon />, path: '/quotations' },
    { key: 'orders', label: t('orders'), icon: <OrderIcon />, path: '/orders' },
    { key: 'shipments', label: t('shipments'), icon: <ShipmentIcon />, path: '/shipments' },
    {
      key: 'rfq_mgmt',
      label: t('rfq_management'),
      icon: <QuotationIcon />,
      children: [
        { key: 'rfqs-all', label: t('all_rfqs'), path: '/procurement/rfqs/all', activePrefixes: ['/procurement/rfqs/process', '/procurement/rfqs/view', '/rfqs/view', '/rfqs/process'] },
        { key: 'rfqs-draft', label: t('draft_rfqs'), path: '/procurement/rfqs/draft' },
        { key: 'rfqs-outstanding', label: t('outstanding_rfqs'), path: '/procurement/rfqs/outstanding' },
      ]
    },
    {
      key: 'lead_mgmt',
      label: t('lead_management'),
      icon: <LeadIcon />,
      children: [
        { key: 'leads-all', label: t('leads'), path: '/procurement/leads/all', activePrefixes: ['/procurement/leads/view', '/leads/view'] },
        { key: 'leads-outstanding', label: t('outstanding_leads'), path: '/procurement/leads/outstanding' },
        { key: 'leads-assigned', label: t('assigned_leads'), path: '/procurement/leads/assigned' },
        { key: 'leads-manual', label: t('manual_upload'), path: '/procurement/leads/manual-upload' },
        { key: 'leads-folder', label: t('upload_folder_leads'), path: '/procurement/leads/folder-upload' },
      ]
    },
    {
      key: 'supplier_mgmt',
      label: t('supplier_management'),
      icon: <SupplierIcon />,
      children: [
        { key: 'suppliers', label: t('suppliers'), path: '/suppliers', activePrefixes: ['/suppliers/'] },
        { key: 'quoted-items', label: t('quoted_items'), path: '/suppliers/quoted-items' },
        { key: 'purchase-orders', label: t('purchase_orders'), path: '/suppliers/purchase-orders' },
      ]
    },
    { key: 'customers', label: t('customers'), icon: <CustomerIcon />, path: '/customers', activePrefixes: ['/customers/'] },
    {
      key: 'inventory',
      label: t('inventory'),
      icon: <InventoryIcon />,
      children: [
        { key: 'products', label: t('products'), path: '/inventory/products', activePrefixes: ['/inventory/products/'] },
        { key: 'categories', label: t('categories'), path: '/inventory/categories' },
        { key: 'sub-categories', label: t('sub_categories'), path: '/inventory/sub-categories' },
      ]
    },
    {
      key: 'security',
      label: t('user_and_access'),
      icon: <SecurityIcon />,
      children: [
        { key: 'users', label: t('users'), path: '/security/users' },
        { key: 'roles', label: t('roles_and_permissions'), path: '/security/roles' },
      ]
    },
    {
      key: 'setup',
      label: t('setup_master'),
      icon: <SetupIcon />,
      children: [
        { key: 'currency', label: t('currency'), path: '/setup/currency' },
        { key: 'warehouse', label: t('warehouse'), path: '/setup/warehouse' },
        { key: 'master', label: t('master_sub'), path: '/setup/master' },
        { key: 'uom', label: t('uom'), path: '/setup/uom' },
        { key: 'locations', label: t('locations'), path: '/setup/locations' },
        { key: 'quote-format', label: t('quote_format'), path: '/setup/quote-format' },
        { key: 'business-unit', label: t('business_unit'), path: '/setup/business-unit' },
      ]
    },
  ];

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
              onClick={() => hasChildren ? handleGroupClick(item.key) : navigate(item.path!)}
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
                    onClick={() => navigate(child.path)}
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
