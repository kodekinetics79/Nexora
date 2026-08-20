import React, { useState, useMemo, useCallback, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Grid, Stack, Chip,
  Table, TableHead, TableRow, TableCell, TableBody,
  IconButton, TextField, Autocomplete, CircularProgress,
  MenuItem, Dialog, DialogTitle, DialogContent, DialogActions,
  Divider, Tabs, Tab, InputAdornment, List, ListItem, ListItemText,
  ListItemAvatar, Avatar, Checkbox, TablePagination, Alert
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Save as SaveIcon,
  Visibility as ViewIcon,
  Add as AddIcon,
  Email as BatchIcon,
  Close as CloseIcon,
  Search as SearchIcon,
  Language as InternetIcon,
  Dns as DatabaseIcon,
  Business as SupplierIcon,
  Send as SendIcon,
  AutoFixHigh as AutoFixIcon,
  Delete as DeleteIcon,
} from '@mui/icons-material';
import { Breadcrumbs, Link } from '@mui/material';

import leadService from '../../../api/services/leadService';
import type { AcceptedLeadFullResponseDTO, AcceptedLeadItemDTO } from '../../../api/services/leadService';
import productService from '../../../api/services/productService';
import rfqService from '../../../api/services/rfqService';
import type { RfqCreatePayload, RfqResponseDTO } from '../../../api/services/rfqService';
import customerService from '../../../api/services/customerService';
import supplierService, { supplierTierLabel } from '../../../api/services/supplierService';
import {
  DEFAULT_DISPATCH_TIERS,
  DISPATCH_TIER_OPTIONS,
  dispatchTierQueryHint,
  filterSuppliersByTier,
  suppliersHiddenByTier,
  toggleDispatchTier,
  type DispatchTier,
} from '../../../utils/supplierTierFilter';
import { useAuth } from '../../../context/AuthContext';
import { presentableErrorMessage, toPresentableError } from '../../../utils/apiErrors';
import { formatMoney, parseMoneyInput } from '../../../utils/currency';
import { toast } from 'react-hot-toast';
import supplierQuotedItemService from '../../../api/services/supplierQuotedItemService';
import { useTranslation } from 'react-i18next';

// ─── Types ────────────────────────────────────────────────────────────────────

interface ProcessItem extends AcceptedLeadItemDTO {
  selectionSource: 'product' | 'quotedItem';
  productId: number | null;
  supplierQuotedItemId: number | null;
  matchStatus: 'pending' | 'loading' | 'matched' | 'no-match' | 'unavailable' | 'sourced-web';
  finalSalesPrice?: number;
  finalLandedCost?: number;
  qtyOnHand?: number;
  availableToPromise?: number;
  incomingAvailable?: number;
  projectedShortage?: number;
  availabilityStatus?: string;
  leadTimeDays?: number | null;
  expectedAvailableOn?: string | null;
  unitCost?: number | null;
  costCurrencyCode?: string;
  decisionState?: string;
  evidenceReference?: string | null;
  include: boolean;
  preferredSupplierName?: string;
  preferredSupplierEmail?: string;
  selectedName?: string; // To show the name of the selected product/quote
}

/**
 * A lead line whose quantity the source document actually stated, as a number the RFQ payload can
 * carry. `AcceptedLeadItemDTO.quantity` is `number | null` because "the document stated no readable
 * quantity" is a real outcome of extraction, and it is NOT the same thing as zero — a line nobody
 * could read a quantity from must be answered by a person, not silently sourced for none.
 *
 * This is written as a type predicate rather than a plain boolean so the submit path can map over
 * the filtered lines and hand `RfqitemCreatePayload` a proven `number`, instead of asserting one
 * with `!` and hoping the guard above it still runs. The three conditions are the same ones the
 * server enforces, so the message the user sees here is the message they would have got from a
 * round-trip 400.
 */
const hasStatedQuantity = (item: ProcessItem): item is ProcessItem & { quantity: number } =>
  item.quantity !== null && Number.isInteger(item.quantity) && item.quantity >= 1;

// ─── Sub-components ───────────────────────────────────────────────────────────

interface ProductSelectorProps {
  value: number | null;
  onChange: (p: any) => void;
  businessUnitId: number;
}

const ProductSelector: React.FC<ProductSelectorProps> = React.memo(({ value, onChange, businessUnitId }) => {
  const [search, setSearch] = useState('');
  const [inputValue, setInputValue] = useState('');

  // `isError` was never read, so a failed product search rendered as MUI's default "No options"
  // — the same thing the user sees when their search genuinely matches nothing. On the screen
  // that assigns products to RFQ lines, those two readings lead to opposite actions.
  const { data: products = [], isLoading, isError } = useQuery({
    queryKey: ['product-search', search, businessUnitId],
    queryFn: () =>
      productService
        .getAll({ pageNumber: 1, pageSize: 20, search, businessUnitId, isActive: true })
        .then(r => r.items),
    enabled: businessUnitId > 0,
    staleTime: 30_000,
  });

  const selectedOption = useMemo(
    () => products.find(p => p.id === value) ?? null,
    [products, value]
  );

  const handleInputChange = useCallback((_: React.SyntheticEvent, v: string, reason: string) => {
    setInputValue(v);
    if (reason === 'input') setSearch(v);
  }, []);

  const handleChange = useCallback((_: React.SyntheticEvent, v: any) => {
    onChange(v);
  }, [onChange]);

  return (
    <Autocomplete
      size="small"
      options={products}
      loading={isLoading}
      noOptionsText={isError ? 'Product search is unavailable right now — this is not an empty catalogue.' : 'No products match'}
      getOptionLabel={(o) => `${o.productName}${o.partNo ? ` (${o.partNo})` : ''}`}
      value={selectedOption}
      inputValue={inputValue}
      onInputChange={handleInputChange}
      onChange={handleChange}
      isOptionEqualToValue={(option, val) => option.id === val?.id}
      sx={{ '& .MuiInputBase-root': { borderRadius: 1.5, height: 32, fontSize: '0.75rem' } }}
      renderInput={(params) => <TextField {...params} placeholder="Search Product" />}
      renderOption={(props, option) => {
        const { key, ...optionProps } = props;
        return (
          <li key={key} {...optionProps}>
            <Box sx={{ width: '100%' }}>
              <Typography variant="body2" sx={{ fontWeight: 800 }}>{option.productName}</Typography>
              <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
                <Typography variant="caption" color="text.secondary">{option.partNo}</Typography>
                <Chip
                  label={`On hand: ${option.qtyOnHand ?? 0}`}
                  size="small"
                  color={(option.qtyOnHand ?? 0) > 0 ? 'success' : 'error'}
                  sx={{ height: 14, fontSize: '0.55rem', fontWeight: 900 }}
                />
              </Box>
            </Box>
          </li>
        );
      }}
    />
  );
});

interface QuoteSelectorProps {
  value: number | null;
  onChange: (q: any) => void;
  businessUnitId: number;
}

const QuoteSelector: React.FC<QuoteSelectorProps> = React.memo(({ value, onChange, businessUnitId }) => {
  const [inputValue, setInputValue] = useState('');

  // See ProductSelector: an empty option list must not be how a failed request looks.
  const { data: quotes = [], isLoading, isError } = useQuery({
    queryKey: ['quoted-items', businessUnitId],
    queryFn: () => supplierQuotedItemService.getAll(businessUnitId),
    enabled: businessUnitId > 0,
    staleTime: 60_000,
  });

  const selectedOption = useMemo(
    () => quotes.find(q => q.id === value) ?? null,
    [quotes, value]
  );

  const handleInputChange = useCallback((_: React.SyntheticEvent, v: string) => {
    setInputValue(v);
  }, []);

  const handleChange = useCallback((_: React.SyntheticEvent, v: any) => {
    onChange(v);
  }, [onChange]);

  return (
    <Autocomplete
      size="small"
      options={quotes}
      loading={isLoading}
      noOptionsText={isError ? 'Supplier quote lookup is unavailable right now — this is not an empty list.' : 'No supplier quotes match'}
      getOptionLabel={(o) => `${o.itemName} - ${o.supplierName}`}
      value={selectedOption}
      inputValue={inputValue}
      onInputChange={handleInputChange}
      onChange={handleChange}
      isOptionEqualToValue={(option, val) => option.id === val?.id}
      sx={{ '& .MuiInputBase-root': { borderRadius: 1.5, height: 32, fontSize: '0.75rem' } }}
      renderInput={(params) => <TextField {...params} placeholder="Search Quote" />}
      renderOption={(props, option) => {
        const { key, ...optionProps } = props;
        return (
          <li key={key} {...optionProps}>
            <Box sx={{ width: '100%' }}>
              <Typography variant="body2" sx={{ fontWeight: 800 }}>{option.itemName}</Typography>
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="caption" color="text.secondary">{option.supplierName}</Typography>
                {/* The offer carries its own currency. A hardcoded $ is the exact defect
                    utils/currency.ts was written to stop; where the record states no code,
                    formatMoney returns a bare grouped number rather than inventing a symbol. */}
                <Typography variant="caption" sx={{ fontWeight: 900, color: 'primary.main', fontVariantNumeric: 'tabular-nums' }}>
                  {formatMoney(option.unitPrice, option.currencyName)}
                </Typography>
              </Box>
            </Box>
          </li>
        );
      }}
    />
  );
});

// ─── Item Row ─────────────────────────────────────────────────────────────────

interface ItemRowProps {
  item: ProcessItem;
  index: number;
  onUpdate: (index: number, fields: Partial<ProcessItem>) => void;
  onRemove: (index: number) => void;
  onViewDetails: (item: ProcessItem) => void;
  onToggleSelect: (index: number) => void;
  businessUnitId: number;
}

