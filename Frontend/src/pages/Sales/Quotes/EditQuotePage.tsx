import React, { useState, useEffect, useMemo } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery, useMutation } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Grid, Stack, Button, TextField,
  Autocomplete, IconButton, Divider, Table, TableHead,
  TableRow, TableCell, TableBody, InputAdornment, Card, CardContent,
  CircularProgress, Breadcrumbs, Link, MenuItem, Select, FormControl, InputLabel, Alert, AlertTitle
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Save as SaveIcon,
  Add as AddIcon,
  Delete as DeleteIcon,
  Edit as EditIcon,
} from '@mui/icons-material';
import { useAuth } from '../../../context/AuthContext';
import { useUnsavedWorkGuard } from '../../../hooks/useUnsavedWorkGuard';
import customerService from '../../../api/services/customerService';
import quoteService from '../../../api/services/quoteService';
import setupService from '../../../api/services/setupService';
import productService from '../../../api/services/productService';
import CustomerContextPanel from './CustomerContextPanel';
import commercialPolicyService from '../../../api/services/commercialPolicyService';
import {
  TAX_CATEGORIES, TAX_CATEGORY_STANDARD, taxCategoryLabel, taxCategoryRequiresReason,
} from '../../../constants/taxCategories';
import { toast } from 'react-hot-toast';
import { calculateQuoteTotals, type DiscountKind } from './quoteTotals';
import { formatMoney } from '../../../utils/currency';

interface QuoteItem {
  id?: number;
  productId: number | null;
  productName: string;
  itemDescription: string;
  quantity: number;
  // Read-only carriers from the source RFQ line: shown, never edited here, and
  // echoed back on save so an edit round-trip cannot strip them.
  unitOfMeasure?: string | null;
  customerLineRef?: string | null;
  unitPrice: number;
  totalAmount: number;
  discount: number;
  discountTypeId: number | null;
  discountValue: number;
  // Server-DERIVED (R17). Displayed, never edited: the rate comes from Commercial Policy settings
  // and anything typed here is discarded on save. taxRatePercentApplied is null when the tax has
  // never been derived, which is a different state from "derived to zero" and is what blocks the
  // send — so the grid shows those two differently.
  taxAmount: number;
  taxRatePercentApplied: number | null;
  // The user's own statement of how the supply is taxed (R19), and the evidence for it.
  taxCategory: string;
  taxCategoryReason: string;
  deliveryLeadTime: number;
  isDeleted?: boolean;
}

