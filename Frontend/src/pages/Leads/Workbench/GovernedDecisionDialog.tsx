import React from 'react';
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import type {
  DecisionReasonCodeDTO,
  LineParticipationDecision,
} from '../../../api/services/leadDecisionService';

interface GovernedDecisionDialogProps {
  open: boolean;
  decision: Extract<LineParticipationDecision, 'NoBid' | 'Clarify'>;
  lineCount: number;
  reasonCodes: DecisionReasonCodeDTO[];
  initialReasonCode?: string;
  initialNote?: string;
  onCancel: () => void;
  onConfirm: (reasonCode: string, note?: string) => void;
}

const GovernedDecisionDialog: React.FC<GovernedDecisionDialogProps> = ({
  open,
  decision,
  lineCount,
  reasonCodes,
  initialReasonCode = '',
  initialNote = '',
  onCancel,
  onConfirm,
}) => {
  const [reasonCode, setReasonCode] = React.useState(initialReasonCode);
  const [note, setNote] = React.useState(initialNote);
  const availableReasons = reasonCodes.filter((reason) => reason.appliesTo.includes(decision));

  React.useEffect(() => {
    if (!open) return;
    setReasonCode(initialReasonCode);
    setNote(initialNote);
  }, [open, initialReasonCode, initialNote, decision]);

  const label = decision === 'NoBid' ? 'No-bid' : 'Clarification required';
  const noGovernedReasons = availableReasons.length === 0;

  return (
    <Dialog open={open} onClose={onCancel} fullWidth maxWidth="sm" aria-labelledby="governed-decision-title">
      <DialogTitle id="governed-decision-title" sx={{ fontWeight: 900 }}>
        {label} for {lineCount} line{lineCount === 1 ? '' : 's'}
      </DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          <Typography variant="body2" color="text.secondary">
            This decision is recorded against the current immutable Lead revision. It does not create or change an RFQ.
          </Typography>
          {noGovernedReasons ? (
            <Alert severity="error">
              No governed reason is configured for this decision. Ask an administrator to configure the reason list; free text alone cannot authorize it.
            </Alert>
          ) : null}
          <FormControl fullWidth required disabled={noGovernedReasons}>
            <InputLabel id="decision-reason-label">Governed reason</InputLabel>
            <Select
              labelId="decision-reason-label"
              label="Governed reason"
              value={reasonCode}
              onChange={(event) => setReasonCode(event.target.value)}
            >
              {availableReasons.map((reason) => (
                <MenuItem key={reason.code} value={reason.code}>
                  <Stack>
                    <Typography variant="body2" sx={{ fontWeight: 700 }}>{reason.label}</Typography>
                    {reason.description ? <Typography variant="caption" color="text.secondary">{reason.description}</Typography> : null}
                  </Stack>
                </MenuItem>
              ))}
            </Select>
          </FormControl>
          <TextField
            label="Decision note (optional)"
            value={note}
            onChange={(event) => setNote(event.target.value.slice(0, 1000))}
            multiline
            minRows={3}
            helperText={`${note.length}/1000 · Add facts specific to these lines; do not repeat the governed reason.`}
          />
        </Stack>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button color="inherit" onClick={onCancel}>Cancel</Button>
        <Button
          variant="contained"
          color={decision === 'NoBid' ? 'warning' : 'primary'}
          disabled={!reasonCode || noGovernedReasons}
          onClick={() => onConfirm(reasonCode, note.trim() || undefined)}
          sx={{ fontWeight: 800 }}
        >
          Apply decision
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default GovernedDecisionDialog;
