import React, { useState, useEffect, useMemo } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery, useMutation } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Grid, Stack, Button, TextField,
  Autocomplete, IconButton, Divider, Table, TableHead,
  TableRow, TableCell, TableBody, InputAdornment, Card, CardContent,
  CircularProgress, Breadcrumbs, Link, MenuItem, Select, FormControl, InputLabel
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Save as SaveIcon,
  Add as AddIcon,
  Delete as DeleteIcon,
  Edit as EditIcon,
} from '@mui/icons-material';
import { useAuth } from '../../../context/AuthContext';
import customerService from '../../../api/services/customerService';
import quoteService from '../../../api/services/quoteService';
import setupService from '../../../api/services/setupService';
import productService from '../../../api/services/productService';
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

  const { data: quote, isLoading: isLoadingQuote } = useQuery({
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
      setItems(quote.quoteItems.map(i => ({
        id: i.id,
        productId: i.productId,
        productName: i.productName || '',
        itemDescription: i.itemDescription || '',
        quantity: i.quantity,
        unitPrice: i.unitPrice,
        totalAmount: i.totalAmount,
        discount: i.discount || 0,
        discountTypeId: i.discountTypeId || null,
        discountValue: i.discountValue || 0,
        taxAmount: i.taxAmount || 0,
        deliveryLeadTime: i.deliveryLeadTime || 7
      })));
    }
  }, [quote]);

  const { data: customers = [] } = useQuery({
    queryKey: ['customers-list', businessUnitId],
    queryFn: () => customerService.getAll({ pageSize: 100, businessUnitId }).then(r => r.items),
  });

  // Calculate Totals
  const subtotal = useMemo(() => {
    return items.filter(i => !i.isDeleted).reduce((sum, item) => {
      const itemTotal = item.quantity * item.unitPrice;
      let itemDiscount = 0;
      const type = discountTypes.find(t => t.setupId === item.discountTypeId);
      if (type) {
        if (type.setupCode === 'PERCENTAGE') itemDiscount = itemTotal * (item.discountValue / 100);
        else if (type.setupCode === 'FIXED') itemDiscount = item.discountValue;
      }
      return sum + (itemTotal - itemDiscount);
    }, 0);
  }, [items, discountTypes]);

  const totalTax = useMemo(() => items.filter(i => !i.isDeleted).reduce((sum, item) => sum + (item.taxAmount || 0), 0), [items]);
  
  const calculatedHeaderDiscount = useMemo(() => {
    const type = discountTypes.find(t => t.setupId === discountTypeId);
    if (!type) return 0;
    if (type.setupCode === 'PERCENTAGE') return subtotal * (discountValue / 100);
    else if (type.setupCode === 'FIXED') return discountValue;
    return 0;
  }, [subtotal, discountTypeId, discountValue, discountTypes]);

  const grandTotal = subtotal - calculatedHeaderDiscount + totalTax;

  const updateMutation = useMutation({
    mutationFn: (data: any) => quoteService.update(Number(id), data),
    onSuccess: () => {
      toast.success('Quote updated successfully');
      navigate(`/sales/quotes/view/${id}`);
    },
    onError: (error: any) => {
      toast.error(error?.response?.data || 'Failed to update quote');
    }
  });

  const handleAddItem = () => {
    setItems([...items, {
      productId: null, productName: '', itemDescription: '', quantity: 1, unitPrice: 0, totalAmount: 0, discount: 0, discountTypeId: null, discountValue: 0, taxAmount: 0, deliveryLeadTime: 7
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
            item.unitPrice = prod.sellingPrice || prod.unitCost || 0;
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
    const payload = {
      id: Number(id), quoteNo, customerId, quoteDate, validUntil, headerRemarks,
      discountTypeId, discountValue, statusId,
      modifiedBy: userData?.userName || 'System',
      totalAmount: grandTotal,
      quoteItems: items.map(item => ({
        id: item.id, productId: item.productId, itemDescription: item.itemDescription || item.productName,
        quantity: item.quantity, unitPrice: item.unitPrice, totalAmount: item.totalAmount,
        discountTypeId: item.discountTypeId, discountValue: item.discountValue,
        taxAmount: item.taxAmount, deliveryLeadTime: item.deliveryLeadTime,
        isDeleted: item.isDeleted || false
      }))
    };
    updateMutation.mutate(payload);
  };

  if (isLoadingQuote) return <Box sx={{ p: 4, display: 'flex', justifyContent: 'center' }}><CircularProgress /></Box>;

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
          <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate(`/sales/quotes/view/${id}`)} size="small">Discard</Button>
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
                  slotProps={{ input: { endAdornment: <InputAdornment position="end">{discountTypes.find(t => t.setupId === discountTypeId)?.setupCode === 'PERCENTAGE' ? '%' : '$'}</InputAdornment> } }}
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
                {items.filter(i => !i.isDeleted).map((item, index) => (
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
                        slotProps={{ input: { startAdornment: <Typography variant="caption" sx={{ mr: 0.5 }}>$</Typography> } }} 
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
                                {discountTypes.map(t => <MenuItem key={t.setupId} value={t.setupId}><Typography variant="caption">{t.setupCode === 'PERCENTAGE' ? '%' : '$'}</Typography></MenuItem>)}
                            </Select>
                            <TextField 
                                type="number" size="small" variant="standard" sx={{ width: 40 }} 
                                value={item.discountValue} onChange={(e) => updateItem(index, { discountValue: Number(e.target.value) })} 
                            />
                        </Stack>
                    </TableCell>
                    <TableCell align="center">
                      <Typography sx={{ fontWeight: 700, fontSize: '0.875rem' }}>$ {item.totalAmount.toLocaleString(undefined, { minimumFractionDigits: 2 })}</Typography>
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
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>$ {subtotal.toLocaleString(undefined, { minimumFractionDigits: 2 })}</Typography>
                </Box>
                {calculatedHeaderDiscount > 0 && (
                  <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                    <Typography variant="body2" color="error">Addit. Discount</Typography>
                    <Typography variant="body2" color="error" sx={{ fontWeight: 700 }}>- $ {calculatedHeaderDiscount.toLocaleString(undefined, { minimumFractionDigits: 2 })}</Typography>
                  </Box>
                )}
                <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                  <Typography variant="body2" color="text.secondary">Tax Amount</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>$ {totalTax.toLocaleString(undefined, { minimumFractionDigits: 2 })}</Typography>
                </Box>
                <Divider />
                <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <Typography variant="h6" sx={{ fontWeight: 900 }}>Total</Typography>
                  <Typography variant="h5" sx={{ fontWeight: 900, color: 'primary.main' }}>
                    $ {grandTotal.toLocaleString(undefined, { minimumFractionDigits: 2 })}
                  </Typography>
                </Box>
              </Stack>
            </CardContent>
          </Card>
        </Grid>
      </Grid>
    </Box>
  );
};

export default EditQuotePage;
