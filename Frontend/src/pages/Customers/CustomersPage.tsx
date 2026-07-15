import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Dialog, DialogTitle, DialogContent, DialogActions,
  Grid, FormControlLabel, Switch, TextField, CircularProgress,
  Table, TableHead, TableRow, TableCell, TableBody,
  Tooltip, Divider, MenuItem, Select, FormControl, InputLabel,
} from '@mui/material';
import { DataGrid, type GridColDef, type GridPaginationModel } from '@mui/x-data-grid';
import {
  Add as AddIcon, Edit as EditIcon, Delete as DeleteIcon,
  PersonAdd as PersonAddIcon, Save as SaveIcon, Close as CloseIcon,
  CloudUpload as UploadIcon, Person as CustomerIcon,
  LocalShipping as ShippingIcon, Receipt as BillingIcon,
  Visibility as ViewIcon,
} from '@mui/icons-material';
import { Avatar, Badge, Stack } from '@mui/material';
import customerService, { type CustomerDTO } from '../../api/services/customerService';
import contactService, { type ContactDTO } from '../../api/services/contactService';
import countryService from '../../api/services/countryService';
import stateService from '../../api/services/stateService';
import cityService from '../../api/services/cityService';
import { useAuth } from '../../context/AuthContext';
import SearchField from '../../components/common/SearchField';
import UploadExportToolbar from '../../components/common/UploadExportToolbar';
import { useSnackbar } from 'notistack';

// ─── Empty forms ───────────────────────────────────────────────────────────
const emptyCustomer = {
  name: '', contactEmail: '',
  billingAddressLine1: '', billingAddressLine2: '',
  billingCity: '', billingState: '', billingCountry: '', billingPostalCode: '',
  shippingAddressLine1: '', shippingAddressLine2: '',
  shippingCity: '', shippingState: '', shippingCountry: '', shippingPostalCode: '',
  isActive: true,
};

type CustomerFormState = typeof emptyCustomer & { imageFile?: File | null };

const emptyContact = {
  firstName: '', middleName: '', lastName: '', email: '',
  phoneNo: '', mobileNo: '', position: '', isPrimary: false, isActive: true,
};

// ─── Contact inline sub-form ───────────────────────────────────────────────
const ContactSubForm: React.FC<{
  value: typeof emptyContact;
  onChange: (v: typeof emptyContact) => void;
  onSave: () => void;
  onCancel: () => void;
  isSaving: boolean;
  isEdit: boolean;
}> = ({ value, onChange, onSave, onCancel, isSaving, isEdit }) => {
  const { t } = useTranslation();
  const f = (field: string) => (e: React.ChangeEvent<HTMLInputElement>) =>
    onChange({ ...value, [field]: e.target.value });

  return (
    <Box sx={{ p: 2, bgcolor: 'action.hover', borderRadius: 2, border: '1px dashed', borderColor: 'primary.main', mb: 2 }}>
      <Typography variant="caption" sx={{ fontWeight: 800, color: 'primary.main', textTransform: 'uppercase', letterSpacing: '0.06em', display: 'block', mb: 1.5 }}>
        {isEdit ? t('edit_supplier') : t('create_new')}
      </Typography>
      <Grid container spacing={1.5}>
        <Grid size={{ xs: 12, sm: 4 }}>
          <TextField size="small" fullWidth label="First Name *" value={value.firstName} onChange={f('firstName')} />
        </Grid>
        <Grid size={{ xs: 12, sm: 4 }}>
          <TextField size="small" fullWidth label="Middle Name" value={value.middleName} onChange={f('middleName')} />
        </Grid>
        <Grid size={{ xs: 12, sm: 4 }}>
          <TextField size="small" fullWidth label="Last Name" value={value.lastName} onChange={f('lastName')} />
        </Grid>
        <Grid size={{ xs: 12, sm: 4 }}>
          <TextField size="small" fullWidth label="Email" type="email" value={value.email} onChange={f('email')} />
        </Grid>
        <Grid size={{ xs: 12, sm: 4 }}>
          <TextField size="small" fullWidth label="Phone No" value={value.phoneNo} onChange={f('phoneNo')} />
        </Grid>
        <Grid size={{ xs: 12, sm: 4 }}>
          <TextField size="small" fullWidth label="Mobile No" value={value.mobileNo} onChange={f('mobileNo')} />
        </Grid>
        <Grid size={{ xs: 12, sm: 4 }}>
          <TextField size="small" fullWidth label="Position" value={value.position} onChange={f('position')} />
        </Grid>
        <Grid size={{ xs: 12, sm: 8 }} sx={{ display: 'flex', alignItems: 'center', gap: 3 }}>
          <FormControlLabel
            control={<Switch size="small" checked={value.isPrimary} onChange={(e) => onChange({ ...value, isPrimary: e.target.checked })} />}
            label={<Typography variant="caption" sx={{ fontWeight: 700 }}>Primary</Typography>}
          />
          <FormControlLabel
            control={<Switch size="small" checked={value.isActive} onChange={(e) => onChange({ ...value, isActive: e.target.checked })} />}
            label={<Typography variant="caption" sx={{ fontWeight: 700 }}>Active</Typography>}
          />
        </Grid>
        <Grid size={{ xs: 12 }} sx={{ display: 'flex', justifyContent: 'flex-end', gap: 1 }}>
          <Button size="small" onClick={onCancel} color="inherit" startIcon={<CloseIcon />}>Cancel</Button>
          <Button size="small" variant="contained" onClick={onSave} disabled={isSaving}
            startIcon={isSaving ? <CircularProgress size={14} /> : <SaveIcon />}>
            {t('save_contact')}
          </Button>
        </Grid>
      </Grid>
    </Box>
  );
};

