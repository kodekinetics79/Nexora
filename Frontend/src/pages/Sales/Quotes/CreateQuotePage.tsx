import React, { useState, useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery, useMutation } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Grid, Stack, Button, TextField,
  Autocomplete, IconButton, Divider, Table, TableHead,
  TableRow, TableCell, TableBody, InputAdornment, Card, CardContent,
  CircularProgress, Breadcrumbs, Link, MenuItem, Select, FormControl, InputLabel, Alert
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Save as SaveIcon,
  Add as AddIcon,
  Delete as DeleteIcon,
  Receipt as QuoteIcon
} from '@mui/icons-material';
import { useAuth } from '../../../context/AuthContext';
import { useUnsavedWorkGuard } from '../../../hooks/useUnsavedWorkGuard';
import rfqService from '../../../api/services/rfqService';
import quoteService from '../../../api/services/quoteService';
import commercialPolicyService from '../../../api/services/commercialPolicyService';
import setupService from '../../../api/services/setupService';
import productService from '../../../api/services/productService';
import currencyService from '../../../api/services/currencyService';
import { calculateQuoteTotals, type DiscountKind } from './quoteTotals';
import CustomerContextPanel from './CustomerContextPanel';
import { toast } from 'react-hot-toast';

interface QuoteItem {
  id?: number;
  productId: number | null;
  productName: string;
  itemDescription: string;
  quantity: number;
  unitPrice: number;
  totalAmount: number;
  discount: number;
  discountTypeId: number | null;
  discountValue: number;
  taxAmount: number;
  deliveryLeadTime: number;
}

