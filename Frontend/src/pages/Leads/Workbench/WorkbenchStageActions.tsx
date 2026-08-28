import React from 'react';
import {
  Box,
  Button,
  CircularProgress,
  Divider,
  Paper,
  Stack,
  Typography,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  ArrowForward as ForwardIcon,
  CheckCircleOutlined as PromoteIcon,
  SaveOutlined as SaveIcon,
} from '@mui/icons-material';
import type { WorkbenchStage, WorkbenchStageStatus } from './WorkbenchStageNavigation';

interface WorkbenchStageActionsProps {
  stage: WorkbenchStage;
  status: WorkbenchStageStatus;
  onStageChange: (stage: WorkbenchStage) => void;
  canContinueEvidence: boolean;
  canContinueValidation: boolean;
  canEdit: boolean;
  dirty: boolean;
  hasSavedFitAssessment: boolean;
  decisionPending: boolean;
  decisionRecordLocked: boolean;
  canCommit: boolean;
  participationCommitted: boolean;
  participationStatus: string;
  fullNoBid: boolean;
  fullNoBidClosed: boolean;
  onSaveDraft: () => void;
  onCommit: () => void;
  canPromote: boolean;
  promotionBlocked: boolean;
  promotionPending: boolean;
  alreadyPromoted: boolean;
  approvedLineCount: number;
  onPromote: () => void;
}

const BackButton = ({ stage, onStageChange }: {
  stage: Extract<WorkbenchStage, 'validate' | 'participation' | 'promote'>;
  onStageChange: (stage: WorkbenchStage) => void;
}) => {
  const previous: Record<typeof stage, WorkbenchStage> = {
    validate: 'evidence',
    participation: 'validate',
    promote: 'participation',
  };
  const labels: Record<typeof stage, string> = {
    validate: 'Back to evidence',
    participation: 'Back to review',
    promote: 'Back to participation',
  };
  return (
    <Button variant="text" startIcon={<BackIcon />} onClick={() => onStageChange(previous[stage])}>
      {labels[stage]}
    </Button>
  );
};

export const WorkbenchStageActions: React.FC<WorkbenchStageActionsProps> = ({
  stage,
  status,
  onStageChange,
  canContinueEvidence,
  canContinueValidation,
  canEdit,
  dirty,
  hasSavedFitAssessment,
  decisionPending,
  decisionRecordLocked,
  canCommit,
  participationCommitted,
  participationStatus,
  fullNoBid,
  fullNoBidClosed,
  onSaveDraft,
  onCommit,
  canPromote,
  promotionBlocked,
  promotionPending,
  alreadyPromoted,
  approvedLineCount,
  onPromote,
}) => (
  <Paper
    elevation={6}
    component="footer"
    aria-label={`${stage} stage actions`}
    sx={{ position: 'sticky', bottom: 12, zIndex: 10, mt: 2, p: 1.5, borderRadius: 2, width: '100%' }}
  >
    <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.25} sx={{ alignItems: { xs: 'stretch', md: 'center' } }}>
      <Box sx={{ flex: 1, minWidth: 0 }} aria-live="polite">
        <Typography variant="body2" sx={{ fontWeight: 800 }}>{status.detail}</Typography>
        <Typography variant="caption" color="text.secondary">
          Stage {stage === 'validate' ? '2' : stage === 'participation' ? '3' : stage === 'promote' ? '4' : '1'} of 4
          {stage === 'participation' ? ` · Participation ${participationStatus.toLowerCase()}` : ''}
          {stage === 'participation' && dirty ? ' · Unsaved changes' : ''}
        </Typography>
      </Box>
      <Divider orientation="vertical" flexItem sx={{ display: { xs: 'none', md: 'block' } }} />

      {stage === 'evidence' ? (
        <Button
          variant="contained"
          endIcon={<ForwardIcon />}
          disabled={!canContinueEvidence}
          onClick={() => onStageChange('validate')}
          sx={{ fontWeight: 800 }}
        >
          Review transformation
        </Button>
      ) : null}

      {stage === 'validate' ? (
        <>
          <BackButton stage="validate" onStageChange={onStageChange} />
          <Button
            variant="contained"
            endIcon={<ForwardIcon />}
            disabled={!canContinueValidation}
            onClick={() => onStageChange('participation')}
            sx={{ fontWeight: 800 }}
          >
            Continue to fit & participation
          </Button>
        </>
      ) : null}

      {stage === 'participation' ? (
        <>
          <BackButton stage="participation" onStageChange={onStageChange} />
          {participationCommitted && !dirty ? (
            <Button variant="contained" endIcon={<ForwardIcon />} onClick={() => onStageChange('promote')} sx={{ fontWeight: 800 }}>
              Continue to promotion
            </Button>
          ) : (
            <>
              <Button
                variant="outlined"
                startIcon={<SaveIcon />}
                disabled={!canEdit || !dirty || !hasSavedFitAssessment || decisionPending || decisionRecordLocked}
                onClick={onSaveDraft}
              >
                Save draft
              </Button>
              <Button
                variant="contained"
                color={fullNoBid ? 'warning' : 'primary'}
                disabled={!canCommit || decisionRecordLocked}
                onClick={onCommit}
                sx={{ fontWeight: 800 }}
              >
                {decisionPending ? 'Saving…' : fullNoBid ? 'Commit full no-bid' : 'Commit participation'}
              </Button>
            </>
          )}
        </>
      ) : null}

      {stage === 'promote' ? (
        <>
          <BackButton stage="promote" onStageChange={onStageChange} />
          {!alreadyPromoted && !fullNoBidClosed ? (
            <Button
              variant="contained"
              color="success"
              startIcon={promotionPending ? <CircularProgress size={16} color="inherit" /> : <PromoteIcon />}
              disabled={!canPromote || promotionBlocked || promotionPending}
              onClick={onPromote}
              sx={{ fontWeight: 900 }}
            >
              {promotionPending ? 'Promoting…' : `Promote ${approvedLineCount} line${approvedLineCount === 1 ? '' : 's'} to RFQ`}
            </Button>
          ) : null}
        </>
      ) : null}
    </Stack>
  </Paper>
);

export default WorkbenchStageActions;
