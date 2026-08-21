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
  Edit as EditIcon,
} from '@mui/icons-material';
import { DataGrid, type GridColDef, type GridPaginationModel } from '@mui/x-data-grid';
import businessUnitService, { type BusinessUnitDTO } from '../../../api/services/businessUnitService';
import SearchField from '../../../components/common/SearchField';
// The grid is sized against the viewport, and the Setup breadcrumb bar is part of that viewport.
import { SETUP_CHROME_HEIGHT } from '../SetupShell';
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
    taxRegistrationNumber: '',
    isActive: true,
  });

  // Mirrors ERP_RFQ_Automation.Tax.TaxRegistrationNumbers: a value that CLAIMS to be Saudi (all
  // digits, leading 3) must be a well-formed 15-digit KSA VAT number; other formats are accepted.
  const taxRegistrationError = ((value?: string) => {
    const trn = (value ?? '').replace(/[\s\-–—]/g, '').toUpperCase();
    if (trn.length === 0) return undefined;
    if (trn.length > 50) return 'Tax registration number is longer than 50 characters.';
    if (trn.length < 5) return 'Tax registration number is too short to be a registration number.';
    if (!/^[A-Z0-9./]+$/.test(trn)) return "Use only letters, digits, '.' and '/'.";
    if (/^3\d*$/.test(trn) && !/^3\d{13}3$/.test(trn))
      return 'A KSA VAT number is exactly 15 digits, beginning with 3 and ending with 3.';
    return undefined;
  })(formData.taxRegistrationNumber);

  const { data, isLoading } = useQuery({
    queryKey: ['businessUnits', paginationModel, search],
    queryFn: () => businessUnitService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      businessUnitName: search || undefined
    }),
  });

  // The ONLY field of an existing business unit a tenant identity may change. Code, name,
  // description and activation state are control-plane facts and PUT /api/BusinessUnit/{id}
  // forbids tenant callers outright, so the edit dialog shows them read-only rather than
  // pretending to save them.
  const updateMutation = useMutation({
    mutationFn: ({ id, taxRegistrationNumber }: { id: number; taxRegistrationNumber: string | null }) =>
      businessUnitService.updateTaxRegistration(id, taxRegistrationNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['businessUnits'] });
      enqueueSnackbar('Tax registration number updated', { variant: 'success' });
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
      taxRegistrationNumber: record.taxRegistrationNumber ?? '',
      isActive: record.isActive,
    });
    setIsModalOpen(true);
  };

  const handleSave = () => {
    if (taxRegistrationError) return;

    // Update only. There is no create path: POST /api/BusinessUnit is `return Forbid()`
    // unconditionally, so the branch that used to live here could do nothing but produce a 403
    // the user had not been warned about. Removed rather than left unreachable — an unreachable
    // call to a refused endpoint is how a future entry point silently reintroduces the defect.
    if (!selectedRecord) return;
    updateMutation.mutate({
      id: selectedRecord.id,
      taxRegistrationNumber: (formData.taxRegistrationNumber ?? '').trim() || null,
    });
  };

  const columns: GridColDef[] = [
    { field: 'businessUnitCode', headerName: 'Code', flex: 0.8, minWidth: 100 },
    { field: 'businessUnitName', headerName: 'Name', flex: 1.5, minWidth: 200 },
    { field: 'description', headerName: 'Description', flex: 2, minWidth: 250 },
    {
      field: 'taxRegistrationNumber',
      headerName: 'VAT Registration',
      flex: 1.2,
      minWidth: 170,
      renderCell: (params) => params.value
        ? <span>{params.value}</span>
        : <Chip label="Not set" color="warning" size="small" variant="outlined" />,
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
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>{t('business_unit')}</Typography>
          {/*
            Say what this screen can actually do. It cannot create anything: POST /api/BusinessUnit
            is `return Forbid()` unconditionally, because the governed platform control plane owns
            the tenant lifecycle. That refusal is CORRECT and stays — the defect was that the page
            never mentioned it, so the first thing a founding administrator does in Setup (this is
            the first card of the first group) was open a create dialog, fill five fields, and get
            a 403 with no explanation.

            The dialog's own copy already said the right sentence, but it was bound to
            `selectedRecord` — the EDIT path — so the one path that always fails said nothing.
          */}
          <Typography variant="body2" color="text.secondary">
            The trading entities that issue your quotes and invoices. New business units are
            provisioned by the platform, not created here — you can update the VAT / tax
            registration number on each one.
          </Typography>
        </Box>
      </Box>

      <Paper sx={{ p: 1, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', backgroundColor: 'background.paper', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search business units..." />
      </Paper>

      <Paper sx={{ height: `calc(100vh - ${220 + SETUP_CHROME_HEIGHT}px)`, width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid
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
        <DialogTitle sx={{ fontWeight: 800 }}>Business Unit</DialogTitle>
        <DialogContent dividers sx={{ p: 3 }}>
          <Grid container spacing={3}>
            <Grid size={{ xs: 12, sm: 4 }}>
              <TextField
                fullWidth
                label="BU Code"
                value={formData.businessUnitCode}
                onChange={(e) => setFormData({ ...formData, businessUnitCode: e.target.value })}
                required
                disabled={!!selectedRecord}
              />
            </Grid>
            <Grid size={{ xs: 12, sm: 8 }}>
              <TextField
                fullWidth
                label="BU Name"
                value={formData.businessUnitName}
                onChange={(e) => setFormData({ ...formData, businessUnitName: e.target.value })}
                required
                disabled={!!selectedRecord}
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
                disabled={!!selectedRecord}
                helperText={selectedRecord
                  ? 'Code, name, description and status are managed by the platform control plane.'
                  : undefined}
              />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <TextField
                fullWidth
                label="VAT / Tax Registration Number"
                value={formData.taxRegistrationNumber ?? ''}
                onChange={(e) => setFormData({ ...formData, taxRegistrationNumber: e.target.value })}
                error={!!taxRegistrationError}
                placeholder="KSA VAT: 15 digits, 3…3"
                helperText={taxRegistrationError
                  ?? 'This entity’s own VAT number — the claimant on input-tax reclaims and the seller VAT number on its tax invoices.'}
              />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <FormControlLabel
                control={<Switch checked={formData.isActive} onChange={(e) => setFormData({ ...formData, isActive: e.target.checked })} disabled={!!selectedRecord} />}
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
            disabled={updateMutation.isPending || !!taxRegistrationError}
            sx={{ px: 4 }}
          >
            {updateMutation.isPending ? <CircularProgress size={24} /> : 'Save'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default BusinessUnitPage;
