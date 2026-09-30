import React, { useCallback, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import {
  Box, Typography, Paper, Button, IconButton,
  Tooltip, Stack, Alert, Collapse, ToggleButton, ToggleButtonGroup,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  ChevronRight as OpenIcon,
  Refresh as RefreshIcon,
  Layers as ItemsIcon,
  CloudUpload as UploadIcon,
  Tune as TuneIcon,
  FilterAltOff as ClearFiltersIcon,
} from '@mui/icons-material';
import rfqService, { type RfqListChoice, type RfqResponseDTO } from '../../../api/services/rfqService';
import ExportExcelButton, { loadAllPages, type ExportColumn } from '../../../components/common/ExportExcelButton';
import SearchField from '../../../components/common/SearchField';
import gridEmptyOverlay from '../../../components/common/gridOverlays';
import ViewTabs from '../../../components/layout/ViewTabs';
import ColumnPreferences from '../../../components/common/ColumnPreferences';
import HeaderFilter, { type HeaderFilterOption } from '../../../components/common/HeaderFilter';
import useColumnPreferences from '../../../hooks/useColumnPreferences';
import {
  AHEAD_WINDOWS, AHEAD_WINDOW_LABELS, DUE_WINDOWS, DUE_WINDOW_LABELS, NO_CUSTOMER, OWNER_CHOICES,
  RECEIVED_WINDOWS, RECEIVED_WINDOW_LABELS, aheadRange, defineListFilters, dueParams, receivedRange, useListFilterState,
  type AheadWindow, type DueWindow, type ListFilterKind, type ListFilterValuesOf, type ReceivedWindow,
} from '../../../hooks/useListFilters';
import { useOwnerOptions } from '../../Leads/LeadOwnerPicker';
import { loadDensity, loadView, saveChoice, type DensityChoice, type ViewChoice } from '../../../utils/listPreferences';
import { useAuth } from '../../../context/AuthContext';
import { formatDateSafe, formatDateTimeSafe, formatDeadline } from '../../../utils/dates';
import { DEADLINE_COLOR, deadlineWords } from '../../../utils/deadline';

/**
 * Same defect as QuotesPage: two rail entries point here at a FILTERED address while the page
 * heads itself "All RFQs / Manage and track all Request for Quotations" either way.
 *
 * RfqRepository.GetAllAsync narrows on 'open' (no sent quote, not finished — the plain list's
 * default) and 'ready-for-quote' (the same, narrowed further). Only 'ready-for-quote' is a filter a
 * link can ask for, so it is the only value that earns a filter chip. "Sourcing Cases" sends
 * ?state=requires-sourcing, which the server drops
 * on the floor — that rail entry lands the user on EVERY RFQ under a heading promising a sourcing
 * subset, so it gets a stated warning rather than a silently complete list.
 */
const RFQ_FILTERS: Record<string, { label: string }> = {
  'ready-for-quote': { label: 'Ready for Quote' },
};

/** The customer's own number for the request, whatever they call it. */
const RFQ_NUMBER = 'RFQ/Bid #';

const RFQ_EXPORT_COLUMNS: ExportColumn<RfqResponseDTO>[] = [
  { header: 'Nexora Serial', value: (r) => r.nexoraSerial || r.commercialCaseReference },
  { header: RFQ_NUMBER, value: (r) => r.rfqno },
  { header: 'Customer RFQ reference', value: (r) => r.customerRfqReference },
  { header: 'Customer', value: (r) => r.customerName },
  { header: 'Customer email', value: (r) => r.customerEmail || r.leadEmail },
  { header: 'Buyer', value: (r) => r.buyersName },
  { header: 'Contact', value: (r) => r.contactName },
  { header: 'Account owner', value: (r) => r.accountOwnerName },
  { header: 'Opportunity owner', value: (r) => r.opportunityOwnerName },
  { header: 'Lines', value: (r) => r.noOfLineItems ?? 0 },
  { header: 'Received', value: (r) => formatDateSafe(r.recDate, '') },
  { header: 'Closing date', value: (r) => formatDeadline(r.bidClosingDate, '') },
  { header: 'Closing date (Hijri)', value: (r) => r.bidClosingDateHijri },
  { header: 'Required delivery', value: (r) => formatDateSafe(r.requiredDeliveryDate, '') },
  { header: 'Delivery location', value: (r) => r.deliveryLocation },
  { header: 'Agreement reference', value: (r) => r.agreementReference },
  { header: 'Opportunity #', value: (r) => r.opportunityNo },
  { header: 'RFQ type', value: (r) => r.rfqtype },
  { header: 'Inquiry type', value: (r) => r.inquiryType },
  { header: 'Agreement duration', value: (r) => r.durationAgreement },
  { header: 'Bidding decision', value: (r) => r.biddingDecision },
  { header: 'Acknowledged', value: (r) => formatDateSafe(r.acknowledgmentDate, '') },
  { header: 'Submitted', value: (r) => formatDateSafe(r.subDate, '') },
  { header: 'Status', value: (r) => r.rfqstatusValue },
  { header: 'Readiness', value: (r) => r.readiness },
  { header: 'Lead revision', value: (r) => r.sourceLeadRevisionNumber },
  { header: 'Made from lead by', value: (r) => r.promotedBy },
  { header: 'Made from lead on', value: (r) => formatDateTimeSafe(r.promotedAtUtc, '') },
  { header: 'Remarks', value: (r) => r.headerRemarks },
  { header: 'Business unit', value: (r) => r.businessUnitName },
  { header: 'Created by', value: (r) => r.createdBy },
  { header: 'Created', value: (r) => formatDateTimeSafe(r.createdDate, '') },
  { header: 'Modified by', value: (r) => r.modifiedBy },
  { header: 'Modified', value: (r) => formatDateTimeSafe(r.modifiedDate, '') },
];

/** Local Simple/Spreadsheet and density choices, like the Leads list. */
const PREFERENCE_KEY = 'nexora.rfqsPage';

// ---------------------------------------------------------------------------
// Column-header filters, held on the URL like the Leads list's
// ---------------------------------------------------------------------------

const QUOTE_STATES = ['none', 'draft', 'sent'] as const;
type QuoteState = (typeof QUOTE_STATES)[number];
const QUOTE_STATE_LABELS: Record<QuoteState, string> = { none: 'Not quoted', draft: 'Not sent yet', sent: 'Sent' };

const RFQ_FILTER_SCHEMA = {
  customer: { kind: 'idOrNone' },
  rfq: { kind: 'text' },
  serial: { kind: 'text' },
  customerRef: { kind: 'text' },
  email: { kind: 'text' },
  buyer: { kind: 'text' },
  accountOwner: { kind: 'text' },
  location: { kind: 'text' },
  agreement: { kind: 'text' },
  opportunity: { kind: 'text' },
  promotedBy: { kind: 'text' },
  linesMin: { kind: 'count' },
  linesMax: { kind: 'count' },
  owner: { kind: 'enum', values: OWNER_CHOICES },
  rep: { kind: 'id' },
  quote: { kind: 'enum', values: QUOTE_STATES },
  statusId: { kind: 'id' },
  rfqType: { kind: 'text' },
  inquiryType: { kind: 'text' },
  bidding: { kind: 'text' },
  due: { kind: 'enum', values: DUE_WINDOWS },
  dueFrom: { kind: 'day' },
  dueTo: { kind: 'day' },
  received: { kind: 'enum', values: RECEIVED_WINDOWS },
  receivedFrom: { kind: 'day' },
  receivedTo: { kind: 'day' },
  required: { kind: 'enum', values: AHEAD_WINDOWS },
  requiredFrom: { kind: 'day' },
  requiredTo: { kind: 'day' },
  submitted: { kind: 'enum', values: RECEIVED_WINDOWS },
  submittedFrom: { kind: 'day' },
  submittedTo: { kind: 'day' },
  created: { kind: 'enum', values: RECEIVED_WINDOWS },
  createdFrom: { kind: 'day' },
  createdTo: { kind: 'day' },
  modified: { kind: 'enum', values: RECEIVED_WINDOWS },
  modifiedFrom: { kind: 'day' },
  modifiedTo: { kind: 'day' },
} as const satisfies Record<string, ListFilterKind>;

const RFQ_LIST_FILTERS = defineListFilters(RFQ_FILTER_SCHEMA, {
  presets: [
    ['due', 'dueFrom', 'dueTo'], ['received', 'receivedFrom', 'receivedTo'], ['required', 'requiredFrom', 'requiredTo'],
    ['submitted', 'submittedFrom', 'submittedTo'], ['created', 'createdFrom', 'createdTo'], ['modified', 'modifiedFrom', 'modifiedTo'],
  ],
  // One rep's list is a slice of everyone's: "Unassigned" or "Mine" plus a rep is not a question.
  supersedes: [['owner', 'rep']],
});
type RfqFilterValues = ListFilterValuesOf<typeof RFQ_FILTER_SCHEMA>;

/** The Lines buckets. */
const LINE_BUCKETS: { value: string; label: string; min: number; max: number | null }[] = [
  { value: '1-10', label: '1–10', min: 1, max: 10 },
  { value: '11-100', label: '11–100', min: 11, max: 100 },
  { value: '101-', label: 'Over 100', min: 101, max: null },
];
const linesBucket = (min: number | null, max: number | null): string | null => {
  if (min == null && max == null) return null;
  return LINE_BUCKETS.find((bucket) => bucket.min === min && bucket.max === max)?.value ?? 'other';
};
const linesWords = (min: number | null, max: number | null): string => {
  const bucket = LINE_BUCKETS.find((b) => b.min === min && b.max === max);
  if (bucket) return `${bucket.label} lines`;
  if (min != null && max != null) return `${min}–${max} lines`;
  return min != null ? `${min} or more lines` : `Up to ${max} lines`;
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
 * The server's list parameters for the header filters. Presets become day ranges here, on the
 * reader's own calendar, so "Last 7 days" means the reader's week and not the server's.
 */
const columnFilterParams = (f: RfqFilterValues, myUserId: number | null, now: Date = new Date()) => {
  const back = (preset: ReceivedWindow | null, from: string | null, to: string | null) =>
    (preset ? receivedRange(preset, now) : { from, to });
  const received = back(f.received, f.receivedFrom, f.receivedTo);
  const submitted = back(f.submitted, f.submittedFrom, f.submittedTo);
  const created = back(f.created, f.createdFrom, f.createdTo);
  const modified = back(f.modified, f.modifiedFrom, f.modifiedTo);
  const required = f.required ? aheadRange(f.required, now) : { from: f.requiredFrom, to: f.requiredTo };
  return compact({
    customer: f.customer,
    rfq: f.rfq,
    serial: f.serial,
    customerRef: f.customerRef,
    email: f.email,
    buyer: f.buyer,
    accountOwner: f.accountOwner,
    location: f.location,
    agreement: f.agreement,
    opportunity: f.opportunity,
    promotedBy: f.promotedBy,
    linesMin: f.linesMin,
    linesMax: f.linesMax,
    quote: f.quote,
    statusId: f.statusId,
    rfqType: f.rfqType,
    inquiryType: f.inquiryType,
    bidding: f.bidding,
    ...dueParams(f.due, now),
    // A custom Deadline range is sent INSTEAD of a window, never beside one.
    dueFrom: f.due ? null : f.dueFrom,
    dueTo: f.due ? null : f.dueTo,
    receivedFrom: received.from,
    receivedTo: received.to,
    requiredFrom: required.from,
    requiredTo: required.to,
    submittedFrom: submitted.from,
    submittedTo: submitted.to,
    createdFrom: created.from,
    createdTo: created.to,
    modifiedFrom: modified.from,
    modifiedTo: modified.to,
    unassigned: f.owner === 'unassigned' ? true : null,
    assignedToId: f.owner === 'mine' ? myUserId : f.rep,
  });
};

/** "Label (N)" choices from the server's counted list. */
const countedOptions = (choices: RfqListChoice[] | undefined): HeaderFilterOption<string>[] =>
  (choices ?? []).map((choice) => ({ value: choice.value, label: `${choice.label} (${choice.count})` }));
const choiceLabel = (choices: RfqListChoice[] | undefined, value: string): string =>
  choices?.find((choice) => choice.value === value)?.label ?? value;

/**
 * The Spreadsheet view's plain columns. Keys match ListViewCatalog "rfqs.list"; the columns the
 * Simple view already draws (deadline, customer, RFQ #, serial, lines, owner, quote, open) are
 * reused as they are.
 */
const RFQ_SHEET_TEXT: { field: string; headerName: string; width: number; value: (r: RfqResponseDTO) => string | number | null | undefined }[] = [
  { field: 'customerRfqReference', headerName: 'Customer RFQ reference', width: 170, value: (r) => r.customerRfqReference },
  { field: 'customerEmail', headerName: 'Customer email', width: 200, value: (r) => r.customerEmail || r.leadEmail },
  { field: 'buyersName', headerName: 'Buyer', width: 170, value: (r) => r.buyersName },
  { field: 'contactName', headerName: 'Contact', width: 160, value: (r) => r.contactName },
  { field: 'accountOwnerName', headerName: 'Account owner', width: 150, value: (r) => r.accountOwnerName },
  { field: 'recDate', headerName: 'Received', width: 120, value: (r) => formatDateSafe(r.recDate, '') },
  { field: 'bidClosingDateHijri', headerName: 'Deadline (Hijri)', width: 130, value: (r) => r.bidClosingDateHijri },
  { field: 'requiredDeliveryDate', headerName: 'Required delivery', width: 140, value: (r) => formatDateSafe(r.requiredDeliveryDate, '') },
  { field: 'deliveryLocation', headerName: 'Delivery location', width: 200, value: (r) => r.deliveryLocation },
  { field: 'agreementReference', headerName: 'Agreement reference', width: 170, value: (r) => r.agreementReference },
  { field: 'opportunityNo', headerName: 'Opportunity #', width: 140, value: (r) => r.opportunityNo },
  { field: 'rfqtype', headerName: 'RFQ type', width: 120, value: (r) => r.rfqtype },
  { field: 'inquiryType', headerName: 'Inquiry type', width: 130, value: (r) => r.inquiryType },
  { field: 'biddingDecision', headerName: 'Bidding decision', width: 140, value: (r) => r.biddingDecision },
  { field: 'subDate', headerName: 'Submitted', width: 120, value: (r) => formatDateSafe(r.subDate, '') },
  { field: 'status', headerName: 'Status', width: 120, value: (r) => r.rfqstatusValue },
  { field: 'readiness', headerName: 'Readiness', width: 130, value: (r) => r.readiness },
  { field: 'promotedBy', headerName: 'Made from lead by', width: 160, value: (r) => r.promotedBy },
  { field: 'createdDate', headerName: 'Created', width: 150, value: (r) => formatDateTimeSafe(r.createdDate, '') },
  { field: 'modifiedDate', headerName: 'Modified', width: 150, value: (r) => formatDateTimeSafe(r.modifiedDate, '') },
];

const AllRFQsPage: React.FC = () => {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const readiness = searchParams.get('state') || undefined;
  const unappliedFilter = readiness && !RFQ_FILTERS[readiness] ? readiness : undefined;
  const { userData, hasPermission } = useAuth();
  const isManager = userData?.isManager === true || Boolean(userData?.isSuperAdmin);
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 25, page: 0 });
  const [search, setSearch] = useState('');
  const myUserId = userData?.id ?? null;
  const [listView, setListView] = useState<ViewChoice>(() => loadView(PREFERENCE_KEY));
  const [density, setDensity] = useState<DensityChoice>(() => loadDensity(PREFERENCE_KEY));
  const [displayOpen, setDisplayOpen] = useState(false);
  const columnPreferences = useColumnPreferences('rfqs.list');

  // Every column-header filter lives on the URL, so Back and a link sent to a colleague keep them.
  const listFilters = useListFilterState(RFQ_LIST_FILTERS);
  const setListFilters = listFilters.set;
  // Rep is a manager's choice; a non-manager arriving on a link with `rep=` is not narrowed by a
  // filter they cannot see or change.
  const repFilter = isManager ? listFilters.values.rep : null;
  const f = useMemo(() => ({ ...listFilters.values, rep: repFilter }), [listFilters.values, repFilter]);
  const columnFiltersActive = RFQ_LIST_FILTERS.any(f);

  // The plain list is "open": RFQs whose quote has not been sent. Once sent, the work is on the
  // Quotes list, so the RFQ leaves this one. A typed search, or asking for sent quotes, still
  // reaches every RFQ.
  const listReadiness = readiness ?? (search.trim() || f.quote === 'sent' ? undefined : 'open');

  /**
   * Everything that narrows the list, in ONE object read by both the grid and Export to Excel, so
   * the spreadsheet is always the list on screen.
   */
  const listParams = useMemo(() => compact({
    search: search || undefined,
    businessUnitId: userData?.businessUnitId || undefined,
    readiness: listReadiness,
    ...columnFilterParams(f, myUserId),
  }), [search, userData?.businessUnitId, listReadiness, f, myUserId]);

  // A narrower list is a new result set; page 3 of it may not exist. Adjusted during render, not in
  // an effect: an effect runs after the grid query has already asked for page 3 of the new list.
  const narrowingKey = JSON.stringify(listParams);
  const [pagedNarrowingKey, setPagedNarrowingKey] = useState(narrowingKey);
  if (pagedNarrowingKey !== narrowingKey) {
    setPagedNarrowingKey(narrowingKey);
    if (paginationModel.page !== 0) setPaginationModel({ ...paginationModel, page: 0 });
  }

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['rfqs', paginationModel, listParams],
    queryFn: () => rfqService.getAll({
      ...listParams,
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
    }),
  });

  /**
   * Every RFQ, open or quoted, so an empty default list can say which nothing it is: no RFQs at
   * all, or every one already quoted. One row is enough; only the count is read. Silent because
   * the main query reports its own failures. Under the ['rfqs'] prefix so the same invalidations
   * refresh it.
   */
  const totalQuery = useQuery({
    queryKey: ['rfqs', 'total', userData?.businessUnitId ?? null],
    queryFn: () => rfqService.getAll({
      pageNumber: 1,
      pageSize: 1,
      businessUnitId: userData?.businessUnitId || undefined,
    }),
    meta: { silenceGlobalError: true },
  });

  /**
   * The header choices (customers, statuses, RFQ types, inquiry types, bidding decisions) with
   * counts, from the RFQs in the same readiness view. The menus print their own failure.
   */
  const choicesQuery = useQuery({
    queryKey: ['rfqs', 'choices', listReadiness ?? null],
    queryFn: () => rfqService.getListChoices(listReadiness),
    meta: { silenceGlobalError: true },
  });
  const choices = choicesQuery.data;
  const customerOptions = useMemo<HeaderFilterOption<string>[]>(() => {
    if (!choices) return [];
    const options = countedOptions(choices.customers);
    if (choices.noCustomer > 0) options.push({ value: NO_CUSTOMER, label: `No customer yet (${choices.noCustomer})` });
    return options;
  }, [choices]);

  // The people a manager can narrow the Owner column to.
  const ownerOptions = useOwnerOptions(isManager);
  const repOptions = useMemo<HeaderFilterOption<number>[]>(
    () => (ownerOptions.data ?? []).map((option) => ({ value: option.userId, label: option.name })),
    [ownerOptions.data],
  );

  const filtersActive = Boolean(search) || Boolean(readiness) || columnFiltersActive;
  const isCaughtUp = !filtersActive && (totalQuery.data?.totalItems ?? 0) > 0;

  /**
   * Clears what the reader narrowed, in one URL write. `keepView` is the tabs-row button: it stays
   * on the tab (Ready for quote) the reader is on. The empty state's button also drops that.
   */
  const clearFilters = useCallback((keepView = false) => {
    setSearch('');
    setPaginationModel((current) => ({ ...current, page: 0 }));
    const next = RFQ_LIST_FILTERS.clearKeys(searchParams);
    if (!keepView) next.delete('state');
    if (next.toString() !== searchParams.toString()) setSearchParams(next, { replace: true });
  }, [searchParams, setSearchParams]);

  /** The header filters in words, e.g. "Saudi Electricity Company · Overdue · Mine". */
  const headerFilterWords = useMemo(() => {
    const words: string[] = [];
    const range = (noun: string, preset: ReceivedWindow | null, from: string | null, to: string | null) => {
      if (preset) words.push(`${noun} ${RECEIVED_WINDOW_LABELS[preset].toLowerCase()}`);
      else if (from || to) words.push(`${noun} ${rangeWords(from, to)}`);
    };
    if (f.customer != null) {
      words.push(f.customer === NO_CUSTOMER ? 'No customer yet' : choices?.customers.find((c) => c.value === f.customer)?.label ?? 'Chosen customer');
    }
    if (f.due != null) words.push(DUE_WINDOW_LABELS[f.due]);
    else if (f.dueFrom || f.dueTo) words.push(`Deadline ${rangeWords(f.dueFrom, f.dueTo)}`);
    range('Received', f.received, f.receivedFrom, f.receivedTo);
    if (f.required != null) words.push(`Wanted in the ${AHEAD_WINDOW_LABELS[f.required].toLowerCase()}`);
    else if (f.requiredFrom || f.requiredTo) words.push(`Wanted ${rangeWords(f.requiredFrom, f.requiredTo)}`);
    range('Submitted', f.submitted, f.submittedFrom, f.submittedTo);
    range('Created', f.created, f.createdFrom, f.createdTo);
    range('Modified', f.modified, f.modifiedFrom, f.modifiedTo);
    if (f.linesMin != null || f.linesMax != null) words.push(linesWords(f.linesMin, f.linesMax));
    const texts: [string | null, string][] = [
      [f.rfq, RFQ_NUMBER], [f.serial, 'Serial'], [f.customerRef, 'Customer RFQ reference'], [f.email, 'Customer email'],
      [f.buyer, 'Buyer'], [f.accountOwner, 'Account owner'], [f.location, 'Delivery location'], [f.agreement, 'Agreement'],
      [f.opportunity, 'Opportunity #'], [f.promotedBy, 'Made from lead by'],
    ];
    texts.forEach(([value, noun]) => { if (value) words.push(`${noun} contains "${value}"`); });
    if (f.quote != null) words.push(`Quote ${QUOTE_STATE_LABELS[f.quote].toLowerCase()}`);
    if (f.statusId != null) words.push(choiceLabel(choices?.statuses, String(f.statusId)));
    if (f.rfqType) words.push(`RFQ type ${choiceLabel(choices?.rfqTypes, f.rfqType)}`);
    if (f.inquiryType) words.push(`Inquiry type ${choiceLabel(choices?.inquiryTypes, f.inquiryType)}`);
    if (f.bidding) words.push(`Bidding ${choiceLabel(choices?.biddingDecisions, f.bidding)}`);
    if (f.owner === 'unassigned') words.push('Unassigned');
    if (f.owner === 'mine') words.push('Mine');
    if (f.rep != null) words.push(repOptions.find((r) => r.value === f.rep)?.label ?? 'Chosen rep');
    return words.join(' · ');
  }, [f, choices, repOptions]);

  /**
   * The three highest-traffic grids in the product — this one, Leads and Quotes — shipped MUI's
   * bare "No rows". That string cannot say which of the three nothings the reader is looking at,
   * and it offers no way out of any of them. Memoised because DataGrid takes a component TYPE
   * here: rebuilding the factory each render would remount the overlay for nothing.
   */
  const noRowsOverlay = useMemo(() => gridEmptyOverlay(isCaughtUp ? {
    title: 'Nothing to quote',
    icon: <ItemsIcon sx={{ fontSize: 48 }} />,
    action: (
      <Button variant="contained" onClick={() => navigate('/sales/quotes')} sx={{ fontWeight: 700 }}>
        Open quotes
      </Button>
    ),
  } : {
    title: 'No RFQs yet',
    message: 'An RFQ is created when you qualify an enquiry. Qualify one and it lands here, ready to price.',
    icon: <ItemsIcon sx={{ fontSize: 48 }} />,
    action: (
      <Button variant="contained" onClick={() => navigate('/procurement/leads/all')} sx={{ fontWeight: 700 }}>
        See all inquiries
      </Button>
    ),
    filtered: filtersActive,
    filteredTitle: columnFiltersActive ? 'No RFQs match' : 'No RFQ matches this view',
    filteredMessage: columnFiltersActive
      ? headerFilterWords
      : readiness
        ? 'Nothing in the list matches this filter. Clear it to see every RFQ.'
        : 'No RFQ matches this search. Clear it to see every RFQ.',
    filteredAction: (
      <Button
        variant={columnFiltersActive ? 'contained' : 'outlined'}
        startIcon={columnFiltersActive ? <ClearFiltersIcon /> : undefined}
        onClick={() => clearFilters()}
        sx={{ fontWeight: 700 }}
      >
        {columnFiltersActive ? 'Clear filters' : search && !readiness ? 'Clear search' : 'Show all RFQs'}
      </Button>
    ),
  }), [isCaughtUp, filtersActive, columnFiltersActive, headerFilterWords, search, readiness, clearFilters, navigate]);

  const openRfq = (id: number) => navigate(`/procurement/rfqs/view/${id}`);
  const cellText = { whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' } as const;

  const columns: GridColDef<RfqResponseDTO>[] = [
    {
      field: 'bidClosingDate',
      headerName: 'Deadline',
      width: 130,
      sortable: false,
      renderCell: (p) => {
        const sent = Boolean(p.row.latestQuoteSentOn);
        const { text, tone } = deadlineWords(p.row.bidClosingDate);
        return (
          <Tooltip title={p.row.bidClosingDate ? formatDeadline(p.row.bidClosingDate) : ''} placement="top-start">
            <Typography sx={{ ...cellText, fontSize: '0.85rem', fontWeight: sent ? 500 : 700, color: sent ? 'text.secondary' : DEADLINE_COLOR[tone] }}>
              {text}
            </Typography>
          </Tooltip>
        );
      },
    },
    {
      field: 'customerName',
      headerName: 'Customer',
      flex: 1,
      minWidth: 200,
      sortable: false,
      renderCell: (p) => {
        const buyer = p.row.buyersName && p.row.buyersName !== p.row.customerName ? p.row.buyersName : null;
        return (
          <Typography sx={{ ...cellText, fontSize: '0.875rem' }} title={[p.row.customerName, buyer].filter(Boolean).join(' · ')}>
            <Box component="span" sx={{ fontWeight: 700, color: p.row.customerName ? 'text.primary' : 'warning.dark' }}>
              {p.row.customerName || 'No customer'}
            </Box>
            {buyer && <Box component="span" sx={{ color: 'text.secondary' }}> · {buyer}</Box>}
          </Typography>
        );
      },
    },
    {
      field: 'rfqno',
      headerName: RFQ_NUMBER,
      width: 205,
      sortable: false,
      renderCell: (p) => (
        <Typography title={p.row.rfqno} sx={{ ...cellText, fontFamily: 'monospace', fontSize: '0.82rem', fontWeight: 600 }}>
          {p.row.rfqno || `RFQ-${p.row.id}`}
        </Typography>
      ),
    },
    {
      field: 'nexoraSerial',
      headerName: 'Nexora serial',
      width: 200,
      sortable: false,
      // No fallback through the lead or RFQ: a blank here is real, and "Not linked" says so.
      renderCell: (p) => (
        <Typography title={p.row.nexoraSerial ?? ''} sx={{ ...cellText, fontFamily: 'monospace', fontSize: '0.75rem', color: p.row.nexoraSerial ? 'text.secondary' : 'warning.dark' }}>
          {p.row.nexoraSerial || 'Not linked'}
        </Typography>
      ),
    },
    {
      field: 'itemCount',
      headerName: 'Lines',
      width: 92,
      align: 'right',
      headerAlign: 'right',
      sortable: false,
      renderCell: (p) => (
        <Typography className="tabular-nums" sx={{ fontSize: '0.85rem', fontWeight: 600 }}>
          {p.row.itemCount ?? p.row.noOfLineItems ?? 0}
        </Typography>
      ),
    },
    {
      field: 'ownerName',
      headerName: 'Owner',
      width: 150,
      sortable: false,
      renderCell: (p) => (
        <Typography sx={{ ...cellText, fontSize: '0.85rem', color: p.row.ownerName ? 'text.primary' : 'text.disabled' }}>
          {p.row.ownerName || 'Unassigned'}
        </Typography>
      ),
    },
    {
      field: 'quote',
      headerName: 'Quote',
      width: 270,
      sortable: false,
      renderCell: (p) => {
        const status = (p.row.rfqstatusCode ?? p.row.rfqstatusValue ?? '').toUpperCase();
        const closed = status && !['DRAFT', 'OPEN', 'NEW', 'ACTIVE', 'INPROGRESS', 'IN_PROGRESS'].includes(status);
        const sentOn = p.row.latestQuoteSentOn;
        const text = sentOn
          ? `Sent ${formatDateSafe(sentOn)}`
          : p.row.latestQuoteNo ? 'Not sent yet' : closed ? (p.row.rfqstatusValue ?? '') : 'Not quoted';
        return (
          <Typography sx={{ ...cellText, fontSize: '0.85rem' }} title={p.row.latestQuoteNo ?? ''}>
            <Box component="span" sx={{ fontWeight: 700, color: sentOn ? 'success.dark' : p.row.latestQuoteNo ? 'warning.dark' : 'text.secondary' }}>
              {text}
            </Box>
            {p.row.latestQuoteNo && <Box component="span" sx={{ color: 'text.secondary', fontFamily: 'monospace', fontSize: '0.75rem' }}> · {p.row.latestQuoteNo}</Box>}
          </Typography>
        );
      },
    },
    {
      field: 'open',
      headerName: '',
      width: 44,
      sortable: false,
      disableColumnMenu: true,
      renderCell: (p) => (
        <IconButton size="small" aria-label={`Open RFQ ${p.row.rfqno || p.row.id}`} onClick={() => openRfq(p.row.id)} sx={{ minHeight: 0 }}>
          <OpenIcon fontSize="small" />
        </IconButton>
      ),
    },
  ];

  // Spreadsheet: every field, in the column order this user saved under Display.
  const byField = new Map(columns.map((column) => [column.field, column]));
  const sheetColumns: GridColDef<RfqResponseDTO>[] = [
    byField.get('nexoraSerial')!,
    byField.get('rfqno')!,
    byField.get('customerName')!,
    byField.get('ownerName')!,
    byField.get('itemCount')!,
    byField.get('bidClosingDate')!,
    byField.get('quote')!,
    ...RFQ_SHEET_TEXT.map((spec): GridColDef<RfqResponseDTO> => ({
      field: spec.field,
      headerName: spec.headerName,
      width: spec.width,
      sortable: false,
      valueGetter: (_value, row) => spec.value(row) ?? '',
    })),
    byField.get('open')!,
  ];
  // ------------------------------------------------------------------
  // Column-header filters: the same control on the Simple and Spreadsheet columns, one state.
  // ------------------------------------------------------------------
  type TextKey = 'rfq' | 'serial' | 'customerRef' | 'email' | 'buyer' | 'accountOwner' | 'location' | 'agreement' | 'opportunity' | 'promotedBy';
  type ChoiceKey = 'rfqType' | 'inquiryType' | 'bidding';
  type LookBackKey = 'received' | 'submitted' | 'created' | 'modified';
  type FilterKind = 'due' | 'dueHijri' | 'customer' | 'lines' | 'owner' | 'quote' | 'status' | 'required'
    | { text: TextKey; noun: string } | { choice: ChoiceKey; noun: string } | { lookBack: LookBackKey; noun: string };
  const filterFor: Record<string, FilterKind> = {
    bidClosingDate: 'due',
    bidClosingDateHijri: 'dueHijri',
    customerName: 'customer',
    rfqno: { text: 'rfq', noun: 'RFQ/Bid number' },
    nexoraSerial: { text: 'serial', noun: 'Serial' },
    customerRfqReference: { text: 'customerRef', noun: 'Customer RFQ reference' },
    customerEmail: { text: 'email', noun: 'Customer email' },
    buyersName: { text: 'buyer', noun: 'Buyer' },
    accountOwnerName: { text: 'accountOwner', noun: 'Account owner' },
    deliveryLocation: { text: 'location', noun: 'Delivery location' },
    agreementReference: { text: 'agreement', noun: 'Agreement reference' },
    opportunityNo: { text: 'opportunity', noun: 'Opportunity number' },
    promotedBy: { text: 'promotedBy', noun: 'Made from lead by' },
    itemCount: 'lines',
    ownerName: 'owner',
    quote: 'quote',
    status: 'status',
    rfqtype: { choice: 'rfqType', noun: 'RFQ type' },
    inquiryType: { choice: 'inquiryType', noun: 'Inquiry type' },
    biddingDecision: { choice: 'bidding', noun: 'Bidding decision' },
    recDate: { lookBack: 'received', noun: 'Received date' },
    requiredDeliveryDate: 'required',
    subDate: { lookBack: 'submitted', noun: 'Submitted date' },
    createdDate: { lookBack: 'created', noun: 'Created date' },
    modifiedDate: { lookBack: 'modified', noun: 'Modified date' },
  };
  const choiceLists: Record<ChoiceKey, RfqListChoice[] | undefined> = {
    rfqType: choices?.rfqTypes, inquiryType: choices?.inquiryTypes, bidding: choices?.biddingDecisions,
  };
  const ownerFilterOptions: HeaderFilterOption<string>[] = [
    { value: 'unassigned', label: 'Unassigned' },
    ...(myUserId != null ? [{ value: 'mine', label: 'Mine' }] : []),
    ...(isManager ? repOptions.map((option) => ({ value: `rep:${option.value}`, label: option.label })) : []),
  ];
  const ownerFilterValue = f.owner ?? (f.rep != null ? `rep:${f.rep}` : null);
  const pickOwnerFilter = (choice: string | null) => {
    if (choice?.startsWith('rep:')) setListFilters({ owner: null, rep: Number(choice.slice(4)) });
    else setListFilters({ owner: choice === 'unassigned' || choice === 'mine' ? choice : null, rep: null });
  };
  const linesValue = linesBucket(f.linesMin, f.linesMax);
  const dueHeader = (title: string, noun: string) => (
    <HeaderFilter title={title} noun={noun} anyLabel="Any date" anyLast
      options={DUE_WINDOWS.map((window) => ({ value: window, label: DUE_WINDOW_LABELS[window] }))}
      value={f.due}
      onChange={(due: DueWindow | null) => setListFilters({ due, dueFrom: null, dueTo: null })}
      range={{ from: f.dueFrom, to: f.dueTo, onApply: (dueFrom, dueTo) => setListFilters({ due: null, dueFrom, dueTo }) }} />
  );
  const renderFilterHeader = (kind: FilterKind, title: string): React.ReactNode => {
    if (typeof kind === 'object') {
      if ('text' in kind) {
        const key = kind.text;
        return <HeaderFilter title={title} noun={kind.noun} text={{ value: f[key], onChange: (value) => setListFilters({ [key]: value }) }} />;
      }
      if ('choice' in kind) {
        const key = kind.choice;
        return (
          <HeaderFilter title={title} noun={kind.noun} anyLabel="Any" searchable={choiceLists[key] != null && choiceLists[key]!.length > 8}
            options={countedOptions(choiceLists[key])} value={f[key]}
            loading={choicesQuery.isLoading} error={choicesQuery.isError}
            onChange={(value: string | null) => setListFilters({ [key]: value })} />
        );
      }
      const key = kind.lookBack;
      const fromKey = `${key}From` as const;
      const toKey = `${key}To` as const;
      return (
        <HeaderFilter title={title} noun={kind.noun} anyLabel="Any date" anyLast
          options={RECEIVED_WINDOWS.map((window) => ({ value: window, label: RECEIVED_WINDOW_LABELS[window] }))}
          value={f[key]}
          onChange={(preset: ReceivedWindow | null) => setListFilters({ [key]: preset, [fromKey]: null, [toKey]: null })}
          range={{ from: f[fromKey], to: f[toKey], onApply: (from, to) => setListFilters({ [key]: null, [fromKey]: from, [toKey]: to }) }} />
      );
    }
    switch (kind) {
      case 'due':
        return dueHeader(title, 'Deadline');
      case 'dueHijri':
        return dueHeader(title, 'Deadline (Hijri)');
      case 'customer':
        return (
          <HeaderFilter title={title} noun="Customer" anyLabel="Any customer" searchable
            options={customerOptions} value={f.customer}
            loading={choicesQuery.isLoading} error={choicesQuery.isError}
            onChange={(customer: string | null) => setListFilters({ customer })} />
        );
      case 'lines':
        return (
          <HeaderFilter title={title} noun="Lines" anyLabel="Any" anyLast
            options={[
              ...LINE_BUCKETS.map((bucket) => ({ value: bucket.value, label: bucket.label })),
              ...(linesValue === 'other' ? [{ value: 'other', label: linesWords(f.linesMin, f.linesMax) }] : []),
            ]}
            value={linesValue}
            onChange={(choice: string | null) => {
              // "other" is a hand-edited range already on the URL; picking it again changes nothing.
              if (choice === 'other') return;
              const bucket = LINE_BUCKETS.find((b) => b.value === choice);
              setListFilters({ linesMin: bucket?.min ?? null, linesMax: bucket?.max ?? null });
            }} />
        );
      case 'owner':
        return (
          <HeaderFilter title={title} noun="Owner" anyLabel="Anyone"
            options={ownerFilterOptions} value={ownerFilterValue} onChange={pickOwnerFilter} />
        );
      case 'quote':
        return (
          <HeaderFilter title={title} noun="Quote" anyLabel="Any"
            options={QUOTE_STATES.map((state) => ({ value: state, label: QUOTE_STATE_LABELS[state] }))}
            value={f.quote}
            onChange={(quote: QuoteState | null) => setListFilters({ quote })} />
        );
      case 'status':
        return (
          <HeaderFilter title={title} noun="Status" anyLabel="Any status"
            options={countedOptions(choices?.statuses)} value={f.statusId != null ? String(f.statusId) : null}
            loading={choicesQuery.isLoading} error={choicesQuery.isError}
            onChange={(status: string | null) => setListFilters({ statusId: status ? Number(status) : null })} />
        );
      case 'required':
        return (
          <HeaderFilter title={title} noun="Required delivery" anyLabel="Any date" anyLast
            options={AHEAD_WINDOWS.map((window) => ({ value: window, label: AHEAD_WINDOW_LABELS[window] }))}
            value={f.required}
            onChange={(required: AheadWindow | null) => setListFilters({ required, requiredFrom: null, requiredTo: null })}
            range={{ from: f.requiredFrom, to: f.requiredTo, onApply: (requiredFrom, requiredTo) => setListFilters({ required: null, requiredFrom, requiredTo }) }} />
        );
      default:
        return title;
    }
  };
  const withHeaderFilter = (column: GridColDef<RfqResponseDTO>): GridColDef<RfqResponseDTO> => {
    const kind = filterFor[column.field];
    if (!kind) return column;
    const title = column.headerName ?? '';
    return { ...column, renderHeader: () => renderFilterHeader(kind, title) };
  };
  const gridColumns = (listView === 'simple' ? columns : columnPreferences.arrangeColumns(sheetColumns)).map(withHeaderFilter);

  /**
   * The grid's row count holds its last known value while the next page loads: a new page is a new
   * query key, and a row count of 0 meanwhile made DataGrid clamp back to the first page.
   */
  const [gridRowCount, setGridRowCount] = useState(0);
  if (data && data.totalItems !== gridRowCount) setGridRowCount(data.totalItems);

  return (
    <Box sx={{ p: { xs: 1, sm: 2 }, minWidth: 0 }}>
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 1, flexWrap: 'wrap', rowGap: 1 }}>
        <Typography variant="h5" component="h1" sx={{ fontWeight: 800, letterSpacing: '-0.01em' }}>
          RFQs
          {data && <Box component="span" className="tabular-nums" sx={{ color: 'text.secondary', fontWeight: 600 }}> · {data.totalItems}</Box>}
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
            placeholder="Search RFQ/Bid number, serial, customer or buyer"
          />
        </Box>
        <Box sx={{ flex: 1 }} />
        {hasPermission('RFQ Management', 'create') && (
          <Tooltip title="Upload a customer inquiry for Lead reconciliation">
            <Button
              variant="outlined"
              startIcon={<UploadIcon />}
              onClick={() => navigate('/procurement/leads/manual-upload')}
              sx={{ fontWeight: 700, minHeight: 36 }}
            >
              Upload inquiry
            </Button>
          </Tooltip>
        )}
        <ExportExcelButton
          name="RFQs"
          columns={RFQ_EXPORT_COLUMNS}
          loadRows={() => loadAllPages((pageNumber, pageSize) => rfqService.getAll({
            ...listParams,
            pageNumber,
            pageSize,
          }))}
        />
        <Tooltip title="Refresh">
          <IconButton aria-label="Refresh" onClick={() => refetch()} sx={{ width: 36, height: 36 }}>
            <RefreshIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      </Stack>
      {unappliedFilter && (
        <Alert severity="warning" sx={{ mb: 1 }}>
          This link asked for "{unappliedFilter}", which is not a filter this list applies. Every RFQ is shown.
        </Alert>
      )}

      {/* All / Drafts / Ready for quote as one level of tabs, with the view controls at the right end
          of the same line. Every filter lives in its column's header, so there is no filter row
          between the tabs and the grid. */}
      <Box
        sx={{
          display: 'flex', flexWrap: 'wrap', alignItems: 'center', columnGap: 1, mb: 1,
          borderBottom: '1px solid', borderColor: 'divider',
          '& .MuiToggleButton-root': { py: 0.25, minHeight: 32 }, '& .MuiButton-root': { minHeight: 32 },
        }}
      >
        <Box sx={{ flex: '1 1 auto', minWidth: 0 }}>
          <ViewTabs primaryKey="rfqs" ariaLabel="RFQ views" sx={{ borderBottom: 0, mb: 0 }} />
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
              if (!value) return;
              setListView(value);
              saveChoice(PREFERENCE_KEY, 'view', value);
            }}
            aria-label="List view"
          >
            <Tooltip title="The working columns, no sideways scrolling." describeChild><ToggleButton value="simple" aria-label="Simple view">Simple</ToggleButton></Tooltip>
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
              if (!value) return;
              setDensity(value);
              saveChoice(PREFERENCE_KEY, 'density', value);
            }}
            aria-label="Row density"
          >
            <ToggleButton value="comfortable" aria-label="Comfortable rows">Comfortable</ToggleButton>
            <ToggleButton value="standard" aria-label="Standard rows">Standard</ToggleButton>
            <ToggleButton value="compact" aria-label="Compact rows">Compact</ToggleButton>
          </ToggleButtonGroup>
        </Box>
      </Collapse>

      <Paper sx={{ height: { xs: 'calc(100vh - 280px)', sm: 'calc(100vh - 150px)' }, minHeight: 420, width: '100%', minWidth: 0, borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        {isError ? (
          <Box sx={{ height: '100%', display: 'grid', placeItems: 'center', p: 3 }}>
            <Stack spacing={2} sx={{ alignItems: 'center', maxWidth: 480 }}>
              <Alert severity="error">We couldn't load RFQs. No empty result has been assumed.</Alert>
              <Button variant="contained" startIcon={<RefreshIcon />} onClick={() => refetch()}>Retry</Button>
            </Stack>
          </Box>
        ) : <DataGrid
          rows={data?.items ?? []}
          columns={gridColumns}
          rowCount={gridRowCount}
          loading={isLoading}
          slots={{ noRowsOverlay }}
          pageSizeOptions={[25, 50, 100]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          disableRowSelectionOnClick
          getRowId={(r) => r.id}
          rowHeight={48}
          density={density}
          {...(listView === 'spreadsheet'
            ? {
                columnVisibilityModel: columnPreferences.columnVisibilityModel,
                onColumnVisibilityModelChange: columnPreferences.onColumnVisibilityModelChange,
              }
            : {})}
          columnHeaderHeight={40}
          onRowClick={(p) => openRfq(p.row.id)}
          onCellKeyDown={(p, event) => { if (event.key === 'Enter') openRfq(p.row.id); }}
          sx={{
            border: 0,
            '& .MuiDataGrid-row': { cursor: 'pointer' },
            '& .MuiDataGrid-cell': { display: 'flex', alignItems: 'center' },
            '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 700 },
          }}
        />}
      </Paper>
    </Box>
  );
};

export default AllRFQsPage;
