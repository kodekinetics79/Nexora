import React from 'react';
import { Chip, Tooltip } from '@mui/material';
import { HistoryToggleOff as LateIcon } from '@mui/icons-material';
import { formatDateSafe } from '../../utils/dates';

/**
 * Ingestion-audit badge (owner requirement: audit fairness).
 *
 * Rendered when the backend flags a lead as late-ingested: the lead ENTERED
 * Nexora (earliest source-document received_on; createdDate for manual
 * uploads) after its business due date, so it was already past deadline on
 * arrival. Such leads are excluded from Nexora's response-time / aging
 * performance metrics — this badge is how that audit fact stays visible to
 * users instead of living only in the API payload.
 *
 * Renders nothing when `lateIngested` is falsy, so callers can pass the DTO
 * fields straight through.
 */
export interface LateIngestedBadgeProps {
  lateIngested?: boolean;
  /** Audit ingestion timestamp (leadService `ingestedOn`). */
  ingestedOn?: string | null;
  /** Business due date the ingestion is judged against (bid closing / submission). */
  dueDate?: string | null;
}

const LateIngestedBadge: React.FC<LateIngestedBadgeProps> = ({ lateIngested, ingestedOn, dueDate }) => {
  if (!lateIngested) return null;

  const ingestedLabel = formatDateSafe(ingestedOn);
  const dueLabel = formatDateSafe(dueDate);
  const tooltip = `Audit: this lead was loaded into Nexora on ${ingestedLabel}, after its deadline`
    + `${dueLabel !== '—' ? ` of ${dueLabel}` : ''}. It was already past due on arrival, so it is `
    + 'excluded from Nexora\'s response-time and aging performance metrics.';

  return (
    <Tooltip title={tooltip}>
      <Chip
        size="small"
        icon={<LateIcon sx={{ fontSize: 14 }} />}
        label="Loaded after deadline"
        aria-label="Loaded after deadline"
        sx={{
          height: 20,
          fontSize: '0.65rem',
          fontWeight: 700,
          color: 'warning.main',
          bgcolor: 'transparent',
          border: '1px solid',
          borderColor: 'warning.main',
          '& .MuiChip-icon': { color: 'warning.main' },
        }}
      />
    </Tooltip>
  );
};

export default LateIngestedBadge;
