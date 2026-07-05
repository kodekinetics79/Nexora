import React, { useState, useEffect } from 'react';
import { useParams, useNavigate, useLocation } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Autocomplete,
  Box,
  Button,
  Grid,
  IconButton,
  InputAdornment,
  MenuItem,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
  alpha,
  useTheme,
} from '@mui/material';
import {
  Add as AddIcon,
  ArrowBack as BackIcon,
  Calculate as CalcIcon,
  Delete as DeleteIcon,
  EventAvailable as DateIcon,
  Notes as NotesIcon,
  Person as CustomerIcon,
  Save as SaveIcon,
  ShoppingCart as OrderIcon,
} from '@mui/icons-material';
import { useAuth } from '../../../context/AuthContext';
import orderService from '../../../api/services/orderService';
import customerService from '../../../api/services/customerService';
import productService from '../../../api/services/productService';
import setupService from '../../../api/services/setupService';
import dayjs from 'dayjs';
import InfoCard from '../../../components/common/InfoCard';
import LoadingPage from '../../../components/common/LoadingPage';
import EmptyState from '../../../components/common/EmptyState';
import StatusBadge from '../../../components/common/StatusBadge';

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

const StepPill: React.FC<{ number: number; title: string; active?: boolean }> = ({ number, title, active }) => {
  const theme = useTheme();
  return (
    <Stack
      direction="row"
      spacing={1}
      sx={{
        alignItems: 'center',
        px: 1.25,
        py: 0.75,
        borderRadius: 2,
        bgcolor: active ? alpha(theme.palette.primary.main, 0.12) : 'action.hover',
        border: '1px solid',
        borderColor: active ? alpha(theme.palette.primary.main, 0.24) : 'divider',
      }}
    >
      <Box
        sx={{
          width: 24,
          height: 24,
          borderRadius: '50%',
          display: 'grid',
          placeItems: 'center',
          bgcolor: active ? 'primary.main' : 'background.paper',
          color: active ? '#fff' : 'text.secondary',
          fontSize: 12,
          fontWeight: 900,
        }}
      >
        {number}
      </Box>
      <Typography variant="caption" sx={{ fontWeight: 850, color: active ? 'primary.main' : 'text.secondary' }}>
        {title}
      </Typography>
    </Stack>
  );
};

const CreateOrderPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const location = useLocation();
  const theme = useTheme();
  const queryClient = useQueryClient();
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;

  const isEditMode = !!id;
  const searchParams = new URLSearchParams(location.search);
  const rfqId = searchParams.get('rfqId');
  const quoteId = searchParams.get('quoteId');

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

  const { data: orderData, isLoading: isLoadingOrder } = useQuery({
    queryKey: ['order-edit', id],
    queryFn: () => orderService.getById(Number(id), businessUnitId),
    enabled: isEditMode,
    staleTime: 2 * 60_000,
  });

  const { data: customersData } = useQuery({
    queryKey: ['customers-lookup', businessUnitId],
    queryFn: () => customerService.getAll({ businessUnitId, pageSize: 100 }),
    enabled: !!businessUnitId,
  });

  const { data: productsData } = useQuery({
    queryKey: ['products-lookup', businessUnitId],
    queryFn: () => productService.getAll({ businessUnitId, pageSize: 200 }),
    enabled: !!businessUnitId,
  });

  const { data: paymentStatuses } = useQuery({
    queryKey: ['setup-payment-statuses'],
    queryFn: () => setupService.getAll({ setupType: 'PaymentStatus' }),
  });

  const { data: paymentMethods } = useQuery({
    queryKey: ['setup-payment-methods'],
    queryFn: () => setupService.getAll({ setupType: 'PaymentMethod' }),
  });

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
        tempId: Math.random().toString(36).substr(2, 9),
      })) as OrderItemState[]);
    }
  }, [isEditMode, orderData]);

  const mutation = useMutation({
    mutationFn: (data: any) => isEditMode ? orderService.update(Number(id), data) : orderService.createManual(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['orders-list'] });
      navigate('/sales/orders');
    },
  });

  const handleAddItem = () => {
    setItems(prev => [
      ...prev,
      {
        productId: 0,
        productName: '',
        description: '',
        quantity: 1,
        unitPrice: 0,
        discount: 0,
        taxAmount: 0,
        totalAmount: 0,
        tempId: Math.random().toString(36).substr(2, 9),
      },
    ]);
  };

  const handleRemoveItem = (tempId: string) => {
    setItems(items.filter(item => item.tempId !== tempId));
  };

  const handleItemChange = (tempId: string, field: keyof OrderItemState, value: any) => {
    setItems(items.map(item => {
      if (item.tempId !== tempId) return item;

      const updatedItem = { ...item, [field]: value };
      if (field === 'quantity' || field === 'unitPrice' || field === 'discount' || field === 'taxAmount') {
        const qty = field === 'quantity' ? Number(value) : item.quantity;
        const price = field === 'unitPrice' ? Number(value) : item.unitPrice;
        const disc = field === 'discount' ? Number(value) : item.discount;
        const tax = field === 'taxAmount' ? Number(value) : item.taxAmount;
        updatedItem.totalAmount = (qty * price) - disc + tax;
      }

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
    }));
  };

  const subtotal = items.reduce((sum, item) => sum + (item.quantity * item.unitPrice), 0);
  const totalDiscount = items.reduce((sum, item) => sum + Number(item.discount), 0);
  const totalTax = items.reduce((sum, item) => sum + Number(item.taxAmount), 0);
  const grandTotal = subtotal - totalDiscount + totalTax;

  const money = (amount: number) => `$ ${Number(amount || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

  const handleSave = () => {
    if (!customerId) {
      alert('Please select a customer');
      return;
    }
    if (items.length === 0) {
      alert('Please add at least one item');
      return;
    }

    mutation.mutate({
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
      quoteId: quoteId ? Number(quoteId) : null,
    });
  };

  if (isEditMode && isLoadingOrder) return <LoadingPage variant="form" />;

  return (
    <Box>
      <Box
        sx={{
          mb: 2.5,
        }}
      >
        <Stack direction={{ xs: 'column', lg: 'row' }} sx={{ justifyContent: 'space-between', gap: 2, alignItems: { lg: 'flex-end' } }}>
          <Box>
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 900, textTransform: 'uppercase' }}>
              Sales order workspace
            </Typography>
            <Stack direction="row" spacing={1.25} sx={{ alignItems: 'center', mt: 1 }}>
              <Box sx={{ width: 46, height: 46, borderRadius: 2, color: '#fff', background: `linear-gradient(135deg, ${theme.palette.primary.main}, ${theme.palette.primary.dark})`, boxShadow: `0 16px 30px ${alpha(theme.palette.primary.main, 0.22)}`, display: 'grid', placeItems: 'center' }}>
                <OrderIcon />
              </Box>
              <Box>
                <Typography variant="h4" sx={{ fontWeight: 950 }}>
                  {isEditMode ? `Edit Order #${orderData?.orderNo}` : 'New Sales Order'}
                </Typography>
                <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 650 }}>
                  Build the customer order, line items, payment details, and terms in one flow.
                </Typography>
              </Box>
            </Stack>
          </Box>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
            <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate('/sales/orders')}>
              Cancel
            </Button>
            <Button variant="contained" startIcon={<SaveIcon />} onClick={handleSave} disabled={mutation.isPending}>
              {mutation.isPending ? 'Saving...' : (isEditMode ? 'Update Order' : 'Create Order')}
            </Button>
          </Stack>
        </Stack>
      </Box>

      <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', mb: 2.5 }}>
        <StepPill number={1} title="Basic Information" active={!!customerId} />
        <StepPill number={2} title="Line Items" active={items.length > 0} />
        <StepPill number={3} title="Payment & Terms" active={grandTotal > 0} />
      </Stack>

      <Grid container spacing={2.5}>
        <Grid size={{ xs: 12, lg: 8.3 }}>
          <Stack spacing={2.5}>
            <InfoCard title="Basic Information" subtitle="Customer, delivery dates, and payment setup." icon={<CustomerIcon />} accent={theme.palette.primary.main}>
              <Grid container spacing={2}>
                <Grid size={{ xs: 12, md: 6 }}>
                  <Autocomplete
                    options={customersData?.items || []}
                    getOptionLabel={(option) => option.name}
                    value={customersData?.items.find(c => c.id === customerId) || null}
                    onChange={(_, newValue) => setCustomerId(newValue?.id || null)}
                    renderInput={(params) => <TextField {...params} label="Customer" required />}
                  />
                </Grid>
                <Grid size={{ xs: 12, md: 3 }}>
                  <TextField fullWidth label="Order Date" type="date" value={orderDate} onChange={(e) => setOrderDate(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} required />
                </Grid>
                <Grid size={{ xs: 12, md: 3 }}>
                  <TextField fullWidth label="Delivery Date" type="date" value={deliveryDate} onChange={(e) => setDeliveryDate(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
                </Grid>
                <Grid size={{ xs: 12, md: 4 }}>
                  <TextField select fullWidth label="Payment Status" value={paymentStatusId} onChange={(e) => setPaymentStatusId(e.target.value)}>
                    <MenuItem value="">Select Status</MenuItem>
                    {paymentStatuses?.items.map((s) => <MenuItem key={s.setupId} value={s.setupId}>{s.setupName}</MenuItem>)}
                  </TextField>
                </Grid>
                <Grid size={{ xs: 12, md: 4 }}>
                  <TextField select fullWidth label="Payment Method" value={paymentMethodId} onChange={(e) => setPaymentMethodId(e.target.value)}>
                    <MenuItem value="">Select Method</MenuItem>
                    {paymentMethods?.items.map((m) => <MenuItem key={m.setupId} value={m.setupId}>{m.setupName}</MenuItem>)}
                  </TextField>
                </Grid>
                <Grid size={{ xs: 12, md: 4 }}>
                  <TextField fullWidth label="Paid Amount" type="number" value={paidAmount} onChange={(e) => setPaidAmount(Number(e.target.value))} slotProps={{ input: { startAdornment: <InputAdornment position="start">$</InputAdornment> } }} />
                </Grid>
                <Grid size={{ xs: 12 }}>
                  <TextField fullWidth label="Payment Reference" value={paymentReference} onChange={(e) => setPaymentReference(e.target.value)} placeholder="Bank transfer, cheque number, or payment reference" />
                </Grid>
              </Grid>
            </InfoCard>

            <InfoCard
              title="Order Line Items"
              subtitle="Select products, tune pricing, and calculate totals."
              icon={<OrderIcon />}
              accent={theme.palette.primary.main}
              actions={<Button startIcon={<AddIcon />} onClick={handleAddItem} variant="contained">Add Item</Button>}
            >
              <Box sx={{ overflowX: 'auto', mx: -2.5, mb: -2.5 }}>
                <Table size="small" stickyHeader>
                  <TableHead>
                    <TableRow>
                      <TableCell>Product</TableCell>
                      <TableCell align="right" width={100}>Qty</TableCell>
                      <TableCell align="right" width={130}>Unit Price</TableCell>
                      <TableCell align="right" width={120}>Discount</TableCell>
                      <TableCell align="right" width={120}>Tax</TableCell>
                      <TableCell align="right" width={130}>Total</TableCell>
                      <TableCell align="center" width={60}></TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {items.length === 0 ? (
                      <TableRow>
                        <TableCell colSpan={7}>
                          <EmptyState title="No items added" message="Add products to start building this sales order." actionLabel="Add Item" onAction={handleAddItem} />
                        </TableCell>
                      </TableRow>
                    ) : items.map((item) => (
                      <TableRow key={item.tempId} hover>
                        <TableCell sx={{ minWidth: 360 }}>
                          <Autocomplete
                            size="small"
                            options={productsData?.items || []}
                            getOptionLabel={(option) => `${option.productName} (${option.partNo})`}
                            value={productsData?.items.find(p => p.id === item.productId) || null}
                            onChange={(_, newValue) => handleItemChange(item.tempId, 'productId', newValue?.id || 0)}
                            renderInput={(params) => <TextField {...params} placeholder="Select Product" />}
                          />
                          <TextField
                            fullWidth
                            placeholder="Description"
                            value={item.description}
                            onChange={(e) => handleItemChange(item.tempId, 'description', e.target.value)}
                            sx={{ mt: 1 }}
                          />
                        </TableCell>
                        {(['quantity', 'unitPrice', 'discount', 'taxAmount'] as const).map((field) => (
                          <TableCell key={field} align="right">
                            <TextField
                              type="number"
                              value={item[field]}
                              onChange={(e) => handleItemChange(item.tempId, field, e.target.value)}
                              sx={{ width: field === 'quantity' ? 82 : 112 }}
                              slotProps={{ input: { startAdornment: field === 'quantity' ? undefined : <InputAdornment position="start">$</InputAdornment> } }}
                            />
                          </TableCell>
                        ))}
                        <TableCell align="right">
                          <StatusBadge label={money(item.totalAmount)} tone="success" />
                        </TableCell>
                        <TableCell align="center">
                          <IconButton size="small" color="error" onClick={() => handleRemoveItem(item.tempId)}>
                            <DeleteIcon fontSize="small" />
                          </IconButton>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </Box>
            </InfoCard>
          </Stack>
        </Grid>

        <Grid size={{ xs: 12, lg: 3.7 }}>
          <Stack spacing={2.5} sx={{ position: { lg: 'sticky' }, top: { lg: 92 } }}>
            <Paper sx={{ p: 2.5, background: `linear-gradient(135deg, ${alpha(theme.palette.primary.main, 0.11)}, ${alpha('#0F1B2D', 0.045)})` }}>
              <Stack direction="row" spacing={1.25} sx={{ alignItems: 'center', mb: 2 }}>
                <Box sx={{ width: 38, height: 38, borderRadius: 2, display: 'grid', placeItems: 'center', bgcolor: alpha(theme.palette.primary.main, 0.12), color: 'primary.main' }}>
                  <CalcIcon />
                </Box>
                <Box>
                  <Typography variant="subtitle1" sx={{ fontWeight: 900 }}>Order Summary</Typography>
                  <Typography variant="caption" color="text.secondary">{items.length} items selected</Typography>
                </Box>
              </Stack>
              <Stack spacing={1.5}>
                {[
                  ['Subtotal', money(subtotal), 'text.primary'],
                  ['Total Discount', `- ${money(totalDiscount)}`, 'error.main'],
                  ['Total Tax', `+ ${money(totalTax)}`, 'text.primary'],
                ].map(([label, value, color]) => (
                  <Stack key={label} direction="row" sx={{ justifyContent: 'space-between', gap: 2 }}>
                    <Typography variant="body2" color="text.secondary">{label}</Typography>
                    <Typography variant="body2" sx={{ fontWeight: 850, color }}>{value}</Typography>
                  </Stack>
                ))}
                <Box sx={{ height: 1, bgcolor: 'divider', my: 0.5 }} />
                <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', gap: 2 }}>
                  <Typography variant="h6" sx={{ fontWeight: 950 }}>Grand Total</Typography>
                  <Typography variant="h5" sx={{ fontWeight: 950, color: 'primary.main' }}>{money(grandTotal)}</Typography>
                </Stack>
                <Button fullWidth variant="contained" startIcon={<SaveIcon />} onClick={handleSave} disabled={mutation.isPending}>
                  {mutation.isPending ? 'Saving...' : (isEditMode ? 'Update Order' : 'Create Order')}
                </Button>
              </Stack>
            </Paper>

            <InfoCard title="Terms & Notes" subtitle="Customer-facing terms and internal context." icon={<NotesIcon />} accent={theme.palette.warning.main}>
              <Stack spacing={2}>
                <TextField
                  fullWidth
                  multiline
                  rows={4}
                  label="Terms and Conditions"
                  placeholder="Enter terms and conditions..."
                  value={termsAndConditions}
                  onChange={(e) => setTermsAndConditions(e.target.value)}
                />
                <TextField
                  fullWidth
                  multiline
                  rows={4}
                  label="Internal Notes"
                  placeholder="Enter internal notes..."
                  value={notes}
                  onChange={(e) => setNotes(e.target.value)}
                />
              </Stack>
            </InfoCard>

            <Paper sx={{ p: 2, borderStyle: 'dashed' }}>
              <Stack direction="row" spacing={1.25} sx={{ alignItems: 'center' }}>
                <DateIcon color="primary" />
                <Box>
                  <Typography variant="body2" sx={{ fontWeight: 850 }}>Delivery Window</Typography>
                  <Typography variant="caption" color="text.secondary">
                    {orderDate || '-'} to {deliveryDate || '-'}
                  </Typography>
                </Box>
              </Stack>
            </Paper>
          </Stack>
        </Grid>
      </Grid>
    </Box>
  );
};

export default CreateOrderPage;
