import React from 'react';
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  MenuItem,
  Paper,
  Select,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  DataGrid,
  type GridColDef,
  type GridPaginationModel,
  type GridRowId,
  type GridRowSelectionModel,
} from '@mui/x-data-grid';
import type {
  DecisionReasonCodeDTO,
  LeadDecisionLineDTO,
  LineParticipationDecision,
} from '../../../api/services/leadDecisionService';
import type { DecisionMap } from './workbenchRules';
import GovernedDecisionDialog from './GovernedDecisionDialog';
import {
  catalogPolicyLabel,
  catalogWarningSummary,
  parseCatalogWarningSnapshot,
} from './catalogWarningPresentation';

interface LeadValidationGridProps {
  lines: LeadDecisionLineDTO[];
  decisions: DecisionMap;
  reasonCodes: DecisionReasonCodeDTO[];
  unitOptions: Array<{ code: string; label: string }>;
  currencyOptions: Array<{ code: string; label: string }>;
  readOnly?: boolean;
  onDecisionsChange: (next: DecisionMap) => void;
}

interface DialogState {
  decision: Extract<LineParticipationDecision, 'NoBid' | 'Clarify'>;
  revisionLineIds: number[];
  initialReasonCode?: string;
  initialNote?: string;
}

interface WarningDialogState {
  revisionLineIds: number[];
  detail: string;
  note: string;
}

const verificationLabel = (status: string): string => {
  if (status === 'VERIFIED') return 'Source verified';
  if (status === 'NEEDS_CHECK') return 'Needs check';
  if (status === 'MISSING_SOURCE') return 'Missing source';
  if (status === 'MACHINE_SUGGESTION') return 'Machine suggestion';
  return status.replaceAll('_', ' ').toLowerCase().replace(/^./, (value) => value.toUpperCase());
};

