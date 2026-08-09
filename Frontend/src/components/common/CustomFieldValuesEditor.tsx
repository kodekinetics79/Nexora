import React, { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Checkbox, Chip, CircularProgress, Divider, FormControlLabel,
  MenuItem, Stack, TextField, Typography, Button,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import customFieldService, {
  type CustomFieldBagItem,
} from '../../api/services/customFieldService';
import { presentableErrorMessage } from '../../utils/apiErrors';

/**
 * AA-01 · value editor for tenant-defined custom fields on one record.
 *
 * Drops into any record form. It renders whatever the tenant has defined for that entity —
 * nothing here knows the names of any particular field.
 *
 * Validation is the server's. The inputs below constrain the obvious (a number input for a
 * number field), but the save is what decides, and the server's message is shown verbatim
 * rather than being reworded into something vaguer. A rule enforced only in the browser is
 * not a rule.
 */

export interface CustomFieldValuesEditorProps {
  /** Canonical entity type: Customer, Supplier, LeadItem. */
  entityType: string;
  /** Persisted record id. Null while a record is still being created — the editor hides. */
  entityId: number | null;
  canEdit: boolean;
  /** Heading shown above the fields. */
  title?: string;
}

/** Converts an input's string state into the JSON shape the declared type expects. */
const toJsonValue = (field: CustomFieldBagItem, raw: string | boolean | string[]): unknown => {
  if (typeof raw === 'boolean') return raw;
  if (Array.isArray(raw)) return raw;
  const text = raw.trim();
  if (text === '') return null; // clears the value
  switch (field.dataType) {
    case 'Integer': {
      const parsed = Number(text);
      return Number.isInteger(parsed) ? parsed : text; // let the server reject a bad number
    }
    case 'Decimal': {
      const parsed = Number(text);
      return Number.isFinite(parsed) ? parsed : text;
    }
    default:
      return text;
  }
};

/** Renders a stored value back into the string an input holds. */
const toInputValue = (field: CustomFieldBagItem): string | boolean | string[] => {
  const value = field.value;
  if (field.dataType === 'Boolean') return value === true;
  if (field.dataType === 'MultiOption') return Array.isArray(value) ? value.map(String) : [];
  if (value === null || value === undefined) return '';
  return String(value);
};

const CustomFieldValuesEditor: React.FC<CustomFieldValuesEditorProps> = ({
  entityType, entityId, canEdit, title = 'Your organisation’s fields',
}) => {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [values, setValues] = useState<Record<string, string | boolean | string[]>>({});
  const [dirty, setDirty] = useState(false);

  const queryKey = useMemo(
    () => ['custom-field-record', entityType, entityId],
    [entityType, entityId],
  );

  const { data, isPending, isError, refetch } = useQuery({
    queryKey,
    queryFn: () => customFieldService.getRecordFields(entityType, entityId!),
    enabled: entityId != null && entityId > 0,
    retry: false,
  });

  const fields = useMemo(() => data?.fields ?? [], [data]);

  useEffect(() => {
    if (!data) return;
    const next: Record<string, string | boolean | string[]> = {};
    for (const field of data.fields) next[field.stableKey] = toInputValue(field);
    setValues(next);
    setDirty(false);
  }, [data]);

  const saveMutation = useMutation({
    mutationFn: () => {
      const payload: Record<string, unknown> = {};
      for (const field of fields) {
        payload[field.stableKey] = toJsonValue(field, values[field.stableKey] ?? '');
      }
      return customFieldService.updateRecordFields(entityType, entityId!, payload);
    },
    onSuccess: (response) => {
      queryClient.setQueryData(queryKey, response);
      setDirty(false);
      enqueueSnackbar('Saved.', { variant: 'success' });
    },
    // The server's wording is shown as-is: it names the field and says exactly what was
    // wrong with the value. Replacing it with "Invalid input" would destroy that.
    onError: (error: unknown) =>
      enqueueSnackbar(presentableErrorMessage(error, 'The values could not be saved.'), { variant: 'error' }),
  });

  if (entityId == null || entityId <= 0) return null;
  if (isPending) {
    return (
      <Box sx={{ py: 2, display: 'flex', justifyContent: 'center' }}>
        <CircularProgress size={20} />
      </Box>
    );
  }
  if (isError) {
    return (
      <Alert
        severity="warning"
        sx={{ my: 2 }}
        action={<Button color="inherit" size="small" onClick={() => void refetch()}>Retry</Button>}
      >
        Your organisation’s own fields could not be loaded for this record. Everything else on
        this form is unaffected.
      </Alert>
    );
  }
  if (fields.length === 0) return null;

  const set = (key: string, value: string | boolean | string[]) => {
    setValues((p) => ({ ...p, [key]: value }));
    setDirty(true);
  };

  return (
    <Box sx={{ mt: 2 }}>
      <Divider sx={{ mb: 2 }}>
        <Typography variant="caption" sx={{ fontWeight: 800, letterSpacing: '0.06em', textTransform: 'uppercase', color: 'text.secondary' }}>
          {title}
        </Typography>
      </Divider>

      <Stack spacing={2}>
        {fields.map((field) => {
          const value = values[field.stableKey];
          const disabled = !canEdit || saveMutation.isPending;
          const common = {
            size: 'small' as const,
            fullWidth: true,
            label: field.label,
            required: field.isRequired,
            disabled,
          };

          if (field.dataType === 'Boolean') {
            return (
              <FormControlLabel
                key={field.stableKey}
                control={
                  <Checkbox
                    size="small"
                    checked={value === true}
                    disabled={disabled}
                    onChange={(e) => set(field.stableKey, e.target.checked)}
                    slotProps={{ input: { 'aria-label': field.label } }}
                  />
                }
                label={
                  <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center' }}>
                    <Typography variant="body2">{field.label}</Typography>
                    {field.requiresManagerAccess && (
                      <Chip label="Manager only" size="small" variant="outlined" sx={{ height: 18, fontSize: '0.6rem' }} />
                    )}
                  </Stack>
                }
              />
            );
          }

          if (field.dataType === 'Option') {
            return (
              <TextField
                key={field.stableKey} {...common} select
                value={typeof value === 'string' ? value : ''}
                onChange={(e) => set(field.stableKey, e.target.value)}
              >
                <MenuItem value=""><em>Not set</em></MenuItem>
                {field.options.map((option) => (
                  <MenuItem key={option.stableKey} value={option.stableKey}>{option.label}</MenuItem>
                ))}
              </TextField>
            );
          }

          if (field.dataType === 'MultiOption') {
            const selected = Array.isArray(value) ? value : [];
            return (
              <TextField
                key={field.stableKey} {...common} select
                slotProps={{ select: { multiple: true } }}
                value={selected}
                onChange={(e) => {
                  const raw = e.target.value as unknown as string[] | string;
                  set(field.stableKey, Array.isArray(raw) ? raw : [raw]);
                }}
              >
                {field.options.map((option) => (
                  <MenuItem key={option.stableKey} value={option.stableKey}>{option.label}</MenuItem>
                ))}
              </TextField>
            );
          }

          return (
            <TextField
              key={field.stableKey} {...common}
              type={field.dataType === 'Date' ? 'date'
                : field.dataType === 'Integer' || field.dataType === 'Decimal' ? 'number' : 'text'}
              slotProps={field.dataType === 'Date' ? { inputLabel: { shrink: true } } : undefined}
              value={typeof value === 'string' ? value : ''}
              onChange={(e) => set(field.stableKey, e.target.value)}
            />
          );
        })}
      </Stack>

      {canEdit && (
        <Box sx={{ display: 'flex', justifyContent: 'flex-end', mt: 2 }}>
          <Button
            size="small"
            variant="outlined"
            disabled={!dirty || saveMutation.isPending}
            startIcon={saveMutation.isPending ? <CircularProgress size={14} /> : undefined}
            onClick={() => saveMutation.mutate()}
          >
            Save these fields
          </Button>
        </Box>
      )}
    </Box>
  );
};

export default CustomFieldValuesEditor;
