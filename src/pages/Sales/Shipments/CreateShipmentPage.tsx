import React, { useState, useEffect } from 'react';
import { useParams, useNavigate, useLocation } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Grid, TextField, Button, Divider, Table,
  TableHead, TableRow, TableCell, TableBody, MenuItem, Stack,
  Card, CardContent, CircularProgress, Breadcrumbs, Link
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
  items: ShipmentItemDTO[];
}

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
    items: [],
  });

  // Fetch orders for selection
  const { data: orders } = useQuery({
    queryKey: ['orders-for-shipment', businessUnitId],
    queryFn: () => orderService.getAll({ businessUnitId, pageSize: 100 }),
    enabled: !isEdit && !isFromOrder,
  });

  // Fetch statuses
  const { data: statusesData } = useQuery({
    queryKey: ['shipment-statuses'],
    queryFn: () => setupService.getAll({ setupType: 'ShipmentStatus' }),
  });
  const statuses = statusesData?.items || [];

  // Fetch existing shipment if editing
  const { data: existingShipment, isLoading: isLoadingShipment } = useQuery({
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
        items: existingShipment.items,
      });
    } else if (selectedOrder && !isEdit && form.items.length === 0) {
      setForm(prev => ({
        ...prev,
        shippingAddress: selectedOrder.notes || '', // Default address from order notes
        items: selectedOrder.items.map(item => ({
          orderItemId: item.id,
          productName: item.productName,
          quantity: item.quantity,
          notes: ''
        }))
      }));
    }
  }, [isEdit, existingShipment, selectedOrder, form.items.length]);

  const mutation = useMutation({
    mutationFn: (data: CreateShipmentDTO) => 
      isEdit ? shipmentService.update(Number(shipmentId), data) : shipmentService.create(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['shipments'] });
      navigate('/sales/shipments');
    },
  });

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
                    {isFromOrder && selectedOrder ? (
                      <MenuItem value={selectedOrder.id}>{selectedOrder.orderNo} - {selectedOrder.customerName}</MenuItem>
                    ) : (
                      orders?.map(order => (
                        <MenuItem key={order.id} value={order.id}>{order.orderNo} - {order.customerName}</MenuItem>
                      ))
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
                    {statuses.map(status => (
                      <MenuItem key={status.setupId} value={status.setupId}>{status.setupName}</MenuItem>
                    ))}
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
              </Grid>
            </Paper>

            <Paper sx={{ mt: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden' }}>
              <Box sx={{ p: 2, bgcolor: 'grey.50', borderBottom: '1px solid', borderColor: 'divider' }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>SHIPMENT ITEMS</Typography>
              </Box>
              <Table size="small">
                <TableHead>
                  <TableRow sx={{ bgcolor: 'grey.100' }}>
                    <TableCell sx={{ fontWeight: 800 }}>Product</TableCell>
                    <TableCell sx={{ fontWeight: 800 }} align="right">Order Qty</TableCell>
                    <TableCell sx={{ fontWeight: 800 }} align="right" width={120}>Ship Qty</TableCell>
                    <TableCell sx={{ fontWeight: 800 }}>Item Notes</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {form.items.length === 0 ? (
                    <TableRow>
                      <TableCell colSpan={4} align="center" sx={{ py: 3 }}>
                        <Typography variant="body2" color="text.secondary">Select an order to load items</Typography>
                      </TableCell>
                    </TableRow>
                  ) : (
                    form.items.map((item, index) => {
                      const orderItem = selectedOrder?.items.find(oi => oi.id === item.orderItemId);
                      return (
                        <TableRow key={index}>
                          <TableCell sx={{ fontWeight: 600 }}>{item.productName || orderItem?.productName}</TableCell>
                          <TableCell align="right">{orderItem?.quantity || 0}</TableCell>
                          <TableCell align="right">
                            <TextField
                              type="number"
                              size="small"
                              value={item.quantity}
                              onChange={(e) => handleItemQuantityChange(index, Number(e.target.value))}
                              slotProps={{
                                htmlInput: { min: 1, max: orderItem?.quantity || 9999 }
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
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>$ {selectedOrder.totalAmount.toLocaleString()}</Typography>
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
