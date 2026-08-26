import React from 'react';
import { Box, Paper, Tab, Tabs } from '@mui/material';

export type WorkbenchStage = 'evidence' | 'validate' | 'participation' | 'promote';

export const WORKBENCH_STAGE_LABELS: Record<WorkbenchStage, string> = {
  evidence: '1. Evidence',
  validate: '2. Review transformation',
  participation: '3. Fit & Participation',
  promote: '4. Promote',
};

export const workbenchStageTabId = (stage: WorkbenchStage): string => `lead-decision-tab-${stage}`;
export const workbenchStagePanelId = (stage: WorkbenchStage): string => `lead-decision-panel-${stage}`;

interface WorkbenchStageTabsProps {
  value: WorkbenchStage;
  onChange: (stage: WorkbenchStage) => void;
}

export const WorkbenchStageTabs: React.FC<WorkbenchStageTabsProps> = ({ value, onChange }) => (
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
          value={stage}
          label={WORKBENCH_STAGE_LABELS[stage]}
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
