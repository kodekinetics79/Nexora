import React from 'react';
import {
  Box,
  Button,
  Checkbox,
  Chip,
  Divider,
  FormControl,
  IconButton,
  InputAdornment,
  InputBase,
  Link,
  ListSubheader,
  Menu,
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
  Tooltip,
  Typography,
} from '@mui/material';
import { alpha } from '@mui/material/styles';
import { Close as CloseIcon, Search as SearchIcon } from '@mui/icons-material';
import type {
  DecisionReasonCodeDTO,
  LeadDecisionLineDTO,
  LineParticipationDecision,
} from '../../../api/services/leadDecisionService';
import type { DecisionMap, EditableLineDecision } from '../Workbench/workbenchRules';
import { catalogWarningSummary } from '../Workbench/catalogWarningPresentation';
import { lineLabel, lineNeeds, lineTitle, type LineNeedKind } from './decideRules';
import { readUnit, tenantUnitCode, unitCaption } from './unitRules';

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
  /** Sets the rep's chosen unit on every line marked to quote that has none the tenant quotes in. */
  onBulkUnit?: (code: string) => void;
  /**
   * A request to take the rep to a unit picker: one line's (turning to its page), or the one for
   * every line when `lineId` is absent. A new object is a new request.
   */
  focusUnit?: { lineId?: number; nonce: number } | null;
  /** Lines drawn per page; the default suits a real bid list, tests use fewer. */
  linesPerPage?: number;
  /**
   * What a read-only line reports. `choice`: what was chosen (the default). `rfq`: whether the line
   * went into the RFQ this revision became. `newer-revision`: the lines arrived after the RFQ was
   * created. `legacy`: the RFQ was created before this screen recorded line choices.
   */
  chipMode?: LineChipMode;
  /** The RFQ's number (or `#id`), for the newer-revision call-out. */
  rfqRef?: string | null;
  /** The revision on screen, for the newer-revision call-out. */
  currentRevisionNumber?: number | null;
  /** The revision the RFQ was created from, for the newer-revision call-out. */
  promotedRevisionNumber?: number | null;
  /**
   * One change on many lines: the ticked ones, or every line when `ids` is null. Without it the
   * list offers no ticks and no bulk buttons.
   */
  onApply?: (ids: number[] | null, patch: Partial<EditableLineDecision>) => void;
  /** The page's own controls for the lines (Download Excel, Check against the document). */
  toolbarEnd?: React.ReactNode;
  /** The running tally beside the page control. */
  counter?: React.ReactNode;
}

export type LineFilter = 'all' | 'pending' | 'bid' | 'skip' | 'fix';

export type LineChipMode = 'choice' | 'rfq' | 'newer-revision' | 'legacy';

export interface ReadOnlyLineChip {
  label: string;
  color: 'primary' | 'default';
  variant: 'filled' | 'outlined';
  /** Said on hover when the label alone does not explain itself. */
  tooltip?: string;
}

/**
 * The chip a read-only line shows in place of Quote / Skip.
 *
 * The word "Quoted" is never used: a line marked Bid has not been quoted to anyone, and on a
 * request that had only become an RFQ the old chip read as "a quote went out". A skipped line says
 * why in the tenant's own reason words, and never a raw reason code.
 */
export const readOnlyLineChip = (
  mode: LineChipMode,
  choice: LineParticipationDecision | undefined,
  reasonLabel: string | null | undefined,
  context: { rfqRef?: string | null; currentRevisionNumber?: number | null; promotedRevisionNumber?: number | null } = {},
): ReadOnlyLineChip => {
  const quiet = (label: string, tooltip?: string): ReadOnlyLineChip =>
    ({ label, color: 'default', variant: 'outlined', ...(tooltip ? { tooltip } : {}) });
  const reason = reasonLabel?.trim();
  const withReason = (base: string) => (reason ? `${base} · ${reason}` : base);

  if (mode === 'newer-revision') {
    const arrived = context.currentRevisionNumber != null ? `Revision ${context.currentRevisionNumber}` : 'A newer revision';
    const rfq = context.rfqRef ? `RFQ ${context.rfqRef}` : 'the RFQ';
    const from = context.promotedRevisionNumber != null ? ` from revision ${context.promotedRevisionNumber}` : '';
    return quiet('Newer revision', `${arrived} arrived after ${rfq} was created${from}. Its lines are not decided one by one.`);
  }
  if (mode === 'rfq') {
    // Only lines marked to quote are copied onto the RFQ, so every other line was left out.
    if (choice === 'Bid') return { label: 'Went into the RFQ', color: 'primary', variant: 'filled' };
    return quiet(choice === 'NoBid' ? withReason('Left out') : 'Left out');
  }
  if (choice === 'Bid') return { label: 'Marked to quote', color: 'primary', variant: 'outlined' };
  if (choice === 'NoBid') return quiet(withReason('Skipped'));
  if (mode === 'legacy') {
    return quiet(
      'Not recorded here',
      'This request became an RFQ before this screen recorded decisions. The RFQ shows which lines it holds.',
    );
  }
  return quiet('Not chosen yet');
};

