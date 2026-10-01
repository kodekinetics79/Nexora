import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import {
  Box, Typography, Paper, Button, Chip, IconButton, Link,
  Tooltip, Stack, Alert, Snackbar, Menu, MenuItem, ListItemIcon, ListItemText,
} from '@mui/material';
import { alpha } from '@mui/material/styles';
import {
  DataGrid, type GridColDef, type GridPaginationModel,
} from '@mui/x-data-grid';
import {
  Refresh as RefreshIcon,
  FileDownloadOutlined as DownloadIcon,
  Email as EmailIcon,
  Add as AddIcon,
  UploadFile as UploadIcon,
  ContentCopy as ReviseIcon,
  MoreVert as MoreIcon,
  Person as UserIcon,
} from '@mui/icons-material';
import ExportExcelButton, { loadAllPages, type ExportColumn } from '../../../components/common/ExportExcelButton';
import quoteService, { describeQuoteSendOutcome, type QuoteDTO, type PriceAttestationSource } from '../../../api/services/quoteService';
import UploadQuoteDialog from './UploadQuoteDialog';
import UpdateQuoteStatusDialog from './UpdateQuoteStatusDialog';
import PriceConfirmationDialog from './PriceConfirmationDialog';
import QuoteStatusChip from './QuoteStatusChip';
import { quoteCode, quoteNextMove, quoteStatusWords, type QuoteMove } from './quoteState';
import EmailPromptDialog from '../../../components/common/EmailPromptDialog';
import SearchField from '../../../components/common/SearchField';
import ListViewControls, { useListViewChoice } from '../../../components/common/ListViewControls';
import gridEmptyOverlay from '../../../components/common/gridOverlays';
import ViewTabs from '../../../components/layout/ViewTabs';
import { useAuth } from '../../../context/AuthContext';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import { formatDeadlineDay } from '../../../utils/dates';
import { DEADLINE_COLOR, deadlineWords } from '../../../utils/deadline';
import dayjs from 'dayjs';

/**
 * The states QuoteRepository.GetAllAsync actually narrows on. The tab strip names the slice being
 * shown (and "All" is one click away), so the heading no longer repeats it.
 *
 * The server IGNORES any other value, so for an unrecognised state the grid really is showing
 * everything — which is why that case gets the "not applied" warning instead of silence.
 */
const QUOTE_FILTERS: Record<string, { label: string }> = {
  draft: { label: 'Drafts' },
  sent: { label: 'Sent' },
  'follow-up': { label: 'Follow-up due' },
  outcomes: { label: 'Won / lost' },
};

