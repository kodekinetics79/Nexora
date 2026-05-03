import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, IconButton,
  Dialog, DialogTitle, DialogContent, DialogActions,
  Grid, TextField, MenuItem, Select, FormControl, InputLabel,
  Chip,
} from '@mui/material';
import {
  Add as AddIcon, Edit as EditIcon,
  ShoppingBag as POIcon,
  Receipt as InvoiceIcon,
} from '@mui/icons-material';
import { DataGrid, type GridColDef } from '@mui/x-data-grid';
import orderService, { type OrderDTO } from '../../api/services/orderService';
import supplierService from '../../api/services/supplierService';
import currencyService from '../../api/services/currencyService';
import { useAuth } from '../../context/AuthContext';
import { useSnackbar } from 'notistack';
import SearchField from '../../components/common/SearchField';

const emptyPO: Partial<OrderDTO> = {
  orderNumber: '',
  customerId: 0, // Using CustomerId as SupplierId for now if it's the same table or generic
  orderDate: new Date().toISOString().split('T')[0],
  status: 'Pending',
  totalAmount: 0,
};

const PurchaseOrdersPage: React.FC = () => {
  const { userData } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const buid = userData?.businessUnitId || 0;

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedPO, setSelectedPO] = useState<OrderDTO | null>(null);
  const [formData, setFormData] = useState(emptyPO);
  const [search, setSearch] = useState('');

  // ── Queries ──
  const { data: orders = [], isLoading } = useQuery({
    queryKey: ['orders', buid],
    queryFn: () => orderService.getAll(buid),
    enabled: !!buid,
  });

  const { data: suppliersData } = useQuery({
    queryKey: ['suppliers-list', buid],
    queryFn: () => supplierService.getAll({ businessUnitId: buid, pageSize: 1000 }),
    enabled: !!buid,
  });
  const suppliers = suppliersData?.items ?? [];

  useQuery({
    queryKey: ['currencies', buid],
    queryFn: () => currencyService.getAll({ businessUnitId: buid, pageSize: 1000 }),
    enabled: !!buid,
  });

  // ── Mutations ──
  const createMutation = useMutation({
    mutationFn: (data: any) => orderService.createManual(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['orders'] });
      enqueueSnackbar('Purchase order created', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (err: any) => enqueueSnackbar(err.message || 'Failed to create PO', { variant: 'error' }),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: number; data: any }) => orderService.update(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['orders'] });
      enqueueSnackbar('Purchase order updated', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (err: any) => enqueueSnackbar(err.message || 'Failed to update PO', { variant: 'error' }),
  });

  // ── Handlers ──
  const handleAddNew = () => {
    setSelectedItem(null);
    setFormData({ ...emptyPO, orderNumber: `PO-${Date.now().toString().slice(-6)}` });
    setIsModalOpen(true);
  };

  const setSelectedItem = (item: OrderDTO | null) => {
    setSelectedPO(item);
  };

  const handleEdit = (item: OrderDTO) => {
    setSelectedPO(item);
    setFormData({
      ...item,
      orderDate: item.orderDate ? item.orderDate.split('T')[0] : '',
    });
    setIsModalOpen(true);
  };

  const handleSave = () => {
    const data = {
      ...formData,
      businessUnitId: buid,
    };
    if (selectedPO) {
      updateMutation.mutate({ id: selectedPO.id, data });
    } else {
      createMutation.mutate(data);
    }
  };

  const f = (field: string) => (e: any) => setFormData(p => ({ ...p, [field]: e.target.value }));

  // ── Grid Columns ──
  const columns: GridColDef[] = [
    { 
      field: 'orderNumber', 
      headerName: 'PO Number', 
      width: 150,
      renderCell: (p) => <Typography variant="subtitle2" sx={{ fontWeight: 800, color: 'primary.main' }}>{p.value}</Typography>
    },
    { field: 'customerName', headerName: 'Supplier', flex: 1 }, // Mapping customerName to Supplier for this view
    { field: 'orderDate', headerName: 'Order Date', width: 130, renderCell: (p) => new Date(p.value).toLocaleDateString() },
    { 
      field: 'totalAmount', 
      headerName: 'Total Amount', 
      width: 150,
      renderCell: (p) => (
        <Typography variant="body2" sx={{ fontWeight: 700 }}>
          {p.value?.toLocaleString(undefined, { minimumFractionDigits: 2 })}
        </Typography>
      )
    },
    { 
      field: 'status', 
      headerName: 'Status', 
      width: 130,
      renderCell: (p) => {
        const colors: Record<string, any> = { 'Pending': 'warning', 'Completed': 'success', 'Cancelled': 'error' };
        return <Chip label={p.value} color={colors[p.value] || 'default'} size="small" sx={{ fontWeight: 700 }} />;
      }
    },
    {
      field: 'actions',
      headerName: 'Actions',
      width: 100,
      sortable: false,
      renderCell: (p) => (
        <Box>
          <IconButton size="small" color="primary" onClick={() => handleEdit(p.row)}><EditIcon fontSize="small" /></IconButton>
          <IconButton size="small" color="info" onClick={() => enqueueSnackbar('Invoice generation coming soon', { variant: 'info' })}><InvoiceIcon fontSize="small" /></IconButton>
        </Box>
      )
    }
  ];

  return (
    <Box sx={{ p: 3 }}>
      {/* Header */}
      <Box sx={{ mb: 3, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>Purchase Orders</Typography>
          <Typography variant="body2" color="text.secondary">Manage and track all purchase orders issued to your suppliers</Typography>
        </Box>
        <Button variant="contained" startIcon={<AddIcon />} onClick={handleAddNew} sx={{ px: 3, borderRadius: 2, height: 48 }}>
          Create New PO
        </Button>
      </Box>

      {/* Stats Bar */}
      <Grid container spacing={2} sx={{ mb: 3 }}>
        {['Total POs', 'Pending', 'Delivered', 'Overdue'].map((label, i) => (
          <Grid key={label} size={{ xs: 12, sm: 3 }}>
            <Paper sx={{ p: 2, borderRadius: 2, border: '1px solid', borderColor: 'divider', textAlign: 'center' }}>
              <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.secondary', textTransform: 'uppercase' }}>{label}</Typography>
              <Typography variant="h5" sx={{ fontWeight: 800 }}>{i === 0 ? orders.length : 0}</Typography>
            </Paper>
          </Grid>
        ))}
      </Grid>

      {/* Search */}
      <Paper sx={{ p: 1, mb: 2, display: 'flex', alignItems: 'center', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search by PO number or supplier..." />
      </Paper>

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 340px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid
          rows={orders}
          columns={columns}
          loading={isLoading}
          getRowId={(r) => r.id}
          disableRowSelectionOnClick
          sx={{ border: 'none' }}
        />
      </Paper>

      {/* Dialog */}
      <Dialog open={isModalOpen} onClose={() => setIsModalOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800, display: 'flex', alignItems: 'center', gap: 1 }}>
          <POIcon color="primary" />
          {selectedPO ? `Edit PO: ${selectedPO.orderNumber}` : 'Create Purchase Order'}
        </DialogTitle>
        <DialogContent dividers sx={{ p: 3 }}>
          <Grid container spacing={3}>
            <Grid size={{ xs: 12 }}>
              <TextField fullWidth label="PO Number *" value={formData.orderNumber} onChange={f('orderNumber')} />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <FormControl fullWidth>
                <InputLabel>Supplier *</InputLabel>
                <Select value={formData.customerId} label="Supplier *" onChange={f('customerId')}>
                  {suppliers.map((s: any) => (
                    <MenuItem key={s.id} value={s.id}>{s.name}</MenuItem>
                  ))}
                </Select>
              </FormControl>
            </Grid>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField fullWidth type="date" label="Order Date" value={formData.orderDate} onChange={f('orderDate')} slotProps={{ inputLabel: { shrink: true } }} />
            </Grid>
            <Grid size={{ xs: 12, sm: 6 }}>
              <FormControl fullWidth>
                <InputLabel>Status</InputLabel>
                <Select value={formData.status} label="Status" onChange={f('status')}>
                  <MenuItem value="Pending">Pending</MenuItem>
                  <MenuItem value="Approved">Approved</MenuItem>
                  <MenuItem value="Completed">Completed</MenuItem>
                  <MenuItem value="Cancelled">Cancelled</MenuItem>
                </Select>
              </FormControl>
            </Grid>
            <Grid size={{ xs: 12 }}>
              <TextField fullWidth type="number" label="Total Amount" value={formData.totalAmount} onChange={f('totalAmount')} />
            </Grid>
          </Grid>
        </DialogContent>
        <DialogActions sx={{ p: 2.5 }}>
          <Button onClick={() => setIsModalOpen(false)} color="inherit" sx={{ fontWeight: 700 }}>Cancel</Button>
          <Button 
            variant="contained" 
            onClick={handleSave} 
            disabled={createMutation.isPending || updateMutation.isPending}
            sx={{ px: 4, borderRadius: 2, fontWeight: 700 }}
          >
            {selectedPO ? 'Update PO' : 'Create PO'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default PurchaseOrdersPage;
