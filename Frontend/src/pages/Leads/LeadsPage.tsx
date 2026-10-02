import React, { useCallback, useMemo, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Tooltip, Stack, MenuItem, CircularProgress,
  Alert,
  Link, Menu, ListItemIcon, ListItemText,
  ToggleButton, ToggleButtonGroup,
  Skeleton, Collapse,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel, type GridRowId, type GridRowSelectionModel,
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Email as EmailIcon,
  MoreVert as MoreIcon,
  FilterAltOff as ClearFiltersIcon,
  MarkEmailRead as InboxIcon,
  AssignmentInd as AssignIcon,
  Person as UserIcon,
  Tune as TuneIcon,
  UploadFileOutlined as UploadIcon,
} from '@mui/icons-material';
import useColumnPreferences from '../../hooks/useColumnPreferences';
import ColumnPreferences from '../../components/common/ColumnPreferences';
import leadService, { type LeadResponseDTO } from '../../api/services/leadService';
import decisionService, { DECISION_SUMMARY_BATCH_CAP, type LeadDecisionSummary } from '../../api/services/decisionService';
import { DECISION_META, decisionFacts, decisionLabel } from './decisionRead';
import LateIngestedBadge from './LateIngestedBadge';
import ClientCell from './ClientCell';
import ResolveClientDialog from './ResolveClientDialog';
import SearchField from '../../components/common/SearchField';
import ExportExcelButton, { loadAllPages, type ExportColumn } from '../../components/common/ExportExcelButton';
import gridEmptyOverlay from '../../components/common/gridOverlays';
import ViewTabs from '../../components/layout/ViewTabs';
import HeaderFilter, { type HeaderFilterOption as ListFilterOption } from '../../components/common/HeaderFilter';
import useListFilters, {
  AHEAD_WINDOWS, AHEAD_WINDOW_LABELS, DUE_WINDOWS, DUE_WINDOW_LABELS, NOT_OPENED, NO_CUSTOMER,
  RECEIVED_WINDOWS, RECEIVED_WINDOW_LABELS, aheadRange, anyListFilter, clearListFilterKeys, dueParams, localDayInstants, receivedRange,
  type AheadWindow, type DueWindow, type ListFilterValues, type ReceivedWindow,
} from '../../hooks/useListFilters';
import { useSnackbar } from 'notistack';
import { formatDateSafe, formatDateTimeSafe, formatDeadline, formatDeadlineDate, parseDateSafe } from '../../utils/dates';
import { DEADLINE_COLOR, deadlineWords } from '../../utils/deadline';
import { useAuth } from '../../context/AuthContext';
import { presentableErrorMessage } from '../../utils/apiErrors';
import commercialRoutingService, {
  LEAD_OWNERSHIP_ACTION, type RoutingOwnerOption,
} from '../../api/services/commercialRoutingService';
import {
  OwnerPickerMenu, AssignReasonDialog, assignmentNeedsReason, useOwnerOptions,
} from './LeadOwnerPicker';
import { commercialActionPermissions } from '../../utils/commercialActionPermissions';
import { LEAD_STATUS_WORDS, leadStatusWords } from '../../utils/leadStatusWords';

// ---------------------------------------------------------------------------
// Column visibility and order are AA-01 server-side per-user preferences now
// (useColumnPreferences + ColumnPreferences). The old localStorage column model
// was per-browser, order-less and invisible to the server, so a user who moved
// machines lost their layout and no tenant-defined field could ever appear in
// it. Defaults for this grid live in the server catalog under `leads.list`.
//
// Density stays local: it is a rendering comfort setting with no server
// contract, and there is nothing to compose it with.
// ---------------------------------------------------------------------------

const DENSITY_KEY_BASE = 'nexora.leadsPage.density';

const userScopedKey = (base: string): string => {
  try {
    const raw = localStorage.getItem('userData');
    if (raw) {
      const parsed: unknown = JSON.parse(raw);
      if (parsed && typeof parsed === 'object' && 'id' in parsed) {
        const id = (parsed as { id?: unknown }).id;
        if (typeof id === 'number' || typeof id === 'string') return `${base}:user-${id}`;
      }
    }
  } catch {
    // Corrupted userData — fall back to a global preference key.
  }
  return `${base}:global`;
};

type DensityChoice = 'comfortable' | 'standard' | 'compact';

const loadDensity = (): DensityChoice => {
  try {
    const stored = localStorage.getItem(userScopedKey(DENSITY_KEY_BASE));
    return stored === 'compact' || stored === 'standard' ? stored : 'comfortable';
  } catch {
    return 'comfortable';
  }
};

// Two ways to read the same rows. "Simple" is five plain columns a rep scans without
// scrolling sideways; "Spreadsheet" is every field, in this user's saved order. Same rows,
// same queries, same cells — only the column set differs. Local, like density.
const VIEW_KEY_BASE = 'nexora.leadsPage.view';
type ViewChoice = 'simple' | 'spreadsheet';
const loadView = (): ViewChoice => {
  try {
    return localStorage.getItem(userScopedKey(VIEW_KEY_BASE)) === 'spreadsheet' ? 'spreadsheet' : 'simple';
  } catch {
    return 'simple';
  }
};

// ---------------------------------------------------------------------------
// Presentation helpers
// ---------------------------------------------------------------------------

const INTERNAL_EMAIL_SUFFIX = '@pipeline.local';

/** Internal pipeline addresses and blanks must never be shown to users. */
const buyerContact = (row: LeadResponseDTO): { text: string; internal: boolean } => {
  const email = (row.clientemail ?? '').trim();
  if (!email || email.toLowerCase().endsWith(INTERNAL_EMAIL_SUFFIX)) {
    const source = (row.leadSource ?? '').toLowerCase();
    if (source === 'bulk') return { text: 'Bulk upload', internal: true };
    if (source === 'email') return { text: 'No contact email', internal: true };
    return { text: 'Manual upload', internal: true };
  }
  return { text: email, internal: false };
};

/** Deadline urgency applies only to real (non-sentinel) dates. */
const deadlineSx = (dateStr: string | null | undefined): { color: string; fontWeight: number } => {
  const d = parseDateSafe(dateStr);
  if (!d) return { color: 'text.disabled', fontWeight: 400 };
  const hoursLeft = (d.getTime() - Date.now()) / (1000 * 60 * 60);
  if (hoursLeft < 0) return { color: 'error.main', fontWeight: 700 };
  if (hoursLeft < 72) return { color: 'warning.main', fontWeight: 700 };
  return { color: 'text.primary', fontWeight: 500 };
};

interface StatusMeta {
  label: string;
  color: 'success' | 'error' | 'warning' | 'primary';
  variant: 'filled' | 'outlined';
}

// The tenant's own status code is the fact. `isAccepted` is a legacy hard-coded id (24) that
// this tenant does not use, so a lead already converted to an RFQ used to read "New" with a
// Decide button beside it — the exact lie the DTO comment on `leadStatusCode` warns about.
// The words come from the one shared map, so the list, the Deadline board and the Decide screen
// name a status the same way; only the colour and weight are this grid's own.
const statusMeta = (code: string, color: StatusMeta['color'], variant: StatusMeta['variant']): StatusMeta =>
  ({ label: LEAD_STATUS_WORDS[code], color, variant });
const STATUS_META: Record<string, StatusMeta> = {
  CONVERTED_TO_RFQ: statusMeta('CONVERTED_TO_RFQ', 'success', 'filled'),
  QUOTED: statusMeta('QUOTED', 'success', 'filled'),
  NEGOTIATION: statusMeta('NEGOTIATION', 'success', 'outlined'),
  AWARDED: statusMeta('AWARDED', 'success', 'filled'),
  PARTIALLY_AWARDED: statusMeta('PARTIALLY_AWARDED', 'success', 'outlined'),
  COMPLETED: statusMeta('COMPLETED', 'success', 'outlined'),
  QUALIFIED: statusMeta('QUALIFIED', 'primary', 'filled'),
  UNDER_REVIEW: statusMeta('UNDER_REVIEW', 'warning', 'outlined'),
  DISQUALIFIED: statusMeta('DISQUALIFIED', 'error', 'outlined'),
  LOST: statusMeta('LOST', 'error', 'outlined'),
  CANCELLED: statusMeta('CANCELLED', 'error', 'outlined'),
  DUPLICATED: statusMeta('DUPLICATED', 'warning', 'outlined'),
};
/** A lead whose decision has been made: the row offers "See decision", not "Decide". */
const DECIDED_CODES = new Set(['CONVERTED_TO_RFQ', 'QUOTED', 'NEGOTIATION', 'AWARDED', 'PARTIALLY_AWARDED', 'COMPLETED', 'DISQUALIFIED', 'LOST', 'CANCELLED', 'DUPLICATED']);
const isDecided = (row: LeadResponseDTO): boolean =>
  DECIDED_CODES.has((row.leadStatusCode ?? '').toUpperCase()) || row.isAccepted || row.isRejected;
const leadStatus = (row: LeadResponseDTO): StatusMeta => {
  const known = STATUS_META[(row.leadStatusCode ?? '').toUpperCase()];
  if (known) return known;
  if (row.isAccepted) return { label: 'Accepted', color: 'success', variant: 'filled' };
  if (row.isRejected) return { label: 'Rejected', color: 'error', variant: 'outlined' };
  if (row.headerRemarks?.startsWith('[NEEDS REVIEW]')) return { label: 'Needs review', color: 'warning', variant: 'filled' };
  return { label: 'New', color: 'primary', variant: 'outlined' };
};

type LeadExportRow = LeadResponseDTO & { decision?: LeadDecisionSummary };
const yesNo = (value?: boolean | null) => (value == null ? '' : value ? 'Yes' : 'No');

const LEAD_EXPORT_COLUMNS: ExportColumn<LeadExportRow>[] = [
  { header: 'Nexora Serial', value: (r) => r.nexoraSerial || r.commercialCaseReference },
  { header: 'RFQ/Bid #', value: (r) => r.rfqno },
  { header: 'Client', value: (r) => r.customerName },
  { header: 'Client as written on document', value: (r) => r.customerCompanyNameExtracted },
  { header: 'Customer portal', value: (r) => r.customerPortalNameExtracted },
  { header: 'Client match', value: (r) => r.customerMatchStatus },
  { header: 'Client match reason', value: (r) => r.customerMatchExplanation },
  { header: 'Buyer contact', value: (r) => r.buyersName },
  { header: 'Buyer email', value: (r) => r.clientemail },
  { header: 'Account owner', value: (r) => r.accountOwnerName },
  { header: 'Source', value: (r) => r.leadSource },
  { header: 'Email from', value: (r) => r.emailSender },
  { header: 'Email subject', value: (r) => r.emailSubject },
  { header: 'Email received', value: (r) => formatDateTimeSafe(r.emailReceivedAtUtc, '') },
  { header: 'Received', value: (r) => formatDateSafe(r.recDate, '') },
  { header: 'Ingested', value: (r) => formatDateTimeSafe(r.ingestedOn || r.ingestedAtUtc, '') },
  { header: 'Arrived late', value: (r) => yesNo(r.lateIngested) },
  { header: 'Deadline', value: (r) => formatDeadline(r.bidClosingDate, '') },
  { header: 'Deadline (Hijri)', value: (r) => r.bidClosingDateHijri },
  { header: 'Required delivery', value: (r) => formatDateSafe(r.requiredDeliveryDate, '') },
  { header: 'Delivery location', value: (r) => r.deliveryLocation },
  { header: 'Agreement reference', value: (r) => r.agreementReference },
  { header: 'Opportunity #', value: (r) => r.opportunityNo },
  { header: 'RFQ type', value: (r) => r.rfqtype },
  { header: 'Agreement duration', value: (r) => r.durationAgreement },
  { header: 'Bidding decision', value: (r) => r.biddingDecision },
  { header: 'Acknowledged', value: (r) => formatDateSafe(r.acknowledgmentDate, '') },
  { header: 'Submitted', value: (r) => formatDateSafe(r.subDate, '') },
  { header: 'Items', value: (r) => r.itemCount ?? 0 },
  { header: 'Status', value: (r) => leadStatus(r).label },
  { header: "Nexora's read", value: (r) => (r.decision ? decisionLabel(r.decision.recommendation) : '') },
  { header: 'We stock (%)', value: (r) => r.decision?.coveragePct },
  { header: 'Estimated value', value: (r) => r.decision?.estimatedValue },
  { header: 'Needs commercial review', value: (r) => yesNo(r.requiresCommercialReview) },
  { header: 'Owner', value: (r) => r.assignedToFullName || 'Unassigned' },
  { header: 'How assigned', value: (r) => r.assignmentMethod },
  { header: 'Assignment reason', value: (r) => r.assignmentReason },
  { header: 'Assignment comment', value: (r) => r.assignComment },
  { header: 'Revision', value: (r) => r.currentRevisionNumber },
  { header: 'Duplicate', value: (r) => r.duplicateStatus },
  { header: 'Duplicate of lead', value: (r) => r.duplicateOfLeadId },
  { header: 'Remarks', value: (r) => r.headerRemarks },
  { header: 'Business unit', value: (r) => r.businessUnitName },
  { header: 'Created by', value: (r) => r.createdBy },
  { header: 'Created', value: (r) => formatDateTimeSafe(r.createdDate, '') },
];