const ItemRow: React.FC<ItemRowProps> = React.memo(({ item, index, onUpdate, onRemove, onViewDetails, onToggleSelect, businessUnitId }) => {
  const [isEditing, setIsEditing] = useState(false);
  const [webSearchOpen, setWebSearchOpen] = useState(false);

  const handleWebSupplierSelect = useCallback((supp: any) => {
    onUpdate(index, {
      preferredSupplierName: supp.name,
      preferredSupplierEmail: supp.contactEmail,
      matchStatus: 'sourced-web'
    });
  }, [index, onUpdate]);

  const handleSourceChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    onUpdate(index, {
      selectionSource: e.target.value as 'product' | 'quotedItem',
      productId: null,
      supplierQuotedItemId: null,
      selectedName: undefined
    });
  }, [index, onUpdate]);

  const handleProductChange = useCallback((p: any) => {
    onUpdate(index, {
      matchStatus: p ? 'pending' : 'no-match',
      productId: p?.id ?? null,
      unitPrice: p?.finalSalesPrice ?? p?.sellingPrice ?? 0,
      qtyOnHand: p?.qtyOnHand ?? 0,
      availableToPromise: p?.availableToPromise,
      projectedShortage: undefined,
      evidenceReference: undefined,
      selectedName: p ? `${p.productName} (${p.partNo})` : undefined
    });
    setIsEditing(false);
  }, [index, onUpdate]);

  const handleQuoteChange = useCallback((q: any) => {
    onUpdate(index, {
      supplierQuotedItemId: q?.id ?? null,
      unitPrice: q?.unitPrice ?? 0,
      selectedName: q ? `${q.itemName} - ${q.supplierName}` : undefined
    });
    setIsEditing(false);
  }, [index, onUpdate]);

  const handleQtyChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    onUpdate(index, { quantity: Number(e.target.value) });
  }, [index, onUpdate]);

  // The field no longer embeds a currency symbol in its editable value, so parsing no longer
  // depends on stripping one. `parseMoneyInput` still tolerates a pasted symbol or grouped
  // figure and returns null rather than NaN, so an unparseable entry leaves the previous price
  // untouched instead of writing a corrupt number to the line.
  const handlePriceChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const parsed = parseMoneyInput(e.target.value);
    if (parsed === null) return;
    onUpdate(index, { unitPrice: parsed });
  }, [index, onUpdate]);

  const handleRemove = useCallback(() => onRemove(index), [index, onRemove]);
  const handleViewDetails = useCallback(() => onViewDetails(item), [item, onViewDetails]);

  const handleSmartMatch = useCallback(async () => {
    onUpdate(index, { matchStatus: 'loading' });
    try {
      const res = await productService.matchProduct({
        name: item.productShortName,
        description: item.productShortDescription || item.productShortName,
        partNo: item.manufacturerPartNumber,
        manufacturer: item.manufacturerName,
        businessUnitId: businessUnitId,
        // Not stated stays not stated: sending 0 would ask inventory to price a line for none.
        quantity: item.quantity ?? undefined,
      });
      if (res.hasExactMatch && res.exactMatch) {
        const exact = res.exactMatch;
        onUpdate(index, {
          matchStatus: 'matched',
          productId: exact.productId ?? exact.id ?? null,
          unitPrice: exact.finalSalesPrice ?? exact.sellingPrice ?? 0,
          qtyOnHand: exact.qtyOnHand ?? 0,
          availableToPromise: exact.availableToPromise ?? 0,
          incomingAvailable: exact.incomingAvailable ?? 0,
          // A shortage cannot be derived from a quantity nobody could read. undefined renders as
          // an unknown; 0 would render as "none short", which is a claim about stock we cannot make.
          projectedShortage: exact.projectedShortage
            ?? (item.quantity === null ? undefined : Math.max(0, item.quantity - (exact.availableToPromise ?? 0))),
          availabilityStatus: exact.availabilityStatus,
          leadTimeDays: exact.leadTimeDays,
          expectedAvailableOn: exact.expectedAvailableOn,
          unitCost: exact.unitCost,
          costCurrencyCode: exact.costCurrencyCode,
          decisionState: exact.decisionState,
          evidenceReference: exact.evidenceReference,
          selectedName: `${exact.productName} (${exact.partNo})`
        });
      } else {
        onUpdate(index, { matchStatus: 'no-match' });
      }
    } catch {
      onUpdate(index, { matchStatus: 'unavailable', productId: null, supplierQuotedItemId: null, selectedName: undefined });
    }
  }, [item, index, onUpdate, businessUnitId]);

  return (
    <TableRow sx={{ '& td': { borderBottom: '1px solid #f0f0f0' }, bgcolor: item.include ? 'transparent' : '#fafafa' }}>
      <TableCell padding="checkbox">
        <Checkbox
          size="small"
          checked={!!item.include}
          onChange={() => onToggleSelect(index)}
          slotProps={{ input: { 'aria-label': `${item.include ? 'Exclude' : 'Include'} ${item.productShortName}` } }}
        />
      </TableCell>
      {/* Requested Item */}
      <TableCell sx={{ py: 2 }}>
        <Box>
          <Typography sx={{ fontWeight: 800, fontSize: '0.75rem', color: '#1a237e', textTransform: 'uppercase' }}>
            {item.productShortName}
          </Typography>
          
          {/* Refined Layout: Part Number and Manufacturer */}
          <Box sx={{ display: 'flex', gap: 1, mt: 0.5, mb: 1 }}>
            <Box sx={{ bgcolor: '#f5f5f5', px: 1, py: 0.25, borderRadius: 1, border: '1px solid #eee' }}>
              <Typography variant="caption" sx={{ color: '#666', fontWeight: 700, fontSize: '0.6rem' }}>PN: </Typography>
              <Typography variant="caption" sx={{ color: '#333', fontWeight: 800, fontSize: '0.65rem' }}>{item.manufacturerPartNumber || 'N/A'}</Typography>
            </Box>
            <Box sx={{ bgcolor: '#f5f5f5', px: 1, py: 0.25, borderRadius: 1, border: '1px solid #eee' }}>
              <Typography variant="caption" sx={{ color: '#666', fontWeight: 700, fontSize: '0.6rem' }}>MFG: </Typography>
              <Typography variant="caption" sx={{ color: '#333', fontWeight: 800, fontSize: '0.65rem' }}>{item.manufacturerName || 'N/A'}</Typography>
            </Box>
          </Box>

          <Box sx={{ display: 'flex', gap: 1, my: 0.5 }}>
            {/* An "AI: N%" chip used to lead this row. It rendered a score the
                platform has never measured, so it is not shown. */}
            {item.matchStatus === 'matched' && (
              <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
                <Chip label="System Match" size="small" sx={{ height: 16, fontSize: '0.55rem', fontWeight: 900, bgcolor: '#e3f2fd', color: '#1976d2', borderRadius: 1 }} />
                <Chip label={`ATP ${item.availableToPromise ?? 0}`} size="small" color={(item.projectedShortage ?? 0) > 0 ? 'warning' : 'success'} sx={{ height: 18, fontSize: '0.6rem', fontWeight: 800 }} />
                {(item.projectedShortage ?? 0) > 0 && <Chip label={`Short ${item.projectedShortage}`} size="small" color="warning" sx={{ height: 18, fontSize: '0.6rem', fontWeight: 800 }} />}
              </Stack>
            )}
            {item.matchStatus === 'no-match' && (
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <Chip
                  label="Sourcing required"
                  size="small"
                  sx={{ height: 16, fontSize: '0.55rem', fontWeight: 900, bgcolor: '#fff3e0', color: '#ef6c00', borderRadius: 1 }}
                />
                <Button
                  size="small"
                  variant="outlined"
                  onClick={() => setWebSearchOpen(true)}
                  sx={{ height: 20, fontSize: '0.62rem', textTransform: 'none', fontWeight: 800, p: 0, px: 1, minWidth: 40, borderRadius: 1 }}
                >
                  Search Internet
                </Button>
                <SearchWebSupplierDialog
                  open={webSearchOpen}
                  onClose={() => setWebSearchOpen(false)}
                  query={item.productShortName || ''}
                  onSelectSupplier={handleWebSupplierSelect}
                />
              </Stack>
            )}
            {item.matchStatus === 'sourced-web' && (
              <Chip
                label={`Sourced: ${item.preferredSupplierName || 'Web'}`}
                size="small"
                sx={{ height: 18, fontSize: '0.62rem', fontWeight: 900, bgcolor: '#e1f5fe', color: '#0288d1', borderRadius: 1 }}
              />
            )}
            {item.matchStatus === 'pending' && (
              <Button
                size="small"
                variant="outlined"
                onClick={handleSmartMatch}
                sx={{ height: 16, fontSize: '0.6rem', textTransform: 'none', fontWeight: 800, p: 0, px: 1, minWidth: 40, borderRadius: 1 }}
              >
                Smart Match
              </Button>
            )}
            {item.matchStatus === 'loading' && (
              <CircularProgress size={10} thickness={6} />
            )}
            {item.matchStatus === 'unavailable' && (
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <Chip label="Inventory check unavailable" size="small" color="error" variant="outlined" sx={{ height: 20, fontSize: '0.6rem', fontWeight: 800 }} />
                <Button size="small" variant="outlined" color="error" onClick={handleSmartMatch} aria-label={`Retry inventory check for ${item.productShortName}`} sx={{ height: 22, fontSize: '0.62rem', textTransform: 'none' }}>Retry</Button>
              </Stack>
            )}
          </Box>
          <Typography variant="caption" sx={{ color: '#888', fontWeight: 600, display: 'block' }}>
            Qty: {item.quantity}
          </Typography>
          {item.matchStatus === 'matched' && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
              {item.availabilityStatus || 'Availability resolved'} · Lead time {item.leadTimeDays != null ? `${item.leadTimeDays}d` : 'not established'} · Expected {item.expectedAvailableOn ? new Date(item.expectedAvailableOn).toLocaleDateString() : 'not established'} · Cost {item.unitCost != null ? `${item.costCurrencyCode || 'currency unverified'} ${item.unitCost.toFixed(2)}` : 'not recorded'} · Evidence {item.evidenceReference || 'not recorded'}
            </Typography>
          )}
          <Link
            underline="hover"
            onClick={handleViewDetails}
            sx={{ fontSize: '0.65rem', fontWeight: 800, cursor: 'pointer', mt: 0.5, display: 'inline-block' }}
          >
            View Details
          </Link>
        </Box>
      </TableCell>

      {/* Selector */}
      <TableCell>
        <Stack spacing={1} sx={{ width: { xs: 240, sm: 350 }, maxWidth: '100%' }}>
          <TextField
            select
            size="small"
            value={item.selectionSource}
            onChange={handleSourceChange}
            sx={{ width: 120, '& .MuiInputBase-root': { fontSize: '0.75rem', fontWeight: 700, borderRadius: 1.5, height: 32 } }}
          >
            <MenuItem value="product" sx={{ fontSize: '0.75rem' }}>Inventory ..</MenuItem>
            <MenuItem value="quotedItem" sx={{ fontSize: '0.75rem' }}>Quote ..</MenuItem>
          </TextField>

          {isEditing ? (
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
              <Box sx={{ flex: 1 }}>
                {item.selectionSource === 'product' ? (
                  <ProductSelector
                    value={item.productId}
                    onChange={handleProductChange}
                    businessUnitId={businessUnitId}
                  />
                ) : (
                  <QuoteSelector
                    value={item.supplierQuotedItemId}
                    onChange={handleQuoteChange}
                    businessUnitId={businessUnitId}
                  />
                )}
              </Box>
              <Button
                size="small"
                variant="text"
                onClick={() => setIsEditing(false)}
                sx={{ minWidth: 40, height: 32, fontSize: '0.65rem', textTransform: 'none', fontWeight: 800, color: '#666' }}
              >
                Cancel
              </Button>
            </Stack>
          ) : (
            <Box
              role="button"
              tabIndex={0}
              aria-label={`Select ${item.selectionSource === 'product' ? 'product' : 'supplier quote'} for ${item.productShortName}`}
              onClick={() => setIsEditing(true)}
              onKeyDown={(event) => {
                if (event.key === 'Enter' || event.key === ' ') {
                  event.preventDefault();
                  setIsEditing(true);
                }
              }}
              sx={{
                width: '100%',
                minHeight: 32,
                p: 1,
                border: '1px solid #ddd',
                borderRadius: 1.5,
                bgcolor: 'white',
                cursor: 'pointer',
                display: 'flex',
                alignItems: 'center',
                '&:hover': { borderColor: '#1976d2', bgcolor: '#fafafa' }
              }}
            >
              <Typography sx={{ fontSize: '0.75rem', color: item.selectedName ? 'text.primary' : 'text.secondary', fontWeight: 600 }}>
                {item.selectedName || (item.selectionSource === 'product' ? 'Select Product...' : 'Select Quote...')}
              </Typography>
            </Box>
          )}
        </Stack>
      </TableCell>

      {/* Qty — the backend requires a whole-number quantity >= 1 on every submitted line */}
      <TableCell align="center">
        <TextField
          size="small"
          type="number"
          value={item.quantity ?? ''}
          onChange={handleQtyChange}
          error={item.quantity === null || !Number.isInteger(item.quantity) || item.quantity < 1}
          helperText={item.quantity === null ? 'Not stated' : undefined}
          slotProps={{ htmlInput: { min: 1, step: 1, 'aria-label': `Quantity for ${item.productShortName || 'item'}` } }}
          sx={{ width: 80, '& .MuiInputBase-root': { height: 32, fontSize: '0.75rem', fontWeight: 700 } }}
        />
      </TableCell>

      {/* Price */}
      <TableCell align="center">
        {/*
          The currency is shown as a read-only adornment sourced from the line, never baked into
          the editable value. When the line carries no currency the number stands alone rather
          than borrowing a symbol.
        */}
        <TextField
          size="small"
          type="number"
          value={item.unitPrice ?? 0}
          onChange={handlePriceChange}
          slotProps={{
            input: item.currency
              ? { startAdornment: <InputAdornment position="start"><Typography variant="caption" sx={{ fontWeight: 700 }}>{item.currency}</Typography></InputAdornment> }
              : undefined,
            htmlInput: { min: 0, step: 0.01, 'aria-label': `Unit price for ${item.productShortName || 'item'}${item.currency ? ` in ${item.currency}` : ''}` },
          }}
          sx={{ width: 130, '& .MuiInputBase-root': { height: 32, fontSize: '0.75rem', fontWeight: 700 } }}
        />
      </TableCell>

      {/* Action */}
      <TableCell align="center">
        <IconButton size="small" onClick={handleRemove} color="error" aria-label={`Remove ${item.productShortName}`}>
          <DeleteIcon sx={{ fontSize: 18 }} />
        </IconButton>
      </TableCell>
    </TableRow>
  );
});

