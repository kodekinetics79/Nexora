import React, { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  Grid,
  InputAdornment,
  MenuItem,
  Stack,
  Switch,
  TextField,
  Typography,
} from '@mui/material';
import { ExpandMore as ExpandMoreIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import productService from '../../api/services/productService';
import { useAuth } from '../../context/AuthContext';

const PRODUCT_NAME_MAX = 100;
const DESCRIPTION_MAX = 500;

const serverMessage = (error: unknown, fallback: string): string => {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;
  const body = data as { detail?: string; error?: string; title?: string; errors?: Record<string, string[]> } | undefined;
  if (body?.detail?.trim()) return body.detail;
  if (body?.error?.trim()) return body.error;
  const validation = body?.errors && Object.values(body.errors).flat().find(Boolean);
  return validation || body?.title || fallback;
};

const counter = (value: string, max: number) =>
  value.length >= max ? `${value.length}/${max} · limit reached` : `${value.length}/${max}`;

const supplierTierLabel = (tier?: string) => tier === 'TIER_1_PARTNER'
  ? 'In Network'
  : tier === 'TIER_2_EXTENDED'
    ? 'Extended Network'
    : tier === 'TIER_3_OUT_OF_NETWORK'
      ? 'New supplier'
      : '';

const emptyForm = {
  productName: '', partNo: '', modelNo: '', description: '',
  categoryId: '', subCategoryId: '', reorderPoint: '0', uomId: '',
  priceCurrencyId: '', unitCost: '', sellingPrice: '',
  warehouseId: '', preferredSupplierId: '', leadTime: '', countryOfOrigin: '',
  hscode: '', barcode: '', qrcode: '', height: '', width: '', depth: '', weight: '', dimensions: '',
  batchTracking: false, serialTracking: false, expirationDate: '', isActive: true, isCatalogItem: false,
};

type ProductForm = typeof emptyForm;
type FieldErrors = Partial<Record<'partNo' | 'priceCurrencyId' | 'unitCost' | 'sellingPrice', string>>;

interface Props {
  open: boolean;
  onClose: () => void;
  productId?: number;
}

const useProductLookup = <T,>(key: string, queryFn: () => Promise<T>, enabled: boolean) => useQuery({
  queryKey: [key], queryFn, enabled,
});

const ProductFormDialog: React.FC<Props> = ({ open, onClose, productId }) => {
  const { t } = useTranslation();
  const { userData, hasPermission } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const isEdit = productId != null;
  const [form, setForm] = useState<ProductForm>(emptyForm);
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState('');
  const [pricingChanged, setPricingChanged] = useState(false);

  const { data: categories } = useProductLookup('product-categories', productService.getCategories, open);
  const { data: subCategories } = useProductLookup('product-subcategories', productService.getSubCategories, open);
  const { data: warehouses } = useProductLookup('product-warehouses', productService.getWarehouses, open);
  const { data: uoms } = useProductLookup('product-uoms', productService.getUoms, open);
  const { data: suppliers } = useProductLookup('product-suppliers', productService.getSuppliers, open);
  const { data: currencies } = useProductLookup('product-price-currencies', productService.getCurrencies, open);

  const { data: editData, isLoading: isEditLoading, isError: isEditError, refetch: refetchEdit } = useQuery({
    queryKey: ['product-detail', productId],
    queryFn: () => productService.getById(productId!),
    enabled: open && isEdit,
  });

  useEffect(() => {
    if (!open || !isEdit || !editData) return;
    setForm({
      productName: editData.productName ?? '',
      partNo: editData.partNo ?? '',
      modelNo: editData.modelNo ?? '',
      description: editData.description ?? '',
      categoryId: editData.categoryId == null ? '' : String(editData.categoryId),
      subCategoryId: editData.subCategoryId == null ? '' : String(editData.subCategoryId),
      reorderPoint: String(editData.reorderPoint ?? 0),
      uomId: editData.uomId == null ? '' : String(editData.uomId),
      priceCurrencyId: editData.priceCurrencyId == null ? '' : String(editData.priceCurrencyId),
      unitCost: editData.unitCost == null ? '' : String(editData.unitCost),
      sellingPrice: editData.sellingPrice == null ? '' : String(editData.sellingPrice),
      warehouseId: editData.warehouseId == null ? '' : String(editData.warehouseId),
      preferredSupplierId: editData.preferredSupplierId == null ? '' : String(editData.preferredSupplierId),
      leadTime: editData.leadTime == null ? '' : String(editData.leadTime),
      countryOfOrigin: editData.countryOfOrigin ?? '',
      hscode: editData.hscode ?? '',
      barcode: editData.barcode ?? '',
      qrcode: editData.qrcode ?? '',
      height: editData.height == null ? '' : String(editData.height),
      width: editData.width == null ? '' : String(editData.width),
      depth: editData.depth == null ? '' : String(editData.depth),
      weight: editData.weight == null ? '' : String(editData.weight),
      dimensions: editData.dimensions ?? '',
      batchTracking: editData.batchTracking ?? false,
      serialTracking: editData.serialTracking ?? false,
      expirationDate: editData.expirationDate ?? '',
      isActive: editData.isActive ?? true,
      isCatalogItem: editData.isCatalogItem ?? false,
    });
    setPricingChanged(false);
  }, [editData, isEdit, open]);

  const close = () => {
    setForm(emptyForm);
    setFieldErrors({});
    setFormError('');
    setPricingChanged(false);
    onClose();
  };

  const saveMutation = useMutation({
    mutationFn: (data: FormData) => isEdit ? productService.update(productId!, data) : productService.create(data),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['products'] });
      void queryClient.invalidateQueries({ queryKey: ['product-detail', productId] });
      void queryClient.invalidateQueries({ queryKey: ['pricing-sheet'] });
      void queryClient.invalidateQueries({ queryKey: ['stock-price'] });
      enqueueSnackbar(isEdit ? 'Product updated' : 'Product created', { variant: 'success' });
      close();
    },
    onError: (error) => setFormError(serverMessage(error, 'The product could not be saved.')),
  });

  const setField = (field: keyof ProductForm) => (event: React.ChangeEvent<HTMLInputElement>) => {
    setForm((current) => ({ ...current, [field]: event.target.value }));
    if (field === 'unitCost' || field === 'sellingPrice' || field === 'priceCurrencyId') setPricingChanged(true);
    setFieldErrors((current) => ({ ...current, [field]: undefined }));
    setFormError('');
  };

  const validate = (): boolean => {
    const errors: FieldErrors = {};
    const cost = form.unitCost === '' ? null : Number(form.unitCost);
    const sale = form.sellingPrice === '' ? null : Number(form.sellingPrice);
    const currencyEntered = form.priceCurrencyId !== '';
    const anyPricingEntered = cost != null || sale != null || currencyEntered;
    const completePricing = cost != null && sale != null && currencyEntered;
    if (!form.partNo.trim()) errors.partNo = 'Part number is required.';
    if (!isEdit || pricingChanged) {
      if (anyPricingEntered && cost == null) errors.unitCost = 'Enter landed cost or clear all pricing fields.';
      else if (cost != null && (!Number.isFinite(cost) || cost <= 0)) errors.unitCost = 'Enter an amount above 0.';
      if (anyPricingEntered && sale == null) errors.sellingPrice = 'Enter selling price or clear all pricing fields.';
      else if (sale != null && (!Number.isFinite(sale) || sale <= 0)) errors.sellingPrice = 'Enter an amount above 0.';
      if (anyPricingEntered && !currencyEntered) errors.priceCurrencyId = 'Choose a currency or clear both prices.';
      if (anyPricingEntered && !completePricing && !errors.priceCurrencyId && !currencyEntered) errors.priceCurrencyId = 'Complete all pricing fields or clear them.';
    }
    setFieldErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const save = (event: React.FormEvent) => {
    event.preventDefault();
    if (!hasPermission('Products', isEdit ? 'edit' : 'create')) {
      setFormError('You do not have permission to save this product.');
      return;
    }
    if (!validate()) return;

    const data = new FormData();
    Object.entries(form).forEach(([key, value]) => {
      if (value !== '') data.append(key, String(value));
    });
    if (isEdit) {
      data.append('applyPricing', String(pricingChanged));
      data.append('modifiedBy', userData.userName || 'System');
    } else data.append('createdBy', userData.userName || 'System');
    data.append('buid', String(userData.businessUnitId || 1));
    saveMutation.mutate(data);
  };

  const landedCost = Number(form.unitCost);
  const sellingPrice = Number(form.sellingPrice);
  const pricesEntered = form.unitCost !== '' && form.sellingPrice !== '' && landedCost > 0 && sellingPrice > 0;
  const margin = pricesEntered ? ((sellingPrice - landedCost) / landedCost) * 100 : null;
  const belowCost = pricesEntered && sellingPrice < landedCost;
  const selectedCurrency = currencies?.find((currency) => String(currency.id) === form.priceCurrencyId)?.code || '—';

  return (
    <Dialog open={open} onClose={close} fullWidth maxWidth="md">
      <DialogTitle sx={{ fontWeight: 800 }}>{isEdit ? 'Edit product' : 'Add product'}</DialogTitle>
      <DialogContent dividers sx={{ p: { xs: 2, sm: 3 } }}>
        <Box component="form" id="product-form" noValidate onSubmit={save}>
          <Grid container spacing={2}>
            {formError && <Grid size={{ xs: 12 }}><Alert severity="error">{formError}</Alert></Grid>}
            {isEditLoading && <Grid size={{ xs: 12 }}><Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}><CircularProgress size={20} /><Typography>Loading product…</Typography></Stack></Grid>}
            {isEditError && <Grid size={{ xs: 12 }}><Alert severity="error" action={<Button color="inherit" onClick={() => refetchEdit()}>Retry</Button>}>The product could not be loaded for editing.</Alert></Grid>}

            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField required fullWidth label="Part number" value={form.partNo} onChange={setField('partNo')} error={!!fieldErrors.partNo} helperText={fieldErrors.partNo} />
            </Grid>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField fullWidth label="Product name" value={form.productName} onChange={setField('productName')} helperText={counter(form.productName, PRODUCT_NAME_MAX)} slotProps={{ htmlInput: { maxLength: PRODUCT_NAME_MAX } }} />
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField select fullWidth label={t('uom') || 'UOM'} value={form.uomId} onChange={setField('uomId')}>
                <MenuItem value="">Not set</MenuItem>
                {uoms?.map((u: any) => <MenuItem key={u.id} value={String(u.id)}>{u.value ?? u.name ?? u.uomName}</MenuItem>)}
              </TextField>
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField fullWidth type="number" label="Reorder point" value={form.reorderPoint} onChange={setField('reorderPoint')} slotProps={{ htmlInput: { min: 0 } }} />
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField select fullWidth label="Price currency" value={form.priceCurrencyId} onChange={setField('priceCurrencyId')} error={!!fieldErrors.priceCurrencyId} helperText={fieldErrors.priceCurrencyId}>
                <MenuItem value="">Unpriced</MenuItem>
                {currencies?.map((currency) => <MenuItem key={currency.id} value={String(currency.id)}>{currency.code}{currency.isBase ? ' · base' : ''}</MenuItem>)}
              </TextField>
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField fullWidth type="number" label="Landed cost" value={form.unitCost} onChange={setField('unitCost')} error={!!fieldErrors.unitCost} helperText={fieldErrors.unitCost || 'Cost delivered into stock'} slotProps={{ input: { startAdornment: <InputAdornment position="start">{selectedCurrency}</InputAdornment> }, htmlInput: { min: 0, step: '0.01' } }} />
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField fullWidth type="number" label="Selling price" value={form.sellingPrice} onChange={setField('sellingPrice')} error={!!fieldErrors.sellingPrice} helperText={fieldErrors.sellingPrice || 'Current catalogue sale price'} slotProps={{ input: { startAdornment: <InputAdornment position="start">{selectedCurrency}</InputAdornment> }, htmlInput: { min: 0, step: '0.01' } }} />
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }} sx={{ display: 'flex', alignItems: 'center' }}>
              <Box><Typography variant="caption" color="text.secondary">Margin on cost</Typography><Typography sx={{ fontWeight: 800 }}>{margin == null ? '—' : `${margin.toFixed(1)}%`}</Typography></Box>
            </Grid>
            {belowCost && <Grid size={{ xs: 12 }}><Alert severity="warning">Selling price is below landed cost. You can save it, but this product has a negative margin.</Alert></Grid>}

            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField select fullWidth label="Preferred supplier" value={form.preferredSupplierId} onChange={setField('preferredSupplierId')}>
                <MenuItem value="">Not set</MenuItem>
                {suppliers?.map((supplier: any) => <MenuItem key={supplier.id} value={String(supplier.id)}>{supplier.name}{supplierTierLabel(supplier.tier) ? ` · ${supplierTierLabel(supplier.tier)}` : ''}</MenuItem>)}
              </TextField>
            </Grid>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField select fullWidth label="Default warehouse" value={form.warehouseId} onChange={setField('warehouseId')}>
                <MenuItem value="">Not set</MenuItem>
                {warehouses?.map((warehouse: any) => <MenuItem key={warehouse.id ?? warehouse.warehouseId} value={String(warehouse.id ?? warehouse.warehouseId)}>{warehouse.name ?? warehouse.warehouseName}</MenuItem>)}
              </TextField>
            </Grid>

            <Grid size={{ xs: 12 }}>
              <Accordion disableGutters elevation={0} sx={{ border: '1px solid', borderColor: 'divider', borderRadius: '10px !important', '&::before': { display: 'none' } }}>
                <AccordionSummary expandIcon={<ExpandMoreIcon />}>
                  <Box><Typography sx={{ fontWeight: 700 }}>More product details</Typography><Typography variant="body2" color="text.secondary">Description, classification, identifiers, dimensions and tracking</Typography></Box>
                </AccordionSummary>
                <AccordionDetails>
                  <Grid container spacing={2}>
                    <Grid size={{ xs: 12, sm: 6 }}><TextField fullWidth label="Model number" value={form.modelNo} onChange={setField('modelNo')} /></Grid>
                    <Grid size={{ xs: 12, sm: 3 }}><TextField select fullWidth label="Category" value={form.categoryId} onChange={setField('categoryId')}><MenuItem value="">None</MenuItem>{categories?.map((c: any) => <MenuItem key={c.id ?? c.categoryId} value={String(c.id ?? c.categoryId)}>{c.name ?? c.categoryName}</MenuItem>)}</TextField></Grid>
                    <Grid size={{ xs: 12, sm: 3 }}><TextField select fullWidth label="Sub-category" value={form.subCategoryId} onChange={setField('subCategoryId')}><MenuItem value="">None</MenuItem>{subCategories?.map((c: any) => <MenuItem key={c.id ?? c.subCategoryId} value={String(c.id ?? c.subCategoryId)}>{c.name ?? c.subCategoryName}</MenuItem>)}</TextField></Grid>
                    <Grid size={{ xs: 12 }}><TextField fullWidth multiline rows={2} label="Description" value={form.description} onChange={setField('description')} helperText={counter(form.description, DESCRIPTION_MAX)} slotProps={{ htmlInput: { maxLength: DESCRIPTION_MAX } }} /></Grid>
                    <Grid size={{ xs: 6, sm: 3 }}><TextField fullWidth type="number" label="Lead time (days)" value={form.leadTime} onChange={setField('leadTime')} /></Grid>
                    <Grid size={{ xs: 6, sm: 3 }}><TextField fullWidth label="Country of origin" value={form.countryOfOrigin} onChange={setField('countryOfOrigin')} /></Grid>
                    <Grid size={{ xs: 6, sm: 3 }}><TextField fullWidth label="HS code" value={form.hscode} onChange={setField('hscode')} /></Grid>
                    <Grid size={{ xs: 6, sm: 3 }}><TextField fullWidth label="Barcode" value={form.barcode} onChange={setField('barcode')} /></Grid>
                    <Grid size={{ xs: 6, sm: 3 }}><TextField fullWidth label="QR code" value={form.qrcode} onChange={setField('qrcode')} /></Grid>
                    <Grid size={{ xs: 6, sm: 3 }}><TextField fullWidth label="Dimensions" value={form.dimensions} onChange={setField('dimensions')} /></Grid>
                    <Grid size={{ xs: 6, sm: 3 }}><TextField fullWidth type="number" label="Weight (kg)" value={form.weight} onChange={setField('weight')} /></Grid>
                    <Grid size={{ xs: 6, sm: 3 }}><TextField fullWidth type="date" label="Expiration date" value={form.expirationDate} onChange={setField('expirationDate')} slotProps={{ inputLabel: { shrink: true } }} /></Grid>
                    <Grid size={{ xs: 12 }}><Stack direction={{ xs: 'column', sm: 'row' }} spacing={1}><FormControlLabel control={<Switch checked={form.batchTracking} onChange={(e) => setForm((current) => ({ ...current, batchTracking: e.target.checked }))} />} label="Batch tracking" /><FormControlLabel control={<Switch checked={form.serialTracking} onChange={(e) => setForm((current) => ({ ...current, serialTracking: e.target.checked }))} />} label="Serial tracking" /><FormControlLabel control={<Switch checked={form.isCatalogItem} onChange={(e) => setForm((current) => ({ ...current, isCatalogItem: e.target.checked }))} />} label="Catalogue item" /><FormControlLabel control={<Switch checked={form.isActive} onChange={(e) => setForm((current) => ({ ...current, isActive: e.target.checked }))} />} label="Active" /></Stack></Grid>
                  </Grid>
                </AccordionDetails>
              </Accordion>
            </Grid>
          </Grid>
        </Box>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={close} color="inherit">{t('cancel') || 'Cancel'}</Button>
        <Button form="product-form" type="submit" variant="contained" disabled={saveMutation.isPending || isEditLoading || isEditError || !hasPermission('Products', isEdit ? 'edit' : 'create')} sx={{ minWidth: 140 }}>
          {saveMutation.isPending ? <CircularProgress size={22} /> : (isEdit ? 'Save product' : 'Create product')}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default ProductFormDialog;
