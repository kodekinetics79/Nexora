import type { ReactNode } from 'react';
import { Box, IconButton, Tooltip, Typography } from '@mui/material';
import type { TooltipProps } from '@mui/material/Tooltip';

interface FeatureHelpProps {
  label: string;
  description: ReactNode;
  title?: string;
  placement?: TooltipProps['placement'];
}

/**
 * Short, contextual product education for unfamiliar concepts.
 *
 * The trigger is a real button so the same explanation works with hover, keyboard focus,
 * touch, and assistive technology. Essential warnings and irreversible consequences must
 * remain visible in the page or confirmation dialog rather than living only in this tooltip.
 */
export default function FeatureHelp({
  label,
  description,
  title = label,
  placement = 'top',
}: FeatureHelpProps) {
  return (
    <Tooltip
      arrow
      describeChild
      placement={placement}
      enterTouchDelay={0}
      leaveTouchDelay={8_000}
      title={(
        <Box sx={{ p: 0.5 }}>
          <Typography variant="subtitle2" component="p" sx={{ fontWeight: 800, mb: 0.5 }}>
            {title}
          </Typography>
          <Typography variant="body2" component="p" sx={{ m: 0, lineHeight: 1.5 }}>
            {description}
          </Typography>
        </Box>
      )}
      slotProps={{
        tooltip: {
          sx: {
            maxWidth: 360,
            p: 1,
            bgcolor: 'grey.900',
            color: 'common.white',
            boxShadow: 6,
          },
        },
      }}
    >
      <IconButton
        size="small"
        aria-label={`Learn more about ${label}`}
        sx={{
          width: 28,
          height: 28,
          ml: 0.25,
          color: 'primary.main',
          '&:focus-visible': {
            outline: '3px solid',
            outlineColor: 'primary.main',
            outlineOffset: 2,
          },
        }}
      >
        <Typography component="span" aria-hidden="true" sx={{ fontSize: 15, fontWeight: 900, lineHeight: 1 }}>
          ?
        </Typography>
      </IconButton>
    </Tooltip>
  );
}
