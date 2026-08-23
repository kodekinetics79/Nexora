import React, { useState, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Dialog, DialogTitle, DialogContent, DialogActions,
  Button, TextField, MenuItem, Grid, FormControlLabel,
  Switch, Divider, Typography, CircularProgress,
  Alert,
} from '@mui/material';
import productService from '../../api/services/productService';
import { useAuth } from '../../context/AuthContext';
import { useSnackbar } from 'notistack';

/**
 * The server's own sentence, verbatim.
 *
 * Every refusal this dialog can now provoke arrives already written for an operator — "PartNo
 * VLV-8830 already exists in this Business Unit.", "Category ID 44 does not exist in this Business
 * Unit.", "The field ProductName must be a string with a maximum length of 100." — and that
 * sentence is the only thing that tells the user what to change. Replacing it with "Failed to save
 * product." throws the answer away and leaves them clicking Save again.
 *
 * The 409 branch this replaces was WRONG on the only day it could ever have fired. It rendered
 * "This product changed since you opened it. Reload and review the latest values." — a stale-record
 * message — but the sole Conflict() in ProductController was inside Delete, which this dialog never
 * calls, so the branch was unreachable and nobody noticed. Create and Update now return 409 for a
 * duplicate part number, which would have shown a reload prompt for a problem no reload can fix.
 *
 * Reads all three shapes ASP.NET actually sends: the RFC 7807 body the controller builds
 * (`detail`/`title`), the `{ error }` body the delete path uses, and the ModelState dictionary an
 * automatic 400 produces. Mirrors the local helpers in CustomersPage and DeliveryConfirmationPanel
 * rather than importing the Inventory/Commercial one, which does not unwrap the ModelState bag.
 */
const serverMessage = (error: unknown, fallback: string): string => {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;

  const body = data as { detail?: string; error?: string; title?: string; errors?: Record<string, string[]> } | undefined;
  if (body?.detail?.trim()) return body.detail;
  if (body?.error?.trim()) return body.error;

  if (body?.errors) {
    const first = Object.values(body.errors).flat().find(message => typeof message === 'string' && message.trim());
    if (first) return first;
  }

  return body?.title?.trim() ? body.title : fallback;
};

/**
 * The server's caps, stated at the keyboard instead of at the Save button.
 *
 * ProductCreateRequestDTO and ProductUpdateRequestDTO cap ProductName at 100 and Description at
 * 500, mirroring `Products."ProductName"` varchar(100) and `Products."Description"` varchar(500).
 * Those attributes were tightened so an over-long value is refused by validation with a readable
 * sentence rather than dying inside the INSERT as Postgres 22001 — but a refusal that only arrives
 * after Save still costs the user everything they typed. Repeating the cap here means the 101st
 * character is never typed in the first place.
 *
 * The counter is not decoration. `maxLength` swallows keystrokes silently, which reads as a broken
 * keyboard unless something on screen says why — so the field states where it is, and says so
 * plainly at the boundary. Same pairing the rest of the app already uses (AccountsReceivablePage,
 * ExtendValidityDialog): `htmlInput.maxLength` plus an `n/max` helper.
 *
 * Defence in depth, not a replacement: `serverMessage` above still renders the server's own
 * sentence, and the server remains the authority on what fits.
 */
const PRODUCT_NAME_MAX = 100;
const DESCRIPTION_MAX = 500;

/** `12/100` — and, at the boundary, why the keyboard appears to have stopped. */
const counter = (value: string, max: number) =>
  value.length >= max ? `${value.length}/${max} · limit reached` : `${value.length}/${max}`;

interface Props {
  open: boolean;
  onClose: () => void;
  productId?: number; // if provided, loads for edit
}

