import React from 'react';
import { Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack, Typography } from '@mui/material';
import { lineWord } from './decideRules';

/** What pressing "Yes" will do to the request's status, as far as the page knows it. */
export type QualificationOutlook = 'transition' | 'already' | 'checking' | 'unknown';

// Said before Yes is pressed, so a status move is what Yes will do, never a fact about the request
// now: the request is not qualified until the person says yes.
export const QUALIFICATION_SENTENCES: Readonly<Record<QualificationOutlook, string>> = {
  transition: 'Yes also marks the request qualified.',
  already: 'The request is already qualified.',
  // While the first status read is still on its way, Yes runs with no status to move from, exactly as
  // after a failed read. Saying "couldn't read" then would be false: nothing has failed yet.
  checking: "Nexora is still reading the request's status, so Yes won't mark it qualified. If it isn't qualified already, the RFQ won't be created.",
  unknown: "Nexora couldn't read the request's status, so it isn't marked qualified here. If it isn't qualified already, the RFQ won't be created.",
};

/** "3 of 4 lines go into the RFQ. 1 is left out." */
export const linesIntoRfqSentence = (bidCount: number, lineCount: number): string => {
  const leftOut = lineCount - bidCount;
  const into = `${bidCount} of ${lineCount} ${lineWord(lineCount)} ${bidCount === 1 ? 'goes' : 'go'} into the RFQ.`;
  return leftOut > 0 ? `${into} ${leftOut} ${leftOut === 1 ? 'is' : 'are'} left out.` : into;
};

export interface CreateRfqConfirmDialogProps {
  open: boolean;
  /** The customer's name, or the request's reference when no customer name is known. */
  customer: string;
  bidCount: number;
  lineCount: number;
  qualification: QualificationOutlook;
  onCancel: () => void;
  onConfirm: () => void;
}

/**
 * The one question before the RFQ is created. One click used to record the concern answer, mark
 * the request qualified, lock the line choices and create the RFQ with nothing in between, beside
 * a sentence that read like saving. This says what the click will do, in the order a person
 * checks it, and asks. It writes nothing itself: "Yes" runs the same chain the button always ran.
 */
const CreateRfqConfirmDialog: React.FC<CreateRfqConfirmDialogProps> = ({
  open, customer, bidCount, lineCount, qualification, onCancel, onConfirm,
}) => (
  <Dialog open={open} onClose={onCancel} fullWidth maxWidth="xs" aria-labelledby="create-rfq-confirm-title">
    <DialogTitle id="create-rfq-confirm-title" sx={{ fontWeight: 800 }}>
      {`Create an RFQ for ${customer}?`}
    </DialogTitle>
    <DialogContent>
      <Stack spacing={1.25}>
        <Typography>{linesIntoRfqSentence(bidCount, lineCount)}</Typography>
        <Typography>{QUALIFICATION_SENTENCES[qualification]}</Typography>
        <Typography>Concern: none raised.</Typography>
        <Typography color="text.secondary">Once the RFQ exists, these choices are locked on this screen.</Typography>
      </Stack>
    </DialogContent>
    <DialogActions sx={{ px: 3, pb: 2 }}>
      <Button onClick={onCancel}>Go back</Button>
      <Button variant="contained" onClick={onConfirm} sx={{ fontWeight: 800 }}>Yes, create the RFQ</Button>
    </DialogActions>
  </Dialog>
);

export default CreateRfqConfirmDialog;
