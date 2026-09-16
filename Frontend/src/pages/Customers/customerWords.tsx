import React from 'react';
import { Tooltip, Typography } from '@mui/material';
import { statusLabel } from '../../utils/statusLabels';

/**
 * The customer screens' shared words for two things a rep kept meeting as raw tokens.
 *
 * 1. "No account team" — shown in red with no explanation of why it is red or what to do.
 * 2. Health and trend statuses — "insufficient-evidence", "unavailable", and a landed-cost
 *    sentence written for an engineer — printed straight from the wire.
 *
 * One module, so the list cell, the detail page and the tests read the same sentences.
 */

/** Why a missing account team is worth a red mark, and where to fix it. */
export const NO_ACCOUNT_TEAM_EXPLANATION =
  'No account team: leads from this customer route to nobody by default. Set one on the customer page.';

export const NO_HISTORY_YET = 'Not enough history yet';

/**
 * A status token from the intelligence endpoints in a rep's words. "available" renders as
 * nothing, because the number beside it already says everything.
 */
export const healthWord = (status?: string | null): string => {
  switch ((status ?? '').trim().toLowerCase()) {
    case 'available': return '';
    case '':
    case 'unavailable':
    case 'insufficient-evidence':
    case 'insufficient_evidence': return NO_HISTORY_YET;
    case 'healthy': return 'Healthy';
    case 'at-risk':
    case 'at_risk':
    case 'risk': return 'At risk';
    case 'watch': return 'Worth watching';
    default: return statusLabel(status);
  }
};

/** The account-health band as a chip label: never the raw token. */
export const healthBandWord = (band?: string | null): string => healthWord(band) || NO_HISTORY_YET;

/**
 * A measured figure with its status: the figure when there is one, otherwise the status in plain
 * words. "12.5%" or "Not enough history yet" — never "Insufficient evidence (insufficient-evidence)".
 */
export const metricWithStatus = (value: number | null | undefined, suffix: string, status?: string | null): string =>
  value == null ? (healthWord(status) || NO_HISTORY_YET) : `${value.toLocaleString()}${suffix}`;

/**
 * The stated gap for a customer with no account team. In a grid cell the reason is a tooltip
 * (and an accessible description); on the detail page it is printed, because there is room and
 * the button to fix it sits beside it.
 */
export const AccountTeamGap: React.FC<{ variant?: 'cell' | 'detail' }> = ({ variant = 'cell' }) => {
  const mark = (
    <Typography
      component="span"
      tabIndex={variant === 'cell' ? 0 : undefined}
      sx={{ color: 'warning.main', fontWeight: 700, fontSize: variant === 'cell' ? '0.75rem' : '0.875rem' }}
    >
      No account team
    </Typography>
  );
  if (variant === 'detail') {
    return (
      <span>
        {mark}
        <Typography component="span" variant="body2" color="text.secondary" sx={{ ml: 1 }}>
          Leads from this customer route to nobody by default.
        </Typography>
      </span>
    );
  }
  return (
    <Tooltip title={NO_ACCOUNT_TEAM_EXPLANATION} describeChild>
      {mark}
    </Tooltip>
  );
};
