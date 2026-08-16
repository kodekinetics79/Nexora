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
import currencyService, { type CurrencyDTO } from '../../../api/services/currencyService';
import { useAuth } from '../../../context/AuthContext';
import SearchField from '../../../components/common/SearchField';
// The grid is sized against the viewport, and the Setup breadcrumb bar is part of that viewport.
import { SETUP_CHROME_HEIGHT } from '../SetupShell';
import { handleApiError } from '../../../utils/errorHandler';
import { useSnackbar } from 'notistack';

const CurrencyPage: React.FC = () => {
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
  const [selectedRecord, setSelectedRecord] = useState<CurrencyDTO | null>(null);

  const [formData, setFormData] = useState<Partial<CurrencyDTO>>({
    code: '',
    currencyName: '',
    symbol: '',
    exchangeRate: 1,
    isBaseCurrency: false,
    isActive: true,
  });

  const { data, isLoading } = useQuery({
    queryKey: ['currencies', paginationModel, search],
    queryFn: () => currencyService.getAll({
      businessUnitId: userData.businessUnitId || 1,
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      currencyName: search || undefined
    }),
  });

  const createMutation = useMutation({
    mutationFn: (newRecord: any) => currencyService.create(newRecord),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['currencies'] });
      enqueueSnackbar('Currency created successfully', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (error: any) => handleApiError(error),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, updateData }: { id: number; updateData: any }) => currencyService.update(id, updateData),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['currencies'] });
      enqueueSnackbar('Currency updated successfully', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (error: any) => handleApiError(error),
  });

  const handleEdit = (record: CurrencyDTO) => {
    setSelectedRecord(record);
    setFormData({
      code: record.code,
      currencyName: record.currencyName,
      symbol: record.symbol,
      exchangeRate: record.exchangeRate,
      isBaseCurrency: record.isBaseCurrency,
      isActive: record.isActive,
    });
    setIsModalOpen(true);
  };

  const handleAddNew = () => {
    setSelectedRecord(null);
    setFormData({
      code: '',
      currencyName: '',
      symbol: '',
      exchangeRate: 1,
      isBaseCurrency: false,
      isActive: true,
    });
    setIsModalOpen(true);
  };

  const handleSave = () => {
    const businessUnitID = userData.businessUnitId || 1;
    const payload = {
      ...formData,
      businessUnitID,
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
    { field: 'code', headerName: 'Code', flex: 0.5, minWidth: 80 },
    { field: 'currencyName', headerName: 'Name', flex: 1.5, minWidth: 150 },
    { field: 'symbol', headerName: 'Symbol', flex: 0.5, minWidth: 80 },
    { field: 'exchangeRate', headerName: 'Exchange Rate', flex: 1, minWidth: 120, type: 'number' },
    {
      field: 'isBaseCurrency',
      headerName: 'Base',
      flex: 0.5,
      minWidth: 80,
      renderCell: (params) => params.value ? <Chip label="Yes" color="primary" size="small" /> : 'No',
    },
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
      {/* Header */}
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>{t('currency')}</Typography>
          <Typography variant="body2" color="text.secondary">Manage global currencies and exchange rates</Typography>
        </Box>
        <Button variant="contained" startIcon={<AddIcon />} onClick={handleAddNew} sx={{ px: 3 }}>
          {t('create_new')}
        </Button>
      </Box>

      {/* Filter */}
      <Paper sx={{ p: 1, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', backgroundColor: 'background.paper', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search currencies..." />
      </Paper>

      {/* Grid */}
      <Paper sx={{ height: `calc(100vh - ${220 + SETUP_CHROME_HEIGHT}px)`, width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
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
      <Dialog open={isModalOpen} onClose={() => setIsModalOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>{selectedRecord ? 'Edit Currency' : 'Add New Currency'}</DialogTitle>
        <DialogContent dividers sx={{ p: 3 }}>
          <Grid container spacing={3}>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField fullWidth label="Currency Code" value={formData.code} onChange={(e) => setFormData({ ...formData, code: e.target.value })} />
            </Grid>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField fullWidth label="Symbol" value={formData.symbol} onChange={(e) => setFormData({ ...formData, symbol: e.target.value })} />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <TextField fullWidth label="Currency Name" value={formData.currencyName} onChange={(e) => setFormData({ ...formData, currencyName: e.target.value })} />
            </Grid>
            <Grid size={{ xs: 12, sm: 6 }}>
              <TextField fullWidth type="number" label="Exchange Rate" value={formData.exchangeRate} onChange={(e) => setFormData({ ...formData, exchangeRate: parseFloat(e.target.value) })} />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <FormControlLabel control={<Switch checked={formData.isBaseCurrency} onChange={(e) => setFormData({ ...formData, isBaseCurrency: e.target.checked })} />} label="Is Base Currency" />
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

export default CurrencyPage;
