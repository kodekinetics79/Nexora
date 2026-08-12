import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton, Alert,
  Dialog, DialogTitle, DialogContent, DialogActions,
  Grid, FormControlLabel, Switch, TextField, CircularProgress,
  Table, TableHead, TableRow, TableCell, TableBody,
  Tooltip, Divider, MenuItem, Select, FormControl, InputLabel,
} from '@mui/material';
import { DataGrid, type GridColDef, type GridPaginationModel } from '@mui/x-data-grid';
import {
  Add as AddIcon, Edit as EditIcon, Delete as DeleteIcon,
  PersonAdd as PersonAddIcon, Save as SaveIcon, Close as CloseIcon,
  LocationOn as LocationIcon, Paid as CurrencyIcon,
  Visibility as ViewIcon,
} from '@mui/icons-material';
import { Stack } from '@mui/material';
import supplierService, { supplierTierLabel, type SupplierDTO } from '../../api/services/supplierService';
import contactService, { type ContactDTO } from '../../api/services/contactService';
import currencyService from '../../api/services/currencyService';
import countryService from '../../api/services/countryService';
import cityService from '../../api/services/cityService';
import { useAuth } from '../../context/AuthContext';
import SearchField from '../../components/common/SearchField';
import ColumnPreferences from '../../components/common/ColumnPreferences';
import CustomFieldValuesEditor from '../../components/common/CustomFieldValuesEditor';
import useColumnPreferences from '../../hooks/useColumnPreferences';
import UploadExportToolbar from '../../components/common/UploadExportToolbar';
import { useSnackbar } from 'notistack';

// ─── Empty forms ───────────────────────────────────────────────────────────
const emptySupplier = {
  name: '', contactEmail: '', paymentTerms: '',
  addressLine1: '', addressLine2: '', postalCode: '',
  tags: '', comments: '', isActive: true,
  cityId: '' as any, countryId: '' as any, currencyId: '' as any,
  // Carried through this dialog although it edits neither: the supplier update replaces every
  // field it receives, so a value this form did not send back would be erased by an unrelated
  // edit. Tier and credit days are set in SupplierFormDialog and must survive a grid edit.
  tier: '', creditDays: '' as any,
  concurrencyToken: '',
};

type SupplierFormState = typeof emptySupplier;

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
          {isEdit
            ? <Chip size="small" label={value.isActive ? 'Active' : 'Inactive'} color={value.isActive ? 'success' : 'default'} variant="outlined" />
            : <FormControlLabel
                control={<Switch size="small" checked={value.isActive} onChange={(e) => onChange({ ...value, isActive: e.target.checked })} />}
                label={<Typography variant="caption" sx={{ fontWeight: 700 }}>Active</Typography>}
              />}
        </Grid>
        <Grid size={{ xs: 12 }} sx={{ display: 'flex', justifyContent: 'flex-end', gap: 1 }}>
          <Button size="small" onClick={onCancel} color="inherit" startIcon={<CloseIcon />}>Cancel</Button>
          <Button size="small" variant="contained" onClick={onSave} disabled={isSaving} startIcon={isSaving ? <CircularProgress size={14} /> : <SaveIcon />}>
            {t('save_contact')}
          </Button>
        </Grid>
      </Grid>
    </Box>
  );
};

