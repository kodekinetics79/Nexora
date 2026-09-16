import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Dialog, DialogActions, DialogContent, DialogTitle, FormControl, InputLabel, MenuItem, Select, Stack, TextField, Typography } from '@mui/material';
import { ManageAccounts as ManageAccountsIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import commercialIntelligenceService, { type AccountOwnershipDTO } from '../../api/services/commercialIntelligenceService';

/**
 * Set or change the account owner of one customer.
 *
 * One dialog, used from the Account ownership list and from the customer's own page, so both
 * take the same two clicks, offer the same people (the server's account-owner options, with
 * capacity) and hit the same endpoint — `POST account-ownership/{customerId}/assign` with the
 * row's version and an idempotency key. The customer page used to say "Account owner —
 * Unassigned" with nothing to press; a fact a manager could only act on from another screen.
 *
 * Mount it with `key={target?.customerId}` so a new target starts from a clean form.
 */
export interface AccountOwnerDialogProps {
  /** The customer being assigned, or null when closed. */
  target: AccountOwnershipDTO | null;
  onClose: () => void;
  /** Called after a successful assignment, before the dialog closes. */
  onAssigned?: (updated: AccountOwnershipDTO) => void;
}

export default function AccountOwnerDialog({ target, onClose, onAssigned }: AccountOwnerDialogProps) {
  const [ownerUserId, setOwnerUserId] = useState<number | ''>(target?.ownerUserId ?? '');
  const [reason, setReason] = useState('');
  const mutationIntent = useRef<{ fingerprint: string; key: string } | null>(null);
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const owners = useQuery({
    queryKey: ['commercial-intelligence', 'account-owner-options'],
    queryFn: commercialIntelligenceService.getAccountOwnerOptions,
    enabled: target !== null,
  });
  const mutation = useMutation({
    mutationFn: () => {
      const normalizedReason = reason.trim();
      const fingerprint = `${target!.customerId}|${ownerUserId}|${target!.version}|${normalizedReason}`;
      if (mutationIntent.current?.fingerprint !== fingerprint) mutationIntent.current = { fingerprint, key: crypto.randomUUID() };
      return commercialIntelligenceService.assignAccount(target!.customerId, Number(ownerUserId), target!.version, normalizedReason || undefined, mutationIntent.current.key);
    },
    onSuccess: (updated) => {
      enqueueSnackbar('Account owner updated', { variant: 'success' });
      mutationIntent.current = null;
      void client.invalidateQueries({ queryKey: ['commercial-intelligence', 'account-ownership'] });
      void client.invalidateQueries({ queryKey: ['customer-account-ownership'] });
      onAssigned?.(updated);
      onClose();
    },
    onError: (error: unknown) => {
      const response = (error as { response?: { status?: number; data?: { error?: string } } })?.response;
      const conflict = response?.status === 409;
      enqueueSnackbar(
        conflict ? 'Ownership changed elsewhere. Refresh before trying again.' : (response?.data?.error || 'The account owner could not be updated'),
        { variant: conflict ? 'warning' : 'error' },
      );
      if (conflict) {
        mutationIntent.current = null;
        void client.invalidateQueries({ queryKey: ['commercial-intelligence', 'account-ownership'] });
        void client.invalidateQueries({ queryKey: ['customer-account-ownership'] });
        onClose();
      }
    },
  });

  const reassignment = !!target?.ownerUserId && target.ownerUserId !== ownerUserId;
  const canSubmit = ownerUserId !== '' && ownerUserId !== target?.ownerUserId && (!reassignment || reason.trim().length >= 5);
  const close = () => {
    if (mutation.isPending) return;
    mutationIntent.current = null;
    onClose();
  };

  return (
    <Dialog open={target !== null} onClose={close} fullWidth maxWidth="sm" aria-labelledby="account-owner-dialog-title">
      <DialogTitle id="account-owner-dialog-title">{target?.ownerUserId ? 'Change account owner' : 'Set account owner'}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Typography variant="body2" color="text.secondary">{target?.customerName}</Typography>
          <FormControl fullWidth>
            <InputLabel id="account-owner-label">Owner</InputLabel>
            <Select labelId="account-owner-label" label="Owner" value={ownerUserId} onChange={(event) => setOwnerUserId(Number(event.target.value))}>
              {(owners.data ?? []).map((owner) => (
                <MenuItem key={owner.userId} value={owner.userId} disabled={!owner.isAvailable}>
                  {owner.name} - {owner.workload.workloadPoints} points, {owner.capacityPercent}% capacity{owner.isAvailable ? '' : ' (at capacity)'}
                </MenuItem>
              ))}
            </Select>
          </FormControl>
          {owners.isError && <Typography variant="body2" color="error">The list of people who can own accounts could not be loaded. Try again shortly.</Typography>}
          {reassignment && (
            <TextField
              label="Reason for the change"
              value={reason}
              onChange={(event) => setReason(event.target.value)}
              required
              error={reason.length > 0 && reason.trim().length < 5}
              helperText="Required when moving an account to someone else; at least 5 characters."
              multiline
              minRows={2}
              slotProps={{ htmlInput: { maxLength: 500 } }}
            />
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={close} disabled={mutation.isPending}>Cancel</Button>
        <Button variant="contained" startIcon={<ManageAccountsIcon />} disabled={!canSubmit || mutation.isPending || owners.isLoading} onClick={() => mutation.mutate()}>
          Confirm owner
        </Button>
      </DialogActions>
    </Dialog>
  );
}
