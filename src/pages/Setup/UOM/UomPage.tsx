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
import { DataGrid, type GridColDef } from '@mui/x-data-grid';
import uomService, { type UomDTO } from '../../../api/services/uomService';
import { useAuth } from '../../../context/AuthContext';
import SearchField from '../../../components/common/SearchField';
import { handleApiError } from '../../../utils/errorHandler';
import { useSnackbar } from 'notistack';

const UomPage: React.FC = () => {
  const { t } = useTranslation();
  const { userData } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [search, setSearch] = useState('');
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedRecord, setSelectedRecord] = useState<UomDTO | null>(null);

  const [formData, setFormData] = useState<Partial<UomDTO>>({
    uomCode: '',
    uomName: '',
    description: '',
    isActive: true,
  });

  const { data, isLoading } = useQuery({
    queryKey: ['uoms', userData.businessUnitId],
    queryFn: () => uomService.getAll(userData.businessUnitId || 1),
  });

  const filteredData = (data || []).filter(item =>
    item.uomName.toLowerCase().includes(search.toLowerCase()) ||
    item.uomCode.toLowerCase().includes(search.toLowerCase())
  );

  const createMutation = useMutation({
    mutationFn: (newRecord: any) => uomService.create(newRecord),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['uoms'] });
      enqueueSnackbar('UOM created successfully', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (error: any) => handleApiError(error),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, updateData }: { id: number; updateData: any }) => uomService.update(id, updateData),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['uoms'] });
      enqueueSnackbar('UOM updated successfully', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (error: any) => handleApiError(error),
  });

  const handleEdit = (record: UomDTO) => {
    setSelectedRecord(record);
    setFormData({
      uomCode: record.uomCode,
      uomName: record.uomName,
      description: record.description || '',
      isActive: record.isActive,
    });
    setIsModalOpen(true);
  };

  const handleAddNew = () => {
    setSelectedRecord(null);
    setFormData({
      uomCode: '',
      uomName: '',
      description: '',
      isActive: true,
    });
    setIsModalOpen(true);
  };

  const handleSave = () => {
    const businessUnitId = userData.businessUnitId || 1;
    const payload = {
      ...formData,
      businessUnitId,
    };

    if (selectedRecord) {
      updateMutation.mutate({ id: selectedRecord.uomId, updateData: payload });
    } else {
      createMutation.mutate(payload);
    }
  };

  const columns: GridColDef[] = [
    { field: 'uomCode', headerName: 'Code', flex: 0.5, minWidth: 100 },
    { field: 'uomName', headerName: 'Name', flex: 1, minWidth: 150 },
    { field: 'description', headerName: 'Description', flex: 1.5, minWidth: 200 },
    {
      field: 'isActive',
      headerName: t('status') || 'Status',
      flex: 0.5,
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
      headerName: t('actions') || 'Actions',
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
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>{t('uom')}</Typography>
          <Typography variant="body2" color="text.secondary">Manage units of measure for products and services</Typography>
        </Box>
        <Button variant="contained" startIcon={<AddIcon />} onClick={handleAddNew} sx={{ px: 3 }}>
          {t('create_new')}
        </Button>
      </Box>

      {/* Filter Bar */}
      <Paper sx={{ p: 1, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', backgroundColor: 'background.paper', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search UOMs..." />
      </Paper>

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 220px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid
          rows={filteredData}
          columns={columns}
          loading={isLoading}
          getRowId={(row) => row.uomId}
          disableRowSelectionOnClick
        />
      </Paper>

      {/* Add/Edit Modal */}
      <Dialog open={isModalOpen} onClose={() => setIsModalOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>{selectedRecord ? 'Edit UOM' : 'Add New UOM'}</DialogTitle>
        <DialogContent dividers sx={{ p: 3 }}>
          <Grid container spacing={3}>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField fullWidth label="UOM Code" value={formData.uomCode} onChange={(e) => setFormData({ ...formData, uomCode: e.target.value })} required />
            </Grid>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField fullWidth label="UOM Name" value={formData.uomName} onChange={(e) => setFormData({ ...formData, uomName: e.target.value })} required />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <TextField fullWidth label="Description" multiline rows={3} value={formData.description} onChange={(e) => setFormData({ ...formData, description: e.target.value })} />
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

export default UomPage;
