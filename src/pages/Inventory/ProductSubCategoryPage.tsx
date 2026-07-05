import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Dialog, DialogTitle, DialogContent, DialogActions,
  TextField, FormControlLabel, Switch, CircularProgress,
} from '@mui/material';
import { Add as AddIcon, Edit as EditIcon, Folder as SubCatIcon } from '@mui/icons-material';
import { DataGrid, type GridColDef, type GridPaginationModel } from '@mui/x-data-grid';
import { subCategoryService, type ProductSubCategoryDTO } from '../../api/services/categoryService';
import SearchField from '../../components/common/SearchField';
import UploadExportToolbar from '../../components/common/UploadExportToolbar';
import { useAuth } from '../../context/AuthContext';
import { useSnackbar } from 'notistack';

const empty = { subCategoryName: '', description: '', isActive: true };

const ProductSubCategoryPage: React.FC = () => {
  const { t } = useTranslation();
  const { userData } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();

  const [pagination, setPagination] = useState<GridPaginationModel>({ page: 0, pageSize: 10 });
  const [search, setSearch] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [selected, setSelected] = useState<ProductSubCategoryDTO | null>(null);
  const [form, setForm] = useState(empty);

  const { data, isLoading } = useQuery({
    queryKey: ['product-subcategories-page', pagination, search],
    queryFn: () => subCategoryService.getAll({ pageNumber: pagination.page + 1, pageSize: pagination.pageSize, search: search || undefined }),
  });

  const createMutation = useMutation({
    mutationFn: (body: any) => subCategoryService.create({ ...body, createdBy: userData.userName }),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['product-subcategories'] }); enqueueSnackbar('Sub-Category created!', { variant: 'success' }); closeModal(); },
    onError: () => enqueueSnackbar('Failed to create sub-category', { variant: 'error' }),
  });

  const updateMutation = useMutation({
    mutationFn: (body: any) => subCategoryService.update(selected!.id, { ...body, modifiedBy: userData.userName }),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['product-subcategories'] }); enqueueSnackbar('Sub-Category updated!', { variant: 'success' }); closeModal(); },
    onError: () => enqueueSnackbar('Failed to update sub-category', { variant: 'error' }),
  });

  const openCreate = () => { setSelected(null); setForm(empty); setModalOpen(true); };
  const openEdit = (row: ProductSubCategoryDTO) => {
    setSelected(row);
    setForm({ subCategoryName: row.subCategoryName, description: row.description ?? '', isActive: row.isActive });
    setModalOpen(true);
  };
  const closeModal = () => setModalOpen(false);
  const handleSave = () => selected ? updateMutation.mutate(form) : createMutation.mutate(form);
  const isBusy = createMutation.isPending || updateMutation.isPending;

  const columns: GridColDef[] = [
    { field: 'subCategoryName', headerName: 'Sub-Category Name', flex: 1.5, minWidth: 180 },
    { field: 'description', headerName: 'Description', flex: 2, minWidth: 220, renderCell: (p) => p.value || <span style={{ opacity: 0.35 }}>—</span> },
    { field: 'isActive', headerName: t('status'), width: 100, renderCell: (p) => <Chip label={p.value ? 'Active' : 'Inactive'} color={p.value ? 'success' : 'default'} size="small" variant="outlined" /> },
    { field: 'actions', headerName: t('actions'), width: 80, sortable: false, renderCell: (p) => <IconButton size="small" color="primary" onClick={() => openEdit(p.row)}><EditIcon fontSize="small" /></IconButton> },
  ];

  return (
    <Box sx={{ p: 1 }}>
      {/* Header */}
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 2 }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, display: 'flex', alignItems: 'center', gap: 1 }}>
            <SubCatIcon color="primary" /> Product Sub-Categories
          </Typography>
          <Typography variant="body2" color="text.secondary">Manage product sub-category master data</Typography>
        </Box>
        <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center' }}>
          <UploadExportToolbar
            onDownloadTemplate={subCategoryService.downloadTemplate}
            onUpload={subCategoryService.uploadTemplate}
            onExport={subCategoryService.export}
            templateFileName="ProductSubCategoryTemplate.xlsx"
            exportFileName="ProductSubCategories.xlsx"
          />
          <Button variant="contained" startIcon={<AddIcon />} onClick={openCreate} disableElevation sx={{ textTransform: 'none', fontWeight: 700 }}>
            Add Sub-Category
          </Button>
        </Box>
      </Box>

      {/* Filter */}
      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search sub-categories..." />
      </Paper>

      {/* Grid */}
      <Paper sx={{ border: '1px solid', borderColor: 'divider', boxShadow: 'none', borderRadius: 2, overflow: 'hidden' }}>
        <DataGrid
          autoHeight
          rows={data?.items ?? []}
          columns={columns}
          rowCount={data?.totalItems ?? 0}
          loading={isLoading}
          pageSizeOptions={[10, 25, 50]}
          paginationModel={pagination}
          paginationMode="server"
          onPaginationModelChange={setPagination}
          disableRowSelectionOnClick
        />
      </Paper>

      {/* Modal */}
      <Dialog open={modalOpen} onClose={closeModal} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>{selected ? 'Edit Sub-Category' : 'Add Sub-Category'}</DialogTitle>
        <DialogContent dividers sx={{ display: 'flex', flexDirection: 'column', gap: 2.5, pt: 2 }}>
          <TextField fullWidth label="Sub-Category Name" value={form.subCategoryName} onChange={(e) => setForm(p => ({ ...p, subCategoryName: e.target.value }))} required />
          <TextField fullWidth label="Description" multiline rows={2} value={form.description} onChange={(e) => setForm(p => ({ ...p, description: e.target.value }))} />
          <FormControlLabel control={<Switch checked={form.isActive} onChange={(e) => setForm(p => ({ ...p, isActive: e.target.checked }))} />} label="Active" />
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={closeModal} color="inherit">Cancel</Button>
          <Button variant="contained" onClick={handleSave} disabled={isBusy} disableElevation sx={{ px: 4, fontWeight: 700 }}>
            {isBusy ? <CircularProgress size={20} /> : (selected ? 'Update' : 'Create')}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default ProductSubCategoryPage;
