import React from 'react';
import { useParams, useNavigate, Link as RouterLink } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Grid, Stack, Button, Chip,
  Table, TableHead, TableRow, TableCell, TableBody,
  Divider, CircularProgress, Card, CardContent, Tooltip, Alert, Link,
  Breadcrumbs, Accordion, AccordionSummary, AccordionDetails, Menu, MenuItem, ListItemIcon, ListItemText
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Edit as EditIcon,
  PictureAsPdf as PdfIcon,
  Email as EmailIcon,
  Send as SendIcon,
  ShoppingCart as OrderIcon,
  EmojiEvents as OutcomeIcon,
  MarkEmailRead as RespondedIcon,
  ContentCopy as ReviseIcon,
  EventRepeat as ExtendValidityIcon,
  NotificationsActive as FollowUpIcon,
  ExpandMore as ExpandMoreIcon,
  NavigateNext as NextIcon
} from '@mui/icons-material';
import quoteService, { describeQuoteSendOutcome, type PriceAttestationSource } from '../../../api/services/quoteService';
import QuoteOutcomeDialog from './QuoteOutcomeDialog';
import ExtendValidityDialog from './ExtendValidityDialog';
import FollowUpDialog from './FollowUpDialog';
import PriceConfirmationDialog from './PriceConfirmationDialog';
import EmailPromptDialog from '../../../components/common/EmailPromptDialog';
import { CustomerAwardDialog, type CustomerAwardQuote } from './customer-awards';
import { useAuth } from '../../../context/AuthContext';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import { formatMoney } from '../../../utils/currency';
import { summariseStoredQuote } from './quoteTotals';
import { alpha } from '@mui/material/styles';
import dayjs from 'dayjs';
import { toast } from 'react-hot-toast';
import CommercialLineIntelligence from '../../../components/common/CommercialLineIntelligence';
import NextStepPanel from '../../../components/common/NextStepPanel';
import procurementService from '../../../api/services/procurementService';
import { statusLabel } from '../../../utils/statusLabels';

const QuoteViewPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { userData, hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const businessUnitId = userData?.businessUnitId || 0;
  const [moreAnchor, setMoreAnchor] = React.useState<null | HTMLElement>(null);
  const moreMenuId = React.useId();

  const { data: quote, isLoading, isError, refetch } = useQuery({
    queryKey: ['quote-detail', id],
    queryFn: () => quoteService.getById(Number(id), businessUnitId),
    enabled: !!id
  });
  const sourcingQuery = useQuery({
    queryKey: ['procurement-sourcing-workbench', quote?.rfqId],
    queryFn: () => procurementService.getWorkbench(Number(quote?.rfqId)),
    enabled: Boolean(quote?.rfqId),
    retry: 1,
  });

  const pdfMutation = useMutation({
    mutationFn: () => quoteService.downloadPdf(Number(id)),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `${quote?.quoteNo || `quote-${id}`}.pdf`;
      anchor.click();
      URL.revokeObjectURL(url);
    },
    // R5: the PDF can now be refused because nobody has confirmed where the prices came
    // from. That reason is actionable, so it must reach the rep verbatim instead of being
    // flattened into "export failed".
    onError: (error: unknown) =>
      toast.error(presentableErrorMessage(error, 'The quote PDF could not be exported.'), { duration: 6000 })
  });

  // WP-B4 revisions-lite: chain facts drive the "Rev n" chip + Revise button.
  const { data: revisionInfo } = useQuery({
    queryKey: ['quote-revisions', id],
    queryFn: () => quoteService.getRevisionInfo(Number(id)),
    enabled: !!id
  });

  // Everything the SERVER knows would refuse this send, asked before the dialog opens.
  //
  // The three client-side reasons below this used to be the whole story, and they could only
  // ever see what the quote screen already had. They cannot see that the business unit has no
  // legal name, or that the tenant has no transmitting mailbox — refusals that live in the
  // background delivery worker, reach nobody, and permanently burn the quote number because
  // the delivery idempotency key is fixed per quote. They also missed a draft with prices but
  // no currency, which is the shape both customer quotes on production are in today.
  //
  // Only asked for a quote the Send button can appear on. The readiness check consults
  // IOutboundSenderResolver, which deliberately keeps no per-tenant cache ("one projected read
  // per send is the honest cost, and quote and RFQ sends are rare"), so it must not be turned
  // into one read per page view of every quote ever raised.
  const sendableStatus = ['DRAFT', 'SENT'].includes(
    (quote?.statusCode || quote?.statusValue || '').toUpperCase());
  const { data: sendReadiness } = useQuery({
    queryKey: ['quote-send-readiness', id],
    queryFn: () => quoteService.getSendReadiness(Number(id)),
    enabled: !!id && sendableStatus
  });

  const reviseMutation = useMutation({
    mutationFn: () => quoteService.revise(Number(id)),
    onSuccess: (draft) => {
      toast.success(`Revision ${draft.quoteNo} created — you are now editing the new draft`);
      queryClient.invalidateQueries({ queryKey: ['quote-revisions', id] });
      queryClient.invalidateQueries({ queryKey: ['quotes'] });
      navigate(`/sales/quotes/edit/${draft.id}`);
    },
    onError: (error: any) => {
      const message = error?.response?.data?.message || 'This quote cannot be revised.';
      toast.error(message, { duration: 6000 });
    }
  });

  // WP-B3: quote-send with below-floor hold awareness.
  // R5: the send is gated on a price-provenance confirmation. The recipient is chosen
  // first, then the rep confirms the prices and where they came from; only then is the
  // send attempted. The server refuses an unconfirmed send regardless of this flow.
  const [emailOpen, setEmailOpen] = React.useState(false);
  // R7: extending the validity of a quote that is already with the customer.
  const [extendValidityOpen, setExtendValidityOpen] = React.useState(false);
  const [followUpOpen, setFollowUpOpen] = React.useState(false);
  const [priceConfirmOpen, setPriceConfirmOpen] = React.useState(false);
  const [pendingRecipient, setPendingRecipient] = React.useState('');
  const [holdInfo, setHoldInfo] = React.useState<string | null>(null);
  const sendMutation = useMutation({
    mutationFn: (recipientEmail: string) => quoteService.sendEmail(Number(id), recipientEmail),
    onSuccess: (result) => {
      if (result.priceAttestationRequired) {
        // The prices changed between the confirmation and the send — confirm again.
        toast.error(result.message || 'The prices changed. Confirm the price source again before sending.', { duration: 8000 });
        queryClient.invalidateQueries({ queryKey: ['quote-price-attestation', Number(id)] });
        setPriceConfirmOpen(true);
        return;
      }
      // R17: nothing was sent because a line's output tax was never calculated. Confirming the
      // price source again would not help, so the confirm dialog is closed and the server's
      // sentence — which names the line and the fix — is shown as-is.
      if (result.taxDerivationRequired) {
        toast.error(result.message
          || 'A line has no calculated tax. Set the output tax rate in Commercial Policy settings.',
          { duration: 10000 });
        setPriceConfirmOpen(false);
        return;
      }
      setPriceConfirmOpen(false);
      setEmailOpen(false);
      if (result.held) {
        setHoldInfo(result.message || null);
        toast('Sent for approval — pricing is below your floor. Track it in Approvals.', { icon: '⏳', duration: 6000 });
      } else {
        // Queued is not emailed. The server says which it was; the rep is told the same thing.
        const outcome = describeQuoteSendOutcome(result);
        if (outcome.delivered) toast.success(outcome.message);
        else toast(outcome.message, { icon: '📨', duration: 8000 });
        queryClient.invalidateQueries({ queryKey: ['quote-detail', id] });
        queryClient.invalidateQueries({ queryKey: ['quote-send-readiness', id] });
      }
    },
    // The server's refusals here are sentences a rep can act on — a stale revision, a delivery
    // key already used, prices that moved since the send was authorised, a delivery already
    // dead-lettered. Replacing all of them with "Failed to send the quote email" threw away the
    // only part that said what to do.
    onError: (error) => toast.error(
      presentableErrorMessage(error, 'Failed to send the quote email'), { duration: 10000 })
  });

  // R5: record the confirmation, then send. Both steps must succeed for the quote to go out.
  const confirmPriceMutation = useMutation({
    mutationFn: ({ source, reference }: { source: PriceAttestationSource; reference: string }) =>
      quoteService.confirmPriceAttestation(Number(id), source, reference),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quote-price-attestation', Number(id)] });
      sendMutation.mutate(pendingRecipient);
    },
    onError: (error: any) => {
      const message = error?.response?.data?.message || 'The price confirmation could not be recorded.';
      toast.error(message, { duration: 6000 });
    }
  });

  // WP-A4: outcome capture + "customer responded" stamp.
  const [outcomeOpen, setOutcomeOpen] = React.useState(false);
  const respondedMutation = useMutation({
    mutationFn: () => quoteService.markResponded(Number(id)),
    onSuccess: () => {
      toast.success('Marked as responded');
      queryClient.invalidateQueries({ queryKey: ['quote-detail', id] });
    },
    onError: () => toast.error('Failed to mark as responded')
  });

  const resolveImpactMutation = useMutation({
    mutationFn: () => quoteService.resolveRevisionImpact(Number(id)),
    onSuccess: () => {
      toast.success('Revision review marked complete');
      queryClient.invalidateQueries({ queryKey: ['quote-detail', id] });
    },
    onError: () => toast.error('The revision review could not be completed')
  });

  const [awardOpen, setAwardOpen] = React.useState(false);

  if (isLoading) return <Box sx={{ p: 4, display: 'flex', justifyContent: 'center' }}><CircularProgress /></Box>;
  if (isError) return <Box sx={{ p: 4 }}><Alert severity="error" action={<Button color="inherit" onClick={() => refetch()}>Retry</Button>}>We couldn't load this quote.</Alert></Box>;
  if (!quote) return <Box sx={{ p: 4 }}>Quote not found</Box>;

  // The financial breakdown, READ from what the server stored — never reconstructed.
  //
  // This block used to compute `headerDiscount = (gross - lineDiscounts) - quote.totalAmount`,
  // subtracting a tax-INCLUSIVE grand total from a tax-EXCLUSIVE net. On a 1,000.00 quote at 15%
  // with no header discount that is -150.00, suppressed by the `> 0` guard, leaving a panel that
  // read 1,000.00 / 0.00 / 1,150.00 and did not add up. With a 200.00 header discount it printed
  // 80.00. The same reconstruction, in the PDF builder, is what QuoteItem.HeaderDiscountAllocated
  // was added to stop; the column has always been on the row and simply never reached this screen.
  //
  // Every figure below is a stored per-line value or a sum of them, matching QuoteService's PDF
  // builder line for line. The customer's copy and the rep's screen now state one arithmetic.
  const totals = summariseStoredQuote(
    quote.quoteItems,
    quote.discountTypeId != null && quote.discountValue != null,
    quote.totalAmount || 0,
  );
  const itemsSubtotal = totals.grossSubTotal;
  const itemsDiscounts = totals.totalLineDiscounts;
  const headerDiscount = totals.headerDiscount;
  // Name the rate only when every taxed line shares one. A quote that mixes a zero-rated export
  // with a standard line has no single rate true of its total, so the label stays bare.
  const vatLabel = totals.singleTaxRatePercent === null ? 'VAT' : `VAT ${totals.singleTaxRatePercent}%`;
  const awardQuote: CustomerAwardQuote | null = quote.commercialCaseId && quote.customerId && quote.currencyId
    ? {
        id: quote.id,
        quoteNo: quote.quoteNo,
        version: quote.version,
        commercialCaseId: quote.commercialCaseId,
        customerId: quote.customerId,
        currencyId: quote.currencyId,
        currencyCode: quote.currencyCode,
        lines: quote.quoteItems.map((item) => ({
          id: item.id,
          productId: item.productId,
          productName: item.productName,
          description: item.itemDescription || item.productName || `Quote line ${item.id}`,
          quantity: item.quantity,
          unitPrice: item.unitPrice,
        })),
      }
    : null;
  const sourceFor = (item: any) => {
    const rfqItemId = Number(item.rfqItemId || item.rfqitemId || 0);
    const line = sourcingQuery.data?.lines.find((entry) => entry.id === rfqItemId);
    const award = sourcingQuery.data?.awards.find((entry) => entry.rfqItemId === rfqItemId);
    const offer = award ? sourcingQuery.data?.offers.find((entry) => entry.id === award.supplierQuotedItemId) : undefined;
    const source = award && line?.resolution === 'PARTIAL' ? 'MIXED_INVENTORY_AND_SUPPLIER'
      : award ? 'SELECTED_SUPPLIER_QUOTE'
        : line?.resolution === 'IN_STOCK' ? 'INTERNAL_INVENTORY'
          : line?.resolution === 'INCOMING' ? 'INCOMING_INVENTORY'
            : 'COST_SOURCE_PENDING';
    return { rfqItemId, line, award, offer, source };
  };
  const supplierValidityWarnings = quote.quoteItems.map(sourceFor).filter(({ offer }) => offer && (
    !offer.validUntil || dayjs(offer.validUntil).isBefore(dayjs()) ||
    Boolean(quote.validUntil && dayjs(offer.validUntil).isBefore(dayjs(quote.validUntil)))
  ));
  const isDraftQuote = (quote.statusCode || quote.statusValue || '').toUpperCase() === 'DRAFT';
  const isUnpricedDraft = (quote.statusCode || quote.statusValue || '').toUpperCase() === 'DRAFT'
    && !quote.currencyId
    && quote.quoteItems.every((item) => Number(item.unitPrice || 0) === 0);
  // Why "Send to customer" is disabled, printed beside the button so the rep can act on it
  // instead of raising a ticket. Order matters: a stale revision must be reviewed before either
  // of the other two is worth fixing; a draft with no prices cannot have tax; a priced draft
  // whose lines carry no derived tax is blocked by one setting a manager can change in Setup.
  //
  // The tax gate used to live only in the Financial Summary as a warning ("cannot be sent")
  // while this button stayed enabled — the server refused the send later with a toast, and the
  // rep had been told two things at once.
  //
  // The server's list wins whenever it has loaded: it applies the same rules the sender and the
  // renderer will, so it cannot disagree with them. The client heuristics remain as the fallback
  // for a failed or in-flight readiness call, so a broken query never makes this screen worse
  // than it was.
  const serverBlocker = sendReadiness?.blockers?.[0];
  const sendBlockedReason: { text: string; link?: { label: string; to: string } } | null = serverBlocker
    ? {
        text: serverBlocker.message,
        link: serverBlocker.setupPath && serverBlocker.setupLabel
          ? { label: `Open ${serverBlocker.setupLabel}`, to: serverBlocker.setupPath }
          : undefined,
      }
    : sendReadiness?.canSend
      ? null
      : quote.revisionImpact
        ? { text: 'Review the customer revision before sending.' }
        : isUnpricedDraft
          ? { text: 'Add prices to the quote lines before sending.' }
          : totals.hasUnderivedTax
            ? {
                text: 'Set the VAT rate in Setup > Commercial Policy before sending.',
                link: { label: 'Open Commercial Policy', to: '/setup/commercial-policy' },
              }
            : null;
  const revisionImpactPresentation = quote.revisionImpact === 'INVENTORY_REVALIDATION_REQUIRED'
    ? {
        title: 'Inventory Revalidation Required',
        detail: 'Stock changed after this Quote Draft was prepared. Revalidate inventory before sending it to the customer.',
        action: 'Mark revalidation complete',
      }
    : quote.revisionImpact
      ? {
          title: 'Customer Revision Received',
          detail: `This Quote Draft is stale and must be reviewed against Lead Revision ${quote.sourceLeadRevision}. The customer-issued document has not been overwritten.`,
          action: 'Mark review complete',
        }
      : null;

  // Which control is THE next step. Exactly one contained button per state; a contained button
  // that is also disabled points the rep at a dead end, so a blocked draft promotes Edit instead.
  const statusUpper = (quote.statusCode || quote.statusValue || '').toUpperCase();
  const isSentQuote = quote.statusValue === 'Sent';
  const isSuperseded = Boolean(revisionInfo?.supersededByQuoteNo);
  const primaryAction: 'send' | 'edit' | 'responded' | 'outcome' | 'po' | 'pdf' | null =
    isSuperseded ? null
      : quote.statusValue === 'Accepted' ? 'po'
        : isSentQuote ? (quote.respondedOn ? 'outcome' : 'responded')
          : statusUpper === 'ORDERED' ? 'pdf'
            : isDraftQuote ? (sendBlockedReason === null ? 'send' : 'edit')
              : null;
  const blockerRows = sendReadiness?.blockers?.length
    ? sendReadiness.blockers.map((blocker) => ({
        key: blocker.code,
        text: blocker.message,
        link: blocker.setupPath && blocker.setupLabel
          ? { label: `Open ${blocker.setupLabel}`, to: blocker.setupPath }
          : undefined,
      }))
    : sendBlockedReason ? [{ key: 'client', ...sendBlockedReason }] : [];
  // Pre-send gates only matter while the quote can still be sent; on an ordered or closed
  // quote they would argue with the sentence that says it is finished.
  const showPreSendGates = isDraftQuote || isSentQuote;
  const showBlockerPanel = isUnpricedDraft || blockerRows.length > 0 || (showPreSendGates && (supplierValidityWarnings.length > 0 || revisionImpactPresentation !== null));
  // The sentence that tells the rep what happens next, in every state, derived from facts the
  // page already holds. The screen drives; the rep never has to work out the next move.
  const blockerCount = blockerRows.length + (showPreSendGates && revisionImpactPresentation ? 1 : 0) + (showPreSendGates && supplierValidityWarnings.length > 0 ? 1 : 0);
  const nextStepText = revisionInfo?.supersededByQuoteNo
    ? `A newer revision replaces this quote. Work on ${revisionInfo.supersededByQuoteNo} instead.`
    : statusUpper === 'ORDERED'
      ? 'This quote became an order. Export the PDF if the customer needs a copy; nothing else is left to do here.'
      : quote.outcomeOn && statusUpper !== 'ACCEPTED'
        ? 'The outcome is recorded. Revise this quote if the customer comes back.'
        : quote.statusValue === 'Accepted'
          ? (awardQuote
            ? 'The customer accepted. Capture their purchase order to turn this quote into an order.'
            : 'The customer accepted, but this quote has no linked case, customer or currency, so the purchase order cannot be captured yet.')
          : quote.statusValue === 'Sent'
            ? (quote.respondedOn
              ? 'The customer replied. Record the outcome: won, lost or expired.'
              : 'Waiting for the customer. Mark "Customer responded" when they reply, or record the outcome.')
            : isDraftQuote
              ? (sendBlockedReason === null
                ? (blockerCount > 0
                  ? `Nothing blocks sending, but check the ${blockerCount === 1 ? 'item' : `${blockerCount} items`} below first.`
                  : 'Prices are in and nothing is blocking. Send this quote to the customer.')
                : `Fix the ${blockerCount === 1 ? 'item' : `${blockerCount} items`} below, then send the quote.`)
              : 'This quote is closed. Revise it if the customer comes back.';
  const moreMenuOpen = Boolean(moreAnchor);
  const sendControl = hasPermission('Quotations', 'edit')
    && !isSuperseded
    && quote.statusValue?.toUpperCase() !== 'ORDERED'
    && (isDraftQuote || isSentQuote)
    ? (
      <Tooltip title={sendBlockedReason ? `${blockerCount || 1} thing${(blockerCount || 1) === 1 ? '' : 's'} must be fixed first — see the list below` : ''}>
        <Box
          component="span"
          role={sendBlockedReason ? 'button' : undefined}
          aria-disabled={sendBlockedReason ? 'true' : undefined}
          tabIndex={sendBlockedReason ? 0 : -1}
          sx={{ display: 'inline-flex', borderRadius: 2, '&:focus-visible': { outline: '3px solid', outlineColor: 'primary.main', outlineOffset: 2 } }}
        >
          <Button
            variant={primaryAction === 'send' ? 'contained' : 'outlined'}
            startIcon={isDraftQuote ? <SendIcon /> : <EmailIcon />}
            disabled={sendBlockedReason !== null}
            title={sendBlockedReason?.text}
            onClick={() => setEmailOpen(true)}
            sx={{ borderRadius: 2, fontWeight: primaryAction === 'send' ? 800 : undefined, whiteSpace: 'nowrap' }}
          >
            {isDraftQuote ? 'Send to customer' : 'Send again'}
          </Button>
        </Box>
      </Tooltip>
    ) : null;
  const editControl = hasPermission('Quotations', 'edit') ? (
    <Tooltip title={quote.statusValue?.toUpperCase() === 'ORDERED' ? 'An ordered quote can no longer be edited.' : 'Change prices, validity, discounts and remarks.'} describeChild>
      <span>
        <Button
          variant={primaryAction === 'edit' ? 'contained' : 'outlined'}
          startIcon={<EditIcon />}
          onClick={() => navigate(`/sales/quotes/edit/${id}`)}
          disabled={quote.statusValue?.toUpperCase() === 'ORDERED'}
          sx={{ borderRadius: 2, fontWeight: primaryAction === 'edit' ? 800 : undefined, whiteSpace: 'nowrap' }}
        >
          Edit
        </Button>
      </span>
    </Tooltip>
  ) : null;
  const respondedControl = hasPermission('Quotations', 'edit') && !isSuperseded && isSentQuote && !quote.respondedOn ? (
    <Tooltip title="Note that the customer has replied. The quote stops counting as stale." describeChild>
      <span>
        <Button
          variant={primaryAction === 'responded' ? 'contained' : 'outlined'}
          startIcon={respondedMutation.isPending ? <CircularProgress size={18} /> : <RespondedIcon />}
          onClick={() => respondedMutation.mutate()}
          disabled={respondedMutation.isPending}
          sx={{ borderRadius: 2, fontWeight: primaryAction === 'responded' ? 800 : undefined, whiteSpace: 'nowrap' }}
        >
          Customer responded
        </Button>
      </span>
    </Tooltip>
  ) : null;
  const outcomeControl = hasPermission('Quotations', 'edit') && !isSuperseded && isSentQuote ? (
    <Tooltip title="Close this quote as won, lost or expired, with the reason." describeChild>
      <Button
        variant={primaryAction === 'outcome' ? 'contained' : 'outlined'}
        color="warning"
        startIcon={<OutcomeIcon />}
        onClick={() => setOutcomeOpen(true)}
        sx={{ borderRadius: 2, fontWeight: 800, whiteSpace: 'nowrap' }}
      >
        Record outcome
      </Button>
    </Tooltip>
  ) : null;
  const poControl = hasPermission('Orders', 'create') && quote.statusValue === 'Accepted' ? (
    <Tooltip title={!awardQuote ? 'This quote needs a linked commercial case, a customer and a currency before a client PO can be captured.' : ''}>
      <Box component="span" role={!awardQuote ? 'button' : undefined} aria-disabled={!awardQuote ? 'true' : undefined} tabIndex={!awardQuote ? 0 : -1} sx={{ display: 'inline-flex', borderRadius: 2, '&:focus-visible': { outline: '3px solid', outlineColor: 'primary.main', outlineOffset: 2 } }}>
        <Button
            variant="contained"
            color="primary"
            startIcon={<OrderIcon />}
            onClick={() => setAwardOpen(true)}
            disabled={!awardQuote || quote.statusValue?.toUpperCase() === 'ORDERED'}
            sx={{ borderRadius: 2, fontWeight: 800, whiteSpace: 'nowrap' }}
        >
          Capture Client PO
        </Button>
      </Box>
    </Tooltip>
  ) : null;
  // The control that belongs beside the next-step sentence. The rail keeps the rest.
  const panelAction = primaryAction === 'send' ? sendControl
    : primaryAction === 'edit' ? editControl
      : primaryAction === 'responded' ? respondedControl
        : primaryAction === 'outcome' ? outcomeControl
          : primaryAction === 'po' ? poControl
            : null;

  return (
    <Box sx={{ p: { xs: 1.5, md: 3 }, maxWidth: 1600, mx: 'auto', bgcolor: 'background.default', minHeight: '100vh', minWidth: 0, overflowX: 'hidden' }}>
      <Breadcrumbs separator={<NextIcon sx={{ fontSize: 14 }} />} sx={{ mb: 1.5 }}>
        <Link component="button" variant="caption" onClick={() => navigate('/sales/quotes')} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
          Quotes
        </Link>
        <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 900, textTransform: 'uppercase' }}>{quote.quoteNo}</Typography>
      </Breadcrumbs>

      {/* Identity row: number, state, and only the chips that change what the rep does next.
          The serial and the record facts live in the strip and the folds below. */}
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: { xs: 'flex-start', lg: 'center' }, gap: 2, flexDirection: { xs: 'column', lg: 'row' }, mb: 2 }}>
        <Stack direction="row" useFlexGap spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', minWidth: 0 }}>
          <Typography variant="h4" sx={{ fontWeight: 800, lineHeight: 1.15, whiteSpace: 'nowrap' }}>Quote: {quote.quoteNo}</Typography>
          <Chip label={quote.statusValue} color={quote.statusValue === 'Sent' ? 'success' : quote.statusValue === 'Accepted' ? 'primary' : 'default'} sx={{ fontWeight: 700, borderRadius: 1.5 }} />
          {!quote.nexoraSerial && (
            // Shown, never hidden. A quotation with no commercial case cannot be traced from
            // inquiry to delivery, and an absent chip would read as a rendering gap rather than
            // the defect it is.
            <Tooltip title="This quotation states no commercial case, so it cannot be traced to its inquiry or to delivery. It was created outside the RFQ path.">
              <Chip label="Not linked to a case" color="warning" variant="outlined" sx={{ fontWeight: 900, maxWidth: '100%' }} />
            </Tooltip>
          )}
          {revisionInfo?.supersededByQuoteNo && (
            <Tooltip title="A newer revision replaces this quote — open it">
              <Chip
                label={`Superseded by ${revisionInfo.supersededByQuoteNo}`}
                color="warning"
                variant="outlined"
                size="small"
                onClick={() => navigate(`/sales/quotes/view/${revisionInfo.supersededByQuoteId}`)}
                sx={{ fontWeight: 700, borderRadius: 1.5 }}
              />
            </Tooltip>
          )}
          {quote.outcomeOn && (
            <Tooltip title={[quote.outcomeReasonName, quote.outcomeNote].filter(Boolean).join(' — ') || 'No reason recorded'}>
              <Chip
                label={(quote.statusCode || '').toUpperCase() === 'ACCEPTED' || (quote.statusCode || '').toUpperCase() === 'ORDERED'
                  ? 'Won' : (quote.statusCode || '').toUpperCase() === 'REJECTED' ? 'Lost' : 'Expired'}
                color={(quote.statusCode || '').toUpperCase() === 'ACCEPTED' || (quote.statusCode || '').toUpperCase() === 'ORDERED'
                  ? 'success' : (quote.statusCode || '').toUpperCase() === 'REJECTED' ? 'error' : 'default'}
                sx={{ fontWeight: 700, borderRadius: 1.5 }}
              />
            </Tooltip>
          )}
          {quote.isStale && !quote.respondedOn && (
            <Chip
              label={`Stale · no reply for ${quote.daysSinceSent ?? '?'} days`}
              color="warning"
              variant="outlined"
              sx={{ fontWeight: 700, borderRadius: 1.5 }}
            />
          )}
          {revisionInfo && revisionInfo.revisionNo > 1 && revisionInfo.revisionOfQuoteNo && (
            <Tooltip title={`This quote is revision ${revisionInfo.revisionNo} and replaces ${revisionInfo.revisionOfQuoteNo}`}>
              <Chip
                label={`Rev ${revisionInfo.revisionNo} · replaces ${revisionInfo.revisionOfQuoteNo}`}
                color="info"
                variant="outlined"
                size="small"
                onClick={revisionInfo.revisionOfQuoteId ? () => navigate(`/sales/quotes/view/${revisionInfo.revisionOfQuoteId}`) : undefined}
                sx={{ fontWeight: 700, borderRadius: 1.5 }}
              />
            </Tooltip>
          )}
        </Stack>

        {/* One action rail: Back first, the rarely used actions behind "More", and exactly one
            contained button — the next step for this quote's state — last. */}
        <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'center', position: { lg: 'sticky' }, top: 8, zIndex: 2, p: 0.75, borderRadius: 2, bgcolor: (t) => alpha(t.palette.background.default, 0.72), backdropFilter: 'blur(10px)', '& > button, & > span > button': { whiteSpace: 'nowrap' } }}>
          <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate('/sales/quotes')} sx={{ borderRadius: 2, borderColor: 'divider', color: 'text.secondary' }}>
            Back
          </Button>
          {primaryAction !== 'edit' && editControl}
          <Tooltip title={isUnpricedDraft ? 'Available once the lines are priced.' : 'Download the quotation the customer will receive.'} describeChild>
            <span>
              <Button
                variant={primaryAction === 'pdf' ? 'contained' : 'outlined'}
                startIcon={<PdfIcon />}
                onClick={() => pdfMutation.mutate()}
                disabled={pdfMutation.isPending || isUnpricedDraft}
                sx={{ borderRadius: 2 }}
              >
                Export PDF
              </Button>
            </span>
          </Tooltip>
          {hasPermission('Quotations', 'edit') && (
            <>
              <Button
                title="Follow-up, extend validity, revise, sourcing and the source RFQ"
                variant="outlined"
                endIcon={<ExpandMoreIcon />}
                aria-haspopup="menu"
                aria-expanded={moreMenuOpen ? 'true' : undefined}
                aria-controls={moreMenuOpen ? moreMenuId : undefined}
                onClick={(event) => setMoreAnchor(event.currentTarget)}
                sx={{ borderRadius: 2 }}
              >
                More
              </Button>
              <Menu id={moreMenuId} anchorEl={moreAnchor} open={moreMenuOpen} onClose={() => setMoreAnchor(null)}>
                {/* A follow-up the rep sets by hand. Delivery creates one automatically when a quote is
                    sent; anything promised in a phone call afterwards had nowhere to go. */}
                <MenuItem onClick={() => { setMoreAnchor(null); setFollowUpOpen(true); }}>
                  <ListItemIcon><FollowUpIcon fontSize="small" /></ListItemIcon>
                  <ListItemText primary="Follow up on this quote" secondary="Set a reminder for yourself about this quote. It appears on your Follow-ups list." />
                </MenuItem>
                {/*
                  R7: the buyer's most common request — "can you hold your price for another two
                  weeks". Offered only while the quote is live with the customer and has not been
                  superseded; extending records a reason and does NOT create a revision, so the
                  customer keeps looking at the same commercial offer.
                */}
                {quote.canExtendValidity && !revisionInfo?.supersededByQuoteId && (
                  <MenuItem onClick={() => { setMoreAnchor(null); setExtendValidityOpen(true); }}>
                    <ListItemIcon><ExtendValidityIcon fontSize="small" /></ListItemIcon>
                    <ListItemText primary="Extend validity" secondary="Hold this quote's price open until a later date. Records a reason; does not create a revision." />
                  </MenuItem>
                )}
                {revisionInfo?.canRevise && (
                  <MenuItem disabled={reviseMutation.isPending} onClick={() => { setMoreAnchor(null); reviseMutation.mutate(); }}>
                    <ListItemIcon>{reviseMutation.isPending ? <CircularProgress size={18} /> : <ReviseIcon fontSize="small" />}</ListItemIcon>
                    <ListItemText primary="Revise" secondary="Create a new draft revision of this quote (the original stays untouched)" />
                  </MenuItem>
                )}
                {quote.rfqId && (
                  <MenuItem onClick={() => { setMoreAnchor(null); navigate(`/procurement/rfqs/${quote.rfqId}/sourcing`); }}>
                    <ListItemIcon><OutcomeIcon fontSize="small" /></ListItemIcon>
                    <ListItemText primary="Sourcing & offers" />
                  </MenuItem>
                )}
                {quote.rfqId && (
                  <MenuItem onClick={() => { setMoreAnchor(null); navigate(`/procurement/rfqs/view/${quote.rfqId}`); }}>
                    <ListItemIcon><NextIcon fontSize="small" /></ListItemIcon>
                    <ListItemText primary="Open Source RFQ" secondary={`RFQ ${quote.rfqNo}`} />
                  </MenuItem>
                )}
              </Menu>
            </>
          )}

          {primaryAction !== 'send' && sendControl}

          {primaryAction !== 'responded' && respondedControl}
          {primaryAction !== 'outcome' && outcomeControl}
          {primaryAction !== 'po' && poControl}
        </Stack>
      </Box>

      {/* Who, how to reach them, how long the offer stands. The four facts a rep opens a quote for. */}
      <Paper sx={{ p: 2, mb: 2, borderRadius: 3, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <Grid container spacing={2} sx={{ alignItems: 'center' }}>
          <Grid size={{ xs: 12, sm: 6, md: 3 }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Customer Name</Typography><Typography sx={{ fontWeight: 700 }}>{quote.customerName || 'Customer unresolved'}</Typography></Grid>
          <Grid size={{ xs: 12, sm: 6, md: 3 }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Contact</Typography><Typography sx={{ fontWeight: 700 }}>{quote.contactName || quote.customerEmail || 'Contact unresolved'}</Typography></Grid>
          <Grid size={{ xs: 12, sm: 6, md: 2 }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Email</Typography><Typography sx={{ fontWeight: 700, overflowWrap: 'anywhere' }}>{quote.customerEmail || 'N/A'}</Typography></Grid>
          <Grid size={{ xs: 12, sm: 6, md: 2 }}>
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Validity</Typography>
            <Typography sx={{ fontWeight: 700 }}>{quote.validUntil ? `Until ${dayjs(quote.validUntil).format('DD MMM YYYY')}` : 'Commercial validity pending'}</Typography>
            {quote.validityExtendedOn && (
              <Typography variant="caption" color="text.secondary">
                Extended on {dayjs(quote.validityExtendedOn).format('DD MMM YYYY')} — open “Extend validity” to read why.
              </Typography>
            )}
          </Grid>
          <Grid size={{ xs: 12, md: 2 }} sx={{ display: 'flex', justifyContent: { md: 'flex-end' } }}>
            {quote.nexoraSerial && (
              <Chip label={`Nexora Serial: ${quote.nexoraSerial}`} variant="outlined" size="small" sx={{ fontWeight: 900, fontFamily: 'monospace', maxWidth: '100%' }} />
            )}
          </Grid>
        </Grid>
      </Paper>

      {/* Everything that stops this quote, said once, in full, each with its own way to act on it.
          This list used to be split between grey captions under the Send button, three banners and a
          warning inside the totals card, and the same fact was worded differently in each. */}
      {(showBlockerPanel || nextStepText) && (
        <NextStepPanel
          tone={showPreSendGates && (revisionImpactPresentation || supplierValidityWarnings.length > 0) ? 'error' : showBlockerPanel ? 'warning' : primaryAction === 'send' ? 'success' : 'info'}
          title={showBlockerPanel ? (isDraftQuote ? 'Before this quote can be sent' : 'Needs attention') : 'Next step'}
          sentence={nextStepText ?? blockerRows[0]?.text ?? ''}
          action={panelAction}
          testId="quote-next-step"
        >
          {isUnpricedDraft && (
            <Typography variant="body2" sx={{ mb: 1 }}>Pricing Pending · Inventory Pending · Lead Time Pending · Tax, freight and commercial validity are not yet set.</Typography>
          )}
          <Stack component="ol" spacing={1} sx={{ m: 0, pl: 2.5 }}>
            {showPreSendGates && revisionImpactPresentation && (
              <Stack component="li" direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ alignItems: { sm: 'center' }, justifyContent: 'space-between' }}>
                <Box>
                  <Typography sx={{ fontWeight: 800 }}>{revisionImpactPresentation.title}</Typography>
                  <Typography variant="body2">{revisionImpactPresentation.detail}</Typography>
                </Box>
                {hasPermission('Quotations', 'edit') && (
                  <Button
                    color="inherit"
                    size="small"
                    variant="outlined"
                    disabled={resolveImpactMutation.isPending}
                    onClick={() => resolveImpactMutation.mutate()}
                    sx={{ whiteSpace: 'nowrap' }}
                  >
                    {revisionImpactPresentation.action}
                  </Button>
                )}
              </Stack>
            )}
            {/* Every blocker is listed, not just the first. A rep who fixes one, comes back,
                and is stopped by the next has made a round trip for nothing — and an
                incomplete draft usually has more than one thing missing. */}
            {blockerRows.map((reason) => (
              <Typography key={reason.key} component="li" variant="body2">
                {reason.text}
                {reason.link && (
                  <>
                    {' '}
                    <Link component={RouterLink} to={reason.link.to} underline="hover" sx={{ fontWeight: 700 }}>
                      {reason.link.label}
                    </Link>
                  </>
                )}
              </Typography>
            ))}
            {showPreSendGates && supplierValidityWarnings.length > 0 && (
              <Stack component="li" direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ alignItems: { sm: 'center' }, justifyContent: 'space-between' }}>
                <Box>
                  <Typography sx={{ fontWeight: 800 }}>Supplier validity does not support this Customer Quote</Typography>
                  <Typography variant="body2">{supplierValidityWarnings.length} priced line{supplierValidityWarnings.length === 1 ? '' : 's'} use an expired, unstated, or shorter Supplier Quote validity. Review the source offer before sending.</Typography>
                </Box>
                {quote.rfqId && <Button color="inherit" size="small" variant="outlined" startIcon={<OutcomeIcon />} onClick={() => navigate(`/procurement/rfqs/${quote.rfqId}/sourcing`)} sx={{ whiteSpace: 'nowrap' }}>Sourcing & offers</Button>}
              </Stack>
            )}
          </Stack>
        </NextStepPanel>
      )}

      {holdInfo !== null && (
        <Alert
          severity="info"
          onClose={() => setHoldInfo(null)}
          action={
            <Button color="inherit" size="small" sx={{ fontWeight: 800 }} onClick={() => navigate('/copilot/approvals')}>
              Open Approvals
            </Button>
          }
          sx={{ mb: 3, borderRadius: 2, fontWeight: 600 }}
        >
          Sent for approval — pricing is below your floor. Track it in Approvals.
          {holdInfo ? ` (${holdInfo})` : ''}
        </Alert>
      )}

      <Stack spacing={3}>
        {/* The work: the lines and, directly under them, the totals they add up to. */}
        <Paper sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', maxWidth: '100%', overflow: 'hidden' }}>
          <Box sx={{ p: 2, borderBottom: '1px solid', borderColor: 'divider', bgcolor: 'action.hover' }}><Typography variant="h6" sx={{ fontWeight: 800 }}>Quoted Items</Typography></Box>
          <Box sx={{ overflowX: 'auto' }}>
            <Table size="small">
              <TableHead><TableRow sx={{ bgcolor: 'action.hover' }}><TableCell sx={{ fontWeight: 800 }}>Ref</TableCell><TableCell sx={{ fontWeight: 800 }}>Description</TableCell><TableCell sx={{ fontWeight: 800 }} align="center">Qty</TableCell><TableCell sx={{ fontWeight: 800 }}>UOM</TableCell><TableCell sx={{ fontWeight: 800 }}>Cost source</TableCell><TableCell sx={{ fontWeight: 800 }} align="right">Unit Price</TableCell><TableCell sx={{ fontWeight: 800 }} align="right">Discount</TableCell><TableCell sx={{ fontWeight: 800 }} align="right">Total</TableCell></TableRow></TableHead>
              <TableBody>
                {quote.quoteItems.map((item, idx) => (
                  <TableRow key={item.id} hover>
                    {/* The buyer's own line reference (their RFQ line, e.g. SAP "00010"); synthetic index only for legacy lines */}
                    <TableCell>{item.customerLineRef || idx + 1}</TableCell>
                    <TableCell><Typography sx={{ fontWeight: 700, fontSize: '0.85rem' }}>{item.productName || 'Item'}</Typography><Typography variant="caption" color="text.secondary">{item.itemDescription}</Typography></TableCell>
                    <TableCell align="center">{item.quantity}</TableCell>
                    <TableCell>{item.unitOfMeasure || '—'}</TableCell>
                    <TableCell>
                      {(() => {
                        const source = sourceFor(item);
                        return <Stack spacing={0.5} sx={{ alignItems: 'flex-start' }}>
                          <Chip size="small" color={source.source === 'COST_SOURCE_PENDING' ? 'warning' : 'info'} label={statusLabel(source.source)} />
                          {source.offer && <Typography variant="caption" color="text.secondary">{source.offer.supplierName} · {source.offer.quoteReference || 'No supplier reference'} · valid {source.offer.validUntil ? dayjs(source.offer.validUntil).format('DD MMM YYYY') : 'not stated'}</Typography>}
                          {quote.rfqId && source.rfqItemId > 0 && <Button size="small" sx={{ px: 0 }} onClick={() => navigate(`/procurement/rfqs/${quote.rfqId}/sourcing`)}>View cost evidence</Button>}
                        </Stack>;
                      })()}
                    </TableCell>
                    <TableCell align="right">{Number(item.unitPrice || 0) === 0 ? <Chip size="small" label="Pricing Pending" color="warning" variant="outlined" /> : formatMoney(item.unitPrice, quote.currencyCode)}</TableCell>
                    <TableCell align="right">
                      {(item.discount ?? 0) > 0 ? (
                        <Typography variant="caption" color="error.main" sx={{ fontWeight: 700 }}>
                          - {formatMoney(item.discount, quote.currencyCode)}
                          <br />
                          ({item.discountTypeName})
                        </Typography>
                      ) : '-'}
                    </TableCell>
                    <TableCell align="right" sx={{ fontWeight: 700 }}>{isUnpricedDraft ? 'Pricing Pending' : formatMoney(item.totalAmount, quote.currencyCode)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Box>
          <Box sx={{ p: 2, borderTop: '1px solid', borderColor: 'divider', bgcolor: 'action.hover', display: 'flex', justifyContent: 'flex-end' }}>
            <Card className="tabular-nums" sx={{ borderRadius: 2, border: '1px solid', borderColor: 'primary.main', boxShadow: 'none', width: '100%', maxWidth: 440 }}>
              <CardContent sx={{ p: 3 }}>
                <Typography variant="h6" sx={{ fontWeight: 800, mb: 3 }}>Financial Summary</Typography>
                <Stack spacing={2}>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2 }}><Typography color="text.secondary">Gross Subtotal</Typography><Typography sx={{ fontWeight: 700 }}>{isUnpricedDraft ? 'Pricing Pending' : formatMoney(itemsSubtotal, quote.currencyCode)}</Typography></Box>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2 }}><Typography color="text.secondary">Item Discounts</Typography><Typography sx={{ fontWeight: 700, color: 'error.main' }}>{isUnpricedDraft ? 'Pending' : `- ${formatMoney(itemsDiscounts, quote.currencyCode)}`}</Typography></Box>
                  {headerDiscount > 0 && <Box sx={{ display: 'flex', justifyContent: 'space-between' }}><Typography color="text.secondary">Header Discount</Typography><Typography sx={{ fontWeight: 700, color: 'error.main' }}>- {formatMoney(headerDiscount, quote.currencyCode)}</Typography></Box>}
                  {/* The two rows the panel had no way to show, and without which the numbers on it
                      could not be added up: the base the tax is charged on, and the tax. Same three
                      lines, same order, as the printed quote. */}
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2 }}><Typography color="text.secondary">Total excluding VAT</Typography><Typography sx={{ fontWeight: 700 }}>{isUnpricedDraft ? 'Pending' : totals.hasUnderivedTax ? '—' : formatMoney(totals.netExcludingTax, quote.currencyCode)}</Typography></Box>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2 }}>
                    <Typography color={totals.hasUnderivedTax ? 'warning.main' : 'text.secondary'}>{vatLabel}</Typography>
                    <Typography sx={{ fontWeight: 700 }} color={totals.hasUnderivedTax ? 'warning.main' : undefined}>
                      {totals.hasUnderivedTax ? 'Not derived' : formatMoney(totals.totalTax, quote.currencyCode)}
                    </Typography>
                  </Box>
                  {totals.hasUnderivedTax && <Alert severity="warning" sx={{ py: 0 }}>No output tax rate is configured, so this quote cannot be sent.</Alert>}
                  <Divider />
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2 }}><Typography variant="h5" sx={{ fontWeight: 900 }}>Grand Total</Typography><Typography variant="h5" sx={{ fontWeight: 900, color: isUnpricedDraft ? 'warning.main' : 'primary.main' }}>{isUnpricedDraft ? 'Pricing Pending' : formatMoney(quote.totalAmount, quote.currencyCode)}</Typography></Box>
                </Stack>
              </CardContent>
            </Card>
          </Box>
        </Paper>

        {quote.headerRemarks && <Box sx={{ p: 2, bgcolor: 'action.hover', borderRadius: 1, borderLeft: '4px solid', borderColor: 'primary.main' }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 800 }}>REMARKS</Typography><Typography variant="body2">{quote.headerRemarks}</Typography></Box>}

        {/* Evidence and record, folded. Nothing is removed; it is simply not in the rep's way. */}
        <Accordion variant="outlined" disableGutters sx={{ borderRadius: 3, '&::before': { display: 'none' } }}>
          <AccordionSummary expandIcon={<ExpandMoreIcon />}>
            <Box>
              <Typography sx={{ fontWeight: 900 }}>Where the prices come from</Typography>
              <Typography variant="body2" color="text.secondary">Stock, shortage and supplier evidence behind each line.</Typography>
            </Box>
          </AccordionSummary>
          <AccordionDetails>
            <Stack spacing={2} sx={{ alignItems: 'flex-start' }}>
              <Box sx={{ width: '100%' }}><CommercialLineIntelligence stage="quote" recordId={quote.id} /></Box>
              {quote.rfqId && <Button variant="outlined" startIcon={<OutcomeIcon />} onClick={() => navigate(`/procurement/rfqs/${quote.rfqId}/sourcing`)}>Sourcing & offers</Button>}
            </Stack>
          </AccordionDetails>
        </Accordion>

        <Accordion variant="outlined" disableGutters sx={{ borderRadius: 3, '&::before': { display: 'none' } }}>
          <AccordionSummary expandIcon={<ExpandMoreIcon />}>
            <Box>
              <Typography sx={{ fontWeight: 900 }}>Quote record and lineage</Typography>
              <Typography variant="body2" color="text.secondary">Created on {dayjs(quote.createdDate).format('DD MMM YYYY')} by {quote.createdBy} · from {quote.rfqNo ? `RFQ ${quote.rfqNo}` : 'no RFQ'}</Typography>
            </Box>
          </AccordionSummary>
          <AccordionDetails>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, sm: 6, md: 3 }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Source RFQ</Typography><Button size="small" sx={{ px: 0 }} onClick={() => quote.rfqId && navigate(`/procurement/rfqs/view/${quote.rfqId}`)}>{quote.rfqNo || 'None'}</Button></Grid>
              <Grid size={{ xs: 12, sm: 6, md: 3 }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Opportunity Owner</Typography><Typography sx={{ fontWeight: 700 }}>{quote.createdBy}</Typography></Grid>
              <Grid size={{ xs: 12, sm: 6, md: 3 }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Lineage</Typography><Typography sx={{ fontWeight: 700, color: quote.nexoraSerial ? 'text.primary' : 'warning.main' }}>{quote.nexoraSerial || 'Not linked to a commercial case'}</Typography></Grid>
              <Grid size={{ xs: 12, sm: 6, md: 3 }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Source Revisions</Typography><Typography sx={{ fontWeight: 700 }}>{quote.sourceLeadRevision > 0 && quote.sourceRfqRevision > 0 ? `Lead Rev ${quote.sourceLeadRevision} · RFQ Rev ${quote.sourceRfqRevision}` : 'Legacy source revision unverified'}</Typography></Grid>
              {(quote.discountValue || 0) > 0 && (
                <Grid size={{ xs: 12, sm: 6, md: 3 }}>
                  <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, textTransform: 'uppercase' }}>Header Discount</Typography>
                  <Typography sx={{ fontWeight: 700, color: 'error.main' }}>
                    {quote.discountTypeName}: {quote.discountValue}
                  </Typography>
                </Grid>
              )}
              <Grid size={{ xs: 12 }}>
                <Stack direction="row" useFlexGap spacing={1} sx={{ flexWrap: 'wrap' }}>
                  {quote.rfqId && <Button size="small" variant="outlined" onClick={() => navigate(`/procurement/rfqs/view/${quote.rfqId}`)}>Open Source RFQ</Button>}
                  {quote.leadId && <Button size="small" variant="outlined" onClick={() => navigate(`/procurement/leads/view/${quote.leadId}`)}>Open Canonical Lead</Button>}
                </Stack>
              </Grid>
            </Grid>
          </AccordionDetails>
        </Accordion>
      </Stack>

      <QuoteOutcomeDialog
        open={outcomeOpen}
        onClose={() => setOutcomeOpen(false)}
        quoteId={Number(id)}
        quoteNo={quote.quoteNo}
        invalidateKeys={[['quote-detail', id], ['quotes']]}
      />

      <FollowUpDialog
        open={followUpOpen}
        onClose={() => setFollowUpOpen(false)}
        quoteId={Number(id)}
        quoteNo={quote.quoteNo}
      />

      <ExtendValidityDialog
        open={extendValidityOpen}
        onClose={() => setExtendValidityOpen(false)}
        quoteId={Number(id)}
        quoteNo={quote.quoteNo}
        currentValidUntil={quote.validUntil}
        invalidateKeys={[['quote-detail', id], ['quotes']]}
      />

      <CustomerAwardDialog
        open={awardOpen}
        quote={awardQuote}
        onClose={() => setAwardOpen(false)}
        onCompleted={(result) => {
          setAwardOpen(false);
          if (result.order) navigate(`/sales/orders/${result.order.id}`);
        }}
      />

      <EmailPromptDialog
        open={emailOpen}
        title={`Email quote ${quote.quoteNo}`}
        initialEmail={quote.customerEmail || ''}
        loading={sendMutation.isPending}
        composerFields="recipient-only"
        confirmLabel="Send quote"
        businessUnitId={businessUnitId}
        customerId={quote.customerId ?? null}
        onCancel={() => setEmailOpen(false)}
        onConfirm={(email) => {
          // R5: choosing the recipient no longer sends. The prices are confirmed first.
          setPendingRecipient(email);
          setEmailOpen(false);
          setPriceConfirmOpen(true);
        }}
      />

      <PriceConfirmationDialog
        open={priceConfirmOpen}
        quoteId={Number(id)}
        quoteNo={quote.quoteNo}
        recipientEmail={pendingRecipient}
        submitting={confirmPriceMutation.isPending || sendMutation.isPending}
        onCancel={() => setPriceConfirmOpen(false)}
        onConfirm={(source, reference) => confirmPriceMutation.mutate({ source, reference })}
      />
    </Box>
  );
};

export default QuoteViewPage;
