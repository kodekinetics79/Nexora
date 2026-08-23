import React from 'react';
import { Autocomplete, Box, Button, Chip, CircularProgress, Dialog, DialogContent, DialogTitle, Menu, MenuItem, Stack, TextField, Typography } from '@mui/material';
import { ExpandMore as ExpandMoreIcon } from '@mui/icons-material';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'react-hot-toast';
import commercialRoutingService, {
  LEAD_OWNERSHIP_ACTION,
} from '../../api/services/commercialRoutingService';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { useAuth } from '../../context/AuthContext';

interface Props {
  leadId: number;
  assignedToId?: number | null;
  assignedToName?: string | null;
  assignmentMethod?: 'AUTOMATIC' | 'MANUAL';
  assignmentVersion: number;
  canEdit: boolean;
}

const mutationIdentity = (leadId: number): string =>
  `lead-owner-${leadId}-${Date.now()}-${crypto.randomUUID()}`;

const LeadOwnerControl: React.FC<Props> = ({
  leadId, assignedToId, assignedToName, assignmentMethod = 'AUTOMATIC', assignmentVersion, canEdit,
}) => {
  const queryClient = useQueryClient();
  const { userData } = useAuth();
  const [anchorEl, setAnchorEl] = React.useState<HTMLElement | null>(null);
  const [selectorOpen, setSelectorOpen] = React.useState(false);
  const owners = useQuery({
    queryKey: ['lead-owner-options'],
    queryFn: commercialRoutingService.getOwnerOptions,
    enabled: canEdit && selectorOpen,
    staleTime: 60_000,
  });

  const mutation = useMutation({
    mutationFn: ({ action, ownerId }: { action: number; ownerId?: number }) => {
      const identity = mutationIdentity(leadId);
      return commercialRoutingService.changeLeadOwner(leadId, {
        action: action as 0 | 1 | 2,
        assignedToUserId: ownerId,
        expectedAssignmentVersion: assignmentVersion,
        idempotencyKey: identity,
        correlationId: identity,
      });
    },
    onSuccess: async () => {
      setAnchorEl(null);
      setSelectorOpen(false);
      toast.success('Lead owner updated.');
      await queryClient.invalidateQueries({ queryKey: ['lead-detail', leadId] });
    },
    onError: (error: unknown) => toast.error(
      presentableErrorMessage(error, 'The owner changed elsewhere. Refresh the lead and try again.'),
    ),
  });

  const ownerLabel = assignedToName?.trim() || 'Unassigned';
  return (
    <Box>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', fontWeight: 800, mb: 0.5 }}>
        Owner
      </Typography>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
        <Button
          size="small"
          variant="outlined"
          endIcon={mutation.isPending ? <CircularProgress size={14} /> : <ExpandMoreIcon />}
          disabled={!canEdit || mutation.isPending}
          onClick={(event) => setAnchorEl(event.currentTarget)}
          sx={{ borderRadius: 2, fontWeight: 800, textTransform: 'none' }}
        >
          {ownerLabel}
        </Button>
        <Chip
          size="small"
          label={assignmentMethod === 'MANUAL' ? 'Manual' : 'Automatic'}
          color={assignmentMethod === 'MANUAL' ? 'primary' : 'default'}
          variant="outlined"
        />
      </Stack>
      <Menu anchorEl={anchorEl} open={Boolean(anchorEl)} onClose={() => setAnchorEl(null)}>
        {userData.id && assignedToId !== userData.id ? (
          <MenuItem onClick={() => mutation.mutate({ action: LEAD_OWNERSHIP_ACTION.Assign, ownerId: userData.id })}>
            Assign to me
          </MenuItem>
        ) : null}
        <MenuItem onClick={() => { setAnchorEl(null); setSelectorOpen(true); }}>Assign to…</MenuItem>
        {assignedToId ? (
          <MenuItem onClick={() => mutation.mutate({ action: LEAD_OWNERSHIP_ACTION.Unassign })}>Unassign</MenuItem>
        ) : null}
        {assignmentMethod === 'MANUAL' ? (
          <MenuItem onClick={() => mutation.mutate({ action: LEAD_OWNERSHIP_ACTION.ReturnToAutomatic })}>
            Return to automatic routing
          </MenuItem>
        ) : null}
      </Menu>
      <Dialog open={selectorOpen} onClose={() => setSelectorOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 900 }}>Assign lead owner</DialogTitle>
        <DialogContent sx={{ pt: '12px !important' }}>
          <Autocomplete
            options={owners.data ?? []}
            loading={owners.isLoading}
            getOptionLabel={(option) => `${option.name} · ${option.email}`}
            isOptionEqualToValue={(option, value) => option.userId === value.userId}
            onChange={(_event, owner) => {
              if (owner) mutation.mutate({ action: LEAD_OWNERSHIP_ACTION.Assign, ownerId: owner.userId });
            }}
            renderOption={(props, option) => (
              <li {...props} key={option.userId}>
                <Box>
                  <Typography variant="body2" sx={{ fontWeight: 800 }}>{option.name}</Typography>
                  <Typography variant="caption" color="text.secondary">
                    {option.roleName || option.email} · {option.capacityPercent}% capacity
                  </Typography>
                </Box>
              </li>
            )}
            renderInput={(params) => (
              <TextField {...params} autoFocus label="Search eligible owners" placeholder="Name or email" />
            )}
          />
        </DialogContent>
      </Dialog>
    </Box>
  );
};

export default LeadOwnerControl;
