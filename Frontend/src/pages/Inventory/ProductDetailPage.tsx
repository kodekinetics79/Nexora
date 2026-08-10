import React, { useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, Grid,
  CircularProgress, Divider, Alert,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Edit as EditIcon,
  Inventory2 as InventoryIcon,
  AttachFile as AttachIcon,
} from '@mui/icons-material';
import productService from '../../api/services/productService';
import ProductFormDialog from './ProductFormDialog';
import ChangeHistoryPanel from '../../components/common/ChangeHistoryPanel';
import { useAuth } from '../../context/AuthContext';

// ─── Info Row ──────────────────────────────────────────────────────────────
const InfoRow: React.FC<{ label: string; value: React.ReactNode; mono?: boolean }> = ({ label, value, mono }) => (
  <Box sx={{ display: 'flex', gap: 2, py: 1, borderBottom: '1px solid', borderColor: 'divider', alignItems: 'center', '&:last-child': { border: 'none', pb: 0 } }}>
    <Typography component="span" sx={{ minWidth: 150, color: 'text.secondary', fontSize: '0.8rem', fontWeight: 600, flexShrink: 0 }}>
      {label}
    </Typography>
    <Box sx={{ fontSize: '0.875rem', fontWeight: 600, fontFamily: mono ? 'monospace' : 'inherit', flex: 1 }}>
      {value ?? <Typography component="span" sx={{ color: '#9ca3af', fontSize: '0.875rem' }}>Not set</Typography>}
    </Box>
  </Box>
);

// ─── Section ───────────────────────────────────────────────────────────────
const Section: React.FC<{ title: string; children: React.ReactNode; noPad?: boolean }> = ({ title, children, noPad }) => (
  <Box>
    <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.secondary', textTransform: 'uppercase', letterSpacing: '0.08em', display: 'block', mb: 1.5 }}>
      {title}
    </Typography>
    <Paper sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', p: noPad ? 0 : 2.5, overflow: 'hidden' }}>
      {children}
    </Paper>
  </Box>
);

