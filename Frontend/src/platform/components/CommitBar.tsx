import { useEffect, useState } from 'react';
import {
  Alert, Box, Button, Collapse, Dialog, DialogActions, DialogContent, DialogTitle,
  Table, TableBody, TableCell, TableRow, TextField, Typography,
} from '@mui/material';
import type { StagedChange } from './useStagedChanges';

/**
 * The one place a change to a customer leaves the screen.
 *
 * There was exactly one commit bar in the whole console — on the Modules tab — and sixty-seven
 * other mutations that each saved on their own button with no shared dirty state. This is that
 * pattern made general, and it is deliberately the ONLY save affordance on the customer page:
 * a screen with two save buttons is a screen where somebody saves one of them.
 *
 * Three things it insists on, each because its absence was a real defect:
 *
 *  - **Review before commit.** The dialog shows before → after for every field. The console used
 *    to apply money-affecting changes with no preview of what they changed; only the purge screen
 *    showed consequences, and only because deletion forced the question.
 *  - **One reason for the whole act.** Not one per section. The reason is what an auditor reads a
 *    year later, and asking for it once per slice produced "fix", "fix", "fix".
 *  - **It says what happens on partial failure.** The commit calls several audited endpoints
 *    because they carry different authorities; if one fails, the operator is told which landed.
 *    Silence there is how "I saved it" becomes false.
 */
export interface CommitBarProps<T> {
  changes: StagedChange<T>[];
  /** Blocks the commit with a reason the operator can act on, e.g. an invalid field. */
  blockedReason?: string | null;
  busy?: boolean;
  minReasonLength?: number;
  onCommit: (reason: string) => void;
  onDiscard: () => void;
}

export default function CommitBar<T>({
  changes, blockedReason, busy = false, minReasonLength = 10, onCommit, onDiscard,
}: CommitBarProps<T>) {
  const [reviewing, setReviewing] = useState(false);
  const [reason, setReason] = useState('');
  const [touched, setTouched] = useState(false);

  const dirty = changes.length > 0;

  // When the commit lands, the caller rebases and `changes` empties. Close on that rather than on
  // the click: the dialog used to stay open over a successful save, so the operator pressed Save
  // a second time and got a stale-write conflict describing their own edit as somebody else's.
  useEffect(() => {
    if (!dirty && reviewing && !busy) { setReviewing(false); setReason(''); setTouched(false); }
  }, [dirty, reviewing, busy]);
  const reasonTooShort = reason.trim().length < minReasonLength;

  return (
    <>
      <Collapse in={dirty}>
        <Box
          sx={{
            position: 'sticky', bottom: 0, zIndex: 3, mt: 3,
            px: 2.5, py: 1.75, borderRadius: 1,
            border: '1px solid', borderColor: 'primary.main',
            bgcolor: 'background.paper',
            boxShadow: '0 -6px 24px -12px rgba(0,0,0,0.45)',
            display: 'flex', alignItems: 'center', gap: 2, flexWrap: 'wrap',
          }}
        >
          <Typography sx={{ fontWeight: 700, flex: 1 }}>
            {changes.length} unsaved {changes.length === 1 ? 'change' : 'changes'}
          </Typography>
          <Button color="inherit" onClick={onDiscard} disabled={busy}>Discard</Button>
          <Button
            variant="contained"
            onClick={() => setReviewing(true)}
            disabled={busy || Boolean(blockedReason)}
          >
            Review and save
          </Button>
          {blockedReason && (
            <Typography variant="caption" color="error" sx={{ flexBasis: '100%' }}>
              {blockedReason}
            </Typography>
          )}
        </Box>
      </Collapse>

      <Dialog open={reviewing} onClose={() => !busy && setReviewing(false)} maxWidth="sm" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Save these changes?</DialogTitle>
        <DialogContent>
          <Table size="small" sx={{ mb: 2 }}>
            <TableBody>
              {changes.map((c) => (
                <TableRow key={String(c.field)}>
                  <TableCell sx={{ fontWeight: 600, width: '38%', border: 0, pl: 0 }}>{c.label}</TableCell>
                  <TableCell sx={{ border: 0, color: 'text.disabled', textDecoration: 'line-through' }}>
                    {c.before}
                  </TableCell>
                  <TableCell sx={{ border: 0, fontWeight: 700 }}>{c.after}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>

          <TextField
            label="Why" fullWidth multiline minRows={2} required
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            onBlur={() => setTouched(true)}
            error={touched && reasonTooShort}
            helperText={
              touched && reasonTooShort
                ? `At least ${minReasonLength} characters — this is what somebody reads a year from now.`
                : 'Recorded on the audit trail against your operator account, for every change above.'
            }
          />

          <Alert severity="info" sx={{ mt: 2 }}>
            Each group is saved through its own audited endpoint, because they carry different
            authorities. If one is refused you will be told which — nothing is reported as saved
            unless it was.
          </Alert>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button onClick={() => setReviewing(false)} disabled={busy}>Cancel</Button>
          <Button
            variant="contained"
            disabled={busy || reasonTooShort}
            onClick={() => { setTouched(true); if (!reasonTooShort) onCommit(reason.trim()); }}
          >
            {busy ? 'Saving…' : `Save ${changes.length} ${changes.length === 1 ? 'change' : 'changes'}`}
          </Button>
        </DialogActions>
      </Dialog>
    </>
  );
}
