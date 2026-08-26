import React from 'react';
import { Alert, Box, Button, Chip, CircularProgress, Menu, MenuItem, Stack, Typography } from '@mui/material';
import { ExpandMore as ExpandMoreIcon } from '@mui/icons-material';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'react-hot-toast';
import commercialRoutingService, {
  LEAD_OWNERSHIP_ACTION, type RoutingOwnerOption,
} from '../../api/services/commercialRoutingService';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { useAuth } from '../../context/AuthContext';
import {
  OwnerPickerMenu, AssignReasonDialog, assignmentNeedsReason, useOwnerOptions,
} from './LeadOwnerPicker';
import LeadOwnerHistory from './LeadOwnerHistory';

interface Props {
  leadId: number;
  assignedToId?: number | null;
  assignedToName?: string | null;
  assignmentMethod?: 'AUTOMATIC' | 'MANUAL';
  assignmentVersion: number;
  canEdit: boolean;
}

/**
 * Who may do what to a lead's owner.
 *
 * The server's rule, and therefore this one: take work that is nobody's, put down work that is
 * yours, and only a manager moves anyone else's. It is enforced with a 403 — but `apiErrors.ts`
 * deliberately replaces a 403's server sentence with the generic "Your role does not permit this
 * action", so a rep who clicked a control they could never use learns nothing from the failure.
 * The answer is not better error copy. It is not offering the control.
 */
export const ownershipAuthority = (
  assignedToId: number | null | undefined,
  myUserId: number | null | undefined,
  isManager: boolean,
) => ({
  /** Taking an unowned lead is everybody's right; taking one off a colleague is a manager's. */
  canTakeIt: isManager ? assignedToId !== myUserId : assignedToId == null,
  /** Handing a lead to a THIRD person. */
  canGiveItToSomeoneElse: isManager,
  /** Putting it back in the pool: yours to put down, or a manager's to take back. */
  canReturnItToThePool: assignedToId != null && (isManager || assignedToId === myUserId),
});

const mutationIdentity = (leadId: number): string =>
  `lead-owner-${leadId}-${Date.now()}-${crypto.randomUUID()}`;