const SearchWebSupplierDialog: React.FC<{
  open: boolean;
  onClose: () => void;
  query: string;
  onSelectSupplier: (supp: any) => void;
}> = ({ open, onClose, query, onSelectSupplier }) => {
  const [results, setResults] = useState<any[]>([]);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (open && query) {
      setLoading(true);
      supplierService.searchWebSuppliers(query)
        .then(res => {
          setResults(res || []);
        })
        .catch(() => {
          setResults([]);
        })
        .finally(() => {
          setLoading(false);
        });
    }
  }, [open, query]);

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle sx={{ fontWeight: 800, p: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <Typography sx={{ fontSize: '1rem', fontWeight: 800, color: '#1a237e' }}>
          Search Internet for Supplier
        </Typography>
        <IconButton onClick={onClose} size="small"><CloseIcon /></IconButton>
      </DialogTitle>
      <DialogContent dividers>
        <Typography variant="caption" sx={{ color: '#666', mb: 2, display: 'block' }}>
          Searching for: <strong>{query}</strong>
        </Typography>
        {loading ? (
          <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}>
            <CircularProgress size={30} />
          </Box>
        ) : results.length === 0 ? (
          <Typography sx={{ py: 2, fontSize: '0.75rem', color: '#888' }}>No suppliers found on the internet.</Typography>
        ) : (
          <List dense sx={{ p: 0 }}>
            {results.map((supp, i) => (
              <React.Fragment key={supp.id || i}>
                {i > 0 && <Divider />}
                <ListItem
                  secondaryAction={
                    <Button
                      size="small"
                      variant="contained"
                      onClick={() => {
                        onSelectSupplier(supp);
                        onClose();
                      }}
                      sx={{ fontSize: '0.65rem', fontWeight: 800, textTransform: 'none' }}
                    >
                      Select
                    </Button>
                  }
                  sx={{ py: 1, px: 0 }}
                >
                  <ListItemAvatar>
                    <Avatar sx={{ bgcolor: '#e8f5e9', color: '#2e7d32' }}><SupplierIcon /></Avatar>
                  </ListItemAvatar>
                  <ListItemText
                    primary={<Typography sx={{ fontSize: '0.75rem', fontWeight: 700, color: '#333' }}>{supp.name}</Typography>}
                    secondary={
                      <Typography sx={{ fontSize: '0.65rem', color: '#666' }}>
                        Email: {supp.contactEmail} • Location: {supp.city}, {supp.countryName}
                      </Typography>
                    }
                  />
                </ListItem>
              </React.Fragment>
            ))}
          </List>
        )}
      </DialogContent>
    </Dialog>
  );
};

// ─── Supplier Search & Quote Dialogs ──────────────────────────────────────────

