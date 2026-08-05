import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, IconButton,
  Dialog, DialogTitle, DialogContent, DialogActions,
  Grid, TextField, MenuItem, Select, FormControl, InputLabel,
  CircularProgress, Stack, Chip, Divider,
} from '@mui/material';
import {
  Add as AddIcon, Edit as EditIcon, Delete as DeleteIcon,
  RequestPage as QuoteIcon,
  Handshake as SupplierIcon, Category as ItemIcon,
} from '@mui/icons-material';
import { DataGrid, type GridColDef } from '@mui/x-data-grid';
import supplierQuotedItemService, { type SupplierQuotedItemDTO } from '../../api/services/supplierQuotedItemService';
import supplierService from '../../api/services/supplierService';
import currencyService from '../../api/services/currencyService';
import uomService from '../../api/services/uomService';
import { useAuth } from '../../context/AuthContext';
import { useSnackbar } from 'notistack';
import SearchField from '../../components/common/SearchField';

const emptyItem: Partial<SupplierQuotedItemDTO> = {
  supplierId: 0,
  itemName: '',
  description: '',
  uomId: 0,
  quantity: 1,
  unitPrice: 0,
  currencyId: 0,
  quoteReference: '',
  quoteDate: new Date().toISOString().split('T')[0],
  validUntil: '',
  taxAmount: 0,
  discountAmount: 0,
  isActive: true,
};