// ─── Main Page ─────────────────────────────────────────────────────────────
const CustomersPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { userData } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedRecord, setSelectedRecord] = useState<CustomerDTO | null>(null);
  const [formData, setFormData] = useState<CustomerFormState>(emptyCustomer);
  const [sameAsB, setSameAsB] = useState(false);

  // Contact state
  const [showContactForm, setShowContactForm] = useState(false);
  const [editingContact, setEditingContact] = useState<ContactDTO | null>(null);
  const [contactForm, setContactForm] = useState(emptyContact);

  // ── Queries ──
  const { data, isLoading } = useQuery({
    queryKey: ['customers', paginationModel, search],
    queryFn: () => customerService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      name: search || undefined,
    }),
  });

  const { data: contacts = [], isLoading: contactsLoading } = useQuery({
    queryKey: ['customer-contacts', selectedRecord?.id],
    queryFn: () => contactService.getByCustomer(selectedRecord!.id),
    enabled: !!selectedRecord?.id && isModalOpen,
  });

  const f = (field: string) => (e: React.ChangeEvent<HTMLInputElement | { name?: string; value: unknown }>) =>
    setFormData(p => ({ ...p, [field]: e.target.value }));

  // ── Reference Data Queries ──
  const buid = userData?.businessUnitId || 0;

  const { data: countries = [] } = useQuery({
    queryKey: ['countries', buid],
    queryFn: () => countryService.getAll(buid),
    enabled: !!buid,
  });

  const { data: states = [] } = useQuery({
    queryKey: ['states', buid],
    queryFn: () => stateService.getAll(buid),
    enabled: !!buid,
  });

  const { data: cities = [] } = useQuery({
    queryKey: ['cities', buid],
    queryFn: () => cityService.getAll(buid),
    enabled: !!buid,
  });

  // Helpers to find IDs by Name for filtering
  const getCountryId = (name: string) => countries.find(c => c.countryName === name)?.countryId;
  const getStateId = (name: string) => states.find(s => s.stateName === name)?.stateId;

  // Filtered lists for Billing
  const billingStates = states.filter(s => !formData.billingCountry || s.countryId === getCountryId(formData.billingCountry));
  const billingCities = cities.filter(c => !formData.billingState || c.stateId === getStateId(formData.billingState));

  // Filtered lists for Shipping
  const shippingStates = states.filter(s => !formData.shippingCountry || s.countryId === getCountryId(formData.shippingCountry));
  const shippingCities = cities.filter(c => !formData.shippingState || c.stateId === getStateId(formData.shippingState));

  // Copy billing to shipping
  const handleSameAsB = (checked: boolean) => {
    setSameAsB(checked);
    if (checked) {
      setFormData(p => ({
        ...p,
        shippingAddressLine1: p.billingAddressLine1,
        shippingAddressLine2: p.billingAddressLine2,
        shippingCity: p.billingCity,
        shippingState: p.billingState,
        shippingCountry: p.billingCountry,
        shippingPostalCode: p.billingPostalCode,
      }));
    }
  };

  // ── Customer mutations ──
  const createMutation = useMutation({
    mutationFn: (fd: FormData) => customerService.create(fd),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['customers'] }); enqueueSnackbar('Customer created!', { variant: 'success' }); setIsModalOpen(false); },
    onError: () => enqueueSnackbar('Failed to create customer', { variant: 'error' }),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, fd }: { id: number; fd: FormData }) => customerService.update(id, fd),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['customers'] }); enqueueSnackbar('Customer updated!', { variant: 'success' }); setIsModalOpen(false); },
    onError: () => enqueueSnackbar('Failed to update customer', { variant: 'error' }),
  });

  // ── Contact mutations ──
  const createContactMutation = useMutation({
    mutationFn: (body: Partial<ContactDTO>) => contactService.create(body),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['customer-contacts', selectedRecord?.id] }); enqueueSnackbar('Contact added!', { variant: 'success' }); setShowContactForm(false); setContactForm(emptyContact); },
    onError: () => enqueueSnackbar('Failed to add contact', { variant: 'error' }),
  });

  const updateContactMutation = useMutation({
    mutationFn: ({ id, body }: { id: number; body: Partial<ContactDTO> }) => contactService.update(id, body),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['customer-contacts', selectedRecord?.id] }); enqueueSnackbar('Contact updated!', { variant: 'success' }); setShowContactForm(false); setEditingContact(null); setContactForm(emptyContact); },
    onError: () => enqueueSnackbar('Failed to update contact', { variant: 'error' }),
  });

  const deleteContactMutation = useMutation({
    mutationFn: (id: number) => contactService.delete(id),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['customer-contacts', selectedRecord?.id] }); enqueueSnackbar('Contact removed', { variant: 'info' }); },
    onError: () => enqueueSnackbar('Failed to delete contact', { variant: 'error' }),
  });

  // ── Handlers ──
  const handleEdit = (record: CustomerDTO) => {
    setSelectedRecord(record);
    setFormData({
      name: record.name ?? '', contactEmail: record.contactEmail ?? '',
      billingAddressLine1: record.billingAddressLine1 ?? '', billingAddressLine2: record.billingAddressLine2 ?? '',
      billingCity: record.billingCity ?? '', billingState: record.billingState ?? '',
      billingCountry: record.billingCountry ?? '', billingPostalCode: record.billingPostalCode ?? '',
      shippingAddressLine1: record.shippingAddressLine1 ?? '', shippingAddressLine2: record.shippingAddressLine2 ?? '',
      shippingCity: record.shippingCity ?? '', shippingState: record.shippingState ?? '',
      shippingCountry: record.shippingCountry ?? '', shippingPostalCode: record.shippingPostalCode ?? '',
      isActive: record.isActive ?? true,
    });
    setSameAsB(false);
    setShowContactForm(false);
    setIsModalOpen(true);
  };

  const handleAddNew = () => {
    setSelectedRecord(null);
    setFormData(emptyCustomer);
    setSameAsB(false);
    setShowContactForm(false);
    setContactForm(emptyContact);
    setIsModalOpen(true);
  };

  const handleSaveCustomer = () => {
    const fd = new FormData();
    Object.entries(formData).forEach(([k, v]) => {
      if (k === 'imageFile') {
        if (v) fd.append('ImageFile', v as File);
      } else {
        fd.append(k, String(v));
      }
    });
    fd.append(selectedRecord ? 'modifiedBy' : 'createdBy', userData.userName || 'System');
    selectedRecord
      ? updateMutation.mutate({ id: selectedRecord.id, fd })
      : createMutation.mutate(fd);
  };

  const handleSaveContact = () => {
    if (!selectedRecord) return;

    // Validate IsPrimary: Only one primary contact per parent
    if (contactForm.isPrimary) {
      const existingPrimary = contacts.find(c => c.isPrimary && (!editingContact || c.id !== editingContact.id));
      if (existingPrimary) {
        enqueueSnackbar('A primary contact already exists for this customer.', { variant: 'warning' });
        return;
      }
    }

    const body: Partial<ContactDTO> = {
      ...contactForm,
      customerId: selectedRecord.id,
      createdBy: userData.userName || 'System',
      ...(editingContact ? { modifiedBy: userData.userName || 'System' } : {}),
    };
    editingContact
      ? updateContactMutation.mutate({ id: editingContact.id, body })
      : createContactMutation.mutate(body);
  };

  const openEditContact = (c: ContactDTO) => {
    setEditingContact(c);
    setContactForm({ firstName: c.firstName ?? '', middleName: c.middleName ?? '', lastName: c.lastName ?? '', email: c.email ?? '', phoneNo: c.phoneNo ?? '', mobileNo: c.mobileNo ?? '', position: c.position ?? '', isPrimary: c.isPrimary ?? false, isActive: c.isActive ?? true });
    setShowContactForm(true);
  };

  const isBusy = createMutation.isPending || updateMutation.isPending;

  // ── Grid columns ──
  const columns: GridColDef[] = [
    { field: 'docId', headerName: 'Doc ID', width: 110, renderCell: (p) => <Typography sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>{p.value ?? '—'}</Typography> },
    { field: 'name', headerName: t('customers'), flex: 1.5, minWidth: 160 },
    { field: 'contactEmail', headerName: t('email'), flex: 1.2, minWidth: 180 },
    { field: 'billingCity', headerName: t('city'), width: 120, renderCell: (p) => p.value ?? '—' },
    { field: 'billingCountry', headerName: t('country'), width: 120, renderCell: (p) => p.value ?? '—' },
    { field: 'isActive', headerName: t('status'), width: 100, renderCell: (p) => <Chip label={p.value ? 'Active' : 'Inactive'} color={p.value ? 'success' : 'error'} size="small" variant="outlined" /> },
    { 
      field: 'actions', 
      headerName: t('actions'), 
      width: 120, 
      sortable: false, 
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5}>
          <Tooltip title="View Details">
            <IconButton size="small" color="primary" onClick={() => navigate(`/customers/${p.row.id}`)}><ViewIcon fontSize="small" /></IconButton>
          </Tooltip>
          <Tooltip title="Edit">
            <IconButton size="small" color="info" onClick={() => handleEdit(p.row)}><EditIcon fontSize="small" /></IconButton>
          </Tooltip>
        </Stack>
      )
    },
  ];

  return (
    <Box sx={{ width: '100%', px: 1, py: 1 }}>
      {/* Header */}
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>{t('customers')}</Typography>
          <Typography variant="body2" color="text.secondary">Manage your customer accounts and billing information</Typography>
        </Box>
        <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center' }}>
          <UploadExportToolbar
            onDownloadTemplate={customerService.downloadTemplate}
            onUpload={customerService.uploadTemplate}
            onExport={customerService.export}
            templateFileName="CustomerTemplate.xlsx"
            exportFileName="Customers.xlsx"
          />
          <Button variant="contained" startIcon={<AddIcon />} onClick={handleAddNew} sx={{ px: 3 }}>Add Customer</Button>
        </Box>
      </Box>

      {/* Search */}
      <Paper sx={{ p: 1, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', backgroundColor: 'background.paper', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search customers..." />
      </Paper>

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 220px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid rows={data?.items ?? []} columns={columns} rowCount={data?.totalCount ?? 0} loading={isLoading} pageSizeOptions={[10, 25, 50]} paginationModel={paginationModel} paginationMode="server" onPaginationModelChange={setPaginationModel} getRowId={(r) => r.id} disableRowSelectionOnClick />
      </Paper>

      {/* ── Dialog ─────────────────────────────────────────────────────────── */}
      <Dialog open={isModalOpen} onClose={() => setIsModalOpen(false)} fullWidth maxWidth="md">
        <DialogTitle sx={{ fontWeight: 800 }}>
          {selectedRecord ? `Edit: ${selectedRecord.name}` : 'Add New Customer'}
        </DialogTitle>

        <DialogContent dividers sx={{ p: 3 }}>

          {/* ── Logo & General Info ── */}
          <Box sx={{ display: 'flex', gap: 3, mb: 4, alignItems: 'flex-start' }}>
            <Box sx={{ textAlign: 'center' }}>
              <Badge
                overlap="circular"
                anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
                badgeContent={
                  <IconButton
                    component="label"
                    sx={{
                      bgcolor: 'primary.main',
                      color: 'white',
                      '&:hover': { bgcolor: 'primary.dark' },
                      width: 32,
                      height: 32,
                      boxShadow: 2,
                    }}
                  >
                    <UploadIcon sx={{ fontSize: 18 }} />
                    <input
                      type="file"
                      hidden
                      accept="image/*"
                      onChange={(e) => {
                        const file = e.target.files?.[0];
                        if (file) setFormData(p => ({ ...p, imageFile: file }));
                      }}
                    />
                  </IconButton>
                }
              >
                <Avatar
                  src={formData.imageFile ? URL.createObjectURL(formData.imageFile) : selectedRecord?.imageUrl}
                  sx={{
                    width: 120,
                    height: 120,
                    border: '4px solid',
                    borderColor: 'background.paper',
                    boxShadow: 3,
                    fontSize: '3rem',
                    bgcolor: 'grey.100',
                    color: 'grey.400'
                  }}
                >
                  <CustomerIcon sx={{ fontSize: 'inherit' }} />
                </Avatar>
              </Badge>
              <Typography variant="caption" sx={{ display: 'block', mt: 1, fontWeight: 700, color: 'text.secondary' }}>
                Customer Logo
              </Typography>
            </Box>

            <Grid container spacing={2} sx={{ flex: 1 }}>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label="Customer Name *" value={formData.name} onChange={f('name')} />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label="Contact Email" type="email" value={formData.contactEmail} onChange={f('contactEmail')} />
              </Grid>
              <Grid size={{ xs: 12 }}>
                <FormControlLabel
                  control={<Switch checked={formData.isActive} onChange={(e) => setFormData(p => ({ ...p, isActive: e.target.checked }))} color="success" />}
                  label={<Typography variant="subtitle2" sx={{ fontWeight: 700 }}>Active Status</Typography>}
                />
              </Grid>
            </Grid>
          </Box>

          {/* ── Billing Address ── */}
          <Box sx={{ mb: 4, p: 2, borderRadius: 2, bgcolor: 'grey.50', border: '1px solid', borderColor: 'divider' }}>
            <Stack direction="row" spacing={1} sx={{ mb: 2, alignItems: 'center' }}>
              <BillingIcon color="primary" fontSize="small" />
              <Typography variant="subtitle2" sx={{ fontWeight: 800, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
                Billing Address
              </Typography>
            </Stack>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12 }}>
                <TextField fullWidth label="Address Line 1" value={formData.billingAddressLine1} onChange={f('billingAddressLine1')} size="small" />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label="Address Line 2" value={formData.billingAddressLine2} onChange={f('billingAddressLine2')} size="small" />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>{t('country')}</InputLabel>
                  <Select
                    value={formData.billingCountry}
                    label={t('country')}
                    onChange={(e) => setFormData(p => ({ ...p, billingCountry: e.target.value as string, billingState: '', billingCity: '' }))}
                  >
                    <MenuItem value=""><em>None</em></MenuItem>
                    {countries.map(c => <MenuItem key={c.countryId} value={c.countryName}>{c.countryName}</MenuItem>)}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12, sm: 4 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>State</InputLabel>
                  <Select
                    value={formData.billingState}
                    label="State"
                    onChange={(e) => setFormData(p => ({ ...p, billingState: e.target.value as string, billingCity: '' }))}
                    disabled={!formData.billingCountry}
                  >
                    <MenuItem value=""><em>None</em></MenuItem>
                    {billingStates.map(s => <MenuItem key={s.stateId} value={s.stateName}>{s.stateName}</MenuItem>)}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12, sm: 4 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>{t('city')}</InputLabel>
                  <Select
                    value={formData.billingCity}
                    label={t('city')}
                    onChange={(e) => setFormData(p => ({ ...p, billingCity: e.target.value as string }))}
                    disabled={!formData.billingState}
                  >
                    <MenuItem value=""><em>None</em></MenuItem>
                    {billingCities.map(c => <MenuItem key={c.cityId} value={c.cityName}>{c.cityName}</MenuItem>)}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12, sm: 4 }}>
                <TextField fullWidth label="Postal Code" value={formData.billingPostalCode} onChange={f('billingPostalCode')} size="small" />
              </Grid>
            </Grid>
          </Box>

          {/* ── Shipping Address ── */}
          <Box sx={{ mb: 4, p: 2, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
            <Stack direction="row" sx={{ mb: 2, justifyContent: 'space-between', alignItems: 'center' }}>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <ShippingIcon color="primary" fontSize="small" />
                <Typography variant="subtitle2" sx={{ fontWeight: 800, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
                  Shipping Address
                </Typography>
              </Stack>
              <FormControlLabel
                control={<Switch size="small" checked={sameAsB} onChange={(e) => handleSameAsB(e.target.checked)} />}
                label={<Typography variant="caption" sx={{ fontWeight: 700 }}>Same as Billing</Typography>}
              />
            </Stack>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12 }}>
                <TextField fullWidth label="Address Line 1" value={formData.shippingAddressLine1} onChange={f('shippingAddressLine1')} size="small" disabled={sameAsB} />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label="Address Line 2" value={formData.shippingAddressLine2} onChange={f('shippingAddressLine2')} size="small" disabled={sameAsB} />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>{t('country')}</InputLabel>
                  <Select
                    value={formData.shippingCountry}
                    label={t('country')}
                    onChange={(e) => setFormData(p => ({ ...p, shippingCountry: e.target.value as string, shippingState: '', shippingCity: '' }))}
                    disabled={sameAsB}
                  >
                    <MenuItem value=""><em>None</em></MenuItem>
                    {countries.map(c => <MenuItem key={c.countryId} value={c.countryName}>{c.countryName}</MenuItem>)}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12, sm: 4 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>State</InputLabel>
                  <Select
                    value={formData.shippingState}
                    label="State"
                    onChange={(e) => setFormData(p => ({ ...p, shippingState: e.target.value as string, shippingCity: '' }))}
                    disabled={sameAsB || !formData.shippingCountry}
                  >
                    <MenuItem value=""><em>None</em></MenuItem>
                    {shippingStates.map(s => <MenuItem key={s.stateId} value={s.stateName}>{s.stateName}</MenuItem>)}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12, sm: 4 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>{t('city')}</InputLabel>
                  <Select
                    value={formData.shippingCity}
                    label={t('city')}
                    onChange={(e) => setFormData(p => ({ ...p, shippingCity: e.target.value as string }))}
                    disabled={sameAsB || !formData.shippingState}
                  >
                    <MenuItem value=""><em>None</em></MenuItem>
                    {shippingCities.map(c => <MenuItem key={c.cityId} value={c.cityName}>{c.cityName}</MenuItem>)}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12, sm: 4 }}>
                <TextField fullWidth label="Postal Code" value={formData.shippingPostalCode} onChange={f('shippingPostalCode')} size="small" disabled={sameAsB} />
              </Grid>
            </Grid>
          </Box>

          {/* ── Section: Contacts ── */}
          <Divider sx={{ my: 2.5 }} />
          <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 1.5 }}>
            <Typography variant="overline" sx={{ fontWeight: 800, color: 'text.secondary', letterSpacing: '0.08em' }}>
              Contacts {selectedRecord && !contactsLoading && `(${contacts.length})`}
            </Typography>
            {selectedRecord && !showContactForm && (
              <Button size="small" variant="outlined" startIcon={<PersonAddIcon />}
                onClick={() => { setEditingContact(null); setContactForm(emptyContact); setShowContactForm(true); }}
                sx={{ fontWeight: 700, textTransform: 'none' }}>
                Add Contact
              </Button>
            )}
          </Box>

          {!selectedRecord && (
            <Typography variant="body2" sx={{ color: 'text.disabled', fontStyle: 'italic', mb: 1 }}>
              Save the customer first, then you can add contacts from the Edit view.
            </Typography>
          )}

          {selectedRecord && showContactForm && (
            <ContactSubForm
              value={contactForm}
              onChange={setContactForm}
              onSave={handleSaveContact}
              onCancel={() => { setShowContactForm(false); setEditingContact(null); setContactForm(emptyContact); }}
              isSaving={createContactMutation.isPending || updateContactMutation.isPending}
              isEdit={!!editingContact}
            />
          )}

          {selectedRecord && !contactsLoading && contacts.length > 0 && (
            <Table size="small" sx={{ mt: 1 }}>
              <TableHead>
                <TableRow sx={{ '& th': { fontWeight: 800, fontSize: '0.72rem', color: 'text.secondary', textTransform: 'uppercase', letterSpacing: '0.05em', py: 0.8 } }}>
                  <TableCell>Name</TableCell>
                  <TableCell>Position</TableCell>
                  <TableCell>Email</TableCell>
                  <TableCell>Phone</TableCell>
                  <TableCell>Primary</TableCell>
                  <TableCell>Status</TableCell>
                  <TableCell align="right">Actions</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {contacts.map((c) => (
                  <TableRow key={c.id} sx={{ '&:hover': { bgcolor: 'action.hover' } }}>
                    <TableCell sx={{ fontWeight: 700, fontSize: '0.83rem' }}>
                      {[c.firstName, c.middleName, c.lastName].filter(Boolean).join(' ')}
                    </TableCell>
                    <TableCell sx={{ color: 'text.secondary', fontSize: '0.8rem' }}>{c.position ?? '—'}</TableCell>
                    <TableCell sx={{ fontSize: '0.8rem' }}>{c.email ?? '—'}</TableCell>
                    <TableCell sx={{ fontSize: '0.8rem' }}>{c.phoneNo || c.mobileNo || '—'}</TableCell>
                    <TableCell>
                      {c.isPrimary ? <Chip label="Primary" size="small" color="primary" sx={{ height: 18, fontSize: '0.65rem', fontWeight: 800 }} /> : '—'}
                    </TableCell>
                    <TableCell>
                      <Chip label={c.isActive ? 'Active' : 'Inactive'} color={c.isActive ? 'success' : 'default'} size="small" variant="outlined" sx={{ height: 18, fontSize: '0.65rem', fontWeight: 800 }} />
                    </TableCell>
                    <TableCell align="right" sx={{ whiteSpace: 'nowrap' }}>
                      <Tooltip title="Edit">
                        <IconButton size="small" onClick={() => openEditContact(c)}><EditIcon fontSize="small" /></IconButton>
                      </Tooltip>
                      <Tooltip title="Remove">
                        <IconButton size="small" color="error" onClick={() => deleteContactMutation.mutate(c.id)}>
                          <DeleteIcon fontSize="small" />
                        </IconButton>
                      </Tooltip>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}

          {selectedRecord && !contactsLoading && contacts.length === 0 && !showContactForm && (
            <Box sx={{ textAlign: 'center', py: 3 }}>
              <PersonAddIcon sx={{ fontSize: 36, color: 'action.disabled', mb: 0.5 }} />
              <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>No contacts yet. Click "Add Contact" to add one.</Typography>
            </Box>
          )}

        </DialogContent>

        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setIsModalOpen(false)} color="inherit">Cancel</Button>
          <Button variant="contained" onClick={handleSaveCustomer} disabled={isBusy}>
            {isBusy ? <CircularProgress size={22} /> : 'Save Customer'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default CustomersPage;
