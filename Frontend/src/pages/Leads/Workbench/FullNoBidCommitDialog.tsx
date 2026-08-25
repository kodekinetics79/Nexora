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
  Paper,
  Select,
  Stack,
  TablePagination,
  TextField,
  Typography,
} from '@mui/material';
import type { DecisionReasonCodeDTO, LeadDecisionLineDTO } from '../../../api/services/leadDecisionService';
import type { DecisionMap } from './workbenchRules';

interface FullNoBidCommitDialogProps {
  open: boolean;
  lineCount: number;
  reasonCodes: DecisionReasonCodeDTO[];
  lines?: LeadDecisionLineDTO[];
  decisions?: DecisionMap;
  onCancel: () => void;
  onConfirm: (reasonCode: string, notes?: string) => void;
}

const FullNoBidCommitDialog: React.FC<FullNoBidCommitDialogProps> = ({
  open, lineCount, reasonCodes, lines = [], decisions = {}, onCancel, onConfirm,
}) => {
  const [reasonCode, setReasonCode] = React.useState('');
  const [notes, setNotes] = React.useState('');
  const [page, setPage] = React.useState(0);
  const governedReasons = reasonCodes.filter((reason) => reason.appliesTo.includes('NoBid'));

  React.useEffect(() => {
    if (!open) return;
    setReasonCode('');
    setNotes('');
    setPage(0);
  }, [open]);

  return (
    <Dialog open={open} onClose={onCancel} fullWidth maxWidth="sm" aria-labelledby="full-no-bid-title">
      <DialogTitle id="full-no-bid-title" sx={{ fontWeight: 900 }}>Commit full no-bid</DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          <Alert severity="warning">
            This closes participation for all {lineCount} lines without creating an RFQ. The header reason is recorded separately from the line-level decisions.
          </Alert>
          {governedReasons.length === 0 ? (
            <Alert severity="error">No governed no-bid reason is configured. This decision cannot be committed with free text alone.</Alert>
          ) : null}
          {lines.length > 0 ? (
            <Stack component="section" spacing={1} sx={{ maxHeight: 260, overflowY: 'auto' }} aria-label="Full no-bid line scope">
              {lines.slice(page * 25, (page + 1) * 25).map((line) => {
                const decision = decisions[line.revisionLineId];
                const lineReason = reasonCodes.find((reason) => reason.code === decision?.reasonCode);
                return (
                  <Paper key={line.revisionLineId} variant="outlined" sx={{ p: 1.25 }}>
                    <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>Line {line.lineItemNo || line.id} · No-bid</Typography>
                    <Typography variant="body2">{lineReason?.label || decision?.reasonCode || 'Line reason missing'}</Typography>
                    {decision?.note ? <Typography variant="caption" color="text.secondary">{decision.note}</Typography> : null}
                  </Paper>
                );
              })}
            </Stack>
          ) : null}
          {lines.length > 0 ? (
            <TablePagination
              component="div"
              count={lines.length}
              page={Math.min(page, Math.max(0, Math.ceil(lines.length / 25) - 1))}
              onPageChange={(_, nextPage) => setPage(nextPage)}
              rowsPerPage={25}
              rowsPerPageOptions={[25]}
              labelRowsPerPage="Lines per page"
            />
          ) : null}
          <FormControl fullWidth required disabled={governedReasons.length === 0}>
            <InputLabel id="full-no-bid-reason-label">Full no-bid reason</InputLabel>
            <Select
              labelId="full-no-bid-reason-label"
              label="Full no-bid reason"
              value={reasonCode}
              onChange={(event) => setReasonCode(event.target.value)}
            >
              {governedReasons.map((reason) => (
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
            value={notes}
            onChange={(event) => setNotes(event.target.value.slice(0, 1000))}
            multiline
            minRows={3}
            helperText={`${notes.length}/1000 · Record the commercial context for the full no-bid.`}
          />
        </Stack>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button color="inherit" onClick={onCancel}>Cancel</Button>
        <Button
          variant="contained"
          color="warning"
          disabled={!reasonCode || governedReasons.length === 0}
          onClick={() => onConfirm(reasonCode, notes.trim() || undefined)}
          sx={{ fontWeight: 800 }}
        >
          Commit full no-bid
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default FullNoBidCommitDialog;
