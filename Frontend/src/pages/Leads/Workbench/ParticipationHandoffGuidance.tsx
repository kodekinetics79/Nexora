import React from 'react';
import { Alert, AlertTitle } from '@mui/material';

interface ParticipationHandoffGuidanceProps {
  canEdit: boolean;
  isManager: boolean;
  participationStatus: string;
}

/** Pilot-safe maker-checker guidance backed only by persisted drafts and managed scope. */
export const ParticipationHandoffGuidance: React.FC<ParticipationHandoffGuidanceProps> = ({
  canEdit,
  isManager,
  participationStatus,
}) => {
  if (!canEdit) return null;

  if (!isManager) {
    return (
      <Alert severity="info" sx={{ mb: 1.5 }}>
        <AlertTitle>Prepare the participation scope for manager review</AlertTitle>
        Save your fit assessment and participation choices as a draft. The persisted draft will be
        available to your assigned manager in their managed scope. A Manager, Admin, or Owner must
        inspect it, commit the decision, and promote the RFQ.
      </Alert>
    );
  }

  if (participationStatus === 'DRAFT') {
    return (
      <Alert severity="warning" sx={{ mb: 1.5 }}>
        <AlertTitle>Participation draft requires manager review</AlertTitle>
        This scope was prepared as a draft. Inspect the source evidence, Bid quantities and terms,
        warning acknowledgements, and every No-bid exclusion before committing it.
      </Alert>
    );
  }

  return null;
};

export default ParticipationHandoffGuidance;
