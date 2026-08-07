import type { ReactElement } from 'react';
import { Box, Tooltip } from '@mui/material';

interface Props {
  /** The capability from `usePlatformPermissions`, never a role string compared inline. */
  allowed: boolean;
  /** Who the operator would have to be. Shown verbatim on the disabled control. */
  requirement: string;
  /**
   * Rendered with the disabled flag so the caller keeps ownership of its own control —
   * a Button, an IconButton or a whole toolbar can all be gated the same way.
   */
  children: (disabled: boolean) => ReactElement;
}

/**
 * Wraps a privileged control so an operator without the authority sees it disabled and is
 * told which role carries it, rather than clicking into a 403.
 *
 * <p>Disabled rather than hidden, deliberately. The separation of duties between Owner,
 * SupportAdmin and BillingAdmin is a designed property of the console; hiding the controls
 * makes it look like the feature does not exist, and the operator's real next step — find
 * the person who can — needs them to know the action is there and who owns it.</p>
 *
 * <p>The wrapping span is required: a disabled MUI button swallows pointer events, so the
 * tooltip would never fire and the explanation would be unreachable.</p>
 */
export default function RoleGate({ allowed, requirement, children }: Props) {
  if (allowed) return children(false);
  return (
    <Tooltip title={requirement}>
      <Box component="span" sx={{ display: 'inline-flex' }}>
        {children(true)}
      </Box>
    </Tooltip>
  );
}