const day = (value?: string | null) => (value ? dayjs(value).format('DD MMM YYYY') : '');
const dayTime = (value?: string | null) => (value ? dayjs(value).format('DD MMM YYYY HH:mm') : '');
const money = (value?: number | null) =>
  Number(value ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** Won, lost, expired or ordered: the validity date is history, not urgency. */
const CLOSED_CODES = ['ACCEPTED', 'REJECTED', 'EXPIRED', 'ORDERED'];

/**
 * Every field a quote row carries, in one order. The Excel export AND the Spreadsheet view are
 * both built from this list, so the two can never drift apart.
 */
const QUOTE_EXPORT_COLUMNS: ExportColumn<QuoteDTO>[] = [
  { header: 'Nexora Serial', value: (q) => q.nexoraSerial || q.commercialCaseReference },
  { header: 'Quote #', value: (q) => q.quoteNo },
  { header: 'Quote no. on the file', value: (q) => q.externalQuoteReference },
  { header: 'Uploaded file', value: (q) => q.uploadedFileName },
  { header: 'Revision', value: (q) => q.version },
  { header: 'RFQ #', value: (q) => q.rfqNo },
  { header: 'Customer', value: (q) => q.customerName },
  { header: 'Contact', value: (q) => q.contactName },
  { header: 'Customer email', value: (q) => q.customerEmail },
  { header: 'Owner', value: (q) => q.ownerName },
  { header: 'Date', value: (q) => day(q.quoteDate) },
  { header: 'Valid until', value: (q) => day(q.validUntil) },
  { header: 'Status', value: (q) => q.statusValue },
  { header: 'Shown as', value: (q) => quoteStatusWords(q).label },
  { header: 'Customer step', value: (q) => (q.subStatusKind === 'STEP' ? q.subStatusName : '') },
  { header: 'How it ended', value: (q) => (q.subStatusKind === 'ENDING' ? q.subStatusName : '') },
  { header: 'Step or ending since', value: (q) => (q.subStatusName ? dayTime(q.subStatusOn) : '') },
  { header: 'Currency', value: (q) => q.currencyCode },
  { header: 'Discount type', value: (q) => q.discountTypeName },
  { header: 'Discount', value: (q) => q.discountValue },
  { header: 'Total', value: (q) => q.totalAmount },
  { header: 'Lines', value: (q) => q.itemCount },
  { header: 'Sent', value: (q) => dayTime(q.sentOn) },
  { header: 'Days since sent', value: (q) => q.daysSinceSent },
  { header: 'Stale', value: (q) => (q.isStale ? 'Yes' : 'No') },
  { header: 'Customer responded', value: (q) => dayTime(q.respondedOn) },
  { header: 'Outcome date', value: (q) => dayTime(q.outcomeOn) },
  { header: 'Outcome reason', value: (q) => q.outcomeReasonName },
  { header: 'Outcome note', value: (q) => q.outcomeNote },
  { header: 'Validity extended', value: (q) => dayTime(q.validityExtendedOn) },
  { header: 'Revision impact', value: (q) => q.revisionImpactDetail
    ? `Lead revision ${q.revisionImpactDetail.fromRevision} → ${q.revisionImpactDetail.toRevision}: ${q.revisionImpactDetail.changes.length} line change(s)`
    : q.revisionImpact },
  { header: 'Remarks', value: (q) => q.headerRemarks },
  { header: 'Business unit', value: (q) => q.businessUnitName },
  { header: 'Created by', value: (q) => q.createdBy },
  { header: 'Created', value: (q) => dayTime(q.createdDate) },
  { header: 'Modified by', value: (q) => q.modifiedBy },
  { header: 'Modified', value: (q) => dayTime(q.modifiedDate) },
];

const SPREADSHEET_WIDTH: Record<string, number> = {
  'Nexora Serial': 200, 'Quote #': 150, Customer: 220, 'Customer email': 200, Status: 150,
  'Outcome note': 220, 'Revision impact': 220, Remarks: 220, Total: 140,
};

/** A click on the row itself opens the quote; a click that lands on a control does its own job. */
const ROW_CONTROL = 'button, a, input, textarea, [role="button"], [role="menuitem"], [role="checkbox"]';

const QuotesPage: React.FC = () => {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const state = searchParams.get('state') || undefined;
  const activeFilter = state ? QUOTE_FILTERS[state] : undefined;
  const unappliedFilter = state && !activeFilter ? state : undefined;
  const { userData, hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 25, page: 0 });
  const [search, setSearch] = useState('');
  // A new search or a new tab starts at page one. Staying on page 3 of the old list showed an
  // empty grid for a search that had matches.
  React.useEffect(() => {
    setPaginationModel((current) => (current.page === 0 ? current : { ...current, page: 0 }));
  }, [search, state]);
  const [uploadOpen, setUploadOpen] = useState(false);
  const [rowMenu, setRowMenu] = useState<{ anchor: HTMLElement; quote: QuoteDTO } | null>(null);
  const [statusTarget, setStatusTarget] = useState<QuoteDTO | null>(null);
  const { view, setView, density, setDensity } = useListViewChoice('nexora.quotesPage');

  // The grid runs from wherever it starts to the bottom of the window, so the pager under it is
  // always on screen. It used to be a fixed calc() under a taller header, and the pager sat below
  // the fold: the list looked like it simply ended. Measured the way ViewRFQPage measures its lines.
  const gridBoxRef = React.useRef<HTMLDivElement>(null);
  const [gridHeight, setGridHeight] = useState<number | null>(null);
  React.useLayoutEffect(() => {
    const fit = () => {
      const box = gridBoxRef.current;
      if (!box) return;
      const next = Math.max(360, Math.floor(window.innerHeight - box.getBoundingClientRect().top - 24));
      setGridHeight((current) => (current !== null && Math.abs(current - next) < 3 ? current : next));
    };
    fit();
    window.addEventListener('resize', fit);
    return () => window.removeEventListener('resize', fit);
  });
  const [snackbar, setSnackbar] = useState<{
    open: boolean; message: string; severity: 'success' | 'error' | 'info'; openQuoteId?: number;
  }>({
    open: false,
    message: '',
    severity: 'success'
  });

  // R5: emailing a quote from the LIST runs the identical recipient -> confirm-prices -> send
  // flow the detail page runs, including the 409 `priceAttestationRequired` re-prompt. The two
  // pages must not diverge: the list is where a rep works a pipeline, and a send that skipped
  // the price confirmation would be refused by the server with nothing on screen to explain it.
  const [emailTarget, setEmailTarget] = useState<
    { id: number; quoteNo: string; customerEmail?: string; customerId?: number | null } | null>(null);
  const [priceConfirmTarget, setPriceConfirmTarget] = useState<{ id: number; quoteNo: string } | null>(null);
  const [pendingRecipient, setPendingRecipient] = useState('');

  const { data, isLoading, isError, isFetching, isPlaceholderData, refetch } = useQuery({
    queryKey: ['quotes', paginationModel, search, state],
    queryFn: () => quoteService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      state,
      businessUnitId: userData?.businessUnitId || undefined,
    }),
    // Keep the current page on screen while the next one loads, instead of flashing empty.
    placeholderData: (previous) => previous,
  });
  // The previous tab's rows under the new tab's name are not this tab's rows: say it is loading.
  const showLoading = isLoading || (isFetching && isPlaceholderData);

  /**
   * Each tab gets its own empty state: "no draft quotes" and "no quotes at all" are different
   * facts and lead to different next actions. An address the server ignored is not a filter:
   * the server returned everything, so an empty grid there really means no quotes.
   */
  const noRowsOverlay = React.useMemo(() => gridEmptyOverlay({
    title: 'No quotes yet',
    message: 'A quote is drafted from an RFQ. Open an RFQ that is ready to price and prepare a draft from it.',
    action: (
      <Button variant="contained" onClick={() => navigate('/procurement/rfqs/all?state=ready-for-quote')} sx={{ fontWeight: 700 }}>
        RFQs ready to quote
      </Button>
    ),
    filtered: Boolean(search) || Boolean(activeFilter),
    filteredTitle: search ? 'No quote matches this search' : `Nothing in "${activeFilter?.label}"`,
    filteredMessage: search
      ? (activeFilter ? `Nothing in "${activeFilter.label}" matches "${search}".` : `No quote matches "${search}".`)
      : 'Other quotes may be under another tab.',
    filteredAction: (
      <Stack direction="row" spacing={1} sx={{ justifyContent: 'center' }}>
        {search && (
          <Button variant="outlined" onClick={() => setSearch('')} sx={{ fontWeight: 700 }}>
            Clear the search
          </Button>
        )}
        {activeFilter && (
          <Button
            variant="outlined"
            onClick={() => { setSearch(''); navigate('/sales/quotes'); }}
            sx={{ fontWeight: 700 }}
          >
            Show all quotes
          </Button>
        )}
      </Stack>
    ),
  }), [search, activeFilter, navigate]);

  // WP-B4 revisions-lite: clone a non-draft quote as a new DRAFT revision and
  // jump straight into editing it. 409 = draft / superseded / outcome-locked chain.
  const reviseMutation = useMutation({
    mutationFn: (id: number) => quoteService.revise(id),
    onSuccess: (draft) => {
      queryClient.invalidateQueries({ queryKey: ['quotes'] });
      navigate(`/sales/quotes/edit/${draft.id}`);
    },
    onError: (error: any) => {
      const message = error?.response?.data?.message || 'This quote cannot be revised.';
      setSnackbar({ open: true, message, severity: 'error' });
    }
  });

  // Step 2 of the send: the confirmation is on the record, now attempt the email.
  // A `priceAttestationRequired` result means NOTHING was sent — the prices moved between
  // the confirmation and the send — so the confirm dialog is deliberately left open for the
  // rep to redo it, exactly as the detail page does.
  const sendMutation = useMutation({
    mutationFn: ({ id, recipientEmail }: { id: number; recipientEmail: string }) =>
      quoteService.sendEmail(id, recipientEmail),
    onSuccess: (result, variables) => {
      if (result.priceAttestationRequired) {
        setSnackbar({
          open: true,
          message: result.message || 'The prices changed. Confirm the price source again before sending.',
          severity: 'error',
        });
        queryClient.invalidateQueries({ queryKey: ['quote-price-attestation', variables.id] });
        return;
      }
      // R17: nothing was sent because a line's output tax was never calculated. Re-confirming the
      // price source would not fix it, so the dialog closes and the server's sentence is shown.
      if (result.taxDerivationRequired) {
        setPriceConfirmTarget(null);
        setSnackbar({
          open: true,
          message: result.message
            || 'A line has no calculated tax. Set the output tax rate in Commercial Policy settings.',
          severity: 'error',
        });
        return;
      }
      setPriceConfirmTarget(null);
      if (result.held) {
        // Not a failure and not a success: the send is parked in Approvals (WP-B3).
        setSnackbar({
          open: true,
          message: result.message || 'Sent for approval — pricing is below your floor. Track it in Approvals.',
          severity: 'error',
        });
        return;
      }
      // Queued is not emailed. The server says which it was; the rep is told the same thing.
      const outcome = describeQuoteSendOutcome(result);
      setSnackbar({ open: true, message: outcome.message, severity: outcome.delivered ? 'success' : 'info' });
      queryClient.invalidateQueries({ queryKey: ['quotes'] });
    },
    onError: (error: unknown) => setSnackbar({
      open: true,
      message: presentableErrorMessage(error, 'The quote email could not be sent.'),
      severity: 'error',
    }),
  });

  // Step 1 of the send: record where the prices came from, then send. Both must succeed.
  const confirmPriceMutation = useMutation({
    mutationFn: ({ id, source, reference }: { id: number; source: PriceAttestationSource; reference: string }) =>
      quoteService.confirmPriceAttestation(id, source, reference),
    onSuccess: (_status, variables) => {
      queryClient.invalidateQueries({ queryKey: ['quote-price-attestation', variables.id] });
      sendMutation.mutate({ id: variables.id, recipientEmail: pendingRecipient });
    },
    onError: (error: unknown) => setSnackbar({
      open: true,
      message: presentableErrorMessage(error, 'The price confirmation could not be recorded.'),
      severity: 'error',
    }),
  });

  // A quote made outside Nexora downloads as the file the rep uploaded — that is what the customer
  // holds. Every other quote downloads as its Nexora PDF.
  const handleDownload = async (quote: QuoteDTO) => {
    try {
      if (quote.uploadedFileName) {
        await quoteService.downloadUploadedFile(quote.id, quote.uploadedFileName);
        return;
      }
      const blob = await quoteService.downloadPdf(quote.id);
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `Quote_${quote.quoteNo}.pdf`;
      document.body.appendChild(a);
      a.click();
      window.URL.revokeObjectURL(url);
    } catch (error) {
      // R5: the commonest refusal here is now "the price source has not been confirmed",
      // which the rep can act on immediately. "Failed to download PDF" would hide it.
      setSnackbar({
        open: true,
        message: presentableErrorMessage(error, 'The quote could not be downloaded.'),
        severity: 'error',
      });
    }
  };

  const canEdit = hasPermission('Quotations', 'edit');
  const canEnterPo = hasPermission('Orders', 'create');
  const openQuote = (quote: Pick<QuoteDTO, 'id'>) => navigate(`/sales/quotes/view/${quote.id}`);

  /**
   * What the Spreadsheet view's ⋮ offers, only what the server will accept. A quote a revision
   * replaced is neither emailed nor revised (both are refused); a closed quote's chain is locked,
   * so it is not revised; a quote made outside Nexora was never Nexora's document, so it is
   * neither emailed nor revised from here. Download is always there.
   */
  const rowActions = (quote: QuoteDTO) => {
    const live = quoteCode(quote) === 'SENT' && !quote.supersededByQuoteNo && !quote.pendingRevisionId && !quote.uploadedFileName;
    return { revise: canEdit && live, email: canEdit && live };
  };

  const runMove = (quote: QuoteDTO, move: QuoteMove) => {
    if (move === 'update') setStatusTarget(quote);
    else if (move === 'po') navigate(`/sales/quotes/view/${quote.id}?action=po`);
    else if (move === 'revision' && quote.pendingRevisionId) navigate(`/sales/quotes/view/${quote.pendingRevisionId}`);
    else openQuote(quote);
  };

  /** The row's one button, the way Leads' "Decide" is: one word for the next move. */
  const verbButton = (quote: QuoteDTO) => {
    const next = quoteNextMove(quote, { edit: canEdit, enterPo: canEnterPo });
    const quiet = next.move === 'view';
    return (
      <Button
        size="small"
        variant="outlined"
        color={quiet ? 'inherit' : 'primary'}
        aria-label={`${next.label} ${quote.quoteNo}`}
        onClick={() => runMove(quote, next.move)}
        sx={{ fontWeight: 700, width: 108, whiteSpace: 'nowrap', ...(quiet ? { color: 'text.secondary', borderColor: 'divider' } : {}) }}
      >
        {next.label}
      </Button>
    );
  };

  const quoteLink = (quote: QuoteDTO) => (
    <Link
      component="button"
      type="button"
      underline="hover"
      aria-label={`Open quote ${quote.quoteNo}`}
      onClick={() => openQuote(quote)}
      sx={{ fontWeight: 500, fontSize: '0.85rem', textAlign: 'left' }}
    >
      {quote.quoteNo}
    </Link>
  );

  const simpleColumns: GridColDef<QuoteDTO>[] = [
    {
      field: 'customerQuote',
      headerName: 'Customer & quote',
      flex: 1,
      minWidth: 280,
      valueGetter: (_value, row) => row.customerName || '',
      renderCell: (p) => {
        const quote = p.row;
        const items = quote.itemCount ?? 0;
        return (
          <Box sx={{ lineHeight: 1.3, py: 0.25, minWidth: 0 }}>
            <Typography
              noWrap
              title={quote.customerName || undefined}
              sx={{ fontWeight: 700, fontSize: '0.85rem', color: quote.customerName ? 'text.primary' : 'warning.dark' }}
            >
              {quote.customerName || 'Customer not set'}
            </Typography>
            <Stack direction="row" spacing={0.75} useFlexGap sx={{ alignItems: 'center', flexWrap: 'wrap', mt: 0.25 }}>
              {quoteLink(quote)}
              <Typography variant="caption" color="text.secondary">
                {`· ${items} ${items === 1 ? 'item' : 'items'}`}
                {quote.rfqNo ? ` · RFQ ${quote.rfqNo}` : ''}
              </Typography>
              {quote.uploadedFileName && (
                <Tooltip title={quote.uploadedFileName}>
                  <Chip
                    label={quote.externalQuoteReference ? `Uploaded · ${quote.externalQuoteReference}` : 'Uploaded'}
                    size="small"
                    variant="outlined"
                    sx={{ height: 20, fontSize: '0.65rem', fontWeight: 700 }}
                  />
                </Tooltip>
              )}
            </Stack>
          </Box>
        );
      },
    },
    {
      field: 'totalAmount',
      headerName: 'Total incl. VAT',
      width: 150,
      align: 'right',
      headerAlign: 'right',
      renderCell: (p) => {
        const quote = p.row;
        const unpriced = quoteCode(quote) === 'DRAFT' && !quote.currencyId && Number(quote.totalAmount || 0) === 0;
        if (unpriced) return <Typography color="text.secondary" sx={{ fontSize: '0.85rem' }}>—</Typography>;
        return (
          <Box sx={{ lineHeight: 1.3, textAlign: 'right' }}>
            <Typography className="tabular-nums" sx={{ fontWeight: 700, fontSize: '0.85rem', color: 'text.primary' }}>
              {quote.currencyCode ? `${quote.currencyCode} ${money(quote.totalAmount)}` : money(quote.totalAmount)}
            </Typography>
            {!quote.currencyCode && (
              <Typography variant="caption" sx={{ color: 'warning.dark', fontWeight: 700 }}>No currency</Typography>
            )}
          </Box>
        );
      },
    },
    {
      field: 'deadline',
      headerName: 'Deadline',
      width: 140,
      valueGetter: (_value, row) => row.validUntil || '',
      renderCell: (p) => {
        const quote = p.row;
        const valid = formatDeadlineDay(quote.validUntil, '');
        if (CLOSED_CODES.includes(quoteCode(quote))) {
          const closed = formatDeadlineDay(quote.outcomeOn, '');
          return (
            <Typography variant="body2" sx={{ fontSize: '0.85rem', fontWeight: 500, color: 'text.secondary' }}>
              {closed ? `Closed ${closed}` : 'Closed'}
            </Typography>
          );
        }
        // A replaced quote's clock no longer matters either: shown, but quietly.
        const quiet = Boolean(quote.supersededByQuoteNo);
        const words = deadlineWords(quote.validUntil);
        // A validity date that has passed means the prices stopped holding, not that the rep is late.
        const { text, tone } = words.tone === 'late' ? { text: 'Validity ended', tone: words.tone } : words;
        return (
          <Box sx={{ lineHeight: 1.3, py: 0.25 }}>
            <Typography variant="body2" sx={{ fontSize: '0.85rem', fontWeight: quiet ? 500 : 700, color: quiet ? 'text.secondary' : DEADLINE_COLOR[tone] }}>
              {text}
            </Typography>
            {valid && (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                Valid until {valid}
              </Typography>
            )}
          </Box>
        );
      },
    },
    {
      field: 'ownerName',
      headerName: 'Owner',
      width: 160,
      renderCell: (p) => {
        const owner = (p.row.ownerName ?? '').trim();
        if (!owner) {
          return (
            <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.disabled', fontStyle: 'italic' }}>
              Not set
            </Typography>
          );
        }
        return (
          <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', minWidth: 0 }}>
            <UserIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
            <Typography noWrap title={owner} sx={{ fontWeight: 700, fontSize: '0.8rem' }}>{owner}</Typography>
          </Stack>
        );
      },
    },
    {
      field: 'status',
      headerName: 'Status',
      width: 220,
      renderCell: (p) => <QuoteStatusChip quote={p.row} />,
    },
    {
      field: 'simpleAction',
      headerName: 'Actions',
      width: 130,
      hideable: false,
      renderCell: (p) => verbButton(p.row),
    },
  ];

  const spreadsheetColumns: GridColDef<QuoteDTO>[] = [
    ...QUOTE_EXPORT_COLUMNS.map((column, index): GridColDef<QuoteDTO> => {
      const base: GridColDef<QuoteDTO> = {
        field: `c${index}`,
        headerName: column.header,
        width: SPREADSHEET_WIDTH[column.header] ?? 140,
        valueGetter: (_value, row) => column.value(row) ?? '',
      };
      if (column.header === 'Quote #') return { ...base, renderCell: (p) => quoteLink(p.row) };
      if (column.header === 'Status') return { ...base, renderCell: (p) => <QuoteStatusChip quote={p.row} hideDetail /> };
      if (column.header === 'Total') {
        return {
          ...base, align: 'right', headerAlign: 'right',
          valueFormatter: (value: unknown) => (value === '' || value == null ? '' : money(Number(value))),
        };
      }
      return base;
    }),
    {
      field: 'sheetActions',
      headerName: 'Actions',
      width: 170,
      hideable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
          {verbButton(p.row)}
          <Tooltip title="More">
            <IconButton
              size="small"
              aria-label={`More for ${p.row.quoteNo}`}
              onClick={(event) => setRowMenu({ anchor: event.currentTarget, quote: p.row })}
            >
              <MoreIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      ),
    },
  ];

  // The server sorts nothing and filters only by tab and search: a client sort or filter would
  // rearrange the 25 rows on screen and look like it had sorted every quote.
  const gridColumns = (view === 'simple' ? simpleColumns : spreadsheetColumns)
    .map((column) => ({ ...column, sortable: false, filterable: false }));

  const menuQuote = rowMenu?.quote;
  const menuActions = menuQuote ? rowActions(menuQuote) : null;
  const totalCount = data?.totalItems ?? 0;

  return (
    <Box sx={{ p: { xs: 1, sm: 2 }, minWidth: 0 }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ alignItems: { xs: 'stretch', sm: 'center' }, mb: 1 }}>
        <Typography variant="h5" component="h1" sx={{ fontWeight: 800, letterSpacing: '-0.01em', whiteSpace: 'nowrap' }}>
          Quotes
          {!isLoading && !isError && data && (
            <Box component="span" className="tabular-nums" sx={{ color: 'text.secondary', fontWeight: 600 }} title={`${totalCount} ${totalCount === 1 ? 'quote' : 'quotes'}`}>
              {' · '}{totalCount}
            </Box>
          )}
        </Typography>
        <Box sx={{ width: { xs: '100%', sm: 340 }, maxWidth: '100%' }}>
          <SearchField width="100%" value={search} onChange={setSearch} placeholder="Search by quote, RFQ, customer or serial" />
        </Box>
        <Box sx={{ flexGrow: 1 }} />
        <Stack direction="row" spacing={1} useFlexGap sx={{ alignItems: 'center', flexWrap: 'wrap', justifyContent: { xs: 'space-between', sm: 'flex-end' } }}>
          {hasPermission('Quotations', 'create') && (
            <Button variant="outlined" startIcon={<UploadIcon />} onClick={() => setUploadOpen(true)} sx={{ fontWeight: 700, minHeight: 36 }}>
              Upload a quote
            </Button>
          )}
          {hasPermission('Quotations', 'create') && (
            <Button variant="outlined" startIcon={<AddIcon />} onClick={() => navigate('/sales/quotes/create')} sx={{ fontWeight: 700, minHeight: 36 }}>
              Create quote
            </Button>
          )}
          <ExportExcelButton
            name="Quotes"
            columns={QUOTE_EXPORT_COLUMNS}
            loadRows={() => loadAllPages((pageNumber, pageSize) => quoteService.getAll({
              pageNumber,
              pageSize,
              search: search || undefined,
              state,
              businessUnitId: userData?.businessUnitId || undefined,
            }))}
          />
          <Tooltip title="Refresh">
            <IconButton aria-label="Refresh quotes" onClick={() => refetch()} sx={{ width: 36, height: 36 }}>
              <RefreshIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      </Stack>

      {unappliedFilter && (
        <Alert severity="warning" sx={{ mb: 1 }}>
          This link asked for "{unappliedFilter}", which is not a filter this list applies. Every quote is shown.
        </Alert>
      )}

      <ViewTabs primaryKey="quotes" ariaLabel="Quote views" />

      <Box sx={{ mb: 1, display: 'flex', flexWrap: 'wrap', gap: 1, alignItems: 'center', '& .MuiToggleButton-root': { py: 0.5, minHeight: 34 }, '& .MuiButton-root': { minHeight: 34 } }}>
        <Box sx={{ flexGrow: 1 }} />
        <ListViewControls view={view} onViewChange={setView} density={density} onDensityChange={setDensity} />
      </Box>

      <Paper
        ref={gridBoxRef}
        sx={{
          height: gridHeight ?? 'calc(100vh - 300px)', width: '100%', minWidth: 0, borderRadius: 2, overflow: 'hidden',
          border: '1px solid', borderColor: 'divider', boxShadow: 'none',
        }}
      >
        {isError ? (
          <Box sx={{ height: '100%', display: 'grid', placeItems: 'center', p: 3 }}>
            <Stack spacing={2} sx={{ alignItems: 'center', maxWidth: 480 }}>
              <Alert severity="error">We couldn't load quotes. No empty result has been assumed.</Alert>
              <Button variant="contained" startIcon={<RefreshIcon />} onClick={() => refetch()} sx={{ fontWeight: 700 }}>Retry</Button>
            </Stack>
          </Box>
        ) : <DataGrid
          rows={data?.items ?? []}
          columns={gridColumns}
          rowCount={totalCount}
          loading={showLoading}
          slots={{ noRowsOverlay }}
          pageSizeOptions={[10, 25, 50, 100]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          disableRowSelectionOnClick
          getRowId={(r) => r.id}
          density={density}
          getRowHeight={view === 'simple' ? () => 'auto' : undefined}
          columnHeaderHeight={40}
          onRowClick={(params, event) => {
            if ((event.target as HTMLElement | null)?.closest?.(ROW_CONTROL)) return;
            openQuote(params.row as QuoteDTO);
          }}
          // A quote a sent revision replaced no longer counts. It stays listed, quieter.
          getRowClassName={(p) => (p.row.supersededByQuoteNo ? 'quote-superseded' : '')}
          sx={{
            border: 0,
            '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 700 },
            '& .MuiDataGrid-cell .MuiButton-root': { minHeight: 30, py: 0.25 },
            '& .MuiDataGrid-cell': { display: 'flex', alignItems: 'center', ...(view === 'simple' ? { py: 0.75 } : {}) },
            '& .MuiDataGrid-row': { cursor: 'pointer' },
            '& .MuiDataGrid-row:hover': {
              bgcolor: (t) => alpha(t.palette.primary.main, t.palette.mode === 'dark' ? 0.08 : 0.04),
            },
            '& .quote-superseded': { bgcolor: 'action.hover' },
            '& .quote-superseded .MuiTypography-root, & .quote-superseded .MuiLink-root': { color: 'text.secondary' },
          }}
        />}
      </Paper>

      <Menu
        anchorEl={rowMenu?.anchor}
        open={Boolean(rowMenu)}
        onClose={() => setRowMenu(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
      >
        {menuQuote && (
          <MenuItem onClick={() => { void handleDownload(menuQuote); setRowMenu(null); }}>
            <ListItemIcon><DownloadIcon fontSize="small" /></ListItemIcon>
            <ListItemText>{menuQuote.uploadedFileName ? 'Download file' : 'Download PDF'}</ListItemText>
          </MenuItem>
        )}
        {menuQuote && menuActions?.email && (
          <MenuItem
            disabled={sendMutation.isPending || confirmPriceMutation.isPending}
            onClick={() => {
              setEmailTarget({
                id: menuQuote.id,
                quoteNo: menuQuote.quoteNo,
                customerEmail: menuQuote.customerEmail,
                customerId: menuQuote.customerId ?? null,
              });
              setRowMenu(null);
            }}
          >
            <ListItemIcon><EmailIcon fontSize="small" /></ListItemIcon>
            <ListItemText>Send again</ListItemText>
          </MenuItem>
        )}
        {menuQuote && menuActions?.revise && (
          <MenuItem
            disabled={reviseMutation.isPending}
            onClick={() => { reviseMutation.mutate(menuQuote.id); setRowMenu(null); }}
          >
            <ListItemIcon><ReviseIcon fontSize="small" /></ListItemIcon>
            <ListItemText>Make a revision</ListItemText>
          </MenuItem>
        )}
      </Menu>

      <UploadQuoteDialog
        open={uploadOpen}
        businessUnitId={userData?.businessUnitId || undefined}
        onClose={() => setUploadOpen(false)}
        onSaved={(saved) => {
          setUploadOpen(false);
          setSnackbar({
            open: true,
            message: saved.replacedDraftNo
              ? `${saved.quoteNo} saved as sent. It replaces draft ${saved.replacedDraftNo}.`
              : `${saved.quoteNo} saved as sent.`,
            severity: 'success',
            openQuoteId: saved.quoteId,
          });
        }}
      />

      <UpdateQuoteStatusDialog
        open={Boolean(statusTarget)}
        quote={statusTarget}
        onClose={() => setStatusTarget(null)}
        invalidateKeys={[['quotes']]}
      />

      <EmailPromptDialog
        open={!!emailTarget}
        title={emailTarget ? `Email quote ${emailTarget.quoteNo}` : 'Email quote'}
        initialEmail={emailTarget?.customerEmail || ''}
        loading={sendMutation.isPending}
        businessUnitId={userData?.businessUnitId || 0}
        customerId={emailTarget?.customerId ?? null}
        composerFields="recipient-only"
        confirmLabel="Send quote"
        onCancel={() => setEmailTarget(null)}
        onConfirm={(email) => {
          // R5: choosing the recipient does not send. The prices are confirmed first.
          if (!emailTarget) return;
          setPendingRecipient(email);
          setPriceConfirmTarget({ id: emailTarget.id, quoteNo: emailTarget.quoteNo });
          setEmailTarget(null);
        }}
      />

      {priceConfirmTarget && (
        <PriceConfirmationDialog
          open
          quoteId={priceConfirmTarget.id}
          quoteNo={priceConfirmTarget.quoteNo}
          recipientEmail={pendingRecipient}
          submitting={confirmPriceMutation.isPending || sendMutation.isPending}
          onCancel={() => setPriceConfirmTarget(null)}
          onConfirm={(source, reference) =>
            confirmPriceMutation.mutate({ id: priceConfirmTarget.id, source, reference })}
        />
      )}

      <Snackbar
        open={snackbar.open}
        autoHideDuration={6000}
        onClose={() => setSnackbar({ ...snackbar, open: false })}
      >
        <Alert
          severity={snackbar.severity}
          sx={{ width: '100%' }}
          action={snackbar.openQuoteId != null ? (
            <Button
              color="inherit"
              size="small"
              onClick={() => {
                const id = snackbar.openQuoteId!;
                setSnackbar({ ...snackbar, open: false });
                openQuote({ id });
              }}
              sx={{ fontWeight: 700 }}
            >
              Open
            </Button>
          ) : undefined}
        >
          {snackbar.message}
        </Alert>
      </Snackbar>
    </Box>
  );
};

export default QuotesPage;
