import React, { useState, useEffect } from 'react';
import { useParams, useNavigate, useLocation } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper,Grid, TextField, Button, Divider, Table,
  TableHead, TableRow, TableCell, TableBody, MenuItem, Stack, IconButton,
 CircularProgress, Breadcrumbs, Link, Autocomplete,
  InputAdornment, 
} from '@mui/material';
import {
  Save as SaveIcon,
  ArrowBack as BackIcon,
  Delete as DeleteIcon,
  Add as AddIcon,
  Calculate as CalcIcon,
  Person as CustomerIcon,
  ShoppingCart as OrderIcon
} from '@mui/icons-material';
import { useAuth } from '../../../context/AuthContext';
import orderService from '../../../api/services/orderService';
import customerService from '../../../api/services/customerService';
import productService from '../../../api/services/productService';
import setupService from '../../../api/services/setupService';
import { useSnackbar } from 'notistack';
import { handleApiError } from '../../../utils/errorHandler';
import dayjs from 'dayjs';

interface OrderItemState {
  id?: number;
  productId: number;
  productName: string;
  description?: string;
  quantity: number;
  unitPrice: number;
  discount: number;
  taxAmount: number;
  totalAmount: number;
  uomId?: number;
  warehouseId?: number;
  tempId: string;
}

const CreateOrderPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const location = useLocation();
  const queryClient = useQueryClient();
  const { userData } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const businessUnitId = userData?.businessUnitId || 0;

  const [customerError, setCustomerError] = useState(false);

  const isEditMode = !!id;
  const searchParams = new URLSearchParams(location.search);
  const rfqId = searchParams.get('rfqId');

  const [customerId, setCustomerId] = useState<number | null>(null);
  const [orderDate, setOrderDate] = useState(dayjs().format('YYYY-MM-DD'));
  const [deliveryDate, setDeliveryDate] = useState(dayjs().add(7, 'day').format('YYYY-MM-DD'));
  const [paymentStatusId, setPaymentStatusId] = useState<number | string>('');
  const [paymentMethodId, setPaymentMethodId] = useState<number | string>('');
  const [paidAmount, setPaidAmount] = useState<number>(0);
  const [paymentReference, setPaymentReference] = useState('');
  const [notes, setNotes] = useState('');
  const [termsAndConditions, setTermsAndConditions] = useState('');
  const [items, setItems] = useState<OrderItemState[]>([]);

  // Queries
  const { data: orderData, isLoading: isLoadingOrder } = useQuery({
    queryKey: ['order-edit', id],
    queryFn: () => orderService.getById(Number(id), businessUnitId),
    enabled: isEditMode,
  });

  const { data: customersData } = useQuery({
    queryKey: ['customers-lookup'],
    queryFn: () => customerService.getAll({ businessUnitId, pageSize: 100 }),
  });

  const { data: productsData } = useQuery({
    queryKey: ['products-lookup'],
    queryFn: () => productService.getAll({ businessUnitId, pageSize: 200 }),
  });

  const { data: paymentStatuses } = useQuery({
    queryKey: ['setup-payment-statuses'],
    queryFn: () => setupService.getAll({ setupType: 'PaymentStatus' }),
  });

  const { data: paymentMethods } = useQuery({
    queryKey: ['setup-payment-methods'],
    queryFn: () => setupService.getAll({ setupType: 'PaymentMethod' }),
  });

  // Effect to load data in edit mode or from RFQ/Quote
  useEffect(() => {
    if (isEditMode && orderData) {
      setCustomerId(orderData.customerId);
      setOrderDate(dayjs(orderData.orderDate).format('YYYY-MM-DD'));
      setDeliveryDate(orderData.deliveryDate ? dayjs(orderData.deliveryDate).format('YYYY-MM-DD') : '');
      setPaymentStatusId(orderData.paymentStatusId || '');
      setPaymentMethodId(orderData.paymentMethodId || '');
      setPaidAmount(orderData.paidAmount || 0);
      setPaymentReference(orderData.paymentReference || '');
      setNotes(orderData.notes || '');
      setTermsAndConditions(orderData.termsAndConditions || '');
      setItems(orderData.items.map(item => ({
        ...item,
        tempId: Math.random().toString(36).substr(2, 9)
      })) as OrderItemState[]);
    }
  }, [isEditMode, orderData]);

  // Mutations
  const mutation = useMutation({
    mutationFn: (data: any) => isEditMode ? orderService.update(Number(id), data) : orderService.createManual(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['orders-list'] });
      enqueueSnackbar(isEditMode ? 'Order updated successfully' : 'Order created successfully', { variant: 'success' });
      navigate('/sales/orders');
    },
    onError: (error: any) => handleApiError(error),
  });

  const handleAddItem = () => {
    const newItem: OrderItemState = {
      productId: 0,
      productName: '',
      description: '',
      quantity: 1,
      unitPrice: 0,
      discount: 0,
      taxAmount: 0,
      totalAmount: 0,
      tempId: Math.random().toString(36).substr(2, 9)
    };
    setItems([...items, newItem]);
  };

  const handleRemoveItem = (tempId: string) => {
    setItems(items.filter(item => item.tempId !== tempId));
  };

  const handleItemChange = (tempId: string, field: keyof OrderItemState, value: any) => {
    setItems(items.map(item => {
      if (item.tempId === tempId) {
        const updatedItem = { ...item, [field]: value };
        
        // Auto-calculate total for this item
        if (field === 'quantity' || field === 'unitPrice' || field === 'discount' || field === 'taxAmount') {
          const qty = field === 'quantity' ? Number(value) : item.quantity;
          const price = field === 'unitPrice' ? Number(value) : item.unitPrice;
          const disc = field === 'discount' ? Number(value) : item.discount;
          const tax = field === 'taxAmount' ? Number(value) : item.taxAmount;
          updatedItem.totalAmount = (qty * price) - disc + tax;
        }
        
        // If product changes, auto-fill price and name
        if (field === 'productId') {
          const product = productsData?.items.find(p => p.id === value);
          if (product) {
            updatedItem.productName = product.productName || '';
            updatedItem.unitPrice = product.finalSalesPrice || product.sellingPrice || 0;
            updatedItem.description = product.description || '';
            updatedItem.uomId = product.uomId;
            updatedItem.warehouseId = product.warehouseId;
            updatedItem.totalAmount = (updatedItem.quantity * updatedItem.unitPrice) - updatedItem.discount + updatedItem.taxAmount;
          }
        }
        
        return updatedItem;
      }
      return item;
    }));
  };

  const calculateTotals = () => {
    const subtotal = items.reduce((sum, item) => sum + (item.quantity * item.unitPrice), 0);
    const totalDiscount = items.reduce((sum, item) => sum + Number(item.discount), 0);
    const totalTax = items.reduce((sum, item) => sum + Number(item.taxAmount), 0);
    const grandTotal = subtotal - totalDiscount + totalTax;
    return { subtotal, totalDiscount, totalTax, grandTotal };
  };

  const { subtotal, totalDiscount, totalTax, grandTotal } = calculateTotals();

  const handleSave = () => {
    if (!customerId) {
      setCustomerError(true);
      enqueueSnackbar('Please select a customer', { variant: 'warning' });
      return;
    }
    if (items.length === 0) {
      enqueueSnackbar('Please add at least one item before saving', { variant: 'warning' });
      return;
    }

    const payload = {
      customerId,
      businessUnitId,
      orderDate: dayjs(orderDate).toISOString(),
      deliveryDate: deliveryDate ? dayjs(deliveryDate).toISOString() : null,
      paymentStatusId: paymentStatusId || null,
      paymentMethodId: paymentMethodId || null,
      paidAmount: Number(paidAmount),
      paymentReference,
      notes,
      termsAndConditions,
      items: items.map(({ tempId, ...rest }) => rest),
      rfqId: rfqId ? Number(rfqId) : null,
      quoteId: null
    };

    mutation.mutate(payload);
  };

  if (isEditMode && isLoadingOrder) {
    return <Box sx={{ display: 'flex', justifyContent: 'center', p: 5 }}><CircularProgress /></Box>;
  }

  return (
    <Box sx={{ p: 3, bgcolor: 'background.default', minHeight: '100vh' }}>
      {/* Header */}
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Breadcrumbs sx={{ mb: 1 }}>
            <Link component="button" onClick={() => navigate('/sales/orders')} underline="hover" color="inherit">Orders</Link>
            <Typography color="text.primary">{isEditMode ? 'Edit Order' : 'Create Order'}</Typography>
          </Breadcrumbs>
          <Typography variant="h4" sx={{ fontWeight: 900, display: 'flex', alignItems: 'center', gap: 1.5 }}>
            <OrderIcon color="primary" fontSize="large" />
            {isEditMode ? `Edit Order #${orderData?.orderNo}` : 'New Sales Order'}
          </Typography>
        </Box>
        <Stack direction="row" spacing={2}>
          <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate('/sales/orders')}>
            Cancel
          </Button>
          <Button variant="contained" startIcon={<SaveIcon />} onClick={handleSave} disabled={mutation.isPending}>
            {mutation.isPending ? 'Saving...' : (isEditMode ? 'Update Order' : 'Create Order')}
          </Button>
        </Stack>
      </Stack>

      <Grid container spacing={3}>
        {/* Basic Information */}
        <Grid size={{ xs: 12, md: 8 }}>
          <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
            <Typography variant="subtitle1" sx={{ fontWeight: 800, mb: 3, display: 'flex', alignItems: 'center', gap: 1 }}>
              <CustomerIcon color="primary" /> BASIC INFORMATION
            </Typography>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 6 }}>
                <Autocomplete
                  options={customersData?.items || []}
                  getOptionLabel={(option) => option.name}
                  value={customersData?.items.find(c => c.id === customerId) || null}
                  onChange={(_, newValue) => { setCustomerId(newValue?.id || null); if (newValue) setCustomerError(false); }}
                  renderInput={(params) => (
                    <TextField
                      {...params}
                      label="Customer"
                      required
                      size="small"
                      error={customerError}
                      helperText={customerError ? 'Customer is required' : ''}
                    />
                  )}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 3 }}>
                <TextField
                  fullWidth
                  label="Order Date"
                  type="date"
                  value={orderDate}
                  onChange={(e) => setOrderDate(e.target.value)}
                  slotProps={{ inputLabel: { shrink: true } }}
                  size="small"
                  required
                />
              </Grid>
              <Grid size={{ xs: 12, md: 3 }}>
                <TextField
                  fullWidth
                  label="Delivery Date"
                  type="date"
                  value={deliveryDate}
                  onChange={(e) => setDeliveryDate(e.target.value)}
                  slotProps={{ inputLabel: { shrink: true } }}
                  size="small"
                />
              </Grid>

              <Grid size={{ xs: 12, md: 4 }}>
                <TextField
                  select
                  fullWidth
                  label="Payment Status"
                  value={paymentStatusId}
                  onChange={(e) => setPaymentStatusId(e.target.value)}
                  size="small"
                >
                  <MenuItem value="">Select Status</MenuItem>
                  {paymentStatuses?.items.map((s) => (
                    <MenuItem key={s.setupId} value={s.setupId}>{s.setupName}</MenuItem>
                  ))}
                </TextField>
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField
                  select
                  fullWidth
                  label="Payment Method"
                  value={paymentMethodId}
                  onChange={(e) => setPaymentMethodId(e.target.value)}
                  size="small"
                >
                  <MenuItem value="">Select Method</MenuItem>
                  {paymentMethods?.items.map((m) => (
                    <MenuItem key={m.setupId} value={m.setupId}>{m.setupName}</MenuItem>
                  ))}
                </TextField>
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField
                  fullWidth
                  label="Paid Amount"
                  type="number"
                  value={paidAmount}
                  onChange={(e) => setPaidAmount(Number(e.target.value))}
                  size="small"
                  slotProps={{ input: { startAdornment: <InputAdornment position="start">$</InputAdornment> } }}
                />
              </Grid>
            </Grid>
          </Paper>

          {/* Order Items */}
          <Paper sx={{ mt: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden' }}>
            <Box sx={{ p: 2, bgcolor: 'grey.50', borderBottom: '1px solid', borderColor: 'divider', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>ORDER LINE ITEMS</Typography>
              <Button startIcon={<AddIcon />} size="small" onClick={handleAddItem} variant="outlined">Add Item</Button>
            </Box>
            <Table size="small">
              <TableHead>
                <TableRow sx={{ bgcolor: 'grey.100' }}>
                  <TableCell sx={{ fontWeight: 800 }}>Product</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right" width={100}>Qty</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right" width={120}>Unit Price</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right" width={120}>Discount</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right" width={120}>Tax</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right" width={120}>Total</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="center" width={50}></TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {items.length === 0 ? (
                  <TableRow><TableCell colSpan={7} align="center" sx={{ py: 4, color: 'text.secondary' }}>No items added yet. Click "Add Item" to start.</TableCell></TableRow>
                ) : (
                  items.map((item) => (
                    <TableRow key={item.tempId}>
                      <TableCell>
                        <Autocomplete
                          size="small"
                          options={productsData?.items || []}
                          getOptionLabel={(option) => `${option.productName} (${option.partNo})`}
                          value={productsData?.items.find(p => p.id === item.productId) || null}
                          onChange={(_, newValue) => handleItemChange(item.tempId, 'productId', newValue?.id || 0)}
                          renderInput={(params) => <TextField {...params} variant="standard" placeholder="Select Product" />}
                          sx={{ width: 300 }}
                        />
                        <TextField
                          fullWidth
                          variant="standard"
                          placeholder="Description"
                          value={item.description}
                          onChange={(e) => handleItemChange(item.tempId, 'description', e.target.value)}
                          sx={{ mt: 1, '& .MuiInput-root': { fontSize: '0.75rem' } }}
                        />
                      </TableCell>
                      <TableCell align="right">
                        <TextField
                          type="number"
                          size="small"
                          variant="standard"
                          value={item.quantity}
                          onChange={(e) => handleItemChange(item.tempId, 'quantity', e.target.value)}
                          slotProps={{ input: { sx: { textAlign: 'right' } } }}
                        />
                      </TableCell>
                      <TableCell align="right">
                        <TextField
                          type="number"
                          size="small"
                          variant="standard"
                          value={item.unitPrice}
                          onChange={(e) => handleItemChange(item.tempId, 'unitPrice', e.target.value)}
                          slotProps={{ input: { startAdornment: <Box sx={{ mr: 0.5 }}>$</Box>, sx: { textAlign: 'right' } } }}
                        />
                      </TableCell>
                      <TableCell align="right">
                        <TextField
                          type="number"
                          size="small"
                          variant="standard"
                          value={item.discount}
                          onChange={(e) => handleItemChange(item.tempId, 'discount', e.target.value)}
                          slotProps={{ input: { startAdornment: <Box sx={{ mr: 0.5 }}>$</Box>, sx: { textAlign: 'right' } } }}
                        />
                      </TableCell>
                      <TableCell align="right">
                        <TextField
                          type="number"
                          size="small"
                          variant="standard"
                          value={item.taxAmount}
                          onChange={(e) => handleItemChange(item.tempId, 'taxAmount', e.target.value)}
                          slotProps={{ input: { startAdornment: <Box sx={{ mr: 0.5 }}>$</Box>, sx: { textAlign: 'right' } } }}
                        />
                      </TableCell>
                      <TableCell align="right" sx={{ fontWeight: 700 }}>
                        $ {item.totalAmount.toLocaleString(undefined, { minimumFractionDigits: 2 })}
                      </TableCell>
                      <TableCell align="center">
                        <IconButton size="small" color="error" onClick={() => handleRemoveItem(item.tempId)}>
                          <DeleteIcon fontSize="small" />
                        </IconButton>
                      </TableCell>
                    </TableRow>
                  ))
                )}
              </TableBody>
            </Table>
          </Paper>
        </Grid>

        {/* Sidebar: Notes & Totals */}
        <Grid size={{ xs: 12, md: 4 }}>
          <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', mb: 3 }}>
            <Typography variant="subtitle1" sx={{ fontWeight: 800, mb: 2, display: 'flex', alignItems: 'center', gap: 1 }}>
              <CalcIcon color="primary" /> ORDER SUMMARY
            </Typography>
            <Stack spacing={2}>
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2" color="text.secondary">Subtotal</Typography>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>$ {subtotal.toLocaleString(undefined, { minimumFractionDigits: 2 })}</Typography>
              </Box>
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2" color="text.secondary">Total Discount</Typography>
                <Typography variant="body2" sx={{ fontWeight: 700, color: 'error.main' }}>- $ {totalDiscount.toLocaleString(undefined, { minimumFractionDigits: 2 })}</Typography>
              </Box>
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2" color="text.secondary">Total Tax</Typography>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>+ $ {totalTax.toLocaleString(undefined, { minimumFractionDigits: 2 })}</Typography>
              </Box>
              <Divider />
              <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <Typography variant="h6" sx={{ fontWeight: 900 }}>Grand Total</Typography>
                <Typography variant="h6" sx={{ fontWeight: 900, color: 'primary.main' }}>$ {grandTotal.toLocaleString(undefined, { minimumFractionDigits: 2 })}</Typography>
              </Box>
            </Stack>
          </Paper>

          <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 2 }}>TERMS & CONDITIONS</Typography>
            <TextField
              fullWidth
              multiline
              rows={3}
              placeholder="Enter terms and conditions..."
              value={termsAndConditions}
              onChange={(e) => setTermsAndConditions(e.target.value)}
              sx={{ mb: 3 }}
            />
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 2 }}>INTERNAL NOTES</Typography>
            <TextField
              fullWidth
              multiline
              rows={3}
              placeholder="Enter internal notes..."
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
            />
          </Paper>
        </Grid>
      </Grid>
    </Box>
  );
};

export default CreateOrderPage;
