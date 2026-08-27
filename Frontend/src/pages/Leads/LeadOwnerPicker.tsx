import React from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Alert, Box, Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogContentText,
  DialogTitle, Menu, MenuItem, TextField, Typography,
} from '@mui/material';
import { Person as UserIcon } from '@mui/icons-material';
import commercialRoutingService, { type RoutingOwnerOption } from '../../api/services/commercialRoutingService';

/**
 * The one owner picker every assign control on the leads screens opens.
 *
 * There used to be three of them and they disagreed. `OutstandingLeadsPage` had a 2-click inline
 * menu that printed WHY a name could not take the lead; `LeadOwnerControl` on the lead detail page
 * had an autocomplete that printed nothing at all and, on a tenant where nobody yet has a Sales Rep
 * profile, rendered as an EMPTY box — the only assignment control a rep could reach, silently
 * offering nothing and saying nothing about it. The leads list, the screen a rep actually lives on,
 * had no owner control whatsoever.
 *
 * So the menu, the eligibility notes and the honest "nobody can take a lead yet" state live here
 * once, and each screen supplies only the anchor and what to do with the name that comes back.
 */

/**
 * What governed routing needs before ANY name can appear here, in the words of the thing the
 * reader has to go and do. `GET /api/commercial-routing/owner-options` returns an empty array —
 * not an error — when no user in the business unit carries an effective Sales Rep profile, a
 * team membership, an account ownership or a live assignment, so an empty list is a setup fact
 * and has to read as one.
 */
export const NO_ELIGIBLE_OWNER_TITLE = 'Nobody in this business unit can currently receive a lead.';
export const NO_ELIGIBLE_OWNER_DETAIL =
  'Governed routing only accepts a user who has an effective Sales Rep profile with capacity left, '
  + 'and there is no such user right now. Give someone a profile in Sales > Rep directory.';

/** The justification printed under a name, so a greyed-out row is never a mystery. */
export const ownerAvailabilityNote = (option: RoutingOwnerOption): string =>
  option.isAvailable
    ? `${option.capacityPercent}% capacity`
    : option.eligibilityReason || 'Not currently eligible to receive a lead.';

/**
 * Whether this change of owner has to carry a reason.
 *
 * Taking an UNOWNED lead — the overwhelmingly common case, and the one this whole screen exists
 * to make cheap — never asks for one. Moving a lead that already belongs to somebody else is a
 * decision about a colleague's work and does.
 *
 * This is deliberately the single place that rule is expressed, and it is the client half of the
 * server's own guard on `PUT /api/commercial-routing/leads/{id}/owner` (and the four other assign
 * verbs, which are guarded centrally). The two must agree: the server refuses with a 400, and a
 * form that only finds that out after the click is a form that wasted the click.
 */
export const assignmentNeedsReason = (
  currentOwnerId: number | null | undefined,
  nextOwnerId: number,
): boolean => currentOwnerId != null && currentOwnerId !== nextOwnerId;

/** The server refuses anything shorter after trimming, so the form does not offer to try. */
export const MINIMUM_ASSIGNMENT_REASON = 5;

/**
 * The eligible-owner list, shared by every caller through one react-query key so opening the
 * menu on a second row does not re-fetch it.
 */
export const useOwnerOptions = (enabled: boolean) => useQuery({
  queryKey: ['lead-owner-options'],
  queryFn: commercialRoutingService.getOwnerOptions,
  enabled,
  staleTime: 60_000,
});

/** Rendered wherever the eligible-owner list came back empty. Never a blank control. */
export const NoEligibleOwners: React.FC<{ compact?: boolean }> = ({ compact = false }) => (
  <Alert severity="warning" sx={{ borderRadius: 2, m: compact ? 1 : 0, maxWidth: 420 }}>
    <Typography variant="body2" sx={{ fontWeight: 700 }}>{NO_ELIGIBLE_OWNER_TITLE}</Typography>
    <Typography variant="body2">{NO_ELIGIBLE_OWNER_DETAIL}</Typography>
  </Alert>
);

interface OwnerPickerMenuProps {
  anchorEl: HTMLElement | null;
  open: boolean;
  onClose: () => void;
  /** Called with the name the reader picked. The caller decides what that means. */
  onPick: (owner: RoutingOwnerOption) => void;
  /** Disables every row while an assignment is in flight. */
  busy?: boolean;
  /** Heading above the list — "Pick a person", or who the selection is for. */
  heading?: string;
  /** Greyed out with "Already the owner" rather than offered as a no-op. */
  currentOwnerId?: number | null;
}

