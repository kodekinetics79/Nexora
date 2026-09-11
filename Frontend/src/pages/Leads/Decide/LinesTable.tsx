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
  TablePagination,
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
import { lineLabel, lineNeeds, lineTitle, type LineNeedKind } from './decideRules';

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
  /** Lines drawn per page; the default suits a real bid list, tests use fewer. */
  linesPerPage?: number;
}

/** Lines drawn at once. Enough to work through, few enough to draw instantly. */
export const LINES_PER_PAGE = 100;

const numberOrEmpty = (value: number | undefined): string =>
  value == null || !Number.isFinite(value) ? '' : String(value);

interface LineRowProps {
  line: LeadDecisionLineDTO;
  decision: EditableLineDecision | undefined;
  unitOptions: Option[];
  currencyOptions: Option[];
  unitCodes: Set<string>;
  currencyCodes: Set<string>;
  skipReasons: DecisionReasonCodeDTO[];
  readOnly: boolean;
  onChange: LinesTableProps['onChange'];
  onOpenDocument: LinesTableProps['onOpenDocument'];
}

/**
 * One line, memoised: a bid list can run to two thousand lines, and a keystroke in one row
 * must not re-render the other 1,999. What the row highlights is exactly what the next-step
 * sentence will ask for, because both read the same `lineNeeds`.
 */
const LineRow = React.memo(function LineRow({
  line, decision, unitOptions, currencyOptions, unitCodes, currencyCodes, skipReasons, readOnly, onChange, onOpenDocument,
}: LineRowProps) {
  const label = lineLabel(line);
  const choice = decision?.decision ?? 'Pending';
  const quoting = choice === 'Bid';
  const skipping = choice === 'NoBid';
  const needs = new Set<LineNeedKind>(lineNeeds(line, decision, unitCodes, currencyCodes).map((need) => need.kind));
  const unverified = needs.has('source') || needs.has('missing-source');
  // Two numbers can sit on a line and they are not the same thing: the buyer's own material
  // code, and the maker's part number. Each is named so a rep never quotes the wrong one.
  const detail = [
    line.itemMaterialCode ? `Material ${line.itemMaterialCode}` : null,
    line.manufacturerPartNumber
      ? `${line.manufacturerName ? `${line.manufacturerName} ` : ''}P/N ${line.manufacturerPartNumber}`
      : line.manufacturerName,
  ].filter(Boolean).join(' · ');
  const showDetailRow = !readOnly && (skipping || unverified || (quoting && Boolean(line.needsAttention)));

  return (
    <>
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
                error={needs.has('quantity')}
                slotProps={{ htmlInput: { min: 0, step: 'any', 'aria-label': `Quantity for line ${label}` } }}
                onChange={(event) => {
                  const next = Number(event.target.value);
                  onChange(line.revisionLineId, { quantity: event.target.value === '' || !Number.isFinite(next) ? undefined : next });
                }}
                sx={{ width: 96 }}
              />
              <FormControl size="small" error={needs.has('unit') || needs.has('unit-unconfigured')} sx={{ minWidth: 88 }}>
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
            </Stack>
          )}
        </TableCell>
        <TableCell>
          {readOnly || !quoting ? (
            <Typography>{decision?.currency ?? line.currency ?? '—'}</Typography>
          ) : (
            <FormControl size="small" error={needs.has('currency') || needs.has('currency-unconfigured')} sx={{ minWidth: 120 }}>
              <Select
                value={decision?.currency ?? ''}
                displayEmpty
                renderValue={(value: string) => value || <Box component="em" sx={{ color: 'text.secondary' }}>Not stated</Box>}
                inputProps={{ 'aria-label': `Currency for line ${label}` }}
                onChange={(event) => onChange(line.revisionLineId, { currency: event.target.value || undefined })}
              >
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
      {showDetailRow ? (
        <TableRow>
          <TableCell colSpan={4} sx={{ pt: 0, pb: 1.5 }}>
            <Stack spacing={1} sx={{ pl: { sm: 2 } }}>
              {skipping ? (
                <FormControl size="small" error={needs.has('reason')} sx={{ maxWidth: 420 }}>
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
                    error={needs.has('attention')}
                    onChange={(event) => onChange(line.revisionLineId, { note: event.target.value.slice(0, 1000) || undefined })}
                    sx={{ mt: 1, maxWidth: 560 }}
                  />
                </Box>
              ) : null}
              {unverified ? (
                <Typography variant="body2" color="warning.main">
                  {needs.has('missing-source')
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
    </>
  );
});

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
  linesPerPage = LINES_PER_PAGE,
}) => {
  const skipReasons = React.useMemo(() => reasonCodes.filter((reason) => reason.appliesTo.includes('NoBid')), [reasonCodes]);
  const unitCodes = React.useMemo(() => new Set(unitOptions.map((option) => option.code.toUpperCase())), [unitOptions]);
  const currencyCodes = React.useMemo(() => new Set(currencyOptions.map((option) => option.code.toUpperCase())), [currencyOptions]);

  // A page of lines at a time. Every row is a live form (quantity, unit, currency, a reason),
  // and a real bid list runs to 1,500 lines: drawing them all at once froze the browser for
  // minutes after "Quote all". A hundred at a time draws in well under a second, and the page
  // control says where you are. Decisions are kept for every line, on every page.
  const [page, setPage] = React.useState(0);
  const [pageSize, setPageSize] = React.useState(linesPerPage);
  React.useEffect(() => {
    if (page * pageSize >= lines.length) setPage(0);
  }, [lines.length, page, pageSize]);
  const visible = lines.length > pageSize ? lines.slice(page * pageSize, (page + 1) * pageSize) : lines;

  return (
    <TableContainer sx={{ overflowX: 'auto' }}>
      {lines.length > linesPerPage ? (
        <TablePagination
          component="div"
          count={lines.length}
          page={page}
          onPageChange={(_event, next) => setPage(next)}
          rowsPerPage={pageSize}
          onRowsPerPageChange={(event) => { setPageSize(Number(event.target.value)); setPage(0); }}
          rowsPerPageOptions={[linesPerPage, linesPerPage * 2, linesPerPage * 5]}
          labelRowsPerPage="Lines per page"
          labelDisplayedRows={({ from, to, count }) => `Lines ${from}–${to} of ${count}`}
          getItemAriaLabel={(type) => `${type} page of lines`}
        />
      ) : null}
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
          {visible.map((line) => (
            <LineRow
              key={line.revisionLineId}
              line={line}
              decision={decisions[line.revisionLineId]}
              unitOptions={unitOptions}
              currencyOptions={currencyOptions}
              unitCodes={unitCodes}
              currencyCodes={currencyCodes}
              skipReasons={skipReasons}
              readOnly={readOnly}
              onChange={onChange}
              onOpenDocument={onOpenDocument}
            />
          ))}
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