const ProductFormDialog: React.FC<Props> = ({ open, onClose, productId }) => {
  const { t } = useTranslation();
  const { userData, hasPermission } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const isEdit = !!productId;

  const emptyForm = {
    productName: '', partNo: '', modelNo: '', description: '',
    categoryId: '', subCategoryId: '', reorderPoint: 0,
    uomId: '', unitCost: '', sellingPrice: '', finalLandedCost: '', finalSalesPrice: '',
    warehouseId: '', preferredSupplierId: '',
    batchTracking: false, serialTracking: false, expirationDate: '',
    height: '', width: '', depth: '', weight: '', dimensions: '', barcode: '', qrcode: '',
    leadTime: '', hscode: '', countryOfOrigin: '', isActive: true, isCatalogItem: false,
  };

  const [form, setForm] = useState(emptyForm);

  // Lookups
  const { data: categories } = useQuery({ queryKey: ['product-categories'], queryFn: productService.getCategories, enabled: open });
  const { data: subCategories } = useQuery({ queryKey: ['product-subcategories'], queryFn: productService.getSubCategories, enabled: open });
  const { data: warehouses } = useQuery({ queryKey: ['product-warehouses'], queryFn: productService.getWarehouses, enabled: open });
  const { data: uoms } = useQuery({ queryKey: ['product-uoms'], queryFn: productService.getUoms, enabled: open });
  const { data: suppliers } = useQuery({ queryKey: ['product-suppliers'], queryFn: productService.getSuppliers, enabled: open });

  // Load for edit
  const { data: editData, isLoading: isEditLoading, isError: isEditError, refetch: refetchEdit } = useQuery({
    queryKey: ['product-detail', productId],
    queryFn: () => productService.getById(productId!),
    enabled: open && isEdit,
  });

  useEffect(() => {
    if (editData && open && isEdit) {
      setForm({
        productName: editData.productName ?? '',
        partNo: editData.partNo ?? '',
        modelNo: editData.modelNo ?? '',
        description: editData.description ?? '',
        categoryId: editData.categoryId != null ? String(editData.categoryId) : '',
        subCategoryId: editData.subCategoryId != null ? String(editData.subCategoryId) : '',
        reorderPoint: editData.reorderPoint ?? 0,
        uomId: editData.uomId != null ? String(editData.uomId) : '',
        unitCost: editData.unitCost != null ? String(editData.unitCost) : '',
        sellingPrice: editData.sellingPrice != null ? String(editData.sellingPrice) : '',
        finalLandedCost: editData.finalLandedCost != null ? String(editData.finalLandedCost) : '',
        finalSalesPrice: editData.finalSalesPrice != null ? String(editData.finalSalesPrice) : '',
        warehouseId: editData.warehouseId != null ? String(editData.warehouseId) : '',
        preferredSupplierId: editData.preferredSupplierId != null ? String(editData.preferredSupplierId) : '',
        batchTracking: editData.batchTracking ?? false,
        serialTracking: editData.serialTracking ?? false,
        expirationDate: editData.expirationDate ?? '',
        height: editData.height != null ? String(editData.height) : '',
        width: editData.width != null ? String(editData.width) : '',
        depth: editData.depth != null ? String(editData.depth) : '',
        weight: editData.weight != null ? String(editData.weight) : '',
        dimensions: editData.dimensions ?? '',
        barcode: editData.barcode ?? '',
        qrcode: editData.qrcode ?? '',
        leadTime: editData.leadTime != null ? String(editData.leadTime) : '',
        hscode: editData.hscode ?? '',
        countryOfOrigin: editData.countryOfOrigin ?? '',
        isActive: editData.isActive ?? true,
        isCatalogItem: editData.isCatalogItem ?? false,
      });
    }
  }, [editData, open, isEdit]);

  const saveMutation = useMutation({
    mutationFn: (fd: FormData) => isEdit ? productService.update(productId!, fd) : productService.create(fd),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['products'] });
      queryClient.invalidateQueries({ queryKey: ['product-detail', productId] });
      enqueueSnackbar(isEdit ? 'Product updated!' : 'Product created!', { variant: 'success' });
      handleClose();
    },
    onError: (error) => enqueueSnackbar(serverMessage(error, 'Failed to save product.'), { variant: 'error' }),
  });

  const handleClose = () => {
    setForm(emptyForm);
    onClose();
  };

  const handleSave = () => {
    if (!hasPermission('Products', isEdit ? 'edit' : 'create')) {
      enqueueSnackbar('You do not have permission to save this product.', { variant: 'error' });
      return;
    }
    const fd = new FormData();
    Object.entries(form).forEach(([k, v]) => {
      if (v !== '' && v !== null && v !== undefined) fd.append(k, String(v));
    });
    if (!isEdit) fd.append('createdBy', userData.userName || 'System');
    else fd.append('modifiedBy', userData.userName || 'System');
    fd.append('buid', String(userData.businessUnitId || 1));
    saveMutation.mutate(fd);
  };

  const f = (field: string) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm(prev => ({ ...prev, [field]: e.target.value }));

  const SectionTitle = ({ label }: { label: string }) => (
    <Grid size={{ xs: 12 }}>
      <Divider sx={{ mt: 1 }} />
      <Typography variant="overline" sx={{ fontWeight: 700, color: 'text.secondary', letterSpacing: 1, mt: 1.5, display: 'block' }}>
        {label}
      </Typography>
    </Grid>
  );

  return (
    <Dialog open={open} onClose={handleClose} fullWidth maxWidth="md">
      <DialogTitle sx={{ fontWeight: 800 }}>{isEdit ? 'Edit Product' : 'Add New Product'}</DialogTitle>
      <DialogContent dividers sx={{ p: 3 }}>
        <Grid container spacing={2}>

          {/* Basic Info */}
          <SectionTitle label="Basic Information" />
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField
              fullWidth
              label="Product Name"
              value={form.productName}
              onChange={f('productName')}
              helperText={counter(form.productName, PRODUCT_NAME_MAX)}
              slotProps={{ htmlInput: { maxLength: PRODUCT_NAME_MAX } }}
            />
          </Grid>
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField fullWidth label="Part No" value={form.partNo} onChange={f('partNo')} required />
          </Grid>
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField fullWidth label="Model No" value={form.modelNo} onChange={f('modelNo')} />
          </Grid>
          <Grid size={{ xs: 12, sm: 3 }}>
            <TextField select fullWidth label={t('categories') || 'Category'} value={form.categoryId} onChange={f('categoryId')}>
              <MenuItem value="">None</MenuItem>
              {categories?.map((c: any) => <MenuItem key={c.id ?? c.categoryId} value={c.id ?? c.categoryId}>{c.name ?? c.categoryName}</MenuItem>)}
            </TextField>
          </Grid>
          <Grid size={{ xs: 12, sm: 3 }}>
            <TextField select fullWidth label={t('sub_categories') || 'Sub-Category'} value={form.subCategoryId} onChange={f('subCategoryId')}>
              <MenuItem value="">None</MenuItem>
              {subCategories?.map((c: any) => <MenuItem key={c.id ?? c.subCategoryId} value={c.id ?? c.subCategoryId}>{c.name ?? c.subCategoryName}</MenuItem>)}
            </TextField>
          </Grid>
          <Grid size={{ xs: 12 }}>
            <TextField
              fullWidth
              multiline
              rows={2}
              label="Description"
              value={form.description}
              onChange={f('description')}
              helperText={counter(form.description, DESCRIPTION_MAX)}
              slotProps={{ htmlInput: { maxLength: DESCRIPTION_MAX } }}
            />
          </Grid>

          {isEditLoading && <Grid size={{ xs: 12 }}><CircularProgress size={22} aria-label="Loading product" /></Grid>}
          {isEditError && <Grid size={{ xs: 12 }}><Alert severity="error" action={<Button color="inherit" onClick={() => refetchEdit()}>Retry</Button>}>The product could not be loaded for editing.</Alert></Grid>}

          {/* Pricing & Stock */}
          <SectionTitle label="Pricing & Stock" />
          <Grid size={{ xs: 6, sm: 3 }}>
            <TextField fullWidth type="number" label="Reorder Point" value={form.reorderPoint} onChange={f('reorderPoint')} />
          </Grid>
          <Grid size={{ xs: 12, sm: 3 }}>
            <TextField select fullWidth label={t('uom') || 'UOM'} value={form.uomId} onChange={f('uomId')}>
              <MenuItem value="">None</MenuItem>
              {uoms?.map((u: any) => <MenuItem key={u.id} value={String(u.id)}>{u.value ?? u.name ?? u.uomName}</MenuItem>)}
            </TextField>
          </Grid>
          <Grid size={{ xs: 12, sm: 3 }}>
            <TextField fullWidth type="number" label="Unit Cost" value={form.unitCost} onChange={f('unitCost')} />
          </Grid>
          <Grid size={{ xs: 12, sm: 3 }}>
            <TextField fullWidth type="number" label="Selling Price ($)" value={form.sellingPrice} onChange={f('sellingPrice')} />
          </Grid>
          <Grid size={{ xs: 12, sm: 3 }}>
            <TextField fullWidth type="number" label="Final Landed Cost ($)" value={form.finalLandedCost} onChange={f('finalLandedCost')} />
          </Grid>
          <Grid size={{ xs: 12, sm: 3 }}>
            <TextField fullWidth type="number" label="Final Sales Price ($)" value={form.finalSalesPrice} onChange={f('finalSalesPrice')} />
          </Grid>

          {/* Logistics */}
          <SectionTitle label="Logistics" />
          <Grid size={{ xs: 12, sm: 4 }}>
            <TextField select fullWidth label={t('warehouse') || 'Warehouse'} value={form.warehouseId} onChange={f('warehouseId')}>
              <MenuItem value="">None</MenuItem>
              {warehouses?.map((w: any) => <MenuItem key={w.id ?? w.warehouseId} value={w.id ?? w.warehouseId}>{w.name ?? w.warehouseName}</MenuItem>)}
            </TextField>
          </Grid>
          <Grid size={{ xs: 12, sm: 4 }}>
            <TextField select fullWidth label={t('supplier') || 'Preferred Supplier'} value={form.preferredSupplierId} onChange={f('preferredSupplierId')}>
              <MenuItem value="">None</MenuItem>
              {suppliers?.map((s: any) => <MenuItem key={s.id} value={s.id}>{s.name}</MenuItem>)}
            </TextField>
          </Grid>
          <Grid size={{ xs: 12, sm: 2 }}>
            <TextField fullWidth type="number" label="Lead Time (days)" value={form.leadTime} onChange={f('leadTime')} />
          </Grid>
          <Grid size={{ xs: 12, sm: 2 }}>
            <TextField fullWidth label={t('country') || 'Country of Origin'} value={form.countryOfOrigin} onChange={f('countryOfOrigin')} />
          </Grid>
          <Grid size={{ xs: 12, sm: 4 }}>
            <TextField fullWidth label="HS Code" value={form.hscode} onChange={f('hscode')} />
          </Grid>
          <Grid size={{ xs: 12, sm: 4 }}>
            <TextField fullWidth label="Barcode" value={form.barcode} onChange={f('barcode')} />
          </Grid>
          <Grid size={{ xs: 12, sm: 4 }}>
            <TextField fullWidth label="QR Code" value={form.qrcode} onChange={f('qrcode')} />
          </Grid>

          {/* Physical */}
          <SectionTitle label="Physical Dimensions" />
          <Grid size={{ xs: 6, sm: 3 }}>
            <TextField fullWidth type="number" label="Height" value={form.height} onChange={f('height')} />
          </Grid>
          <Grid size={{ xs: 6, sm: 3 }}>
            <TextField fullWidth type="number" label="Width" value={form.width} onChange={f('width')} />
          </Grid>
          <Grid size={{ xs: 6, sm: 3 }}>
            <TextField fullWidth type="number" label="Depth" value={form.depth} onChange={f('depth')} />
          </Grid>
          <Grid size={{ xs: 6, sm: 3 }}>
            <TextField fullWidth type="number" label="Weight (kg)" value={form.weight} onChange={f('weight')} />
          </Grid>
          <Grid size={{ xs: 12 }}>
            <TextField fullWidth label="Dimensions (text)" value={form.dimensions} onChange={f('dimensions')} placeholder="e.g. 10x5x3 cm" />
          </Grid>

          {/* Tracking & Flags */}
          <SectionTitle label="Tracking & Status" />
          <Grid size={{ xs: 12, sm: 3 }}>
            <TextField fullWidth type="date" label="Expiration Date" value={form.expirationDate} onChange={f('expirationDate')} slotProps={{ inputLabel: { shrink: true } }} />
          </Grid>
          <Grid size={{ xs: 12, sm: 9 }} sx={{ display: 'flex', alignItems: 'center', gap: 3 }}>
            <FormControlLabel control={<Switch checked={form.batchTracking} onChange={(e) => setForm(p => ({ ...p, batchTracking: e.target.checked }))} />} label="Batch Tracking" />
            <FormControlLabel control={<Switch checked={form.serialTracking} onChange={(e) => setForm(p => ({ ...p, serialTracking: e.target.checked }))} />} label="Serial Tracking" />
            <FormControlLabel control={<Switch checked={form.isCatalogItem} onChange={(e) => setForm(p => ({ ...p, isCatalogItem: e.target.checked }))} />} label="Catalog Item" />
            <FormControlLabel control={<Switch checked={form.isActive} onChange={(e) => setForm(p => ({ ...p, isActive: e.target.checked }))} color="primary" />} label="Active" />
          </Grid>

        </Grid>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={handleClose} color="inherit">{t('cancel') || 'Cancel'}</Button>
        <Button variant="contained" onClick={handleSave} disabled={saveMutation.isPending || isEditLoading || isEditError || !hasPermission('Products', isEdit ? 'edit' : 'create')} sx={{ px: 4 }}>
          {saveMutation.isPending ? <CircularProgress size={22} /> : (isEdit ? 'Update Product' : 'Create Product')}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default ProductFormDialog;
