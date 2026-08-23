import React, { useState, useEffect } from 'react';
import { useParams, useNavigate, useLocation } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Grid, TextField, Button, Divider, Table,
  TableHead, TableRow, TableCell, TableBody, MenuItem, Stack,
  Card, CardContent, CircularProgress, Breadcrumbs, Link, Alert
} from '@mui/material';
import {
  Save as SaveIcon,
  ArrowBack as BackIcon
} from '@mui/icons-material';
import { useAuth } from '../../../context/AuthContext';
import shipmentService from '../../../api/services/shipmentService';
import type { CreateShipmentDTO, ShipmentItemDTO } from '../../../api/services/shipmentService';
import orderService from '../../../api/services/orderService';
import setupService from '../../../api/services/setupService';

import dayjs from 'dayjs';
import { formatMoney } from '../../../utils/currency';

interface FormState {
  orderId: string;
  statusId: string;
  shipmentDate: string;
  estimatedDeliveryDate: string;
  actualDeliveryDate: string;
  carrier: string;
  serviceLevel: string;
  trackingNumber: string;
  shippingAddress: string;
  notes: string;
  complianceOverrideReason: string;
  items: ShipmentItemDTO[];
}

/**
 * The server's message, verbatim. It names the line, the ordered quantity, what has already
 * shipped and what was declared — or which lot is quarantined, or which certificate has lapsed.
 * Inventing client copy here would contradict the only account of the refusal that is true.
 */
const serverMessage = (error: unknown): string | null => {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;
  const message = (data as { message?: unknown })?.message;
  return typeof message === 'string' && message.trim() ? message : null;
};

