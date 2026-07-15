import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box,
  Typography,
  Paper,
  Button,
  Chip,
  IconButton,
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  Grid,
  FormControlLabel,
  Switch,
  TextField,
  CircularProgress,
  MenuItem,
} from '@mui/material';
import {
  Add as AddIcon,
  Edit as EditIcon,
} from '@mui/icons-material';
import { DataGrid, type GridColDef, type GridPaginationModel } from '@mui/x-data-grid';
import warehouseService, { type WarehouseDTO } from '../../../api/services/warehouseService';
import { useAuth } from '../../../context/AuthContext';
import SearchField from '../../../components/common/SearchField';
import { handleApiError } from '../../../utils/errorHandler';
import { useSnackbar } from 'notistack';
import countryService from '../../../api/services/countryService';
import stateService from '../../../api/services/stateService';
import cityService from '../../../api/services/cityService';

const WarehousePage: React.FC = () => {
  const { t } = useTranslation();
  const { userData } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({
    pageSize: 10,
    page: 0,
  });

  const [search, setSearch] = useState('');
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedRecord, setSelectedRecord] = useState<WarehouseDTO | null>(null);

  const [formData, setFormData] = useState<Partial<WarehouseDTO>>({
    warehouseCode: '',
    warehouseName: '',
    location: '',
    addressLine1: '',
    addressLine2: '',
    city: '',
    state: '',
    country: '',
    postalCode: '',
    capacity: 0,
    managerName: '',
    contactPhone: '',
    contactEmail: '',
    isActive: true,
  });

  const { data, isLoading } = useQuery({
    queryKey: ['warehouses', paginationModel, search],
    queryFn: () => warehouseService.getAll({
      businessUnitId: userData.businessUnitId || 1,
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      warehouseName: search || undefined
    }),
  });

  const { data: countries } = useQuery({
    queryKey: ['countries', userData.businessUnitId],
    queryFn: () => countryService.getAll(userData.businessUnitId || 1),
  });

  const { data: states } = useQuery({
    queryKey: ['states', userData.businessUnitId],
    queryFn: () => stateService.getAll(userData.businessUnitId || 1),
  });

  const { data: cities } = useQuery({
    queryKey: ['cities', userData.businessUnitId],
    queryFn: () => cityService.getAll(userData.businessUnitId || 1),
  });

  const filteredStates = states?.filter(s => {
    const selectedCountry = countries?.find(c => c.countryName === formData.country);
    return selectedCountry ? s.countryId === selectedCountry.countryId : false;
  }) || [];

  const filteredCities = cities?.filter(c => {
    const selectedState = states?.find(s => s.stateName === formData.state);
    return selectedState ? c.stateId === selectedState.stateId : false;
  }) || [];

  const createMutation = useMutation({
    mutationFn: (newRecord: any) => warehouseService.create(newRecord),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['warehouses'] });
      enqueueSnackbar('Warehouse created successfully', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (error: any) => handleApiError(error),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, updateData }: { id: number; updateData: any }) => warehouseService.update(id, updateData),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['warehouses'] });
      enqueueSnackbar('Warehouse updated successfully', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (error: any) => handleApiError(error),
  });

  const handleEdit = (record: WarehouseDTO) => {
    setSelectedRecord(record);
    setFormData({ ...record });
    setIsModalOpen(true);
  };

  const handleAddNew = () => {
    setSelectedRecord(null);
    setFormData({
      warehouseCode: '',
      warehouseName: '',
      location: '',
      addressLine1: '',
      addressLine2: '',
      city: '',
      state: '',
      country: '',
      postalCode: '',
      capacity: 0,
      managerName: '',
      contactPhone: '',
      contactEmail: '',
      isActive: true,
    });
    setIsModalOpen(true);
  };

  const handleSave = () => {
    const businessUnitId = userData.businessUnitId || 1;
    const payload = {
      ...formData,
      businessUnitId,
      createdBy: userData.userName || 'System',
      modifiedBy: userData.userName || 'System',
    };

    if (selectedRecord) {
      updateMutation.mutate({ id: selectedRecord.id, updateData: payload });
    } else {
      createMutation.mutate(payload);
    }
  };

  const columns: GridColDef[] = [
    { field: 'warehouseCode', headerName: 'Code', flex: 0.8, minWidth: 100 },
    { field: 'warehouseName', headerName: 'Name', flex: 1.5, minWidth: 150 },
    { field: 'location', headerName: 'Location', flex: 1, minWidth: 120 },
    { field: 'city', headerName: 'City', flex: 0.8, minWidth: 100 },
    { field: 'managerName', headerName: 'Manager', flex: 1, minWidth: 120 },
    {
      field: 'isActive',
      headerName: t('status'),
      flex: 0.8,
      minWidth: 100,
      renderCell: (params) => (
        <Chip
          label={params.value ? 'Active' : 'Inactive'}
          color={params.value ? 'success' : 'error'}
          size="small"
          variant="outlined"
        />
      ),
    },
    {
      field: 'actions',
      headerName: t('actions'),
      width: 80,
      sortable: false,
      renderCell: (params) => (
        <IconButton size="small" color="primary" onClick={() => handleEdit(params.row)}>
          <EditIcon fontSize="small" />
        </IconButton>
      ),
    },
  ];

  return (
    <Box sx={{ width: '100%', px: 1, py: 1 }}>
      {/* Header */}
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>Warehouse Setup</Typography>
          <Typography variant="body2" color="text.secondary">Manage system warehouses and storage locations</Typography>
        </Box>
        <Button variant="contained" startIcon={<AddIcon />} onClick={handleAddNew} sx={{ px: 3 }}>
          Add Warehouse
        </Button>
      </Box>

      {/* Search */}
      <Paper sx={{ p: 1, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', backgroundColor: 'background.paper', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search warehouses..." />
      </Paper>

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 220px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid
          rows={data?.items || []}
          columns={columns}
          rowCount={data?.totalItems || 0}
          loading={isLoading}
          pageSizeOptions={[10, 25, 50]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          getRowId={(row) => row.id}
          disableRowSelectionOnClick
        />
      </Paper>

      {/* Modal */}
      <Dialog open={isModalOpen} onClose={() => setIsModalOpen(false)} fullWidth maxWidth="md">
        <DialogTitle sx={{ fontWeight: 800 }}>{selectedRecord ? 'Edit Warehouse' : 'Add New Warehouse'}</DialogTitle>
        <DialogContent dividers sx={{ p: 3 }}>
          <Grid container spacing={3}>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField fullWidth label="Warehouse Code" value={formData.warehouseCode} onChange={(e) => setFormData({ ...formData, warehouseCode: e.target.value })} required />
            </Grid>
            <Grid size={{ xs: 12, sm: 8 }}>
              <TextField fullWidth label="Warehouse Name" value={formData.warehouseName} onChange={(e) => setFormData({ ...formData, warehouseName: e.target.value })} required />
            </Grid>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField fullWidth label="Location" value={formData.location} onChange={(e) => setFormData({ ...formData, location: e.target.value })} />
            </Grid>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField fullWidth label="Manager Name" value={formData.managerName} onChange={(e) => setFormData({ ...formData, managerName: e.target.value })} />
            </Grid>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField fullWidth label="Contact Phone" value={formData.contactPhone} onChange={(e) => setFormData({ ...formData, contactPhone: e.target.value })} />
            </Grid>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField fullWidth label="Contact Email" value={formData.contactEmail} onChange={(e) => setFormData({ ...formData, contactEmail: e.target.value })} />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <TextField fullWidth label="Address Line 1" value={formData.addressLine1} onChange={(e) => setFormData({ ...formData, addressLine1: e.target.value })} />
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField
                select
                fullWidth
                label="Country"
                value={formData.country || ''}
                onChange={(e) => setFormData({ ...formData, country: e.target.value, state: '', city: '' })}
              >
                {countries?.map(c => <MenuItem key={c.countryId} value={c.countryName}>{c.countryName}</MenuItem>)}
              </TextField>
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField
                select
                fullWidth
                label="State"
                value={formData.state || ''}
                disabled={!formData.country}
                onChange={(e) => setFormData({ ...formData, state: e.target.value, city: '' })}
              >
                {filteredStates.map(s => <MenuItem key={s.stateId} value={s.stateName}>{s.stateName}</MenuItem>)}
              </TextField>
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField
                select
                fullWidth
                label="City"
                value={formData.city || ''}
                disabled={!formData.state}
                onChange={(e) => setFormData({ ...formData, city: e.target.value })}
              >
                {filteredCities.map(c => <MenuItem key={c.cityId} value={c.cityName}>{c.cityName}</MenuItem>)}
              </TextField>
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField fullWidth label="Postal Code" value={formData.postalCode} onChange={(e) => setFormData({ ...formData, postalCode: e.target.value })} />
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField fullWidth type="number" label="Capacity" value={formData.capacity} onChange={(e) => setFormData({ ...formData, capacity: parseFloat(e.target.value) })} />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <FormControlLabel control={<Switch checked={formData.isActive} onChange={(e) => setFormData({ ...formData, isActive: e.target.checked })} />} label="Is Active" />
            </Grid>
          </Grid>
        </DialogContent>
        <DialogActions sx={{ p: 3 }}>
          <Button onClick={() => setIsModalOpen(false)}>Cancel</Button>
          <Button variant="contained" onClick={handleSave} disabled={createMutation.isPending || updateMutation.isPending}>
            {(createMutation.isPending || updateMutation.isPending) ? <CircularProgress size={24} /> : 'Save'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default WarehousePage;