// ─── Main Page ─────────────────────────────────────────────────────────────
const SuppliersPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { userData, hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const canCreateSupplier = hasPermission('Suppliers', 'create');
  const canEditSupplier = hasPermission('Suppliers', 'edit');
  const canViewContacts = hasPermission('Suppliers');
  const canCreateContact = hasPermission('Suppliers', 'create');
  const canEditContact = hasPermission('Suppliers', 'edit');
  const canDeleteContact = hasPermission('Suppliers', 'delete');

  // List state
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  // AA-01 · per-user column layout + tenant-defined Supplier fields as columns.
  const columnPreferences = useColumnPreferences('suppliers.list');
  const [search, setSearch] = useState('');

  // Dialog state
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedRecord, setSelectedRecord] = useState<SupplierDTO | null>(null);
  const [formData, setFormData] = useState<SupplierFormState>(emptySupplier);

  // Contact state
  const [showContactForm, setShowContactForm] = useState(false);
  const [editingContact, setEditingContact] = useState<ContactDTO | null>(null);
  const [contactForm, setContactForm] = useState(emptyContact);

  // ── Queries ──
  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: ['suppliers', paginationModel, search],
    queryFn: () => supplierService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      name: search || undefined,
    }),
  });

  const { data: contacts = [], isLoading: contactsLoading } = useQuery({
    queryKey: ['supplier-contacts', selectedRecord?.id],
    queryFn: () => contactService.getBySupplier(selectedRecord!.id),
    enabled: !!selectedRecord?.id && isModalOpen && canViewContacts,
  });

  const f = (field: string) => (e: React.ChangeEvent<HTMLInputElement | { name?: string; value: unknown }>) =>
    setFormData(p => ({ ...p, [field]: e.target.value }));

  // ── Reference Data Queries ──
  const buid = userData?.businessUnitId || 0;

  const { data: currenciesData } = useQuery({
    queryKey: ['currencies', buid],
    queryFn: () => currencyService.getAll({ businessUnitId: buid, pageSize: 1000 }),
    enabled: !!buid,
  });
  const currencies = currenciesData?.items ?? [];

  const { data: countries = [] } = useQuery({
    queryKey: ['countries', buid],
    queryFn: () => countryService.getAll(buid),
    enabled: !!buid,
  });

  const { data: allCities = [] } = useQuery({
    queryKey: ['cities', buid],
    queryFn: () => cityService.getAll(buid),
    enabled: !!buid,
  });

  const filteredCities = allCities.filter(c => !formData.countryId || c.countryId === formData.countryId);

  // ── Supplier mutations ──
  const createMutation = useMutation({
    mutationFn: (fd: FormData) => supplierService.create(fd),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['suppliers'] });
      enqueueSnackbar('Supplier created!', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (error: any) => enqueueSnackbar(
      error?.response?.data?.detail || error?.response?.data || 'Failed to create supplier',
      { variant: 'error' },
    ),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, fd }: { id: number; fd: FormData }) => supplierService.update(id, fd),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['suppliers'] });
      enqueueSnackbar('Supplier updated!', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (error: any) => enqueueSnackbar(
      error?.response?.data?.detail || error?.response?.data || 'Failed to update supplier',
      { variant: 'error' },
    ),
  });

  // ── Contact mutations ──
  const createContactMutation = useMutation({
    mutationFn: (body: Partial<ContactDTO>) => contactService.create(body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['supplier-contacts', selectedRecord?.id] });
      enqueueSnackbar('Contact added!', { variant: 'success' });
      setShowContactForm(false);
      setContactForm(emptyContact);
    },
    onError: () => enqueueSnackbar('Failed to add contact', { variant: 'error' }),
  });

  const updateContactMutation = useMutation({
    mutationFn: ({ id, body }: { id: number; body: Partial<ContactDTO> }) => contactService.update(id, body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['supplier-contacts', selectedRecord?.id] });
      enqueueSnackbar('Contact updated!', { variant: 'success' });
      setShowContactForm(false);
      setEditingContact(null);
      setContactForm(emptyContact);
    },
    onError: () => enqueueSnackbar('Failed to update contact', { variant: 'error' }),
  });

  const deleteContactMutation = useMutation({
    mutationFn: (contact: ContactDTO) => contactService.delete(contact.id, contact.concurrencyToken),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['supplier-contacts', selectedRecord?.id] });
      enqueueSnackbar('Contact removed', { variant: 'info' });
    },
    onError: () => enqueueSnackbar('Failed to delete contact', { variant: 'error' }),
  });

  // ── Handlers ──
  const handleEdit = (record: SupplierDTO) => {
    if (!canEditSupplier) return;
    setSelectedRecord(record);
    setFormData({
      name: record.name ?? '', contactEmail: record.contactEmail ?? '',
      paymentTerms: record.paymentTerms ?? '', addressLine1: record.addressLine1 ?? '',
      addressLine2: record.addressLine2 ?? '', postalCode: record.postalCode ?? '',
      tags: record.tags ?? '', comments: record.comments ?? '', isActive: record.isActive ?? true,
      cityId: record.cityId ?? '', countryId: record.countryId ?? '', currencyId: record.currencyId ?? '',
      tier: record.tier ?? '',
      creditDays: record.creditDays === null || record.creditDays === undefined ? '' : record.creditDays,
      concurrencyToken: record.concurrencyToken ?? '',
    });
    setShowContactForm(false);
    setIsModalOpen(true);
  };

  const handleAddNew = () => {
    if (!canCreateSupplier) return;
    setSelectedRecord(null);
    setFormData(emptySupplier);
    setShowContactForm(false);
    setContactForm(emptyContact);
    setIsModalOpen(true);
  };

  const handleSaveSupplier = () => {
    if (selectedRecord ? !canEditSupplier : !canCreateSupplier) return;
    if (!formData.name.trim()) {
      enqueueSnackbar('Supplier name is required.', { variant: 'warning' });
      return;
    }
    if (formData.contactEmail && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(formData.contactEmail)) {
      enqueueSnackbar('Enter a valid Supplier contact email.', { variant: 'warning' });
      return;
    }
    const fd = new FormData();
    Object.entries(formData).forEach(([k, v]) => {
      if (k !== 'isActive' && v !== '') fd.append(k, String(v));
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
        enqueueSnackbar('A primary contact already exists for this supplier.', { variant: 'warning' });
        return;
      }
    }

    const { isActive, ...editableContact } = contactForm;
    const body: Partial<ContactDTO> = {
      ...editableContact,
      supplierId: selectedRecord.id,
      ...(!editingContact ? { isActive } : {}),
      ...(editingContact ? { concurrencyToken: editingContact.concurrencyToken } : {}),
    };
    editingContact
      ? updateContactMutation.mutate({ id: editingContact.id, body })
      : createContactMutation.mutate(body);
  };

  const openEditContact = (c: ContactDTO) => {
    setEditingContact(c);
    setContactForm({
      firstName: c.firstName ?? '', middleName: c.middleName ?? '', lastName: c.lastName ?? '',
      email: c.email ?? '', phoneNo: c.phoneNo ?? '', mobileNo: c.mobileNo ?? '',
      position: c.position ?? '', isPrimary: c.isPrimary ?? false, isActive: c.isActive ?? true,
    });
    setShowContactForm(true);
  };

  const isBusy = createMutation.isPending || updateMutation.isPending;

  // ── Grid columns ──
  const columns: GridColDef[] = [
    { field: 'docId', headerName: 'Doc ID', width: 110, renderCell: (p) => <Typography sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>{p.value ?? '—'}</Typography> },
    { field: 'name', headerName: t('supplier_name'), flex: 1.5, minWidth: 160 },
    { field: 'contactEmail', headerName: t('email'), flex: 1.2, minWidth: 180 },
    { field: 'countryName', headerName: t('country'), width: 120, renderCell: (p) => p.value ?? '—' },
    { field: 'currencyName', headerName: t('currency'), width: 100, renderCell: (p) => p.value ?? '—' },
    { field: 'paymentTerms', headerName: t('payment_terms'), width: 140, renderCell: (p) => p.value ?? '—' },
    // Tier is a commercial classification, not a status: rendered as plain text next to the status
    // chips rather than as a chip of its own, so it cannot be read as an approval verdict.
    { field: 'tier', headerName: 'Tier', width: 175, renderCell: (p) => supplierTierLabel(p.value) },
    { field: 'governanceStatus', headerName: 'Approval', width: 145, renderCell: (p) => <Chip label={(p.value || 'UNVERIFIED').replaceAll('_', ' ')} size="small" variant="outlined" /> },
    { field: 'readinessStatus', headerName: 'RFQ readiness', width: 145, renderCell: (p) => <Chip label={(p.value || 'REVIEW_REQUIRED').replaceAll('_', ' ')} size="small" variant="outlined" /> },
    { field: 'isActive', headerName: t('status'), width: 100, renderCell: (p) => <Chip label={p.value ? 'Active' : 'Inactive'} color={p.value ? 'success' : 'error'} size="small" variant="outlined" /> },
    { 
      field: 'actions', 
      headerName: t('actions'), 
      width: 120, 
      sortable: false, 
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5}>
          <Tooltip title="View Details">
            <IconButton size="small" color="primary" onClick={() => navigate(`/suppliers/${p.row.id}`)}><ViewIcon fontSize="small" /></IconButton>
          </Tooltip>
          {canEditSupplier && <Tooltip title="Edit">
            <IconButton size="small" color="info" onClick={() => handleEdit(p.row)}><EditIcon fontSize="small" /></IconButton>
          </Tooltip>}
        </Stack>
      )
    },
  ];

  return (
    <Box sx={{ width: '100%', px: 1, py: 1 }}>
      {/* Header */}
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>{t('supplier_management')}</Typography>
          <Typography variant="body2" color="text.secondary">{t('manage_supplier_network')}</Typography>
        </Box>
        <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center' }}>
          <UploadExportToolbar canUpload={canCreateSupplier} onDownloadTemplate={supplierService.downloadTemplate} onUpload={supplierService.uploadTemplate} onExport={supplierService.export} templateFileName="SupplierTemplate.xlsx" exportFileName="Suppliers.xlsx" />
          {canCreateSupplier && <Button variant="contained" startIcon={<AddIcon />} onClick={handleAddNew} sx={{ px: 3 }}>{t('add_supplier')}</Button>}
        </Box>
      </Box>

      {/* Search */}
      <Paper sx={{ p: 1, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', backgroundColor: 'background.paper', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder={t('search_suppliers')} />
        <Box sx={{ flexGrow: 1 }} />
        <ColumnPreferences preferences={columnPreferences} />
      </Paper>

      {/* Grid */}
      {isError && <Alert severity="error" sx={{ mb: 1.5 }} action={<Button onClick={() => refetch()}>Retry</Button>}>
        {(error as any)?.response?.data?.detail || (error as Error)?.message || 'Suppliers could not be loaded.'}
      </Alert>}
      <Paper sx={{ height: 'calc(100vh - 220px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid rows={data?.items ?? []} columns={columnPreferences.arrangeColumns(columns)} rowCount={data?.totalCount ?? 0} loading={isLoading} pageSizeOptions={[10, 25, 50]} paginationModel={paginationModel} paginationMode="server" onPaginationModelChange={setPaginationModel} getRowId={(r) => r.id} disableRowSelectionOnClick columnVisibilityModel={columnPreferences.columnVisibilityModel} onColumnVisibilityModelChange={columnPreferences.onColumnVisibilityModelChange} />
      </Paper>

      {/* ── Dialog ─────────────────────────────────────────────────────────── */}
      <Dialog open={isModalOpen} onClose={() => setIsModalOpen(false)} fullWidth maxWidth="md">
        <DialogTitle sx={{ fontWeight: 800 }}>
          {selectedRecord ? `${t('edit_supplier')}: ${selectedRecord.name}` : t('add_new_supplier')}
        </DialogTitle>

        <DialogContent dividers sx={{ p: 3 }}>

          <Box sx={{ mb: 4 }}>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label={`${t('supplier_name')} *`} value={formData.name} onChange={f('name')} variant="outlined" />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label={t('contact_email')} type="email" value={formData.contactEmail} onChange={f('contactEmail')} />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label={t('payment_terms')} value={formData.paymentTerms} onChange={f('paymentTerms')} placeholder="e.g. Net 30" />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label={t('tags')} value={formData.tags} onChange={f('tags')} placeholder="electronics, preferred" />
              </Grid>
              <Grid size={{ xs: 12 }}>
                <Chip size="small" label={selectedRecord?.isActive === false ? 'Inactive' : 'Active'}
                  color={selectedRecord?.isActive === false ? 'default' : 'success'} variant="outlined" />
                <Typography variant="caption" color="text.secondary" sx={{ ml: 1 }}>
                  Activation and deactivation are recorded through Supplier Governance.
                </Typography>
              </Grid>
            </Grid>
          </Box>

          {/* ── Address Section ── */}
          <Box sx={{ mb: 4, p: 2, borderRadius: 2, bgcolor: 'grey.50', border: '1px solid', borderColor: 'divider' }}>
            <Stack direction="row" spacing={1} sx={{ mb: 2, alignItems: 'center' }}>
              <LocationIcon color="primary" fontSize="small" />
              <Typography variant="subtitle2" sx={{ fontWeight: 800, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
                {t('location_address')}
              </Typography>
            </Stack>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12 }}>
                <TextField fullWidth label={t('address_line_1')} value={formData.addressLine1} onChange={f('addressLine1')} size="small" />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label={t('address_line_2')} value={formData.addressLine2} onChange={f('addressLine2')} size="small" />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label={t('postal_code')} value={formData.postalCode} onChange={f('postalCode')} size="small" />
              </Grid>

              <Grid size={{ xs: 12, sm: 6 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>{t('country')}</InputLabel>
                  <Select
                    value={formData.countryId}
                    label={t('country')}
                    onChange={(e) => setFormData(p => ({ ...p, countryId: e.target.value as number, cityId: '' as any }))}
                  >
                    <MenuItem value=""><em>None</em></MenuItem>
                    {countries.map(c => (
                      <MenuItem key={c.countryId} value={c.countryId}>{c.countryName}</MenuItem>
                    ))}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>{t('city')}</InputLabel>
                  <Select
                    value={formData.cityId}
                    label={t('city')}
                    onChange={(e) => setFormData(p => ({ ...p, cityId: e.target.value as number }))}
                    disabled={!formData.countryId}
                  >
                    <MenuItem value=""><em>None</em></MenuItem>
                    {filteredCities.map(c => (
                      <MenuItem key={c.cityId} value={c.cityId}>{c.cityName}</MenuItem>
                    ))}
                  </Select>
                </FormControl>
              </Grid>
            </Grid>
          </Box>

          {/* ── Commercial defaults ── */}
          <Box sx={{ mb: 4, p: 2, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
            <Stack direction="row" spacing={1} sx={{ mb: 2, alignItems: 'center' }}>
              <CurrencyIcon color="primary" fontSize="small" />
              <Typography variant="subtitle2" sx={{ fontWeight: 800, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
                Commercial defaults
              </Typography>
            </Stack>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, sm: 4 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>{t('currency')}</InputLabel>
                  <Select
                    value={formData.currencyId}
                    label={t('currency')}
                    onChange={(e) => setFormData(p => ({ ...p, currencyId: e.target.value as number }))}
                  >
                    <MenuItem value=""><em>None</em></MenuItem>
                    {currencies.map(c => (
                      <MenuItem key={c.id} value={c.id}>{c.currencyName} ({c.code})</MenuItem>
                    ))}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12 }}>
                <TextField fullWidth multiline rows={2} label={t('comments')} value={formData.comments} onChange={f('comments')} size="small" />
              </Grid>
            </Grid>
          </Box>

          {/* ── Section: Contacts ── */}
          <Divider sx={{ my: 3 }} />
          <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 1.5 }}>
            <Typography variant="overline" sx={{ fontWeight: 800, color: 'text.secondary', letterSpacing: '0.08em' }}>
              {t('contacts')} {selectedRecord && !contactsLoading && `(${contacts.length})`}
            </Typography>
            {selectedRecord && canCreateContact && !showContactForm && (
              <Button size="small" variant="outlined" startIcon={<PersonAddIcon />}
                onClick={() => { setEditingContact(null); setContactForm(emptyContact); setShowContactForm(true); }}
                sx={{ fontWeight: 700, textTransform: 'none' }}>
                {t('add_contact')}
              </Button>
            )}
          </Box>

          {/* Note for new supplier */}
          {!selectedRecord && (
            <Typography variant="body2" sx={{ color: 'text.disabled', fontStyle: 'italic', mb: 1 }}>
              Save the supplier first, then you can add contacts from the Edit view.
            </Typography>
          )}

          {/* Inline contact sub-form */}
          {selectedRecord && canViewContacts && showContactForm && (
            <ContactSubForm
              value={contactForm}
              onChange={setContactForm}
              onSave={handleSaveContact}
              onCancel={() => { setShowContactForm(false); setEditingContact(null); setContactForm(emptyContact); }}
              isSaving={createContactMutation.isPending || updateContactMutation.isPending}
              isEdit={!!editingContact}
            />
          )}

          {/* Contacts table */}
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
                      {canEditContact && <Tooltip title="Edit">
                        <IconButton size="small" onClick={() => openEditContact(c)}><EditIcon fontSize="small" /></IconButton>
                      </Tooltip>}
                      {canDeleteContact && <Tooltip title="Remove">
                        <IconButton size="small" color="error" onClick={() => deleteContactMutation.mutate(c)}>
                          <DeleteIcon fontSize="small" />
                        </IconButton>
                      </Tooltip>}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}

          {selectedRecord && !contactsLoading && contacts.length === 0 && !showContactForm && (
            <Box sx={{ textAlign: 'center', py: 3 }}>
              <PersonAddIcon sx={{ fontSize: 36, color: 'action.disabled', mb: 0.5 }} />
              <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>{canCreateContact ? 'No contacts yet. Add the first contact.' : 'No contacts are recorded.'}</Typography>
            </Box>
          )}

          {/* AA-01 · fields this tenant defined on Supplier. Renders nothing when none exist,
              and only once the record is persisted — a value bag needs a row to hang on. */}
          <CustomFieldValuesEditor
            entityType="Supplier"
            entityId={selectedRecord?.id ?? null}
            canEdit={canEditSupplier}
          />

        </DialogContent>

        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setIsModalOpen(false)} color="inherit">Cancel</Button>
          <Button variant="contained" onClick={handleSaveSupplier} disabled={isBusy}>
            {isBusy ? <CircularProgress size={22} /> : t('save_supplier')}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default SuppliersPage;