/** Click 2 of the 2-click assign: the list of names, each with the reason it can or cannot take it. */
export const OwnerPickerMenu: React.FC<OwnerPickerMenuProps> = ({
  anchorEl, open, onClose, onPick, busy = false, heading = 'Pick a person', currentOwnerId,
}) => {
  const owners = useOwnerOptions(open);
  const options = owners.data ?? [];

  return (
    <Menu
      anchorEl={anchorEl}
      open={open}
      onClose={onClose}
      slotProps={{
        paper: { sx: { borderRadius: 2, minWidth: 260, maxWidth: 420, maxHeight: 380 } },
        list: { 'aria-label': 'Eligible lead owners' },
      }}
    >
      <Typography sx={{ px: 2, py: 0.75, fontSize: '0.65rem', fontWeight: 900, color: 'text.disabled', textTransform: 'uppercase' }}>
        {heading}
      </Typography>

      {owners.isLoading && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, px: 2, py: 1.5 }}>
          <CircularProgress size={16} />
          <Typography variant="body2" color="text.secondary">Checking who can take it…</Typography>
        </Box>
      )}

      {/* An owner list that failed to load is not an owner list that is empty, and the two must
          never render the same way: one is a setup task, the other is an outage. */}
      {owners.isError && (
        <Box sx={{ px: 1, py: 1 }}>
          <Alert severity="error" sx={{ borderRadius: 2, maxWidth: 380 }}>
            We couldn&apos;t load the list of people who can take this lead. Nothing was changed —
            close this and try again.
          </Alert>
        </Box>
      )}

      {!owners.isLoading && !owners.isError && options.length === 0 && (
        <Box sx={{ px: 1, py: 1 }}><NoEligibleOwners /></Box>
      )}

      {options.map((owner) => {
        const isCurrent = currentOwnerId != null && currentOwnerId === owner.userId;
        return (
          <MenuItem
            key={owner.userId}
            disabled={busy || !owner.isAvailable || isCurrent}
            onClick={() => onPick(owner)}
            sx={{ display: 'block', py: 1 }}
          >
            <Box sx={{ display: 'flex', alignItems: 'center' }}>
              <UserIcon sx={{ fontSize: 16, mr: 1, color: 'primary.main' }} />
              <Typography sx={{ fontSize: '0.85rem', fontWeight: 700 }}>{owner.name}</Typography>
            </Box>
            <Typography variant="caption" sx={{ display: 'block', pl: 3, color: 'text.secondary', whiteSpace: 'normal' }}>
              {isCurrent ? 'Already the owner' : ownerAvailabilityNote(owner)}
            </Typography>
          </MenuItem>
        );
      })}
    </Menu>
  );
};

interface AssignReasonDialogProps {
  open: boolean;
  /** The name the lead is moving TO — so the prompt says what it is asking about. */
  ownerName: string;
  /** Who holds it now, when that is one person. */
  currentOwnerName?: string | null;
  /** How many leads this reason will be recorded against. */
  leadCount: number;
  onCancel: () => void;
  onConfirm: (reason: string) => void;
  busy?: boolean;
}

/**
 * Asked for ONLY when `assignmentNeedsReason` says so — never on a plain self-assign, which is
 * the whole point of the two-click path.
 */
export const AssignReasonDialog: React.FC<AssignReasonDialogProps> = ({
  open, ownerName, currentOwnerName, leadCount, onCancel, onConfirm, busy = false,
}) => {
  const [reason, setReason] = React.useState('');

  React.useEffect(() => { if (open) setReason(''); }, [open]);

  const trimmed = reason.trim();
  const tooShort = trimmed.length < MINIMUM_ASSIGNMENT_REASON;
  return (
    <Dialog open={open} onClose={onCancel} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 900 }}>Why is this moving?</DialogTitle>
      <DialogContent>
        <DialogContentText sx={{ mb: 2 }}>
          {leadCount === 1
            ? `This inquiry already belongs to ${currentOwnerName?.trim() || 'someone else'}. Moving it to ${ownerName} is recorded against both names, so say why.`
            : `${leadCount} of the selected inquiries already belong to someone else. Moving them to ${ownerName} is recorded against every name involved, so say why.`}
        </DialogContentText>
        <TextField
          fullWidth
          multiline
          rows={3}
          label="Reason"
          placeholder="e.g. Owner is on leave until the 3rd"
          value={reason}
          onChange={(event) => setReason(event.target.value)}
        />
      </DialogContent>
      <DialogActions sx={{ p: 2.5 }}>
        <Button onClick={onCancel} color="inherit" sx={{ fontWeight: 700 }}>Cancel</Button>
        <Button
          variant="contained"
          disabled={tooShort || busy}
          onClick={() => onConfirm(trimmed)}
          sx={{ fontWeight: 800 }}
        >
          {busy ? 'Reassigning…' : 'Reassign'}
        </Button>
      </DialogActions>
      {/* A disabled control that will not say why is a support ticket. */}
      {tooShort && (
        <Typography variant="caption" sx={{ px: 3, pb: 2, display: 'block', color: 'text.secondary' }}>
          Type a reason of at least {MINIMUM_ASSIGNMENT_REASON} characters to enable Reassign.
        </Typography>
      )}
    </Dialog>
  );
};

export default OwnerPickerMenu;