const CreateQuotePage: React.FC = () => {
  const navigate = useNavigate();
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;

  // Form State
  const [customerId, setCustomerId] = useState<number | null>(null);
  const [rfqId, setRfqId] = useState<number | null>(null);
  const [quoteDate, setQuoteDate] = useState(new Date().toISOString().split('T')[0]);
  const [validUntil, setValidUntil] = useState(new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toISOString().split('T')[0]);
  const [headerRemarks, setHeaderRemarks] = useState('');
  // D6: the quote's currency. It used to be omitted from the payload entirely, so every quote this
  // screen created stored a null CurrencyId and the PDF fell back to "USD" — a Riyadh trader
  // emailing dollar-denominated quotations. A null currency also makes QuoteStatsDTO's pipeline
  // total unanswerable by design, so the dashboard figure could never resolve either.
  const [currencyId, setCurrencyId] = useState<number | null>(null);
  
  // Header Discount
  const [discountTypeId, setDiscountTypeId] = useState<number | null>(null);
  const [discountValue, setDiscountValue] = useState<number>(0);

  const [items, setItems] = useState<QuoteItem[]>([]);

  // Queries
  //
  // Every one of these defaults to an empty array on failure, and this is the screen that produces
  // the customer-facing document. A failed currency load left `currencyId` null and the rep built
  // a quote with no currency at all; a failed discount-type load made `discountKindOf` return null
  // so header discounts silently stopped applying. Neither said anything. The aggregate notice
  // below is lifted from SourcingWorkbenchPage, which already handles this correctly.
  const rfqsQuery = useQuery({
    queryKey: ['rfqs-list', businessUnitId],
    queryFn: () => rfqService.getAll({ pageNumber: 1, pageSize: 100, businessUnitId }).then(r => r.items),
  });
  const rfqs = rfqsQuery.data ?? [];
  const selectedRfq = rfqs.find((rfq) => rfq.id === rfqId) || null;

  const productsQuery = useQuery({
    queryKey: ['products-list', businessUnitId],
    queryFn: () => productService.getAll({ pageSize: 200, businessUnitId }).then(r => r.items),
  });
  const products = productsQuery.data ?? [];

  const discountTypesQuery = useQuery({
    queryKey: ['setup-discount-types'],
    queryFn: () => setupService.getAll({ setupType: 'DiscountType', pageSize: 50 }).then(r => r.items),
  });
  const discountTypes = discountTypesQuery.data ?? [];

  const currenciesQuery = useQuery({
    queryKey: ['currencies-list', businessUnitId],
    queryFn: () => currencyService.getAll({ pageSize: 100, businessUnitId }).then(r => r.items),
  });
  const currencies = currenciesQuery.data ?? [];
  // Default to the tenant's base currency rather than leaving it unset: unset is the state that
  // produced the defect, and a trader's own currency is the only sane opening guess.
  React.useEffect(() => {
    if (currencyId === null && currencies.length > 0) {
      setCurrencyId((currencies.find(c => c.isBaseCurrency) ?? currencies[0]).id);
    }
  }, [currencies, currencyId]);
  const selectedCurrency = currencies.find(c => c.id === currencyId) ?? null;
  const currencyLabel = selectedCurrency?.symbol || selectedCurrency?.code || '';

  // The business unit's output tax rate, so this screen previews the tax the server will derive
  // instead of showing a flat zero. Null means the tenant has stated no rate — the quote will save
  // but cannot be sent until one is set in Commercial Policy settings.
  const commercialPolicyQuery = useQuery({
    queryKey: ['commercial-policy'],
    queryFn: () => commercialPolicyService.getPolicy(),
  });
  const outputTaxRatePercent = commercialPolicyQuery.data?.outputTaxRatePercent ?? null;

  const referenceQueries = [rfqsQuery, productsQuery, discountTypesQuery, currenciesQuery, commercialPolicyQuery];
  const failedReferenceData = [
    currenciesQuery.isError && 'currencies',
    discountTypesQuery.isError && 'discount types',
    productsQuery.isError && 'products',
    rfqsQuery.isError && 'RFQs',
    commercialPolicyQuery.isError && 'the tax policy',
  ].filter((label): label is string => Boolean(label));

  // Totals. One shared implementation with the server (see quoteTotals.ts): the header discount
  // comes off the tax-EXCLUSIVE net, is allocated across lines pro rata, and each line's tax is
  // derived from what is left. This screen used to take the header discount off an ex-VAT subtotal
  // while the server took it off a VAT-inclusive one, so the rep was shown a total the server never
  // saved, and the VAT stated was the VAT on the pre-discount base.
  const discountKindOf = (id: number | null): DiscountKind => {
    const code = discountTypes.find(t => t.setupId === id)?.setupCode?.toUpperCase();
    return code === 'PERCENTAGE' ? 'PERCENTAGE' : code === 'FIXED' ? 'FIXED' : null;
  };

  const totals = useMemo(() => calculateQuoteTotals(
    items.map(item => ({
      quantity: item.quantity,
      unitPrice: item.unitPrice,
      discountKind: discountKindOf(item.discountTypeId),
      discountValue: item.discountValue,
      taxCategory: 'STANDARD',
    })),
    discountKindOf(discountTypeId),
    discountValue,
    outputTaxRatePercent,
  ), [items, discountTypes, discountTypeId, discountValue, outputTaxRatePercent]);

  /* See EditQuotePage: same 40-line grid, same total loss on a mistaken click, same fix. */
  const guard = useUnsavedWorkGuard({
    storageKey: 'nexora.quote.create',
    value: { rfqId, customerId, quoteDate, validUntil, headerRemarks, discountTypeId, discountValue, items },
    enabled: true,
  });


  // Mutations
  const createMutation = useMutation({
    mutationFn: (data: any) => quoteService.create(data),
    onSuccess: () => {
      toast.success('Quote created successfully');
      guard.markSaved({ rfqId, customerId, quoteDate, validUntil, headerRemarks, discountTypeId, discountValue, items });
      navigate('/sales/quotes');
    },
    // The raw `error?.response?.data` render that used to live here is gone: utils/apiErrors.ts
    // forbids rendering a non-string body (an object prints as "[object Object]"), and the
    // cache-level handler in api/queryClient.ts now presents this failure through that boundary.
  });

  const handleAddItem = () => {
    setItems([...items, {
      productId: null, productName: '', itemDescription: '', quantity: 1, unitPrice: 0, 
      totalAmount: 0, discount: 0, discountTypeId: null, discountValue: 0, 
      taxAmount: 0, deliveryLeadTime: 7
    }]);
  };

  const updateItem = (index: number, fields: Partial<QuoteItem>) => {
    const newItems = [...items];
    const item = { ...newItems[index], ...fields };

    // If product changed, update description and price
    if (fields.productId !== undefined) {
        const prod = products.find(p => p.id === fields.productId);
        if (prod) {
            item.productName = prod.productName || '';
            item.itemDescription = prod.description || prod.productName || '';
            // D5: seed from the SELLING price only. This used to fall back to `unitCost`, so any
            // product without a list price was quoted at cost — a zero-margin quote, with nothing
            // on screen saying so. A blank price the rep has to fill in is the honest failure.
            item.unitPrice = prod.sellingPrice ?? 0;
        }
    }

    // Line tax and line totals are quote-wide now, because the header discount has to be shared
    // out before any line's taxable base is known. See `totals` above.
    newItems[index] = item;
    setItems(newItems);
  };

  const removeItem = (index: number) => {
    setItems(items.filter((_, i) => i !== index));
  };

  const money = (value: number) =>
    `${currencyLabel} ${value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`.trim();

  const handleSubmit = () => {
    if (!currencyId) {
      toast.error('Select the currency this quote is priced in');
      return;
    }
    if (!rfqId) {
      toast.error('Please select the RFQ this quote belongs to');
      return;
    }
    if (!customerId || selectedRfq?.customerId !== customerId) {
      toast.error('The selected RFQ must have a verified customer before a quote can be created');
      return;
    }
    if (items.length === 0) {
      toast.error('Please add at least one item');
      return;
    }

    const payload = {
      customerId, contactId: selectedRfq?.contactId ?? null, rfqId, businessUnitId, quoteDate, validUntil, headerRemarks,
      discountTypeId, discountValue,
      currencyId,
      createdBy: userData?.userName || 'System',
      totalAmount: totals.grandTotal,
      // Every money figure below is recomputed server-side and these are ignored; they are sent
      // only so the request is a complete record of what the rep was looking at.
      quoteItems: items.map((item, index) => ({
        productId: item.productId,
        itemDescription: item.itemDescription || item.productName,
        quantity: item.quantity,
        unitPrice: item.unitPrice,
        totalAmount: totals.lines[index]?.taxableBase ?? 0,
        discountTypeId: item.discountTypeId,
        discountValue: item.discountValue,
        taxAmount: totals.lines[index]?.taxAmount ?? 0,
        deliveryLeadTime: item.deliveryLeadTime
      }))
    };

    createMutation.mutate(payload);
  };

  return (
    <Box sx={{ p: 2, bgcolor: 'background.default', minHeight: '100vh' }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
        <Box>
          <Breadcrumbs sx={{ mb: 0.5 }}>
            <Link component="button" variant="caption" onClick={() => navigate('/sales/quotes')} underline="hover" color="inherit">Quotes</Link>
            <Typography variant="caption" color="text.primary">New Quote</Typography>
          </Breadcrumbs>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <QuoteIcon color="primary" />
            <Typography variant="h5" sx={{ fontWeight: 900 }}>New Sales Quotation</Typography>
          </Stack>
        </Box>
        <Stack direction="row" spacing={1.5}>
          <Button variant="outlined" startIcon={<BackIcon />} size="small"
            onClick={() => {
              if (guard.isDirty
                && !window.confirm('Leave without saving? The lines you have entered on this quote will be lost.')) return;
              navigate('/sales/quotes');
            }}>Cancel</Button>
          <Button 
            variant="contained" 
            startIcon={createMutation.isPending ? <CircularProgress size={20} color="inherit" /> : <SaveIcon />} 
            onClick={handleSubmit}
            disabled={createMutation.isPending}
            size="small"
            sx={{ px: 3, fontWeight: 700 }}
          >
            Create Quote
          </Button>
        </Stack>
      </Stack>

      {failedReferenceData.length > 0 && (
        <Alert
          severity="error"
          sx={{ mb: 2 }}
          action={
            <Button
              color="inherit"
              size="small"
              onClick={() => referenceQueries.filter((query) => query.isError).forEach((query) => { void query.refetch(); })}
            >
              Retry
            </Button>
          }
        >
          {`We couldn't load ${failedReferenceData.join(', ')}. `}
          Fields that depend on the missing reference data are empty and the quote total may be
          wrong, so do not save this quote until it is restored.
        </Alert>
      )}

      <Grid container spacing={2}>
        <Grid size={{ xs: 12, lg: 9 }}>
          {/* Header Info - More Compact */}
          <Paper sx={{ p: 2, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', mb: 2 }}>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 4 }}>
                <Autocomplete
                  size="small"
                  options={rfqs}
                  getOptionLabel={(o) => o.rfqno || `RFQ-${o.id}`}
                  value={rfqs.find(r => r.id === rfqId) || null}
                  onChange={(_, v) => {
                    setRfqId(v?.id || null);
                    setCustomerId(v?.customerId || null);
                  }}
                  renderInput={(params) => <TextField {...params} label="Reference RFQ" required />}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField
                  fullWidth
                  size="small"
                  label="Customer from RFQ"
                  value={selectedRfq?.customerName || 'Customer unresolved'}
                  disabled
                  // Three distinct states, because "no RFQ chosen yet" and "the chosen RFQ has no
                  // commercial case" are different problems and only the second blocks the quote.
                  helperText={selectedRfq?.nexoraSerial
                    ? `Nexora Serial: ${selectedRfq.nexoraSerial}`
                    : selectedRfq
                      ? 'This RFQ is not linked to a commercial case, so a quotation cannot inherit one from it.'
                      : 'Select an RFQ to preserve commercial identity'}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField
                  fullWidth
                  size="small"
                  label="Contact from RFQ"
                  value={selectedRfq?.contactName || selectedRfq?.customerEmail || 'Contact unresolved'}
                  disabled
                />
              </Grid>
              {selectedRfq && !selectedRfq.customerId && (
                <Grid size={{ xs: 12 }}>
                  <Alert severity="warning">This RFQ has no verified customer. Resolve the customer on the RFQ before preparing a quote.</Alert>
                </Grid>
              )}
              <Grid size={{ xs: 12, md: 2 }}>
                <TextField fullWidth type="date" label="Date" size="small" value={quoteDate} onChange={(e) => setQuoteDate(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
              </Grid>
              <Grid size={{ xs: 12, md: 2 }}>
                <TextField fullWidth type="date" label="Valid Until" size="small" value={validUntil} onChange={(e) => setValidUntil(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
              </Grid>
              <Grid size={{ xs: 12, md: 2 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>Currency</InputLabel>
                  <Select value={currencyId ?? ''} label="Currency" onChange={(e) => setCurrencyId(Number(e.target.value) || null)}>
                    {currencies.map(c => (
                      <MenuItem key={c.id} value={c.id}>{c.code}{c.isBaseCurrency ? ' (base)' : ''}</MenuItem>
                    ))}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField fullWidth label="Header Remarks / Terms" size="small" value={headerRemarks} onChange={(e) => setHeaderRemarks(e.target.value)} />
              </Grid>
              <Grid size={{ xs: 12, md: 2 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>Disc. Type</InputLabel>
                  <Select value={discountTypeId || ''} label="Disc. Type" onChange={(e) => setDiscountTypeId(Number(e.target.value) || null)}>
                    <MenuItem value="">None</MenuItem>
                    {discountTypes.map(t => (
                      <MenuItem key={t.setupId} value={t.setupId}>{t.setupCode}</MenuItem>
                    ))}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12, md: 2 }}>
                <TextField 
                  fullWidth type="number" label="Disc. Val" size="small" value={discountValue} onChange={(e) => setDiscountValue(Number(e.target.value))}
                  slotProps={{ input: { endAdornment: <InputAdornment position="end">{discountTypes.find(t => t.setupId === discountTypeId)?.setupCode === 'PERCENTAGE' ? '%' : currencyLabel}</InputAdornment> } }}
                />
              </Grid>
            </Grid>
          </Paper>

          {/* Line Items - More compact table */}
          <Paper sx={{ p: 0, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden' }}>
            <Box sx={{ p: 1.5, borderBottom: '1px solid', borderColor: 'divider', display: 'flex', justifyContent: 'space-between', alignItems: 'center', bgcolor: 'grey.50' }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>LINE ITEMS ({items.length})</Typography>
              <Button startIcon={<AddIcon />} variant="contained" onClick={handleAddItem} size="small" sx={{ borderRadius: 1.5, textTransform: 'none' }}>Add Product</Button>
            </Box>
            <Table size="small">
              <TableHead>
                <TableRow sx={{ bgcolor: 'grey.100' }}>
                  <TableCell sx={{ fontWeight: 800, width: '25%' }}>Product</TableCell>
                  <TableCell sx={{ fontWeight: 800 }}>Description</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 80 }} align="center">Qty</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 110 }} align="center">Price</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 100 }} align="center">Disc</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 100 }} align="center">Total</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 50 }} align="center"></TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {items.length === 0 && (
                  <TableRow><TableCell colSpan={7} align="center" sx={{ py: 3 }}><Typography color="text.secondary" variant="body2">No items added. Click 'Add Product' to start.</Typography></TableCell></TableRow>
                )}
                {items.map((item, index) => (
                  <TableRow key={index} sx={{ '&:hover': { bgcolor: 'grey.50' } }}>
                    <TableCell>
                      <Autocomplete
                        size="small"
                        options={products}
                        getOptionLabel={(o) => o.productName || ''}
                        value={products.find(p => p.id === item.productId) || null}
                        onChange={(_, v) => updateItem(index, { productId: v?.id || null })}
                        renderInput={(params) => <TextField {...params} placeholder="Select Product" variant="standard" />}
                      />
                    </TableCell>
                    <TableCell>
                      <TextField fullWidth size="small" variant="standard" value={item.itemDescription} onChange={(e) => updateItem(index, { itemDescription: e.target.value })} />
                    </TableCell>
                    <TableCell align="center">
                      <TextField type="number" size="small" variant="standard" sx={{ width: 60 }} value={item.quantity} onChange={(e) => updateItem(index, { quantity: Number(e.target.value) })} />
                    </TableCell>
                    <TableCell align="center">
                      <TextField 
                        type="number" size="small" variant="standard" sx={{ width: 90 }} 
                        slotProps={{ input: { startAdornment: <Typography variant="caption" sx={{ mr: 0.5 }}>{currencyLabel}</Typography> } }} 
                        value={item.unitPrice} onChange={(e) => updateItem(index, { unitPrice: Number(e.target.value) })} 
                      />
                    </TableCell>
                    <TableCell align="center">
                        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
                            <Select 
                                size="small" variant="standard" sx={{ fontSize: '0.75rem', width: 45 }} 
                                value={item.discountTypeId || ''} 
                                onChange={(e) => updateItem(index, { discountTypeId: Number(e.target.value) || null })}
                            >
                                <MenuItem value=""><Typography variant="caption">N/A</Typography></MenuItem>
                                {discountTypes.map(t => <MenuItem key={t.setupId} value={t.setupId}><Typography variant="caption">{t.setupCode === 'PERCENTAGE' ? '%' : currencyLabel}</Typography></MenuItem>)}
                            </Select>
                            <TextField 
                                type="number" size="small" variant="standard" sx={{ width: 40 }} 
                                value={item.discountValue} onChange={(e) => updateItem(index, { discountValue: Number(e.target.value) })} 
                            />
                        </Stack>
                    </TableCell>
                    <TableCell align="center">
                      <Typography sx={{ fontWeight: 700, fontSize: '0.875rem' }}>{money(totals.lines[index]?.net ?? 0)}</Typography>
                    </TableCell>
                    <TableCell align="center">
                      <IconButton color="error" size="small" onClick={() => removeItem(index)}><DeleteIcon fontSize="small" /></IconButton>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, lg: 3 }}>
          <Card sx={{ borderRadius: 2, border: '1px solid', borderColor: 'primary.light', bgcolor: 'primary.lighter', boxShadow: 'none', position: 'sticky', top: 16 }}>
            <CardContent sx={{ p: 2 }}>
              <Typography variant="subtitle1" sx={{ fontWeight: 800, mb: 1.5, color: 'primary.dark' }}>Summary</Typography>
              <Stack spacing={1.5}>
                {/* Reads top to bottom as the arithmetic itself, and in the same order as the
                    printed quotation: gross, what comes off it, the net VAT is charged on, the VAT,
                    the total. "Total excl. VAT" is the sum of the line column. */}
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                  <Typography variant="body2" color="text.secondary">Gross Total</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>{money(totals.grossSubTotal)}</Typography>
                </Box>
                {totals.totalLineDiscounts > 0 && (
                  <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                    <Typography variant="body2" color="error">Item Discounts</Typography>
                    <Typography variant="body2" color="error" sx={{ fontWeight: 700 }}>- {money(totals.totalLineDiscounts)}</Typography>
                  </Box>
                )}
                {totals.headerDiscount > 0 && (
                  <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                    <Typography variant="body2" color="error">Addit. Discount</Typography>
                    <Typography variant="body2" color="error" sx={{ fontWeight: 700 }}>- {money(totals.headerDiscount)}</Typography>
                  </Box>
                )}
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                  <Typography variant="body2" color="text.secondary">Total excl. VAT</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>{money(totals.netExcludingTax)}</Typography>
                </Box>
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                  <Typography variant="body2" color="text.secondary">
                    VAT{outputTaxRatePercent !== null ? ` ${outputTaxRatePercent}%` : ''}
                  </Typography>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>{money(totals.totalTax)}</Typography>
                </Box>
                <Divider />
                <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <Typography variant="h6" sx={{ fontWeight: 900 }}>Total</Typography>
                  <Typography variant="h5" sx={{ fontWeight: 900, color: 'primary.main' }}>
                    {money(totals.grandTotal)}
                  </Typography>
                </Box>
              </Stack>
              {/* The send gate refuses a standard-rated line with no derived tax, so say so here
                  rather than letting the rep find out after the quote is written. */}
              {totals.hasUnderivedTax && (
                <Alert severity="warning" sx={{ mt: 1.5 }}>
                  No output tax rate is configured for this business unit, so VAT cannot be
                  calculated and this quote cannot be sent. Set it in Setup &rarr; Commercial Policy.
                </Alert>
              )}
            </CardContent>
          </Card>

          {/* WP-B2: "This customer" history — win rate + last-sold prices. */}
          <CustomerContextPanel customerId={customerId} />
        </Grid>
      </Grid>
    </Box>
  );
};

export default CreateQuotePage;