const SupplierQuoteRequestDialog: React.FC<{
  open: boolean;
  onClose: () => void;
  supplier: any;
  items: ProcessItem[];
  rfqNo: string;
}> = ({ open, onClose, supplier, items, rfqNo }) => {
  const [emailData, setEmailData] = useState({
    to: '',
    cc: '',
    subject: '',
    body: ''
  });

  useEffect(() => {
    if (supplier && items.length > 0) {
      const subject = `Quote Request - RFQ #${rfqNo}`;

      const itemRows = items.map((item, idx) =>
        `| ${idx + 1} | ${item.manufacturerPartNumber || 'N/A'} | ${item.manufacturerName || 'N/A'} | ${item.productShortDescription || item.productShortName} | ${item.quantity} | EA |`
      ).join('\n');

      const body = `Dear ${supplier.name},

We would like to request a quotation for the following items:

| # | Part Number | Manufacturer | Description | Quantity | UOM |
|---|--------------|--------------|-------------|----------|-----|
${itemRows}

Please provide your best pricing and lead time for the items listed above.

Thank you for your assistance.

Best regards`;

      setEmailData({
        to: supplier.email || '',
        cc: '',
        subject,
        body
      });
    }
  }, [supplier, items, rfqNo]);

  const handleContinue = () => {
    toast('Create the draft RFQ, then dispatch this request from the governed sourcing workbench.');
    onClose();
  };

  return (
    <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth>
      <DialogTitle sx={{ fontWeight: 800, p: 2 }}>
        Compose Quote Request - {supplier?.name}
        <IconButton onClick={onClose} size="small" sx={{ position: 'absolute', right: 8, top: 8 }}><CloseIcon /></IconButton>
      </DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Box>
            <Typography variant="caption" sx={{ fontWeight: 800, color: '#666' }}>Request Items ({items.length})</Typography>
            <Table size="small" sx={{ border: '1px solid #eee', mt: 1 }}>
              <TableHead sx={{ bgcolor: '#fafafa' }}>
                <TableRow>
                  <TableCell sx={{ fontSize: '0.7rem', fontWeight: 800 }}>Item</TableCell>
                  <TableCell sx={{ fontSize: '0.7rem', fontWeight: 800 }}>Description</TableCell>
                  <TableCell sx={{ fontSize: '0.7rem', fontWeight: 800 }}>Qty</TableCell>
                  <TableCell sx={{ fontSize: '0.7rem', fontWeight: 800 }}>Supplier</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {items.map((item, idx) => (
                  <TableRow key={idx}>
                    <TableCell sx={{ fontSize: '0.75rem' }}>{item.manufacturerPartNumber}</TableCell>
                    <TableCell sx={{ fontSize: '0.75rem' }}>{item.productShortDescription || item.productShortName}</TableCell>
                    <TableCell sx={{ fontSize: '0.75rem' }}>{item.quantity}</TableCell>
                    <TableCell sx={{ fontSize: '0.75rem', fontWeight: 700 }}>{item.preferredSupplierName || supplier?.name || 'No Supplier'}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Box>

          <TextField label="To" fullWidth size="small" value={emailData.to} onChange={e => setEmailData({ ...emailData, to: e.target.value })} />
          <TextField label="CC" fullWidth size="small" value={emailData.cc} onChange={e => setEmailData({ ...emailData, cc: e.target.value })} />
          <TextField label="Subject" fullWidth size="small" value={emailData.subject} onChange={e => setEmailData({ ...emailData, subject: e.target.value })} />

          <Box>
            <Typography variant="caption" sx={{ fontWeight: 800, color: '#666', mb: 1, display: 'block' }}>Email Body</Typography>
            <TextField
              fullWidth
              multiline
              rows={12}
              value={emailData.body}
              onChange={e => setEmailData({ ...emailData, body: e.target.value })}
              sx={{ '& .MuiInputBase-root': { fontSize: '0.85rem', fontFamily: 'monospace' } }}
            />
          </Box>
        </Stack>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={onClose} variant="outlined" sx={{ borderRadius: 1.5, textTransform: 'none', fontWeight: 700 }}>Cancel</Button>
        <Button 
          variant="contained" 
          startIcon={<SendIcon />} 
          onClick={handleContinue}
          sx={{ borderRadius: 1.5, textTransform: 'none', fontWeight: 700, px: 3 }}
        >
          Continue with Draft RFQ
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export const ItemDetailsDialog: React.FC<{
  item: ProcessItem | null;
  open: boolean;
  onClose: () => void;
  rfqNo: string;
}> = ({ item, open, onClose, rfqNo }) => {
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;

  const [searchTab, setSearchTab] = useState(0); // 0 = Internal, 1 = Internet
  const [matchingResult, setMatchingResult] = useState<any>(null);
  const [isLoadingMatch, setIsLoadingMatch] = useState(false);
  const [showSupplierSearch, setShowSupplierSearch] = useState(false);
  const [selectedSupplier, setSelectedSupplier] = useState<any>(null);

  const [searchQuery, setSearchQuery] = useState('');
  const [suppliers, setSuppliers] = useState<any[]>([]);
  const [loadingSuppliers, setLoadingSuppliers] = useState(false);
  /**
   * FR-QTM-01. Which tiers the buyer is looking at right now. Tier 1, Tier 2 and the suppliers
   * nobody has tiered yet start selected; Tier 3 is one visible button away and the row below the
   * buttons says so. This shortens a list — it never decides who may be sent an RFQ.
   */
  const [selectedTiers, setSelectedTiers] = useState<DispatchTier[]>(DEFAULT_DISPATCH_TIERS);

  useEffect(() => {
    if (item && searchQuery === '') {
      setSearchQuery(item.manufacturerName || '');
    }
  }, [item]);

  useEffect(() => {
    let active = true;
    if (open && showSupplierSearch && item) {
      setLoadingSuppliers(true);
      if (searchTab === 0) {
        supplierService.searchSuppliers(searchQuery, '', businessUnitId,
          dispatchTierQueryHint(selectedTiers))
          .then(res => {
            if (active) setSuppliers(res || []);
          })
          .catch(() => {
            if (active) setSuppliers([]);
          })
          .finally(() => {
            if (active) setLoadingSuppliers(false);
          });
      } else {
        supplierService.searchWebSuppliers(searchQuery || item.productShortName || '')
          .then(res => {
            if (active) setSuppliers(res || []);
          })
          .catch(() => {
            if (active) setSuppliers([]);
          })
          .finally(() => {
            if (active) setLoadingSuppliers(false);
          });
      }
    }
    return () => { active = false; };
  }, [open, showSupplierSearch, searchTab, searchQuery, item, selectedTiers]);

  useEffect(() => {
    if (open && item && !showSupplierSearch) {
      handleMatchProduct();
    }
  }, [open, item, showSupplierSearch]);

  const handleMatchProduct = async () => {
    if (!item) return;
    setIsLoadingMatch(true);
    try {
      const res = await productService.matchProduct({
        name: item.productShortName,
        partNo: item.manufacturerPartNumber,
        manufacturer: item.manufacturerName,
        quantity: item.quantity ?? undefined,
      });
      setMatchingResult(res);
    } catch (e) {
      console.error("Match failed", e);
    } finally {
      setIsLoadingMatch(false);
    }
  };

  // The tier only narrows suppliers held in our own master data. Internet results carry no tier
  // set by anyone here, so the buttons do not apply to them and nothing is hidden on that tab.
  const visibleSuppliers = searchTab === 0
    ? filterSuppliersByTier(suppliers, selectedTiers)
    : suppliers;
  const hiddenByTier = searchTab === 0
    ? suppliersHiddenByTier(suppliers, selectedTiers)
    : 0;
  const showEveryTier = () =>
    setSelectedTiers(DISPATCH_TIER_OPTIONS.map((option) => option.value));

  if (!item) return null;

  return (
    <>
      <Dialog open={open} onClose={onClose} maxWidth={showSupplierSearch ? "sm" : "md"} fullWidth>
        <DialogTitle sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', p: 2 }}>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            {showSupplierSearch ? <SupplierIcon sx={{ color: '#1976d2' }} /> : <ViewIcon sx={{ color: '#1a237e' }} />}
            <Typography variant="h6" sx={{ fontWeight: 800, fontSize: '1rem' }}>
              {showSupplierSearch ? 'Find Supplier' : 'Item Details & Availability'}
            </Typography>
          </Box>
          <IconButton onClick={onClose} size="small"><CloseIcon /></IconButton>
        </DialogTitle>

        <DialogContent dividers sx={{ p: 0 }}>
          {showSupplierSearch ? (
            <Box>
              <Tabs value={searchTab} onChange={(_, v) => setSearchTab(v)} sx={{ borderBottom: 1, borderColor: 'divider', px: 2 }}>
                <Tab icon={<DatabaseIcon sx={{ fontSize: 18 }} />} label="Internal Database" sx={{ textTransform: 'none', fontWeight: 700, fontSize: '0.8rem' }} />
                <Tab icon={<InternetIcon sx={{ fontSize: 18 }} />} label="Internet Search" sx={{ textTransform: 'none', fontWeight: 700, fontSize: '0.8rem' }} />
              </Tabs>
              <Box sx={{ p: 2 }}>
                <TextField
                  fullWidth
                  size="small"
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  placeholder="Search Suppliers..."
                  slotProps={{
                    input: {
                      endAdornment: (
                        <InputAdornment position="end">
                          <Button variant="contained" size="small" sx={{ minWidth: 40, p: 0.5, borderRadius: 1 }}>
                            <SearchIcon fontSize="small" />
                          </Button>
                        </InputAdornment>
                      )
                    }
                  }}
                />

                {/* FR-QTM-01. The tier a person set on the supplier record, used to shorten this
                    list. Tier 3 is where spot suppliers sit — the people a trader calls for an
                    obsolete or single-source part — so it is always one click from here and the
                    line underneath says what is currently being left out. Nothing on this row
                    stops a supplier being sent an RFQ. */}
                {searchTab === 0 && (
                  <Box sx={{ mt: 2 }}>
                    <Box sx={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 0.75 }}>
                      <Typography variant="caption" sx={{ fontWeight: 800, color: '#555', mr: 0.5 }}>
                        Supplier tier
                      </Typography>
                      {DISPATCH_TIER_OPTIONS.map((option) => {
                        const included = selectedTiers.includes(option.value);
                        return (
                          <Chip
                            key={option.value}
                            size="small"
                            label={option.label}
                            aria-pressed={included}
                            onClick={() =>
                              setSelectedTiers((current) => toggleDispatchTier(current, option.value))
                            }
                            color={included ? 'primary' : 'default'}
                            variant={included ? 'filled' : 'outlined'}
                            sx={{ fontWeight: 700, fontSize: '0.7rem', borderRadius: 1 }}
                          />
                        );
                      })}
                    </Box>
                    <Typography variant="caption" sx={{ display: 'block', mt: 0.75, color: '#888' }}>
                      {selectedTiers.length === 0
                        ? 'Showing every supplier — no tier is being left out.'
                        : `Showing ${DISPATCH_TIER_OPTIONS.filter((option) => selectedTiers.includes(option.value)).map((option) => option.label).join(', ')}. Turn on a tier to include it — a supplier's tier never stops you sending them an RFQ.`}
                    </Typography>
                    {hiddenByTier > 0 && (
                      <Alert
                        severity="info"
                        sx={{ mt: 1, py: 0, fontSize: '0.75rem' }}
                        action={
                          <Button size="small" onClick={showEveryTier} sx={{ textTransform: 'none', fontWeight: 800 }}>
                            Show every tier
                          </Button>
                        }
                      >
                        {hiddenByTier === 1
                          ? '1 supplier matches this search but is in a tier you have turned off.'
                          : `${hiddenByTier} suppliers match this search but are in tiers you have turned off.`}
                      </Alert>
                    )}
                  </Box>
                )}

                {loadingSuppliers ? (
                  <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}>
                    <CircularProgress size={24} />
                  </Box>
                ) : visibleSuppliers.length === 0 ? (
                  <Typography variant="body2" sx={{ color: '#888', py: 3, textAlign: 'center' }}>
                    {searchTab !== 0
                      ? 'No results found on the internet.'
                      : hiddenByTier > 0
                        ? 'Every supplier matching this search is in a tier you have turned off.'
                        : 'No results found in internal database.'}
                  </Typography>
                ) : (
                  <List sx={{ mt: 2 }}>
                    {visibleSuppliers.map((s, idx) => (
                      <ListItem
                        key={s.id || idx}
                        sx={{ border: '1px solid #f0f0f0', borderRadius: 2, mb: 1, p: 2, alignItems: 'flex-start' }}
                        secondaryAction={
                          <Button
                            variant="contained"
                            size="small"
                            startIcon={<SupplierIcon fontSize="small" />}
                            onClick={() => setSelectedSupplier(s)}
                            sx={{ textTransform: 'none', fontWeight: 800, borderRadius: 1.5, fontSize: '0.75rem' }}
                          >
                            Select & Quote
                          </Button>
                        }
                      >
                        <ListItemAvatar sx={{ mt: 0.5 }}>
                          <Avatar sx={{ bgcolor: searchTab === 0 ? '#ede7f6' : '#e3f2fd', color: searchTab === 0 ? '#512da8' : '#1976d2' }}>
                            {searchTab === 0 ? <DatabaseIcon fontSize="small" /> : <InternetIcon fontSize="small" />}
                          </Avatar>
                        </ListItemAvatar>
                        <ListItemText
                          primary={<Typography variant="subtitle2" sx={{ fontWeight: 800 }}>{s.name}</Typography>}
                          secondary={
                            <Box sx={{ mt: 0.5 }}>
                              <Typography variant="caption" sx={{ display: 'block', color: '#888', fontWeight: 600 }}>{s.contactEmail || s.email || 'No email'}</Typography>
                              <Box sx={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: 1, mt: 0.5 }}>
                                <Typography variant="caption" sx={{ color: '#888' }}>{s.cityName || s.city || 'No Location'}</Typography>
                                <Chip label={searchTab === 0 ? "Internal DB" : "External Source"} size="small" sx={{ height: 16, fontSize: '0.5rem', fontWeight: 900, borderRadius: 1, bgcolor: searchTab === 0 ? '#ede7f6' : '#e3f2fd', color: searchTab === 0 ? '#512da8' : '#1976d2' }} />
                                {/* The tier a person set, said in full. "Not classified" is a real
                                    answer here and reads as one — it is not Tier 3 and must never
                                    be shown as one. */}
                                {searchTab === 0 && (
                                  <Chip label={supplierTierLabel(s.tier)} size="small" variant="outlined" sx={{ height: 16, fontSize: '0.5rem', fontWeight: 900, borderRadius: 1 }} />
                                )}
                              </Box>
                            </Box>
                          }
                        />
                      </ListItem>
                    ))}
                  </List>
                )}
              </Box>
            </Box>
          ) : (
            <>
              <Box sx={{ p: 2 }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 900, mb: 1.5, color: '#444' }}>Requested Item</Typography>
                <Paper variant="outlined" sx={{ borderRadius: 1 }}>
                  <Grid container>
                    <Grid size={{ xs: 3 }} sx={{ p: 1.5, bgcolor: '#fafafa', borderRight: '1px solid #eee', borderBottom: '1px solid #eee' }}>
                      <Typography variant="caption" sx={{ color: '#888', fontWeight: 600 }}>Part Number</Typography>
                    </Grid>
                    <Grid size={{ xs: 3 }} sx={{ p: 1.5, borderRight: '1px solid #eee', borderBottom: '1px solid #eee' }}>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>{item.manufacturerPartNumber || 'N/A'}</Typography>
                    </Grid>
                    <Grid size={{ xs: 3 }} sx={{ p: 1.5, bgcolor: '#fafafa', borderRight: '1px solid #eee', borderBottom: '1px solid #eee' }}>
                      <Typography variant="caption" sx={{ color: '#888', fontWeight: 600 }}>Manufacturer</Typography>
                    </Grid>
                    <Grid size={{ xs: 3 }} sx={{ p: 1.5, borderBottom: '1px solid #eee' }}>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>{item.manufacturerName || 'N/A'}</Typography>
                    </Grid>
                    <Grid size={{ xs: 3 }} sx={{ p: 1.5, bgcolor: '#fafafa', borderRight: '1px solid #eee', borderBottom: '1px solid #eee' }}>
                      <Typography variant="caption" sx={{ color: '#888', fontWeight: 600 }}>Description</Typography>
                    </Grid>
                    <Grid size={{ xs: 9 }} sx={{ p: 1.5, borderBottom: '1px solid #eee' }}>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>{item.productShortDescription || item.productShortName}</Typography>
                    </Grid>
                    <Grid size={{ xs: 3 }} sx={{ p: 1.5, bgcolor: '#fafafa', borderRight: '1px solid #eee' }}>
                      <Typography variant="caption" sx={{ color: '#888', fontWeight: 600 }}>Requested Qty</Typography>
                    </Grid>
                    <Grid size={{ xs: 9 }} sx={{ p: 1.5 }}>
                      <Typography variant="body2" sx={{ fontWeight: 800 }}>{item.quantity}</Typography>
                    </Grid>
                  </Grid>
                </Paper>
              </Box>

              <Divider />

              <Box sx={{ p: 2 }}>
                {isLoadingMatch ? (
                  <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                    <CircularProgress size={16} />
                    <Typography variant="body2" sx={{ color: '#888' }}>Checking internal database...</Typography>
                  </Box>
                ) : matchingResult?.hasExactMatch ? (
                  <Box sx={{ bgcolor: '#e8f5e9', p: 2, borderRadius: 1.5, border: '1px solid #c8e6c9' }}>
                    <Typography variant="body2" sx={{ color: '#2e7d32', fontWeight: 800 }}>Exact Match Found!</Typography>
                    <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>{matchingResult.exactMatch.productName} ({matchingResult.exactMatch.partNo})</Typography>
                  </Box>
                ) : (
                  <Box>
                    <Typography variant="body2" sx={{ color: '#888', mb: 2 }}>No direct product matches found in the database.</Typography>
                    <TextField
                      fullWidth
                      placeholder="Search Internet"
                      size="small"
                      onClick={() => setShowSupplierSearch(true)}
                      slotProps={{
                        input: {
                          readOnly: true,
                          sx: { borderRadius: 1.5, cursor: 'pointer', '& input': { cursor: 'pointer' } }
                        }
                      }}
                    />
                  </Box>
                )}
              </Box>
            </>
          )}
        </DialogContent>
        <DialogActions sx={{ p: 2, bgcolor: '#fafafa' }}>
          <Button onClick={() => showSupplierSearch ? setShowSupplierSearch(false) : onClose()} variant="outlined" sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5 }}>
            {showSupplierSearch ? 'Back' : 'Close'}
          </Button>
          {!showSupplierSearch && (
            <Button
              variant="contained"
              startIcon={<BatchIcon />}
              sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5, bgcolor: '#f5f5f5', color: '#ccc' }}
              disabled
            >
              Request Quote From Supplier
            </Button>
          )}
        </DialogActions>
      </Dialog>

      <SupplierQuoteRequestDialog
        open={!!selectedSupplier}
        onClose={() => setSelectedSupplier(null)}
        supplier={selectedSupplier}
        items={item ? [item] : []}
        rfqNo={rfqNo}
      />
    </>
  );
};

