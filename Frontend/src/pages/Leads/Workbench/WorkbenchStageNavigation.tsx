import React from 'react';
import { Box, Chip, Paper, Stack, Tab, Tabs, Typography } from '@mui/material';

export type WorkbenchStage = 'evidence' | 'validate' | 'participation' | 'promote';
export type WorkbenchStageProgress = 'complete' | 'blocked' | 'needs-action';

export interface WorkbenchStageStatus {
  progress: WorkbenchStageProgress;
  detail: string;
}

export type WorkbenchStageStatuses = Record<WorkbenchStage, WorkbenchStageStatus>;

export const WORKBENCH_STAGE_LABELS: Record<WorkbenchStage, string> = {
  evidence: '1. Evidence',
  validate: '2. Review transformation',
  participation: '3. Fit & Participation',
  promote: '4. Promote',
};

export const workbenchStageFromValue = (value: string | null | undefined): WorkbenchStage =>
  value && Object.hasOwn(WORKBENCH_STAGE_LABELS, value) ? value as WorkbenchStage : 'evidence';

export const workbenchStageSearchParams = (
  current: URLSearchParams,
  stage: WorkbenchStage,
): URLSearchParams => {
  const next = new URLSearchParams(current);
  next.set('stage', stage);
  return next;
};

export const workbenchStageTabId = (stage: WorkbenchStage): string => `lead-decision-tab-${stage}`;
export const workbenchStagePanelId = (stage: WorkbenchStage): string => `lead-decision-panel-${stage}`;

interface WorkbenchStageTabsProps {
  value: WorkbenchStage;
  onChange: (stage: WorkbenchStage) => void;
  statuses?: WorkbenchStageStatuses;
}

const STATUS_LABELS: Record<WorkbenchStageProgress, string> = {
  complete: 'Complete',
  blocked: 'Blocked',
  'needs-action': 'Needs action',
};

const STATUS_COLORS: Record<WorkbenchStageProgress, 'success' | 'warning' | 'default'> = {
  complete: 'success',
  blocked: 'warning',
  'needs-action': 'default',
};

export const WorkbenchStageTabs: React.FC<WorkbenchStageTabsProps> = ({ value, onChange, statuses }) => (
  <Paper variant="outlined" sx={{ mb: 1.5, borderRadius: 2, overflow: 'hidden' }}>
    <Tabs
      value={value}
      onChange={(_event, stage: WorkbenchStage) => onChange(stage)}
      variant="scrollable"
      scrollButtons="auto"
      allowScrollButtonsMobile
      aria-label="Lead decision stages"
      sx={{
        '& .MuiTab-root:focus-visible': {
          outline: '3px solid',
          outlineColor: 'text.primary',
          outlineOffset: '-3px',
          borderRadius: 1,
        },
      }}
    >
      {(Object.keys(WORKBENCH_STAGE_LABELS) as WorkbenchStage[]).map((stage) => (
        <Tab
          key={stage}
          id={workbenchStageTabId(stage)}
          aria-controls={workbenchStagePanelId(stage)}
          aria-current={stage === value ? 'step' : undefined}
          aria-label={statuses
            ? `${WORKBENCH_STAGE_LABELS[stage]}: ${stage === value ? `Current, ${STATUS_LABELS[statuses[stage].progress]}` : STATUS_LABELS[statuses[stage].progress]}. ${statuses[stage].detail}`
            : undefined}
          value={stage}
          label={statuses ? (
            <Stack spacing={0.5} sx={{ alignItems: 'center' }}>
              <Typography component="span" variant="button" sx={{ lineHeight: 1.15 }}>
                {WORKBENCH_STAGE_LABELS[stage]}
              </Typography>
              <Chip
                component="span"
                aria-hidden="true"
                size="small"
                variant={stage === value || statuses[stage].progress === 'complete' ? 'filled' : 'outlined'}
                color={stage === value ? 'primary' : STATUS_COLORS[statuses[stage].progress]}
                label={stage === value ? `Current · ${STATUS_LABELS[statuses[stage].progress]}` : STATUS_LABELS[statuses[stage].progress]}
                sx={{ height: 20, '& .MuiChip-label': { px: 0.75, fontSize: '0.68rem', fontWeight: 800 } }}
              />
            </Stack>
          ) : WORKBENCH_STAGE_LABELS[stage]}
          sx={{ minHeight: statuses ? 68 : undefined, minWidth: { xs: 152, sm: 176 } }}
        />
      ))}
    </Tabs>
  </Paper>
);

interface WorkbenchStagePanelProps {
  stage: WorkbenchStage;
  activeStage: WorkbenchStage;
  children: React.ReactNode;
}

export const WorkbenchStagePanel: React.FC<WorkbenchStagePanelProps> = ({ stage, activeStage, children }) => {
  if (stage !== activeStage) return null;

  return (
    <Box
      id={workbenchStagePanelId(stage)}
      role="tabpanel"
      aria-labelledby={workbenchStageTabId(stage)}
      tabIndex={0}
      sx={{
        '&:focus-visible': {
          outline: '3px solid',
          outlineColor: 'text.primary',
          outlineOffset: 2,
        },
      }}
    >
      {children}
    </Box>
  );
};
