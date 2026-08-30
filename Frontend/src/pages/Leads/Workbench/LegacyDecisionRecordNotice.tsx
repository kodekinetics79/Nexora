import React from 'react';
import { Alert, AlertTitle, Button } from '@mui/material';

interface LegacyDecisionRecordNoticeProps {
  message: string;
  actionLabel?: string | null;
  onOpenRfq?: () => void;
}

const LegacyDecisionRecordNotice: React.FC<LegacyDecisionRecordNoticeProps> = ({
  message,
  actionLabel,
  onOpenRfq,
}) => (
  <Alert
    severity="info"
    sx={{ mb: 1.5 }}
    action={actionLabel && onOpenRfq ? (
      <Button color="inherit" onClick={onOpenRfq}>{actionLabel}</Button>
    ) : undefined}
  >
    <AlertTitle>Historical RFQ decision record</AlertTitle>
    {message}
  </Alert>
);

export default LegacyDecisionRecordNotice;
