import React, { useState, useEffect } from 'react';
import { useParams, useNavigate, useLocation } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper,Grid, TextField, Button, Divider, Table,
  TableHead, TableRow, TableCell, TableBody, Stack, IconButton,
 CircularProgress, Breadcrumbs, Link, Autocomplete, Alert,
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
import { useSnackbar } from 'notistack';
import { handleApiError } from '../../../utils/errorHandler';
import dayjs from 'dayjs';
import { formatMoney } from '../../../utils/currency';

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
  const { userData, hasPermission } = useAuth();
  const canConfirmOrders = hasPermission('Customer Awards');
  const { enqueueSnackbar } = useSnackbar();
  const businessUnitId = userData?.businessUnitId || 0;

  const [customerError, setCustomerError] = useState(false);

  const isEditMode = !!id;
  const searchParams = new URLSearchParams(location.search);
  const rfqId = searchParams.get('rfqId');

  const [customerId, setCustomerId] = useState<number | null>(null);
  const [orderDate, setOrderDate] = useState(dayjs().format('YYYY-MM-DD'));
  const [deliveryDate, setDeliveryDate] = useState(dayjs().add(7, 'day').format('YYYY-MM-DD'));
  const [notes, setNotes] = useState('');
  const [termsAndConditions, setTermsAndConditions] = useState('');
  const [items, setItems] = useState<OrderItemState[]>([]);

  // Queries
  const { data: orderData, isLoading: isLoadingOrder, isError: isOrderError } = useQuery({
    queryKey: ['order-edit', id],
    queryFn: () => orderService.getById(Number(id), businessUnitId),
    enabled: isEditMode,
  });

  const { data: customersData } = useQuery({
    queryKey: ['customers-lookup'],
    queryFn: () => customerService.getAll({ pageSize: 100 }),
  });

  const { data: productsData } = useQuery({
    queryKey: ['products-lookup'],
    queryFn: () => productService.getAll({ businessUnitId, pageSize: 200 }),
  });

  // Effect to load data in edit mode or from RFQ/Quote
  useEffect(() => {
    if (isEditMode && orderData) {
      setCustomerId(orderData.customerId);
      setOrderDate(dayjs(orderData.orderDate).format('YYYY-MM-DD'));
      setDeliveryDate(orderData.deliveryDate ? dayjs(orderData.deliveryDate).format('YYYY-MM-DD') : '');
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

  /**
   * The order's own currency. This screen only ever edits an order (create mode renders the
   * client-PO notice below), and an order keeps the currency it was raised in — the update DTO
   * does not carry one. The server now refuses to raise an order that names no currency, because
   * finance cannot invoice such an order; an older order that predates that gate shows
   * "Not stated" here so the gap is visible rather than printed as a bare number.
   */
  const currencyCode = orderData?.currencyCode ?? null;

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

  // Without this, a failed load fell through to the form and rendered a live, editable
  // "Edit Order #undefined" with a working Update button for an order that does not exist.
  if (isEditMode && (isOrderError || !orderData)) {
    return (
      <Box sx={{ p: 4 }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 1 }}>We couldn&apos;t load this order.</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          It may have been removed, or it belongs to another workspace. Nothing was changed.
        </Typography>
        <Button variant="outlined" onClick={() => navigate('/sales/orders')}>Back to orders</Button>
      </Box>
    );
  }

  if (!isEditMode) {
    return (
      <Box sx={{ p: { xs: 2, md: 4 }, maxWidth: 760, mx: 'auto' }}>
        <Alert severity="info">
          <Typography variant="h6" component="h1" sx={{ fontWeight: 900, mb: 1 }}>
            Customer orders start from an accepted purchase order
          </Typography>
          A sales order must retain the customer PO, quote revision, approved quantities and
          commercial-case lineage. Use the Client PO Inbox to reconcile the customer document and
          create the governed order; free-standing manual orders are not supported.
        </Alert>
        {/* The only way forward from here is a Customer Awards screen; a button that lands on
            Access Denied is a dead end wearing a call to action. */}
        {!canConfirmOrders && (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>
            Ask your administrator for Customer Awards access to confirm customer orders.
          </Typography>
        )}
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ mt: 2 }}>
          {canConfirmOrders && (
            <Button variant="contained" onClick={() => navigate('/sales/client-pos')}>Open Client PO Inbox</Button>
          )}
          <Button variant="outlined" onClick={() => navigate('/sales/orders')}>Back to orders</Button>
        </Stack>
      </Box>
    );
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
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  label="Currency"
                  size="small"
                  fullWidth
                  value={currencyCode ?? 'Not stated'}
                  disabled
                  helperText={currencyCode
                    ? 'An order keeps the currency it was raised in.'
                    : 'This order was raised before a currency was required. It cannot be invoiced until one is stated.'}
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

              <Grid size={{ xs: 12 }}>
                <Alert severity="info">
                  Payment status and cash allocation are controlled in Accounts Receivable and
                  cannot be changed through ordinary Sales Order editing.
                </Alert>
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
                          slotProps={{ input: { startAdornment: <Box sx={{ mr: 0.5 }}>{currencyCode ?? ''}</Box>, sx: { textAlign: 'right' } } }}
                        />
                      </TableCell>
                      <TableCell align="right">
                        <TextField
                          type="number"
                          size="small"
                          variant="standard"
                          value={item.discount}
                          onChange={(e) => handleItemChange(item.tempId, 'discount', e.target.value)}
                          slotProps={{ input: { startAdornment: <Box sx={{ mr: 0.5 }}>{currencyCode ?? ''}</Box>, sx: { textAlign: 'right' } } }}
                        />
                      </TableCell>
                      <TableCell align="right">
                        <TextField
                          type="number"
                          size="small"
                          variant="standard"
                          value={item.taxAmount}
                          onChange={(e) => handleItemChange(item.tempId, 'taxAmount', e.target.value)}
                          slotProps={{ input: { startAdornment: <Box sx={{ mr: 0.5 }}>{currencyCode ?? ''}</Box>, sx: { textAlign: 'right' } } }}
                        />
                      </TableCell>
                      <TableCell align="right" sx={{ fontWeight: 700 }}>
                        {formatMoney(item.totalAmount, currencyCode)}
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
                <Typography variant="body2" sx={{ fontWeight: 700 }}>{formatMoney(subtotal, currencyCode)}</Typography>
              </Box>
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2" color="text.secondary">Total Discount</Typography>
                <Typography variant="body2" sx={{ fontWeight: 700, color: 'error.main' }}>- {formatMoney(totalDiscount, currencyCode)}</Typography>
              </Box>
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2" color="text.secondary">Total Tax</Typography>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>+ {formatMoney(totalTax, currencyCode)}</Typography>
              </Box>
              <Divider />
              <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <Typography variant="h6" sx={{ fontWeight: 900 }}>Grand Total</Typography>
                <Typography variant="h6" sx={{ fontWeight: 900, color: 'primary.main' }}>{formatMoney(grandTotal, currencyCode)}</Typography>
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