// ─── Main Page ────────────────────────────────────────────────────────────────

const ProcessRFQPage: React.FC = () => {
  const { t } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { userData } = useAuth();

  const [items, setItems] = useState<ProcessItem[]>([]);
  const [matchedCustomer, setMatchedCustomer] = useState<any>(null);
  const [hasInitialized, setHasInitialized] = useState(false);
  const [detailsItem, setDetailsItem] = useState<ProcessItem | null>(null);
  const [showBatchSupplierSearch, setShowBatchSupplierSearch] = useState(false);
  const [selectedSupplierForBatch, setSelectedSupplierForBatch] = useState<any>(null);

  const [batchSearchQuery, setBatchSearchQuery] = useState('');
  const [batchSearchTab, setBatchSearchTab] = useState(0); // 0: Internal, 1: Web
  const [batchSuppliers, setBatchSuppliers] = useState<any[]>([]);
  const [loadingBatchSuppliers, setLoadingBatchSuppliers] = useState(false);

  useEffect(() => {
    let active = true;
    if (showBatchSupplierSearch) {
      setLoadingBatchSuppliers(true);
      const searchFn = batchSearchTab === 0 
        ? supplierService.getAll({ businessUnitId: userData?.businessUnitId ?? 0, name: batchSearchQuery, pageSize: 20 }).then(r => r.items)
        : supplierService.searchWebSuppliers(batchSearchQuery || 'Supplies');

      searchFn
        .then(res => {
          if (active) setBatchSuppliers(res || []);
        })
        .catch(() => {
          if (active) setBatchSuppliers([]);
        })
        .finally(() => {
          if (active) setLoadingBatchSuppliers(false);
        });
    }
    return () => { active = false; };
  }, [showBatchSupplierSearch, batchSearchQuery, batchSearchTab, userData?.businessUnitId]);

  const [page, setPage] = useState(0);
  const [rowsPerPage, setRowsPerPage] = useState(20);

  // ── Data fetching ──────────────────────────────────────────────────────────

  const {
    data: lead,
    isLoading,
    isError: isLeadError,
    error: leadError,
    refetch: refetchLead,
  } = useQuery({
    queryKey: ['accepted-lead', id],
    queryFn: () => leadService.getAcceptedLeadById(Number(id)),
    enabled: !!id,
    staleTime: 60_000,
  });

  const { data: customers = [] } = useQuery({
    queryKey: ['customers', userData?.businessUnitId],
    queryFn: () => customerService.getAll({ pageSize: 1000 }).then(res => res.items),
    enabled: !!userData?.businessUnitId,
  });

  // ── Initialization (runs once when lead loads) ─────────────────────────────

  const findMatchedCustomer = useCallback(async (leadData: AcceptedLeadFullResponseDTO) => {
    if (!leadData.clientemail) return;
    try {
      const customer = await customerService.getCustomerByEmail(leadData.clientemail);
      if (customer) setMatchedCustomer(customer);
    } catch (e) {
      console.error('Customer matching failed', e);
    }
  }, []);

  // ── Item mutations ─────────────────────────────────────────────────────────

  const updateItem = useCallback((index: number, fields: Partial<ProcessItem>) => {
    setItems(prev => {
      const next = [...prev];
      next[index] = { ...next[index], ...fields };
      return next;
    });
  }, []);

  const handleRunSmartMatchAll = useCallback(async (providedItems?: ProcessItem[]) => {
    const targetItems = providedItems || items;
    if (targetItems.length === 0) return;

    setItems(prev => prev.map(i => i.matchStatus === 'pending' || i.matchStatus === 'unavailable' ? { ...i, matchStatus: 'loading' } : i));

    const chunkSize = 5;
    for (let i = 0; i < targetItems.length; i += chunkSize) {
      const chunk = targetItems.slice(i, i + chunkSize);

      const matches = await Promise.all(
        chunk.map(async (it) => {
          if (it.matchStatus !== 'pending' && it.matchStatus !== 'loading' && it.matchStatus !== 'unavailable') {
            return null;
          }
          try {
            const res = await productService.matchProduct({
              name: it.productShortName,
              description: it.productShortDescription || it.productShortName,
              partNo: it.manufacturerPartNumber,
              manufacturer: it.manufacturerName,
              businessUnitId: userData?.businessUnitId,
              quantity: it.quantity ?? undefined,
            });
            if (res.hasExactMatch && res.exactMatch) {
              const exact = res.exactMatch;
              return {
                matchStatus: 'matched' as const,
                productId: exact.productId ?? exact.id ?? null,
                unitPrice: exact.finalSalesPrice ?? exact.sellingPrice ?? 0,
                qtyOnHand: exact.qtyOnHand ?? 0,
                availableToPromise: exact.availableToPromise ?? 0,
                incomingAvailable: exact.incomingAvailable ?? 0,
                projectedShortage: exact.projectedShortage
                  ?? (it.quantity === null ? undefined : Math.max(0, it.quantity - (exact.availableToPromise ?? 0))),
                availabilityStatus: exact.availabilityStatus,
                leadTimeDays: exact.leadTimeDays,
                expectedAvailableOn: exact.expectedAvailableOn,
                unitCost: exact.unitCost,
                costCurrencyCode: exact.costCurrencyCode,
                decisionState: exact.decisionState,
                evidenceReference: exact.evidenceReference,
                selectedName: `${exact.productName} (${exact.partNo})`
              };
            }
            return { matchStatus: 'no-match' as const };
          } catch {
            return { matchStatus: 'unavailable' as const, productId: null, supplierQuotedItemId: null };
          }
        })
      );

      setItems(prev => {
        const next = [...prev];
        matches.forEach((match, idx) => {
          if (match && next[i + idx]) {
            next[i + idx] = { ...next[i + idx], ...match };
          }
        });
        return next;
      });
    }
  }, [items, productService, userData?.businessUnitId]);

  // Single stable effect — only runs when lead first becomes available
  React.useEffect(() => {
    if (!lead || hasInitialized) return;

    const initialItems = lead.leadItems.map(item => ({
      ...item,
      selectionSource: 'product' as const,
      productId: null,
      supplierQuotedItemId: null,
      matchStatus: 'pending' as const,
      include: true,
    }));

    setItems(initialItems);
    setHasInitialized(true);
    findMatchedCustomer(lead);

    // Auto-trigger smart match for all items
    if (initialItems.length > 0) {
      handleRunSmartMatchAll(initialItems);
    }
  }, [lead, hasInitialized, findMatchedCustomer, handleRunSmartMatchAll]);

  const removeItem = useCallback((index: number) => {
    setItems(prev => prev.filter((_, i) => i !== index));
  }, []);

  const toggleSelectItem = useCallback((index: number) => {
    setItems(prev => {
      const next = [...prev];
      next[index] = { ...next[index], include: !next[index].include };
      return next;
    });
  }, []);

  const handleAddItem = useCallback(() => {
    setItems(prev => [
      ...prev,
      {
        id: 0,
        productShortName: 'Manual Item',
        productShortDescription: '',
        quantity: 1,
        unitPrice: 0,
        selectionSource: 'product',
        productId: null,
        supplierQuotedItemId: null,
        matchStatus: 'no-match',
        include: true,
      } as ProcessItem,
    ]);
  }, []);

  // ── Submit ─────────────────────────────────────────────────────────────────

  const createRfqMutation = useMutation({
    mutationFn: (payload: RfqCreatePayload) => rfqService.create(payload),
    onSuccess: (createdRfq: RfqResponseDTO) => {
      // The backend guarantees commercial-case lineage (the chosen lead, or a governed shell lead
      // when none was sent), and the response now reports the RFQ's OWN case rather than falling
      // back to its lead's. A lead id is therefore no longer evidence of a case: claiming one on
      // that basis is exactly the substitution that was removed from the API.
      const caseRef = createdRfq?.commercialCaseReference?.trim();
      if (caseRef) {
        toast.success(`Draft RFQ created — linked to commercial case ${caseRef}.`);
      } else if (createdRfq?.commercialCaseId != null) {
        toast.success('Draft RFQ created — linked to its commercial case.');
      } else {
        toast.error('Draft RFQ created, but it is not linked to a commercial case and cannot be traced to delivery.');
      }
      const createdId = Number(createdRfq?.id ?? 0);
      navigate(createdId > 0 ? `/procurement/rfqs/${createdId}/sourcing` : '/procurement/rfqs/draft');
    },
    onError: (err: unknown) => {
      // The server's honest reason (e.g. "A tenant-owned lead is required so the RFQ belongs to a
      // commercial case.") renders when it is safe; the fallback covers unrenderable bodies only.
      toast.error(presentableErrorMessage(err, 'The RFQ could not be created. Nothing was changed — please try again.'));
    },
  });

  const handleSubmit = useCallback(() => {
    // This page turns an accepted lead into an RFQ; without the loaded lead there is nothing to
    // send (and the payload would silently drop leadId/recDate, which the backend requires).
    if (!lead) return;

    const includedItems = items.filter(i => i.include);
    if (includedItems.length === 0) {
      toast.error('Please select at least one item to include');
      return;
    }

    // The backend requires a whole-number Quantity >= 1 on every line; catch it here so the user
    // fixes the field instead of getting a round-trip 400.
    const quantifiedItems = includedItems.filter(hasStatedQuantity);
    const invalidQtyCount = includedItems.length - quantifiedItems.length;
    if (invalidQtyCount > 0) {
      toast.error(`${invalidQtyCount === 1 ? '1 line needs' : `${invalidQtyCount} lines need`} a whole-number quantity of at least 1 before the RFQ can be created.`);
      return;
    }

    const unchecked = includedItems.filter(i => i.matchStatus === 'pending' || i.matchStatus === 'unavailable' || i.matchStatus === 'loading');
    if (unchecked.length > 0) {
      toast.error(`Inventory verification is incomplete for ${unchecked.length} line${unchecked.length === 1 ? '' : 's'}. Check availability before creating the RFQ.`);
      return;
    }

    const sourcingRequired = includedItems.filter(i => !i.productId && !i.supplierQuotedItemId).length;
    if (sourcingRequired > 0) {
      toast(`${sourcingRequired} unresolved line${sourcingRequired === 1 ? '' : 's'} will be carried to governed supplier sourcing.`);
    }

    const nowIso = new Date().toISOString();
    createRfqMutation.mutate({
      buyersName: lead.buyersName,
      // RecDate is a non-nullable DateTime server-side; never let it fall out of the payload.
      recDate: lead.recDate ?? nowIso,
      bidClosingDate: lead.bidClosingDate,
      headerRemarks: lead.headerRemarks,
      opportunityNo: lead.opportunityNo,
      rfqtype: lead.rfqtype,
      customerId: matchedCustomer?.id,
      leadId: lead.id,
      rfqitems: quantifiedItems.map(item => ({
        companyRef: item.companyRef,
        customerAccountPortalId: item.customerAccountPortalId,
        customerRfqno: item.customerRfqno,
        lineItemNo: item.lineItemNo,
        productShortName: item.productShortName,
        productId: item.selectionSource === 'product' ? item.productId : null,
        supplierQuotedItemId: item.selectionSource === 'quotedItem' ? item.supplierQuotedItemId : null,
        productShortDescription: item.productShortDescription || item.productShortName,
        quantity: item.quantity,
        unitPrice: item.unitPrice,
        manufacturerName: item.manufacturerName,
        manufacturerPartNumber: item.manufacturerPartNumber,
        bidClosingDateLine: item.bidClosingDateLine ?? lead.bidClosingDate ?? nowIso,
      })),
    });
  }, [items, lead, matchedCustomer, createRfqMutation]);

  // ── Render guards ──────────────────────────────────────────────────────────

  if (isLoading) return <Box sx={{ p: 4, textAlign: 'center' }}><CircularProgress /></Box>;
  if (isLeadError) {
    // Same presentation boundary as the create call: server reasons render only when safe, and an
    // axios `.message` (which bakes in the API hostname) never reaches the screen.
    const message = toPresentableError(leadError, { fallbackMessage: 'The lead could not be loaded.' }).message;
    return (
      <Box sx={{ p: 4 }}>
        <Alert severity="error" action={<Button onClick={() => refetchLead()}>Retry</Button>}>
          {message}
        </Alert>
      </Box>
    );
  }
  if (!lead) {
    return (
      <Box sx={{ p: 4 }}>
        <Alert severity="warning" action={<Button onClick={() => refetchLead()}>Retry</Button>}>
          Lead not found or no longer available.
        </Alert>
      </Box>
    );
  }

  const businessUnitId = userData?.businessUnitId ?? 0;

  // ── Render ─────────────────────────────────────────────────────────────────

  return (
    <Box sx={{ p: 2, bgcolor: '#f8f9fa', minHeight: '100vh' }}>

      {/* Header */}
      <Box sx={{ mb: 1 }}>
        <Breadcrumbs sx={{ fontSize: '0.75rem', fontWeight: 600, mb: 1 }}>
          <Link underline="hover" color="inherit" href="/procurement/rfqs/outstanding" sx={{ cursor: 'pointer' }}>
            Outstanding Rfqs
          </Link>
          <Typography sx={{ fontSize: '0.75rem', fontWeight: 600 }} color="text.primary">{t('process_lead') || 'Process Lead'}</Typography>
        </Breadcrumbs>

        <Box sx={{ display: 'flex', flexDirection: { xs: 'column', md: 'row' }, gap: 1.5, justifyContent: 'space-between', alignItems: { xs: 'stretch', md: 'center' }, mb: 2 }}>
          <Typography variant="h5" sx={{ fontWeight: 950, letterSpacing: 0, color: '#1a237e' }}>
            {t('process_lead_to_rfq') || 'Process Lead To RFQ'}
          </Typography>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ flexWrap: { sm: 'wrap' }, '& .MuiButton-root': { width: { xs: '100%', sm: 'auto' } } }}>
            <Button
              variant="outlined" size="small" startIcon={<BackIcon />}
              onClick={() => navigate(-1)}
              sx={{ bgcolor: 'white', borderColor: '#ddd', color: '#666', fontWeight: 800, textTransform: 'none', px: 2 }}
            >
              Back
            </Button>
            <Button
              variant="outlined" size="small" startIcon={<BatchIcon />}
              onClick={() => {
                if (items.some(i => i.include && i.matchStatus === 'unavailable')) {
                  toast.error('Retry unavailable inventory checks before supplier sourcing.');
                } else if (items.some(i => i.include)) {
                  setShowBatchSupplierSearch(true);
                } else {
                  toast.error('Please select at least one item');
                }
              }}
              disabled={items.some(i => i.include && (i.matchStatus === 'pending' || i.matchStatus === 'unavailable' || i.matchStatus === 'loading'))}
              sx={{ bgcolor: 'white', borderColor: '#ddd', color: '#666', fontWeight: 800, textTransform: 'none', px: 2 }}
            >
              Batch Quote
            </Button>
            <Button
              variant="outlined" size="small" startIcon={<AutoFixIcon />}
              onClick={() => handleRunSmartMatchAll()}
              sx={{ bgcolor: 'white', borderColor: '#1976d2', color: '#1976d2', fontWeight: 800, textTransform: 'none', px: 2 }}
            >
              Smart Match All
            </Button>
            <Button
              variant="contained" size="small" startIcon={<SaveIcon />}
              onClick={handleSubmit}
              disabled={createRfqMutation.isPending || items.some(i => i.include && (i.matchStatus === 'pending' || i.matchStatus === 'unavailable' || i.matchStatus === 'loading'))}
              sx={{ bgcolor: '#1976d2', fontWeight: 800, textTransform: 'none', px: 3 }}
            >
              Create As Draft
            </Button>
          </Stack>
        </Box>
        {items.some(item => item.include && item.matchStatus === 'unavailable') && (
          <Alert severity="error" sx={{ mt: 1.5 }} action={<Button color="inherit" onClick={() => handleRunSmartMatchAll()}>Retry unavailable checks</Button>}>
            Inventory Check Unavailable. Supplier sourcing and RFQ creation remain blocked until these lines are checked successfully.
          </Alert>
        )}
      </Box>

      {/* General Information */}
      <Paper sx={{ mb: 2, borderRadius: 1.5, border: '1px solid #eee', boxShadow: 'none' }}>
        <Box sx={{ p: 1.5, borderBottom: '1px solid #eee', bgcolor: '#fafafa', borderTopLeftRadius: 6, borderTopRightRadius: 6 }}>
          <Typography variant="caption" sx={{ fontWeight: 900, color: '#444' }}>General Information</Typography>
        </Box>
        <Grid container>
          {[
            { label: 'RFQ Number', value: lead.rfqno, border: true, bottom: true },
            { label: 'Buyer', value: lead.buyersName, bottom: true },
            { label: 'Client Email', value: lead.clientemail, border: true, bottom: true },
            { label: 'Matched Customer', value: null, chip: true, bottom: true },
            { label: 'Source', value: lead.leadSource, border: true, bottom: true },
            // An "AI Confidence" row used to close this block. The score behind
            // it was never measured against a labelled corpus, so it is gone.
            { label: 'RFQ Type', value: lead.rfqtype || '—', bottom: true, border: true },
          ].map(({ label, value, chip, border, bottom }) => (
            <Grid
              key={label}
              size={{ xs: 12, md: 6 }}
              sx={{
                p: 1.5,
                ...(bottom && { borderBottom: '1px solid #eee' }),
                ...(border && { borderRight: '1px solid #eee' }),
              }}
            >
              <Box sx={{ display: 'flex', flexDirection: { xs: 'column', sm: 'row' }, gap: { xs: 0.5, sm: 1 }, alignItems: { xs: 'stretch', sm: 'center' } }}>
                <Typography sx={{ width: { xs: 'auto', sm: 160, lg: 200 }, flexShrink: 0, fontSize: '0.75rem', color: '#888', fontWeight: 500 }}>{label}</Typography>
                {chip ? (
                  <Autocomplete
                    size="small"
                    options={customers}
                    getOptionLabel={(option: any) => option.name || ''}
                    value={matchedCustomer}
                    onChange={(_, newValue) => setMatchedCustomer(newValue)}
                    renderInput={(params) => (
                      <TextField
                        {...params}
                        placeholder="Select Customer..."
                        sx={{ 
                          width: '100%',
                          '& .MuiInputBase-root': { height: 28, fontSize: '0.7rem', fontWeight: 700, borderRadius: 1 }
                        }}
                      />
                    )}
                    sx={{
                      width: '100%',
                      minWidth: 0,
                      '& .MuiAutocomplete-input': { p: '0 !important' }
                    }}
                  />
                ) : (
                  <Typography sx={{ fontSize: '0.75rem', fontWeight: 700, color: '#333' }}>{value}</Typography>
                )}
              </Box>
            </Grid>
          ))}
          {/* Empty cell to balance last row */}
          <Grid size={{ xs: 12, md: 6 }} sx={{ p: 1.5 }} />
        </Grid>
      </Paper>

      {/* Assignment Details */}
      <Paper sx={{ mb: 2, borderRadius: 1.5, border: '1px solid #eee', boxShadow: 'none' }}>
        <Box sx={{ p: 1.5, borderBottom: '1px solid #eee', bgcolor: '#fafafa', borderTopLeftRadius: 6, borderTopRightRadius: 6 }}>
          <Typography variant="caption" sx={{ fontWeight: 900, color: '#444' }}>Assignment Details</Typography>
        </Box>
        <Grid container>
          <Grid size={{ xs: 6 }} sx={{ p: 1.5, borderBottom: '1px solid #eee', borderRight: '1px solid #eee' }}>
            <Box sx={{ display: 'flex' }}>
              <Typography sx={{ width: 200, fontSize: '0.75rem', color: '#888', fontWeight: 500 }}>Assigned To</Typography>
              <Typography sx={{ fontSize: '0.75rem', fontWeight: 700, color: '#333' }}>{lead.assignedToFullName}</Typography>
            </Box>
          </Grid>
          <Grid size={{ xs: 6 }} sx={{ p: 1.5, borderBottom: '1px solid #eee' }}>
            <Box sx={{ display: 'flex' }}>
              <Typography sx={{ width: 200, fontSize: '0.75rem', color: '#888', fontWeight: 500 }}>Assigned On</Typography>
              <Typography sx={{ fontSize: '0.75rem', fontWeight: 700, color: '#333' }}>
                {lead.assignedOn ? new Date(lead.assignedOn).toLocaleString() : '—'}
              </Typography>
            </Box>
          </Grid>
          <Grid size={{ xs: 12 }} sx={{ p: 1.5 }}>
            <Box sx={{ display: 'flex' }}>
              <Typography sx={{ width: 200, fontSize: '0.75rem', color: '#888', fontWeight: 500 }}>Assignment Comment</Typography>
              <Typography sx={{ fontSize: '0.75rem', fontWeight: 700, color: '#333' }}>{lead.assignComment || '—'}</Typography>
            </Box>
          </Grid>
        </Grid>
      </Paper>

      {/* Process Items */}
      <Paper sx={{ mb: 1, borderRadius: 1.5, border: '1px solid #eee', boxShadow: 'none', overflow: 'hidden' }}>
        <Box sx={{ p: 1.5, borderBottom: '1px solid #eee', bgcolor: '#fafafa' }}>
          <Typography variant="caption" sx={{ fontWeight: 900, color: '#444' }}>Process Items</Typography>
        </Box>

        <Box sx={{ overflowX: 'auto', width: '100%' }}>
        <Table size="small" sx={{ minWidth: 900 }}>
          <TableHead sx={{ bgcolor: '#fafafa' }}>
            <TableRow>
              <TableCell padding="checkbox">
                <Checkbox
                  size="small"
                  checked={items.length > 0 && items.every(i => i.include)}
                  indeterminate={items.some(i => i.include) && !items.every(i => i.include)}
                  onChange={(e) => setItems(prev => prev.map(i => ({ ...i, include: e.target.checked })))}
                  slotProps={{ input: { 'aria-label': 'Select all RFQ lines' } }}
                />
              </TableCell>
              {['Requested Item', 'Select Product Or Quote', 'Qty', 'Price', 'Action'].map((h, i) => (
                <TableCell
                  key={h}
                  align={i > 1 ? 'center' : 'left'}
                  sx={{ fontSize: '0.75rem', fontWeight: 800, color: '#555', py: 1.5 }}
                >
                  {h}
                </TableCell>
              ))}
            </TableRow>
          </TableHead>
          <TableBody>
            {items.slice(page * rowsPerPage, page * rowsPerPage + rowsPerPage).map((item, localIndex) => {
              const actualIndex = page * rowsPerPage + localIndex;
              return (
                <ItemRow
                  key={item.id || actualIndex}
                  item={item}
                  index={actualIndex}
                  onUpdate={updateItem}
                  onRemove={removeItem}
                  onViewDetails={setDetailsItem}
                  onToggleSelect={toggleSelectItem}
                  businessUnitId={businessUnitId}
                />
              );
            })}
          </TableBody>
        </Table>
        </Box>

        {/* Footer / Pagination */}
        <TablePagination
          component="div"
          count={items.length}
          page={page}
          onPageChange={(_, newPage) => setPage(newPage)}
          rowsPerPage={rowsPerPage}
          onRowsPerPageChange={(e) => {
            setRowsPerPage(parseInt(e.target.value, 10));
            setPage(0);
          }}
          rowsPerPageOptions={[10, 20, 50, 100]}
          sx={{ borderTop: '1px solid #eee' }}
        />
      </Paper>

      {/* Add Item */}
      <Button
        fullWidth variant="outlined"
        onClick={handleAddItem}
        startIcon={<AddIcon />}
        sx={{
          borderStyle: 'dashed', borderColor: '#ccc', color: '#666',
          py: 1, textTransform: 'none', fontSize: '0.75rem', fontWeight: 700,
          '&:hover': { borderStyle: 'dashed', bgcolor: '#fafafa' },
        }}
      >
        Add Item
      </Button>

      <ItemDetailsDialog
        open={!!detailsItem}
        item={detailsItem}
        onClose={() => setDetailsItem(null)}
        rfqNo={lead?.rfqno || ''}
      />

      <Dialog open={showBatchSupplierSearch} onClose={() => setShowBatchSupplierSearch(false)} maxWidth="sm" fullWidth>
        <DialogTitle sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', p: 2 }}>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            <SupplierIcon sx={{ color: '#1976d2' }} />
            <Typography variant="h6" sx={{ fontWeight: 800, fontSize: '1rem' }}>Find Supplier for Selected Items ({items.filter(i => i.include).length})</Typography>
          </Box>
          <IconButton onClick={() => setShowBatchSupplierSearch(false)} size="small"><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent dividers>
          <Box sx={{ mb: 2, p: 1.5, bgcolor: '#fafafa', borderRadius: 1.5, border: '1px solid #eee' }}>
            <Typography variant="caption" sx={{ fontWeight: 900, color: '#333', mb: 1, display: 'block' }}>
              Selected Items to be Sourced:
            </Typography>
            <List dense sx={{ p: 0 }}>
              {items.filter(i => i.include).map((item, idx) => (
                <ListItem key={idx} sx={{ p: 0, mb: 0.5 }}>
                  <ListItemText
                    primary={
                      <Typography sx={{ fontSize: '0.75rem', fontWeight: 700 }}>
                        {item.manufacturerPartNumber || 'N/A'} - {item.productShortDescription || item.productShortName}
                      </Typography>
                    }
                    secondary={
                      item.preferredSupplierName ? (
                        <Chip
                          label={`Current Supplier: ${item.preferredSupplierName}`}
                          size="small"
                          sx={{ height: 16, fontSize: '0.6rem', mt: 0.5, bgcolor: '#e8f5e9', color: '#2e7d32', borderRadius: 1 }}
                        />
                      ) : (
                        <Chip
                          label="No Supplier Selected Yet"
                          size="small"
                          sx={{ height: 16, fontSize: '0.6rem', mt: 0.5, bgcolor: '#fff3e0', color: '#e65100', borderRadius: 1 }}
                        />
                      )
                    }
                  />
                </ListItem>
              ))}
            </List>
          </Box>

          <Tabs
            value={batchSearchTab}
            onChange={(_, v) => setBatchSearchTab(v)}
            variant="fullWidth"
            sx={{ mb: 2, '& .MuiTab-root': { textTransform: 'none', fontWeight: 800, fontSize: '0.75rem' } }}
          >
            <Tab icon={<DatabaseIcon sx={{ fontSize: 18 }} />} iconPosition="start" label="Internal Database" />
            <Tab icon={<InternetIcon sx={{ fontSize: 18 }} />} iconPosition="start" label="Internet Search" />
          </Tabs>

          <TextField
            fullWidth size="small"
            value={batchSearchQuery}
            onChange={(e) => setBatchSearchQuery(e.target.value)}
            placeholder={batchSearchTab === 0 ? "Search internal suppliers..." : "Search internet for new suppliers..."}
            slotProps={{
              input: {
                startAdornment: (
                  <InputAdornment position="start">
                    <SearchIcon sx={{ color: '#888', fontSize: 18 }} />
                  </InputAdornment>
                ),
                sx: { borderRadius: 1.5, bgcolor: '#fff' }
              }
            }}
          />
          {loadingBatchSuppliers ? (
            <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}>
              <CircularProgress size={24} />
            </Box>
          ) : batchSuppliers.length === 0 ? (
            <Typography variant="body2" sx={{ color: '#888', py: 3, textAlign: 'center' }}>
              No suppliers found.
            </Typography>
          ) : (
            <List sx={{ mt: 2 }}>
              {batchSuppliers.map((s, idx) => (
                <ListItem
                  key={s.id || idx}
                  sx={{ 
                    border: '1px solid #f0f0f0', 
                    borderRadius: 2, 
                    mb: 1,
                    '&:hover': { bgcolor: '#f5f7ff', borderColor: '#1976d2' }
                  }}
                  secondaryAction={
                    <Button
                      variant="contained" size="small"
                      onClick={() => {
                        setSelectedSupplierForBatch(s);
                        setShowBatchSupplierSearch(false);
                        setItems(prev => prev.map(item => item.include ? {
                          ...item,
                          preferredSupplierName: s.name,
                          preferredSupplierEmail: s.contactEmail || s.email,
                          matchStatus: batchSearchTab === 0 ? 'matched' : 'sourced-web'
                        } : item));
                      }}
                      sx={{ textTransform: 'none', fontWeight: 800, borderRadius: 1.5, fontSize: '0.7rem' }}
                    >
                      Select & Quote
                    </Button>
                  }
                >
                  <ListItemAvatar>
                    <Avatar sx={{ bgcolor: batchSearchTab === 0 ? '#ede7f6' : '#e3f2fd', color: batchSearchTab === 0 ? '#512da8' : '#1976d2', width: 32, height: 32 }}>
                      {batchSearchTab === 0 ? <DatabaseIcon sx={{ fontSize: 16 }} /> : <InternetIcon sx={{ fontSize: 16 }} />}
                    </Avatar>
                  </ListItemAvatar>
                  <ListItemText
                    primary={<Typography sx={{ fontWeight: 800, fontSize: '0.8rem', color: '#333' }}>{s.name}</Typography>}
                    secondary={
                      <Box sx={{ mt: 0.25 }}>
                        <Typography variant="caption" sx={{ display: 'block', color: '#666' }}>{s.contactEmail || s.email || 'No email'}</Typography>
                        <Typography variant="caption" sx={{ color: '#888' }}>{s.cityName || s.city || 'Location Unknown'}</Typography>
                      </Box>
                    }
                  />
                </ListItem>
              ))}
            </List>
          )}
        </DialogContent>
      </Dialog>

      <SupplierQuoteRequestDialog
        open={!!selectedSupplierForBatch}
        onClose={() => setSelectedSupplierForBatch(null)}
        supplier={selectedSupplierForBatch}
        items={items.filter(i => i.include)}
        rfqNo={lead?.rfqno || ''}
      />
    </Box>
  );
};

export default ProcessRFQPage;