/** Lines drawn at once. Enough to work through, few enough to draw instantly. */
export const LINES_PER_PAGE = 100;


const numberOrEmpty = (value: number | undefined): string =>
  value == null || !Number.isFinite(value) ? '' : String(value);

/** Controls inside a row are one line tall, so a row is two lines of text and no more. */
const DENSE_INPUT_SX = { '& .MuiInputBase-root': { height: 32, fontSize: '0.875rem' } } as const;

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
  /** The tenant's words for the line's saved reason, or null when it has none they name. */
  reasonLabel: string | null;
  /** The choice the read-only chip reports: the server's record once the lines belong to an RFQ. */
  chipChoice: LineParticipationDecision | undefined;
  chipMode: LineChipMode;
  rfqRef?: string | null;
  currentRevisionNumber?: number | null;
  promotedRevisionNumber?: number | null;
  /** Whether the row carries a tick box, and whether it is ticked. */
  selectable: boolean;
  selected: boolean;
  onToggleSelect: (revisionLineId: number, range: boolean) => void;
  columns: number;
}

/**
 * One line, memoised: a bid list can run to two thousand lines, and a keystroke in one row
 * must not re-render the other 1,999. What the row highlights is exactly what the next-step
 * sentence will ask for, because both read the same `lineNeeds`.
 */