const EditQuotePage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;

  // Form State
  const [quoteNo, setQuoteNo] = useState('');
  const [customerId, setCustomerId] = useState<number | null>(null);
  const [quoteDate, setQuoteDate] = useState('');
  const [validUntil, setValidUntil] = useState('');
  const [headerRemarks, setHeaderRemarks] = useState('');
  const [statusId, setStatusId] = useState<number | null>(null);
  const [, setStatusValue] = useState('');
  
  // Header Discount
  const [discountTypeId, setDiscountTypeId] = useState<number | null>(null);
  const [discountValue, setDiscountValue] = useState<number>(0);
  /**
   * The currency the quote is denominated in, read from the record.
   *
   * This screen printed a literal "$" on the unit price, both discount adornments and every row of
   * the summary, while the quote it was editing was stored with a CurrencyId — so a SAR quote was
   * edited in dollars. There is no fallback here on purpose: `formatMoney` renders a bare number
   * when the record carries no currency, which is the honest output. Inventing a symbol is the
   * defect, not the absence of one.
   */
  const [currencyCode, setCurrencyCode] = useState<string | null>(null);

  const [items, setItems] = useState<QuoteItem[]>([]);

  // Queries
  const { data: discountTypes = [] } = useQuery({
    queryKey: ['setup-discount-types'],
    queryFn: () => setupService.getAll({ setupType: 'DiscountType', pageSize: 50 }).then(r => r.items),
  });

  const { data: products = [] } = useQuery({
    queryKey: ['products-list', businessUnitId],
    queryFn: () => productService.getAll({ pageSize: 200, businessUnitId }).then(r => r.items),
  });

  const { data: quote, isLoading: isLoadingQuote, isError: isQuoteError } = useQuery({
    queryKey: ['quote-edit', id],
    queryFn: () => quoteService.getById(Number(id), businessUnitId),
    enabled: !!id
  });

  useEffect(() => {
    if (quote) {
      if (quote.statusValue?.toUpperCase() === 'ORDERED') {
          toast.error('Locked: This quote has already been converted to an order and cannot be edited.');
          navigate(`/sales/quotes/view/${id}`);
          return;
      }
      setQuoteNo(quote.quoteNo);
      setCustomerId(quote.customerId || null);
      setQuoteDate(quote.quoteDate ? quote.quoteDate.split('T')[0] : '');
      setValidUntil(quote.validUntil ? quote.validUntil.split('T')[0] : '');
      setHeaderRemarks(quote.headerRemarks || '');
      setStatusId(quote.statusId || null);
      setStatusValue(quote.statusValue || '');
      setDiscountTypeId(quote.discountTypeId || null);
      setDiscountValue(quote.discountValue || 0);
      // The quote's OWN currency. Every amount on this screen is denominated in it; there is no
      // house default, and a quote with none renders a bare number rather than an invented symbol.
      setCurrencyCode(quote.currencyCode ?? null);
      setItems(quote.quoteItems.map(i => ({
        id: i.id,
        productId: i.productId ?? null,
        productName: i.productName || '',
        itemDescription: i.itemDescription || '',
        quantity: i.quantity,
        unitOfMeasure: i.unitOfMeasure || null,
        customerLineRef: i.customerLineRef || null,
        unitPrice: i.unitPrice,
        totalAmount: i.totalAmount,
        discount: i.discount || 0,
        discountTypeId: i.discountTypeId || null,
        discountValue: i.discountValue || 0,
        taxAmount: i.taxAmount || 0,
        taxRatePercentApplied: i.taxRatePercentApplied ?? null,
        taxCategory: i.taxCategory || TAX_CATEGORY_STANDARD,
        taxCategoryReason: i.taxCategoryReason || '',
        deliveryLeadTime: i.deliveryLeadTime || 7
      })));
    }
  }, [quote]);

  const { data: customers = [] } = useQuery({
    queryKey: ['customers-list', businessUnitId],
    queryFn: () => customerService.getAll({ pageSize: 100 }).then(r => r.items),
  });

  // The business unit's output tax rate, so the grid can PREVIEW the tax the server will derive on
  // save rather than showing a figure that went stale the moment a price was edited. The server
  // remains the authority — nothing typed or previewed here is persisted as the tax.
  const { data: commercialPolicy } = useQuery({
    queryKey: ['commercial-policy'],
    queryFn: () => commercialPolicyService.getPolicy(),
  });
  const outputTaxRatePercent = commercialPolicy?.outputTaxRatePercent ?? null;

  // Totals. Same shared implementation as the create screen and the server (quoteTotals.ts): the
  // header discount comes off the tax-EXCLUSIVE net, is allocated across lines pro rata, and each
  // line's tax follows the discounted base. This page previously previewed a header discount taken
  // on an ex-VAT subtotal while the server took it on a VAT-inclusive one, and summed each line's
  // tax from BEFORE the header discount — so it showed a total the server never saved and a VAT
  // figure larger than the one that was due.
  const liveItems = useMemo(() => items.filter(i => !i.isDeleted), [items]);

  const discountKindOf = (id: number | null): DiscountKind => {
    const code = discountTypes.find(t => t.setupId === id)?.setupCode?.toUpperCase();
    return code === 'PERCENTAGE' ? 'PERCENTAGE' : code === 'FIXED' ? 'FIXED' : null;
  };

  const totals = useMemo(() => calculateQuoteTotals(
    liveItems.map(item => ({
      quantity: item.quantity,
      unitPrice: item.unitPrice,
      discountKind: discountKindOf(item.discountTypeId),
      discountValue: item.discountValue,
      taxCategory: item.taxCategory,
    })),
    discountKindOf(discountTypeId),
    discountValue,
    outputTaxRatePercent,
  ), [liveItems, discountTypes, discountTypeId, discountValue, outputTaxRatePercent]);

  /*
   * Unsaved-work protection. This screen is where a rep spends 25 minutes pricing 40 lines, and
   * it had none: the button below labelled "Discard" navigated away on click with no prompt, and
   * every line lived in useState, so a mistaken sidebar click lost all of it silently. The one
   * page in the product that did protect this (ExtractionReviewDetailPage) says why in its own
   * comment: "A reviewer who loses twenty minutes of corrections once goes back to Excel
   * permanently."
   */
  const guard = useUnsavedWorkGuard({
    storageKey: id ? `nexora.quote.edit.${id}` : '',
    value: { quoteNo, customerId, quoteDate, validUntil, headerRemarks, discountTypeId, discountValue, items },
    enabled: Boolean(quote),
    leaveMessage: 'Leave without saving? The pricing you have entered on this quote will be lost.',
  });
  /** Put a recovered draft back on the form — every field the guard stores, nothing invented. */
  const restoreDraft = (draft: typeof guard.recoveredDraft) => {
    if (!draft) return;
    const v = draft.value;
    if (v.quoteNo) setQuoteNo(v.quoteNo);
    setCustomerId(v.customerId ?? null);
    if (v.quoteDate) setQuoteDate(v.quoteDate);
    if (v.validUntil) setValidUntil(v.validUntil);
    setHeaderRemarks(v.headerRemarks ?? '');
    setDiscountTypeId(v.discountTypeId ?? null);
    setDiscountValue(v.discountValue ?? 0);
    setItems(Array.isArray(v.items) ? v.items : []);
    guard.acceptRecovered();
  };


  // The render loop walks `items` (deleted rows included, hidden); `totals.lines` is indexed over
  // the live rows only. This maps one to the other so a line never reads another line's figures.
  const pricedByItemIndex = useMemo(() => {
    const map = new Map<number, (typeof totals.lines)[number]>();
    let live = 0;
    items.forEach((item, index) => {
      if (!item.isDeleted) map.set(index, totals.lines[live++]);
    });
    return map;
  }, [items, totals]);

  const subtotal = totals.grossSubTotal - totals.totalLineDiscounts;
  const totalTax = totals.totalTax;
  const calculatedHeaderDiscount = totals.headerDiscount;
  const grandTotal = totals.grandTotal;

  const updateMutation = useMutation({
    mutationFn: (data: any) => quoteService.update(Number(id), data),
    onSuccess: () => {
      toast.success('Quote updated successfully');
      // Re-baseline before navigating, so the saved work is not still offered as a stray draft.
      guard.markSaved({ quoteNo, customerId, quoteDate, validUntil, headerRemarks, discountTypeId, discountValue, items });
      navigate(`/sales/quotes/view/${id}`);
    },
  });

  const handleAddItem = () => {
    setItems([...items, {
      productId: null, productName: '', itemDescription: '', quantity: 1, unitPrice: 0, totalAmount: 0,
      discount: 0, discountTypeId: null, discountValue: 0,
      // A brand-new line has no derived tax until the server computes one on save.
      taxAmount: 0, taxRatePercentApplied: null, taxCategory: TAX_CATEGORY_STANDARD, taxCategoryReason: '',
      deliveryLeadTime: 7
    }]);
  };

  const updateItem = (index: number, fields: Partial<QuoteItem>) => {
    const newItems = [...items];
    const item = { ...newItems[index], ...fields };
    
    if (fields.productId !== undefined) {
        const prod = products.find(p => p.id === fields.productId);
        if (prod) {
            item.productName = prod.productName || '';
            item.itemDescription = prod.description || prod.productName || '';
            // D5: seed from the SELLING price only. Falling back to `unitCost` quoted any product
            // without a list price at cost — a zero-margin line with nothing on screen saying so.
            item.unitPrice = prod.sellingPrice ?? 0;
        }
    }

    const itemRawTotal = item.quantity * item.unitPrice;
    let itemDiscount = 0;
    const type = discountTypes.find(t => t.setupId === item.discountTypeId);
    if (type) {
      if (type.setupCode === 'PERCENTAGE') itemDiscount = itemRawTotal * (item.discountValue / 100);
      else if (type.setupCode === 'FIXED') itemDiscount = item.discountValue;
    }
    item.totalAmount = itemRawTotal - itemDiscount;
    item.discount = itemDiscount;

    // Tax is no longer derived per line here: the header discount has to be shared across the
    // whole quote before any line's taxable base is known. `totals` above owns that, and the
    // server recomputes all of it on save regardless.
    const effectiveRate = item.taxCategory === TAX_CATEGORY_STANDARD ? outputTaxRatePercent : 0;
    item.taxRatePercentApplied = effectiveRate;
    // A line that goes back to standard rated carries no reason to state.
    if (!taxCategoryRequiresReason(item.taxCategory)) item.taxCategoryReason = '';

    newItems[index] = item;
    setItems(newItems);
  };

  const removeItem = (index: number) => {
    const newItems = [...items];
    if (newItems[index].id) {
      newItems[index].isDeleted = true;
      setItems(newItems);
    } else {
      setItems(items.filter((_, i) => i !== index));
    }
  };

  const handleSubmit = () => {
    if (!customerId) {
      toast.error('Please select a customer');
      return;
    }
    // R19: the server refuses this too, but failing here keeps the rep in the grid where the
    // offending line is, instead of bouncing them off a save with a sentence about a line number.
    const missingReason = items.find(i => !i.isDeleted
      && taxCategoryRequiresReason(i.taxCategory) && !i.taxCategoryReason.trim());
    if (missingReason) {
      toast.error(`Line ${missingReason.customerLineRef || missingReason.itemDescription || ''} is `
        + `${taxCategoryLabel(missingReason.taxCategory)} — say why it is not taxed at the standard rate.`);
      return;
    }
    const payload = {
      id: Number(id), quoteNo, customerId,
      // Empty date inputs must go over the wire as null, not "".
      //
      // A quote whose validUntil is null on the server loads into this form as '' (line ~107),
      // and '' round-tripped back is not a DateTime?: ASP.NET fails to bind the WHOLE request,
      // so the 400 reads "The request field is required" and the record cannot be saved AT ALL.
      // Found on the live tenant, where quote QT-0826-0002 carries validUntil = null and was
      // therefore permanently unsaveable from this screen.
      quoteDate: quoteDate || null,
      validUntil: validUntil || null,
      // Round-trip the currency the record already has. The server now treats an absent
      // CurrencyId as "not supplied" rather than "clear it", so this is belt AND braces: the
      // payload states the truth, and the server no longer destroys it if some future caller
      // forgets to. This screen deliberately offers no way to CHANGE the currency.
      currencyId: quote?.currencyId ?? null,
      headerRemarks,
      discountTypeId, discountValue, statusId,
      modifiedBy: userData?.userName || 'System',
      totalAmount: grandTotal,
      quoteItems: items.map((item, index) => ({
        id: item.id, productId: item.productId, itemDescription: item.itemDescription || item.productName,
        quantity: item.quantity, unitPrice: item.unitPrice,
        totalAmount: pricedByItemIndex.get(index)?.taxableBase ?? 0,
        unitOfMeasure: item.unitOfMeasure || null, customerLineRef: item.customerLineRef || null,
        discountTypeId: item.discountTypeId, discountValue: item.discountValue,
        // taxAmount is sent for wire compatibility only — the server re-derives it and discards
        // whatever arrives here. The category and its reason ARE the user's input.
        taxAmount: pricedByItemIndex.get(index)?.taxAmount ?? 0,
        taxCategory: item.taxCategory,
        taxCategoryReason: item.taxCategoryReason.trim() || null,
        deliveryLeadTime: item.deliveryLeadTime,
        isDeleted: item.isDeleted || false
      }))
    };
    updateMutation.mutate(payload);
  };

  if (isLoadingQuote) return <Box sx={{ p: 4, display: 'flex', justifyContent: 'center' }}><CircularProgress /></Box>;

  // A failed load used to fall through to the form below, which then rendered an empty,
  // fully editable quote titled "Edit: " with a live Update button — an edit screen for a
  // record that does not exist. Say so instead, and offer the only action that can work.
  if (isQuoteError || !quote) {
    return (
      <Box sx={{ p: 4 }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 1 }}>We couldn&apos;t load this quote.</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          It may have been removed, or it belongs to another workspace. Nothing was changed.
        </Typography>
        <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate('/sales/quotes')}>
          Back to quotes
        </Button>
      </Box>
    );
  }

  return (
    <Box sx={{ p: 2, bgcolor: 'background.default', minHeight: '100vh' }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
        <Box>
          <Breadcrumbs sx={{ mb: 0.5 }}>
            <Link component="button" variant="caption" onClick={() => navigate('/sales/quotes')} underline="hover" color="inherit">Quotes</Link>
            <Typography variant="caption" color="text.primary">Edit: {quoteNo}</Typography>
          </Breadcrumbs>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <EditIcon color="primary" />
            <Typography variant="h5" sx={{ fontWeight: 900 }}>Update Quote</Typography>
          </Stack>
        </Box>
        <Stack direction="row" spacing={1.5}>
          <Button variant="outlined" startIcon={<BackIcon />} size="small"
            onClick={() => {
              // "Discard" sat next to Save and threw the work away on a single click. It now says
              // what it does, and asks first when there is something to lose.
              if (guard.isDirty
                && !window.confirm('Leave without saving? The pricing you have entered on this quote will be lost.')) return;
              navigate(`/sales/quotes/view/${id}`);
            }}>Cancel</Button>
          <Button 
            variant="contained" 
            startIcon={updateMutation.isPending ? <CircularProgress size={20} color="inherit" /> : <SaveIcon />} 
            onClick={handleSubmit}
            disabled={updateMutation.isPending}
            size="small"
            sx={{ px: 3, fontWeight: 700 }}
          >
            Update Quote
          </Button>
        </Stack>
      </Stack>

      {/* The guard has written this draft to sessionStorage since the day it was added; this page
          never read it back. Same banner as the lead decision workbench, the one screen that did. */}
      {guard.recoveredDraft && (
        <Alert
          severity="warning"
          sx={{ mb: 2 }}
          action={(
            <Stack direction="row" spacing={1}>
              <Button color="inherit" onClick={() => restoreDraft(guard.recoveredDraft)}>Restore</Button>
              <Button color="inherit" onClick={guard.discardRecovered}>Discard</Button>
            </Stack>
          )}
        >
          <AlertTitle>Unsaved pricing recovered</AlertTitle>
          Restore the changes to {quoteNo || 'this quote'} saved in this browser
          {guard.recoveredDraft.savedAt ? ` (saved ${new Date(guard.recoveredDraft.savedAt).toLocaleString()})` : ''},
          or discard them and keep the saved version.
        </Alert>
      )}

      <Grid container spacing={2}>
        <Grid size={{ xs: 12, lg: 9 }}>
          <Paper sx={{ p: 2, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', mb: 2 }}>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField fullWidth label="Quote No" size="small" value={quoteNo} onChange={(e) => setQuoteNo(e.target.value)} required />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <Autocomplete
                  size="small"
                  options={customers}
                  getOptionLabel={(o) => o.name}
                  value={customers.find(c => c.id === customerId) || null}
                  onChange={(_, v) => setCustomerId(v?.id || null)}
                  renderInput={(params) => <TextField {...params} label="Customer" required />}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 2 }}>
                <TextField fullWidth type="date" label="Date" size="small" value={quoteDate} onChange={(e) => setQuoteDate(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
              </Grid>
              <Grid size={{ xs: 12, md: 2 }}>
                <TextField fullWidth type="date" label="Valid Until" size="small" value={validUntil} onChange={(e) => setValidUntil(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
              </Grid>
              <Grid size={{ xs: 12, md: 8 }}>
                <TextField fullWidth label="Remarks / Terms" size="small" value={headerRemarks} onChange={(e) => setHeaderRemarks(e.target.value)} />
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
                  slotProps={{ input: { endAdornment: <InputAdornment position="end">{discountTypes.find(t => t.setupId === discountTypeId)?.setupCode === 'PERCENTAGE' ? '%' : (currencyCode ?? '')}</InputAdornment> } }}
                />
              </Grid>
            </Grid>
          </Paper>

          <Paper sx={{ p: 0, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden' }}>
             <Box sx={{ p: 1.5, borderBottom: '1px solid', borderColor: 'divider', display: 'flex', justifyContent: 'space-between', alignItems: 'center', bgcolor: 'grey.50' }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>LINE ITEMS ({items.filter(i => !i.isDeleted).length})</Typography>
              <Button startIcon={<AddIcon />} variant="contained" onClick={handleAddItem} size="small" sx={{ borderRadius: 1.5, textTransform: 'none' }}>Add Product</Button>
            </Box>
            <Table size="small">
              <TableHead>
                <TableRow sx={{ bgcolor: 'grey.100' }}>
                  <TableCell sx={{ fontWeight: 800, width: 70 }}>Ref</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: '25%' }}>Product</TableCell>
                  <TableCell sx={{ fontWeight: 800 }}>Description</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 80 }} align="center">Qty</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 60 }} align="center">UOM</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 110 }} align="center">Price</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 100 }} align="center">Disc</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 190 }} align="center">Tax treatment</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 100 }} align="center">Total</TableCell>
                  <TableCell sx={{ fontWeight: 800, width: 50 }} align="center"></TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {items.filter(i => !i.isDeleted).map((item, index) => (
                  <TableRow key={index} sx={{ '&:hover': { bgcolor: 'grey.50' } }}>
                    {/* Read-only: the buyer's own line reference from their RFQ */}
                    <TableCell>
                      <Typography variant="body2" sx={{ fontFamily: 'monospace' }}>{item.customerLineRef || '—'}</Typography>
                    </TableCell>
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
                    {/* Read-only: the unit the quantity is quoted in, carried from the RFQ line */}
                    <TableCell align="center">
                      <Typography variant="body2">{item.unitOfMeasure || '—'}</Typography>
                    </TableCell>
                    <TableCell align="center">
                      <TextField 
                        type="number" size="small" variant="standard" sx={{ width: 90 }} 
                        slotProps={{ input: { startAdornment: <Typography variant="caption" sx={{ mr: 0.5 }}>{currencyCode ?? ''}</Typography> } }} 
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
                                {discountTypes.map(t => <MenuItem key={t.setupId} value={t.setupId}><Typography variant="caption">{t.setupCode === 'PERCENTAGE' ? '%' : (currencyCode ?? 'AMT')}</Typography></MenuItem>)}
                            </Select>
                            <TextField 
                                type="number" size="small" variant="standard" sx={{ width: 40 }} 
                                value={item.discountValue} onChange={(e) => updateItem(index, { discountValue: Number(e.target.value) })} 
                            />
                        </Stack>
                    </TableCell>
                    {/* R17/R19. The category is the rep's decision; the amount beneath it is the
                        server's derivation and is never editable here. "Not calculated yet" is a
                        real state, distinct from a derived zero, and it blocks the send. */}
                    <TableCell align="center">
                      <Stack spacing={0.25}>
                        <Select
                          size="small" variant="standard" sx={{ fontSize: '0.75rem' }}
                          value={item.taxCategory}
                          onChange={(e) => updateItem(index, { taxCategory: String(e.target.value) })}
                        >
                          {TAX_CATEGORIES.map(option => (
                            <MenuItem key={option.code} value={option.code}>
                              <Typography variant="caption">{option.label}</Typography>
                            </MenuItem>
                          ))}
                        </Select>
                        <Typography variant="caption" color={pricedByItemIndex.get(index)?.taxAmount === null ? 'warning.main' : 'text.secondary'}>
                          {pricedByItemIndex.get(index)?.taxAmount === null
                            ? 'No output tax rate configured — this quote cannot be sent'
                            : `${(pricedByItemIndex.get(index)?.taxAmount ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2 })} @ ${item.taxCategory === TAX_CATEGORY_STANDARD ? outputTaxRatePercent : 0}%`}
                        </Typography>
                        {taxCategoryRequiresReason(item.taxCategory) && (
                          <TextField
                            size="small" variant="standard" placeholder="Why not standard rated?"
                            value={item.taxCategoryReason}
                            error={!item.taxCategoryReason.trim()}
                            onChange={(e) => updateItem(index, { taxCategoryReason: e.target.value.slice(0, 500) })}
                            slotProps={{ htmlInput: { style: { fontSize: '0.75rem' } } }}
                          />
                        )}
                      </Stack>
                    </TableCell>
                    <TableCell align="center">
                      <Typography sx={{ fontWeight: 700, fontSize: '0.875rem' }}>{(pricedByItemIndex.get(index)?.net ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2 })}</Typography>
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
              <Typography variant="subtitle1" sx={{ fontWeight: 800, mb: 1.5, color: 'primary.dark' }}>Revised Summary</Typography>
              <Stack spacing={1.5}>
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                  <Typography variant="body2" color="text.secondary">Gross Total</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>{formatMoney(subtotal, currencyCode)}</Typography>
                </Box>
                {calculatedHeaderDiscount > 0 && (
                  <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                    <Typography variant="body2" color="error">Addit. Discount</Typography>
                    <Typography variant="body2" color="error" sx={{ fontWeight: 700 }}>- {formatMoney(calculatedHeaderDiscount, currencyCode)}</Typography>
                  </Box>
                )}
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                  <Typography variant="body2" color="text.secondary">Tax Amount</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>{formatMoney(totalTax, currencyCode)}</Typography>
                </Box>
                <Divider />
                <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <Typography variant="h6" sx={{ fontWeight: 900 }}>Total</Typography>
                  <Typography variant="h5" sx={{ fontWeight: 900, color: 'primary.main' }}>
                    {formatMoney(grandTotal, currencyCode)}
                  </Typography>
                </Box>
              </Stack>
            </CardContent>
          </Card>

          {/* WP-B2: "This customer" history — win rate + last-sold prices. */}
          <CustomerContextPanel customerId={customerId} />
        </Grid>
      </Grid>
    </Box>
  );
};

export default EditQuotePage;
