import type { ReactElement } from 'react';
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined';
import { Box, IconButton, Tooltip } from '@mui/material';

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
 * <p>A disabled MUI button swallows pointer and keyboard events. The adjacent information
 * button is therefore the tooltip trigger: it remains a valid focus stop without pretending
 * the disabled action itself is enabled or nesting one button inside another.</p>
 */
export default function RoleGate({ allowed, requirement, children }: Props) {
  if (allowed) return children(false);
  return (
    <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5 }}>
      {children(true)}
      <Tooltip title={requirement}>
        <IconButton
          size="small"
          aria-label={`Why this action is unavailable: ${requirement}`}
          sx={{ color: 'text.secondary' }}
        >
          <InfoOutlinedIcon fontSize="inherit" />
        </IconButton>
      </Tooltip>
    </Box>
  );
}
