import React from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack, TextField, Typography,
} from '@mui/material';
import dayjs from 'dayjs';
import { toast } from 'react-hot-toast';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { presentableErrorMessage } from '../../../utils/apiErrors';

interface FollowUpDialogProps {
  open: boolean;
  onClose: () => void;
  quoteId: number;
  quoteNo: string;
}

/** The Reason column on the Follow-ups list is 80 characters wide; the field says so. */
const REASON_MAX = 80;

/**
 * "Follow up on this quote": a due date and a one-line reason, assigned to the person setting it.
 *
 * Until now the only follow-ups in the product were the ones quote delivery created on its own,
 * so a rep who promised to "call Thursday about the price hold" had nowhere to write it down.
 */
const FollowUpDialog: React.FC<FollowUpDialogProps> = ({ open, onClose, quoteId, quoteNo }) => {
  const queryClient = useQueryClient();
  const [dueDate, setDueDate] = React.useState(() => dayjs().add(3, 'day').format('YYYY-MM-DD'));
  const [reason, setReason] = React.useState('');

  React.useEffect(() => {
    if (!open) return;
    setDueDate(dayjs().add(3, 'day').format('YYYY-MM-DD'));
    setReason('');
  }, [open]);

  const trimmed = reason.trim();
  const blocked = !dueDate
    ? 'Pick the day the follow-up is due.'
    : trimmed.length === 0
      ? 'Say what the follow-up is for.'
      : trimmed.length > REASON_MAX
        ? `Keep the reason to ${REASON_MAX} characters.`
        : null;

  const mutation = useMutation({
    mutationFn: () => commercialIntelligenceService.createFollowUp(
      // Midnight UTC on the chosen day: a follow-up is due on a DAY, and the same key with the
      // same content must replay as the same follow-up.
      { quoteId, dueAt: new Date(`${dueDate}T00:00:00Z`).toISOString(), reason: trimmed },
      `quote-follow-up:${quoteId}:${dueDate}:${crypto.randomUUID()}`,
    ),
    onSuccess: (created) => {
      toast.success(`Follow-up set for ${dayjs(created.dueAt).format('DD MMM YYYY')}. It is on your Follow-ups list.`);
      // The Follow-ups list and the sales-today board both read under this prefix.
      queryClient.invalidateQueries({ queryKey: ['commercial-intelligence'] });
      onClose();
    },
    onError: (error: unknown) => toast.error(presentableErrorMessage(error, 'The follow-up was not saved.'), { duration: 6000 }),
  });

  return (
    <Dialog open={open} onClose={mutation.isPending ? undefined : onClose} maxWidth="xs" fullWidth>
      <DialogTitle sx={{ fontWeight: 800 }}>Follow up on {quoteNo}</DialogTitle>
      <DialogContent>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          A reminder assigned to you. It appears on your Follow-ups list until you mark it complete.
        </Typography>
        <Stack spacing={2}>
          <TextField
            label="Due on"
            type="date"
            value={dueDate}
            onChange={(event) => setDueDate(event.target.value)}
            slotProps={{ inputLabel: { shrink: true } }}
            fullWidth
          />
          <TextField
            label="What for"
            placeholder="e.g. Call about the price hold"
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            helperText={`${trimmed.length}/${REASON_MAX} — shown as the reason on your Follow-ups list`}
            error={trimmed.length > REASON_MAX}
            fullWidth
          />
        </Stack>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2, flexDirection: 'column', alignItems: 'stretch', gap: 1 }}>
        <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end' }}>
          <Button onClick={onClose} disabled={mutation.isPending}>Cancel</Button>
          <Button
            variant="contained"
            onClick={() => mutation.mutate()}
            disabled={blocked !== null || mutation.isPending}
            title={blocked ?? undefined}
          >
            {mutation.isPending ? 'Saving…' : 'Set follow-up'}
          </Button>
        </Stack>
        {/* A disabled control that will not say why becomes a support call. */}
        {blocked && <Typography variant="caption" color="text.secondary" sx={{ textAlign: 'right' }}>{blocked}</Typography>}
      </DialogActions>
    </Dialog>
  );
};

export default FollowUpDialog;