const LeadValidationGrid: React.FC<LeadValidationGridProps> = ({
  lines, decisions, reasonCodes, unitOptions, currencyOptions, readOnly = false, onDecisionsChange,
}) => {
  const [selection, setSelection] = React.useState<GridRowSelectionModel>({ type: 'include', ids: new Set<GridRowId>() });
  const [pagination, setPagination] = React.useState<GridPaginationModel>({ page: 0, pageSize: 50 });
  const [dialog, setDialog] = React.useState<DialogState | null>(null);
  const [warningDialog, setWarningDialog] = React.useState<WarningDialogState | null>(null);

  const selectedRevisionLineIds = React.useMemo(() => {
    const selectedIds = selection.type === 'exclude'
      ? lines.filter((line) => !selection.ids.has(line.id)).map((line) => line.id)
      : lines.filter((line) => selection.ids.has(line.id)).map((line) => line.id);
    const selectedSet = new Set(selectedIds);
    return lines.filter((line) => selectedSet.has(line.id)).map((line) => line.revisionLineId);
  }, [lines, selection]);

  const applyDecision = React.useCallback((revisionLineIds: number[], decision: LineParticipationDecision, reasonCode?: string, note?: string) => {
    const target = new Set(revisionLineIds);
    const next: DecisionMap = { ...decisions };
    for (const line of lines) {
      if (!target.has(line.revisionLineId)) continue;
      const existing = next[line.revisionLineId] ?? { decision: 'Pending' as const };
      next[line.revisionLineId] = decision === 'Bid'
        ? { ...existing, decision, reasonCode: undefined, note: note ?? existing.note }
        : decision === 'Pending'
          ? { ...existing, decision, reasonCode: undefined, note: undefined }
          : { ...existing, decision, reasonCode, note };
    }
    onDecisionsChange(next);
  }, [decisions, lines, onDecisionsChange]);

  const requestDecision = React.useCallback((line: LeadDecisionLineDTO, decision: LineParticipationDecision) => {
    if (decision === 'NoBid' || decision === 'Clarify') {
      const current = decisions[line.revisionLineId];
      setDialog({
        decision,
        revisionLineIds: [line.revisionLineId],
        initialReasonCode: current?.decision === decision ? current.reasonCode : undefined,
        initialNote: current?.decision === decision ? current.note : undefined,
      });
      return;
    }
    if (decision === 'Bid' && line.needsAttention) {
      setWarningDialog({
        revisionLineIds: [line.revisionLineId],
        detail: line.attentionReason || 'This line needs human review before it can be included in the bid.',
        note: decisions[line.revisionLineId]?.note ?? '',
      });
      return;
    }
    applyDecision([line.revisionLineId], decision);
  }, [applyDecision, decisions]);

  const updateCommercialField = React.useCallback((revisionLineId: number, patch: Partial<DecisionMap[number]>) => {
    onDecisionsChange({
      ...decisions,
      [revisionLineId]: { ...(decisions[revisionLineId] ?? { decision: 'Pending' }), ...patch },
    });
  }, [decisions, onDecisionsChange]);

  const columns = React.useMemo<GridColDef<LeadDecisionLineDTO>[]>(() => [
    {
      field: 'lineItemNo',
      headerName: 'Line',
      width: 80,
      sortable: false,
      renderCell: ({ row }) => <Typography variant="body2" sx={{ fontWeight: 800 }}>{row.lineItemNo || '—'}</Typography>,
    },
    {
      field: 'participation',
      headerName: 'Participation',
      width: 165,
      sortable: false,
      renderCell: ({ row }) => {
        const current = decisions[row.revisionLineId]?.decision ?? 'Pending';
        if (readOnly) return <Chip size="small" label={current === 'NoBid' ? 'No-bid' : current} variant={current === 'Pending' ? 'outlined' : 'filled'} />;
        return (
          <Select
            size="small"
            value={current}
            onChange={(event) => requestDecision(row, event.target.value as LineParticipationDecision)}
            inputProps={{ 'aria-label': `Participation decision for line ${row.lineItemNo || row.id}` }}
            sx={{ minWidth: 135, fontSize: '0.78rem' }}
          >
            <MenuItem value="Pending">Undecided</MenuItem>
            <MenuItem value="Bid">Bid</MenuItem>
            <MenuItem value="NoBid">No-bid…</MenuItem>
            <MenuItem value="Clarify">Clarify…</MenuItem>
          </Select>
        );
      },
    },
    {
      field: 'quantity',
      headerName: 'Quote values',
      width: 260,
      sortable: false,
      renderCell: ({ row }) => readOnly ? (
        <Typography variant="body2" sx={{ fontWeight: 800 }}>
          {decisions[row.revisionLineId]?.quantity ?? row.quantity ?? 'Missing'} {decisions[row.revisionLineId]?.unitOfMeasure || row.unitOfMeasure || ''}
          {' · '}{decisions[row.revisionLineId]?.currency || row.currency || 'No currency'}
        </Typography>
      ) : (
        <Stack direction="row" spacing={0.5}>
          <TextField size="small" type="number" label="Qty" sx={{ width: 82 }}
            value={decisions[row.revisionLineId]?.quantity ?? ''}
            slotProps={{ htmlInput: { min: 1, step: 1, 'aria-label': `Quantity for line ${row.lineItemNo || row.id}` } }}
            onClick={(event) => event.stopPropagation()}
            onChange={(event) => updateCommercialField(row.revisionLineId, {
              quantity: event.target.value ? Number(event.target.value) : undefined,
            })} />
          <TextField select size="small" label="UOM" sx={{ width: 92 }}
            value={decisions[row.revisionLineId]?.unitOfMeasure ?? ''}
            slotProps={{ select: { inputProps: { 'aria-label': `Unit of measure for line ${row.lineItemNo || row.id}` } } }}
            onClick={(event) => event.stopPropagation()}
            onChange={(event) => updateCommercialField(row.revisionLineId, { unitOfMeasure: event.target.value || undefined })}>
            <MenuItem value="">Select</MenuItem>
            {unitOptions.map((option) => <MenuItem key={option.code} value={option.code}>{option.code} · {option.label}</MenuItem>)}
          </TextField>
          <TextField select size="small" label="CCY" sx={{ width: 92 }}
            value={decisions[row.revisionLineId]?.currency ?? ''}
            slotProps={{ select: { inputProps: { 'aria-label': `Currency for line ${row.lineItemNo || row.id}` } } }}
            onClick={(event) => event.stopPropagation()}
            onChange={(event) => updateCommercialField(row.revisionLineId, { currency: event.target.value || undefined })}>
            <MenuItem value="">Select</MenuItem>
            {currencyOptions.map((option) => <MenuItem key={option.code} value={option.code}>{option.code} · {option.label}</MenuItem>)}
          </TextField>
        </Stack>
      ),
    },
    {
      field: 'sourceText',
      headerName: 'Customer request / source',
      minWidth: 260,
      flex: 1.3,
      sortable: false,
      renderCell: ({ row }) => {
        const fields = row.sourceFields?.length ? row.sourceFields : row.sourceText
          ? [{ field: row.sourceField || 'Source field', rawValue: row.sourceText, sourceAddress: row.sourceAddress }]
          : [];
        return (
        <Tooltip title={fields.length
          ? fields.map((field) => `${field.field}${field.sourceAddress ? ` at ${field.sourceAddress}` : ''}: ${field.rawValue}`).join('\n')
          : 'No exact source-field value was captured'}>
          <Box sx={{ minWidth: 0 }}>
            {fields.length ? fields.map((field) => (
              <Typography key={`${field.field}-${field.sourceAddress || ''}`} variant="caption" noWrap sx={{ display: 'block' }}>
                <Box component="span" sx={{ color: 'text.secondary', fontWeight: 800 }}>{field.field}{field.sourceAddress ? ` · ${field.sourceAddress}` : ''}: </Box>
                {field.rawValue}
              </Typography>
            )) : <Typography variant="body2" color="error.main">No exact source-field value</Typography>}
          </Box>
        </Tooltip>
        );
      },
    },
    {
      field: 'productName',
      headerName: 'Normalized item',
      minWidth: 220,
      flex: 1,
      renderCell: ({ row }) => (
        <Box sx={{ minWidth: 0 }}>
          <Typography variant="body2" sx={{ fontWeight: 800 }} noWrap>{row.productName || 'Unresolved product'}</Typography>
          <Typography variant="caption" color="text.secondary" noWrap sx={{ display: 'block' }}>{row.description || row.manufacturerPartNumber || 'No description'}</Typography>
        </Box>
      ),
    },
    {
      field: 'manufacturerPartNumber',
      headerName: 'Manufacturer / part',
      minWidth: 180,
      flex: 0.8,
      renderCell: ({ row }) => (
        <Box sx={{ minWidth: 0 }}>
          <Typography variant="body2" noWrap>{row.manufacturerName || '—'}</Typography>
          <Typography variant="caption" color="text.secondary" noWrap sx={{ display: 'block', fontFamily: 'monospace' }}>{row.manufacturerPartNumber || '—'}</Typography>
        </Box>
      ),
    },
    {
      field: 'catalogResolution',
      headerName: 'Catalog match',
      minWidth: 230,
      flex: 0.9,
      sortable: false,
      renderCell: ({ row }) => {
        if (readOnly) {
          const participation = row.participation;
          const snapshot = parseCatalogWarningSnapshot(participation?.warningSnapshotJson);
          const selected = snapshot.matches.find((match) => match.productId === participation?.productId);
          const selectedLabel = participation?.productId
            ? selected?.productName || selected?.materialCode || `Product #${participation.productId}`
            : 'No catalog product selected';
          return (
            <Tooltip title={snapshot.attentionReason || `Saved decision product: ${selectedLabel}`}>
              <Chip size="small" color={snapshot.needsAttention ? 'warning' : 'success'} variant="outlined"
                label={selectedLabel} />
            </Tooltip>
          );
        }
        return (
        <TextField
          select
          size="small"
          fullWidth
          value={decisions[row.revisionLineId]?.productId ?? ''}
          onChange={(event) => updateCommercialField(row.revisionLineId, {
            productId: event.target.value ? Number(event.target.value) : undefined,
          })}
          onClick={(event) => event.stopPropagation()}
          aria-label={`Catalog product for line ${row.lineItemNo || row.id}`}
          error={row.needsAttention && !decisions[row.revisionLineId]?.productId}
        >
          <MenuItem value="">No catalog product</MenuItem>
          {(row.catalogMatches ?? []).map((match) => (
            <MenuItem key={match.productId} value={match.productId}>
              {match.productName || match.materialCode || `Product #${match.productId}`} · {Math.round(match.score * 100)}%
            </MenuItem>
          ))}
        </TextField>
        );
      },
    },
    {
      field: 'verificationStatus',
      headerName: 'Validation',
      width: 155,
      renderCell: ({ row }) => (
        <Tooltip title={row.verificationDetail || verificationLabel(row.verificationStatus)}>
          <Chip
            tabIndex={0}
            aria-label={`${verificationLabel(row.verificationStatus)}; focus for validation details`}
            size="small"
            variant="outlined"
            color={row.verificationStatus === 'VERIFIED' ? 'success' : row.verificationStatus === 'MISSING_SOURCE' ? 'error' : 'warning'}
            label={verificationLabel(row.verificationStatus)}
          />
        </Tooltip>
      ),
    },
    {
      field: 'governance',
      headerName: 'Decision record',
      minWidth: 240,
      flex: 0.8,
      sortable: false,
      renderCell: ({ row }) => {
        const decision = decisions[row.revisionLineId] ?? { decision: 'Pending' as const };
        if (decision.decision === 'Pending') return <Typography variant="caption" color="text.secondary">Not decided</Typography>;
        const reason = reasonCodes.find((item) => item.code === decision.reasonCode);
        const policy = row.participation?.catalogPolicyVersion || row.catalogPolicyVersion;
        const snapshot = row.participation?.warningSnapshotJson || row.warningSnapshotJson;
        const savedWarning = parseCatalogWarningSnapshot(snapshot);
        const warningSummary = catalogWarningSummary(snapshot, row.attentionReason);
        return (
          <Tooltip title={`${warningSummary} ${catalogPolicyLabel(policy)}`}>
            <Box sx={{ minWidth: 0 }}>
              <Typography variant="caption" sx={{ display: 'block', fontWeight: 800 }} noWrap>
                {decision.decision === 'Bid' ? (savedWarning.needsAttention ? 'Warning acknowledged' : 'Bid approved') : reason?.label || decision.reasonCode || 'Reason missing'}
              </Typography>
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }} noWrap>
                {decision.note || (decision.decision === 'Bid' ? 'No acknowledgement required' : 'No note')}
              </Typography>
              {decision.decision === 'Bid' ? <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }} noWrap>Policy {policy || 'not supplied'}</Typography> : null}
            </Box>
          </Tooltip>
        );
      },
    },
  ], [currencyOptions, decisions, readOnly, reasonCodes, requestDecision, unitOptions, updateCommercialField]);

  const selectionCount = selectedRevisionLineIds.length;

  return (
    <Stack spacing={1.25}>
      {!readOnly && selectionCount > 0 ? (
        <Paper role="region" aria-label="Bulk participation actions" variant="outlined" sx={{ p: 1.25, borderColor: 'primary.main' }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
            <Typography variant="body2" sx={{ fontWeight: 900 }}>{selectionCount} line{selectionCount === 1 ? '' : 's'} selected</Typography>
            <Button size="small" variant="outlined" color="success" onClick={() => {
              const warningLines = lines.filter((line) => selectedRevisionLineIds.includes(line.revisionLineId) && line.needsAttention);
              if (warningLines.length > 0) {
                setWarningDialog({
                  revisionLineIds: selectedRevisionLineIds,
                  detail: `${warningLines.length} selected line${warningLines.length === 1 ? '' : 's'} require warning acknowledgement before bidding.`,
                  note: '',
                });
              } else applyDecision(selectedRevisionLineIds, 'Bid');
            }}>Mark Bid</Button>
            <Button size="small" variant="outlined" color="warning" onClick={() => setDialog({ decision: 'NoBid', revisionLineIds: selectedRevisionLineIds })}>Mark No-bid…</Button>
            <Button size="small" variant="outlined" onClick={() => setDialog({ decision: 'Clarify', revisionLineIds: selectedRevisionLineIds })}>Request clarification…</Button>
            <Button size="small" color="inherit" onClick={() => applyDecision(selectedRevisionLineIds, 'Pending')}>Clear decisions</Button>
            <Box sx={{ flex: 1 }} />
            <Button size="small" color="inherit" onClick={() => setSelection({ type: 'include', ids: new Set<GridRowId>() })}>Clear selection</Button>
          </Stack>
        </Paper>
      ) : null}

      <Typography variant="caption" color="text.secondary" sx={{ display: { xs: 'block', md: 'none' }, px: 0.5 }}>
        Swipe or scroll the table horizontally to review catalog match, quote values, validation, participation, and the decision record.
      </Typography>

      <Paper variant="outlined" sx={{ height: { xs: 560, md: 640 }, width: '100%', overflow: 'hidden' }}>
        <DataGrid
          rows={lines}
          columns={columns}
          getRowId={(row) => row.id}
          checkboxSelection={!readOnly}
          disableRowSelectionOnClick
          rowSelectionModel={selection}
          onRowSelectionModelChange={setSelection}
          paginationModel={pagination}
          onPaginationModelChange={setPagination}
          pageSizeOptions={[25, 50, 100]}
          rowHeight={76}
          columnHeaderHeight={44}
          hideFooterSelectedRowCount
          sx={{
            border: 0,
            '& .MuiDataGrid-cell': { py: 0.5 },
            '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 900 },
          }}
        />
      </Paper>

      {dialog ? (
        <GovernedDecisionDialog
          open
          decision={dialog.decision}
          lineCount={dialog.revisionLineIds.length}
          reasonCodes={reasonCodes}
          initialReasonCode={dialog.initialReasonCode}
          initialNote={dialog.initialNote}
          onCancel={() => setDialog(null)}
          onConfirm={(reasonCode, note) => {
            applyDecision(dialog.revisionLineIds, dialog.decision, reasonCode, note);
            setDialog(null);
          }}
        />
      ) : null}
      <Dialog open={Boolean(warningDialog)} onClose={() => setWarningDialog(null)} maxWidth="sm" fullWidth>
        <DialogTitle>Acknowledge line warning</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            <Alert severity="warning">{warningDialog?.detail}</Alert>
            <TextField
              required
              multiline
              minRows={3}
              label="Human review note"
              value={warningDialog?.note ?? ''}
              onChange={(event) => setWarningDialog((current) => current ? { ...current, note: event.target.value.slice(0, 1000) } : null)}
              helperText="Record what you checked or corrected. At least 5 characters."
            />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setWarningDialog(null)}>Cancel</Button>
          <Button variant="contained" disabled={(warningDialog?.note.trim().length ?? 0) < 5} onClick={() => {
            if (!warningDialog) return;
            applyDecision(warningDialog.revisionLineIds, 'Bid', undefined, warningDialog.note.trim());
            setWarningDialog(null);
          }}>Acknowledge and mark Bid</Button>
        </DialogActions>
      </Dialog>
    </Stack>
  );
};

export default LeadValidationGrid;