// ─── Stat Box ──────────────────────────────────────────────────────────────
const StatBox: React.FC<{ label: string; value: React.ReactNode }> = ({ label, value }) => (
  <Box sx={{ flex: 1, textAlign: 'center', p: 2, borderRight: '1px solid', borderColor: 'divider', '&:last-child': { border: 'none' } }}>
    <Typography sx={{ fontSize: '0.7rem', color: 'text.secondary', fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', mb: 0.5 }}>
      {label}
    </Typography>
    <Typography sx={{ fontSize: '1.25rem', fontWeight: 900, color: 'text.primary' }}>
      {value ?? '—'}
    </Typography>
  </Box>
);

// ─── Main ─────────────────────────────────────────────────────────────────
const ProductDetailPage: React.FC = () => {
  const { t } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const canEdit = hasPermission('Products', 'edit');
  const [editOpen, setEditOpen] = useState(false);

  const { data: product, isLoading, isError, refetch } = useQuery({
    queryKey: ['product-detail', id],
    queryFn: () => productService.getById(Number(id)),
    enabled: !!id,
  });

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}>
        <CircularProgress />
      </Box>
    );
  }

  if (isError) {
    return <Box sx={{ p: 4 }}><Alert severity="error" action={<Button color="inherit" onClick={() => refetch()}>Retry</Button>}>The product could not be loaded.</Alert></Box>;
  }

  if (!product) {
    return <Box sx={{ p: 4 }}><Typography>Product not found.</Typography></Box>;
  }

  const apiBase = import.meta.env.VITE_API_BASE_URL ?? import.meta.env.VITE_API_URL ?? '';

  const stockChip = () => {
    if (product.qtyOnHand === 0) return <Chip label="Out of Stock" color="error" size="small" />;
    if (product.qtyOnHand <= product.reorderPoint) return <Chip label="Low Stock" color="warning" size="small" />;
    return <Chip label="In Stock" color="success" size="small" />;
  };

  return (
    <Box sx={{ p: 3, width: '100%' }}>

      {/* ── Top Bar ─────────────────────────────────────────────────── */}
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', mb: 3 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2 }}>
          <Button
            startIcon={<BackIcon />}
            onClick={() => navigate('/inventory/products')}
            size="small"
            variant="outlined"
            sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5 }}
          >
            Back
          </Button>
          <Divider orientation="vertical" flexItem />
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            <InventoryIcon sx={{ fontSize: 20, color: 'text.secondary' }} />
            <Typography sx={{ fontWeight: 700, fontSize: '0.9rem', color: 'text.secondary' }}>
              Inventory / {t('products') || 'Products'} /
            </Typography>
            <Typography sx={{ fontWeight: 800, fontSize: '0.9rem' }}>
              {product.partNo}
            </Typography>
          </Box>
        </Box>
        {canEdit && <Button
          variant="contained"
          startIcon={<EditIcon />}
          aria-label="Edit Product"
          onClick={() => setEditOpen(true)}
          disableElevation
          sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5 }}
        >
          {t('edit') || 'Edit Product'}
        </Button>}
      </Box>

      {/* ── Product Header Card ─────────────────────────────────────── */}
      <Paper sx={{ p: 3, mb: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <Box sx={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', mb: 2 }}>
          <Box>
            <Typography variant="h5" sx={{ fontWeight: 900, mb: 0.5 }}>
              {product.productName || product.partNo}
            </Typography>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
              <Typography sx={{ fontFamily: 'monospace', fontSize: '0.85rem', color: 'text.secondary', fontWeight: 600, bgcolor: 'action.hover', px: 1, py: 0.25, borderRadius: 1 }}>
                {product.partNo}
              </Typography>
              {product.modelNo && (
                <Typography sx={{ fontFamily: 'monospace', fontSize: '0.85rem', color: 'text.secondary', fontWeight: 600, bgcolor: 'action.hover', px: 1, py: 0.25, borderRadius: 1 }}>
                  {product.modelNo}
                </Typography>
              )}
            </Box>
          </Box>
          <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
            {stockChip()}
            <Chip label={product.isActive ? 'Active' : 'Inactive'} color={product.isActive ? 'success' : 'default'} size="small" variant="outlined" />
            {product.isCatalogItem && <Chip label="Catalog" size="small" variant="outlined" />}
          </Box>
        </Box>

        {/* Description */}
        {product.description && (
          <Typography sx={{ fontSize: '0.875rem', color: 'text.secondary', lineHeight: 1.7, mb: 2 }}>
            {product.description}
          </Typography>
        )}

        <Divider />

        {/* Stats Strip */}
        <Box sx={{ display: 'flex', mt: 0 }}>
          <StatBox label={t('quantity') || 'Qty on Hand'} value={product.qtyOnHand} />
          <StatBox label="Reorder Point" value={product.reorderPoint} />
          <StatBox label={t('uom') || 'UOM'} value={product.uomName} />
          <StatBox label={t('price') || 'Selling Price'} value={product.sellingPrice != null ? `$${product.sellingPrice.toFixed(2)}` : null} />
          <StatBox label="Lead Time" value={product.leadTime ? `${product.leadTime}d` : null} />
        </Box>
      </Paper>

      {/* ── Main Body ───────────────────────────────────────────────── */}
      <Grid container spacing={3}>

        {/* ── LEFT ─────────────────────────────────────────────────── */}
        <Grid size={{ xs: 12, md: 7 }}>
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>

            {/* Classification */}
            <Section title="Classification">
              <InfoRow label={t('categories') || 'Category'} value={product.categoryName} />
              <InfoRow label={t('sub_categories') || 'Sub-Category'} value={product.subCategoryName} />
              <InfoRow label={t('uom') || 'UOM'} value={product.uomName} />
              <InfoRow label="Catalog Item" value={product.isCatalogItem ? 'Yes' : 'No'} />
            </Section>

            {/* Pricing */}
            <Section title="Pricing">
              <InfoRow label="Unit Cost" value={product.unitCost != null ? `${product.unitCost.toFixed(2)} (currency not recorded)` : null} />
              <InfoRow label={t('price') || 'Selling Price'} value={product.sellingPrice != null ? `${product.sellingPrice.toFixed(2)} (currency not recorded)` : null} />
              <InfoRow label="Final Landed Cost" value={product.finalLandedCost != null ? `${product.finalLandedCost.toFixed(2)} (currency not recorded)` : null} />
              <InfoRow label="Final Sales Price" value={product.finalSalesPrice != null ? `${product.finalSalesPrice.toFixed(2)} (currency not recorded)` : null} />
            </Section>

            {/* Logistics */}
            <Section title="Logistics & Supply">
              <InfoRow label={t('warehouse') || 'Warehouse'} value={product.warehouseName} />
              <InfoRow label={t('supplier') || 'Preferred Supplier'} value={product.preferredSupplierName} />
              <InfoRow label="Supplier Email" value={product.preferredSupplierEmail} />
              <InfoRow label="Lead Time" value={product.leadTime ? `${product.leadTime} days` : null} />
              <InfoRow label={t('country') || 'Country of Origin'} value={product.countryOfOrigin} />
              <InfoRow label="HS Code" value={product.hscode} mono />
            </Section>

          </Box>
        </Grid>

        {/* ── RIGHT ────────────────────────────────────────────────── */}
        <Grid size={{ xs: 12, md: 5 }}>
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>

            {/* Stock */}
            <Section title="Stock">
              <InfoRow label={t('quantity') || 'Qty on Hand'} value={
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                  <strong>{product.qtyOnHand}</strong>
                  {stockChip()}
                </Box>
              } />
              <InfoRow label="Reorder Point" value={product.reorderPoint} />
            </Section>

            {/* Physical */}
            <Section title="Physical Dimensions">
              <Box sx={{ display: 'flex', gap: 1, mb: product.dimensions ? 1.5 : 0, flexWrap: 'wrap' }}>
                {[
                  { label: 'Height', val: product.height },
                  { label: 'Width', val: product.width },
                  { label: 'Depth', val: product.depth },
                  { label: 'Weight', val: product.weight ? `${product.weight} kg` : null },
                ].map(({ label, val }) => (
                  <Box key={label} sx={{ flex: 1, minWidth: 70, textAlign: 'center', p: 1.5, bgcolor: 'action.hover', borderRadius: 1.5 }}>
                    <Typography sx={{ fontSize: '0.65rem', color: 'text.secondary', fontWeight: 700, textTransform: 'uppercase' }}>{label}</Typography>
                    <Typography sx={{ fontWeight: 800, fontSize: '1rem' }}>{val ?? '—'}</Typography>
                  </Box>
                ))}
              </Box>
              {product.dimensions && <InfoRow label="Dimensions" value={product.dimensions} />}
            </Section>

            {/* Identifiers */}
            <Section title="Identifiers">
              <InfoRow label="Barcode" value={product.barcode} mono />
              <InfoRow label="QR Code" value={product.qrcode} mono />
            </Section>

            {/* Tracking */}
            <Section title="Tracking">
              <InfoRow label="Batch Tracking" value={
                <Chip label={product.batchTracking ? 'Enabled' : 'Disabled'} color={product.batchTracking ? 'success' : 'default'} size="small" sx={{ height: 20, fontSize: '0.7rem' }} />
              } />
              <InfoRow label="Serial Tracking" value={
                <Chip label={product.serialTracking ? 'Enabled' : 'Disabled'} color={product.serialTracking ? 'success' : 'default'} size="small" sx={{ height: 20, fontSize: '0.7rem' }} />
              } />
              <InfoRow label="Expiration Date" value={product.expirationDate} />
            </Section>

          </Box>
        </Grid>

        {/* ── Images ──────────────────────────────────────────────── */}
        {product.images && product.images.length > 0 && (
          <Grid size={{ xs: 12 }}>
            <Section title="Images">
              <Box sx={{ display: 'flex', gap: 2, flexWrap: 'wrap' }}>
                {product.images.map((img) => (
                  <Box key={img.attachmentId} component="a" href={`${apiBase}${img.location}`} target="_blank" rel="noopener noreferrer"
                    sx={{ width: 130, height: 130, borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider', display: 'block', '&:hover': { borderColor: 'primary.main' }, transition: 'border-color 0.2s' }}>
                    <img src={`${apiBase}${img.location}`} alt={img.fileName} style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
                  </Box>
                ))}
              </Box>
            </Section>
          </Grid>
        )}

        {/* ── Change history (FR-MDM-05) ──────────────────────────── */}
        {/* Placed directly under Pricing's column rather than in a hidden tab: Final Landed Cost
            is rendered two sections above, and the question "who last moved it, and from what"
            has to be answerable on the same screen that shows the number. */}
        <Grid size={{ xs: 12 }}>
          <Section title="Change History">
            <ChangeHistoryPanel
              entityType="Product"
              entityId={Number(id)}
              emptyMessage="No changes have been recorded for this product since audit capture began."
            />
          </Section>
        </Grid>

        {/* ── Attachments ─────────────────────────────────────────── */}
        {product.attachments && product.attachments.length > 0 && (
          <Grid size={{ xs: 12 }}>
            <Section title="Attachments">
              <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap' }}>
                {product.attachments.map((att) => (
                  <Chip key={att.attachmentId} icon={<AttachIcon />} label={att.fileName}
                    component="a" href={`${apiBase}${att.location}`} target="_blank" rel="noopener noreferrer"
                    clickable variant="outlined" sx={{ fontWeight: 600 }} />
                ))}
              </Box>
            </Section>
          </Grid>
        )}

      </Grid>

      <ProductFormDialog open={editOpen && canEdit} onClose={() => setEditOpen(false)} productId={Number(id)} />
    </Box>
  );
};

export default ProductDetailPage;
