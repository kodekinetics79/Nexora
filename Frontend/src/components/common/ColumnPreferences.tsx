import React, { useState } from 'react';
import {
  Box, Button, Checkbox, Chip, CircularProgress, Divider, FormControlLabel,
  IconButton, Popover, Stack, Tooltip, Typography,
} from '@mui/material';
import {
  ViewColumn as ColumnsIcon,
  KeyboardArrowUp as UpIcon,
  KeyboardArrowDown as DownIcon,
  RestartAlt as ResetIcon,
} from '@mui/icons-material';
import type { UseColumnPreferencesResult } from '../../hooks/useColumnPreferences';

/**
 * AA-01 · the control the product owner actually asked for.
 *
 * "by clicking the check box the customer will have flexibility to have what field they
 * would think they need, plus they would be able to shuffle columns as per user preference"
 *
 * Deliberately a button ON the grid's own toolbar, not a settings page: a Sales Engineer
 * changes which columns they want while looking at the data, not three levels into an admin
 * area. Everything is a checkbox and a pair of arrows — no drag-and-drop, because drag on a
 * dense list is the least reliable interaction for a non-technical user on a laptop
 * trackpad, and it would need a dependency this project does not carry.
 *
 * Changes save immediately and per user. There is no Apply button, so there is nothing to
 * forget to press.
 */
export interface ColumnPreferencesProps {
  preferences: UseColumnPreferencesResult;
  /** Button label. Defaults to "Columns". */
  label?: string;
}

const ColumnPreferences: React.FC<ColumnPreferencesProps> = ({ preferences, label = 'Columns' }) => {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const {
    columns, isLoading, isError, isCustomised, isSaving, setVisible, move, reset,
    supportsCustomFields,
  } = preferences;

  const visibleCount = columns.filter((c) => c.visible || c.locked).length;
  const customFieldCount = columns.filter((c) => c.source === 'customField').length;

  return (
    <>
      <Tooltip title="Choose which columns you see, and their order. Saved to your account.">
        <Button
          size="small"
          startIcon={<ColumnsIcon />}
          onClick={(e) => setAnchor(e.currentTarget)}
          aria-label="Choose columns"
          sx={{ fontWeight: 600, color: 'text.secondary', whiteSpace: 'nowrap' }}
        >
          {label}
          {columns.length > 0 && (
            <Typography component="span" variant="caption" sx={{ ml: 0.75, color: 'text.disabled' }}>
              {visibleCount}/{columns.length}
            </Typography>
          )}
        </Button>
      </Tooltip>

      <Popover
        open={Boolean(anchor)}
        anchorEl={anchor}
        onClose={() => setAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
        slotProps={{ paper: { sx: { width: 320, maxHeight: 480 } } }}
      >
        <Box sx={{ p: 2, pb: 1 }}>
          <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between', mb: 0.5 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 700 }}>
              Show columns
            </Typography>
            {isSaving && <CircularProgress size={14} />}
          </Stack>
          <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>
            Tick what you want to see. Use the arrows to reorder. Saved to your account only.
          </Typography>
        </Box>

        <Divider />

        <Box sx={{ px: 1.5, py: 1, maxHeight: 300, overflowY: 'auto' }}>
          {isLoading && (
            <Typography variant="body2" sx={{ color: 'text.secondary', p: 1 }}>
              Loading your column layout…
            </Typography>
          )}

          {isError && (
            <Typography variant="body2" sx={{ color: 'text.secondary', p: 1 }}>
              Your saved layout could not be loaded, so this grid is showing its standard
              columns. Nothing has been lost — try again shortly.
            </Typography>
          )}

          {!isLoading && !isError && columns.map((column, index) => (
            <Stack
              key={column.key}
              direction="row"
              sx={{ alignItems: 'center', justifyContent: 'space-between', gap: 0.5 }}
            >
              <FormControlLabel
                sx={{ flexGrow: 1, minWidth: 0, mr: 0 }}
                control={
                  <Checkbox
                    size="small"
                    checked={column.visible || column.locked}
                    disabled={column.locked}
                    onChange={(e) => setVisible(column.key, e.target.checked)}
                    slotProps={{ input: { 'aria-label': `Show ${column.label}` } }}
                  />
                }
                label={
                  <Stack direction="row" sx={{ alignItems: 'center', gap: 0.5, minWidth: 0 }}>
                    <Typography
                      variant="body2"
                      sx={{
                        color: column.locked ? 'text.disabled' : 'text.primary',
                        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                      }}
                    >
                      {column.label}
                    </Typography>
                    {column.source === 'customField' && (
                      <Tooltip title="A field your organisation added">
                        <Chip
                          label="Custom"
                          size="small"
                          variant="outlined"
                          color="primary"
                          sx={{ height: 18, fontSize: '0.6rem', fontWeight: 700 }}
                        />
                      </Tooltip>
                    )}
                  </Stack>
                }
              />
              <Stack direction="row" sx={{ flexShrink: 0 }}>
                <IconButton
                  size="small"
                  aria-label={`Move ${column.label} up`}
                  disabled={index === 0}
                  onClick={() => move(column.key, -1)}
                >
                  <UpIcon fontSize="inherit" />
                </IconButton>
                <IconButton
                  size="small"
                  aria-label={`Move ${column.label} down`}
                  disabled={index === columns.length - 1}
                  onClick={() => move(column.key, 1)}
                >
                  <DownIcon fontSize="inherit" />
                </IconButton>
              </Stack>
            </Stack>
          ))}

          {/* Only promised where a tenant-defined field can actually appear. */}
          {!isLoading && !isError && columns.length > 0 && customFieldCount === 0 && supportsCustomFields && (
            <Typography variant="caption" sx={{ color: 'text.disabled', display: 'block', px: 1, pt: 1 }}>
              Fields your organisation adds to this record appear here automatically.
            </Typography>
          )}
        </Box>

        <Divider />

        <Box sx={{ p: 1.5, display: 'flex', justifyContent: 'flex-end' }}>
          <Button
            size="small"
            startIcon={<ResetIcon />}
            onClick={reset}
            disabled={!isCustomised || isSaving}
            sx={{ fontWeight: 600 }}
          >
            Reset to default
          </Button>
        </Box>
      </Popover>
    </>
  );
};

export default ColumnPreferences;