const LineRow = React.memo(function LineRow({
  line, decision, unitOptions, currencyOptions, unitCodes, currencyCodes, skipReasons, readOnly, onChange, onOpenDocument,
  reasonLabel, chipChoice, chipMode, rfqRef, currentRevisionNumber, promotedRevisionNumber,
  selectable, selected, onToggleSelect, columns,
}: LineRowProps) {
  const label = lineLabel(line);
  const choice = decision?.decision ?? 'Pending';
  const quoting = choice === 'Bid';
  const skipping = choice === 'NoBid';
  const needList = lineNeeds(line, decision, unitCodes, currencyCodes);
  const needs = new Set<LineNeedKind>(needList.map((need) => need.kind));
  const unverified = needs.has('source') || needs.has('missing-source');
  // What the customer wrote for the unit. A word Nexora understood is said on hover; only a word
  // that asks something of the rep (not stated, not a unit this tenant quotes in) is on the line.
  const unitReading = readUnit(line, unitCodes);
  const unitValue = tenantUnitCode(decision?.unitOfMeasure, unitOptions) ?? '';
  const unitNote = skipping ? null : unitCaption(unitReading, decision?.unitOfMeasure, quoting && !readOnly);
  const unitNoteOnHover = unitReading.kind === 'mapped';
  // Two numbers can sit on a line and they are not the same thing: the buyer's own material
  // code, and the maker's part number. Each is named so a rep never quotes the wrong one.
  const detail = [
    line.itemMaterialCode ? `Material ${line.itemMaterialCode}` : null,
    line.manufacturerPartNumber
      ? `${line.manufacturerName ? `${line.manufacturerName} ` : ''}P/N ${line.manufacturerPartNumber}`
      : line.manufacturerName,
  ].filter(Boolean).join(' · ');
  // What the buyer wrote about the line beyond its name: the specification and their own
  // columns (approved makers, standing instructions). Folded by default — a 1,500-line list
  // must stay a list — and one click away, on the line, not in a dialog.
  const extraEntries = Object.entries(line.extras ?? {}).filter(([, value]) => Boolean(value));
  const hasDetails = Boolean(line.specification?.trim()) || extraEntries.length > 0;
  const [detailsOpen, setDetailsOpen] = React.useState(false);
  // The row's edge says where it stands at a glance: something to fix, quoting, skipped.
  const edge: 'fix' | 'bid' | 'skip' | null = !readOnly && choice !== 'Pending' && needList.length > 0
    ? 'fix'
    : quoting ? 'bid' : skipping ? 'skip' : null;

  const quantityText = (
    <Typography variant="body2" component="span" sx={{ fontVariantNumeric: 'tabular-nums' }}>
      {numberOrEmpty(decision?.quantity ?? line.quantity ?? undefined) || '—'}{' '}
      <Box component="span" sx={unitNote && unitNoteOnHover ? { textDecoration: 'underline dotted', textUnderlineOffset: 3 } : undefined}>
        {decision?.unitOfMeasure ?? line.unitOfMeasure ?? ''}
      </Box>
    </Typography>
  );

  return (
    <>
      <TableRow
        hover
        selected={selected}
        sx={{
          '& > td': { borderBottom: detailsOpen ? 0 : undefined, py: 0.75, verticalAlign: 'middle' },
          '& > td:first-of-type': {
            boxShadow: (t) => `inset 3px 0 0 ${edge === 'fix' ? t.palette.warning.main : edge === 'bid' ? t.palette.primary.main : edge === 'skip' ? t.palette.text.disabled : 'transparent'}`,
          },
          opacity: skipping ? 0.72 : 1,
        }}
      >
        {selectable ? (
          <TableCell padding="checkbox">
            <Checkbox
              size="small"
              checked={selected}
              onClick={(event) => onToggleSelect(line.revisionLineId, event.shiftKey)}
              slotProps={{ input: { 'aria-label': `Select line ${label}` } }}
            />
          </TableCell>
        ) : null}
        <TableCell sx={{ color: 'text.secondary', fontVariantNumeric: 'tabular-nums', fontSize: '0.8rem', whiteSpace: 'nowrap', pl: selectable ? 0 : undefined }}>
          {label}
        </TableCell>
        <TableCell sx={{ minWidth: 0 }}>
          <Typography variant="body2" sx={{ fontWeight: 600, lineHeight: 1.35, textDecoration: skipping ? 'line-through' : 'none', overflowWrap: 'anywhere' }}>
            {lineTitle(line)}
          </Typography>
          {detail || hasDetails ? (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', lineHeight: 1.4 }}>
              {detail}
              {hasDetails ? (
                <>
                  {detail ? ' · ' : ''}
                  <Link
                    component="button"
                    type="button"
                    onClick={() => setDetailsOpen((open) => !open)}
                    aria-expanded={detailsOpen}
                    aria-label={`${detailsOpen ? 'Hide' : 'Show'} details for line ${label}`}
                    sx={{ fontWeight: 700, verticalAlign: 'baseline', fontSize: 'inherit' }}
                  >
                    {detailsOpen ? 'Hide details' : 'Details'}
                  </Link>
                </>
              ) : null}
            </Typography>
          ) : null}
          {/* What a quoted line still owes, on the line itself rather than on a row of its own:
              how a catalogue warning was handled (written for the rep by Quote, and changeable
              here), and whether a person has checked the line against the document. */}
          {!readOnly && quoting && line.needsAttention ? (
            <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center', mt: 0.25, maxWidth: 760 }}>
              <Typography variant="caption" color="warning.main" sx={{ fontWeight: 700, whiteSpace: 'nowrap' }}>
                {catalogWarningSummary(line.warningSnapshotJson, line.attentionReason)}:
              </Typography>
              <InputBase
                value={decision?.note ?? ''}
                placeholder="How you handled it"
                error={needs.has('attention')}
                inputProps={{ 'aria-label': `How you handled it (line ${label})` }}
                onChange={(event) => onChange(line.revisionLineId, { note: event.target.value.slice(0, 1000) || undefined })}
                sx={{
                  flex: 1,
                  minWidth: 0,
                  height: 24,
                  fontSize: '0.75rem',
                  px: 0.75,
                  borderRadius: 1,
                  border: 1,
                  borderColor: needs.has('attention') ? 'error.main' : 'transparent',
                  bgcolor: (t) => alpha(t.palette.warning.main, 0.06),
                  '&:hover, &.Mui-focused': { borderColor: needs.has('attention') ? 'error.main' : 'divider' },
                }}
              />
            </Stack>
          ) : null}
          {!readOnly && unverified ? (
            <Typography variant="caption" color="warning.main" sx={{ display: 'block', mt: 0.25, lineHeight: 1.4 }}>
              {needs.has('missing-source')
                ? 'No source document is on file for this line, so it cannot be quoted.'
                : (
                  <>
                    {/* Two different truths. A line whose item, quantity and unit are each an
                        exact cell of the document was read, not guessed; saying "not sure"
                        about it told the rep the reader was unreliable when only the person's
                        confirmation was missing. Only a line the evidence does not cover
                        earns the doubt. */}
                    {line.sourceEvidenceComplete
                      ? 'Read from the document; not yet checked by a person.'
                      : 'Nexora is not sure it read this line correctly.'}{' '}
                    {/* Named for its line: the page's one "Check the document" is the next-step button. */}
                    <Link component="button" type="button" onClick={() => onOpenDocument(line)} sx={{ fontWeight: 700, verticalAlign: 'baseline', fontSize: 'inherit' }}>
                      {`Check line ${label}`}
                    </Link>
                  </>
                )}
            </Typography>
          ) : null}
          {choice === 'Clarify' ? (
            <Chip size="small" label="Waiting on the customer" color="warning" variant="outlined" sx={{ mt: 0.5 }} />
          ) : null}
        </TableCell>
        {skipping && !readOnly ? (
          // A skipped line has no quantity or currency to give; its two cells ask why instead, so
          // the reason sits on the line and not on a row of its own.
          <TableCell colSpan={2}>
            <FormControl size="small" error={needs.has('reason')} sx={{ width: '100%', maxWidth: 320, ...DENSE_INPUT_SX }}>
              <Select
                value={decision?.reasonCode ?? ''}
                displayEmpty
                renderValue={(value: string) => skipReasons.find((reason) => reason.code === value)?.label
                  ?? <Box component="em" sx={{ color: 'text.secondary' }}>Why skip?</Box>}
                inputProps={{ 'aria-label': `Why skip line ${label}` }}
                onChange={(event) => onChange(line.revisionLineId, { reasonCode: event.target.value || undefined })}
              >
                {skipReasons.map((reason) => (
                  <MenuItem key={reason.code} value={reason.code}>{reason.label}</MenuItem>
                ))}
              </Select>
            </FormControl>
          </TableCell>
        ) : (
          <>
            <TableCell>
              {readOnly || !quoting ? (
                unitNote && unitNoteOnHover ? <Tooltip title={unitNote}>{quantityText}</Tooltip> : quantityText
              ) : (
                <Stack direction="row" spacing={0.75}>
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
                    sx={{ width: 84, ...DENSE_INPUT_SX }}
                  />
                  <Tooltip title={unitNote && unitNoteOnHover ? unitNote : ''}>
                    <FormControl
                      size="small"
                      error={needs.has('unit') || needs.has('unit-unconfigured')}
                      sx={{ minWidth: 80, ...DENSE_INPUT_SX }}
                      data-unit-line={line.revisionLineId}
                    >
                      {/* Only a unit the tenant quotes in is ever the value, so the box never renders
                          blank while holding a word; that word is said underneath instead. */}
                      <Select
                        value={unitValue}
                        displayEmpty
                        renderValue={(value: string) => value || <Box component="em" sx={{ color: 'text.secondary' }}>Unit</Box>}
                        inputProps={{ 'aria-label': `Unit for line ${label}` }}
                        onChange={(event) => onChange(line.revisionLineId, { unitOfMeasure: event.target.value || undefined })}
                      >
                        <MenuItem value=""><em>Unit</em></MenuItem>
                        {unitOptions.map((option) => (
                          <MenuItem key={option.code} value={option.code}>{option.code}</MenuItem>
                        ))}
                      </Select>
                    </FormControl>
                  </Tooltip>
                </Stack>
              )}
              {unitNote && !unitNoteOnHover ? (
                <Typography
                  variant="caption"
                  sx={{ display: 'block', mt: 0.25, lineHeight: 1.3, color: readOnly ? 'text.secondary' : 'warning.dark' }}
                >
                  {unitNote}
                </Typography>
              ) : null}
            </TableCell>
            <TableCell>
              {readOnly || !quoting ? (
                <Typography variant="body2">{decision?.currency ?? line.currency ?? '—'}</Typography>
              ) : (
                <FormControl size="small" error={needs.has('currency') || needs.has('currency-unconfigured')} sx={{ minWidth: 96, ...DENSE_INPUT_SX }}>
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
          </>
        )}
        <TableCell align="right">
          {readOnly ? (() => {
            const chip = readOnlyLineChip(chipMode, chipChoice, reasonLabel, {
              rfqRef, currentRevisionNumber, promotedRevisionNumber,
            });
            // The label wraps rather than being cut off, so a skip reason is read in full.
            const drawn = (
              <Chip
                size="small"
                label={chip.label}
                color={chip.color}
                variant={chip.variant}
                sx={{ maxWidth: '100%', height: 'auto', '& .MuiChip-label': { whiteSpace: 'normal', py: 0.25 } }}
              />
            );
            return chip.tooltip ? <Tooltip describeChild title={chip.tooltip}>{drawn}</Tooltip> : drawn;
          })() : (
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
              sx={{ '& .MuiToggleButton-root': { height: 30, minHeight: 30, px: 1.5, py: 0, fontWeight: 700, fontSize: '0.8rem' } }}
            >
              <ToggleButton value="Bid">Quote</ToggleButton>
              <ToggleButton value="NoBid">Skip</ToggleButton>
            </ToggleButtonGroup>
          )}
        </TableCell>
      </TableRow>
      {detailsOpen ? (
        <TableRow selected={selected}>
          {selectable ? <TableCell padding="checkbox" /> : null}
          <TableCell />
          <TableCell colSpan={columns - (selectable ? 2 : 1)} sx={{ pt: 0, pb: 1 }}>
            <Box sx={{ pl: 1.5, borderLeft: 2, borderColor: 'divider', maxWidth: 860 }}>
              {line.specification?.trim() ? (
                <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap', color: 'text.secondary', mb: extraEntries.length ? 1 : 0 }}>
                  {line.specification.trim()}
                </Typography>
              ) : null}
              {extraEntries.map(([key, value]) => (
                <Typography key={key} variant="body2" sx={{ color: 'text.secondary', whiteSpace: 'pre-wrap' }}>
                  <Box component="span" sx={{ fontWeight: 700, color: 'text.primary' }}>{key}: </Box>{value}
                </Typography>
              ))}
            </Box>
          </TableCell>
        </TableRow>
      ) : null}
    </>
  );
});

const FILTERS: ReadonlyArray<{ key: LineFilter; label: string }> = [
  { key: 'all', label: 'All' },
  { key: 'pending', label: 'Not chosen' },
  { key: 'bid', label: 'Quote' },
  { key: 'skip', label: 'Skip' },
  { key: 'fix', label: 'To fix' },
];

const searchText = (line: LeadDecisionLineDTO): string => [
  lineLabel(line), lineTitle(line), line.itemMaterialCode, line.manufacturerName, line.manufacturerPartNumber, line.description,
].filter(Boolean).join(' ').toLowerCase();

/**
 * The lines as a list to work through: a tick on every line, the bulk buttons acting on the ticked
 * lines (or all of them when none is ticked), filters with counts, a search, and column headings
 * that stay put while only the lines scroll (owner 2026-09-28). Only what is missing becomes a
 * control: a line that arrived with a quantity, a unit and a currency reads as text.
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
  onBulkUnit,
  focusUnit,
  linesPerPage = LINES_PER_PAGE,
  chipMode = 'choice',
  rfqRef,
  currentRevisionNumber,
  promotedRevisionNumber,
  onApply,
  toolbarEnd,
  counter,
}) => {
  const skipReasons = React.useMemo(() => reasonCodes.filter((reason) => reason.appliesTo.includes('NoBid')), [reasonCodes]);
  const reasonLabels = React.useMemo(
    () => new Map(reasonCodes.map((reason) => [reason.code, reason.label?.trim() || null])),
    [reasonCodes],
  );
  // Editable lines ask the question; read-only lines report an answer, and a request that became
  // an RFQ reports what went into it.
  const decisionHeader = !readOnly
    ? 'Quote it?'
    : chipMode === 'rfq' || chipMode === 'newer-revision' ? 'RFQ' : 'Choice';
  const unitCodes = React.useMemo(() => new Set(unitOptions.map((option) => option.code.toUpperCase())), [unitOptions]);
  const currencyCodes = React.useMemo(() => new Set(currencyOptions.map((option) => option.code.toUpperCase())), [currencyOptions]);
  const selectable = !readOnly && Boolean(onApply);
  const columns = (selectable ? 1 : 0) + 5;

  // Which lines are shown: a filter by where each line stands, and a search over its words and numbers.
  const [filter, setFilter] = React.useState<LineFilter>('all');
  const [search, setSearch] = React.useState('');
  const standing = React.useCallback((line: LeadDecisionLineDTO): Exclude<LineFilter, 'all'>[] => {
    const decision = decisions[line.revisionLineId];
    const choice = decision?.decision ?? 'Pending';
    const kinds: Exclude<LineFilter, 'all'>[] = [];
    if (choice === 'Bid') kinds.push('bid');
    else if (choice === 'NoBid') kinds.push('skip');
    else kinds.push('pending');
    if (choice !== 'Pending' && lineNeeds(line, decision, unitCodes, currencyCodes).length > 0) kinds.push('fix');
    return kinds;
  }, [currencyCodes, decisions, unitCodes]);
  const counts = React.useMemo(() => {
    const tally: Record<LineFilter, number> = { all: lines.length, pending: 0, bid: 0, skip: 0, fix: 0 };
    for (const line of lines) for (const kind of standing(line)) tally[kind] += 1;
    return tally;
  }, [lines, standing]);
  const needle = search.trim().toLowerCase();
  const shown = React.useMemo(
    () => lines.filter((line) => (filter === 'all' || standing(line).includes(filter)) && (!needle || searchText(line).includes(needle))),
    [filter, lines, needle, standing],
  );

  // A page of lines at a time. Every row is a live form (quantity, unit, currency, a reason),
  // and a real bid list runs to 1,500 lines: drawing them all at once froze the browser for
  // minutes after "Quote all". A hundred at a time draws in well under a second, and the page
  // control says where you are. Decisions are kept for every line, on every page.
  const [page, setPage] = React.useState(0);
  const [pageSize, setPageSize] = React.useState(linesPerPage);
  React.useEffect(() => {
    if (page * pageSize >= shown.length) setPage(0);
  }, [shown.length, page, pageSize]);
  const visible = shown.length > pageSize ? shown.slice(page * pageSize, (page + 1) * pageSize) : shown;
  const scrollRef = React.useRef<HTMLDivElement | null>(null);
  const turnTo = (next: number) => {
    setPage(next);
    scrollRef.current?.scrollTo?.({ top: 0 });
  };

  // The ticks. Kept by line id across pages and filters; a line that is no longer on the request
  // (a new revision) drops out on its own.
  const [ticked, setTicked] = React.useState<Set<number>>(() => new Set());
  const lastTicked = React.useRef<number | null>(null);
  const lineIds = React.useMemo(() => new Set(lines.map((line) => line.revisionLineId)), [lines]);
  const selectedIds = React.useMemo(() => [...ticked].filter((id) => lineIds.has(id)), [lineIds, ticked]);
  const shownTicked = shown.filter((line) => ticked.has(line.revisionLineId)).length;
  const allShownTicked = shown.length > 0 && shownTicked === shown.length;
  const toggleOne = React.useCallback((revisionLineId: number, range: boolean) => {
    setTicked((current) => {
      const next = new Set(current);
      const turnOn = !current.has(revisionLineId);
      // Shift-click ticks (or clears) every line shown between the last one clicked and this one.
      const ids = visibleIdsRef.current;
      const from = lastTicked.current == null ? -1 : ids.indexOf(lastTicked.current);
      const to = ids.indexOf(revisionLineId);
      const span = range && from >= 0 && to >= 0 ? ids.slice(Math.min(from, to), Math.max(from, to) + 1) : [revisionLineId];
      for (const id of span) if (turnOn) next.add(id); else next.delete(id);
      return next;
    });
    lastTicked.current = revisionLineId;
  }, []);
  const visibleIdsRef = React.useRef<number[]>([]);
  visibleIdsRef.current = visible.map((line) => line.revisionLineId);
  const toggleShown = () => setTicked((current) => {
    const next = new Set(current);
    for (const line of shown) if (allShownTicked) next.delete(line.revisionLineId); else next.add(line.revisionLineId);
    return next;
  });
  const clearTicks = () => setTicked(new Set());

  // The bulk buttons act on the ticked lines, or on every line when none is ticked.
  const scope = selectedIds.length > 0 ? String(selectedIds.length) : 'all';
  const apply = (patch: Partial<EditableLineDecision>) => onApply?.(selectedIds.length > 0 ? selectedIds : null, patch);
  // Each menu says first which lines it will change, and how to change only some (owner 2026-09-28:
  // the rep must see that ticking lines narrows the change).
  const scopeHeader = (
    <ListSubheader sx={{ lineHeight: 1.4, py: 1, fontSize: '0.75rem', maxWidth: 260, whiteSpace: 'normal' }}>
      {selectedIds.length > 0 ? (
        <Box component="span" sx={{ fontWeight: 800, color: 'text.primary' }}>
          For the {selectedIds.length} ticked {selectedIds.length === 1 ? 'line' : 'lines'}
        </Box>
      ) : (
        <>
          <Box component="span" sx={{ display: 'block', fontWeight: 800, color: 'text.primary' }}>For all {lines.length.toLocaleString()} lines</Box>
          Tick lines to change only those.
        </>
      )}
    </ListSubheader>
  );
  const [skipAnchor, setSkipAnchor] = React.useState<HTMLElement | null>(null);
  const [currencyAnchor, setCurrencyAnchor] = React.useState<HTMLElement | null>(null);

  // A bid list with no unit column: every line marked to quote lacks one. One picker sets them all, and
  // only those — a line that already has a unit keeps it.
  const unitlessQuoted = unitOptions.length === 0 ? 0 : lines.filter((line) => {
    const decision = decisions[line.revisionLineId];
    return decision?.decision === 'Bid' && !(decision.unitOfMeasure && unitCodes.has(decision.unitOfMeasure.trim().toUpperCase()));
  }).length;
  const showBulkUnit = !readOnly && Boolean(onBulkUnit) && unitlessQuoted >= 2;

  // The next-step button can bring the rep to a unit picker. Show every line, turn to the line's
  // page, then focus the picker once that page is drawn.
  const containerRef = React.useRef<HTMLDivElement | null>(null);
  const [focusTarget, setFocusTarget] = React.useState<{ lineId?: number } | null>(null);
  React.useEffect(() => {
    if (!focusUnit) return;
    if (focusUnit.lineId != null) {
      setFilter('all');
      setSearch('');
      if (lines.length > pageSize) {
        const index = lines.findIndex((candidate) => candidate.revisionLineId === focusUnit.lineId);
        if (index >= 0) setPage(Math.floor(index / pageSize));
      }
    }
    setFocusTarget({ lineId: focusUnit.lineId });
    // Only a new request moves the page; the lines changing under it must not, so the lines and
    // the page size are read, not watched.
  }, [focusUnit]);
  React.useEffect(() => {
    if (!focusTarget) return;
    const selector = focusTarget.lineId != null
      ? `[data-unit-line="${focusTarget.lineId}"] [role="combobox"]`
      : '[data-testid="decide-bulk-unit"] [role="combobox"]';
    const node = containerRef.current?.querySelector<HTMLElement>(selector);
    node?.scrollIntoView?.({ block: 'center', behavior: 'smooth' });
    node?.focus();
    setFocusTarget(null);
  }, [focusTarget, page]);

  const denseButton = { fontWeight: 700, minHeight: 32, height: 32, py: 0, px: 1.5, whiteSpace: 'nowrap' } as const;
  const paged = shown.length > pageSize || lines.length > linesPerPage;

  return (
    <Box ref={containerRef} sx={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 }}>
      {/* One row above the lines: which lines are shown (filters, search), what to do to the ticked
          ones, then where you are in them and the page's own actions. It was two rows; the lines get
          that height back. */}
      <Stack
        direction="row"
        useFlexGap
        sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 0.75, rowGap: 0.75, px: { xs: 1.5, sm: 2 }, py: 0.75, borderBottom: 1, borderColor: 'divider' }}
      >
        {lines.length > 1 ? (
          <>
          {FILTERS.filter((item) => item.key === 'all' || counts[item.key] > 0 || filter === item.key).map((item) => (
            <Chip
              key={item.key}
              clickable
              size="small"
              aria-pressed={filter === item.key}
              label={`${item.label} ${counts[item.key]}`}
              color={filter === item.key ? (item.key === 'fix' ? 'warning' : 'primary') : 'default'}
              variant={filter === item.key ? 'filled' : 'outlined'}
              onClick={() => { setFilter(item.key); turnTo(0); }}
              sx={{ height: 26, fontSize: '0.75rem', fontWeight: 600, fontVariantNumeric: 'tabular-nums' }}
            />
          ))}
          <TextField
            size="small"
            placeholder="Find a line"
            value={search}
            onChange={(event) => { setSearch(event.target.value); turnTo(0); }}
            slotProps={{
              htmlInput: { 'aria-label': 'Find a line by its words or numbers' },
              input: {
                startAdornment: <InputAdornment position="start"><SearchIcon fontSize="small" /></InputAdornment>,
                endAdornment: search ? (
                  <InputAdornment position="end">
                    <IconButton size="small" aria-label="Clear the search" onClick={() => { setSearch(''); turnTo(0); }}>
                      <CloseIcon fontSize="small" />
                    </IconButton>
                  </InputAdornment>
                ) : undefined,
              },
            }}
            sx={{ width: 220, '& .MuiInputBase-root': { height: 30, fontSize: '0.8125rem' } }}
          />
          </>
        ) : null}
        {selectable && lines.length > 1 ? <Divider orientation="vertical" flexItem sx={{ mx: 0.75, my: 0.5 }} /> : null}
        {selectable && lines.length > 1 ? (
          <>
            {selectedIds.length > 0 ? (
              <Chip
                size="small"
                color="primary"
                label={`${selectedIds.length} selected`}
                onDelete={clearTicks}
                deleteIcon={<CloseIcon aria-label="Clear the selection" />}
                sx={{ fontWeight: 700 }}
              />
            ) : null}
            <Button size="small" variant="outlined" sx={denseButton} onClick={() => apply({ decision: 'Bid', reasonCode: undefined })}>
              {scope === 'all' ? 'Quote all' : `Quote ${scope}`}
            </Button>
            <Button
              size="small"
              variant="outlined"
              sx={denseButton}
              aria-haspopup="menu"
              aria-expanded={skipAnchor ? 'true' : undefined}
              onClick={(event) => setSkipAnchor(event.currentTarget)}
            >
              {scope === 'all' ? 'Skip all…' : `Skip ${scope}…`}
            </Button>
            <Menu anchorEl={skipAnchor} open={Boolean(skipAnchor)} onClose={() => setSkipAnchor(null)} aria-label="Why skip these lines">
              {scopeHeader}
              {skipReasons.map((reason) => (
                <MenuItem key={reason.code} onClick={() => { apply({ decision: 'NoBid', reasonCode: reason.code }); setSkipAnchor(null); }}>
                  {reason.label}
                </MenuItem>
              ))}
            </Menu>
            {currencyOptions.length > 0 ? (
              <>
                <Button
                  size="small"
                  variant="outlined"
                  sx={denseButton}
                  aria-haspopup="menu"
                  aria-expanded={currencyAnchor ? 'true' : undefined}
                  onClick={(event) => setCurrencyAnchor(event.currentTarget)}
                >
                  {scope === 'all' ? 'Currency for all…' : `Currency for ${scope}…`}
                </Button>
                <Menu anchorEl={currencyAnchor} open={Boolean(currencyAnchor)} onClose={() => setCurrencyAnchor(null)} aria-label="Currency for these lines">
                  {scopeHeader}
                  {currencyOptions.map((option) => (
                    <MenuItem key={option.code} onClick={() => { apply({ currency: option.code }); setCurrencyAnchor(null); }}>
                      {option.code}
                    </MenuItem>
                  ))}
                </Menu>
              </>
            ) : null}
          </>
        ) : null}
        <Box sx={{ flex: 1 }} />
        {counter ? (
          <Typography variant="body2" color="text.secondary" sx={{ fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap', mr: 2 }}>
            {counter}
          </Typography>
        ) : null}
        {paged ? (
          <TablePagination
            component="div"
            count={shown.length}
            page={Math.min(page, Math.max(0, Math.ceil(shown.length / pageSize) - 1))}
            onPageChange={(_event, next) => turnTo(next)}
            rowsPerPage={pageSize}
            onRowsPerPageChange={(event) => { setPageSize(Number(event.target.value)); turnTo(0); }}
            rowsPerPageOptions={[linesPerPage, linesPerPage * 2, linesPerPage * 5]}
            labelRowsPerPage="Per page"
            labelDisplayedRows={({ from, to, count }) => `Lines ${from}–${to} of ${count}`}
            getItemAriaLabel={(type) => `${type} page of lines`}
            sx={{
              borderBottom: 0,
              '& .MuiTablePagination-toolbar': { minHeight: 30, height: 30, pl: 0 },
              '& .MuiTablePagination-selectLabel, & .MuiTablePagination-displayedRows': { fontSize: '0.8125rem', my: 0 },
              '& .MuiTablePagination-actions button': { width: 30, height: 30, minWidth: 30, minHeight: 30 },
            }}
          />
        ) : null}
        {toolbarEnd ? <Box sx={{ display: 'flex', gap: 1, '& .MuiButton-root': denseButton }}>{toolbarEnd}</Box> : null}
      </Stack>

      {showBulkUnit ? (
        <Stack
          direction="row"
          spacing={1.5}
          data-testid="decide-bulk-unit"
          sx={{ alignItems: 'center', flexWrap: 'wrap', rowGap: 1, px: { xs: 1.5, sm: 2 }, py: 0.75, borderBottom: 1, borderColor: 'divider', bgcolor: (t) => alpha(t.palette.warning.main, 0.08) }}
        >
          <Typography variant="body2" sx={{ fontWeight: 700 }}>{unitlessQuoted} lines marked to quote need a unit.</Typography>
          <FormControl size="small" error sx={{ minWidth: 180, ...DENSE_INPUT_SX }}>
            <Select
              value=""
              displayEmpty
              renderValue={() => <Box component="em" sx={{ color: 'text.secondary' }}>Unit for all {unitlessQuoted}</Box>}
              inputProps={{ 'aria-label': `Unit for the ${unitlessQuoted} lines marked to quote without one` }}
              onChange={(event) => { if (event.target.value) onBulkUnit?.(String(event.target.value)); }}
            >
              {unitOptions.map((option) => (
                <MenuItem key={option.code} value={option.code}>
                  {option.label && option.label !== option.code ? `${option.code} · ${option.label}` : option.code}
                </MenuItem>
              ))}
            </Select>
          </FormControl>
          <Typography variant="caption" color="text.secondary">Lines that already have a unit keep theirs.</Typography>
        </Stack>
      ) : null}

      {/* Only the lines scroll; the headings stay put so the rep always knows which column is which. */}
      <TableContainer ref={scrollRef} sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
        <Table
          size="small"
          stickyHeader
          aria-label="Lines the customer asked for"
          sx={{
            minWidth: 760,
            '& .MuiTableCell-stickyHeader': {
              bgcolor: 'background.paper',
              py: 0.75,
              fontSize: '0.75rem',
              fontWeight: 700,
              letterSpacing: '.04em',
              textTransform: 'uppercase',
              color: 'text.secondary',
              boxShadow: (t) => `inset 0 -1px 0 ${t.palette.divider}`,
            },
            '& .MuiTableRow-root.Mui-selected': { bgcolor: (t) => alpha(t.palette.primary.main, 0.07) },
            '& .MuiTableRow-root.Mui-selected:hover': { bgcolor: (t) => alpha(t.palette.primary.main, 0.11) },
          }}
        >
          <TableHead>
            <TableRow>
              {selectable ? (
                <TableCell padding="checkbox">
                  <Checkbox
                    size="small"
                    checked={allShownTicked}
                    indeterminate={shownTicked > 0 && !allShownTicked}
                    disabled={shown.length === 0}
                    onChange={toggleShown}
                    slotProps={{ input: { 'aria-label': `Select all ${shown.length} lines shown` } }}
                  />
                </TableCell>
              ) : null}
              <TableCell sx={{ width: 64, pl: selectable ? 0 : undefined }}>Line</TableCell>
              <TableCell>Item</TableCell>
              <TableCell sx={{ width: 200 }}>Quantity</TableCell>
              <TableCell sx={{ width: 120 }}>Price in</TableCell>
              <TableCell sx={{ width: 150 }} align="right">{decisionHeader}</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {visible.map((line) => {
              // Lines that belong to an RFQ (or arrived after one, or predate this screen's records)
              // report what the server saved for them. The page fills its choices in after the first
              // paint, so reading those painted "Left out" on every line of a promoted request first.
              const chipRecord = chipMode === 'choice' ? decisions[line.revisionLineId] : line.participation ?? undefined;
              const reasonCode = chipRecord?.reasonCode;
              return (
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
                  reasonLabel={reasonCode ? reasonLabels.get(reasonCode) ?? null : null}
                  chipChoice={chipRecord?.decision}
                  chipMode={chipMode}
                  rfqRef={rfqRef}
                  currentRevisionNumber={currentRevisionNumber}
                  promotedRevisionNumber={promotedRevisionNumber}
                  selectable={selectable}
                  selected={ticked.has(line.revisionLineId)}
                  onToggleSelect={toggleOne}
                  columns={columns}
                />
              );
            })}
            {lines.length > 0 && shown.length === 0 ? (
              <TableRow>
                <TableCell colSpan={columns}>
                  <Typography variant="body2" color="text.secondary">
                    No line matches.{' '}
                    <Link component="button" type="button" onClick={() => { setFilter('all'); setSearch(''); turnTo(0); }} sx={{ fontWeight: 700, verticalAlign: 'baseline' }}>
                      Show all {lines.length} lines
                    </Link>
                  </Typography>
                </TableCell>
              </TableRow>
            ) : null}
            {lines.length === 0 ? (
              <TableRow>
                <TableCell colSpan={columns}>
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
    </Box>
  );
};

export default LinesTable;
