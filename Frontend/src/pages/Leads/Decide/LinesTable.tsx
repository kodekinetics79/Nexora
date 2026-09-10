import React from 'react';
import {
  Box,
  Chip,
  FormControl,
  InputLabel,
  Link,
  MenuItem,
  Select,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material';
import type {
  DecisionReasonCodeDTO,
  LeadDecisionLineDTO,
} from '../../../api/services/leadDecisionService';
import type { DecisionMap, EditableLineDecision } from '../Workbench/workbenchRules';
import { catalogWarningSummary } from '../Workbench/catalogWarningPresentation';
import { lineLabel, lineTitle } from './decideRules';

interface Option { code: string; label: string }

export interface LinesTableProps {
  leadId: number;
  lines: LeadDecisionLineDTO[];
  decisions: DecisionMap;
  unitOptions: Option[];
  currencyOptions: Option[];
  reasonCodes: DecisionReasonCodeDTO[];
  readOnly: boolean;
  onChange: (revisionLineId: number, patch: Partial<EditableLineDecision>) => void;
  /** Opens the document check beside the lines, focused on the given line when there is one. */
  onOpenDocument: (line?: LeadDecisionLineDTO) => void;
}

const numberOrEmpty = (value: number | undefined): string =>
  value == null || !Number.isFinite(value) ? '' : String(value);

/**
 * One row per line the customer asked for. Only what is missing becomes a control: a line that
 * arrived with a quantity, a unit and a currency reads as text; a line missing its currency shows
 * a highlighted picker on that one cell. Skipping asks why, quoting a flagged line asks how the
 * warning was handled, and nothing else interrupts.
 */
const LinesTable: React.FC<LinesTableProps> = ({
  leadId,
  lines,
  decisions,
  unitOptions,
  currencyOptions,
  reasonCodes,
  readOnly,
  onChange,
  onOpenDocument,
}) => {
  const skipReasons = reasonCodes.filter((reason) => reason.appliesTo.includes('NoBid'));
  const unitCodes = new Set(unitOptions.map((option) => option.code.toUpperCase()));
  const currencyCodes = new Set(currencyOptions.map((option) => option.code.toUpperCase()));

  return (
    <TableContainer sx={{ overflowX: 'auto' }}>
      <Table size="small" aria-label="Lines the customer asked for" sx={{ minWidth: 720 }}>
        <TableHead>
          <TableRow>
            <TableCell sx={{ fontWeight: 700 }}>Item</TableCell>
            <TableCell sx={{ fontWeight: 700, width: 200 }}>Quantity</TableCell>
            <TableCell sx={{ fontWeight: 700, width: 140 }}>Price in</TableCell>
            <TableCell sx={{ fontWeight: 700, width: 170 }} align="right">Quote it?</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {lines.map((line) => {
            const label = lineLabel(line);
            const decision = decisions[line.revisionLineId];
            const choice = decision?.decision ?? 'Pending';
            const quoting = choice === 'Bid';
            const skipping = choice === 'NoBid';
            const quantityMissing = quoting && (!decision?.quantity || decision.quantity <= 0);
            const unitMissing = quoting && !(decision?.unitOfMeasure && (unitCodes.size === 0 || unitCodes.has(decision.unitOfMeasure.toUpperCase())));
            const currencyMissing = quoting && !(decision?.currency && (currencyCodes.size === 0 || currencyCodes.has(decision.currency.toUpperCase())));
            const reasonMissing = skipping && !decision?.reasonCode?.trim();
            const attentionOpen = quoting && Boolean(line.needsAttention) && (decision?.note?.trim().length ?? 0) < 5;
            const unverified = quoting && line.verificationStatus !== 'VERIFIED';
            const detail = [line.manufacturerPartNumber, line.manufacturerName].filter(Boolean).join(' · ');

            return (
              <React.Fragment key={line.revisionLineId}>
                <TableRow
                  hover
                  sx={{ '& > td': { borderBottom: 0, verticalAlign: 'top', pt: 1.5 }, opacity: skipping ? 0.7 : 1 }}
                >
                  <TableCell>
                    <Typography sx={{ fontWeight: 600, textDecoration: skipping ? 'line-through' : 'none', overflowWrap: 'anywhere' }}>
                      {lineTitle(line)}
                    </Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                      {[`Line ${label}`, detail].filter(Boolean).join(' · ')}
                    </Typography>
                    {choice === 'Clarify' ? (
                      <Chip size="small" label="Waiting on the customer" color="warning" variant="outlined" sx={{ mt: 0.5 }} />
                    ) : null}
                  </TableCell>
                  <TableCell>
                    {readOnly || !quoting ? (
                      <Typography sx={{ fontVariantNumeric: 'tabular-nums' }}>
                        {numberOrEmpty(decision?.quantity ?? line.quantity ?? undefined) || '—'} {decision?.unitOfMeasure ?? line.unitOfMeasure ?? ''}
                      </Typography>
                    ) : (
                      <Stack direction="row" spacing={1}>
                        <TextField
                          size="small"
                          type="number"
                          value={numberOrEmpty(decision?.quantity)}
                          error={quantityMissing}
                          slotProps={{ htmlInput: { min: 0, step: 'any', 'aria-label': `Quantity for line ${label}` } }}
                          onChange={(event) => {
                            const next = Number(event.target.value);
                            onChange(line.revisionLineId, { quantity: event.target.value === '' || !Number.isFinite(next) ? undefined : next });
                          }}
                          sx={{ width: 96 }}
                        />
                        {unitOptions.length > 0 ? (
                          <FormControl size="small" error={unitMissing} sx={{ minWidth: 88 }}>
                            <Select
                              value={decision?.unitOfMeasure ?? ''}
                              displayEmpty
                              inputProps={{ 'aria-label': `Unit for line ${label}` }}
                              onChange={(event) => onChange(line.revisionLineId, { unitOfMeasure: event.target.value || undefined })}
                            >
                              <MenuItem value=""><em>Unit</em></MenuItem>
                              {unitOptions.map((option) => (
                                <MenuItem key={option.code} value={option.code}>{option.code}</MenuItem>
                              ))}
                            </Select>
                          </FormControl>
                        ) : (
                          <TextField
                            size="small"
                            value={decision?.unitOfMeasure ?? ''}
                            error={unitMissing}
                            slotProps={{ htmlInput: { 'aria-label': `Unit for line ${label}` } }}
                            onChange={(event) => onChange(line.revisionLineId, { unitOfMeasure: event.target.value || undefined })}
                            sx={{ width: 88 }}
                          />
                        )}
                      </Stack>
                    )}
                  </TableCell>
                  <TableCell>
                    {readOnly || !quoting ? (
                      <Typography>{decision?.currency ?? line.currency ?? '—'}</Typography>
                    ) : (
                      <FormControl size="small" error={currencyMissing} sx={{ minWidth: 120 }}>
                        <Select
                          value={decision?.currency ?? ''}
                          displayEmpty
                          renderValue={(value: string) => value || <Box component="em" sx={{ color: 'text.secondary' }}>Not stated</Box>}
                          inputProps={{ 'aria-label': `Currency for line ${label}` }}
                          onChange={(event) => onChange(line.revisionLineId, { currency: event.target.value || undefined })}
                        >
                          {currencyOptions.length === 0 && decision?.currency ? (
                            <MenuItem value={decision.currency}>{decision.currency}</MenuItem>
                          ) : null}
                          {currencyOptions.map((option) => (
                            <MenuItem key={option.code} value={option.code}>{option.code}</MenuItem>
                          ))}
                        </Select>
                      </FormControl>
                    )}
                  </TableCell>
                  <TableCell align="right">
                    {readOnly ? (
                      <Chip
                        size="small"
                        label={quoting ? 'Quoted' : skipping ? 'Skipped' : 'Undecided'}
                        color={quoting ? 'primary' : 'default'}
                        variant={quoting ? 'filled' : 'outlined'}
                      />
                    ) : (
                      <ToggleButtonGroup
                        exclusive
                        size="small"
                        value={quoting ? 'Bid' : skipping ? 'NoBid' : null}
                        aria-label={`Quote or skip line ${label}`}
                        onChange={(_event, next: 'Bid' | 'NoBid' | null) => {
                          if (!next) return;
                          onChange(line.revisionLineId, next === 'Bid'
                            ? { decision: 'Bid', reasonCode: undefined }
                            : { decision: 'NoBid' });
                        }}
                      >
                        <ToggleButton value="Bid" sx={{ px: 2, fontWeight: 700 }}>Quote</ToggleButton>
                        <ToggleButton value="NoBid" sx={{ px: 2, fontWeight: 700 }}>Skip</ToggleButton>
                      </ToggleButtonGroup>
                    )}
                  </TableCell>
                </TableRow>
                {!readOnly && (skipping || attentionOpen || unverified || (quoting && Boolean(line.needsAttention))) ? (
                  <TableRow>
                    <TableCell colSpan={4} sx={{ pt: 0, pb: 1.5 }}>
                      <Stack spacing={1} sx={{ pl: { sm: 2 } }}>
                        {skipping ? (
                          <FormControl size="small" error={reasonMissing} sx={{ maxWidth: 420 }}>
                            <InputLabel id={`skip-reason-${line.revisionLineId}`}>Why skip line {label}</InputLabel>
                            <Select
                              labelId={`skip-reason-${line.revisionLineId}`}
                              label={`Why skip line ${label}`}
                              value={decision?.reasonCode ?? ''}
                              onChange={(event) => onChange(line.revisionLineId, { reasonCode: event.target.value || undefined })}
                            >
                              {skipReasons.map((reason) => (
                                <MenuItem key={reason.code} value={reason.code}>{reason.label}</MenuItem>
                              ))}
                            </Select>
                          </FormControl>
                        ) : null}
                        {quoting && line.needsAttention ? (
                          <Box>
                            <Typography variant="body2" color="warning.main" sx={{ fontWeight: 600 }}>
                              {catalogWarningSummary(line.warningSnapshotJson, line.attentionReason)}
                            </Typography>
                            <TextField
                              size="small"
                              fullWidth
                              label={`How you handled it (line ${label})`}
                              value={decision?.note ?? ''}
                              error={attentionOpen}
                              onChange={(event) => onChange(line.revisionLineId, { note: event.target.value.slice(0, 1000) || undefined })}
                              sx={{ mt: 1, maxWidth: 560 }}
                            />
                          </Box>
                        ) : null}
                        {unverified ? (
                          <Typography variant="body2" color="warning.main">
                            {line.verificationStatus === 'MISSING_SOURCE'
                              ? 'No source document is on file for this line, so it cannot be quoted.'
                              : (
                                <>
                                  Nexora is not sure it read this line correctly.{' '}
                                  <Link component="button" type="button" onClick={() => onOpenDocument(line)} sx={{ fontWeight: 700, verticalAlign: 'baseline' }}>
                                    Check the document
                                  </Link>
                                </>
                              )}
                          </Typography>
                        ) : null}
                      </Stack>
                    </TableCell>
                  </TableRow>
                ) : null}
              </React.Fragment>
            );
          })}
          {lines.length === 0 ? (
            <TableRow>
              <TableCell colSpan={4}>
                <Typography color="text.secondary">
                  Nexora read no lines from this request. Check the document, or ask the customer for a list.
                </Typography>
                <Link component="button" type="button" onClick={() => onOpenDocument()} sx={{ fontWeight: 700 }}>
                  Check the document for lead {leadId}
                </Link>
              </TableCell>
            </TableRow>
          ) : null}
        </TableBody>
      </Table>
    </TableContainer>
  );
};

export default LinesTable;
