import React, { useState, useMemo, useCallback, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Grid, Stack, Chip,
  Table, TableHead, TableRow, TableCell, TableBody,
  IconButton, TextField, Autocomplete, CircularProgress,
  MenuItem, Dialog, DialogTitle, DialogContent, DialogActions,
  Divider, Tabs, Tab, InputAdornment, List, ListItem, ListItemText,
  ListItemAvatar, Avatar, Checkbox, TablePagination
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
import customerService from '../../../api/services/customerService';
import supplierService from '../../../api/services/supplierService';
import { useAuth } from '../../../context/AuthContext';
import { toast } from 'react-hot-toast';
import supplierQuotedItemService from '../../../api/services/supplierQuotedItemService';

// ─── Types ────────────────────────────────────────────────────────────────────

interface ProcessItem extends AcceptedLeadItemDTO {
  selectionSource: 'product' | 'quotedItem';
  productId: number | null;
  supplierQuotedItemId: number | null;
  matchStatus: 'pending' | 'loading' | 'matched' | 'no-match' | 'sourced-web';
  finalSalesPrice?: number;
  finalLandedCost?: number;
  qtyOnHand?: number;
  include: boolean;
  preferredSupplierName?: string;
  preferredSupplierEmail?: string;
}

// ─── Sub-components ───────────────────────────────────────────────────────────

interface ProductSelectorProps {
  value: number | null;
  onChange: (p: any) => void;
  businessUnitId: number;
}

const ProductSelector: React.FC<ProductSelectorProps> = React.memo(({ value, onChange, businessUnitId }) => {
  const [search, setSearch] = useState('');
  const [inputValue, setInputValue] = useState('');

  const { data: products = [], isLoading } = useQuery({
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
                  label={`Stock: ${option.qtyOnHand ?? 0}`}
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

  const { data: quotes = [], isLoading } = useQuery({
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
                <Typography variant="caption" sx={{ fontWeight: 900, color: 'primary.main' }}>
                  ${(option.unitPrice ?? 0).toFixed(2)}
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
    });
  }, [index, onUpdate]);

  const handleProductChange = useCallback((p: any) => {
    onUpdate(index, {
      productId: p?.id ?? null,
      unitPrice: p?.finalSalesPrice ?? p?.sellingPrice ?? 0,
      qtyOnHand: p?.qtyOnHand ?? 0,
    });
    setIsEditing(false);
  }, [index, onUpdate]);

  const handleQuoteChange = useCallback((q: any) => {
    onUpdate(index, {
      supplierQuotedItemId: q?.id ?? null,
      unitPrice: q?.unitPrice ?? 0,
    });
    setIsEditing(false);
  }, [index, onUpdate]);

  const handleQtyChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    onUpdate(index, { quantity: Number(e.target.value) });
  }, [index, onUpdate]);

  const handlePriceChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    onUpdate(index, { unitPrice: Number(e.target.value.replace('$ ', '')) });
  }, [index, onUpdate]);

  const handleRemove = useCallback(() => onRemove(index), [index, onRemove]);
  const handleViewDetails = useCallback(() => onViewDetails(item), [item, onViewDetails]);

  const handleSmartMatch = useCallback(async () => {
    onUpdate(index, { matchStatus: 'loading' });
    try {
      const res = await productService.matchProduct({
        name: item.productShortName,
        partNo: item.manufacturerPartNumber,
        manufacturer: item.manufacturerName
      });
      if (res.hasExactMatch && res.exactMatch) {
        onUpdate(index, {
          matchStatus: 'matched',
          productId: res.exactMatch.id,
          unitPrice: res.exactMatch.finalSalesPrice ?? res.exactMatch.sellingPrice ?? 0,
          qtyOnHand: res.exactMatch.qtyOnHand ?? 0
        });
      } else {
        onUpdate(index, { matchStatus: 'no-match' });
      }
    } catch (e) {
      onUpdate(index, { matchStatus: 'no-match' });
    }
  }, [item, index, onUpdate]);

  return (
    <TableRow sx={{ '& td': { borderBottom: '1px solid #f0f0f0' }, bgcolor: item.include ? 'transparent' : '#fafafa' }}>
      <TableCell padding="checkbox">
        <Checkbox
          size="small"
          checked={!!item.include}
          onChange={() => onToggleSelect(index)}
        />
      </TableCell>
      {/* Requested Item */}
      <TableCell sx={{ py: 2 }}>
        <Box>
          <Typography sx={{ fontWeight: 800, fontSize: '0.75rem', color: '#1a237e', textTransform: 'uppercase' }}>
            {item.productShortName}
          </Typography>
          <Box sx={{ display: 'flex', gap: 1, my: 0.5 }}>
            <Chip
              label={`AI: ${Math.round((item.aiconfidence ?? 0) * 100)}%`}
              size="small"
              sx={{ height: 16, fontSize: '0.55rem', fontWeight: 900, bgcolor: '#e8f5e9', color: '#2e7d32', borderRadius: 1 }}
            />
            {item.matchStatus === 'matched' && (
              <Chip
                label="System Match"
                size="small"
                sx={{ height: 16, fontSize: '0.55rem', fontWeight: 900, bgcolor: '#e3f2fd', color: '#1976d2', borderRadius: 1 }}
              />
            )}
            {item.matchStatus === 'no-match' && (
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <Chip
                  label="New Item"
                  size="small"
                  sx={{ height: 16, fontSize: '0.55rem', fontWeight: 900, bgcolor: '#fff3e0', color: '#ef6c00', borderRadius: 1 }}
                />
                <Button
                  size="small"
                  variant="outlined"
                  onClick={() => setWebSearchOpen(true)}
                  sx={{ height: 20, fontSize: '0.62rem', textTransform: 'none', fontWeight: 800, p: 0, px: 1, minWidth: 40, borderRadius: 1 }}
                >
                  Search Internet for Supplier
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
          </Box>
          <Typography variant="caption" sx={{ color: '#888', fontWeight: 600, display: 'block' }}>
            Qty: {item.quantity}
          </Typography>
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
        <Stack spacing={1} sx={{ width: 350 }}>
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
              onClick={() => setIsEditing(true)}
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
              <Typography sx={{ fontSize: '0.75rem', color: item.productId || item.supplierQuotedItemId ? 'text.primary' : 'text.secondary', fontWeight: 600 }}>
                {item.selectionSource === 'product'
                  ? item.productId
                    ? `Product ID: ${item.productId} (Click to change)`
                    : 'Select Product...'
                  : item.supplierQuotedItemId
                  ? `Quote ID: ${item.supplierQuotedItemId} (Click to change)`
                  : 'Select Quote...'}
              </Typography>
            </Box>
          )}
        </Stack>
      </TableCell>

      {/* Qty */}
      <TableCell align="center">
        <TextField
          size="small"
          type="number"
          value={item.quantity}
          onChange={handleQtyChange}
          sx={{ width: 80, '& .MuiInputBase-root': { height: 32, fontSize: '0.75rem', fontWeight: 700 } }}
        />
      </TableCell>

      {/* Price */}
      <TableCell align="center">
        <TextField
          size="small"
          value={`$ ${(item.unitPrice ?? 0).toFixed(2)}`}
          onChange={handlePriceChange}
          sx={{ width: 100, '& .MuiInputBase-root': { height: 32, fontSize: '0.75rem', fontWeight: 700 } }}
        />
      </TableCell>

      {/* Action */}
      <TableCell align="center">
        <IconButton size="small" onClick={handleRemove} color="error">
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

Required Date: ${new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString().split('T')[0]}

Please provide your best pricing and lead time for the items listed above.

Thank you for your assistance.

Best regards`;

      setEmailData({
        to: supplier.email || '',
        cc: 'manager@example.com',
        subject,
        body
      });
    }
  }, [supplier, items, rfqNo]);

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
        <Button variant="contained" startIcon={<SendIcon />} sx={{ borderRadius: 1.5, textTransform: 'none', fontWeight: 700, px: 3 }}>Send Request</Button>
      </DialogActions>
    </Dialog>
  );
};

const ItemDetailsDialog: React.FC<{
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
        supplierService.searchSuppliers(searchQuery, '', businessUnitId)
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
  }, [open, showSupplierSearch, searchTab, searchQuery, item]);

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
        manufacturer: item.manufacturerName
      });
      setMatchingResult(res);
    } catch (e) {
      console.error("Match failed", e);
    } finally {
      setIsLoadingMatch(false);
    }
  };

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

                {loadingSuppliers ? (
                  <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}>
                    <CircularProgress size={24} />
                  </Box>
                ) : suppliers.length === 0 ? (
                  <Typography variant="body2" sx={{ color: '#888', py: 3, textAlign: 'center' }}>
                    {searchTab === 0
                      ? 'No results found in internal database.'
                      : 'No results found on the internet.'}
                  </Typography>
                ) : (
                  <List sx={{ mt: 2 }}>
                    {suppliers.map((s, idx) => (
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
                              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mt: 0.5 }}>
                                <Typography variant="caption" sx={{ color: '#888' }}>{s.cityName || s.city || 'No Location'}</Typography>
                                <Chip label={searchTab === 0 ? "Internal DB" : "External Source"} size="small" sx={{ height: 16, fontSize: '0.5rem', fontWeight: 900, borderRadius: 1, bgcolor: searchTab === 0 ? '#ede7f6' : '#e3f2fd', color: searchTab === 0 ? '#512da8' : '#1976d2' }} />
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
                      placeholder="Search Internet for Suppliers"
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
  const [batchSuppliers, setBatchSuppliers] = useState<any[]>([]);
  const [loadingBatchSuppliers, setLoadingBatchSuppliers] = useState(false);

  useEffect(() => {
    let active = true;
    if (showBatchSupplierSearch) {
      setLoadingBatchSuppliers(true);
      supplierService.searchWebSuppliers(batchSearchQuery || 'Supplies')
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
  }, [showBatchSupplierSearch, batchSearchQuery]);

  const [page, setPage] = useState(0);
  const [rowsPerPage, setRowsPerPage] = useState(20);

  // ── Data fetching ──────────────────────────────────────────────────────────

  const { data: lead, isLoading } = useQuery({
    queryKey: ['accepted-lead', id],
    queryFn: () => leadService.getAcceptedLeadById(Number(id)),
    enabled: !!id,
    staleTime: 60_000,
  });

  // ── Initialization (runs once when lead loads) ─────────────────────────────

  const findMatchedCustomer = useCallback(async (leadData: AcceptedLeadFullResponseDTO) => {
    if (!leadData.clientemail) return;
    try {
      const customer = await customerService.getCustomerByEmail(
        leadData.clientemail,
        userData?.businessUnitId ?? 0
      );
      if (customer) setMatchedCustomer(customer);
    } catch (e) {
      console.error('Customer matching failed', e);
    }
  }, [userData?.businessUnitId]);

  // ── Item mutations ─────────────────────────────────────────────────────────

  const updateItem = useCallback((index: number, fields: Partial<ProcessItem>) => {
    setItems(prev => {
      const next = [...prev];
      next[index] = { ...next[index], ...fields };
      return next;
    });
  }, []);
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
  }, [lead, hasInitialized, findMatchedCustomer]);

  const handleRunSmartMatchAll = useCallback(async () => {
    // Mark all pending items as loading
    setItems(prev => prev.map(i => i.matchStatus === 'pending' ? { ...i, matchStatus: 'loading' } : i));

    const chunkSize = 5;
    for (let i = 0; i < items.length; i += chunkSize) {
      const chunk = items.slice(i, i + chunkSize);

      const matches = await Promise.all(
        chunk.map(async (it) => {
          if (it.matchStatus !== 'pending' && it.matchStatus !== 'loading') {
            return null;
          }
          try {
            const res = await productService.matchProduct({
              name: it.productShortName,
              partNo: it.manufacturerPartNumber,
              manufacturer: it.manufacturerName
            });
            if (res.hasExactMatch && res.exactMatch) {
              return {
                matchStatus: 'matched' as const,
                productId: res.exactMatch.id,
                unitPrice: res.exactMatch.finalSalesPrice ?? res.exactMatch.sellingPrice ?? 0,
                qtyOnHand: res.exactMatch.qtyOnHand ?? 0
              };
            }
            return { matchStatus: 'no-match' as const };
          } catch (e) {
            return { matchStatus: 'no-match' as const };
          }
        })
      );

      setItems(prev => {
        const next = [...prev];
        matches.forEach((res, idx) => {
          if (res && next[i + idx]) {
            next[i + idx] = { ...next[i + idx], ...res };
          }
        });
        return next;
      });
    }
  }, [items]);

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
    mutationFn: (payload: any) => rfqService.create(payload),
    onSuccess: () => {
      toast.success('Draft RFQ created successfully');
      navigate('/procurement/rfqs/draft');
    },
    onError: (err: any) => {
      toast.error(err.response?.data?.message ?? 'Failed to create RFQ');
    },
  });

  const handleSubmit = useCallback(() => {
    const includedItems = items.filter(i => i.include);
    if (includedItems.length === 0) {
      toast.error('Please select at least one item to include');
      return;
    }

    const itemsWithoutProduct = includedItems.filter(i => !i.productId && !i.supplierQuotedItemId && !i.preferredSupplierName);
    if (itemsWithoutProduct.length > 0) {
      toast.error('Please match or source a product/supplier for all included items before creating a draft.');
      return;
    }

    createRfqMutation.mutate({
      ...lead,
      rfqno: lead?.rfqno ?? `RFQ-${Date.now()}`,
      customerId: matchedCustomer?.id,
      leadId: lead?.id,
      businessUnitId: userData?.businessUnitId,
      rfqstatusId: 34,
      createdBy: userData?.userName,
      rfqitems: includedItems.map(item => ({
        ...item,
        productId: item.selectionSource === 'product' ? item.productId : null,
        supplierQuotedItemId: item.selectionSource === 'quotedItem' ? item.supplierQuotedItemId : null,
        productShortDescription: item.productShortDescription || item.productShortName,
        bidClosingDateLine: item.bidClosingDateLine ?? lead?.bidClosingDate ?? new Date().toISOString(),
        createdBy: userData?.userName,
      })),
    });
  }, [items, lead, matchedCustomer, userData, createRfqMutation]);

  // ── Render guards ──────────────────────────────────────────────────────────

  if (isLoading) return <Box sx={{ p: 4, textAlign: 'center' }}><CircularProgress /></Box>;
  if (!lead) return <Box sx={{ p: 4 }}><Typography>Lead not found</Typography></Box>;

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
          <Typography sx={{ fontSize: '0.75rem', fontWeight: 600 }} color="text.primary">Process Lead</Typography>
        </Breadcrumbs>

        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
          <Typography variant="h5" sx={{ fontWeight: 950, letterSpacing: '-0.02em', color: '#1a237e' }}>
            Process Lead To RFQ
          </Typography>
          <Stack direction="row" spacing={1.5}>
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
                if (items.some(i => i.include)) {
                  setShowBatchSupplierSearch(true);
                } else {
                  toast.error('Please select at least one item');
                }
              }}
              sx={{ bgcolor: 'white', borderColor: '#ddd', color: '#666', fontWeight: 800, textTransform: 'none', px: 2 }}
            >
              Batch Quote
            </Button>
            <Button
              variant="outlined" size="small" startIcon={<AutoFixIcon />}
              onClick={handleRunSmartMatchAll}
              sx={{ bgcolor: 'white', borderColor: '#1976d2', color: '#1976d2', fontWeight: 800, textTransform: 'none', px: 2 }}
            >
              Smart Match All
            </Button>
            <Button
              variant="contained" size="small" startIcon={<SaveIcon />}
              onClick={handleSubmit}
              disabled={createRfqMutation.isPending}
              sx={{ bgcolor: '#1976d2', fontWeight: 800, textTransform: 'none', px: 3 }}
            >
              Create As Draft
            </Button>
          </Stack>
        </Box>
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
            { label: 'RFQ Type', value: lead.rfqtype || '—', bottom: true },
            { label: 'AI Confidence', value: null, confidence: true, border: true },
          ].map(({ label, value, chip, confidence, border, bottom }) => (
            <Grid
              key={label}
              size={{ xs: 6 }}
              sx={{
                p: 1.5,
                ...(bottom && { borderBottom: '1px solid #eee' }),
                ...(border && { borderRight: '1px solid #eee' }),
              }}
            >
              <Box sx={{ display: 'flex', alignItems: 'center' }}>
                <Typography sx={{ width: 200, fontSize: '0.75rem', color: '#888', fontWeight: 500 }}>{label}</Typography>
                {chip ? (
                  matchedCustomer
                    ? <Chip label={matchedCustomer.name} color="success" size="small" sx={{ height: 18, fontSize: '0.6rem', fontWeight: 900, borderRadius: 1 }} />
                    : <Chip label="No Match" variant="outlined" color="warning" size="small" sx={{ height: 18, fontSize: '0.6rem', fontWeight: 900, borderRadius: 1 }} />
                ) : confidence ? (
                  <Chip
                    label={`${Math.round((lead.aiconfidence ?? 0) * 100)}%`}
                    size="small"
                    sx={{ height: 18, fontSize: '0.6rem', fontWeight: 900, bgcolor: '#e3f2fd', color: '#1976d2', borderRadius: 1 }}
                  />
                ) : (
                  <Typography sx={{ fontSize: '0.75rem', fontWeight: 700, color: '#333' }}>{value}</Typography>
                )}
              </Box>
            </Grid>
          ))}
          {/* Empty cell to balance last row */}
          <Grid size={{ xs: 6 }} sx={{ p: 1.5 }} />
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

        <Table size="small">
          <TableHead sx={{ bgcolor: '#fafafa' }}>
            <TableRow>
              <TableCell padding="checkbox">
                <Checkbox
                  size="small"
                  checked={items.length > 0 && items.every(i => i.include)}
                  indeterminate={items.some(i => i.include) && !items.every(i => i.include)}
                  onChange={(e) => setItems(prev => prev.map(i => ({ ...i, include: e.target.checked })))}
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

          <TextField
            fullWidth size="small"
            value={batchSearchQuery}
            onChange={(e) => setBatchSearchQuery(e.target.value)}
            placeholder="Search Suppliers..."
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
                  sx={{ border: '1px solid #f0f0f0', borderRadius: 2, mb: 1 }}
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
                          matchStatus: 'sourced-web'
                        } : item));
                      }}
                      sx={{ textTransform: 'none', fontWeight: 800, borderRadius: 1.5 }}
                    >
                      Select & Quote
                    </Button>
                  }
                >
                  <ListItemText
                    primary={<Typography sx={{ fontWeight: 700, fontSize: '0.8rem' }}>{s.name}</Typography>}
                    secondary={<Typography sx={{ fontSize: '0.75rem', color: '#666' }}>{s.contactEmail || s.email || 'No email'}</Typography>}
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