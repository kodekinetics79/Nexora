import { Box, Chip } from '@mui/material';
import type {
  BillingMode,
  HealthStatus,
  JobStatus,
  PlanTier,
  TenantStatus,
} from '../types';

type Tone = 'success' | 'warning' | 'error' | 'info' | 'neutral';

const TONE_COLORS: Record<Tone, string> = {
  success: '#10b981',
  warning: '#f59e0b',
  error: '#ef4444',
  info: '#3b82f6',
  neutral: '#94a3b8',
};

/** A soft filled status pill that reads well in both light and dark themes. */
export function SoftChip({ label, tone, dot = true }: { label: string; tone: Tone; dot?: boolean }) {
  const color = TONE_COLORS[tone];
  return (
    <Chip
      size="small"
      label={label}
      icon={dot ? <Box sx={{ width: 7, height: 7, borderRadius: '50%', bgcolor: color, ml: 1.2 }} /> : undefined}
      sx={{
        bgcolor: `${color}1f`,
        color,
        fontWeight: 700,
        fontSize: '0.7rem',
        letterSpacing: 0.2,
        textTransform: 'capitalize',
        border: '1px solid',
        borderColor: `${color}33`,
        '& .MuiChip-icon': { color },
      }}
    />
  );
}

const TENANT_TONE: Record<TenantStatus, Tone> = {
  active: 'success',
  trial: 'info',
  suspended: 'error',
  provisioning: 'warning',
  archived: 'neutral',
};

export const TenantStatusChip = ({ status }: { status: TenantStatus }) => (
  <SoftChip label={status} tone={TENANT_TONE[status]} />
);

const HEALTH_TONE: Record<HealthStatus, Tone> = {
  healthy: 'success',
  degraded: 'warning',
  down: 'error',
};

export const HealthChip = ({ status }: { status: HealthStatus }) => (
  <SoftChip label={status} tone={HEALTH_TONE[status]} />
);

const JOB_TONE: Record<JobStatus, Tone> = {
  queued: 'neutral',
  in_flight: 'info',
  succeeded: 'success',
  failed: 'warning',
  dead_letter: 'error',
};

const JOB_LABEL: Record<JobStatus, string> = {
  queued: 'Queued',
  in_flight: 'In-flight',
  succeeded: 'Succeeded',
  failed: 'Failed',
  dead_letter: 'Dead-letter',
};

export const JobStatusChip = ({ status }: { status: JobStatus }) => (
  <SoftChip label={JOB_LABEL[status]} tone={JOB_TONE[status]} />
);

// Well-known plan codes get a stable tone; any other real plan code falls back
// to "info", and plan-less tenants ("none") read as a warning.
const PLAN_TONE: Record<string, Tone> = {
  free: 'neutral',
  pro: 'info',
  enterprise: 'success',
  none: 'warning',
};

export const PlanChip = ({ tier }: { tier: PlanTier }) => (
  <SoftChip label={tier} tone={PLAN_TONE[tier] ?? 'info'} dot={false} />
);

// Billable is the only mode that earns money, so it is the only one that reads
// as "good". Everything else is service given away and is toned to say so —
// Trial is time-boxed (info), Internal and Partner are open-ended (warning).
const BILLING_MODE_TONE: Record<BillingMode, Tone> = {
  Billable: 'success',
  Trial: 'info',
  Internal: 'warning',
  Partner: 'warning',
};

/** Renders "—" for a tenant provisioned before billing modes existed. */
export const BillingModeChip = ({ mode }: { mode: BillingMode | null }) =>
  mode ? <SoftChip label={mode} tone={BILLING_MODE_TONE[mode]} dot={false} /> : <Dash />;

export const Dash = () => (
  <Box component="span" sx={{ color: 'text.disabled', fontWeight: 700 }}>
    —
  </Box>
);
