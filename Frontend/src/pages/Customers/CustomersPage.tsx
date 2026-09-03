import React, { useEffect, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Dialog, DialogTitle, DialogContent, DialogActions,
  Grid, FormControlLabel, Switch, TextField, CircularProgress,
  Table, TableHead, TableRow, TableCell, TableBody,
  Tooltip, Divider, MenuItem, Select, FormControl, InputLabel, Alert,
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
import contactService, { type ContactDTO, type ContactMutationRequest } from '../../api/services/contactService';
import countryService from '../../api/services/countryService';
import stateService from '../../api/services/stateService';
import cityService from '../../api/services/cityService';
import teamService from '../../api/services/teamService';
import { useAuth } from '../../context/AuthContext';
import SearchField from '../../components/common/SearchField';
import { gridEmptyOverlay } from '../../components/common/gridOverlays';
import UploadExportToolbar from '../../components/common/UploadExportToolbar';
import ColumnPreferences from '../../components/common/ColumnPreferences';
import CustomFieldValuesEditor from '../../components/common/CustomFieldValuesEditor';
import useColumnPreferences from '../../hooks/useColumnPreferences';
import { useSnackbar } from 'notistack';

// ─── Empty forms ───────────────────────────────────────────────────────────
const emptyCustomer = {
  name: '', contactEmail: '',
  billingAddressLine1: '', billingAddressLine2: '',
  billingCity: '', billingState: '', billingCountry: '', billingPostalCode: '',
  shippingAddressLine1: '', shippingAddressLine2: '',
  shippingCity: '', shippingState: '', shippingCountry: '', shippingPostalCode: '',
  isActive: true,
  // FR-CST-01/02. Empty string means NOT CAPTURED and is sent as such; the server
  // canonicalises it to NULL rather than storing "".
  commercialRegistrationNumber: '', taxRegistrationNumber: '', sector: '',
  regionStateId: '', accountTeamId: '',
};

/**
 * The stored sector CODES and their labels. The code is what travels and what is stored — a
 * renamed label must not orphan the rows classified under the old wording.
 */
const SECTOR_OPTIONS: { code: string; label: string }[] = [
  { code: 'GOVERNMENT', label: 'Government' },
  { code: 'SEMI_GOVERNMENT', label: 'Semi-Government' },
  { code: 'PRIVATE', label: 'Private' },
];

const sectorLabel = (code?: string | null) =>
  SECTOR_OPTIONS.find(option => option.code === code)?.label ?? null;

/**
 * The server's own validation message, verbatim. The CR and VAT rules live server-side (they are
 * also enforced by a database CHECK constraint), so restating them in client copy would risk two
 * descriptions of one rule that can drift apart. ASP.NET returns either a plain string or a
 * ProblemDetails `errors` bag; both are unwrapped here, and only a genuinely unreadable response
 * falls back to the caller's generic wording.
 */
const serverMessage = (error: unknown, fallback: string): string => {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;
  const errors = (data as { errors?: Record<string, string[]> } | undefined)?.errors;
  if (errors) {
    const first = Object.values(errors).flat().find(message => typeof message === 'string' && message.trim());
    if (first) return first;
  }
  const title = (data as { title?: string } | undefined)?.title;
  return typeof title === 'string' && title.trim() ? title : fallback;
};

type CustomerFormState = typeof emptyCustomer & { imageFile?: File | null };

const emptyContact = {
  firstName: '', middleName: '', lastName: '', email: '',
  phoneNo: '', mobileNo: '', position: '', isPrimary: false, isActive: true,
};

const validEmail = (value: string) => !value.trim() || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value.trim());

