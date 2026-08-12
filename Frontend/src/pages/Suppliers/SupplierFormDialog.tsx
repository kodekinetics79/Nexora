import React, { useState, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Dialog, DialogTitle, DialogContent, DialogActions,
  Button, TextField, Grid, MenuItem,
  Divider, Typography, CircularProgress,
} from '@mui/material';
import supplierService, { SUPPLIER_TIERS, type SupplierDTO } from '../../api/services/supplierService';
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
  taxRegistrationNumber: '', tier: '', creditDays: '', concurrencyToken: '',
};

/**
 * Mirrors ERP_RFQ_Automation.Tax.TaxRegistrationNumbers so the rep is told before the round trip.
 * Separators are stripped, then: a value that CLAIMS to be Saudi — all digits, leading 3 — must be
 * a well-formed 15-digit KSA VAT number (3…3). Anything else is accepted as a foreign
 * registration, because a supplier in Germany or China will never have a Saudi TRN.
 */
const canonicalTrn = (value: string) => value.replace(/[\s\-–—]/g, '').toUpperCase();

const trnError = (value: string): string | undefined => {
  const trn = canonicalTrn(value);
  if (trn.length === 0) return undefined;
  if (trn.length > 50) return 'Tax registration number is longer than 50 characters.';
  if (trn.length < 5) return 'Tax registration number is too short to be a registration number.';
  if (!/^[A-Z0-9./]+$/.test(trn)) return "Use only letters, digits, '.' and '/'.";
  if (/^3\d*$/.test(trn) && !/^3\d{13}3$/.test(trn))
    return 'A KSA VAT number is exactly 15 digits, beginning with 3 and ending with 3. For a non-Saudi registration, include its country prefix.';
  return undefined;
};

/**
 * Blank is a valid answer and means "not captured". Anything typed has to be a whole, non-negative
 * number of days, bounded well above any real credit term so a mistyped year cannot become one.
 */
const creditDaysMessage = (value: string): string | undefined => {
  const trimmed = value.trim();
  if (trimmed.length === 0) return undefined;
  const days = Number(trimmed);
  if (!Number.isFinite(days) || !Number.isInteger(days)) return 'Enter a whole number of days.';
  if (days < 0) return 'Credit days cannot be negative.';
  if (days > 365) return 'Credit terms longer than a year are not supported. Record them in the free-text payment terms.';
  return undefined;
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
  const taxRegistrationError = trnError(form.taxRegistrationNumber);
  const creditDaysError = creditDaysMessage(form.creditDays);

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
        taxRegistrationNumber: editData.taxRegistrationNumber ?? '',
        tier: editData.tier ?? '',
        // Null credit days means NOT CONFIGURED. It hydrates to blank, never to 0 — "we have not
        // captured this supplier's terms" and "this supplier demands payment on the day" are
        // different facts and the form must not turn the first into the second.
        creditDays: editData.creditDays === null || editData.creditDays === undefined
          ? '' : String(editData.creditDays),
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
    if (nameInvalid || emailInvalid || taxRegistrationError || creditDaysError) return;
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
          {/* Tier and credit days are master data, deliberately not part of the Governance Review
              dialog. A tier is a commercial relationship you choose; approval, compliance and risk
              are verdicts about the supplier. Putting them together would read as one axis and let
              a Tier 3 supplier look non-compliant, or a Tier 1 one look pre-approved. */}
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField
              select fullWidth
              label="Tier"
              value={form.tier}
              onChange={f('tier')}
              helperText="Who you buy from first. Tier orders and pre-selects suppliers for an RFQ; it never blocks one, and it never affects the weighted comparison score."
            >
              <MenuItem value="">Not classified</MenuItem>
              {SUPPLIER_TIERS.map((option) => (
                <MenuItem key={option.value} value={option.value}>{option.label}</MenuItem>
              ))}
            </TextField>
          </Grid>
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField
              fullWidth type="number"
              label="Credit days"
              value={form.creditDays}
              onChange={f('creditDays')}
              error={!!creditDaysError}
              placeholder="e.g. 30"
              slotProps={{ htmlInput: { min: 0, max: 365, step: 1 } }}
              helperText={creditDaysError
                ?? 'The number behind the payment terms above, used when payment terms carry weight in the supplier comparison. Leave blank if you have not agreed terms — blank is not zero.'}
            />
          </Grid>
          <Grid size={{ xs: 12 }}>
            <TextField fullWidth multiline rows={2} label="Comments" value={form.comments} onChange={f('comments')} />
          </Grid>

          <SectionLabel label="Tax" />
          <Grid size={{ xs: 12, sm: 6 }}>
            <TextField
              fullWidth
              label="Tax Registration Number"
              value={form.taxRegistrationNumber}
              onChange={f('taxRegistrationNumber')}
              error={!!taxRegistrationError}
              placeholder="KSA VAT: 15 digits, 3…3"
              helperText={taxRegistrationError
                ?? 'Required before this supplier’s input VAT can be treated as recoverable. Leave blank for an unregistered supplier — its tax will be costed instead.'}
            />
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
        <Button variant="contained" onClick={handleSave} disabled={saveMutation.isPending || nameInvalid || emailInvalid || !!taxRegistrationError || !!creditDaysError} disableElevation sx={{ px: 4, fontWeight: 700 }}>
          {saveMutation.isPending ? <CircularProgress size={22} /> : (isEdit ? 'Update Supplier' : 'Create Supplier')}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default SupplierFormDialog;
