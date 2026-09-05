import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Tooltip, Stack, Alert, Snackbar
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  PictureAsPdf as PdfIcon,
  Email as EmailIcon,
  Add as AddIcon,
  EmojiEvents as OutcomeIcon,
  ContentCopy as ReviseIcon,
} from '@mui/icons-material';
import quoteService, { describeQuoteSendOutcome, type QuoteDTO, type PriceAttestationSource } from '../../../api/services/quoteService';
import QuoteOutcomeDialog from './QuoteOutcomeDialog';
import PriceConfirmationDialog from './PriceConfirmationDialog';
import EmailPromptDialog from '../../../components/common/EmailPromptDialog';
import SearchField from '../../../components/common/SearchField';
import gridEmptyOverlay from '../../../components/common/gridOverlays';
import ViewTabs from '../../../components/layout/ViewTabs';
import { useAuth } from '../../../context/AuthContext';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import dayjs from 'dayjs';

/**
 * Plain-language status cell (WP-A4): outcome chips (Won green / Lost red /
 * Expired grey, reason on hover), "Stale · no reply for N days" for quiet SENT
 * quotes, "Sent · x days ago", and "Responded" once the customer replied.
 */
const QuoteStatusCell: React.FC<{ quote: QuoteDTO }> = ({ quote }) => {
  const code = (quote.statusCode || quote.statusValue || '').toUpperCase();

  const chip = (label: string, colors: { bg: string; fg: string; border: string }, tooltip?: string) => (
    <Tooltip title={tooltip || ''} disableHoverListener={!tooltip}>
      <Chip
        label={label}
        size="small"
        sx={{
          fontWeight: 900, height: 24, fontSize: '0.7rem',
          bgcolor: colors.bg, color: colors.fg,
          border: '1px solid', borderColor: colors.border,
        }}
      />
    </Tooltip>
  );

  const reasonTip = [quote.outcomeReasonName, quote.outcomeNote].filter(Boolean).join(' — ');

  if (code === 'ACCEPTED' || code === 'ORDERED') {
    return chip('Won', { bg: 'success.lighter', fg: 'success.main', border: 'success.light' }, reasonTip);
  }
  if (code === 'REJECTED') {
    return chip('Lost', { bg: 'error.lighter', fg: 'error.main', border: 'error.light' }, reasonTip);
  }
  if (code === 'EXPIRED') {
    return chip('Expired', { bg: 'action.hover', fg: 'text.secondary', border: 'divider' }, reasonTip);
  }
  if (code === 'SENT') {
    if (quote.respondedOn) {
      return chip('Responded', { bg: 'info.lighter', fg: 'info.main', border: 'info.light' });
    }
    if (quote.isStale) {
      return chip(
        `Stale · no reply for ${quote.daysSinceSent ?? '?'} days`,
        { bg: 'warning.lighter', fg: 'warning.main', border: 'warning.light' },
        'The customer has not responded. Consider following up or recording the outcome.',
      );
    }
    const ago = quote.daysSinceSent != null
      ? quote.daysSinceSent === 0 ? 'today' : `${quote.daysSinceSent} day${quote.daysSinceSent === 1 ? '' : 's'} ago`
      : null;
    return chip(ago ? `Sent · ${ago}` : 'Sent', { bg: 'success.lighter', fg: 'success.main', border: 'success.light' });
  }
  return chip(quote.statusValue || '—', { bg: 'action.hover', fg: 'text.secondary', border: 'divider' });
};

/**
 * The rail's four quote entries are FILTERED addresses (?state=draft|sent|follow-up|outcomes), but
 * this page headed itself "Quote Management / Manage sales quotations and customer offers" however
 * the rep arrived. A rep who clicked "Sent Quotes" at 4pm to chase offers saw three rows under a
 * heading that claims to show everything, with nothing lit in the rail, and told their manager the
 * pipeline was nearly empty. A filtered list has to say which slice it is, and offer the way back.
 *
 * Only the states QuoteRepository.GetAllAsync actually narrows on are named here. The server
 * IGNORES any other value, so the grid really is showing everything — announcing a filter that was
 * never applied would be the same lie pointing the other way, which is why an unrecognised state
 * gets the "not applied" warning below instead of a filter chip.
 */