const CreateShipmentPage: React.FC = () => {
  const { id: paramId } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const location = useLocation();
  const queryClient = useQueryClient();
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;

  const isEdit = location.pathname.includes('/edit/');
  const isFromOrder = location.pathname.includes('/from-order/');
  const orderIdFromPath = isFromOrder ? paramId : undefined;
  const shipmentId = isEdit ? paramId : undefined;

  const [form, setForm] = useState<FormState>({
    orderId: orderIdFromPath || '',
    statusId: '',
    shipmentDate: dayjs().format('YYYY-MM-DD'),
    estimatedDeliveryDate: '',
    actualDeliveryDate: '',
    carrier: '',
    serviceLevel: '',
    trackingNumber: '',
    shippingAddress: '',
    notes: '',
    complianceOverrideReason: '',
    items: [],
  });

  // Fetch orders for selection
  const {
    data: orders,
    isLoading: isLoadingOrders,
    isError: isOrdersError,
  } = useQuery({
    queryKey: ['orders-for-shipment', businessUnitId],
    queryFn: () => orderService.getAll({ businessUnitId, pageSize: 100 }),
    enabled: !isEdit && !isFromOrder,
  });

  // Fetch statuses
  const {
    data: statusesData,
    isLoading: isLoadingStatuses,
    isError: isStatusesError,
  } = useQuery({
    queryKey: ['shipment-statuses'],
    queryFn: () => setupService.getAll({ setupType: 'ShipmentStatus' }),
  });
  const statuses = statusesData?.items || [];

  // Fetch existing shipment if editing
  const { data: existingShipment, isLoading: isLoadingShipment, isError: isShipmentError } = useQuery({
    queryKey: ['shipment-edit', shipmentId],
    queryFn: () => shipmentService.getById(Number(shipmentId), businessUnitId),
    enabled: isEdit && !!shipmentId,
  });

  // Fetch order details when order changes
  const { data: selectedOrder, isLoading: isLoadingOrder } = useQuery({
    queryKey: ['order-details', form.orderId],
    queryFn: () => orderService.getById(Number(form.orderId), businessUnitId),
    enabled: !!form.orderId,
  });

  // Every despatch already made against this order. The Ship Qty ceiling is the REMAINING
  // quantity, not the ordered quantity: three despatches of 50 against an order for 100 each
  // looked legal on their own, and the server (which is the only ceiling that counts) refuses
  // them cumulatively. The screen has to offer the same arithmetic or it invites the refusal.
  const { data: priorShipments } = useQuery({
    queryKey: ['shipments-for-order', form.orderId, businessUnitId],
    queryFn: () => shipmentService.getByOrderId(Number(form.orderId), businessUnitId),
    enabled: !!form.orderId,
  });

  const shippedByLine = React.useMemo(() => {
    const totals = new Map<number, number>();
    (priorShipments || [])
      .filter(shipment => !isEdit || shipment.id !== Number(shipmentId))
      .forEach(shipment => shipment.items.forEach(item => {
        totals.set(item.orderItemId, (totals.get(item.orderItemId) || 0) + item.quantity);
      }));
    return totals;
  }, [priorShipments, isEdit, shipmentId]);

  const remainingFor = (orderItemId: number, orderedQuantity: number) =>
    Math.max(0, orderedQuantity - (shippedByLine.get(orderItemId) || 0));

  useEffect(() => {
    if (isEdit && existingShipment) {
      setForm({
        orderId: existingShipment.orderId.toString(),
        statusId: existingShipment.statusId.toString(),
        shipmentDate: dayjs(existingShipment.shipmentDate).format('YYYY-MM-DD'),
        estimatedDeliveryDate: existingShipment.estimatedDeliveryDate ? dayjs(existingShipment.estimatedDeliveryDate).format('YYYY-MM-DD') : '',
        actualDeliveryDate: existingShipment.actualDeliveryDate ? dayjs(existingShipment.actualDeliveryDate).format('YYYY-MM-DD') : '',
        carrier: existingShipment.carrier || '',
        serviceLevel: existingShipment.serviceLevel || '',
        trackingNumber: existingShipment.trackingNumber || '',
        shippingAddress: existingShipment.shippingAddress || '',
        notes: existingShipment.notes || '',
        complianceOverrideReason: '',
        items: existingShipment.items,
      });
    } else if (selectedOrder && !isEdit && form.items.length === 0) {
      setForm(prev => ({
        ...prev,
        shippingAddress: selectedOrder.notes || '', // Default address from order notes
        // Default to what is LEFT to ship, not to the ordered quantity. Defaulting to the full
        // order on a line that is already half despatched pre-fills a quantity the server will
        // refuse, and the operator's only clue was a 200 that silently over-shipped.
        items: selectedOrder.items.map(item => ({
          orderItemId: item.id,
          productName: item.productName,
          quantity: remainingFor(item.id, item.quantity),
          notes: ''
        }))
      }));
    }
  }, [isEdit, existingShipment, selectedOrder, form.items.length, shippedByLine]);

  const mutation = useMutation({
    mutationFn: (data: CreateShipmentDTO) => 
      isEdit ? shipmentService.update(Number(shipmentId), data) : shipmentService.create(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['shipments'] });
      navigate('/sales/shipments');
    },
  });

  // The refusal is rendered verbatim. Before this, a rejected despatch resolved into a generic
  // failure and the operator was left guessing which of over-shipment, quarantined stock or a
  // lapsed certificate had stopped them — three different problems with three different fixes.
  const submitError = mutation.isError ? serverMessage(mutation.error) : null;
  const needsComplianceOverride =
    !!submitError && submitError.toLowerCase().includes('expired certificate');

  const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const { name, value } = e.target;
    setForm(prev => ({ ...prev, [name]: value }));
  };

  const handleItemQuantityChange = (index: number, value: number) => {
    const newItems = [...form.items];
    newItems[index].quantity = value;
    setForm(prev => ({ ...prev, items: newItems }));
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!form.orderId || !form.statusId) return;

    const payload: CreateShipmentDTO = {
      orderId: Number(form.orderId),
      businessUnitId,
      statusId: Number(form.statusId),
      shipmentDate: form.shipmentDate,
      estimatedDeliveryDate: form.estimatedDeliveryDate || undefined,
      carrier: form.carrier,
      serviceLevel: form.serviceLevel,
      trackingNumber: form.trackingNumber,
      shippingAddress: form.shippingAddress,
      notes: form.notes,
      // Only sent when the operator has actually typed one. The server refuses an override on a
      // despatch where every lot is in date, so sending an empty string would be indistinguishable
      // from an override nobody asked for.
      complianceOverrideReason: form.complianceOverrideReason.trim() || undefined,
      items: form.items.map(item => ({
        orderItemId: item.orderItemId,
        quantity: item.quantity,
        notes: item.notes
      })),
    };

    mutation.mutate(payload);
  };

  if (isLoadingShipment || (form.orderId && isLoadingOrder)) {
    return <Box sx={{ display: 'flex', justifyContent: 'center', p: 5 }}><CircularProgress /></Box>;
  }

  // Without this, a failed load fell through to the form and rendered a live, editable
  // "Edit Shipment undefined" for a shipment that does not exist.
  if (isEdit && (isShipmentError || !existingShipment)) {
    return (
      <Box sx={{ p: 4 }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 1 }}>We couldn&apos;t load this shipment.</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          It may have been removed, or it belongs to another workspace. Nothing was changed.
        </Typography>
        <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate('/sales/shipments')}>
          Back to shipments
        </Button>
      </Box>
    );
  }

  return (
    <Box sx={{ p: 3, bgcolor: 'background.default', minHeight: '100vh' }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Breadcrumbs sx={{ mb: 0.5 }}>
            <Link component="button" variant="caption" onClick={() => navigate('/sales/shipments')} underline="hover" color="inherit">Shipments</Link>
            <Typography variant="caption" color="text.primary">{isEdit ? 'Edit Shipment' : 'Create Shipment'}</Typography>
          </Breadcrumbs>
          <Typography variant="h4" sx={{ fontWeight: 900 }}>
            {isEdit ? `Edit Shipment ${existingShipment?.shipmentNo}` : 'New Logistic Shipment'}
          </Typography>
        </Box>
        <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate(-1)}>Back</Button>
      </Stack>

      <form onSubmit={handleSubmit}>
        <Grid container spacing={3}>
          <Grid size={{ xs: 12, lg: 8 }}>
            <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 3, color: 'primary.main' }}>SHIPMENT DETAILS</Typography>
              
              <Grid container spacing={2}>
                <Grid size={{ xs: 12, md: 6 }}>
                  <TextField
                    select
                    fullWidth
                    label="Select Sales Order"
                    name="orderId"
                    value={form.orderId}
                    onChange={handleInputChange}
                    disabled={isEdit || isFromOrder}
                    required
                    slotProps={{
                      inputLabel: { shrink: true }
                    }}
                  >
                    {/* A `select` TextField with no children is a MUI error AND an empty dropdown
                        the user gets no reason for. Say why it is empty — still loading, failed to
                        load, or genuinely no shippable orders — instead of rendering nothing. */}
                    {isFromOrder && selectedOrder ? (
                      <MenuItem value={selectedOrder.id}>{selectedOrder.orderNo} - {selectedOrder.customerName}</MenuItem>
                    ) : orders && orders.length > 0 ? (
                      orders.map(order => (
                        <MenuItem key={order.id} value={order.id}>{order.orderNo} - {order.customerName}</MenuItem>
                      ))
                    ) : (
                      <MenuItem value="" disabled>
                        {isLoadingOrders
                          ? 'Loading sales orders…'
                          : isOrdersError
                            ? 'Sales orders could not be loaded — reload the page to try again'
                            : 'No sales orders are available to ship'}
                      </MenuItem>
                    )}
                  </TextField>
                </Grid>
                <Grid size={{ xs: 12, md: 6 }}>
                  <TextField
                    select
                    fullWidth
                    label="Initial Status"
                    name="statusId"
                    value={form.statusId}
                    onChange={handleInputChange}
                    required
                    slotProps={{
                      inputLabel: { shrink: true }
                    }}
                  >
                    {statuses.length > 0 ? (
                      statuses.map(status => (
                        <MenuItem key={status.setupId} value={status.setupId}>{status.setupName}</MenuItem>
                      ))
                    ) : (
                      <MenuItem value="" disabled>
                        {isLoadingStatuses
                          ? 'Loading statuses…'
                          : isStatusesError
                            ? 'Shipment statuses could not be loaded — reload the page to try again'
                            : 'No shipment statuses are configured — add them under Setup'}
                      </MenuItem>
                    )}
                  </TextField>
                </Grid>
                <Grid size={{ xs: 12, md: 6 }}>
                  <TextField
                    fullWidth
                    label="Shipment Date"
                    type="date"
                    name="shipmentDate"
                    value={form.shipmentDate}
                    onChange={handleInputChange}
                    required
                    slotProps={{
                      inputLabel: { shrink: true }
                    }}
                  />
                </Grid>
                <Grid size={{ xs: 12, md: 6 }}>
                  <TextField
                    fullWidth
                    label="Est. Delivery Date"
                    type="date"
                    name="estimatedDeliveryDate"
                    value={form.estimatedDeliveryDate}
                    onChange={handleInputChange}
                    slotProps={{
                      inputLabel: { shrink: true }
                    }}
                  />
                </Grid>

                <Grid size={12}><Divider sx={{ my: 1 }} /></Grid>

                <Grid size={{ xs: 12, md: 4 }}>
                  <TextField
                    fullWidth
                    label="Carrier"
                    name="carrier"
                    placeholder="e.g. DHL, FedEx"
                    value={form.carrier}
                    onChange={handleInputChange}
                  />
                </Grid>
                <Grid size={{ xs: 12, md: 4 }}>
                  <TextField
                    fullWidth
                    label="Service Level"
                    name="serviceLevel"
                    placeholder="e.g. Express, Ground"
                    value={form.serviceLevel}
                    onChange={handleInputChange}
                  />
                </Grid>
                <Grid size={{ xs: 12, md: 4 }}>
                  <TextField
                    fullWidth
                    label="Tracking Number"
                    name="trackingNumber"
                    value={form.trackingNumber}
                    onChange={handleInputChange}
                  />
                </Grid>
                <Grid size={12}>
                  <TextField
                    fullWidth
                    label="Shipping Address"
                    name="shippingAddress"
                    multiline
                    rows={3}
                    value={form.shippingAddress}
                    onChange={handleInputChange}
                  />
                </Grid>
                <Grid size={12}>
                  <TextField
                    fullWidth
                    label="Internal Notes"
                    name="notes"
                    multiline
                    rows={2}
                    value={form.notes}
                    onChange={handleInputChange}
                  />
                </Grid>
                {/* FR-MTR-02. Shown only once the server has actually refused for a lapsed
                    certificate. Rendering it unconditionally would make signing for a documented
                    risk a field the despatch clerk fills in every time, and an override that is
                    always present stops meaning anything — which is why the server refuses one
                    supplied for material whose certificates are all in date. */}
                {needsComplianceOverride && (
                  <Grid size={12}>
                    <TextField
                      fullWidth
                      required
                      label="Certificate override — reason and authority"
                      name="complianceOverrideReason"
                      multiline
                      rows={2}
                      value={form.complianceOverrideReason}
                      onChange={handleInputChange}
                      helperText="Kept on the lot declaration permanently and shown on both traces. Your name is recorded with it."
                    />
                  </Grid>
                )}
              </Grid>
            </Paper>

            {submitError && (
              <Alert severity="error" sx={{ mt: 3 }}>{submitError}</Alert>
            )}

            <Paper sx={{ mt: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden' }}>
              <Box sx={{ p: 2, bgcolor: 'grey.50', borderBottom: '1px solid', borderColor: 'divider' }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>SHIPMENT ITEMS</Typography>
              </Box>
              <Table size="small">
                <TableHead>
                  <TableRow sx={{ bgcolor: 'grey.100' }}>
                    <TableCell sx={{ fontWeight: 800 }}>Product</TableCell>
                    <TableCell sx={{ fontWeight: 800 }} align="right">Order Qty (units)</TableCell>
                    <TableCell sx={{ fontWeight: 800 }} align="right">Already Shipped (units)</TableCell>
                    <TableCell sx={{ fontWeight: 800 }} align="right">Remaining (units)</TableCell>
                    <TableCell sx={{ fontWeight: 800 }} align="right" width={120}>Ship Qty (units)</TableCell>
                    <TableCell sx={{ fontWeight: 800 }}>Item Notes</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {form.items.length === 0 ? (
                    <TableRow>
                      <TableCell colSpan={6} align="center" sx={{ py: 3 }}>
                        <Typography variant="body2" color="text.secondary">Select an order to load items</Typography>
                      </TableCell>
                    </TableRow>
                  ) : (
                    form.items.map((item, index) => {
                      const orderItem = selectedOrder?.items.find(oi => oi.id === item.orderItemId);
                      const ordered = orderItem?.quantity ?? 0;
                      const alreadyShipped = shippedByLine.get(item.orderItemId) || 0;
                      const remaining = remainingFor(item.orderItemId, ordered);
                      const overRemaining = item.quantity > remaining;
                      return (
                        <TableRow key={index}>
                          <TableCell sx={{ fontWeight: 600 }}>{item.productName || orderItem?.productName}</TableCell>
                          <TableCell align="right">{ordered}</TableCell>
                          <TableCell align="right">{alreadyShipped}</TableCell>
                          <TableCell align="right" sx={{ fontWeight: 700 }}>{remaining}</TableCell>
                          <TableCell align="right">
                            <TextField
                              type="number"
                              size="small"
                              value={item.quantity}
                              onChange={(e) => handleItemQuantityChange(index, Number(e.target.value))}
                              error={overRemaining}
                              // The ceiling is REMAINING, and it is a real ceiling: the number
                              // input's `max` is advisory (it does not stop a paste or a keyed
                              // value), which is exactly why the server now enforces the same
                              // rule. This is a courtesy, not the control.
                              helperText={overRemaining
                                ? `Only ${remaining} left to ship on this line`
                                : undefined}
                              slotProps={{
                                htmlInput: { min: 0, max: remaining }
                              }}
                            />
                          </TableCell>
                          <TableCell>
                            <TextField
                              size="small"
                              fullWidth
                              placeholder="Notes..."
                              value={item.notes || ''}
                              onChange={(e) => {
                                const newItems = [...form.items];
                                newItems[index].notes = e.target.value;
                                setForm(prev => ({ ...prev, items: newItems }));
                              }}
                            />
                          </TableCell>
                        </TableRow>
                      );
                    })
                  )}
                </TableBody>
              </Table>
            </Paper>
          </Grid>

          <Grid size={{ xs: 12, lg: 4 }}>
            <Card sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', position: 'sticky', top: 24 }}>
              <CardContent>
                <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 2 }}>SUMMARY</Typography>
                {selectedOrder ? (
                  <Stack spacing={1.5}>
                    <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                      <Typography variant="body2" color="text.secondary">Customer</Typography>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>{selectedOrder.customerName}</Typography>
                    </Box>
                    <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                      <Typography variant="body2" color="text.secondary">Order Total</Typography>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>{formatMoney(selectedOrder.totalAmount, selectedOrder.currencyCode)}</Typography>
                    </Box>
                    <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                      <Typography variant="body2" color="text.secondary">Items to Ship</Typography>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>{form.items.length}</Typography>
                    </Box>
                    <Divider sx={{ my: 1 }} />
                    <Button
                      type="submit"
                      variant="contained"
                      fullWidth
                      size="large"
                      startIcon={<SaveIcon />}
                      disabled={mutation.isPending}
                      sx={{ py: 1.5, fontWeight: 800 }}
                    >
                      {mutation.isPending ? 'Saving...' : isEdit ? 'Update Shipment' : 'Create Shipment'}
                    </Button>
                    <Button
                      variant="outlined"
                      fullWidth
                      onClick={() => navigate(-1)}
                    >
                      Cancel
                    </Button>
                  </Stack>
                ) : (
                  <Typography variant="body2" color="text.secondary" align="center" sx={{ py: 3 }}>
                    Please select an order to see summary
                  </Typography>
                )}
              </CardContent>
            </Card>
          </Grid>
        </Grid>
      </form>
    </Box>
  );
};

export default CreateShipmentPage;
