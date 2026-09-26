import React, { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import supplierEmailSettingsService, {
  SUPPLIER_EMAIL_REPLY_LINE,
  type SupplierEmailMineUpdate,
} from '../../api/services/supplierEmailSettingsService';
import { handleApiError } from '../../utils/errorHandler';

interface MySupplierEmailDialogProps {
  open: boolean;
  onClose: () => void;
}

type OwnField = keyof SupplierEmailMineUpdate;

const blankToNull = (value: string): string | null => (value.trim() === '' ? null : value);

/**
 * A salesperson's own closing for the RFQ emails they send to suppliers. An empty field means the
 * company's wording is used, which is what "Use company's" does.
 */
const MySupplierEmailDialog: React.FC<MySupplierEmailDialogProps> = ({ open, onClose }) => {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const mineQuery = useQuery({
    queryKey: ['supplier-email-settings', 'mine'],
    queryFn: () => supplierEmailSettingsService.getMine(),
    enabled: open,
  });

  const [form, setForm] = useState<Record<OwnField, string>>({ defaultMessage: '', signOff: '' });

  useEffect(() => {
    if (open && mineQuery.data) {
      setForm({
        defaultMessage: mineQuery.data.defaultMessage ?? '',
        signOff: mineQuery.data.signOff ?? '',
      });
    }
  }, [open, mineQuery.data]);

  const saveMutation = useMutation({
    mutationFn: (body: SupplierEmailMineUpdate) => supplierEmailSettingsService.saveMine(body),
    onSuccess: (saved) => {
      queryClient.setQueryData(['supplier-email-settings', 'mine'], saved);
      queryClient.invalidateQueries({ queryKey: ['supplier-email-settings'] });
      enqueueSnackbar('Saved. Your next supplier emails will use this message and signature.', { variant: 'success' });
      onClose();
    },
    onError: (error: unknown) => handleApiError(error),
  });

  const data = mineQuery.data;
  const company = data?.company;
  const changed = Boolean(
    data &&
      (blankToNull(form.defaultMessage) !== (data.defaultMessage ?? null) ||
        blankToNull(form.signOff) !== (data.signOff ?? null)),
  );

  const effective = (key: OwnField) => (form[key].trim() === '' ? company?.[key] ?? '' : form[key]);

  const fields: { key: OwnField; label: string; rows: number; maxLength: number }[] = [
    { key: 'defaultMessage', label: 'My default message', rows: 3, maxLength: 2000 },
    { key: 'signOff', label: 'My signature', rows: 4, maxLength: 1000 },
  ];

  const saveReason = saveMutation.isPending
    ? 'Saving…'
    : !changed
      ? 'Nothing to save yet.'
      : null;

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" aria-labelledby="my-supplier-email-title">
      <DialogTitle id="my-supplier-email-title">My supplier email</DialogTitle>
      <DialogContent>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Your own message and signature for the RFQ emails you send to suppliers. Leave a box empty to use your
          company&apos;s.
        </Typography>

        {mineQuery.isLoading && (
          <Box sx={{ display: 'flex', justifyContent: 'center', p: 3 }}>
            <CircularProgress aria-label="Loading your supplier email" />
          </Box>
        )}

        {mineQuery.isError && (
          <Alert
            severity="error"
            action={<Button color="inherit" size="small" onClick={() => mineQuery.refetch()}>Try again</Button>}
          >
            Your supplier email could not be loaded.
          </Alert>
        )}

        {data && (
          <Stack spacing={2}>
            {fields.map((field) => (
              <Box key={field.key}>
                <TextField
                  fullWidth
                  multiline
                  minRows={field.rows}
                  label={field.label}
                  value={form[field.key]}
                  onChange={(event) => setForm((current) => ({ ...current, [field.key]: event.target.value }))}
                  helperText={form[field.key].trim() === '' ? `Your company's: ${company?.[field.key] ?? ''}` : undefined}
                  slotProps={{
                    htmlInput: { maxLength: field.maxLength },
                    formHelperText: { sx: { whiteSpace: 'pre-wrap' } },
                  }}
                />
                <Button
                  size="small"
                  onClick={() => setForm((current) => ({ ...current, [field.key]: '' }))}
                  disabled={form[field.key] === ''}
                  sx={{ mt: 0.5 }}
                >
                  Use company&apos;s
                </Button>
              </Box>
            ))}

            <Box
              component="section"
              aria-label="How your email ends"
              sx={{ p: 2, borderRadius: 2, bgcolor: 'action.hover', whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}
            >
              <Typography variant="overline" color="text.secondary">How your email ends</Typography>
              <Typography variant="body2" sx={{ mb: 1 }}>{effective('defaultMessage')}</Typography>
              <Typography variant="body2" sx={{ mb: 1 }}>{SUPPLIER_EMAIL_REPLY_LINE}</Typography>
              <Typography variant="body2">{effective('signOff')}</Typography>
            </Box>
          </Stack>
        )}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        {data && saveReason && (
          <Typography id="my-supplier-email-save-reason" variant="caption" color="text.secondary" sx={{ mr: 'auto' }}>
            {saveReason}
          </Typography>
        )}
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!data || Boolean(saveReason)}
          aria-describedby={saveReason ? 'my-supplier-email-save-reason' : undefined}
          onClick={() =>
            saveMutation.mutate({
              defaultMessage: blankToNull(form.defaultMessage),
              signOff: blankToNull(form.signOff),
            })
          }
        >
          Save
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default MySupplierEmailDialog;
