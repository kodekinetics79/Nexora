import React from 'react';
import { Button, CircularProgress, Menu, MenuItem, Tooltip } from '@mui/material';
import { AccountTree as LifecycleIcon } from '@mui/icons-material';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSnackbar } from 'notistack';
import lifecycleService, { type LifecycleAggregate, type LifecycleTransitionOption } from '../../api/services/commercialLifecycleService';
import { presentableErrorMessage } from '../../utils/apiErrors';

interface Props { aggregate: LifecycleAggregate; id: number; onChanged?: () => void; }

const LifecycleActions: React.FC<Props> = ({ aggregate, id, onChanged }) => {
  const [anchor, setAnchor] = React.useState<HTMLElement | null>(null);
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const stateQuery = useQuery({
    queryKey: ['lifecycle', aggregate, id],
    queryFn: () => lifecycleService.getState(aggregate, id),
  });
  const mutation = useMutation({
    mutationFn: async (option: LifecycleTransitionOption) => {
      const state = stateQuery.data!;
      const reason = option.requiresReason ? window.prompt(`Reason code for ${option.label}`)?.trim() : undefined;
      if (option.requiresReason && !reason) throw new Error('A reason code is required.');
      return lifecycleService.transition(aggregate, id, state, option, reason);
    },
    onSuccess: async () => {
      enqueueSnackbar('Lifecycle updated', { variant: 'success' });
      await queryClient.invalidateQueries({ queryKey: ['lifecycle', aggregate, id] });
      onChanged?.();
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'The lifecycle update could not be applied. The record is unchanged — try again.'),
      { variant: 'error' },
    ),
  });
  const state = stateQuery.data;
  return <>
    <Tooltip title={state ? `Current state: ${state.currentStatusCode}` : 'Lifecycle'}>
      <span>
        <Button size="small" variant="outlined" startIcon={mutation.isPending || stateQuery.isLoading ? <CircularProgress size={16} /> : <LifecycleIcon />}
          disabled={!state || state.allowedTransitions.length === 0 || mutation.isPending}
          onClick={(event) => setAnchor(event.currentTarget)}>
          {state?.currentStatusCode.replaceAll('_', ' ') || 'Lifecycle'}
        </Button>
      </span>
    </Tooltip>
    <Menu anchorEl={anchor} open={Boolean(anchor)} onClose={() => setAnchor(null)}>
      {state?.allowedTransitions.filter(option => aggregate !== 'leads' || option.statusCode !== 'CONVERTED_TO_RFQ').map((option) => <MenuItem key={option.statusCode} onClick={() => { setAnchor(null); mutation.mutate(option); }}>
        Move to {option.label}
      </MenuItem>)}
    </Menu>
  </>;
};

export default LifecycleActions;
