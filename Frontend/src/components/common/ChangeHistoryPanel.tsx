import React from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Chip, CircularProgress, Alert, Divider, Tooltip,
} from '@mui/material';
import masterDataAuditService, {
  type MasterDataChangeEventDTO,
  type MasterDataEntityType,
  type MasterDataFieldChangeDTO,
} from '../../api/services/masterDataAuditService';

/**
 * FR-MDM-05 · "audit every change with before/after values", made readable.
 *
 * The point of this panel is the FIELD row, not the event row. A reviewer asking "who moved this
 * product's landed cost, and what was it before" has to be able to see the answer without opening
 * a database console, and an event list that only says "product updated" does not answer it.
 * So every recorded field is rendered as `before → after`, and the fields the trail exists to
 * protect — cost, price, payment terms — are flagged.
 *
 * Nothing here invents a value. A field with no before value renders an explicit em dash rather
 * than an empty cell, because "this field was blank" and "we did not record this" are very
 * different statements to make to somebody reconstructing what happened.
 */
interface ChangeHistoryPanelProps {
  entityType: MasterDataEntityType;
  entityId: number;
  /** Rendered when the record has never been changed. Never fabricate history to fill the space. */
  emptyMessage?: string;
  limit?: number;
}

const CHANGE_TYPE_COLOR: Record<string, 'success' | 'info' | 'error'> = {
  CREATED: 'success',
  UPDATED: 'info',
  DELETED: 'error',
};

const SOURCE_LABEL: Record<string, string> = {
  API: 'Screen',
  IMPORT: 'Spreadsheet import',
  SYSTEM: 'System',
};

const formatTimestamp = (iso: string): string => {
  const parsed = new Date(iso);
  return Number.isNaN(parsed.getTime()) ? iso : parsed.toLocaleString();
};

/** `FinalLandedCost` reads as "Final Landed Cost" without a translation table to maintain. */
const humanizeField = (field: string): string =>
  field.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/^./, (c) => c.toUpperCase());

const FieldRow: React.FC<{ change: MasterDataFieldChangeDTO }> = ({ change }) => (
  <Box
    sx={{
      display: 'flex', gap: 1.5, alignItems: 'baseline', py: 0.75, flexWrap: 'wrap',
      borderBottom: '1px solid', borderColor: 'divider', '&:last-of-type': { border: 'none', pb: 0 },
    }}
  >
    <Typography component="span" sx={{ minWidth: 170, fontSize: '0.75rem', fontWeight: 700, color: 'text.secondary' }}>
      {humanizeField(change.field)}
    </Typography>
    <Typography
      component="span"
      sx={{ fontSize: '0.8rem', fontFamily: 'monospace', textDecoration: 'line-through', color: 'text.disabled' }}
    >
      {change.before ?? '—'}
    </Typography>
    <Typography component="span" sx={{ fontSize: '0.8rem', color: 'text.secondary' }}>→</Typography>
    <Typography component="span" sx={{ fontSize: '0.8rem', fontFamily: 'monospace', fontWeight: 700 }}>
      {change.after ?? '—'}
    </Typography>
    {change.sensitivity === 'COMMERCIAL' && (
      <Tooltip title="Cost, price or payment terms — this value feeds reported margin.">
        <Chip label="Commercial" size="small" color="warning" variant="outlined" sx={{ height: 18, fontSize: '0.6rem' }} />
      </Tooltip>
    )}
    {change.sensitivity === 'PERSONAL' && (
      <Tooltip title="Personal data.">
        <Chip label="Personal" size="small" variant="outlined" sx={{ height: 18, fontSize: '0.6rem' }} />
      </Tooltip>
    )}
  </Box>
);

const EventCard: React.FC<{ event: MasterDataChangeEventDTO }> = ({ event }) => (
  <Box sx={{ py: 1.5 }}>
    <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', flexWrap: 'wrap', mb: event.changes.length ? 1 : 0 }}>
      <Chip
        label={event.changeType}
        size="small"
        color={CHANGE_TYPE_COLOR[event.changeType] ?? 'default'}
        sx={{ height: 20, fontSize: '0.65rem', fontWeight: 800 }}
      />
      <Typography sx={{ fontSize: '0.8rem', fontWeight: 700 }}>{event.actor}</Typography>
      <Typography sx={{ fontSize: '0.75rem', color: 'text.secondary' }}>
        {formatTimestamp(event.occurredOn)}
      </Typography>
      <Chip
        label={SOURCE_LABEL[event.changeSource] ?? event.changeSource}
        size="small"
        variant="outlined"
        sx={{ height: 20, fontSize: '0.65rem' }}
      />
      {event.correlationId && (
        <Tooltip title="Request correlation id — ties this change to the server log entry.">
          <Typography sx={{ fontSize: '0.7rem', fontFamily: 'monospace', color: 'text.disabled' }}>
            {event.correlationId}
          </Typography>
        </Tooltip>
      )}
    </Box>
    {event.reason && (
      <Typography sx={{ fontSize: '0.8rem', fontStyle: 'italic', color: 'text.secondary', mb: 1 }}>
        {event.reason}
      </Typography>
    )}
    {event.changes.map((change) => (
      <FieldRow key={`${event.id}-${change.field}`} change={change} />
    ))}
  </Box>
);

const ChangeHistoryPanel: React.FC<ChangeHistoryPanelProps> = ({
  entityType, entityId, emptyMessage, limit = 50,
}) => {
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['master-data-change-history', entityType, entityId, limit],
    queryFn: () => masterDataAuditService.getChangeHistory(entityType, entityId, limit),
    enabled: Number.isFinite(entityId) && entityId > 0,
  });

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', py: 3 }}>
        <CircularProgress size={22} />
      </Box>
    );
  }

  if (isError) {
    // The trail failing to load must say so. Rendering an empty list on an error would read as
    // "nothing has ever changed", which is the one wrong answer an audit surface can give.
    const status = (error as { response?: { status?: number } })?.response?.status;
    return (
      <Alert severity="warning" sx={{ fontSize: '0.8rem' }}>
        {status === 403
          ? 'You do not have permission to view this record’s change history.'
          : 'The change history could not be loaded. It is not empty — it is unavailable.'}
      </Alert>
    );
  }

  const events = data ?? [];
  if (events.length === 0) {
    return (
      <Typography sx={{ fontSize: '0.8rem', color: 'text.secondary' }}>
        {emptyMessage ?? 'No changes have been recorded for this record.'}
      </Typography>
    );
  }

  // Deliberately NOT wrapped in a Paper: both host pages already render this inside their own
  // bordered Section, and a Paper here would draw a second border inside the first.
  return (
    <Box>
      {events.map((event, index) => (
        <React.Fragment key={event.id}>
          {index > 0 && <Divider />}
          <EventCard event={event} />
        </React.Fragment>
      ))}
    </Box>
  );
};

export default ChangeHistoryPanel;
