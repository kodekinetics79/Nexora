import React, { useState, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Dialog, DialogTitle, DialogContent, DialogActions,
  Button, TextField, Grid,
  Divider, Typography, CircularProgress,
} from '@mui/material';
import supplierService, { type SupplierDTO } from '../../api/services/supplierService';
import { useAuth } from '../../context/AuthContext';
import { useSnackbar } from 'notistack';

interface Props {
  open: boolean;
  onClose: () => void;
  supplierId?: number;
}

const empty = {
  name: '', contactEmail: '', paymentTerms: '', addressLine1: '', addressLine2: '',
  postalCode: '', tags: '', comments: '', cityId: '', countryId: '', currencyId: '',
  concurrencyToken: '',
};

const SectionLabel = ({ label }: { label: string }) => (
  <Grid size={{ xs: 12 }}>
    <Divider sx={{ mt: 0.5 }} />
    <Typography variant="overline" sx={{ fontWeight: 700, color: 'text.secondary', letterSpacing: 1, mt: 1.5, display: 'block' }}>
      {label}
    </Typography>
  </Grid>
);

const SupplierFormDialog: React.FC<Props> = ({ open, onClose, supplierId }) => {
  const { userData } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const isEdit = !!supplierId;

  const [form, setForm] = useState(empty);
  const normalizedEmail = form.contactEmail.trim();
  const emailInvalid = normalizedEmail.length > 0 && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(normalizedEmail);
  const nameInvalid = form.name.trim().length === 0;

  const { data: editData } = useQuery({
    queryKey: ['supplier-detail', supplierId],
    queryFn: () => supplierService.getById(supplierId!),
    enabled: open && isEdit,
  });

  useEffect(() => {
    if (editData && open && isEdit) {
      setForm({
        name: editData.name ?? '',
        contactEmail: editData.contactEmail ?? '',
        paymentTerms: editData.paymentTerms ?? '',
        addressLine1: editData.addressLine1 ?? '',
        addressLine2: editData.addressLine2 ?? '',
        postalCode: editData.postalCode ?? '',
        tags: editData.tags ?? '',
        comments: editData.comments ?? '',
        cityId: editData.cityId ? String(editData.cityId) : '',
        countryId: editData.countryId ? String(editData.countryId) : '',
        currencyId: editData.currencyId ? String(editData.currencyId) : '',
        concurrencyToken: editData.concurrencyToken ?? '',
      });
    }
  }, [editData, open, isEdit]);

  const saveMutation = useMutation<SupplierDTO | void, Error, FormData>({
    mutationFn: (fd: FormData) =>
      isEdit ? supplierService.update(supplierId!, fd) : supplierService.create(fd),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['suppliers'] });
      queryClient.invalidateQueries({ queryKey: ['supplier-detail', supplierId] });
      enqueueSnackbar(isEdit ? 'Supplier updated!' : 'Supplier created!', { variant: 'success' });
      handleClose();
    },
    onError: (error: any) => enqueueSnackbar(
      error?.response?.data?.detail || error?.response?.data || error?.message || 'Failed to save supplier.',
      { variant: 'error' },
    ),
  });

  const handleClose = () => { setForm(empty); onClose(); };

  const handleSave = () => {
    if (nameInvalid || emailInvalid) return;
    const fd = new FormData();
    Object.entries(form).forEach(([k, v]) => {
      if (v !== '' && v !== null && v !== undefined) fd.append(k, String(v));
    });
    fd.append(isEdit ? 'modifiedBy' : 'createdBy', userData.userName || 'System');
    saveMutation.mutate(fd);
  };

  const f = (field: string) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm(p => ({ ...p, [field]: e.target.value }));

  return (
    <Dialog open={open} onClose={handleClose} fullWidth maxWidth="md">
      <DialogTitle sx={{ fontWeight: 800 }}>{isEdit ? 'Edit Supplier' : 'Add New Supplier'}</DialogTitle>
      <DialogContent dividers sx={{ p: 3 }}>
        <Grid container spacing={2}>

          <SectionLabel label="Basic Information" />
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField fullWidth label="Supplier Name" value={form.name} onChange={f('name')} required error={nameInvalid} helperText={nameInvalid ? 'Supplier name is required.' : undefined} />
          </Grid>
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField fullWidth label="Contact Email" type="email" value={form.contactEmail} onChange={f('contactEmail')} error={emailInvalid} helperText={emailInvalid ? 'Enter a valid email address.' : undefined} />
          </Grid>
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField fullWidth label="Payment Terms" value={form.paymentTerms} onChange={f('paymentTerms')} placeholder="e.g. Net 30, COD" />
          </Grid>
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField fullWidth label="Tags" value={form.tags} onChange={f('tags')} placeholder="e.g. electronics, preferred" />
          </Grid>
          <Grid size={{ xs: 12 }}>
            <TextField fullWidth multiline rows={2} label="Comments" value={form.comments} onChange={f('comments')} />
          </Grid>

          <SectionLabel label="Address" />
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField fullWidth label="Address Line 1" value={form.addressLine1} onChange={f('addressLine1')} />
          </Grid>
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField fullWidth label="Address Line 2" value={form.addressLine2} onChange={f('addressLine2')} />
          </Grid>
          <Grid size={{ xs: 12, sm: 4 }}>
            <TextField fullWidth label="Postal Code" value={form.postalCode} onChange={f('postalCode')} />
          </Grid>

        </Grid>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={handleClose} color="inherit">Cancel</Button>
        <Button variant="contained" onClick={handleSave} disabled={saveMutation.isPending || nameInvalid || emailInvalid} disableElevation sx={{ px: 4, fontWeight: 700 }}>
          {saveMutation.isPending ? <CircularProgress size={22} /> : (isEdit ? 'Update Supplier' : 'Create Supplier')}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default SupplierFormDialog;
