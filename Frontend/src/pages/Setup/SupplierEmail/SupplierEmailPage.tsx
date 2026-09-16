import React, { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Grid,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import supplierEmailSettingsService, {
  SUPPLIER_EMAIL_PLACEHOLDERS,
  SUPPLIER_EMAIL_REPLY_LINE,
  fillSupplierEmailPlaceholders,
  type SupplierEmailCompanySettings,
  type SupplierEmailCompanyUpdate,
  type SupplierEmailField,
  type SupplierEmailTexts,
} from '../../../api/services/supplierEmailSettingsService';
import businessUnitService from '../../../api/services/businessUnitService';
import { useAuth } from '../../../context/AuthContext';
import { handleApiError } from '../../../utils/errorHandler';

export const SAMPLE_SUPPLIER_NAME = 'Gulf Switchgear Trading Co.';
export const SAMPLE_RFQ_NUMBER = 'SRFQ-0001-00000012';
export const SAMPLE_PART_BLOCK = [
  'Line 1: CONTACTOR, 3P, 95A, AC-3, 220V AC COIL',
  '    Part no. LC1D95M7',
  '    Quantity: 16 EA',
  '    Needed by: 2026-11-15',
  'Respond by: Please respond promptly',
].join('\n');

interface FieldSpec {
  key: SupplierEmailField;
  label: string;
  maxLength: number;
  rows?: number;
}

const FIELDS: FieldSpec[] = [
  { key: 'subject', label: 'Subject line', maxLength: 200 },
  { key: 'greeting', label: 'Greeting', maxLength: 200 },
  { key: 'opening', label: 'Opening sentence', maxLength: 1000, rows: 2 },
  { key: 'defaultMessage', label: 'Default message', maxLength: 2000, rows: 3 },
  { key: 'signOff', label: 'Sign-off and signature', maxLength: 1000, rows: 5 },
];

const FIELD_KEYS = FIELDS.map((field) => field.key);

/** What the admin edits: the saved text, or the Nexora default when nothing is saved. */
const formFrom = (settings: SupplierEmailCompanySettings): SupplierEmailTexts => ({
  subject: settings.subject ?? settings.defaults.subject,
  greeting: settings.greeting ?? settings.defaults.greeting,
  opening: settings.opening ?? settings.defaults.opening,
  defaultMessage: settings.defaultMessage ?? settings.defaults.defaultMessage,
  signOff: settings.signOff ?? settings.defaults.signOff,
});

/** A field equal to its default, or left blank, is stored as null so it follows the default. */
const payloadFrom = (form: SupplierEmailTexts, defaults: SupplierEmailTexts): SupplierEmailCompanyUpdate => {
  const payload = {} as SupplierEmailCompanyUpdate;
  for (const key of FIELD_KEYS) {
    const value = form[key];
    payload[key] = value.trim() === '' || value === defaults[key] ? null : value;
  }
  return payload;
};

const SupplierEmailPage: React.FC = () => {
  const { userData, hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const canEdit = hasPermission('Quote Configuration', 'edit');

  const settingsQuery = useQuery({
    queryKey: ['supplier-email-settings', 'company'],
    queryFn: () => supplierEmailSettingsService.getCompany(),
  });

  const businessUnitId = userData?.businessUnitId;
  const businessUnitQuery = useQuery({
    queryKey: ['business-unit', businessUnitId],
    queryFn: () => businessUnitService.getById(Number(businessUnitId)),
    enabled: Boolean(businessUnitId),
    retry: false,
  });
  const companyName = businessUnitQuery.data?.businessUnitName?.trim() || 'Your company';

  const [form, setForm] = useState<SupplierEmailTexts | null>(null);
  const inputRefs = useRef<Partial<Record<SupplierEmailField, HTMLInputElement | HTMLTextAreaElement | null>>>({});
  const lastFocused = useRef<{ key: SupplierEmailField; start: number; end: number }>({
    key: 'defaultMessage', start: -1, end: -1,
  });

  useEffect(() => {
    if (settingsQuery.data) setForm(formFrom(settingsQuery.data));
  }, [settingsQuery.data]);

  const saveMutation = useMutation({
    mutationFn: (body: SupplierEmailCompanyUpdate) => supplierEmailSettingsService.saveCompany(body),
    onSuccess: (saved) => {
      queryClient.setQueryData(['supplier-email-settings', 'company'], saved);
      queryClient.invalidateQueries({ queryKey: ['supplier-email-settings'] });
      enqueueSnackbar('Supplier email saved. New supplier RFQ emails will use this wording.', { variant: 'success' });
    },
    onError: (error: unknown) => handleApiError(error),
  });

  const defaults = settingsQuery.data?.defaults;
  const savedPayload = useMemo(
    () => (settingsQuery.data && defaults ? payloadFrom(formFrom(settingsQuery.data), defaults) : null),
    [settingsQuery.data, defaults],
  );
  const payload = form && defaults ? payloadFrom(form, defaults) : null;
  const changed = Boolean(
    payload && savedPayload && FIELD_KEYS.some((key) => payload[key] !== savedPayload[key]),
  );
  const atDefaults = Boolean(form && defaults && FIELD_KEYS.every((key) => form[key] === defaults[key]));

  if (settingsQuery.isLoading) {
    return <Box sx={{ display: 'flex', justifyContent: 'center', p: 5 }}><CircularProgress aria-label="Loading supplier email" /></Box>;
  }

  if (settingsQuery.isError || !form || !defaults) {
    return (
      <Box sx={{ p: 2 }}>
        <Alert
          severity="error"
          action={<Button color="inherit" size="small" onClick={() => settingsQuery.refetch()}>Try again</Button>}
        >
          The supplier email wording could not be loaded.
        </Alert>
      </Box>
    );
  }

  const setField = (key: SupplierEmailField, value: string) => setForm((current) => (current ? { ...current, [key]: value } : current));

  const rememberCursor = (key: SupplierEmailField) => {
    const input = inputRefs.current[key];
    lastFocused.current = {
      key,
      start: input?.selectionStart ?? -1,
      end: input?.selectionEnd ?? -1,
    };
  };

  const insertPlaceholder = (placeholder: string) => {
    const { key, start, end } = lastFocused.current;
    const current = form[key];
    const from = start < 0 ? current.length : start;
    const to = end < 0 ? current.length : end;
    const next = current.slice(0, from) + placeholder + current.slice(to);
    setField(key, next);
    const caret = from + placeholder.length;
    lastFocused.current = { key, start: caret, end: caret };
    requestAnimationFrame(() => {
      const input = inputRefs.current[key];
      if (input) {
        input.focus();
        input.setSelectionRange(caret, caret);
      }
    });
  };

  const fill = (text: string) =>
    fillSupplierEmailPlaceholders(text, {
      supplierName: SAMPLE_SUPPLIER_NAME,
      companyName,
      rfqNumber: SAMPLE_RFQ_NUMBER,
    });

  const subjectForPreview = form.subject.trim() === ''
    ? defaults.subject
    : form.subject.includes('[RFQ number]') ? form.subject : `${form.subject} ([RFQ number])`;
  const textFor = (key: SupplierEmailField) => (form[key].trim() === '' ? defaults[key] : form[key]);

  const saveReason = !canEdit
    ? null
    : saveMutation.isPending
      ? 'Saving…'
      : !changed
        ? 'Nothing to save yet — change the wording above first.'
        : null;

  const updatedOn = settingsQuery.data?.updatedOn;
  const updatedBy = settingsQuery.data?.updatedBy;

  return (
    <Box sx={{ width: '100%', px: 1, py: 1 }}>
      <Box sx={{ mb: 2 }}>
        <Typography variant="h5" component="h1" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>
          Supplier Email
        </Typography>
        <Typography variant="body2" color="text.secondary">
          The wording your team&apos;s RFQ emails to suppliers use, from the subject line to the signature.
        </Typography>
        {updatedOn && (
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
            Last changed {new Date(updatedOn).toLocaleString()}{updatedBy ? ` by ${updatedBy}` : ''}
          </Typography>
        )}
      </Box>

      {!canEdit && (
        <Alert severity="info" sx={{ mb: 2 }}>
          Only people allowed to change the quote format can change this wording. You can still set your own
          message and signature from your account menu, under My supplier email.
        </Alert>
      )}

      <Grid container spacing={3}>
        <Grid size={{ xs: 12, md: 7 }}>
          <Paper sx={{ p: 2.5, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
            <Stack spacing={2.5}>
              {FIELDS.map((field) => (
                <TextField
                  key={field.key}
                  fullWidth
                  label={field.label}
                  value={form[field.key]}
                  disabled={!canEdit}
                  multiline={Boolean(field.rows)}
                  minRows={field.rows}
                  inputRef={(element: HTMLInputElement | HTMLTextAreaElement | null) => {
                    inputRefs.current[field.key] = element;
                  }}
                  onChange={(event) => {
                    setField(field.key, event.target.value);
                    lastFocused.current = {
                      key: field.key,
                      start: event.target.selectionStart ?? -1,
                      end: event.target.selectionEnd ?? -1,
                    };
                  }}
                  onFocus={() => rememberCursor(field.key)}
                  onSelect={() => rememberCursor(field.key)}
                  onKeyUp={() => rememberCursor(field.key)}
                  onClick={() => rememberCursor(field.key)}
                  helperText={
                    field.key === 'subject' && form.subject.trim() !== '' && !form.subject.includes('[RFQ number]')
                      ? 'The RFQ number is added to the end of the subject so replies can be matched.'
                      : form[field.key].trim() === ''
                        ? 'Left blank, the Nexora default is used.'
                        : undefined
                  }
                  slotProps={{ htmlInput: { maxLength: field.maxLength } }}
                />
              ))}

              <Box>
                <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
                  You can use [Supplier name], [Company name] and [RFQ number]; they are filled in for each email.
                </Typography>
                {canEdit && (
                  <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap' }}>
                    {SUPPLIER_EMAIL_PLACEHOLDERS.map((placeholder) => (
                      <Chip
                        key={placeholder}
                        label={`Insert ${placeholder}`}
                        size="small"
                        variant="outlined"
                        onMouseDown={(event) => event.preventDefault()}
                        onClick={() => insertPlaceholder(placeholder)}
                      />
                    ))}
                  </Stack>
                )}
              </Box>

              <Box
                role="note"
                aria-label="The part details"
                sx={{ p: 2, borderRadius: 2, bgcolor: 'action.hover', border: '1px dashed', borderColor: 'divider' }}
              >
                <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 0.5 }}>The part details</Typography>
                <Typography variant="body2" color="text.secondary">
                  Nexora adds the part lines (description, part number, makers, quantity and needed-by date) between
                  the opening sentence and the message, and they cannot be changed here.
                </Typography>
              </Box>

              {canEdit && (
                <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ alignItems: { sm: 'center' } }}>
                  <Button
                    variant="contained"
                    onClick={() => payload && saveMutation.mutate(payload)}
                    disabled={Boolean(saveReason)}
                    aria-describedby={saveReason ? 'supplier-email-save-reason' : undefined}
                  >
                    {saveMutation.isPending ? 'Saving…' : 'Save'}
                  </Button>
                  <Button
                    variant="outlined"
                    onClick={() => setForm({ ...defaults })}
                    disabled={atDefaults || saveMutation.isPending}
                  >
                    Reset to Nexora defaults
                  </Button>
                  {saveReason && (
                    <Typography id="supplier-email-save-reason" variant="caption" color="text.secondary">
                      {saveReason}
                    </Typography>
                  )}
                </Stack>
              )}
            </Stack>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, md: 5 }}>
          <Paper
            component="section"
            aria-label="Email preview"
            sx={{ p: 2.5, borderRadius: 2, border: '1px solid', borderColor: 'divider', position: { md: 'sticky' }, top: { md: 16 } }}
          >
            <Typography variant="overline" color="text.secondary">Preview</Typography>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1.5 }}>
              How a supplier sees it, with sample details filled in.
            </Typography>
            <Box data-testid="supplier-email-preview" sx={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word', fontSize: 14 }}>
              <Typography sx={{ fontWeight: 700, mb: 1.5 }} data-testid="preview-subject">{fill(subjectForPreview)}</Typography>
              <Typography variant="body2" sx={{ mb: 1.5 }}>{fill(textFor('greeting'))}</Typography>
              <Typography variant="body2" sx={{ mb: 1.5 }}>{fill(textFor('opening'))}</Typography>
              <Box
                component="pre"
                sx={{ m: 0, mb: 1.5, p: 1.5, borderRadius: 1, bgcolor: 'action.hover', fontFamily: 'inherit', fontSize: 13, whiteSpace: 'pre-wrap' }}
              >
                {SAMPLE_PART_BLOCK}
              </Box>
              <Typography variant="body2" sx={{ mb: 1.5 }}>{fill(textFor('defaultMessage'))}</Typography>
              <Typography variant="body2" sx={{ mb: 1.5 }}>{SUPPLIER_EMAIL_REPLY_LINE}</Typography>
              <Typography variant="body2">{fill(textFor('signOff'))}</Typography>
            </Box>
          </Paper>
        </Grid>
      </Grid>
    </Box>
  );
};

export default SupplierEmailPage;