/** Adds Nexora's read to each lead, 100 at a time. A lead the service cannot read stays blank. */
const withDecisions = async (leads: LeadResponseDTO[]): Promise<LeadExportRow[]> => {
  const summaries: Record<string, LeadDecisionSummary> = {};
  for (let i = 0; i < leads.length; i += DECISION_SUMMARY_BATCH_CAP) {
    try {
      const batch = await decisionService.getDecisionSummaries(leads.slice(i, i + DECISION_SUMMARY_BATCH_CAP).map((l) => l.id));
      Object.assign(summaries, batch.summaries);
    } catch {
      // The export still carries every other field.
    }
  }
  return leads.map((lead) => ({ ...lead, decision: summaries[String(lead.id)] }));
};

// NOTE: this grid used to carry a "Confidence" column driven by
// Lead.Aiconfidence, rendered High/Medium/Low in green/amber/red. That score is
// not a measured accuracy — on the structured path it is a literal written per
// cell, on the model path it is the model's own self-report against a rubric in
// its own prompt — so the column is gone. The "Status" column already carries
// the fact a user can act on: whether a person has reviewed the document.

// ---------------------------------------------------------------------------
// Owner filter
// ---------------------------------------------------------------------------

/**
 * The three questions a rep actually asks this list: what has nobody picked up, what is on me,
 * and show me everything. It is ONE control — the Owner column's header filter — not three rail rows
 * and not a second grid.
 *
 * It travels to the server on the same `view` parameter as the queue tabs, comma-joined, because
 * it NARROWS the queue rather than replacing it — "Revisions" plus "Unassigned" means both.
 * `mine` carries the reader's own id since `/api/Lead` forwards no identity to the repository;
 * see `LeadRepository.ParseLeadListView`.
 */
type OwnerView = 'unassigned' | 'mine' | 'all';

/**
 * "All inquiries" opens on everyone's inquiries.
 *
 * The tab is called "All inquiries" and it sits beside "Unassigned" and "Assigned", which are the
 * narrowed lists. Opening it pre-narrowed to Unassigned made a manager's first screen read
 * "0 inquiries · Every inquiry here already has an owner" under a tab that promised everything —
 * a filtered-to-zero sentence on a list the reader had not filtered. The pile nobody has picked
 * up, and a rep's own pile, stay one click away in the Owner header and one tab away on the strip.
 */
export const DEFAULT_OWNER_VIEW: OwnerView = 'all';

/**
 * A manager or administrator who is not themselves a sales rep sees the "no Sales Rep profile"
 * notice once; it is information for them, not a task. It stays for a rep, who cannot take work
 * until an administrator acts, so the sentence and the person to ask must remain on screen.
 */
const REP_PROFILE_NOTICE_KEY_BASE = 'nexora.leadsPage.repProfileNoticeDismissed';
const loadRepProfileNoticeDismissed = (): boolean => {
  try {
    return localStorage.getItem(userScopedKey(REP_PROFILE_NOTICE_KEY_BASE)) === '1';
  } catch {
    return false;
  }
};

/**
 * `repUserId` is a manager's "one rep" filter. It only ever rides with Anyone: picking a rep drops
 * Unassigned/Mine, and picking Unassigned or Mine drops the rep, so the server never receives
 * `mine:` or `unassigned` together with `rep:`.
 */
export const composeLeadsView = (
  queueView: string | undefined,
  ownerView: OwnerView,
  myUserId: number | null | undefined,
  repUserId: number | null = null,
): string | undefined => {
  const tokens: string[] = [];
  if (queueView) tokens.push(queueView);
  if (ownerView === 'unassigned') tokens.push('unassigned');
  else if (ownerView === 'mine' && myUserId != null) tokens.push(`mine:${myUserId}`);
  else if (ownerView === 'all' && repUserId != null) tokens.push(`rep:${repUserId}`);
  return tokens.length > 0 ? tokens.join(',') : undefined;
};

/** How a lead names itself in a failure report — never a bare database id. */
const leadLabel = (lead: LeadResponseDTO): string =>
  (lead.nexoraSerial || lead.commercialCaseReference || lead.rfqno || '').trim() || `Inquiry from ${lead.buyersName || 'an unnamed buyer'}`;

interface AssignFailure {
  leadId: number;
  label: string;
  message: string;
}

const EMPTY_SELECTION: GridRowSelectionModel = { type: 'include', ids: new Set<GridRowId>() };

// ---------------------------------------------------------------------------
// Column-header filters
// ---------------------------------------------------------------------------

/** Where an inquiry came from, as the Source column and the "Came from" choices name it. */
const SOURCE_OPTIONS: ListFilterOption<string>[] = [
  { value: 'Email', label: 'Email' },
  { value: 'Manual', label: 'Manual' },
  { value: 'Bulk', label: 'Bulk upload' },
];

/** The Items buckets, as `min-max` (an open end is blank). */
const ITEM_BUCKETS: { value: string; label: string; min: number; max: number | null }[] = [
  { value: '1-10', label: '1–10', min: 1, max: 10 },
  { value: '11-100', label: '11–100', min: 11, max: 100 },
  { value: '101-', label: 'Over 100', min: 101, max: null },
];
const itemsBucket = (min: number | null, max: number | null): string | null => {
  if (min == null && max == null) return null;
  return ITEM_BUCKETS.find((bucket) => bucket.min === min && bucket.max === max)?.value ?? 'other';
};
const itemsWords = (min: number | null, max: number | null): string => {
  const bucket = ITEM_BUCKETS.find((b) => b.min === min && b.max === max);
  if (bucket) return `${bucket.label} items`;
  if (min != null && max != null) return `${min}–${max} items`;
  return min != null ? `${min} or more items` : `Up to ${max} items`;
};

/** "12 Sep 2026 – 30 Sep 2026", "From 12 Sep 2026", "Until 30 Sep 2026". */
const rangeWords = (from: string | null, to: string | null): string => {
  if (from && to) return `${formatDateSafe(from)} – ${formatDateSafe(to)}`;
  if (from) return `From ${formatDateSafe(from)}`;
  return `Until ${formatDateSafe(to)}`;
};

/** Drops the keys with nothing in them, so the request carries only the filters that are on. */
const compact = <T extends Record<string, unknown>>(params: T): { [K in keyof T]?: NonNullable<T[K]> } =>
  Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined && value !== null && value !== '')) as { [K in keyof T]?: NonNullable<T[K]> };

/**
 * The server's list parameters for the header filters. Presets are turned into day ranges here, on
 * the reader's own calendar, so "Last 7 days" means the reader's week and not the server's.
 */
const columnFilterParams = (f: ListFilterValues, now: Date = new Date()) => {
  const received = f.received ? receivedRange(f.received, now) : { from: f.receivedFrom, to: f.receivedTo };
  const ingestedDays = f.ingested ? receivedRange(f.ingested, now) : { from: f.ingestedFrom, to: f.ingestedTo };
  const ingested = localDayInstants(ingestedDays.from, ingestedDays.to);
  const required = f.required ? aheadRange(f.required, now) : { from: f.requiredFrom, to: f.requiredTo };
  return compact({
    leadSource: f.source,
    customer: f.customer,
    ...dueParams(f.due, now),
    // A custom Deadline range is sent INSTEAD of a window, never beside one.
    dueFrom: f.due ? null : f.dueFrom,
    dueTo: f.due ? null : f.dueTo,
    startDate: received.from,
    endDate: received.to,
    ingestedFrom: ingested.from,
    ingestedBefore: ingested.before,
    requiredFrom: required.from,
    requiredTo: required.to,
    itemsMin: f.itemsMin,
    itemsMax: f.itemsMax,
    status: f.status,
    serial: f.serial,
    rfq: f.rfq,
    buyer: f.buyer,
    agreement: f.agreement,
  });
};

const LeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const view = searchParams.get('view') || searchParams.get('state') || undefined;
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission, userData } = useAuth();
  const myUserId = userData?.id ?? null;
  const isManager = userData?.isManager === true || Boolean(userData?.isSuperAdmin);
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 25, page: 0 });
  // Seeded from `?search=` so a link that names a reference (HumanActionCenterPage) lands on it.
  const [search, setSearch] = useState(() => searchParams.get('search') ?? '');
  const [repProfileNoticeDismissed, setRepProfileNoticeDismissed] = useState<boolean>(loadRepProfileNoticeDismissed);
  const dismissRepProfileNotice = () => {
    setRepProfileNoticeDismissed(true);
    try {
      localStorage.setItem(userScopedKey(REP_PROFILE_NOTICE_KEY_BASE), '1');
    } catch {
      // Storage unavailable — the notice returns next visit, which is the safe direction.
    }
  };
  // Every column-header filter — owner and source included — lives on the URL (useListFilters),
  // so Back and a link sent to a colleague keep them.
  const listFilters = useListFilters();
  const ownerView: OwnerView = listFilters.owner ?? DEFAULT_OWNER_VIEW;
  // Rep is a manager's choice; a non-manager arriving on a link with `rep=` is not narrowed by a
  // filter they cannot see or change.
  const repFilter = isManager ? listFilters.rep : null;
  /**
   * "All inquiries" used to send NO queue view, and the server's default for no view is the
   * untriaged inbox (`LeadStatusId == null`, LeadRepository.GetLeadListAsync). Any lifecycle
   * transition stamps a status, so the list called "All" dropped every inquiry the moment
   * someone advanced it. The old behaviour is now "Not opened yet" in the Status header
   * (`status=none` within the queue).
   *
   * A view carried on the URL (a dashboard tile, the Revisions tab) is a queue of its own and
   * wins outright. The plain list is "queue": live leads that have not become an RFQ. Once a lead
   * is an RFQ the work is on the RFQ list, so it leaves this one. A typed search still reaches it
   * ("open"), so a rep who types an RFQ number finds the lead and its "Became an RFQ" status.
   */
  const queueView = view ?? (search.trim() ? 'open' : 'queue');
  // Column layout and row density are settings, not the day's work. They stay one click away
  // rather than sitting on the default path beside the filters a salesperson actually uses.
  const [displayOpen, setDisplayOpen] = useState(false);

  // Row overflow menu
  const [rowMenuAnchor, setRowMenuAnchor] = useState<HTMLElement | null>(null);
  const [rowMenuLeadId, setRowMenuLeadId] = useState<number | null>(null);

  // Assignment: one picker serves the row cell and the bulk toolbar, so both paths take the
  // same two clicks and print the same eligibility reasons.
  const [selection, setSelection] = useState<GridRowSelectionModel>(EMPTY_SELECTION);
  const [quickAssign, setQuickAssign] = useState<{ el: HTMLElement; leads: LeadResponseDTO[] } | null>(null);
  const [reasonPrompt, setReasonPrompt] = useState<{ owner: RoutingOwnerOption; leads: LeadResponseDTO[]; owned: LeadResponseDTO[] } | null>(null);
  // Per-lead failures survive the snackbar: a batch that half worked has to say WHICH half.
  const [assignFailures, setAssignFailures] = useState<AssignFailure[]>([]);

  // One resolve dialog for the whole grid (never one per row).
  const [resolveLead, setResolveLead] = useState<LeadResponseDTO | null>(null);
  const commercialAccess = commercialActionPermissions(hasPermission);
  const canEditLeads = commercialAccess.canEditLeadDecision;
  const canCheckMailboxes = hasPermission('Leads', 'create');

  // AA-01: which columns, in which order, for THIS user — resolved server-side and
  // shared with every other grid that opts in.
  const columnPreferences = useColumnPreferences('leads.list');
  const [density, setDensity] = useState<DensityChoice>(loadDensity);
  const [listView, setListView] = useState<ViewChoice>(loadView);
  const applyView = (value: ViewChoice) => {
    setListView(value);
    try {
      localStorage.setItem(userScopedKey(VIEW_KEY_BASE), value);
    } catch {
      // Storage unavailable — preference just won't persist.
    }
  };

  const applyDensity = (value: DensityChoice) => {
    setDensity(value);
    try {
      localStorage.setItem(userScopedKey(DENSITY_KEY_BASE), value);
    } catch {
      // Storage unavailable — preference just won't persist.
    }
  };

  const closeRowMenu = () => {
    setRowMenuAnchor(null);
    setRowMenuLeadId(null);
  };

  // Everything that can narrow this list. `view` is included because it is a filter the reader
  // did not type: arriving from a dashboard tile can empty the grid with nothing on screen
  // explaining it, which is exactly the "no data" / "filtered to zero" confusion below.
  const columnFiltersActive = anyListFilter({ ...listFilters.values, rep: repFilter });
  const filtersActive = search.trim().length > 0 || Boolean(view) || columnFiltersActive;
  /**
   * useCallback, not a bare closure: the no-rows overlay below is memoised because DataGrid takes
   * a component TYPE, and a fresh function identity each render would rebuild that type and remount
   * the overlay under the user.
   *
   * `keepView` is the tabs-row button: it clears what the reader narrowed, and stays on the tab
   * (Revisions) they are on. The empty state's button also drops a view that came from elsewhere.
   */
  const clearFilters = useCallback((keepView = false) => {
    setSearch('');
    setPaginationModel((current) => ({ ...current, page: 0 }));
    // One URL write for view/state and the filter keys: two writes in one click would each start
    // from the same old URL and the second would put back what the first removed. Anything else on
    // the URL belongs to someone else and stays.
    const next = clearListFilterKeys(searchParams);
    if (!keepView) {
      next.delete('view');
      next.delete('state');
    }
    if (next.toString() !== searchParams.toString()) setSearchParams(next, { replace: true });
  }, [searchParams, setSearchParams]);

  const syncEmailsMutation = useMutation({
    mutationFn: () => leadService.fetchEmails(),
    onSuccess: (report) => {
      // A 200 is not uniformly a success. The server answers 200 with no `mailboxes`
      // count when the tenant has NO active IMAP mailbox — nothing was polled and
      // nothing ever will be — and puts the reason in `message`. Showing a green
      // "synchronized successfully" over that is the exact lie the backend removed
      // from its own side (ING-08, EmailController.ManualFetchAndSaveLeads), and it
      // sends a tenant back to this button forever instead of to mailbox settings.
      if (!report?.mailboxes) {
        enqueueSnackbar(
          report?.message ?? 'No mailbox was polled, so no new leads were fetched.',
          { variant: 'warning', autoHideDuration: 8000 },
        );
        return;
      }
      const found = report.newMessages ?? 0;
      enqueueSnackbar(
        found > 0
          ? `Checked ${report.mailboxes} mailbox(es) — ${found} new message(s) ingested.`
          : `Checked ${report.mailboxes} mailbox(es) — no new messages.`,
        { variant: 'success' },
      );
      queryClient.invalidateQueries({ queryKey: ['leads'] });
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'Email synchronization could not be started. Nothing was changed — try again.'),
      { variant: 'error' },
    ),
  });

  const requestedView = composeLeadsView(queueView, ownerView, myUserId, repFilter);

  /**
   * Everything that narrows the list, in ONE object read by both the grid and Export to Excel, so
   * the spreadsheet is always the list on screen. They used to be two hand copies of the same
   * literal, and a filter added to one and not the other would have exported a different list.
   */
  // The search box already covers the RFQ number, so it travels once, as `search`, never as `rfqno`.
  const listParams = useMemo(() => compact({
    search: search || undefined,
    view: requestedView,
    ...columnFilterParams(listFilters.values),
  }), [search, requestedView, listFilters.values]);

  // A narrower list is a new result set; page 3 of it may not exist. Keyed on the values, not the
  // controls, because Back and pasted links change them too. Adjusted during render, not in an
  // effect: an effect runs after the grid query has already asked for page 3 of the new list.
  const narrowingKey = JSON.stringify(listParams);
  const [pagedNarrowingKey, setPagedNarrowingKey] = useState(narrowingKey);
  if (pagedNarrowingKey !== narrowingKey) {
    setPagedNarrowingKey(narrowingKey);
    if (paginationModel.page !== 0) setPaginationModel({ ...paginationModel, page: 0 });
  }

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['leads', paginationModel, listParams],
    queryFn: () => leadService.getAll({
      ...listParams,
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
    }),
  });

  /**
   * The Customer choices: the customers of the inquiries in this same view (queue, owner, rep),
   * with counts — never the whole customer book, most of which would empty the list. The date
   * button narrows the counts too; the customer itself is left out so the choices do not shrink
   * to the one already picked.
   */
  const customersQuery = useQuery({
    queryKey: ['leads', 'customers', requestedView, listFilters.due],
    queryFn: () => leadService.getListCustomers(requestedView, dueParams(listFilters.due)),
    // The line prints its own failure in the Customer menu; the grid is unaffected.
    meta: { silenceGlobalError: true },
  });
  const customerOptions = useMemo<ListFilterOption<string>[]>(() => {
    const listed = customersQuery.data;
    if (!listed) return [];
    const options = listed.customers.map((c) => ({ value: String(c.customerId), label: `${c.name} (${c.count})` }));
    if (listed.noCustomer > 0) options.push({ value: NO_CUSTOMER, label: `No customer yet (${listed.noCustomer})` });
    return options;
  }, [customersQuery.data]);

  /**
   * The Status choices, from the same read: "Not opened yet" first (the old untriaged inbox), then
   * each status the inquiries in this view carry, in the words every other screen uses.
   */
  const statusOptions = useMemo<ListFilterOption<string>[]>(() => {
    const listed = customersQuery.data;
    const notOpened: ListFilterOption<string> = {
      value: NOT_OPENED,
      label: listed ? `Not opened yet (${listed.notOpened ?? 0})` : 'Not opened yet',
    };
    const statuses = (listed?.statuses ?? []).map((status) => ({
      value: String(status.statusId),
      label: `${leadStatusWords(status.code) ?? status.label} (${status.count})`,
    }));
    return [notOpened, ...statuses];
  }, [customersQuery.data]);

  /**
   * Whether this business unit has ANY live inquiry, independent of every filter on screen.
   *
   * The grid can be narrowed (to "Unassigned" or "Mine"), and with zero leads in the tenant the
   * first thing a rep read was "Every inquiry here already has an owner" — a filtered-to-zero
   * sentence describing a list that had nothing to filter. True zero and filtered-to-zero must
   * never read the same, and only an unfiltered count can tell them apart.
   * One row is enough: only `totalCount` is read. Failure is silent here because the main query
   * reports its own failures and this one only decides which empty-state copy is honest.
   * It counts 'open' (every live inquiry, RFQ'd or not), not the 'queue' the grid opens on: a
   * business unit whose every inquiry is now an RFQ is caught up, not missing a mailbox.
   */
  const totalQuery = useQuery({
    queryKey: ['leads-total'],
    queryFn: () => leadService.getAll({ pageNumber: 1, pageSize: 1, view: 'open' }),
    meta: { silenceGlobalError: true },
  });
  const isTrueZero = totalQuery.data?.totalCount === 0;
  // The queue is empty with nothing narrowing it, yet live inquiries exist: they all became RFQs.
  const isCaughtUp = !filtersActive && (totalQuery.data?.totalCount ?? 0) > 0;
  const canUploadDocuments = hasPermission('Leads', 'create');
  const canConnectMailbox = hasPermission('Email & SMTP');

  /**
   * The eligible-owner list, read once for the page: it says whether the reader can take a lead
   * themselves (myOwnerOption below) and, for a manager, names the people the Rep filter offers.
   */
  const ownerOptions = useOwnerOptions(canEditLeads || isManager);
  const repOptions = useMemo<ListFilterOption<number>[]>(
    () => (ownerOptions.data ?? []).map((option) => ({ value: option.userId, label: option.name })),
    [ownerOptions.data],
  );

  /**
   * The words for whatever the header filters are narrowing by, e.g. "Saudi Electricity Company ·
   * Overdue · Omar Rep", for the "No inquiries match" message.
   */
  const headerFilterWords = useMemo(() => {
    const f = listFilters.values;
    const words: string[] = [];
    if (f.customer != null) {
      words.push(f.customer === NO_CUSTOMER
        ? 'No customer yet'
        : customersQuery.data?.customers.find((c) => String(c.customerId) === f.customer)?.name ?? 'Chosen customer');
    }
    if (f.source != null) words.push(`Came from ${SOURCE_OPTIONS.find((o) => o.value === f.source)?.label ?? f.source}`);
    if (f.due != null) words.push(DUE_WINDOW_LABELS[f.due]);
    else if (f.dueFrom || f.dueTo) words.push(`Deadline ${rangeWords(f.dueFrom, f.dueTo)}`);
    if (f.received != null) words.push(`Received ${RECEIVED_WINDOW_LABELS[f.received].toLowerCase()}`);
    else if (f.receivedFrom || f.receivedTo) words.push(`Received ${rangeWords(f.receivedFrom, f.receivedTo)}`);
    if (f.ingested != null) words.push(`Ingested ${RECEIVED_WINDOW_LABELS[f.ingested].toLowerCase()}`);
    else if (f.ingestedFrom || f.ingestedTo) words.push(`Ingested ${rangeWords(f.ingestedFrom, f.ingestedTo)}`);
    if (f.required != null) words.push(`Wanted in the ${AHEAD_WINDOW_LABELS[f.required].toLowerCase()}`);
    else if (f.requiredFrom || f.requiredTo) words.push(`Wanted ${rangeWords(f.requiredFrom, f.requiredTo)}`);
    if (f.itemsMin != null || f.itemsMax != null) words.push(itemsWords(f.itemsMin, f.itemsMax));
    if (f.serial) words.push(`Serial contains "${f.serial}"`);
    if (f.rfq) words.push(`RFQ/Bid # contains "${f.rfq}"`);
    if (f.buyer) words.push(`Buyer contains "${f.buyer}"`);
    if (f.agreement) words.push(`Agreement contains "${f.agreement}"`);
    if (f.status != null) words.push(statusOptions.find((o) => o.value === f.status)?.label.replace(/ \(\d+\)$/, '') ?? 'Chosen status');
    if (f.owner === 'unassigned') words.push('Unassigned');
    if (f.owner === 'mine') words.push('Mine');
    if (repFilter != null) words.push(repOptions.find((r) => r.value === repFilter)?.label ?? 'Chosen rep');
    return words.join(' · ');
  }, [listFilters.values, repFilter, customersQuery.data, repOptions, statusOptions]);

  /**
   * Owner, Unassigned-or-Mine and "Not opened yet" each have a sentence and a one-step widening
   * button of their own when they alone empty the list. Any other header filter reads
   * "No inquiries match" with the filters in words.
   */
  const otherColumnFiltersActive = anyListFilter({
    ...listFilters.values, rep: repFilter, owner: null, status: listFilters.status === NOT_OPENED ? null : listFilters.status,
  });
  const onlyOwnerOrNotOpened = !otherColumnFiltersActive && !(listFilters.owner && listFilters.status === NOT_OPENED);
  const notOpenedOnly = listFilters.status === NOT_OPENED;
  const setListFilters = listFilters.set;

  /**
   * The top-of-funnel grid shipped MUI's bare "No rows" — the string a rep reads on day one when
   * the mailbox has not yet been configured, and the same string they read when a search matched
   * nothing. Neither reading tells them what to do, and one of the two is a setup problem they can
   * fix themselves. Memoised because DataGrid takes a component TYPE here.
   */
  const noRowsOverlay = useMemo(() => gridEmptyOverlay(isCaughtUp ? {
    title: 'Nothing to decide',
    action: (
      <Button variant="contained" onClick={() => navigate('/procurement/rfqs/all')} sx={{ fontWeight: 700 }}>
        Open RFQs
      </Button>
    ),
  } : {
    title: 'No inquiries yet',
    message: 'Inquiries arrive on their own once a mailbox is connected — or you can upload a customer document from your machine right now.',
    action: (
      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1, alignItems: 'center' }}>
        <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', justifyContent: 'center' }}>
          {canUploadDocuments && (
            <Button variant="contained" onClick={() => navigate('/procurement/leads/manual-upload')} sx={{ fontWeight: 700 }}>
              Upload a document
            </Button>
          )}
          {canConnectMailbox && (
            <Button variant="outlined" startIcon={<InboxIcon />} onClick={() => navigate('/setup/mailboxes')} sx={{ fontWeight: 700 }}>
              Connect the mailbox
            </Button>
          )}
        </Box>
        {/* A button the reader cannot use is a dead end; the reason and the person to ask are the
            next best thing. */}
        {!canConnectMailbox && (
          <Typography variant="caption" color="text.secondary">
            Ask your administrator to connect a mailbox under Setup &gt; Email Inboxes.
          </Typography>
        )}
        {!canUploadDocuments && (
          <Typography variant="caption" color="text.secondary">
            Uploading documents needs Can Create on Leads. Ask your administrator.
          </Typography>
        )}
        {/* An empty grid is also what a BROKEN intake looks like. Inbound Mail is the only screen
            that can tell the reader which of the two they are looking at, so it stays reachable
            from here rather than only from the sidebar. */}
        <Link component="button" type="button" underline="hover" onClick={() => navigate('/procurement/leads/inbound-mail')} sx={{ fontWeight: 700 }}>
          Already connected? Open Inbound Mail
        </Link>
      </Box>
    ),
    // Only a filter can empty a list that has something in it. With nothing in the tenant, the
    // narrowed opening view must not claim "every inquiry already has an owner".
    filtered: filtersActive && !isTrueZero,
    // The list now OPENS on a working set, so "nothing here" most often means "nothing of
    // yours", not "nothing at all" — and the two must never read the same. Each says which
    // filter emptied it and offers the one button that widens it by a single step.
    filteredTitle: !onlyOwnerOrNotOpened
      ? 'No inquiries match'
      : ownerView === 'mine'
      ? 'Nothing is assigned to you'
      : ownerView === 'unassigned'
        ? 'Every inquiry here already has an owner'
        : notOpenedOnly
          ? 'Nothing is waiting to be looked at'
          : 'No inquiries match these filters',
    filteredMessage: !onlyOwnerOrNotOpened
      ? headerFilterWords
      : ownerView === 'mine'
      ? 'No inquiry in this list carries your name right now. The ones nobody has picked up are one click away.'
      : ownerView === 'unassigned'
        ? 'Nothing in this list is waiting to be picked up. Everything already belongs to somebody.'
        : notOpenedOnly
          ? 'Every inquiry has already been opened by someone. The ones in progress are one click away.'
          : 'Nothing matches the search and filters currently applied. Clearing them shows every inquiry this business unit has.',
    filteredAction: !onlyOwnerOrNotOpened ? (
      <Button variant="contained" startIcon={<ClearFiltersIcon />} onClick={() => clearFilters()} sx={{ fontWeight: 700 }}>
        Clear filters
      </Button>
    ) : (
      <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', justifyContent: 'center' }}>
        {notOpenedOnly && ownerView === 'all' && (
          <Button variant="contained" onClick={() => setListFilters({ status: null })} sx={{ fontWeight: 700 }}>
            Show inquiries in progress
          </Button>
        )}
        {ownerView === 'mine' && (
          <Button variant="contained" onClick={() => setListFilters({ owner: 'unassigned', rep: null })} sx={{ fontWeight: 700 }}>
            Show unassigned inquiries
          </Button>
        )}
        {ownerView === 'unassigned' && (
          <Button variant="contained" onClick={() => setListFilters({ owner: null, rep: null })} sx={{ fontWeight: 700 }}>
            Show everyone&apos;s inquiries
          </Button>
        )}
        <Button
          variant="outlined"
          startIcon={<ClearFiltersIcon />}
          onClick={() => clearFilters()}
          sx={{ fontWeight: 700 }}
        >
          Clear filters
        </Button>
      </Box>
    ),
  }), [filtersActive, isTrueZero, isCaughtUp, canUploadDocuments, canConnectMailbox, clearFilters, navigate, ownerView, notOpenedOnly, onlyOwnerOrNotOpened, headerFilterWords, setListFilters]);

  const rows = useMemo(() => data?.items ?? [], [data]);
  // The simple view is a work queue: leads still waiting for a decision first, soonest deadline
  // first, no deadline last. Within the loaded page only; the server pages newest first.
  const workFirstRows = useMemo(() => {
    const due = (r: LeadResponseDTO) => parseDateSafe(r.bidClosingDate)?.getTime() ?? Number.POSITIVE_INFINITY;
    return [...rows].sort((a, b) => Number(isDecided(a)) - Number(isDecided(b)) || due(a) - due(b));
  }, [rows]);

  /**
   * Can the reader take a lead THEMSELVES?
   *
   * `PUT .../owner` answers 409 for a user governed routing will not accept, so an "Assign to me"
   * that is always offered is a false affordance that fails after the click. The eligible-owner
   * list is the server's own verdict, so it is read once for the page and the button appears only
   * when the answer is yes — with the reason printed above the grid when it is no.
   * (`ownerOptions` is read further up: the Rep filter and the empty state need it too.)
   */
  const myOwnerOption = useMemo(
    () => (ownerOptions.data ?? []).find((option) => option.userId === myUserId) ?? null,
    [ownerOptions.data, myUserId],
  );
  const iCanTakeLeads = myOwnerOption?.acceptsManualAssignment === true;
  /** Only stated once we actually know — never inferred from a list that has not loaded. */
  const whyICannotTakeLeads = canEditLeads && !ownerOptions.isLoading && !ownerOptions.isError && !iCanTakeLeads
    ? (myOwnerOption?.eligibilityReason?.trim()
      || 'You do not have a Sales Rep profile yet, so leads cannot be routed to you. Ask an administrator to add one under Team & exceptions > Sales reps.')
    : null;

  /**
   * The rows the checkboxes point at. Resolved against the CURRENT PAGE only: this grid pages
   * server-side, so MUI's "exclude" model means "everything not ticked" over rows the client has
   * never seen, and acting on that would be a promise the client cannot keep.
   */
  const selectedLeads = useMemo(() => (
    selection.type === 'exclude'
      ? rows.filter((row) => !selection.ids.has(row.id))
      : rows.filter((row) => selection.ids.has(row.id))
  ), [selection, rows]);

  /**
   * One assignment per lead through the endpoint that already exists —
   * `PUT /api/commercial-routing/leads/{id}/owner`. Deliberately NOT `queue/bulk-assign`: that
   * takes WorkItemIds, so it can only reach leads already sitting in the routing queue, which is
   * a strict subset of what a reader can tick here.
   *
   * Sequential, and every failure is caught and NAMED rather than aborting the run: a batch that
   * stops at the first 409 leaves the reader with no idea which inquiries moved.
   */
  const assignMutation = useMutation({
    mutationFn: async ({ owner, leads, reason }: { owner: RoutingOwnerOption; leads: LeadResponseDTO[]; reason?: string }) => {
      const failures: AssignFailure[] = [];
      let assigned = 0;
      for (const lead of leads) {
        const identity = `lead-owner-${lead.id}-${crypto.randomUUID()}`;
        try {
          await commercialRoutingService.changeLeadOwner(lead.id, {
            action: LEAD_OWNERSHIP_ACTION.Assign,
            assignedToUserId: owner.userId,
            expectedAssignmentVersion: lead.assignmentVersion ?? 1,
            idempotencyKey: identity,
            correlationId: identity,
            comment: reason ?? null,
          });
          assigned += 1;
        } catch (error: unknown) {
          failures.push({
            leadId: lead.id,
            label: leadLabel(lead),
            message: presentableErrorMessage(
              error,
              'The owner could not be changed. This inquiry still belongs to whoever held it before.',
            ),
          });
        }
      }
      return { assigned, failures, ownerName: owner.name, total: leads.length };
    },
    onSuccess: ({ assigned, failures, ownerName, total }) => {
      setQuickAssign(null);
      setReasonPrompt(null);
      setAssignFailures(failures);
      if (failures.length === 0) {
        enqueueSnackbar(
          total === 1
            ? `Assigned to ${ownerName}.`
            : `${assigned} ${assigned === 1 ? 'inquiry' : 'inquiries'} assigned to ${ownerName}.`,
          { variant: 'success' },
        );
        setSelection(EMPTY_SELECTION);
      } else {
        // The ones that failed stay ticked, so retrying is one click and not a re-selection.
        setSelection({ type: 'include', ids: new Set<GridRowId>(failures.map((f) => f.leadId)) });
        enqueueSnackbar(
          assigned === 0
            ? `Nothing was assigned to ${ownerName}. The reasons are listed above the grid.`
            : `${assigned} of ${total} assigned to ${ownerName}. ${failures.length} could not be — the reasons are listed above the grid.`,
          { variant: assigned === 0 ? 'error' : 'warning', autoHideDuration: 10000 },
        );
      }
      queryClient.invalidateQueries({ queryKey: ['leads'] });
    },
  });

  /**
   * A reason is asked for ONLY when at least one of the targets already belongs to somebody else.
   * Taking an unowned lead never prompts, which is the point of the whole screen: the cheapest
   * action here is picking up your own work.
   */
  const assignTo = useCallback((owner: RoutingOwnerOption, targets: LeadResponseDTO[]) => {
    const toAssign = targets.filter((lead) => lead.assignedToId !== owner.userId);
    if (toAssign.length === 0) {
      setQuickAssign(null);
      enqueueSnackbar(
        targets.length === 1
          ? `That inquiry already belongs to ${owner.name}.`
          : `Every selected inquiry already belongs to ${owner.name}.`,
        { variant: 'info' },
      );
      return;
    }
    const owned = toAssign.filter((lead) => assignmentNeedsReason(lead.assignedToId, owner.userId));
    if (owned.length > 0) {
      setQuickAssign(null);
      setReasonPrompt({ owner, leads: toAssign, owned });
      return;
    }
    assignMutation.mutate({ owner, leads: toAssign });
  }, [assignMutation, enqueueSnackbar]);

  /** Click 2 of 2 from the picker. */
  const pickOwner = useCallback((owner: RoutingOwnerOption) => {
    assignTo(owner, quickAssign?.leads ?? []);
  }, [assignTo, quickAssign]);

  /**
   * What the reader may actually act on out of what they ticked.
   *
   * A rep may take work nobody holds; only a manager moves a colleague's. Rather than letting the
   * server refuse half a batch with a 403 whose sentence the error layer generalises away, the
   * bar states up front which rows it will leave alone.
   */
  const takeableSelected = useMemo(
    () => (isManager ? selectedLeads : selectedLeads.filter((lead) => lead.assignedToId == null)),
    [isManager, selectedLeads],
  );
  const notMineToMove = selectedLeads.length - takeableSelected.length;

  /** Click 1 of 1 — taking your own work costs a single click and opens nothing. */
  const takeLeads = useCallback((leads: LeadResponseDTO[]) => {
    if (myOwnerOption) assignTo(myOwnerOption, leads);
  }, [assignTo, myOwnerOption]);

  // Decision Brief summaries: one batched call for the current page's ids,
  // fired only after the leads query resolves. This never blocks the grid —
  // it is a separate query, and on error (e.g. the engine isn't deployed yet)
  // the Decision / Estimated value cells simply render nothing.
  const visibleLeadIds = useMemo(() => rows.map((l) => l.id), [rows]);
  const decisionQuery = useQuery({
    queryKey: ['lead-decision-summaries', visibleLeadIds],
    queryFn: () => decisionService.getDecisionSummaries(visibleLeadIds),
    enabled: visibleLeadIds.length > 0,
    retry: false,
    staleTime: 60_000,
  });
  const decisionSummaries = decisionQuery.data?.summaries;
  const decisionsLoading = visibleLeadIds.length > 0 && decisionQuery.isPending;

  /**
   * Where clicking a row's NAME — its serial or RFQ number — takes the reader: the same place the
   * row's own button goes. "Decide" when the reader may decide, the inquiry record otherwise. One
   * target for the name and the button, so the two can never disagree about what "open" means.
   */
  const openLeadPath = (lead: LeadResponseDTO): string => commercialAccess.canOpenLeadWorkbench
    ? `/procurement/leads/${lead.id}/workbench`
    : `/procurement/leads/view/${lead.id}`;

  const columns: GridColDef<LeadResponseDTO>[] = [
    {
      field: 'nexoraSerial',
      headerName: 'Nexora Serial',
      // WIDE ENOUGH FOR THE WHOLE SERIAL, and that is a correctness requirement rather than
      // cosmetics. The serial is `NOOR-SONS-LLC-2026-000059` — 25 characters, and the only
      // part that differs between two leads of the same tenant and year is the tail. At 180px
      // the bold monospace clipped it to `NOOR-SONS-LLC-2026-000`, so every lead on the list
      // rendered an IDENTICAL identifier: two different inquiries, indistinguishable at a
      // glance, with no visual cue that anything had been cut.
      width: 260,
      valueGetter: (_value, row) => row.nexoraSerial || row.commercialCaseReference || '',
      renderCell: (p) => {
        const serial = p.row.nexoraSerial || p.row.commercialCaseReference;
        return serial ? (
          <Link component="button" type="button" underline="hover" onClick={() => navigate(openLeadPath(p.row))}
            sx={{ fontWeight: 800, fontFamily: 'monospace', fontSize: '0.8rem' }}>
            {serial}
          </Link>
        ) : <Typography variant="body2" color="text.disabled">Unassigned</Typography>;
      },
    },
    {
      field: 'rfqno',
      headerName: 'RFQ/Bid #',
      width: 180,
      renderCell: (p) => {
        const raw = (p.row.rfqno ?? '').trim();
        const isMissing = !raw || raw.toUpperCase() === 'NO RFQ #';
        if (isMissing) {
          return (
            <Typography variant="body2" sx={{ color: 'text.disabled', fontStyle: 'italic' }}>
              No RFQ/Bid # yet
            </Typography>
          );
        }
        return (
          <Link
            component="button"
            type="button"
            underline="hover"
            onClick={() => navigate(openLeadPath(p.row))}
            sx={{ fontWeight: 500, fontSize: '0.85rem', color: 'primary.main', textAlign: 'left' }}
          >
            {raw}
          </Link>
        );
      },
    },
    {
      field: 'client',
      headerName: 'Client',
      flex: 1,
      minWidth: 190,
      sortable: false,
      filterable: false,
      valueGetter: (_value, row) => row.customerName || '',
      renderCell: (p) => (
        <ClientCell
          lead={p.row}
          canEdit={canEditLeads}
          onResolve={() => setResolveLead(p.row)}
        />
      ),
    },
    {
      field: 'buyer',
      // Renamed from "Buyer": this column holds a PERSON and their email, and the
      // old heading read like a company — which is half the reason nobody could
      // tell which client a lead came from.
      headerName: 'Buyer contact',
      flex: 1,
      minWidth: 200,
      sortable: false,
      renderCell: (p) => {
        const name = (p.row.buyersName ?? '').trim();
        const unknownBuyer = !name || name.toLowerCase() === 'unknown buyer';
        const contact = buyerContact(p.row);
        return (
          <Box sx={{ lineHeight: 1.3, py: 0.25 }}>
            {unknownBuyer ? (
              <Typography sx={{ fontSize: '0.85rem', color: 'text.disabled' }}>
                Buyer not identified yet
              </Typography>
            ) : (
              <Typography sx={{ fontWeight: 600, fontSize: '0.85rem', color: 'text.primary' }}>
                {name}
              </Typography>
            )}
            <Typography
              variant="caption"
              sx={{
                color: contact.internal ? 'text.disabled' : 'text.secondary',
                fontSize: '0.7rem',
                display: 'flex',
                alignItems: 'center',
                gap: 0.5,
              }}
            >
              {!contact.internal && <EmailIcon sx={{ fontSize: 12 }} />}
              {contact.text}
            </Typography>
          </Box>
        );
      },
    },
    {
      field: 'recDate',
      headerName: 'Received',
      width: 110,
      renderCell: (p) => {
        const label = formatDateSafe(p.row.recDate);
        return (
          <Typography variant="body2" sx={{ fontSize: '0.8rem', color: label === '—' ? 'text.disabled' : 'text.primary' }}>
            {label}
          </Typography>
        );
      },
    },
    {
      field: 'ingestedAtUtc',
      headerName: 'Ingested',
      width: 170,
      // Audit-grade ingestion timestamp: earliest source received_on from the
      // backend (`ingestedOn`), with the legacy pipeline timestamp and
      // createdDate as display fallbacks for older payloads.
      valueGetter: (_value, row) => row.ingestedOn || row.ingestedAtUtc || row.createdDate || '',
      renderCell: (p) => {
        const value = p.row.ingestedOn || p.row.ingestedAtUtc || p.row.createdDate;
        const label = formatDateSafe(value);
        return (
          <Box sx={{ lineHeight: 1.3, py: 0.25 }}>
            <Tooltip title={value ? `Entered Nexora ${new Date(value).toLocaleString()}` : 'Not recorded'}>
              <Typography variant="body2" sx={{ fontSize: '0.8rem', color: label === '—' ? 'text.disabled' : 'text.primary' }}>
                {label === '—' ? label : `Ingested ${label}`}
              </Typography>
            </Tooltip>
            {/* Audit fairness: flag leads that entered Nexora after their deadline. */}
            <LateIngestedBadge
              lateIngested={p.row.lateIngested}
              ingestedOn={value}
              dueDate={p.row.bidClosingDate || p.row.subDate}
            />
          </Box>
        );
      },
    },
    {
      field: 'bidClosingDate',
      headerName: 'Deadline',
      width: 120,
      renderCell: (p) => {
        const sx = deadlineSx(p.row.bidClosingDate);
        return (
          <Typography variant="body2" sx={{ fontSize: '0.8rem', ...sx }}>
            {formatDeadlineDate(p.row.bidClosingDate)}
          </Typography>
        );
      },
    },
    {
      // FR-RFQ-04. The buyer's own required delivery date, beside the bid deadline and
      // never merged with it: one says when the bid is due back, the other says when the
      // goods are wanted. A missing value is an explicit "Not stated", not a blank cell
      // that reads like a loading state.
      field: 'requiredDeliveryDate',
      headerName: 'Required delivery',
      width: 150,
      renderCell: (p) => {
        const value = p.row.requiredDeliveryDate;
        return value ? (
          <Tooltip title="Delivery date requested by the buyer — not the bid deadline">
            <Typography variant="body2" sx={{ fontSize: '0.8rem' }}>
              {formatDateSafe(value)}
            </Typography>
          </Tooltip>
        ) : (
          <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.disabled', fontStyle: 'italic' }}>
            Not stated
          </Typography>
        );
      },
    },
    {
      // FR-RFQ-04. Hidden by default (see the server catalog); a Saudi tender publishes
      // its closing date in Hijri and this is the cross-check against the Gregorian one.
      field: 'bidClosingDateHijri',
      headerName: 'Deadline (Hijri)',
      width: 140,
      renderCell: (p) => (
        <Typography
          variant="body2"
          sx={{ fontSize: '0.8rem', fontFamily: 'monospace', color: p.row.bidClosingDateHijri ? 'text.primary' : 'text.disabled' }}
        >
          {p.row.bidClosingDateHijri || 'Not stated'}
        </Typography>
      ),
    },
    {
      // FR-RFQ-03. The standing agreement this inquiry is called off against — not the
      // inquiry's own reference, which is the RFQ # column.
      field: 'agreementReference',
      headerName: 'Agreement reference',
      width: 170,
      renderCell: (p) => (
        <Typography
          variant="body2"
          sx={{ fontSize: '0.8rem', color: p.row.agreementReference ? 'text.primary' : 'text.disabled' }}
        >
          {p.row.agreementReference || 'None'}
        </Typography>
      ),
    },
    {
      field: 'itemCount',
      headerName: 'Items',
      width: 80,
      type: 'number',
      align: 'right',
      headerAlign: 'right',
      renderCell: (p) => (
        <Typography variant="body2" sx={{ fontSize: '0.85rem', fontWeight: 500 }}>
          {p.row.itemCount ?? 0}
        </Typography>
      ),
    },
    {
      field: 'leadSource',
      headerName: 'Source',
      width: 110,
      renderCell: (p) => (
        <Chip
          label={p.row.leadSource || '—'}
          size="small"
          variant="outlined"
          sx={{ fontWeight: 600, fontSize: '0.7rem' }}
        />
      ),
    },
    {
      field: 'status',
      headerName: 'Status',
      width: 130,
      sortable: false,
      renderCell: (p) => {
        const meta = leadStatus(p.row);
        return (
          <Chip
            label={meta.label}
            color={meta.color}
            variant={meta.variant}
            size="small"
            sx={{ fontWeight: 600, fontSize: '0.7rem' }}
          />
        );
      },
    },
    {
      // WHO OWNS IT, and the control that changes it — one cell, because they are the same
      // question. Assigning a lead used to mean opening it, opening an Owner menu, opening a
      // dialog, picking a name and navigating back: four clicks and two page loads per lead,
      // from the screen a rep spends the day on. Lifted from OutstandingLeadsPage, which has
      // had the two-click version all along on a queue most reps never open.
      field: 'assignee',
      headerName: 'Owner',
      width: 210,
      sortable: false,
      filterable: false,
      valueGetter: (_value, row) => row.assignedToFullName || '',
      renderCell: (p) => {
        const owner = (p.row.assignedToFullName ?? '').trim();
        if (!owner) {
          if (!canEditLeads) {
            return (
              <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.disabled', fontStyle: 'italic' }}>
                Unassigned
              </Typography>
            );
          }
          return (
            <Box sx={{ lineHeight: 1.3, py: 0.25 }}>
              {/* One click, no menu, no dialog — the commonest action on the screen. Shown only
                  when governed routing would actually accept this reader; when it would not, the
                  sentence saying so is printed once above the grid rather than fifty times here. */}
              {iCanTakeLeads && (
                <Button
                  size="small"
                  variant="contained"
                  disableElevation
                  disabled={assignMutation.isPending}
                  onClick={() => takeLeads([p.row])}
                  sx={{ fontWeight: 800, fontSize: '0.7rem', py: 0.25, px: 1, borderRadius: 1.5, textTransform: 'none' }}
                >
                  Assign to me
                </Button>
              )}
              {/* Handing a lead to a COLLEAGUE is a manager's decision — the server answers 403
                  otherwise, and a 403 whose sentence the error layer replaces with a generic one
                  teaches nothing. So the control is simply not offered. */}
              {isManager && (
                <Link
                  component="button"
                  type="button"
                  underline="hover"
                  onClick={(event) => setQuickAssign({ el: event.currentTarget, leads: [p.row] })}
                  sx={{ display: 'block', fontSize: '0.7rem', fontWeight: 700, mt: iCanTakeLeads ? 0.25 : 0 }}
                >
                  {iCanTakeLeads ? 'Someone else…' : 'Assign to…'}
                </Link>
              )}
              {!iCanTakeLeads && !isManager && (
                <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.disabled', fontStyle: 'italic' }}>
                  Unassigned
                </Typography>
              )}
            </Box>
          );
        }
        return (
          <Box sx={{ lineHeight: 1.3, py: 0.25 }}>
            <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
              <UserIcon sx={{ fontSize: 14, color: 'primary.main' }} />
              <Typography sx={{ fontWeight: 700, fontSize: '0.8rem' }}>{owner}</Typography>
            </Stack>
            {/* Moving work that already belongs to somebody is a manager's call. */}
            {canEditLeads && isManager && (
              <Link
                component="button"
                type="button"
                underline="hover"
                onClick={(event) => setQuickAssign({ el: event.currentTarget, leads: [p.row] })}
                sx={{ fontSize: '0.7rem', fontWeight: 700 }}
              >
                Reassign
              </Link>
            )}
          </Box>
        );
      },
    },
    {
      field: 'decision',
      headerName: "Nexora's read",
      width: 230,
      sortable: false,
      filterable: false,
      renderCell: (p) => {
        if (decisionQuery.isError) return null;
        if (decisionsLoading) {
          return <Skeleton variant="rounded" width={96} height={22} sx={{ borderRadius: 3 }} />;
        }
        const summary = decisionSummaries?.[String(p.row.id)];
        if (!summary) return null;
        const meta = DECISION_META[summary.recommendation];
        if (!meta) return null;
        // The read and its reasons sit in the open. A tooltip hid the one line that tells a rep
        // why the word says what it says, which is the line that makes the word believable.
        const facts = decisionFacts(summary);
        return (
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', minWidth: 0 }}>
            <Chip
              label={meta.label}
              color={meta.color}
              size="small"
              sx={{ fontWeight: 700, fontSize: '0.7rem', flexShrink: 0 }}
            />
            {facts.length > 0 && (
              <Typography variant="caption" color="text.secondary" noWrap title={facts.join(' · ')}>
                {facts.join(' · ')}
              </Typography>
            )}
          </Stack>
        );
      },
    },
    {
      field: 'estimatedValue',
      headerName: 'Estimated value',
      width: 130,
      align: 'right',
      headerAlign: 'right',
      sortable: false,
      filterable: false,
      renderCell: (p) => {
        if (decisionQuery.isError) return null;
        if (decisionsLoading) {
          return <Skeleton width={56} height={18} />;
        }
        const summary = decisionSummaries?.[String(p.row.id)];
        if (!summary) return null;
        if (summary.estimatedValue == null) {
          return <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.disabled' }}>—</Typography>;
        }
        return (
          <Typography variant="body2" sx={{ fontSize: '0.8rem', fontWeight: 500 }}>
            {summary.estimatedValue.toLocaleString('en-US', { maximumFractionDigits: 0 })}
          </Typography>
        );
      },
    },
    {
      field: 'actions',
      headerName: t('actions'),
      width: 130,
      sortable: false,
      filterable: false,
      hideable: false,
      renderCell: (p) => {
        const decided = isDecided(p.row);
        return (
          <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
            <Tooltip title="Open the inquiry record">
              <IconButton
                size="small"
                aria-label="Open the inquiry record"
                sx={{ color: 'primary.main' }}
                onClick={() => navigate(`/leads/view/${p.row.id}`)}
              >
                <ViewIcon fontSize="small" />
              </IconButton>
            </Tooltip>
            {commercialAccess.canOpenLeadWorkbench && (
              <Button
                size="small"
                variant={decided ? 'text' : 'outlined'}
                aria-label={`Decide ${p.row.rfqno || `lead ${p.row.id}`}`}
                onClick={() => navigate(`/procurement/leads/${p.row.id}/workbench`)}
                sx={{ fontWeight: 700, minWidth: 0, px: 1.25, whiteSpace: 'nowrap' }}
              >
                {decided ? 'See the decision' : 'Decide'}
              </Button>
            )}
            {!decided && (
              <Tooltip title="More actions">
                <IconButton
                  size="small"
                  aria-label="More actions"
                  onClick={(e) => {
                    setRowMenuAnchor(e.currentTarget);
                    setRowMenuLeadId(p.row.id);
                  }}
                >
                  <MoreIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            )}
          </Stack>
        );
      },
    },
  ];

  // Reordered to this user's saved layout. Falls back to the declared order above when the
  // preference call has not resolved or failed.
  const orderedColumns = columnPreferences.arrangeColumns(columns);
  // The simple view reuses the spreadsheet's own cell renderers, so both views show the same
  // facts with the same controls; only the grouping differs.
  const col = (field: string) => columns.find((c) => c.field === field)!;
  const simpleColumns: GridColDef<LeadResponseDTO>[] = [
    {
      field: 'customerBid',
      headerName: 'Customer & bid',
      flex: 1,
      minWidth: 240,
      sortable: false,
      filterable: false,
      valueGetter: (_value, row) => row.customerName || '',
      renderCell: (p) => {
        const items = p.row.itemCount ?? 0;
        const marker = (p.row.duplicateStatus ?? '').toLowerCase();
        return (
          <Box sx={{ lineHeight: 1.3, py: 0.25, minWidth: 0 }}>
            {col('client').renderCell!(p)}
            <Stack direction="row" spacing={0.75} useFlexGap sx={{ alignItems: 'center', flexWrap: 'wrap', mt: 0.25 }}>
              {col('rfqno').renderCell!(p)}
              <Typography variant="caption" color="text.secondary">
                {items > 0 ? `· ${items} ${items === 1 ? 'item' : 'items'}` : '· items not listed yet'}
                {p.row.deliveryLocation ? ` · to ${p.row.deliveryLocation}` : ''}
              </Typography>
              {(marker === 'suspected' || marker === 'confirmed') && (
                <Chip label="Possible duplicate" color="warning" variant="outlined" size="small" sx={{ fontWeight: 700, height: 20 }} />
              )}
            </Stack>
          </Box>
        );
      },
    },
    {
      field: 'when',
      headerName: 'Deadline',
      width: 130,
      sortable: false,
      filterable: false,
      valueGetter: (_value, row) => row.bidClosingDate || '',
      renderCell: (p) => {
        const due = formatDeadlineDate(p.row.bidClosingDate);
        const wanted = formatDateSafe(p.row.requiredDeliveryDate);
        // Once decided, the deadline is history, not urgency.
        const { text, tone } = deadlineWords(p.row.bidClosingDate);
        const quiet = isDecided(p.row);
        return (
          <Box sx={{ lineHeight: 1.3, py: 0.25 }}>
            <Typography variant="body2" sx={{ fontSize: '0.85rem', fontWeight: quiet ? 500 : 700, color: quiet ? 'text.secondary' : DEADLINE_COLOR[tone] }}>
              {text}
            </Typography>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
              {due !== '—' ? due : wanted !== '—' ? `Wanted by ${wanted}` : ''}
            </Typography>
          </Box>
        );
      },
    },
    { ...col('assignee'), headerName: 'Owner', width: 160 },
    {
      field: 'worth',
      headerName: 'Status',
      width: 220,
      sortable: false,
      filterable: false,
      renderCell: (p) => {
        if (isDecided(p.row)) {
          const meta = leadStatus(p.row);
          return <Chip label={meta.label} color={meta.color} variant={meta.variant} size="small" sx={{ fontWeight: 700, fontSize: '0.7rem' }} />;
        }
        const flagged = leadStatus(p.row);
        const flagChip = flagged.label !== 'Open' && flagged.label !== 'New'
          ? <Chip label={flagged.label} color={flagged.color} variant={flagged.variant} size="small" sx={{ fontWeight: 700, fontSize: '0.7rem' }} />
          : null;
        if (decisionQuery.isError) {
          return <Typography variant="caption" color="text.secondary">Read unavailable</Typography>;
        }
        if (decisionsLoading) {
          return <Skeleton variant="rounded" width={96} height={22} sx={{ borderRadius: 3 }} />;
        }
        const summary = decisionSummaries?.[String(p.row.id)];
        const meta = summary ? DECISION_META[summary.recommendation] : undefined;
        if (!summary || !meta) {
          return flagChip ?? <Typography variant="caption" color="text.secondary">No read yet</Typography>;
        }
        // The deadline has its own column; saying it here too was the same fact twice.
        const facts = decisionFacts({ ...summary, daysLeft: null });
        return (
          <Box sx={{ lineHeight: 1.3, py: 0.25, minWidth: 0 }}>
            <Stack direction="row" spacing={0.5}>
              <Chip label={meta.label} color={meta.color} size="small" sx={{ fontWeight: 700, fontSize: '0.7rem' }} />
              {flagChip}
            </Stack>
            {facts.length > 0 && (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', whiteSpace: 'normal' }} title={facts.join(' · ')}>
                {facts.join(' · ')}
              </Typography>
            )}
          </Box>
        );
      },
    },
    {
      field: 'simpleAction',
      headerName: t('actions'),
      width: 120,
      sortable: false,
      filterable: false,
      hideable: false,
      renderCell: (p) => {
        const decided = isDecided(p.row);
        return commercialAccess.canOpenLeadWorkbench ? (
          <Button
            size="small"
            variant="outlined"
            color={decided ? 'inherit' : 'primary'}
            aria-label={`Decide ${p.row.rfqno || `lead ${p.row.id}`}`}
            onClick={() => navigate(`/procurement/leads/${p.row.id}/workbench`)}
            sx={{ fontWeight: 700, width: 108, whiteSpace: 'nowrap', ...(decided ? { color: 'text.secondary', borderColor: 'divider' } : {}) }}
          >
            {decided ? 'See decision' : 'Decide'}
          </Button>
        ) : (
          <Tooltip title="Open the inquiry record">
            <IconButton size="small" aria-label="Open the inquiry record" sx={{ color: 'primary.main' }} onClick={() => navigate(`/leads/view/${p.row.id}`)}>
              <ViewIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        );
      },
    },
  ];
  // Owner 2026-09-29: the filters live in the column headers they narrow — every column the server
  // can filter carries a small filter button; the list, the count and Excel follow it. Columns that
  // are worked out on screen (Nexora's read, Estimated value) have none.
  type FilterKind = 'customer' | 'customerAndSource' | 'source' | 'due' | 'dueHijri' | 'received' | 'ingested' | 'required'
    | 'items' | 'status' | 'owner' | 'serial' | 'rfq' | 'buyer' | 'agreement';
  const filterFor: Record<string, FilterKind> = {
    customerBid: 'customerAndSource', client: 'customer', leadSource: 'source',
    when: 'due', bidClosingDate: 'due', bidClosingDateHijri: 'dueHijri',
    recDate: 'received', ingestedAtUtc: 'ingested', requiredDeliveryDate: 'required', itemCount: 'items',
    status: 'status', worth: 'status', assignee: 'owner',
    nexoraSerial: 'serial', rfqno: 'rfq', buyer: 'buyer', agreementReference: 'agreement',
  };
  const f = listFilters.values;
  const customerList = {
    label: 'Customer', noun: 'Customer', anyLabel: 'Any customer', searchable: true,
    options: customerOptions, value: f.customer,
    loading: customersQuery.isLoading, error: customersQuery.isError,
    onChange: (customer: string | null) => setListFilters({ customer }),
  };
  const sourceList = {
    label: 'Came from', noun: 'Source', anyLabel: 'Any source',
    options: SOURCE_OPTIONS, value: f.source,
    onChange: (source: string | null) => setListFilters({ source }),
  };
  const textFilter = (key: 'serial' | 'rfq' | 'buyer' | 'agreement') => ({
    value: f[key],
    onChange: (value: string | null) => setListFilters({ [key]: value }),
  });
  // Owner: Anyone · Unassigned · Mine, then (a manager) each rep. A rep is a slice of Anyone.
  const ownerFilterOptions: ListFilterOption<string>[] = [
    { value: 'unassigned', label: 'Unassigned' },
    ...(myUserId != null ? [{ value: 'mine', label: 'Mine' }] : []),
    ...(isManager ? repOptions.map((option) => ({ value: `rep:${option.value}`, label: option.label })) : []),
  ];
  const ownerFilterValue = f.owner ?? (repFilter != null ? `rep:${repFilter}` : null);
  const pickOwnerFilter = (choice: string | null) => {
    if (choice?.startsWith('rep:')) setListFilters({ owner: null, rep: Number(choice.slice(4)) });
    else setListFilters({ owner: choice === 'unassigned' || choice === 'mine' ? choice : null, rep: null });
  };
  const itemsValue = itemsBucket(f.itemsMin, f.itemsMax);
  const dueHeader = (title: string, noun: string) => (
    <HeaderFilter title={title} noun={noun} anyLabel="Any date" anyLast
      options={DUE_WINDOWS.map((window) => ({ value: window, label: DUE_WINDOW_LABELS[window] }))}
      value={f.due}
      onChange={(due: DueWindow | null) => setListFilters({ due, dueFrom: null, dueTo: null })}
      range={{ from: f.dueFrom, to: f.dueTo, onApply: (dueFrom, dueTo) => setListFilters({ due: null, dueFrom, dueTo }) }} />
  );
  const renderFilterHeader = (kind: FilterKind, title: string): React.ReactNode => {
    switch (kind) {
      case 'customer':
        return <HeaderFilter title={title} {...customerList} />;
      case 'customerAndSource':
        // The simple view has no Source column, so "where it came from" rides with the customer.
        return <HeaderFilter title={title} noun="Customer" groups={[customerList, sourceList]} />;
      case 'source':
        return <HeaderFilter title={title} {...sourceList} />;
      case 'due':
        return dueHeader(title, 'Bid due date');
      case 'dueHijri':
        return dueHeader(title, 'Bid due date (Hijri)');
      case 'received':
        return (
          <HeaderFilter title={title} noun="Received date" anyLabel="Any date" anyLast
            options={RECEIVED_WINDOWS.map((window) => ({ value: window, label: RECEIVED_WINDOW_LABELS[window] }))}
            value={f.received}
            onChange={(received: ReceivedWindow | null) => setListFilters({ received, receivedFrom: null, receivedTo: null })}
            range={{ from: f.receivedFrom, to: f.receivedTo, onApply: (receivedFrom, receivedTo) => setListFilters({ received: null, receivedFrom, receivedTo }) }} />
        );
      case 'ingested':
        return (
          <HeaderFilter title={title} noun="Ingested date" anyLabel="Any date" anyLast
            options={RECEIVED_WINDOWS.map((window) => ({ value: window, label: RECEIVED_WINDOW_LABELS[window] }))}
            value={f.ingested}
            onChange={(ingested: ReceivedWindow | null) => setListFilters({ ingested, ingestedFrom: null, ingestedTo: null })}
            range={{ from: f.ingestedFrom, to: f.ingestedTo, onApply: (ingestedFrom, ingestedTo) => setListFilters({ ingested: null, ingestedFrom, ingestedTo }) }} />
        );
      case 'required':
        return (
          <HeaderFilter title={title} noun="Required delivery" anyLabel="Any date" anyLast
            options={AHEAD_WINDOWS.map((window) => ({ value: window, label: AHEAD_WINDOW_LABELS[window] }))}
            value={f.required}
            onChange={(required: AheadWindow | null) => setListFilters({ required, requiredFrom: null, requiredTo: null })}
            range={{ from: f.requiredFrom, to: f.requiredTo, onApply: (requiredFrom, requiredTo) => setListFilters({ required: null, requiredFrom, requiredTo }) }} />
        );
      case 'items':
        return (
          <HeaderFilter title={title} noun="Items" anyLabel="Any" anyLast
            options={[
              ...ITEM_BUCKETS.map((bucket) => ({ value: bucket.value, label: bucket.label })),
              ...(itemsValue === 'other' ? [{ value: 'other', label: itemsWords(f.itemsMin, f.itemsMax) }] : []),
            ]}
            value={itemsValue}
            onChange={(choice: string | null) => {
              // "other" is a hand-edited range already on the URL; picking it again changes nothing.
              if (choice === 'other') return;
              const bucket = ITEM_BUCKETS.find((b) => b.value === choice);
              setListFilters({ itemsMin: bucket?.min ?? null, itemsMax: bucket?.max ?? null });
            }} />
        );
      case 'status':
        return (
          <HeaderFilter title={title} noun="Status" anyLabel="Any status"
            options={statusOptions} value={f.status}
            loading={customersQuery.isLoading} error={customersQuery.isError}
            onChange={(status: string | null) => setListFilters({ status })} />
        );
      case 'owner':
        return (
          <HeaderFilter title={title} noun="Owner" anyLabel="Anyone"
            options={ownerFilterOptions} value={ownerFilterValue} onChange={pickOwnerFilter} />
        );
      case 'serial':
        return <HeaderFilter title={title} noun="Serial" text={textFilter('serial')} />;
      case 'rfq':
        return <HeaderFilter title={title} noun="RFQ/Bid number" text={textFilter('rfq')} />;
      case 'buyer':
        return <HeaderFilter title={title} noun="Buyer" text={textFilter('buyer')} />;
      case 'agreement':
        return <HeaderFilter title={title} noun="Agreement" text={textFilter('agreement')} />;
      default:
        return title;
    }
  };
  const withHeaderFilter = (column: GridColDef<LeadResponseDTO>): GridColDef<LeadResponseDTO> => {
    const kind = filterFor[column.field];
    if (!kind) return column;
    const title = column.headerName ?? '';
    return { ...column, renderHeader: () => renderFilterHeader(kind, title) };
  };
  const gridColumns = (listView === 'simple' ? simpleColumns : orderedColumns).map(withHeaderFilter);

  const totalCount = data?.totalCount ?? 0;
  /**
   * The grid's row count holds its last known value while the next page loads. A new page is a new
   * query key, so `data` is briefly undefined; a row count of 0 made DataGrid clamp the page back
   * to the first one, and "next page" bounced straight back to page 1. (MUI's documented fix for
   * server-side paging.) Adjusted during render rather than in an effect, so there is no stale frame.
   */
  const [gridRowCount, setGridRowCount] = useState(0);
  if (data && data.totalCount !== gridRowCount) setGridRowCount(data.totalCount);

  return (
    <Box sx={{ p: { xs: 1, sm: 2 }, minWidth: 0 }}>
      {/* Header Section */}
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ alignItems: { xs: 'stretch', sm: 'center' }, mb: 1 }}>
        <Typography variant="h5" component="h1" sx={{ fontWeight: 800, letterSpacing: '-0.01em', whiteSpace: 'nowrap' }}>
          {t('leads')}
          {!isLoading && !isError && (
            <Box component="span" className="tabular-nums" sx={{ color: 'text.secondary', fontWeight: 600 }} title={`${totalCount} ${totalCount === 1 ? 'inquiry' : 'inquiries'}`}>
              {' · '}{totalCount}
            </Box>
          )}
        </Typography>
        <Box sx={{ width: { xs: '100%', sm: 340 }, maxWidth: '100%' }}>
          <SearchField
            width="100%"
            value={search}
            onChange={(value) => {
              setSearch(value);
              // A new search is a new result set; page 3 of it may not exist.
              setPaginationModel((current) => ({ ...current, page: 0 }));
            }}
            placeholder="Search by serial, RFQ/Bid number, buyer or email"
          />
        </Box>
        <Box sx={{ flexGrow: 1 }} />
        <Stack
          direction="row"
          spacing={1}
          useFlexGap
          sx={{
            alignItems: 'center',
            justifyContent: { xs: 'flex-start', sm: 'flex-end' },
            flexWrap: 'wrap',
          }}
        >
          {canUploadDocuments && (
            <Button
              variant="contained"
              startIcon={<UploadIcon />}
              onClick={() => navigate('/procurement/leads/manual-upload')}
              sx={{
                fontWeight: 800,
                whiteSpace: 'nowrap',
                '& .MuiButton-startIcon': {
                  transition: 'transform 180ms cubic-bezier(0.16, 1, 0.3, 1)',
                },
                '&:hover .MuiButton-startIcon': {
                  transform: 'translateY(-2px) rotate(-4deg)',
                },
                '&:active .MuiButton-startIcon': {
                  transform: 'translateY(1px)',
                },
                '@media (prefers-reduced-motion: reduce)': {
                  '& .MuiButton-startIcon': { transition: 'none' },
                  '&:hover .MuiButton-startIcon, &:active .MuiButton-startIcon': { transform: 'none' },
                },
              }}
            >
              Upload documents
            </Button>
          )}
          <Tooltip title={canCheckMailboxes
            ? 'Fetches new emails from your connected inboxes now'
            : 'Requires Can Create on Leads. Ask an administrator to update your role under Roles & Permissions.'}>
            <span tabIndex={canCheckMailboxes ? undefined : 0}>
              <Button
                variant="outlined"
                startIcon={syncEmailsMutation.isPending ? <CircularProgress size={18} color="inherit" /> : <EmailIcon />}
                onClick={() => syncEmailsMutation.mutate()}
                disabled={syncEmailsMutation.isPending || !canCheckMailboxes}
                sx={{ fontWeight: 700, minHeight: 36 }}
              >
                {syncEmailsMutation.isPending ? 'Checking…' : 'Check for new leads'}
              </Button>
            </span>
          </Tooltip>
          <ExportExcelButton
            name="Leads"
            columns={LEAD_EXPORT_COLUMNS}
            loadRows={async () => withDecisions(await loadAllPages((pageNumber, pageSize) => leadService.getAll({
              ...listParams,
              pageNumber,
              pageSize,
            })))}
          />
          <Tooltip title="Refresh">
            <IconButton aria-label="Refresh" onClick={() => refetch()} sx={{ width: 36, height: 36 }}>
              <RefreshIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      </Stack>

      {/* The lead queues, as one level of tabs on the screen they filter, with the view controls at
          the right end of the same line. Every filter lives in its column's header, so there is no
          filter row between the tabs and the grid. */}
      <Box
        sx={{
          display: 'flex', flexWrap: 'wrap', alignItems: 'center', columnGap: 1, mb: 1,
          borderBottom: '1px solid', borderColor: 'divider',
          '& .MuiToggleButton-root': { py: 0.25, minHeight: 32 }, '& .MuiButton-root': { minHeight: 32 },
        }}
      >
        <Box sx={{ flex: '1 1 auto', minWidth: 0 }}>
          <ViewTabs primaryKey="leads" ariaLabel="Inquiry views" sx={{ borderBottom: 0, mb: 0 }} />
        </Box>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', ml: 'auto', py: 0.5 }} aria-label="List controls" role="group">
          {(columnFiltersActive || search.trim().length > 0) && (
            <Button size="small" variant="text" startIcon={<ClearFiltersIcon />} onClick={() => clearFilters(true)} sx={{ fontWeight: 700, textTransform: 'none' }}>
              Clear filters
            </Button>
          )}
          <ToggleButtonGroup
            size="small"
            exclusive
            value={listView}
            onChange={(_e, value: ViewChoice | null) => {
              if (value) applyView(value);
            }}
            aria-label="List view"
          >
            <Tooltip title="Five plain columns, no sideways scrolling." describeChild><ToggleButton value="simple" aria-label="Simple view">Simple</ToggleButton></Tooltip>
            <Tooltip title="Every field, in the column order you saved under Display." describeChild><ToggleButton value="spreadsheet" aria-label="Spreadsheet view">Spreadsheet</ToggleButton></Tooltip>
          </ToggleButtonGroup>
          <Button
            size="small"
            variant="text"
            startIcon={<TuneIcon />}
            onClick={() => setDisplayOpen((open) => !open)}
            aria-expanded={displayOpen}
            sx={{ fontWeight: 700, textTransform: 'none' }}
          >
            Display
          </Button>
        </Stack>
      </Box>
      {/* Column layout and row density are settings, not the day's work: one click away. */}
      <Collapse in={displayOpen}>
        <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, alignItems: 'center', pb: 1, mb: 1, borderBottom: '1px solid', borderColor: 'divider' }}>
          {listView === 'spreadsheet' ? (
            <ColumnPreferences preferences={columnPreferences} />
          ) : (
            <Typography variant="caption" color="text.secondary">Switch to the Spreadsheet view to choose and order columns.</Typography>
          )}
          <ToggleButtonGroup
            size="small"
            exclusive
            value={density}
            onChange={(_e, value: DensityChoice | null) => {
              if (value) applyDensity(value);
            }}
            aria-label="Row density"
          >
            <ToggleButton value="comfortable" aria-label="Comfortable rows">Comfortable</ToggleButton>
            <ToggleButton value="standard" aria-label="Standard rows">Standard</ToggleButton>
            <ToggleButton value="compact" aria-label="Compact rows">Compact</ToggleButton>
          </ToggleButtonGroup>
        </Box>
      </Collapse>

      {/* Constraint 7: a control that cannot work is not silently missing. Said ONCE, in words,
          instead of a disabled button repeated down every row. */}
      {whyICannotTakeLeads && !(isManager && repProfileNoticeDismissed) && (
        <Alert
          severity="info"
          sx={{ mb: 1, borderRadius: 2, py: 0, '& .MuiAlert-message': { py: 0.75 }, '& .MuiAlert-icon': { py: 0.75 } }}
          onClose={isManager ? dismissRepProfileNotice : undefined}
          slotProps={{ closeButton: { 'aria-label': 'Dismiss this notice' } }}
        >
          <Typography variant="body2">
            <Box component="span" sx={{ fontWeight: 700 }}>
              {isManager
                ? 'You can assign inquiries to other people, but not to yourself yet.'
                : 'You cannot pick up inquiries yet.'}
            </Box>{' '}
            {whyICannotTakeLeads}
            {/* The rep directory is a manager screen; a link a reader cannot open is a dead end,
                so only a manager gets the shortcut and everyone else gets the menu path. */}
            {isManager && (
              <>
                {' '}
                <Link component="button" type="button" underline="hover" onClick={() => navigate('/sales/reps')} sx={{ fontWeight: 700, verticalAlign: 'baseline' }}>
                  Open Sales reps
                </Link>
              </>
            )}
          </Typography>
        </Alert>
      )}

      {/* Bulk assign. Appears only when something is ticked, so it never occupies the screen for
          the reader who is not using it. */}
      {canEditLeads && selectedLeads.length > 0 && (
        <Paper
          sx={{ p: 1.25, mb: 1.5, display: 'flex', flexWrap: 'wrap', gap: 1.5, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'primary.main', boxShadow: 'none' }}
        >
          <Typography variant="body2" sx={{ fontWeight: 800 }}>
            {selectedLeads.length} selected on this page
          </Typography>
          {iCanTakeLeads && (
            <Button
              variant="contained"
              size="small"
              disableElevation
              disabled={assignMutation.isPending || takeableSelected.length === 0}
              onClick={() => takeLeads(takeableSelected)}
              sx={{ fontWeight: 800, borderRadius: 2, textTransform: 'none' }}
            >
              Assign selected to me
            </Button>
          )}
          {isManager && (
            <Button
              variant={iCanTakeLeads ? 'outlined' : 'contained'}
              size="small"
              startIcon={assignMutation.isPending ? <CircularProgress size={16} color="inherit" /> : <AssignIcon />}
              disabled={assignMutation.isPending}
              onClick={(event) => setQuickAssign({ el: event.currentTarget, leads: selectedLeads })}
              sx={{ fontWeight: 800, borderRadius: 2, textTransform: 'none' }}
            >
              {assignMutation.isPending ? 'Assigning…' : 'Assign selected to…'}
            </Button>
          )}
          {notMineToMove > 0 && (
            <Typography variant="caption" sx={{ color: 'text.secondary', maxWidth: 320 }}>
              {notMineToMove} of these already belong to someone else. Only a manager can move
              those, so they will be left alone.
            </Typography>
          )}
          <Button size="small" color="inherit" onClick={() => setSelection(EMPTY_SELECTION)} sx={{ fontWeight: 700 }}>
            Clear selection
          </Button>
        </Paper>
      )}

      {/* What did NOT work. A batch that half succeeded must name the half that did not, and say
          why for each one — a green "assigned" over five failures is the lie this panel exists
          to prevent. */}
      {assignFailures.length > 0 && (
        <Alert
          severity="warning"
          onClose={() => setAssignFailures([])}
          sx={{ mb: 1.5, borderRadius: 2 }}
        >
          <Typography variant="body2" sx={{ fontWeight: 800, mb: 0.5 }}>
            {assignFailures.length} {assignFailures.length === 1 ? 'inquiry' : 'inquiries'} could not be assigned
          </Typography>
          <Box component="ul" sx={{ m: 0, pl: 2.5 }}>
            {assignFailures.map((failure) => (
              <Typography component="li" variant="body2" key={failure.leadId}>
                <strong>{failure.label}</strong> — {failure.message}
              </Typography>
            ))}
          </Box>
          <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
            They are still ticked, so you can try them again without re-selecting.
          </Typography>
        </Alert>
      )}

      {/* Grid */}
      <Paper sx={{ height: { xs: 'calc(100vh - 280px)', sm: 'calc(100vh - 150px)' }, minHeight: 420, boxShadow: 'none', width: '100%', minWidth: 0, borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        {isError ? (
          <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 2, p: 3, textAlign: 'center' }}>
            <Alert severity="error" sx={{ borderRadius: 2, maxWidth: 480 }}>
              We couldn't load leads. The service may be temporarily unavailable.
            </Alert>
            <Button variant="contained" startIcon={<RefreshIcon />} onClick={() => refetch()} sx={{ fontWeight: 700, borderRadius: 2 }}>
              Retry
            </Button>
          </Box>
        ) : (
          <DataGrid
            rows={listView === 'simple' ? workFirstRows : rows}
            columns={gridColumns}
            rowCount={gridRowCount}
            loading={isLoading}
            slots={{ noRowsOverlay }}
            pageSizeOptions={[10, 25, 50]}
            paginationModel={paginationModel}
            paginationMode="server"
            onPaginationModelChange={setPaginationModel}
            // Checkboxes only for a reader who can actually change an owner — a tick box that
            // leads to nothing is a false affordance. `disableRowSelectionOnClick` stays: the
            // checkbox selects, clicking a row still just reads it.
            checkboxSelection={canEditLeads}
            rowSelectionModel={selection}
            onRowSelectionModelChange={setSelection}
            disableRowSelectionOnClick
            getRowId={(r) => r.id}
            density={density}
            getRowHeight={listView === 'simple' ? () => 'auto' : undefined}
            columnHeaderHeight={40}
            sx={{
              border: 0,
              '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 700 },
              // The theme's 44px touch floor made every in-cell button and link a tall block and
              // left the rows uneven; inside a dense row they are 30px.
              '& .MuiDataGrid-cell .MuiButton-root': { minHeight: 30, py: 0.25 },
              ...(listView === 'simple' ? { '& .MuiDataGrid-cell': { display: 'flex', alignItems: 'center', py: 0.75 } } : {}),
            }}
            {...(listView === 'spreadsheet'
              ? {
                  columnVisibilityModel: columnPreferences.columnVisibilityModel,
                  onColumnVisibilityModelChange: columnPreferences.onColumnVisibilityModelChange,
                }
              : {})}
          />
        )}
      </Paper>

      {/* Client resolution — one dialog for the grid, driven by the client cell */}
      <ResolveClientDialog
        open={resolveLead !== null}
        leadId={resolveLead?.id ?? null}
        lead={resolveLead}
        onClose={() => setResolveLead(null)}
        onResolved={() => queryClient.invalidateQueries({ queryKey: ['leads'] })}
      />

      {/* Click 2 of 2 — one picker for a single row and for the whole ticked set */}
      <OwnerPickerMenu
        anchorEl={quickAssign?.el ?? null}
        open={Boolean(quickAssign)}
        onClose={() => setQuickAssign(null)}
        onPick={pickOwner}
        busy={assignMutation.isPending}
        heading={
          (quickAssign?.leads.length ?? 0) > 1
            ? `Assign ${quickAssign?.leads.length} inquiries to`
            : 'Pick a person'
        }
        currentOwnerId={quickAssign?.leads.length === 1 ? quickAssign.leads[0].assignedToId : null}
      />

      {/* Asked for only when a lead is being taken off somebody else. */}
      <AssignReasonDialog
        open={Boolean(reasonPrompt)}
        ownerName={reasonPrompt?.owner.name ?? ''}
        currentOwnerName={reasonPrompt?.owned.length === 1 ? reasonPrompt.owned[0].assignedToFullName : null}
        leadCount={reasonPrompt?.owned.length ?? 0}
        busy={assignMutation.isPending}
        onCancel={() => setReasonPrompt(null)}
        onConfirm={(reason) => {
          if (reasonPrompt) assignMutation.mutate({ owner: reasonPrompt.owner, leads: reasonPrompt.leads, reason });
        }}
      />

      {/* Row overflow menu */}
      <Menu anchorEl={rowMenuAnchor} open={Boolean(rowMenuAnchor)} onClose={closeRowMenu}>
        <MenuItem
          onClick={() => {
            if (rowMenuLeadId != null) navigate(`/leads/view/${rowMenuLeadId}`);
            closeRowMenu();
          }}
        >
          <ListItemIcon>
            <ViewIcon fontSize="small" color="primary" />
          </ListItemIcon>
          <ListItemText>Open the record</ListItemText>
        </MenuItem>
      </Menu>


    </Box>
  );
};

export default LeadsPage;
