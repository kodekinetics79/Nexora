import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Dialog, DialogTitle, DialogContent, DialogActions,
  TextField, FormControlLabel, Switch, CircularProgress, MenuItem,
} from '@mui/material';
import { Add as AddIcon, Edit as EditIcon, Category as CategoryIcon } from '@mui/icons-material';
import { DataGrid, type GridColDef, type GridPaginationModel } from '@mui/x-data-grid';
import { categoryService, type ProductCategoryDTO } from '../../api/services/categoryService';
import SearchField from '../../components/common/SearchField';
import UploadExportToolbar from '../../components/common/UploadExportToolbar';
import { useAuth } from '../../context/AuthContext';
import { useSnackbar } from 'notistack';

const empty = { categoryName: '', description: '', parentCategoryId: '', isActive: true };

const ProductCategoryPage: React.FC = () => {
  const { t } = useTranslation();
  const { userData } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();

  const [pagination, setPagination] = useState<GridPaginationModel>({ page: 0, pageSize: 10 });
  const [search, setSearch] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [selected, setSelected] = useState<ProductCategoryDTO | null>(null);
  const [form, setForm] = useState(empty);

  const { data, isLoading } = useQuery({
    queryKey: ['product-categories-page', pagination, search],
    queryFn: () => categoryService.getAll({ pageNumber: pagination.page + 1, pageSize: pagination.pageSize, search: search || undefined }),
  });

  // Category list for parent dropdown
  const { data: allCats } = useQuery({
    queryKey: ['product-categories-all'],
    queryFn: () => categoryService.getAll({ pageSize: 1000 }),
  });

  const createMutation = useMutation({
    mutationFn: (body: any) => categoryService.create({ ...body, createdBy: userData.userName }),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['product-categories'] }); enqueueSnackbar('Category created!', { variant: 'success' }); closeModal(); },
    onError: () => enqueueSnackbar('Failed to create category', { variant: 'error' }),
  });

  const updateMutation = useMutation({
    mutationFn: (body: any) => categoryService.update(selected!.id, { ...body, modifiedBy: userData.userName }),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['product-categories'] }); enqueueSnackbar('Category updated!', { variant: 'success' }); closeModal(); },
    onError: () => enqueueSnackbar('Failed to update category', { variant: 'error' }),
  });

  const openCreate = () => { setSelected(null); setForm(empty); setModalOpen(true); };
  const openEdit = (row: ProductCategoryDTO) => {
    setSelected(row);
    setForm({ categoryName: row.categoryName, description: row.description ?? '', parentCategoryId: row.parentCategoryId ? String(row.parentCategoryId) : '', isActive: row.isActive });
    setModalOpen(true);
  };
  const closeModal = () => setModalOpen(false);

  const handleSave = () => {
    const payload = { ...form, parentCategoryId: form.parentCategoryId ? Number(form.parentCategoryId) : null };
    selected ? updateMutation.mutate(payload) : createMutation.mutate(payload);
  };

  const isBusy = createMutation.isPending || updateMutation.isPending;

  const columns: GridColDef[] = [
    { field: 'categoryName', headerName: 'Category Name', flex: 1.5, minWidth: 160 },
    { field: 'description', headerName: 'Description', flex: 2, minWidth: 200 },
    { field: 'parentCategoryName', headerName: 'Parent Category', flex: 1, minWidth: 140, renderCell: (p) => p.value || <span style={{ opacity: 0.35 }}>—</span> },
    { field: 'isActive', headerName: t('status'), width: 100, renderCell: (p) => <Chip label={p.value ? 'Active' : 'Inactive'} color={p.value ? 'success' : 'default'} size="small" variant="outlined" /> },
    { field: 'actions', headerName: t('actions'), width: 80, sortable: false, renderCell: (p) => <IconButton size="small" color="primary" onClick={() => openEdit(p.row)}><EditIcon fontSize="small" /></IconButton> },
  ];

  return (
    <Box sx={{ p: 1 }}>
      {/* Header */}
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 2 }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, display: 'flex', alignItems: 'center', gap: 1 }}>
            <CategoryIcon color="primary" /> {t('categories')}
          </Typography>
          <Typography variant="body2" color="text.secondary">Manage product category master data</Typography>
        </Box>
        <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center' }}>
          <UploadExportToolbar
            onDownloadTemplate={categoryService.downloadTemplate}
            onUpload={categoryService.uploadTemplate}
            onExport={categoryService.export}
            templateFileName="ProductCategoryTemplate.xlsx"
            exportFileName="ProductCategories.xlsx"
          />
          <Button variant="contained" startIcon={<AddIcon />} onClick={openCreate} disableElevation sx={{ textTransform: 'none', fontWeight: 700 }}>
            Add Category
          </Button>
        </Box>
      </Box>

      {/* Filter */}
      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search categories..." />
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
        <DialogTitle sx={{ fontWeight: 800 }}>{selected ? 'Edit Category' : 'Add Category'}</DialogTitle>
        <DialogContent dividers sx={{ display: 'flex', flexDirection: 'column', gap: 2.5, pt: 2 }}>
          <TextField fullWidth label="Category Name" value={form.categoryName} onChange={(e) => setForm(p => ({ ...p, categoryName: e.target.value }))} required />
          <TextField fullWidth label="Description" multiline rows={2} value={form.description} onChange={(e) => setForm(p => ({ ...p, description: e.target.value }))} />
          <TextField select fullWidth label="Parent Category" value={form.parentCategoryId} onChange={(e) => setForm(p => ({ ...p, parentCategoryId: e.target.value }))}>
            <MenuItem value="">None</MenuItem>
            {allCats?.items?.filter(c => c.id !== selected?.id).map(c => <MenuItem key={c.id} value={String(c.id)}>{c.categoryName}</MenuItem>)}
          </TextField>
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

export default ProductCategoryPage;