const LeadOwnerControl: React.FC<Props> = ({
  leadId, assignedToId, assignedToName, assignmentMethod = 'AUTOMATIC', assignmentVersion, canEdit,
}) => {
  const queryClient = useQueryClient();
  const { userData } = useAuth();
  const [anchorEl, setAnchorEl] = React.useState<HTMLElement | null>(null);
  const [pickerAnchor, setPickerAnchor] = React.useState<HTMLElement | null>(null);
  const [reasonPrompt, setReasonPrompt] = React.useState<RoutingOwnerOption | null>(null);

  const menuOpen = Boolean(anchorEl);
  /**
   * Loaded as soon as the MENU opens, not only when the picker does.
   *
   * "Assign to me" used to be offered to every reader and answered 409 for anyone governed
   * routing would not accept — a button that fails after the click. The eligible-owner list is
   * the server's own verdict on that, so it has to be in hand before the menu draws itself.
   */
  const owners = useOwnerOptions(canEdit && (menuOpen || Boolean(pickerAnchor)));
  const myOption = (owners.data ?? []).find((option) => option.userId === userData?.id) ?? null;
  // The server resolves authority. Role names are display text, not permission signals.
  const isManager = userData?.isManager === true || Boolean(userData?.isSuperAdmin);
  const authority = ownershipAuthority(assignedToId, userData?.id ?? null, isManager);
  /**
   * A rep looking at a COLLEAGUE's lead may do none of the three things this menu offers, and a
   * menu with every item filtered out opens as an empty box — a dead end where an explanation
   * belongs. So the absence is stated instead.
   */
  const hasAnyOwnerAction = Boolean(userData?.id && authority.canTakeIt)
    || authority.canGiveItToSomeoneElse
    || authority.canReturnItToThePool;
  const iCanTakeIt = myOption?.isAvailable === true;
  const whyICannotTakeIt = myOption?.eligibilityReason?.trim()
    || 'You do not have a Sales Rep profile yet, so leads cannot be routed to you. Ask an administrator to add one under Sales > Rep directory.';

  const mutation = useMutation({
    mutationFn: ({ action, ownerId, reason }: { action: number; ownerId?: number; reason?: string }) => {
      const identity = mutationIdentity(leadId);
      return commercialRoutingService.changeLeadOwner(leadId, {
        action: action as 0 | 1 | 2,
        assignedToUserId: ownerId,
        expectedAssignmentVersion: assignmentVersion,
        idempotencyKey: identity,
        correlationId: identity,
        comment: reason ?? null,
      });
    },
    onSuccess: async () => {
      setAnchorEl(null);
      setPickerAnchor(null);
      setReasonPrompt(null);
      toast.success('Lead owner updated.');
      await queryClient.invalidateQueries({ queryKey: ['lead-detail', leadId] });
      await queryClient.invalidateQueries({ queryKey: ['lead-assignment-history', leadId] });
    },
    onError: (error: unknown) => toast.error(
      presentableErrorMessage(error, 'The owner changed elsewhere. Refresh the lead and try again.'),
    ),
  });

  /** Same rule as the leads list: only a lead already held by somebody else asks for a reason. */
  const assignTo = (owner: RoutingOwnerOption) => {
    if (assignmentNeedsReason(assignedToId, owner.userId)) {
      setPickerAnchor(null);
      setReasonPrompt(owner);
      return;
    }
    mutation.mutate({ action: LEAD_OWNERSHIP_ACTION.Assign, ownerId: owner.userId });
  };

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

      <Menu anchorEl={anchorEl} open={menuOpen} onClose={() => setAnchorEl(null)}>
        {userData?.id && authority.canTakeIt ? (
          <MenuItem
            disabled={!iCanTakeIt || owners.isLoading}
            onClick={() => {
              if (!myOption) return;
              setAnchorEl(null);
              assignTo(myOption);
            }}
            sx={{ display: 'block', py: 1 }}
          >
            <Typography variant="body2" sx={{ fontWeight: 700 }}>Assign to me</Typography>
            {/* A blocked action that will not say why is a support ticket, so it says why here
                instead of failing with a 409 after the click. */}
            {owners.isLoading && (
              <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary' }}>
                Checking whether this lead can be routed to you…
              </Typography>
            )}
            {!owners.isLoading && !iCanTakeIt && (
              <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary', whiteSpace: 'normal', maxWidth: 260 }}>
                {whyICannotTakeIt}
              </Typography>
            )}
          </MenuItem>
        ) : null}
        {authority.canGiveItToSomeoneElse ? (
          <MenuItem onClick={(event) => { setAnchorEl(null); setPickerAnchor(event.currentTarget); }}>
            Assign to…
          </MenuItem>
        ) : null}
        {/* Unassigning no longer strands an enquiry: it goes back on the routing queue and
            automatic routing may pick it up again. The label says that, because "Unassign" made
            it sound like the lead was being taken out of circulation. */}
        {authority.canReturnItToThePool ? (
          <MenuItem
            onClick={() => mutation.mutate({ action: LEAD_OWNERSHIP_ACTION.Unassign })}
            sx={{ display: 'block', py: 1 }}
          >
            <Typography variant="body2" sx={{ fontWeight: 700 }}>Put it back in the pool</Typography>
            <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary', whiteSpace: 'normal', maxWidth: 260 }}>
              It goes back on the queue and can be picked up again.
            </Typography>
          </MenuItem>
        ) : null}
        {authority.canReturnItToThePool && assignmentMethod === 'MANUAL' ? (
          <MenuItem onClick={() => mutation.mutate({ action: LEAD_OWNERSHIP_ACTION.ReturnToAutomatic })}>
            Return to automatic routing
          </MenuItem>
        ) : null}
        {!hasAnyOwnerAction ? (
          <MenuItem disabled sx={{ display: 'block', py: 1 }}>
            <Typography variant="body2" sx={{ fontWeight: 700 }}>
              This inquiry belongs to {ownerLabel}
            </Typography>
            <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary', whiteSpace: 'normal', maxWidth: 260 }}>
              Only a manager can move a lead that is already somebody&apos;s. Ask yours if it
              should come to you.
            </Typography>
          </MenuItem>
        ) : null}
      </Menu>

      {/* This used to be a Dialog holding a BARE Autocomplete. On a tenant where nobody yet has a
          Sales Rep profile, `owner-options` answers with an empty array — not an error — so the
          only assignment control a rep could reach rendered as an empty box that said nothing
          about why. It is now the same picker the leads list uses, empty state and all. */}
      <OwnerPickerMenu
        anchorEl={pickerAnchor}
        open={Boolean(pickerAnchor)}
        onClose={() => setPickerAnchor(null)}
        onPick={assignTo}
        busy={mutation.isPending}
        currentOwnerId={assignedToId}
      />

      <AssignReasonDialog
        open={Boolean(reasonPrompt)}
        ownerName={reasonPrompt?.name ?? ''}
        currentOwnerName={assignedToName}
        leadCount={1}
        busy={mutation.isPending}
        onCancel={() => setReasonPrompt(null)}
        onConfirm={(reason) => {
          if (reasonPrompt) {
            mutation.mutate({ action: LEAD_OWNERSHIP_ACTION.Assign, ownerId: reasonPrompt.userId, reason });
          }
        }}
      />

      {/* Nothing here is derived: it is the trail the server has been recording all along. */}
      <LeadOwnerHistory leadId={leadId} />

      {canEdit && owners.isError && (menuOpen || Boolean(pickerAnchor)) && (
        <Alert severity="error" sx={{ mt: 1, borderRadius: 2, maxWidth: 420 }}>
          We couldn&apos;t check who can take this lead. Nothing was changed — try again.
        </Alert>
      )}
    </Box>
  );
};

export default LeadOwnerControl;
