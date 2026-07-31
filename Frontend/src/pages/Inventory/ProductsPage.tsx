import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, TextField, MenuItem,
  Chip, IconButton, Tooltip, Dialog, DialogTitle,
  DialogContent, Table, TableHead, TableBody, TableRow, TableCell,
  Stack,
  CircularProgress,
  Alert,
} from '@mui/material';
import {
  Add as AddIcon,
  Visibility as ViewIcon,
  Edit as EditIcon,
  History as HistoryIcon,
  Inventory2 as InventoryIcon,
  TrendingDown as LowStockIcon,
  RemoveShoppingCart as OutOfStockIcon,
} from '@mui/icons-material';
import { DataGrid, type GridColDef, type GridPaginationModel } from '@mui/x-data-grid';
import productService from '../../api/services/productService';
import SearchField from '../../components/common/SearchField';
import UploadExportToolbar from '../../components/common/UploadExportToolbar';
import ProductFormDialog from './ProductFormDialog';
import { useAuth } from '../../context/AuthContext';

const PurchaseHistoryDialog: React.FC<{ open: boolean; onClose: () => void; productId: number | null }> = ({ open, onClose, productId }) => {
  const { t } = useTranslation();
  const { data: history, isLoading, isError, refetch } = useQuery({
    queryKey: ['product-history', productId],
    queryFn: () => productService.getPurchaseHistory(productId!),
    enabled: !!productId && open,
  });

  const historyItems = history?.purchaseHistory ?? [];

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="md">
      <DialogTitle sx={{ fontWeight: 800, display: 'flex', alignItems: 'center', gap: 1 }}>
        <HistoryIcon color="primary" />
        Purchase History {history?.productName ? `: ${history.productName}` : ''}
      </DialogTitle>
      <DialogContent dividers sx={{ p: 0 }}>
        {isLoading ? (
          <Box sx={{ p: 4, textAlign: 'center' }}><CircularProgress size={24} /><Typography sx={{ mt: 1 }}>Loading history...</Typography></Box>
        ) : isError ? (
          <Box sx={{ p: 3 }}><Alert severity="error" action={<Button color="inherit" onClick={() => refetch()}>Retry</Button>}>Purchase history could not be loaded.</Alert></Box>
        ) : !historyItems.length ? (
          <Box sx={{ p: 4, textAlign: 'center' }}><Typography color="text.secondary">No purchase records found for this product.</Typography></Box>
        ) : (
          <Table>
            <TableHead>
              <TableRow sx={{ bgcolor: 'action.hover' }}>
                <TableCell sx={{ fontWeight: 800 }}>{t('date')}</TableCell>
                <TableCell sx={{ fontWeight: 800 }}>{t('supplier')}</TableCell>
                <TableCell sx={{ fontWeight: 800 }} align="right">Qty</TableCell>
                <TableCell sx={{ fontWeight: 800 }} align="right">Unit Price</TableCell>
                <TableCell sx={{ fontWeight: 800 }} align="right">Total</TableCell>
                <TableCell sx={{ fontWeight: 800 }}>Order #</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {historyItems.map((h: any, i: number) => (
                <TableRow key={i}>
                  <TableCell>{h.orderDate ? new Date(h.orderDate).toLocaleDateString() : '—'}</TableCell>
                  <TableCell>{h.supplierName || '—'}</TableCell>
                  <TableCell align="right">{h.quantity}</TableCell>
                  <TableCell align="right">${Number(h.unitPrice).toFixed(2)}</TableCell>
                  <TableCell align="right" sx={{ fontWeight: 700 }}>${(h.quantity * h.unitPrice).toFixed(2)}</TableCell>
                  <TableCell>{h.orderNumber || '—'}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </DialogContent>
    </Dialog>
  );
};

const ProductsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const canCreate = hasPermission('Products', 'create');
  const canEdit = hasPermission('Products', 'edit');

  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ page: 0, pageSize: 10 });
  const [search, setSearch] = useState('');
  const [filterActive, setFilterActive] = useState<'all' | 'true' | 'false'>('all');

  // Dialog States
  const [isFormOpen, setIsFormOpen] = useState(false);
  const [editingProductId, setEditingProductId] = useState<number | undefined>(undefined);
  const [historyProductId, setHistoryProductId] = useState<number | null>(null);

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['products', paginationModel, search, filterActive],
    queryFn: () => productService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      isActive: filterActive === 'all' ? undefined : filterActive === 'true',
    }),
  });

  const products = data?.items ?? [];
  const totalItems = data?.totalItems ?? 0;

  // Quick stats
  const lowStock = products.filter(p => p.qtyOnHand > 0 && p.qtyOnHand <= p.reorderPoint).length;
  const outOfStock = products.filter(p => p.qtyOnHand === 0).length;

  const getStockChip = (qty: number, reorder: number) => {
    if (qty === 0) return <Chip label="Out of Stock" color="error" size="small" />;
    if (qty <= reorder) return <Chip label="Low Stock" color="warning" size="small" />;
    return <Chip label="In Stock" color="success" size="small" />;
  };

  const handleEdit = (id: number) => {
    setEditingProductId(id);
    setIsFormOpen(true);
  };

  const handleAddNew = () => {
    setEditingProductId(undefined);
    setIsFormOpen(true);
  };

  const columns: GridColDef[] = [
    { field: 'partNo', headerName: 'Part No', width: 120, renderCell: (p) => <Typography variant="body2" sx={{ fontWeight: 700, fontFamily: 'monospace' }}>{p.value}</Typography> },
    { field: 'productName', headerName: t('product'), flex: 1.5, minWidth: 180 },
    { field: 'categoryName', headerName: t('categories'), flex: 1, minWidth: 130 },
    { field: 'warehouseName', headerName: t('warehouse'), flex: 1, minWidth: 120 },
    {
      field: 'qtyOnHand',
      headerName: 'Qty on Hand',
      width: 140,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
          <Typography variant="body2" sx={{ fontWeight: 700 }}>{p.value}</Typography>
          {getStockChip(p.row.qtyOnHand, p.row.reorderPoint)}
        </Box>
      ),
    },
    { field: 'unitCost', headerName: 'Unit Cost', width: 165, renderCell: (p) => p.value != null ? `No currency · ${Number(p.value).toFixed(2)}` : '—' },
    { field: 'sellingPrice', headerName: 'Selling Price', width: 165, renderCell: (p) => p.value != null ? `No currency · ${Number(p.value).toFixed(2)}` : '—' },
    {
      field: 'isActive',
      headerName: t('status'),
      width: 100,
      renderCell: (p) => <Chip label={p.value ? 'Active' : 'Inactive'} color={p.value ? 'success' : 'default'} size="small" variant="outlined" />,
    },
    {
      field: 'actions',
      headerName: t('actions'),
      width: 150,
      sortable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5}>
          <Tooltip title="View Details">
            <IconButton size="small" color="primary" onClick={() => navigate(`/inventory/products/${p.row.id}`)}>
              <ViewIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          {canEdit && <Tooltip title="Edit Product">
            <IconButton size="small" color="info" onClick={() => handleEdit(p.row.id)}>
              <EditIcon fontSize="small" />
            </IconButton>
          </Tooltip>}
          <Tooltip title="Purchase History">
            <IconButton size="small" color="secondary" onClick={() => setHistoryProductId(p.row.id)}>
              <HistoryIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      ),
    },
  ];

  return (
    <Box sx={{ width: '100%', px: 1, py: 1 }}>
      {/* Header */}
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, display: 'flex', alignItems: 'center', gap: 1 }}>
            <InventoryIcon color="primary" />
            {t('products')}
          </Typography>
          <Typography variant="body2" color="text.secondary">Browse, search and manage your inventory items</Typography>
        </Box>
        <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center' }}>
          <UploadExportToolbar
            onDownloadTemplate={productService.downloadTemplate}
            onUpload={productService.uploadTemplate}
            onExport={productService.export}
            templateFileName="ProductTemplate.xlsx"
            exportFileName="Products.xlsx"
            canUpload={canCreate}
          />
          {canCreate && <Button variant="contained" startIcon={<AddIcon />} onClick={handleAddNew} sx={{ px: 3 }}>
            Add Product
          </Button>}
        </Box>
      </Box>

      {/* Quick Stats */}
      <Box sx={{ display: 'flex', gap: 2, mb: 2 }}>
        {[
          { label: 'Total Products', value: totalItems, icon: <InventoryIcon />, color: 'primary.main' },
          { label: 'Low Stock on this page', value: lowStock, icon: <LowStockIcon />, color: 'warning.main' },
          { label: 'Out of Stock on this page', value: outOfStock, icon: <OutOfStockIcon />, color: 'error.main' },
        ].map((stat) => (
          <Paper key={stat.label} sx={{ px: 2.5, py: 1.5, borderRadius: 2.5, display: 'flex', alignItems: 'center', gap: 1.5, border: '1px solid', borderColor: 'divider', boxShadow: 'none', minWidth: 150 }}>
            <Box sx={{ color: stat.color }}>{stat.icon}</Box>
            <Box>
              <Typography variant="h6" sx={{ fontWeight: 900, lineHeight: 1 }}>{stat.value}</Typography>
              <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 600 }}>{stat.label}</Typography>
            </Box>
          </Paper>
        ))}
      </Box>

      {/* Filters */}
      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField width="360px" value={search} onChange={setSearch} placeholder="Search by name, part no..." />
        <TextField select size="small" value={filterActive} onChange={(e) => setFilterActive(e.target.value as any)} sx={{ minWidth: 140 }} label="Status">
          <MenuItem value="all">All Status</MenuItem>
          <MenuItem value="true">Active</MenuItem>
          <MenuItem value="false">Inactive</MenuItem>
        </TextField>
      </Paper>

      {/* Data Grid */}
      {isError && <Alert severity="error" sx={{ mb: 1.5 }} action={<Button color="inherit" onClick={() => refetch()}>Retry</Button>}>Products could not be loaded.</Alert>}
      <Paper sx={{ height: 'calc(100vh - 310px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid
          rows={products}
          columns={columns}
          rowCount={totalItems}
          loading={isLoading}
          pageSizeOptions={[10, 25, 50]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          disableRowSelectionOnClick
          getRowId={(row) => row.id}
        />
      </Paper>

      {/* Create/Edit Product Dialog */}
      <ProductFormDialog
        open={isFormOpen && (editingProductId ? canEdit : canCreate)}
        onClose={() => setIsFormOpen(false)}
        productId={editingProductId}
      />

      {/* Purchase History Dialog */}
      <PurchaseHistoryDialog
        open={!!historyProductId}
        onClose={() => setHistoryProductId(null)}
        productId={historyProductId}
      />
    </Box>
  );
};

export default ProductsPage;
