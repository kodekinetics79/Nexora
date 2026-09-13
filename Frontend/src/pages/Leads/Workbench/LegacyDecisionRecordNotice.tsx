import React from 'react';
import { Alert, AlertTitle, Button } from '@mui/material';

interface LegacyDecisionRecordNoticeProps {
  message: string;
  /** The heading; the workbench keeps the historical wording, the Decide screen says it in job words. */
  title?: string;
  actionLabel?: string | null;
  onOpenRfq?: () => void;
}

const LegacyDecisionRecordNotice: React.FC<LegacyDecisionRecordNoticeProps> = ({
  message,
  title = 'Historical RFQ decision record',
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
    <AlertTitle>{title}</AlertTitle>
    {message}
  </Alert>
);

export default LegacyDecisionRecordNotice;
