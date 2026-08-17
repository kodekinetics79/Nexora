import React from 'react';
import { Button, CircularProgress, Menu, MenuItem, Tooltip } from '@mui/material';
import { AccountTree as LifecycleIcon, ArrowDropDown as ArrowDropDownIcon } from '@mui/icons-material';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSnackbar } from 'notistack';
import lifecycleService, { isLeadLoss, type LifecycleAggregate, type LifecycleTransitionOption } from '../../api/services/commercialLifecycleService';
import LeadOutcomeDialog, { type LeadOutcomeCapture } from './LeadOutcomeDialog';
import { presentableErrorMessage } from '../../utils/apiErrors';

interface Props { aggregate: LifecycleAggregate; id: number; onChanged?: () => void; }

const LifecycleActions: React.FC<Props> = ({ aggregate, id, onChanged }) => {
  const [anchor, setAnchor] = React.useState<HTMLElement | null>(null);
  // The transition waiting on a captured outcome (lead lost / abandoned before any quotation).
  const [pendingLoss, setPendingLoss] = React.useState<LifecycleTransitionOption | null>(null);
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const stateQuery = useQuery({
    queryKey: ['lifecycle', aggregate, id],
    queryFn: () => lifecycleService.getState(aggregate, id),
  });
  const mutation = useMutation({
    mutationFn: async ({ option, capture }: { option: LifecycleTransitionOption; capture?: LeadOutcomeCapture }) => {
      const state = stateQuery.data!;
      if (capture) return lifecycleService.transition(aggregate, id, state, option, capture.reasonCode, capture.reasonNotes);
      const reason = option.requiresReason ? window.prompt(`Reason code for ${option.label}`)?.trim() : undefined;
      if (option.requiresReason && !reason) throw new Error('A reason code is required.');
      return lifecycleService.transition(aggregate, id, state, option, reason);
    },
    onSuccess: async () => {
      enqueueSnackbar('Lifecycle updated', { variant: 'success' });
      setPendingLoss(null);
      await queryClient.invalidateQueries({ queryKey: ['lifecycle', aggregate, id] });
      onChanged?.();
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'The lifecycle update could not be applied. The record is unchanged — try again.'),
      { variant: 'error' },
    ),
  });
  const state = stateQuery.data;
  const select = (option: LifecycleTransitionOption) => {
    setAnchor(null);
    // A lead ending before a quotation must record WHY, from the governed picklist.
    if (isLeadLoss(aggregate, option.statusCode)) setPendingLoss(option);
    else mutation.mutate({ option });
  };
  return <>
    {/* THIS IS A BUTTON, AND IT HAS TO LOOK LIKE ONE.
        Rendering the current status as the label makes it read as a status badge — users
        reported "I don't see any option to qualify" while looking straight at it. The label
        now leads with the verb and the caret advertises the menu, so the one control that
        advances a lead is not mistaken for decoration. */}
    <Tooltip title={
      state
        ? state.allowedTransitions.length > 0
          ? `Currently ${state.currentStatusCode.replaceAll('_', ' ')} — click to advance`
          : `Currently ${state.currentStatusCode.replaceAll('_', ' ')} — no moves available`
        : 'Lifecycle'
    }>
      <span>
        <Button size="small" variant="outlined"
          startIcon={mutation.isPending || stateQuery.isLoading ? <CircularProgress size={16} /> : <LifecycleIcon />}
          endIcon={state && state.allowedTransitions.length > 0 ? <ArrowDropDownIcon /> : undefined}
          disabled={!state || state.allowedTransitions.length === 0 || mutation.isPending}
          onClick={(event) => setAnchor(event.currentTarget)}>
          {state ? `Advance · ${state.currentStatusCode.replaceAll('_', ' ')}` : 'Lifecycle'}
        </Button>
      </span>
    </Tooltip>
    <Menu anchorEl={anchor} open={Boolean(anchor)} onClose={() => setAnchor(null)}>
      {state?.allowedTransitions.filter(option => aggregate !== 'leads' || option.statusCode !== 'CONVERTED_TO_RFQ').map((option) => <MenuItem key={option.statusCode} onClick={() => select(option)}>
        Move to {option.label}
      </MenuItem>)}
    </Menu>
    <LeadOutcomeDialog
      open={Boolean(pendingLoss)}
      targetLabel={pendingLoss?.label ?? ''}
      saving={mutation.isPending}
      onCancel={() => setPendingLoss(null)}
      onConfirm={(capture) => mutation.mutate({ option: pendingLoss!, capture })}
    />
  </>;
};

export default LifecycleActions;
