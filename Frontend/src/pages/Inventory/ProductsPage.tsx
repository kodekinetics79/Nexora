import React, { useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link as RouterLink, useSearchParams } from 'react-router-dom';
import {
  Alert,
  Box,
  Button,
  IconButton,
  Link,
  MenuItem,
  Paper,
  Popover,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import AddIcon from '@mui/icons-material/Add';
import EditIcon from '@mui/icons-material/EditOutlined';
import FilterIcon from '@mui/icons-material/FilterList';
import { DataGrid, type GridColDef, type GridRowSelectionModel, type GridSortModel } from '@mui/x-data-grid';
import productService, { type ProductDTO, type ProductFilters } from '../../api/services/productService';
import SearchField from '../../components/common/SearchField';
import UploadExportToolbar from '../../components/common/UploadExportToolbar';
import ProductFormDialog from './ProductFormDialog';
import OpeningStockDialog from './Commercial/OpeningStockDialog';
import { ProductsWorkspaceShell, ProductsWorkspaceToolbar } from './ProductsWorkspaceNav';
import { useAuth } from '../../context/AuthContext';
import useProductPricing from './useProductPricing';
import ProductStockDialog from './ProductStockDialog';
import { confirmLeavingUnsavedWork } from '../../hooks/unsavedWorkRegistry';

const recordedPrice = (value?: number | null) => value == null
  ? '—'
  : value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

const sortFields: Record<string, ProductFilters['sortBy']> = {
  productName: 'name', partNo: 'partNo', uomName: 'unit', qtyOnHand: 'onHand',
  availableQuantity: 'available', lastPurchaseCost: 'lastPurchaseCost', unitCost: 'landedCost',
  sellingPrice: 'salePrice', priceCurrencyCode: 'currency',
};
const stockLabels = { all: 'All', 'in-stock': 'In stock', 'out-of-stock': 'Out of stock', 'low-stock': 'Low stock' };
const emptySelection = (): GridRowSelectionModel => ({ type: 'include', ids: new Set() });

const ProductsPage: React.FC = () => {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canCreate = hasPermission('Products', 'create');
  const canEdit = hasPermission('Products', 'edit');
  const [searchParams, setSearchParams] = useSearchParams();
  const pricing = useProductPricing(canEdit);
  const search = searchParams.get('search') ?? '';
  const filterActive = ['true', 'false'].includes(searchParams.get('active') ?? '') ? searchParams.get('active')! : 'all';
  const stock = (Object.keys(stockLabels).includes(searchParams.get('stock') ?? '') ? searchParams.get('stock') : 'all') as keyof typeof stockLabels;
  const warehouseId = Number(searchParams.get('warehouse')) > 0 ? Number(searchParams.get('warehouse')) : undefined;
  const paginationModel = {
    page: Math.max(0, Number(searchParams.get('page')) || 0),
    pageSize: [10, 25, 50].includes(Number(searchParams.get('size'))) ? Number(searchParams.get('size')) : 25,
  };
  const sortField = Object.hasOwn(sortFields, searchParams.get('sort') ?? '') ? searchParams.get('sort')! : 'productName';
  const sortDirection = searchParams.get('direction') === 'desc' ? 'desc' : 'asc';
  const sortModel: GridSortModel = [{ field: sortField, sort: sortDirection }];
  const [selection, setSelection] = useState<GridRowSelectionModel>(emptySelection);
  const [editingIds, setEditingIds] = useState<Set<number> | null>(null);
  const [filterAnchor, setFilterAnchor] = useState<HTMLElement | null>(null);
  const [isFormOpen, setIsFormOpen] = useState(false);
  const [editingProductId, setEditingProductId] = useState<number>();
  const [openingStock, setOpeningStock] = useState(false);
  const [stockProduct, setStockProduct] = useState<ProductDTO>();
  const updateView = (values: Record<string, string | undefined>, resetPage = true) => {
    setSearchParams((previous) => {
      const next = new URLSearchParams(previous);
      if (resetPage) next.delete('page');
      for (const [key, value] of Object.entries(values)) {
        if (value === undefined || value === '' || value === 'all') next.delete(key);
        else next.set(key, value);
      }
      return next;
    }, { replace: true });
    setSelection(emptySelection());
  };
  const warehouses = useQuery({ queryKey: ['product-warehouses'], queryFn: productService.getWarehouses, staleTime: 60_000 });

  const { data, isLoading, isFetching, isError, refetch } = useQuery({
    queryKey: ['products', paginationModel, search, filterActive, stock, warehouseId, sortField, sortDirection],
    queryFn: () => productService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      isActive: filterActive === 'all' ? undefined : filterActive === 'true',
      stock, warehouseId, sortBy: sortFields[sortField], sortDirection,
    }),
  });
  // Keep the server page stable while a new page is loading; do not show stale rows as current.
  const totalRef = useRef(0);
  if (data) totalRef.current = data.totalItems;
  const editRow = (row: ProductDTO) => pricing.editing && (!editingIds || editingIds.has(row.id));
  const startPricing = () => {
    setEditingIds(selection.ids.size ? new Set([...selection.ids].map(Number)) : null);
    pricing.start();
  };

  const openCreate = () => { setEditingProductId(undefined); setIsFormOpen(true); };
  const openEdit = (id: number) => { setEditingProductId(id); setIsFormOpen(true); };
  const hasActiveFilters = search.trim() !== '' || filterActive !== 'all' || stock !== 'all' || warehouseId !== undefined;
  const clearFilters = () => updateView({ search: undefined, active: undefined, stock: undefined, warehouse: undefined });

  const numberColumn = (field: keyof ProductDTO, headerName: string, width = 100): GridColDef<ProductDTO> => ({
    field, headerName, width, align: 'right', headerAlign: 'right',
    renderCell: ({ row }) => <Typography variant="body2" className="tabular-nums">{typeof row[field] === 'number' ? row[field].toLocaleString() : '—'}</Typography>,
  });
  const textColumn = (field: keyof ProductDTO, headerName: string, width = 160): GridColDef<ProductDTO> => ({
    field, headerName, width,
    renderCell: ({ row }) => <Typography variant="body2">{row[field] == null || row[field] === '' ? '—' : String(row[field])}</Typography>,
  });
  const columns: GridColDef<ProductDTO>[] = [
    {
      field: 'productName', headerName: 'Product', minWidth: 200, flex: 1.8,
      renderCell: ({ row, tabIndex }) => <Link component={RouterLink} to={`/inventory/products/${row.id}`} tabIndex={tabIndex} title={row.productName} onClick={(event) => { if (!confirmLeavingUnsavedWork()) event.preventDefault(); }} underline="hover" color="text.primary" sx={{ fontWeight: 500, display: 'flex', alignItems: 'center', minHeight: 44, minWidth: 0 }}><Box component="span" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{row.productName || 'Unnamed product'}</Box></Link>,
    },
    { ...textColumn('partNo', 'Part number', 145), flex: 1.1, minWidth: 140 },
    textColumn('uomName', 'Unit', 70),
    { ...numberColumn('qtyOnHand', 'In stock', 90), description: warehouseId ? 'Physical stock in the selected warehouse. Open to see all warehouse details.' : 'Physical stock across all warehouses. Open to see warehouse details.', renderCell: ({ row, tabIndex }) => <Tooltip title="View warehouse stock"><Button color="inherit" size="small" tabIndex={tabIndex} aria-label={`Stock details for ${row.partNo}`} onClick={() => setStockProduct(row)} sx={{ minWidth: 44, height: 44, p: 0, justifyContent: 'flex-end', fontWeight: 400, fontVariantNumeric: 'tabular-nums', '&:hover': { textDecoration: 'underline' } }}>{row.qtyOnHand.toLocaleString()}</Button></Tooltip> },
    { ...numberColumn('availableQuantity', 'Available', 95), description: 'Usable stock after quality, allocation and safety-stock exclusions. No reservations.', renderCell: ({ row }) => <Tooltip describeChild title="Usable stock after quality, allocation and safety-stock exclusions. No reservations."><Typography variant="body2" className="tabular-nums">{row.availableQuantity?.toLocaleString() ?? '—'}</Typography></Tooltip> },
    {
      field: 'lastPurchaseCost', headerName: 'Purchase cost', minWidth: 120, flex: 1, align: 'right', headerAlign: 'right',
      description: 'Unit price from the latest supplier purchase order. A different purchase currency is shown beside its amount.',
      renderCell: ({ row }) => {
        const currentCurrency = editRow(row)
          ? pricing.currencies.data?.find((currency) => currency.id === pricing.draftFor(row).currencyId)?.code
          : row.priceCurrencyCode;
        return <Typography variant="body2" className="tabular-nums">{recordedPrice(row.lastPurchaseCost)}{row.lastPurchaseCost != null && row.lastPurchaseCurrencyCode !== currentCurrency ? ` ${row.lastPurchaseCurrencyCode || '(currency not set)'}` : ''}</Typography>;
      },
    },
    {
      field: 'unitCost', headerName: 'Landed cost', minWidth: 115, flex: 1, align: 'right', headerAlign: 'right',
      renderCell: ({ row }) => editRow(row)
        ? <TextField size="small" type="number" value={pricing.draftFor(row).landedCost} disabled={pricing.save.isPending} onChange={(event) => pricing.update(row, 'landedCost', event.target.value)} slotProps={{ htmlInput: { min: 0, step: 'any', 'aria-label': `Landed cost for ${row.partNo}`, style: { textAlign: 'right' } } }} />
        : <Typography variant="body2" className="tabular-nums">{recordedPrice(row.unitCost)}</Typography>,
    },
    {
      field: 'sellingPrice', headerName: 'Selling price', minWidth: 115, flex: 1, align: 'right', headerAlign: 'right',
      renderCell: ({ row }) => editRow(row)
        ? <TextField size="small" type="number" value={pricing.draftFor(row).salePrice} disabled={pricing.save.isPending} onChange={(event) => pricing.update(row, 'salePrice', event.target.value)} helperText={Number(pricing.draftFor(row).salePrice) > 0 && Number(pricing.draftFor(row).salePrice) < Number(pricing.draftFor(row).landedCost) ? 'Below cost' : undefined} slotProps={{ htmlInput: { min: 0, step: 'any', 'aria-label': `Selling price for ${row.partNo}`, style: { textAlign: 'right' } } }} />
        : <Typography variant="body2" className="tabular-nums">{recordedPrice(row.sellingPrice)}</Typography>,
    },
    { ...textColumn('priceCurrencyCode', 'Currency', 100), renderCell: ({ row }) => editRow(row)
      ? <TextField select size="small" value={pricing.draftFor(row).currencyId} disabled={pricing.save.isPending || pricing.currencies.isLoading} onChange={(event) => pricing.update(row, 'currencyId', event.target.value)} slotProps={{ select: { displayEmpty: true }, htmlInput: { 'aria-label': `Currency for ${row.partNo}` } }} sx={{ width: '100%' }}><MenuItem value="">Not set</MenuItem>{pricing.currencies.data?.map((currency) => <MenuItem key={currency.id} value={currency.id}>{currency.code}</MenuItem>)}</TextField>
      : <Typography variant="body2">{row.priceCurrencyCode || 'Not set'}</Typography> },
    {
      field: 'actions', headerName: '', width: 52, sortable: false, filterable: false,
      renderCell: ({ row, tabIndex }) => canEdit
        ? <Tooltip title="Edit product"><span><IconButton size="small" disabled={pricing.editing} tabIndex={tabIndex} aria-label={`Edit ${row.partNo}`} onClick={() => openEdit(row.id)}><EditIcon sx={{ fontSize: 18 }} /></IconButton></span></Tooltip>
        : <Link component={RouterLink} to={`/inventory/products/${row.id}`} tabIndex={tabIndex} aria-label={`View ${row.partNo}`} sx={{ minHeight: 44, display: 'inline-flex', alignItems: 'center' }}>View</Link>,
    },
  ];

  return (
    <ProductsWorkspaceShell
      title="Products"
      count={data?.totalItems}
      actions={(
        <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap' }}>
          {canEdit && !pricing.editing && <Button variant="outlined" color="inherit" onClick={startPricing}>{selection.ids.size ? `Edit prices (${selection.ids.size})` : 'Edit prices'}</Button>}
          {pricing.editing && <><Button disabled={pricing.save.isPending} onClick={pricing.cancel}>Cancel</Button><Button variant="contained" disabled={!pricing.count || pricing.invalid || pricing.save.isPending || pricing.currencies.isError} onClick={() => pricing.save.mutate()}>{pricing.save.isPending ? 'Saving…' : 'Save prices'}</Button></>}
          <UploadExportToolbar variant="menu" menuActions={canEdit ? [{ label: 'Record stock', onClick: () => setOpeningStock(true), disabled: pricing.editing }] : []} onDownloadTemplate={productService.downloadTemplate} onUpload={productService.uploadTemplate} onUploadSuccess={() => { void queryClient.invalidateQueries({ queryKey: ['products'] }); void queryClient.invalidateQueries({ queryKey: ['pricing-sheet'] }); }} onExport={productService.export} templateFileName="ProductTemplate.xlsx" exportFileName="Products.xlsx" canUpload={canCreate && !pricing.editing} />
          {canCreate && <Button variant="contained" disabled={pricing.editing} startIcon={<AddIcon />} onClick={openCreate}>Add product</Button>}
        </Stack>
      )}
    >

      <ProductsWorkspaceToolbar>
        <SearchField width="min(100%, 440px)" value={search} onChange={(value) => updateView({ search: value })} placeholder="Search product or part number…" />
        <TextField select size="small" value={stock} onChange={(event) => updateView({ stock: event.target.value })} sx={{ minWidth: 170 }} slotProps={{ select: { SelectDisplayProps: { 'aria-label': 'Stock filter' }, renderValue: (value) => `Stock: ${stockLabels[value as keyof typeof stockLabels]}` } }}>
          {Object.entries(stockLabels).map(([value, label]) => <MenuItem key={value} value={value}>{label}</MenuItem>)}
        </TextField>
        <TextField select size="small" value={warehouseId ?? ''} onChange={(event) => updateView({ warehouse: event.target.value })} disabled={warehouses.isLoading || warehouses.isError} sx={{ minWidth: 210, maxWidth: '100%' }} slotProps={{ select: { displayEmpty: true, SelectDisplayProps: { 'aria-label': 'Warehouse filter' }, renderValue: (value) => `Warehouse: ${value ? warehouses.data?.find((item) => item.id === Number(value))?.warehouseName ?? 'Selected' : 'All'}` } }}>
          {warehouseId && !warehouses.data?.some((warehouse) => warehouse.id === warehouseId) && <MenuItem value={warehouseId} disabled>{warehouses.isLoading ? 'Loading warehouse…' : 'Warehouse unavailable'}</MenuItem>}
          <MenuItem value="">All warehouses</MenuItem>{warehouses.data?.map((warehouse) => <MenuItem key={warehouse.id} value={warehouse.id}>{warehouse.warehouseName}</MenuItem>)}
        </TextField>
        {hasActiveFilters && <Button size="small" color="inherit" onClick={clearFilters}>Clear filters</Button>}
        {isFetching && !isLoading && <Typography variant="caption" color="text.secondary">Refreshing…</Typography>}
        <Tooltip title={filterActive === 'all' ? 'More filters' : 'Record-state filter applied'}><IconButton aria-label="More filters" aria-expanded={!!filterAnchor} aria-haspopup="dialog" color={filterActive !== 'all' ? 'primary' : 'default'} onClick={(event) => setFilterAnchor(event.currentTarget)} sx={{ ml: 'auto', border: '1px solid', borderColor: 'divider', borderRadius: '4px' }}><FilterIcon /></IconButton></Tooltip>
      </ProductsWorkspaceToolbar>
      <Popover open={!!filterAnchor} anchorEl={filterAnchor} onClose={() => setFilterAnchor(null)} anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }} transformOrigin={{ vertical: 'top', horizontal: 'right' }}>
        <Box role="dialog" aria-label="Additional product filters" sx={{ p: 2, width: 260 }}>
          <TextField select fullWidth size="small" label="Record state" value={filterActive} onChange={(event) => updateView({ active: event.target.value })}><MenuItem value="all">All products</MenuItem><MenuItem value="true">Active</MenuItem><MenuItem value="false">Inactive</MenuItem></TextField>
          <Button sx={{ mt: 1 }} onClick={() => setFilterAnchor(null)}>Done</Button>
        </Box>
      </Popover>
      {warehouses.isError && <Alert severity="warning" sx={{ mb: 1.5 }} action={<Button onClick={() => warehouses.refetch()}>Retry</Button>}>Warehouse filters could not be loaded. You can still use the product list.</Alert>}
      {pricing.editing && <Typography role="status" variant="body2" color="text.secondary" sx={{ mb: 1 }}>Editing {editingIds ? `${editingIds.size} selected products` : 'prices'} · {pricing.count} changed. Save applies all your changed rows, including those on other pages.</Typography>}
      {!pricing.editing && selection.ids.size > 0 && <Typography role="status" variant="body2" sx={{ mb: 1 }}>{selection.ids.size} selected · Use Edit prices to update the selected rows.</Typography>}

      {pricing.editing && pricing.invalid && <Alert severity="warning" sx={{ mb: 1.5 }}>Enter landed cost, selling price and currency together, or clear all three. Prices must be above zero.</Alert>}
      {pricing.editing && pricing.currencies.isError && <Alert severity="error" sx={{ mb: 1.5 }} action={<Button onClick={() => pricing.currencies.refetch()}>Retry</Button>}>Currencies could not be loaded.</Alert>}
      {pricing.save.isError && <Alert severity="error" sx={{ mb: 1.5 }}>{pricing.error}</Alert>}

      {isError && <Alert severity="error" sx={{ mb: 1.5 }} action={<Button color="inherit" onClick={() => refetch()}>Retry</Button>}>Inventory could not be loaded.</Alert>}
      {(!isError || data != null) && <Paper sx={{ width: '100%', display: 'flex', flexDirection: 'column', minHeight: 220, maxHeight: 'calc(100dvh - 250px)', border: '1px solid', borderColor: 'divider', borderRadius: '4px', boxShadow: 'none', overflow: 'hidden' }}>
        <DataGrid
          rows={data?.items ?? []}
          columns={columns}
          rowCount={totalRef.current}
          loading={isFetching}
          pageSizeOptions={[10, 25, 50]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={(model) => updateView({ page: String(model.page), size: String(model.pageSize) }, false)}
          sortingMode="server"
          sortModel={sortModel}
          sortingOrder={['asc', 'desc']}
          onSortModelChange={(model) => updateView({ sort: model[0]?.field, direction: model[0]?.sort ?? undefined })}
          checkboxSelection={canEdit}
          disableRowSelectionExcludeModel
          rowSelectionModel={selection}
          onRowSelectionModelChange={setSelection}
          isRowSelectable={() => !pricing.editing}
          disableRowSelectionOnClick
          rowHeight={44}
          getRowHeight={pricing.editing ? () => 'auto' : undefined}
          getEstimatedRowHeight={() => pricing.editing ? 64 : 44}
          columnHeaderHeight={44}
          aria-label="Products register"
          disableColumnMenu
          sx={{
            border: 0,
            '--DataGrid-overlayHeight': '220px',
            fontSize: '0.875rem',
            '& .MuiDataGrid-cell': { display: 'flex', alignItems: 'center', py: pricing.editing ? 0.75 : 0, px: 1.5, lineHeight: 1.4 },
            '& .MuiDataGrid-cell--textRight': { justifyContent: 'flex-end' },
            '& .MuiDataGrid-columnHeader': { px: 1.5, bgcolor: (theme) => theme.palette.mode === 'dark' ? 'background.default' : '#f5f6f8' },
            '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 600, lineHeight: 1.3 },
            '& .MuiDataGrid-columnHeaders': { '--DataGrid-containerBackground': (theme) => theme.palette.mode === 'dark' ? theme.palette.background.default : '#f5f6f8' },
            '& .MuiDataGrid-row:nth-of-type(even)': { bgcolor: 'action.hover' },
            '& .MuiDataGrid-row:hover': { bgcolor: 'action.selected' },
            '& .MuiDataGrid-cellCheckbox, & .MuiDataGrid-columnHeaderCheckbox': { px: 0 },
            '& .MuiDataGrid-cell .MuiIconButton-root': { width: 44, height: 44 },
            '& .MuiDataGrid-cell .MuiButton-root': { fontSize: '0.875rem' },
            '& .MuiDataGrid-checkboxInput': { width: 44, height: 44 },
            '& .MuiDataGrid-cell .MuiTypography-root, & .MuiDataGrid-cell .MuiLink-root': { lineHeight: 1.4 },
            '& .MuiDataGrid-cell .MuiTypography-root': { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
            '& .MuiDataGrid-cell .MuiOutlinedInput-root': { borderRadius: '4px', fontSize: '0.875rem' },
            '& .MuiDataGrid-cell .MuiInputBase-input': { py: 1 },
          }}
          slots={{ noRowsOverlay: () => <Stack spacing={1} sx={{ height: '100%', alignItems: 'center', justifyContent: 'center', px: 2, textAlign: 'center' }}><Typography sx={{ fontWeight: 700 }}>{hasActiveFilters ? 'No products match these filters' : 'No products yet'}</Typography><Typography variant="body2" color="text.secondary">{hasActiveFilters ? 'Clear the filters to see the full list.' : canCreate ? 'Add the first product to begin tracking pricing and stock.' : 'No inventory records are available to your account.'}</Typography>{hasActiveFilters ? <Button size="small" onClick={clearFilters}>Clear filters</Button> : canCreate ? <Button size="small" variant="contained" onClick={openCreate}>Add product</Button> : null}</Stack> }}
        />
      </Paper>}

      <ProductFormDialog open={isFormOpen && (editingProductId ? canEdit : canCreate)} onClose={() => setIsFormOpen(false)} productId={editingProductId} />
      <OpeningStockDialog open={openingStock && canEdit} onClose={() => setOpeningStock(false)} />
      <ProductStockDialog open={stockProduct != null} productId={stockProduct?.id} productName={stockProduct?.productName} onClose={() => setStockProduct(undefined)} />
    </ProductsWorkspaceShell>
  );
};

export default ProductsPage;
