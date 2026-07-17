import React from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Grid, TextField,
  CircularProgress, Stack, InputAdornment, Divider,
} from '@mui/material';
import { NotificationsActive as AlertIcon, Save as SaveIcon } from '@mui/icons-material';
import { toast } from 'react-hot-toast';
import slaService, { type SlaPolicyDTO } from '../../../api/services/slaService';

type FormState = Omit<SlaPolicyDTO, 'businessUnitId'>;

interface FieldSpec {
  key: keyof FormState;
  label: string;
  help: string;
  unit: 'days' | 'hours';
}

// Plain-language labels — each maps 1:1 to a policy column.
const FIELDS: { section: string; items: FieldSpec[] }[] = [
  {
    section: 'Bid deadlines',
    items: [
      { key: 'warnDaysBeforeClose', label: 'Alert me before a bid closes', help: 'First reminder to the assigned owner, this many days before the bid closing date.', unit: 'days' },
      { key: 'criticalDaysBeforeClose', label: 'Send a final warning before a bid closes', help: 'Last-chance reminder shortly before the deadline. Must not be later than the first alert.', unit: 'days' },
      { key: 'deadlineBufferHours', label: 'Internal safety buffer before a deadline', help: 'How much earlier than the customer deadline your team aims to be done (reserved for upcoming features).', unit: 'hours' },
    ],
  },
  {
    section: 'New leads',
    items: [
      { key: 'unassignedHours', label: 'Tell managers when a lead has no owner', help: 'If an accepted lead sits unassigned longer than this, managers are notified.', unit: 'hours' },
    ],
  },
  {
    section: 'Quotes',
    items: [
      { key: 'staleQuoteDays', label: 'Remind me when a quote gets no reply', help: 'If a sent quote has no customer response after this many days, the owner gets a daily summary.', unit: 'days' },
      { key: 'quoteAutoExpireDays', label: 'Automatically expire old quotes', help: 'Sent quotes past their validity (or send date) by this many days are closed as "Expired" automatically.', unit: 'days' },
    ],
  },
  {
    section: 'Copilot approvals',
    items: [
      { key: 'approvalEscalationHours', label: 'Escalate approvals waiting too long', help: 'If a copilot action waits for approval longer than this, managers are alerted.', unit: 'hours' },
    ],
  },
];

const SlaSettingsPage: React.FC = () => {
  const queryClient = useQueryClient();
  const [form, setForm] = React.useState<FormState | null>(null);

  const { data: policy, isLoading } = useQuery({
    queryKey: ['sla-policy'],
    queryFn: () => slaService.getPolicy(),
  });

  React.useEffect(() => {
    if (policy) {
      const { businessUnitId: _bu, ...rest } = policy;
      setForm(rest);
    }
  }, [policy]);

  const saveMutation = useMutation({
    mutationFn: (update: FormState) => slaService.updatePolicy(update),
    onSuccess: () => {
      toast.success('Deadline and alert settings saved');
      queryClient.invalidateQueries({ queryKey: ['sla-policy'] });
    },
    onError: (error: any) => {
      toast.error(error?.response?.data || 'Failed to save settings');
    },
  });

  const setField = (key: keyof FormState, raw: string) => {
    if (!form) return;
    const value = raw === '' ? 0 : Number(raw);
    if (Number.isNaN(value) || value < 0) return;
    setForm({ ...form, [key]: Math.floor(value) });
  };

  const criticalAfterWarn = !!form && form.criticalDaysBeforeClose > form.warnDaysBeforeClose;

  if (isLoading || !form) {
    return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  }

  return (
    <Box sx={{ p: 3, maxWidth: 900, mx: 'auto' }}>
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 0.5 }}>
        <AlertIcon color="primary" />
        <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>
          Deadlines &amp; Alerts
        </Typography>
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
        Decide when Nexora should remind your team about bids, unassigned leads and quotes that need a follow-up.
        These settings apply to your whole business unit.
      </Typography>

      <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider' }}>
        {FIELDS.map((section, sIdx) => (
          <Box key={section.section}>
            {sIdx > 0 && <Divider sx={{ my: 3 }} />}
            <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', textTransform: 'uppercase', letterSpacing: '0.03em', color: 'text.secondary', mb: 2 }}>
              {section.section}
            </Typography>
            <Grid container spacing={2.5}>
              {section.items.map((field) => (
                <Grid size={{ xs: 12, md: 6 }} key={field.key}>
                  <TextField
                    fullWidth
                    type="number"
                    size="small"
                    label={field.label}
                    value={form[field.key]}
                    onChange={(e) => setField(field.key, e.target.value)}
                    helperText={field.key === 'criticalDaysBeforeClose' && criticalAfterWarn
                      ? 'The final warning must be at or after the first alert.'
                      : field.help}
                    error={field.key === 'criticalDaysBeforeClose' && criticalAfterWarn}
                    slotProps={{
                      input: {
                        endAdornment: <InputAdornment position="end">{field.unit}</InputAdornment>,
                      },
                      htmlInput: { min: 0, step: 1 },
                    }}
                  />
                </Grid>
              ))}
            </Grid>
          </Box>
        ))}

        <Stack direction="row" sx={{ justifyContent: 'flex-end', mt: 3 }}>
          <Button
            variant="contained"
            startIcon={saveMutation.isPending ? <CircularProgress size={18} color="inherit" /> : <SaveIcon />}
            disabled={saveMutation.isPending || criticalAfterWarn}
            onClick={() => saveMutation.mutate(form)}
            sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
          >
            Save settings
          </Button>
        </Stack>
      </Paper>
    </Box>
  );
};

export default SlaSettingsPage;