const QuotedItemsPage: React.FC = () => {
  const { t } = useTranslation();
  const { userData, hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const buid = userData?.businessUnitId || 0;
  const canCreate = hasPermission('Supplier History', 'create');
  const canEdit = hasPermission('Supplier History', 'edit');
  const canDelete = hasPermission('Supplier History', 'delete');

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedItem, setSelectedItem] = useState<SupplierQuotedItemDTO | null>(null);
  const [formData, setFormData] = useState(emptyItem);
  const [search, setSearch] = useState('');

  // ── Queries ──
  const { data: quotedItems = [], isLoading } = useQuery({
    queryKey: ['quoted-items', buid],
    queryFn: () => supplierQuotedItemService.getAll(buid),
    enabled: !!buid,
  });

  const { data: suppliersData } = useQuery({
    queryKey: ['suppliers-list', buid],
    queryFn: () => supplierService.getAll({ businessUnitId: buid, pageSize: 1000 }),
    enabled: !!buid,
  });
  const suppliers = suppliersData?.items ?? [];

  const { data: currenciesData } = useQuery({
    queryKey: ['currencies', buid],
    queryFn: () => currencyService.getAll({ businessUnitId: buid, pageSize: 1000 }),
    enabled: !!buid,
  });
  const currencies = currenciesData?.items ?? [];

  const { data: uoms = [] } = useQuery({
    queryKey: ['uoms', buid],
    queryFn: () => uomService.getAll(buid),
    enabled: !!buid,
  });

  // ── Mutations ──
  const createMutation = useMutation({
    mutationFn: (data: any) => supplierQuotedItemService.create(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quoted-items'] });
      enqueueSnackbar('Quoted item added successfully', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (err: any) => enqueueSnackbar(err.message || 'Failed to add item', { variant: 'error' }),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: number; data: any }) => supplierQuotedItemService.update(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quoted-items'] });
      enqueueSnackbar('Quoted item updated successfully', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (err: any) => enqueueSnackbar(err.message || 'Failed to update item', { variant: 'error' }),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => supplierQuotedItemService.delete(id, buid),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quoted-items'] });
      enqueueSnackbar('Item deleted', { variant: 'info' });
    },
  });

  // ── Handlers ──
  const handleAddNew = () => {
    if (!canCreate) return;
    setSelectedItem(null);
    setFormData(emptyItem);
    setIsModalOpen(true);
  };

  const handleEdit = (item: SupplierQuotedItemDTO) => {
    if (!canEdit) return;
    setSelectedItem(item);
    setFormData({
      ...item,
      quoteDate: item.quoteDate ? item.quoteDate.split('T')[0] : '',
      validUntil: item.validUntil ? item.validUntil.split('T')[0] : '',
    });
    setIsModalOpen(true);
  };

  const handleSave = () => {
    if (selectedItem ? !canEdit : !canCreate) return;
    const data = {
      ...formData,
      businessUnitId: buid,
      createdBy: userData?.userName || 'System',
    };
    if (selectedItem) {
      updateMutation.mutate({ id: selectedItem.id, data: { ...data, id: selectedItem.id } });
    } else {
      createMutation.mutate(data);
    }
  };

  const f = (field: string) => (e: any) => setFormData(p => ({ ...p, [field]: e.target.value }));

  // ── Grid Columns ──
  const columns: GridColDef[] = [
    { 
      field: 'itemName', 
      headerName: 'Item Name', 
      flex: 1.5,
      renderCell: (p) => (
        <Box sx={{ py: 1 }}>
          <Typography variant="subtitle2" sx={{ fontWeight: 700 }}>{p.value}</Typography>
          <Typography variant="caption" color="text.secondary" noWrap sx={{ display: 'block' }}>{p.row.description}</Typography>
        </Box>
      )
    },
    { field: 'supplierName', headerName: t('supplier'), flex: 1, renderCell: (p) => <Chip label={p.value || '—'} size="small" variant="outlined" sx={{ fontWeight: 600 }} /> },
    { 
      field: 'quantity', 
      headerName: 'Quantity', 
      width: 120, 
      renderCell: (p) => (
        <Typography variant="body2" sx={{ fontWeight: 600 }}>
          {p.value} <Typography component="span" variant="caption" color="text.secondary">{p.row.uomName}</Typography>
        </Typography>
      )
    },
    { 
      field: 'unitPrice', 
      headerName: 'Price', 
      width: 130, 
      renderCell: (p) => (
        <Typography variant="body2" color="primary.main" sx={{ fontWeight: 800 }}>
          {p.row.currencyName} {p.value?.toLocaleString()}
        </Typography>
      )
    },
    { field: 'quoteReference', headerName: 'Ref #', width: 120 },
    { field: 'validUntil', headerName: 'Valid Until', width: 120, renderCell: (p) => p.value ? new Date(p.value).toLocaleDateString() : '—' },
    {
      field: 'actions',
      headerName: t('actions'),
      width: 100,
      sortable: false,
      renderCell: (p) => (
        <Box>
          {canEdit && <IconButton aria-label="Edit quoted item" size="small" color="primary" onClick={() => handleEdit(p.row)}><EditIcon fontSize="small" /></IconButton>}
          {canDelete && <IconButton aria-label="Delete quoted item" size="small" color="error" onClick={() => deleteMutation.mutate(p.row.id)}><DeleteIcon fontSize="small" /></IconButton>}
        </Box>
      )
    }
  ];

  const filteredItems = (quotedItems as SupplierQuotedItemDTO[]).filter(item => 
    item.itemName.toLowerCase().includes(search.toLowerCase()) || 
    item.supplierName?.toLowerCase().includes(search.toLowerCase()) ||
    item.quoteReference?.toLowerCase().includes(search.toLowerCase())
  );

  return (
    <Box sx={{ p: 3 }}>
      {/* Header */}
      <Box sx={{ mb: 3, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>Supplier Quoted Items</Typography>
          <Typography variant="body2" color="text.secondary">Maintain a master list of all prices and items quoted by your vendors</Typography>
        </Box>
        {canCreate && <Button variant="contained" startIcon={<AddIcon />} onClick={handleAddNew} sx={{ px: 3, borderRadius: 2, height: 48 }}>
          New Quote Item
        </Button>}
      </Box>

      {/* Search */}
      <Paper sx={{ p: 1, mb: 2, display: 'flex', alignItems: 'center', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search by item, supplier, or reference..." />
      </Paper>

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 240px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid
          rows={filteredItems}
          columns={columns}
          loading={isLoading}
          getRowId={(r) => r.id}
          disableRowSelectionOnClick
          rowHeight={65}
          sx={{ border: 'none' }}
        />
      </Paper>

      {/* Dialog */}
      <Dialog open={isModalOpen} onClose={() => setIsModalOpen(false)} fullWidth maxWidth="md">
        <DialogTitle sx={{ fontWeight: 800, display: 'flex', alignItems: 'center', gap: 1 }}>
          <QuoteIcon color="primary" />
          {selectedItem ? `Edit Quote: ${selectedItem.itemName}` : 'Record New Quote Item'}
        </DialogTitle>
        <DialogContent dividers sx={{ p: 3 }}>
          <Grid container spacing={3}>
            {/* Section: Supplier & Item */}
            <Grid size={{ xs: 12 }}>
              <Stack direction="row" spacing={1} sx={{ mb: 1, alignItems: 'center' }}>
                <SupplierIcon color="primary" fontSize="small" />
                <Typography variant="subtitle2" sx={{ fontWeight: 800, textTransform: 'uppercase' }}>Supplier & Product</Typography>
              </Stack>
              <Grid container spacing={2}>
                <Grid size={{ xs: 12, sm: 6 }}>
                  <FormControl fullWidth>
                    <InputLabel>Supplier *</InputLabel>
                    <Select value={formData.supplierId} label="Supplier *" onChange={f('supplierId')}>
                      {suppliers.map((s: any) => (
                        <MenuItem key={s.id} value={s.id}>{s.name}</MenuItem>
                      ))}
                    </Select>
                  </FormControl>
                </Grid>
                <Grid size={{ xs: 12, sm: 6 }}>
                  <TextField fullWidth label="Item Name *" value={formData.itemName} onChange={f('itemName')} />
                </Grid>
                <Grid size={{ xs: 12 }}>
                  <TextField fullWidth multiline rows={2} label="Description" value={formData.description} onChange={f('description')} />
                </Grid>
              </Grid>
            </Grid>

            {/* Section: Pricing & Quantity */}
            <Grid size={{ xs: 12 }}>
              <Divider sx={{ my: 1 }} />
              <Stack direction="row" spacing={1} sx={{ mb: 1, mt: 1, alignItems: 'center' }}>
                <ItemIcon color="primary" fontSize="small" />
                <Typography variant="subtitle2" sx={{ fontWeight: 800, textTransform: 'uppercase' }}>Pricing & Units</Typography>
              </Stack>
              <Grid container spacing={2}>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <TextField fullWidth type="number" label="Quantity *" value={formData.quantity} onChange={f('quantity')} />
                </Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <FormControl fullWidth>
                    <InputLabel>UOM *</InputLabel>
                    <Select value={formData.uomId} label="UOM *" onChange={f('uomId')}>
                      {uoms.map((u: any) => (
                        <MenuItem key={u.uomId} value={u.uomId}>{u.uomName}</MenuItem>
                      ))}
                    </Select>
                  </FormControl>
                </Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <TextField fullWidth type="number" label="Unit Price *" value={formData.unitPrice} onChange={f('unitPrice')} />
                </Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <FormControl fullWidth>
                    <InputLabel>Currency *</InputLabel>
                    <Select value={formData.currencyId} label="Currency *" onChange={f('currencyId')}>
                      {currencies.map((c: any) => (
                        <MenuItem key={c.id} value={c.id}>{c.currencyName} ({c.code})</MenuItem>
                      ))}
                    </Select>
                  </FormControl>
                </Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <TextField fullWidth type="number" label="Tax Amount" value={formData.taxAmount} onChange={f('taxAmount')} />
                </Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <TextField fullWidth type="number" label="Discount" value={formData.discountAmount} onChange={f('discountAmount')} />
                </Grid>
              </Grid>
            </Grid>

            {/* Section: Reference */}
            <Grid size={{ xs: 12 }}>
              <Divider sx={{ my: 1 }} />
              <Typography variant="subtitle2" sx={{ fontWeight: 800, textTransform: 'uppercase', mb: 1, mt: 1 }}>Quote Reference</Typography>
              <Grid container spacing={2}>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <TextField fullWidth label="Reference #" value={formData.quoteReference} onChange={f('quoteReference')} />
                </Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <TextField fullWidth type="date" label="Quote Date" value={formData.quoteDate} onChange={f('quoteDate')} slotProps={{ inputLabel: { shrink: true } }} />
                </Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <TextField fullWidth type="date" label="Valid Until" value={formData.validUntil} onChange={f('validUntil')} slotProps={{ inputLabel: { shrink: true } }} />
                </Grid>
              </Grid>
            </Grid>
          </Grid>
        </DialogContent>
        <DialogActions sx={{ p: 2.5 }}>
          <Button onClick={() => setIsModalOpen(false)} color="inherit" sx={{ fontWeight: 700 }}>Cancel</Button>
          <Button 
            variant="contained" 
            onClick={handleSave} 
            disabled={createMutation.isPending || updateMutation.isPending}
            startIcon={(createMutation.isPending || updateMutation.isPending) && <CircularProgress size={18} />}
            sx={{ px: 4, borderRadius: 2, fontWeight: 700 }}
          >
            {selectedItem ? 'Update Item' : 'Save Quoted Item'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default QuotedItemsPage;