// ─── Contact inline sub-form ───────────────────────────────────────────────
const ContactSubForm: React.FC<{
  value: typeof emptyContact;
  onChange: (v: typeof emptyContact) => void;
  onSave: () => void;
  onCancel: () => void;
  isSaving: boolean;
  isEdit: boolean;
  errors: { firstName?: string; lastName?: string; email?: string };
}> = ({ value, onChange, onSave, onCancel, isSaving, isEdit, errors }) => {
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
          <TextField size="small" fullWidth required label="First Name" value={value.firstName} onChange={f('firstName')} error={!!errors.firstName} helperText={errors.firstName} />
        </Grid>
        <Grid size={{ xs: 12, sm: 4 }}>
          <TextField size="small" fullWidth label="Middle Name" value={value.middleName} onChange={f('middleName')} />
        </Grid>
        <Grid size={{ xs: 12, sm: 4 }}>
          <TextField size="small" fullWidth required label="Last Name" value={value.lastName} onChange={f('lastName')} error={!!errors.lastName} helperText={errors.lastName} />
        </Grid>
        <Grid size={{ xs: 12, sm: 4 }}>
          <TextField size="small" fullWidth label="Email" type="email" value={value.email} onChange={f('email')} error={!!errors.email} helperText={errors.email} />
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
/** Said wherever a customer cannot be created, so the absence is never unexplained. */
const NO_CREATE_PERMISSION = 'Ask your administrator for permission to add customers.';

const CustomersPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const { userData, hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const canCreate = hasPermission('Customers', 'create');
  const canEdit = hasPermission('Customers', 'edit');
  const canDelete = hasPermission('Customers', 'delete');

  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  // AA-01 · per-user column layout for this grid, plus tenant-defined Customer fields.
  const columnPreferences = useColumnPreferences('customers.list');
  const [search, setSearch] = useState('');
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedRecord, setSelectedRecord] = useState<CustomerDTO | null>(null);
  const [formData, setFormData] = useState<CustomerFormState>(emptyCustomer);
  const [sameAsB, setSameAsB] = useState(false);
  const [customerErrors, setCustomerErrors] = useState<{
    name?: string;
    contactEmail?: string;
    commercialRegistrationNumber?: string;
    taxRegistrationNumber?: string;
  }>({});

  // Contact state
  const [showContactForm, setShowContactForm] = useState(false);
  const [editingContact, setEditingContact] = useState<ContactDTO | null>(null);
  const [contactForm, setContactForm] = useState(emptyContact);
  const [contactErrors, setContactErrors] = useState<{ firstName?: string; lastName?: string; email?: string }>({});
  const [contactToDeactivate, setContactToDeactivate] = useState<ContactDTO | null>(null);
  const [customerToDeactivate, setCustomerToDeactivate] = useState<CustomerDTO | null>(null);
  const editCustomerId = Number(searchParams.get('edit'));

  // ── Queries ──
  const customerListQuery = useQuery({
    queryKey: ['customers', paginationModel, search],
    queryFn: () => customerService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      name: search || undefined,
    }),
  });
  const { data, isLoading } = customerListQuery;

  const requestedCustomerQuery = useQuery({
    queryKey: ['customer-detail', editCustomerId],
    queryFn: () => customerService.getById(editCustomerId),
    enabled: canEdit && Number.isInteger(editCustomerId) && editCustomerId > 0,
    retry: false,
  });

  const contactsQuery = useQuery({
    queryKey: ['customer-contacts', selectedRecord?.id],
    queryFn: () => contactService.getByCustomer(selectedRecord!.id),
    enabled: !!selectedRecord?.id && isModalOpen,
  });
  const { data: contacts = [], isLoading: contactsLoading } = contactsQuery;

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

  // FR-CST-02 — the account teams a customer can be assigned to.
  const { data: teams = [] } = useQuery({
    queryKey: ['teams', buid],
    queryFn: () => teamService.getAll(),
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
    onError: (error) => enqueueSnackbar(serverMessage(error, 'Failed to create customer'), { variant: 'error' }),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, fd }: { id: number; fd: FormData }) => customerService.update(id, fd),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['customers'] }); enqueueSnackbar('Customer updated!', { variant: 'success' }); setIsModalOpen(false); },
    onError: (error) => enqueueSnackbar(serverMessage(error, 'Failed to update customer'), { variant: 'error' }),
  });

  const deactivateCustomerMutation = useMutation({
    mutationFn: (customer: CustomerDTO) => customerService.delete(customer.id, customer.concurrencyToken),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['customers'] });
      enqueueSnackbar('Customer and active contacts deactivated', { variant: 'success' });
      setCustomerToDeactivate(null);
      setIsModalOpen(false);
    },
    onError: () => enqueueSnackbar('Failed to deactivate customer. Reload the record and try again.', { variant: 'error' }),
  });

  // ── Contact mutations ──
  const createContactMutation = useMutation({
    mutationFn: (body: ContactMutationRequest) => contactService.create(body),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['customer-contacts', selectedRecord?.id] }); enqueueSnackbar('Contact added!', { variant: 'success' }); setShowContactForm(false); setContactForm(emptyContact); },
    onError: () => enqueueSnackbar('Failed to add contact', { variant: 'error' }),
  });

  const updateContactMutation = useMutation({
    mutationFn: ({ id, body }: { id: number; body: ContactMutationRequest }) => contactService.update(id, body),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['customer-contacts', selectedRecord?.id] }); enqueueSnackbar('Contact updated!', { variant: 'success' }); setShowContactForm(false); setEditingContact(null); setContactForm(emptyContact); },
    onError: () => enqueueSnackbar('Failed to update contact', { variant: 'error' }),
  });

  const deleteContactMutation = useMutation({
    mutationFn: (contact: ContactDTO) => contactService.delete(contact.id, contact.concurrencyToken),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['customer-contacts', selectedRecord?.id] }); enqueueSnackbar('Contact removed', { variant: 'info' }); },
    onError: () => enqueueSnackbar('Failed to delete contact', { variant: 'error' }),
  });

  // ── Handlers ──
  const handleEdit = React.useCallback((record: CustomerDTO) => {
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
      commercialRegistrationNumber: record.commercialRegistrationNumber ?? '',
      taxRegistrationNumber: record.taxRegistrationNumber ?? '',
      sector: record.sector ?? '',
      regionStateId: record.regionStateId != null ? String(record.regionStateId) : '',
      accountTeamId: record.accountTeamId != null ? String(record.accountTeamId) : '',
    });
    setSameAsB(false);
    setShowContactForm(false);
    setCustomerErrors({});
    setContactErrors({});
    setIsModalOpen(true);
  }, []);

  useEffect(() => {
    if (!requestedCustomerQuery.data) return;
    handleEdit(requestedCustomerQuery.data);
    setSearchParams(current => {
      const next = new URLSearchParams(current);
      next.delete('edit');
      return next;
    }, { replace: true });
  }, [handleEdit, requestedCustomerQuery.data, setSearchParams]);

  const handleAddNew = () => {
    setSelectedRecord(null);
    setFormData(emptyCustomer);
    setSameAsB(false);
    setShowContactForm(false);
    setContactForm(emptyContact);
    setCustomerErrors({});
    setContactErrors({});
    setIsModalOpen(true);
  };

  const handleSaveCustomer = () => {
    const errors = {
      name: formData.name.trim() ? undefined : 'Customer name is required.',
      contactEmail: validEmail(formData.contactEmail) ? undefined : 'Enter a valid email address.',
    };
    setCustomerErrors(errors);
    if (errors.name || errors.contactEmail) return;

    const fd = new FormData();
    Object.entries(formData).forEach(([k, v]) => {
      if (selectedRecord && k === 'isActive') return;
      if (k === 'imageFile') {
        if (v) fd.append('ImageFile', v as File);
      } else if (k === 'regionStateId' || k === 'accountTeamId') {
        // Omitted entirely when unset. Sending "" for a nullable numeric would bind as a value
        // rather than as an absence on some model binders, and 0 is not a key — it would satisfy
        // "not null" and then match no team and no region for the rest of the record's life.
        if (v !== '' && v != null) fd.append(k, String(v));
      } else {
        fd.append(k, String(v));
      }
    });
    if (selectedRecord) fd.append('ConcurrencyToken', selectedRecord.concurrencyToken);
    selectedRecord
      ? updateMutation.mutate({ id: selectedRecord.id, fd })
      : createMutation.mutate(fd);
  };

  const handleSaveContact = () => {
    if (!selectedRecord) return;

    const errors = {
      firstName: contactForm.firstName.trim() ? undefined : 'First name is required.',
      lastName: contactForm.lastName.trim() ? undefined : 'Last name is required.',
      email: validEmail(contactForm.email) ? undefined : 'Enter a valid email address.',
    };
    setContactErrors(errors);
    if (errors.firstName || errors.lastName || errors.email) return;

    // Validate IsPrimary: Only one primary contact per parent
    if (contactForm.isPrimary) {
      const existingPrimary = contacts.find(c => c.isPrimary && (!editingContact || c.id !== editingContact.id));
      if (existingPrimary) {
        enqueueSnackbar('A primary contact already exists for this customer.', { variant: 'warning' });
        return;
      }
    }

    const { isActive, ...editableContact } = contactForm;
    const body: ContactMutationRequest = {
      ...editableContact,
      customerId: selectedRecord.id,
      ...(!editingContact ? { isActive } : {}),
      ...(editingContact ? { concurrencyToken: editingContact.concurrencyToken } : {}),
    };
    editingContact
      ? updateContactMutation.mutate({ id: editingContact.id, body })
      : createContactMutation.mutate(body);
  };

  const openEditContact = (c: ContactDTO) => {
    setEditingContact(c);
    setContactForm({ firstName: c.firstName ?? '', middleName: c.middleName ?? '', lastName: c.lastName ?? '', email: c.email ?? '', phoneNo: c.phoneNo ?? '', mobileNo: c.mobileNo ?? '', position: c.position ?? '', isPrimary: c.isPrimary ?? false, isActive: c.isActive ?? true });
    setContactErrors({});
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
    // ── FR-CST-01/02 ──
    // Each renders a STATED gap rather than an em dash that could be read as "none required".
    {
      field: 'accountTeamName', headerName: 'Account team', width: 160,
      renderCell: (p) => p.value ?? (
        <Typography variant="caption" sx={{ color: 'warning.main', fontWeight: 700 }}>
          No account team
        </Typography>
      ),
    },
    {
      field: 'sector', headerName: 'Sector', width: 140,
      renderCell: (p) => sectorLabel(p.value as string | null) ?? (
        <Typography variant="caption" sx={{ color: 'text.disabled' }}>Not classified</Typography>
      ),
    },
    {
      field: 'regionName', headerName: 'Region', width: 140,
      renderCell: (p) => p.value ?? (
        <Typography variant="caption" sx={{ color: 'text.disabled' }}>Not stated</Typography>
      ),
    },
    {
      field: 'commercialRegistrationNumber', headerName: 'CR number', width: 150,
      renderCell: (p) => p.value
        ? <Typography sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>{p.value}</Typography>
        : <Typography variant="caption" sx={{ color: 'text.disabled' }}>Not captured</Typography>,
    },
    {
      field: 'taxRegistrationNumber', headerName: 'VAT number', width: 160,
      renderCell: (p) => p.value
        ? <Typography sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>{p.value}</Typography>
        : <Typography variant="caption" sx={{ color: 'text.disabled' }}>Not captured</Typography>,
    },
    // AA-01: already in the payload, previously discarded by this grid. Off by default,
    // one tick away for anyone who ships to a different address than they bill.
    { field: 'shippingCity', headerName: 'Shipping city', width: 130, renderCell: (p) => p.value ?? '—' },
    { field: 'shippingCountry', headerName: 'Shipping country', width: 140, renderCell: (p) => p.value ?? '—' },
    { field: 'isActive', headerName: t('status'), width: 100, renderCell: (p) => <Chip label={p.value ? 'Active' : 'Inactive'} color={p.value ? 'success' : 'error'} size="small" variant="outlined" /> },
    { field: 'createdOn', headerName: 'Created', width: 120, renderCell: (p) => (p.value ? new Date(String(p.value)).toLocaleDateString() : '—') },
    { 
      field: 'actions', 
      headerName: t('actions'), 
      width: 160,
      sortable: false, 
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5}>
          <Tooltip title="View Details">
            <IconButton size="small" color="primary" onClick={() => navigate(`/customers/${p.row.id}`)}><ViewIcon fontSize="small" /></IconButton>
          </Tooltip>
          {canEdit && <Tooltip title="Edit">
            <IconButton size="small" color="info" onClick={() => handleEdit(p.row)}><EditIcon fontSize="small" /></IconButton>
          </Tooltip>}
          {canDelete && p.row.isActive && <Tooltip title="Deactivate customer">
            <IconButton size="small" color="error" onClick={() => setCustomerToDeactivate(p.row)}><DeleteIcon fontSize="small" /></IconButton>
          </Tooltip>}
        </Stack>
      )
    },
  ];

  // AA-01: this user's saved layout, plus a column for every custom field the tenant has
  // defined on Customer. `customFields` is the raw jsonb bag carried on each list row.
  /**
   * Three different nothings used to render as MUI's bare "No rows": a tenant that has not added
   * a customer yet, a search that matched none, and a failed request whose rows fell through as
   * an empty array. A salesperson acts differently on each. The error case is already answered by
   * the alert above the grid — which also blanks the rows — so this covers the two the grid can
   * tell apart, and each carries the button that is actually the next move.
   *
   * Memoised because DataGrid takes a component TYPE here: a new factory each render would remount
   * the overlay for no reason.
   */
  const noRowsOverlay = React.useMemo(() => gridEmptyOverlay({
    title: 'No customers yet',
    message: 'Customers appear here once you add them, or when an inquiry is matched to a new company.',
    action: canCreate
      ? <Button variant="contained" onClick={handleAddNew} sx={{ fontWeight: 700 }}>Add the first customer</Button>
      : (
        <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 360 }}>
          {NO_CREATE_PERMISSION}
        </Typography>
      ),
    filtered: search.trim().length > 0,
    filteredTitle: 'No customer matches this search',
    filteredMessage: 'Clear the search to see every customer.',
    filteredAction: (
      <Button variant="outlined" onClick={() => setSearch('')} sx={{ fontWeight: 700 }}>Clear the search</Button>
    ),
  }), [canCreate, search, handleAddNew]);

  const orderedColumns = columnPreferences.arrangeColumns(columns);

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
            canUpload={canCreate && canEdit}
          />
          {canCreate
            ? <Button variant="contained" startIcon={<AddIcon />} onClick={handleAddNew} sx={{ px: 3 }}>Add Customer</Button>
            : (
              /* A missing button is a support ticket unless it says why it is missing. */
              <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 300, textAlign: 'right' }}>
                {NO_CREATE_PERMISSION}
              </Typography>
            )}
        </Box>
      </Box>

      {/* Search */}
      <Paper sx={{ p: 1, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', backgroundColor: 'background.paper', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search customers..." />
        <Box sx={{ flexGrow: 1 }} />
        <ColumnPreferences preferences={columnPreferences} />
      </Paper>

      {requestedCustomerQuery.isError && (
        <Alert severity="error" sx={{ mb: 1.5 }} action={<Button color="inherit" onClick={() => void requestedCustomerQuery.refetch()}>Retry</Button>}>
          The requested customer could not be opened for editing.
        </Alert>
      )}

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 220px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        {/* The failure REPLACES the grid. An error alert above an empty-state panel reading "no
            customers yet" contradicts itself, and the reader believes the wrong half. */}
        {customerListQuery.isError ? (
          <Box sx={{ height: '100%', display: 'grid', placeItems: 'center', p: 3, textAlign: 'center' }}>
            <Alert severity="error" sx={{ maxWidth: 520, borderRadius: 2 }} action={<Button color="inherit" onClick={() => void customerListQuery.refetch()}>Retry</Button>}>
              Customers could not be loaded. No empty result has been assumed.
            </Alert>
          </Box>
        ) : (
        <DataGrid aria-label="Customers" slots={{ noRowsOverlay }} rows={data?.items ?? []} columns={orderedColumns} rowCount={data?.totalCount ?? 0} loading={isLoading} pageSizeOptions={[10, 25, 50]} paginationModel={paginationModel} paginationMode="server" onPaginationModelChange={setPaginationModel} getRowId={(r) => r.id} disableRowSelectionOnClick columnVisibilityModel={columnPreferences.columnVisibilityModel} onColumnVisibilityModelChange={columnPreferences.onColumnVisibilityModelChange} />
        )}
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
                <TextField fullWidth required label="Customer Name" value={formData.name} onChange={f('name')} error={!!customerErrors.name} helperText={customerErrors.name} />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label="Contact Email" type="email" value={formData.contactEmail} onChange={f('contactEmail')} error={!!customerErrors.contactEmail} helperText={customerErrors.contactEmail} />
              </Grid>
              <Grid size={{ xs: 12 }}>
                {selectedRecord
                  ? <Chip label={formData.isActive ? 'Active' : 'Inactive'} color={formData.isActive ? 'success' : 'default'} variant="outlined" />
                  : <FormControlLabel
                      control={<Switch checked={formData.isActive} onChange={(e) => setFormData(p => ({ ...p, isActive: e.target.checked }))} color="success" />}
                      label={<Typography variant="subtitle2" sx={{ fontWeight: 700 }}>Active Status</Typography>}
                    />}
              </Grid>
            </Grid>
          </Box>

          {/* ── FR-CST-01 · Registration and account ─────────────────────────
              The identifiers a KSA counterparty is verified against, plus the account team
              that owns the relationship. Each field states what "empty" means rather than
              leaving a blank that reads like a loading state. */}
          <Box sx={{ mb: 4, p: 2, borderRadius: 2, bgcolor: 'grey.50', border: '1px solid', borderColor: 'divider' }}>
            <Stack direction="row" spacing={1} sx={{ mb: 2, alignItems: 'center' }}>
              <BillingIcon color="primary" fontSize="small" />
              <Typography variant="subtitle2" sx={{ fontWeight: 800, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
                Registration &amp; Account
              </Typography>
            </Stack>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField
                  fullWidth size="small"
                  label="CR number"
                  value={formData.commercialRegistrationNumber}
                  onChange={f('commercialRegistrationNumber')}
                  error={!!customerErrors.commercialRegistrationNumber}
                  helperText={customerErrors.commercialRegistrationNumber
                    ?? 'KSA commercial registration: 10 digits. For a non-Saudi registration, include the country prefix. Leave empty if not captured.'}
                />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField
                  fullWidth size="small"
                  label="VAT registration number"
                  value={formData.taxRegistrationNumber}
                  onChange={f('taxRegistrationNumber')}
                  error={!!customerErrors.taxRegistrationNumber}
                  helperText={customerErrors.taxRegistrationNumber
                    ?? 'KSA VAT number: 15 digits, beginning and ending with 3. Leave empty if the customer is not registered.'}
                />
              </Grid>
              <Grid size={{ xs: 12, sm: 4 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>Sector</InputLabel>
                  <Select
                    value={formData.sector}
                    label="Sector"
                    onChange={(e) => setFormData(p => ({ ...p, sector: e.target.value as string }))}
                  >
                    {/* "Not classified" is a real answer and is stored as NULL. It is deliberately
                        NOT defaulted to Private — the difference decides whether government
                        procurement rules apply. */}
                    <MenuItem value=""><em>Not classified</em></MenuItem>
                    {SECTOR_OPTIONS.map(option => (
                      <MenuItem key={option.code} value={option.code}>{option.label}</MenuItem>
                    ))}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12, sm: 4 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>Region</InputLabel>
                  <Select
                    value={formData.regionStateId}
                    label="Region"
                    onChange={(e) => setFormData(p => ({ ...p, regionStateId: e.target.value as string }))}
                  >
                    {/* The tenant's governed region master — the same list routing resolves sales
                        territory against, not a free-typed string. */}
                    <MenuItem value=""><em>Not stated</em></MenuItem>
                    {states.map(state => (
                      <MenuItem key={state.stateId} value={String(state.stateId)}>{state.stateName}</MenuItem>
                    ))}
                  </Select>
                </FormControl>
              </Grid>
              <Grid size={{ xs: 12, sm: 4 }}>
                <FormControl fullWidth size="small">
                  <InputLabel>Account team</InputLabel>
                  <Select
                    value={formData.accountTeamId}
                    label="Account team"
                    onChange={(e) => setFormData(p => ({ ...p, accountTeamId: e.target.value as string }))}
                  >
                    {/* Assigning a team NARROWS who can read this customer. Leaving it empty
                        leaves the record readable by everyone who holds the Customers permission,
                        which is what it was before this field existed. */}
                    <MenuItem value=""><em>No account team (readable tenant-wide)</em></MenuItem>
                    {teams.map(team => (
                      <MenuItem key={team.id} value={String(team.id)}>{team.teamName}</MenuItem>
                    ))}
                  </Select>
                </FormControl>
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
            {selectedRecord && canCreate && !showContactForm && (
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
              errors={contactErrors}
            />
          )}

          {selectedRecord && contactsQuery.isError && (
            <Alert severity="error" action={<Button color="inherit" onClick={() => void contactsQuery.refetch()}>Retry</Button>}>
              Contacts could not be loaded. No empty result has been assumed.
            </Alert>
          )}

          {selectedRecord && !contactsQuery.isError && !contactsLoading && contacts.length > 0 && (
            <Table size="small" aria-label="Customer contacts" sx={{ mt: 1 }}>
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
                      {canEdit && <Tooltip title="Edit contact">
                        <IconButton size="small" onClick={() => openEditContact(c)}><EditIcon fontSize="small" /></IconButton>
                      </Tooltip>}
                      {canDelete && c.isActive && <Tooltip title="Deactivate contact">
                        <IconButton size="small" color="error" disabled={deleteContactMutation.isPending} onClick={() => setContactToDeactivate(c)}>
                          <DeleteIcon fontSize="small" />
                        </IconButton>
                      </Tooltip>}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}

          {selectedRecord && !contactsQuery.isError && !contactsLoading && contacts.length === 0 && !showContactForm && (
            <Box sx={{ textAlign: 'center', py: 3 }}>
              <PersonAddIcon sx={{ fontSize: 36, color: 'action.disabled', mb: 0.5 }} />
              <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>No contacts yet. Click "Add Contact" to add one.</Typography>
            </Box>
          )}

          {/* AA-01 · fields this tenant defined on Customer. Renders nothing when none exist,
              and only once the record is persisted — a value bag needs a row to hang on. */}
          <CustomFieldValuesEditor
            entityType="Customer"
            entityId={selectedRecord?.id ?? null}
            canEdit={canEdit}
          />

        </DialogContent>

        <DialogActions sx={{ p: 2 }}>
          {selectedRecord && canDelete && selectedRecord.isActive && (
            <Button color="error" startIcon={<DeleteIcon />} onClick={() => setCustomerToDeactivate(selectedRecord)} sx={{ mr: 'auto' }}>
              Deactivate
            </Button>
          )}
          <Button onClick={() => setIsModalOpen(false)} color="inherit">Cancel</Button>
          <Button variant="contained" onClick={handleSaveCustomer} disabled={isBusy}>
            {isBusy ? <CircularProgress size={22} /> : 'Save Customer'}
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog open={!!customerToDeactivate} onClose={() => !deactivateCustomerMutation.isPending && setCustomerToDeactivate(null)} aria-labelledby="deactivate-customer-title">
        <DialogTitle id="deactivate-customer-title">Deactivate customer?</DialogTitle>
        <DialogContent>
          <Alert severity="warning" sx={{ mb: 1.5 }}>All active contacts for this customer will also be deactivated.</Alert>
          <Typography variant="body2">
            {customerToDeactivate ? `${customerToDeactivate.name} will no longer be available for new commercial work. Historical records remain intact.` : ''}
          </Typography>
        </DialogContent>
        <DialogActions>
          <Button color="inherit" disabled={deactivateCustomerMutation.isPending} onClick={() => setCustomerToDeactivate(null)}>Cancel</Button>
          <Button color="error" variant="contained" disabled={deactivateCustomerMutation.isPending} onClick={() => customerToDeactivate && deactivateCustomerMutation.mutate(customerToDeactivate)}>
            {deactivateCustomerMutation.isPending ? <CircularProgress size={20} /> : 'Deactivate customer'}
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog open={!!contactToDeactivate} onClose={() => !deleteContactMutation.isPending && setContactToDeactivate(null)} aria-labelledby="deactivate-contact-title">
        <DialogTitle id="deactivate-contact-title">Deactivate contact?</DialogTitle>
        <DialogContent>
          <Typography variant="body2">
            {contactToDeactivate ? `${[contactToDeactivate.firstName, contactToDeactivate.lastName].filter(Boolean).join(' ')} will no longer be available for new customer work.` : ''}
          </Typography>
        </DialogContent>
        <DialogActions>
          <Button color="inherit" disabled={deleteContactMutation.isPending} onClick={() => setContactToDeactivate(null)}>Cancel</Button>
          <Button color="error" variant="contained" disabled={deleteContactMutation.isPending} onClick={() => {
            if (!contactToDeactivate) return;
            deleteContactMutation.mutate(contactToDeactivate, { onSuccess: () => setContactToDeactivate(null) });
          }}>
            {deleteContactMutation.isPending ? <CircularProgress size={20} /> : 'Deactivate'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default CustomersPage;