const QUOTE_FILTERS: Record<string, { label: string; description: string }> = {
  draft: {
    label: 'Draft quotes',
    description: 'Showing drafts only — quotes not yet with a customer. This is not the whole pipeline.',
  },
  sent: {
    label: 'Sent quotes',
    description: 'Showing sent quotes only — quotes already with a customer. This is not the whole pipeline.',
  },
  'follow-up': {
    label: 'Follow-up due',
    description: 'Showing sent quotes past the follow-up threshold with no reply and no outcome yet. This is not the whole pipeline.',
  },
  outcomes: {
    label: 'Won / Lost / Expired',
    description: 'Showing closed quotes only — won, lost or expired. This is not the whole pipeline.',
  },
};

const QuotesPage: React.FC = () => {
  const { t: _t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const state = searchParams.get('state') || undefined;
  const activeFilter = state ? QUOTE_FILTERS[state] : undefined;
  const unappliedFilter = state && !activeFilter ? state : undefined;
  const { userData, hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');
  const [snackbar, setSnackbar] = useState<{ open: boolean; message: string; severity: 'success' | 'error' | 'info' }>({
    open: false,
    message: '',
    severity: 'success'
  });
  const [outcomeTarget, setOutcomeTarget] = useState<{ id: number; quoteNo: string } | null>(null);

  // R5: emailing a quote from the LIST runs the identical recipient -> confirm-prices -> send
  // flow the detail page runs, including the 409 `priceAttestationRequired` re-prompt. The two
  // pages must not diverge: the list is where a rep works a pipeline, and a send that skipped
  // the price confirmation would be refused by the server with nothing on screen to explain it.
  const [emailTarget, setEmailTarget] = useState<
    { id: number; quoteNo: string; customerEmail?: string; customerId?: number | null } | null>(null);
  const [priceConfirmTarget, setPriceConfirmTarget] = useState<{ id: number; quoteNo: string } | null>(null);
  const [pendingRecipient, setPendingRecipient] = useState('');

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['quotes', paginationModel, search, state],
    queryFn: () => quoteService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      state,
      businessUnitId: userData?.businessUnitId || undefined,
    }),
  });

  /**
   * The four quote states used to be four rail rows pointing at this one screen. They are tabs on
   * the screen now, so each of them needs its own empty state: "no draft quotes" and "no quotes at
   * all" are different facts and lead to different next actions.
   */
  const noRowsOverlay = React.useMemo(() => gridEmptyOverlay({
    title: 'No quotes yet',
    message: 'A quote is drafted from an RFQ. Open an RFQ that is ready to price and prepare a draft from it.',
    action: (
      <Button variant="contained" onClick={() => navigate('/procurement/rfqs/all?state=ready-for-quote')} sx={{ fontWeight: 700 }}>
        RFQs ready to quote
      </Button>
    ),
    filtered: Boolean(search) || Boolean(state),
    filteredTitle: search ? 'No quote matches this search' : `Nothing in "${activeFilter?.label ?? state}"`,
    filteredMessage: search
      ? 'Clear the search to see every quote in this view.'
      : 'This view is genuinely empty. Other quotes may exist under a different tab.',
    filteredAction: (
      <Button
        variant="outlined"
        onClick={() => { setSearch(''); navigate('/sales/quotes'); }}
        sx={{ fontWeight: 700 }}
      >
        Show all quotes
      </Button>
    ),
  }), [search, state, activeFilter, navigate]);

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

  const handleDownloadPdf = async (id: number, quoteNo: string) => {
    try {
      const blob = await quoteService.downloadPdf(id);
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `Quote_${quoteNo}.pdf`;
      document.body.appendChild(a);
      a.click();
      window.URL.revokeObjectURL(url);
    } catch (error) {
      // R5: the commonest refusal here is now "the price source has not been confirmed",
      // which the rep can act on immediately. "Failed to download PDF" would hide it.
      setSnackbar({
        open: true,
        message: presentableErrorMessage(error, 'The quote PDF could not be exported.'),
        severity: 'error',
      });
    }
  };

  const columns: GridColDef[] = [
    {
      field: 'nexoraSerial',
      headerName: 'Nexora Serial',
      width: 180,
      // No fallback through the lead or RFQ. The API used to substitute the parent's case when
      // this document carried none, so a document outside the commercial case displayed one
      // anyway. A blank here is real, and "Not linked" says so rather than reading as a
      // still-loading cell.
      valueGetter: (_value, row) => row.nexoraSerial || '',
      renderCell: (p) => (
        <Typography sx={{ fontWeight: 800, fontFamily: 'monospace', fontSize: '0.8rem', color: p.value ? 'primary.main' : 'warning.main' }}>
          {p.value || 'Not linked'}
        </Typography>
      ),
    },
    {
      field: 'quoteNo',
      headerName: 'Quote Number',
      width: 180,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', color: 'primary.main', fontFamily: 'monospace', letterSpacing: '-0.02em' }}>
            {p.row.quoteNo}
          </Typography>
          {p.row.rfqNo && (
            <Typography variant="caption" sx={{ color: 'text.secondary', fontSize: '0.7rem' }}>
              Ref: {p.row.rfqNo}
            </Typography>
          )}
        </Box>
      )
    },
    {
      field: 'customerName',
      headerName: 'Customer',
      flex: 1,
      minWidth: 200,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', color: 'text.primary' }}>
            {p.row.customerName || 'Customer unresolved'}
          </Typography>
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            {p.row.customerEmail || 'No email provided'}
          </Typography>
        </Box>
      )
    },
    {
      field: 'totalAmount',
      headerName: 'Total Amount',
      width: 150,
      renderCell: (p) => (
        p.row.statusCode?.toUpperCase() === 'DRAFT' && !p.row.currencyId && Number(p.value || 0) === 0
          ? <Chip size="small" label="Pricing Pending" color="warning" variant="outlined" />
          : <Typography sx={{ fontWeight: 800, color: 'text.primary', fontSize: '0.9rem' }}>
              {p.row.currencyCode || ''} {p.value?.toLocaleString(undefined, { minimumFractionDigits: 2 })}
            </Typography>
      )
    },
    {
      field: 'quoteDate',
      headerName: 'Date',
      width: 150,
      valueFormatter: (p: any) => dayjs(p).format('DD MMM YYYY')
    },
    {
      field: 'statusValue',
      headerName: 'Status',
      width: 190,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', height: '100%' }}>
          <QuoteStatusCell quote={p.row as QuoteDTO} />
        </Box>
      )
    },
    {
      field: 'actions',
      headerName: 'Actions',
      width: 210,
      sortable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', height: '100%' }}>
          <Button size="small" startIcon={<ViewIcon />} onClick={() => navigate(`/sales/quotes/view/${p.row.id}`)}>
            Open
          </Button>
          {hasPermission('Quotations', 'edit') && (p.row.statusCode || p.row.statusValue)?.toUpperCase() === 'SENT' && (
            <Tooltip title="Record outcome (won / lost / expired)">
              <IconButton size="small" color="warning" onClick={() => setOutcomeTarget({ id: p.row.id, quoteNo: p.row.quoteNo })}>
                <OutcomeIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          )}
          {hasPermission('Quotations', 'edit') && !['DRAFT', 'ORDERED'].includes(((p.row.statusCode || p.row.statusValue) || '').toUpperCase()) && (
            <Tooltip title="Revise — create a new draft revision of this quote">
              <IconButton
                size="small"
                color="info"
                disabled={reviseMutation.isPending}
                onClick={() => reviseMutation.mutate(p.row.id)}
              >
                <ReviseIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          )}
          <Tooltip title="Download PDF">
            <IconButton size="small" onClick={() => handleDownloadPdf(p.row.id, p.row.quoteNo)}>
              <PdfIcon fontSize="small" color="error" />
            </IconButton>
          </Tooltip>
          {/*
            Offered on SENT quotes only — the same rule the detail page applies, and the same
            rule the outcome button above already uses. A DRAFT is marked "Ready to Send"
            first; there is no second, quieter way to put a quote in front of a customer.
          */}
          {hasPermission('Quotations', 'edit') && (p.row.statusCode || p.row.statusValue)?.toUpperCase() === 'SENT' && (
            <Tooltip title="Email this quote to the customer">
              <span>
                <IconButton
                  size="small"
                  disabled={sendMutation.isPending || confirmPriceMutation.isPending}
                  onClick={() => setEmailTarget({
                    id: p.row.id,
                    quoteNo: p.row.quoteNo,
                    customerEmail: p.row.customerEmail,
                    customerId: p.row.customerId ?? null,
                  })}
                >
                  <EmailIcon fontSize="small" color="primary" />
                </IconButton>
              </span>
            </Tooltip>
          )}
        </Stack>
      )
    },
  ];

  return (
    <Box sx={{ p: 3, bgcolor: 'background.default', minHeight: '100vh' }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 0.5 }}>
            <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>
              Quote Management
            </Typography>
            {activeFilter && (
              <Chip
                size="small"
                color="warning"
                variant="outlined"
                label={`Filtered: ${activeFilter.label}`}
                sx={{ fontWeight: 800 }}
              />
            )}
          </Stack>
          <Typography variant="body2" color="text.secondary">
            {activeFilter ? activeFilter.description : 'Manage sales quotations and customer offers'}
          </Typography>
          {activeFilter && (
            <Button size="small" onClick={() => navigate('/sales/quotes')} sx={{ px: 0, fontWeight: 700 }}>
              Show all quotes
            </Button>
          )}
          {unappliedFilter && (
            <Alert severity="warning" sx={{ mt: 1 }}>
              This link asked for "{unappliedFilter}", which is not a filter this list applies. Every quote is shown.
            </Alert>
          )}
        </Box>
        <Stack direction="row" spacing={2}>
          {hasPermission('Quotations', 'create') && <Button
            variant="contained"
            startIcon={<AddIcon />}
            onClick={() => navigate('/sales/quotes/create')}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Create Quote
          </Button>}
          <Tooltip title="Refresh Data">
            <IconButton onClick={() => refetch()} sx={{ bgcolor: 'white', boxShadow: 1 }}>
              <RefreshIcon />
            </IconButton>
          </Tooltip>
        </Stack>
      </Stack>

      {/* The state filter, as one level of tabs on the screen it filters — it used to be four rail
          rows over one page, which is how a rail grows to sixty-nine destinations. */}
      <ViewTabs primaryKey="quotes" ariaLabel="Quote views" />

      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Search Nexora Serial, quote, customer or RFQ" />
      </Paper>

      <Paper sx={{ height: 'calc(100vh - 240px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        {isError ? (
          <Box sx={{ height: '100%', display: 'grid', placeItems: 'center', p: 3 }}>
            <Stack spacing={2} sx={{ alignItems: 'center', maxWidth: 480 }}>
              <Alert severity="error">We couldn't load quotes. No empty result has been assumed.</Alert>
              <Button variant="contained" startIcon={<RefreshIcon />} onClick={() => refetch()}>Retry</Button>
            </Stack>
          </Box>
        ) : <DataGrid
          rows={data?.items ?? []}
          columns={columns}
          rowCount={data?.totalItems ?? 0}
          loading={isLoading}
          slots={{ noRowsOverlay }}
          pageSizeOptions={[10, 25, 50]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          disableRowSelectionOnClick
          getRowId={(r) => r.id}
          rowHeight={70}
        />}
      </Paper>

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

      <QuoteOutcomeDialog
        open={!!outcomeTarget}
        onClose={() => setOutcomeTarget(null)}
        quoteId={outcomeTarget?.id ?? null}
        quoteNo={outcomeTarget?.quoteNo}
        invalidateKeys={[['quotes']]}
      />

      <Snackbar
        open={snackbar.open}
        autoHideDuration={6000} 
        onClose={() => setSnackbar({ ...snackbar, open: false })}
      >
        <Alert severity={snackbar.severity} sx={{ width: '100%' }}>
          {snackbar.message}
        </Alert>
      </Snackbar>
    </Box>
  );
};

export default QuotesPage;
