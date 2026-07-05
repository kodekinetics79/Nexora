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
} from '@mui/material';
import {
  Add as AddIcon,
  Edit as EditIcon,
} from '@mui/icons-material';
import { DataGrid, type GridColDef, type GridPaginationModel } from '@mui/x-data-grid';
import businessUnitService, { type BusinessUnitDTO } from '../../../api/services/businessUnitService';
import SearchField from '../../../components/common/SearchField';
import { handleApiError } from '../../../utils/errorHandler';
import { useSnackbar } from 'notistack';

const BusinessUnitPage: React.FC = () => {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({
    pageSize: 10,
    page: 0,
  });

  const [search, setSearch] = useState('');
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedRecord, setSelectedRecord] = useState<BusinessUnitDTO | null>(null);

  const [formData, setFormData] = useState<Partial<BusinessUnitDTO>>({
    businessUnitCode: '',
    businessUnitName: '',
    description: '',
    isActive: true,
  });

  const { data, isLoading } = useQuery({
    queryKey: ['businessUnits', paginationModel, search],
    queryFn: () => businessUnitService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      businessUnitName: search || undefined
    }),
  });

  const createMutation = useMutation({
    mutationFn: (newRecord: any) => businessUnitService.create(newRecord),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['businessUnits'] });
      enqueueSnackbar('Business Unit created successfully', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (error: any) => handleApiError(error),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, updateData }: { id: number; updateData: any }) => businessUnitService.update(id, updateData),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['businessUnits'] });
      enqueueSnackbar('Business Unit updated successfully', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (error: any) => handleApiError(error),
  });

  const handleEdit = (record: BusinessUnitDTO) => {
    setSelectedRecord(record);
    setFormData({
      businessUnitCode: record.businessUnitCode,
      businessUnitName: record.businessUnitName,
      description: record.description,
      isActive: record.isActive,
    });
    setIsModalOpen(true);
  };

  const handleAddNew = () => {
    setSelectedRecord(null);
    setFormData({
      businessUnitCode: '',
      businessUnitName: '',
      description: '',
      isActive: true,
    });
    setIsModalOpen(true);
  };

  const handleSave = () => {
    const payload = {
      ...formData,
    };

    if (selectedRecord) {
      updateMutation.mutate({ id: selectedRecord.id, updateData: payload });
    } else {
      createMutation.mutate(payload);
    }
  };

  const columns: GridColDef[] = [
    { field: 'businessUnitCode', headerName: 'Code', flex: 0.8, minWidth: 100 },
    { field: 'businessUnitName', headerName: 'Name', flex: 1.5, minWidth: 200 },
    { field: 'description', headerName: 'Description', flex: 2, minWidth: 250 },
    {
      field: 'isActive',
      headerName: t('status'),
      flex: 1,
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
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>{t('business_unit')}</Typography>
          <Typography variant="body2" color="text.secondary">Configure and manage corporate business units</Typography>
        </Box>
        <Button variant="contained" startIcon={<AddIcon />} onClick={handleAddNew} sx={{ px: 3 }}>
          {t('create_new')}
        </Button>
      </Box>

      <Paper sx={{ p: 1, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', backgroundColor: 'background.paper', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search business units..." />
      </Paper>

      <Paper sx={{ width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid
          autoHeight
          rows={data?.items || []}
          columns={columns}
          rowCount={data?.totalCount || 0}
          loading={isLoading}
          pageSizeOptions={[10, 25, 50]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          disableRowSelectionOnClick
        />
      </Paper>

      <Dialog open={isModalOpen} onClose={() => setIsModalOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>{selectedRecord ? 'Edit Business Unit' : 'Add New Business Unit'}</DialogTitle>
        <DialogContent dividers sx={{ p: 3 }}>
          <Grid container spacing={3}>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField 
                fullWidth 
                label="BU Code" 
                value={formData.businessUnitCode} 
                onChange={(e) => setFormData({ ...formData, businessUnitCode: e.target.value })} 
                required 
              />
            </Grid>
            <Grid size={{ xs: 12, sm: 8 }}>
              <TextField 
                fullWidth 
                label="BU Name" 
                value={formData.businessUnitName} 
                onChange={(e) => setFormData({ ...formData, businessUnitName: e.target.value })} 
                required 
              />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <TextField 
                fullWidth 
                label="Description" 
                multiline 
                rows={3} 
                value={formData.description} 
                onChange={(e) => setFormData({ ...formData, description: e.target.value })} 
              />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <FormControlLabel 
                control={<Switch checked={formData.isActive} onChange={(e) => setFormData({ ...formData, isActive: e.target.checked })} />} 
                label="Active Status" 
              />
            </Grid>
          </Grid>
        </DialogContent>
        <DialogActions sx={{ p: 3 }}>
          <Button onClick={() => setIsModalOpen(false)}>Cancel</Button>
          <Button 
            variant="contained" 
            onClick={handleSave} 
            disabled={createMutation.isPending || updateMutation.isPending}
            sx={{ px: 4 }}
          >
            {(createMutation.isPending || updateMutation.isPending) ? <CircularProgress size={24} /> : 'Save'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default BusinessUnitPage;
