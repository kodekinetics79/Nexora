import React from 'react';
import { useParams, useNavigate, useSearchParams, Link as RouterLink } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Grid, Stack, Button, Chip,
  Table, TableHead, TableRow, TableCell, TableBody,
  Divider, CircularProgress, Card, CardContent, Tooltip, Alert, Link,
  Accordion, AccordionSummary, AccordionDetails, Menu, MenuItem, ListItemIcon, ListItemText
} from '@mui/material';
import {
  Edit as EditIcon,
  PictureAsPdf as PdfIcon,
  Email as EmailIcon,
  Send as SendIcon,
  ShoppingCart as OrderIcon,
  ContentCopy as ReviseIcon,
  EventRepeat as ExtendValidityIcon,
  NotificationsActive as FollowUpIcon,
  ExpandMore as ExpandMoreIcon,
  Inventory2Outlined as SourcingIcon,
  FactCheckOutlined as UpdateStatusIcon,
  OpenInNew as OpenIcon,
} from '@mui/icons-material';
import quoteService, { describeQuoteSendOutcome, type PriceAttestationSource } from '../../../api/services/quoteService';
import UpdateQuoteStatusDialog from './UpdateQuoteStatusDialog';
import QuoteStatusChip from './QuoteStatusChip';
import { quoteCode, quoteStatusWords } from './quoteState';
import SendQuoteDialog from '../../Procurement/RFQs/SendQuoteDialog';
import { deadlineWords, DEADLINE_COLOR } from '../../../utils/deadline';
import { formatDateSafe } from '../../../utils/dates';
import ExtendValidityDialog from './ExtendValidityDialog';
import FollowUpDialog from './FollowUpDialog';
import PriceConfirmationDialog from './PriceConfirmationDialog';
import KeepAsQuotedDialog from './KeepAsQuotedDialog';
import QuoteTermsDialog from './QuoteTermsDialog';
import rfqService from '../../../api/services/rfqService';
import EmailPromptDialog from '../../../components/common/EmailPromptDialog';
import { CustomerAwardDialog, type CustomerAwardQuote } from './customer-awards';
import { useAuth } from '../../../context/AuthContext';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import { formatMoney } from '../../../utils/currency';
import { summariseStoredQuote } from './quoteTotals';
import { describeRevisionImpact } from './revisionImpactText';
import { buyerIdentityLine, buyerNotes, isUnpricedByChoice, lineTotalExVat, pricingStatusText } from './quoteLineText';
import { alpha } from '@mui/material/styles';
import dayjs from 'dayjs';
import { toast } from 'react-hot-toast';
import CommercialLineIntelligence from '../../../components/common/CommercialLineIntelligence';
import NextStepPanel from '../../../components/common/NextStepPanel';
import procurementService from '../../../api/services/procurementService';

/** The one blocker that the send flow itself resolves (see PriceConfirmationDialog). */
const isAttestationBlocker = (blocker: { code?: string | null }) =>
  (blocker.code || '').toUpperCase() === 'PRICE_ATTESTATION_REQUIRED';
/**
 * The readiness codes that mean "the send already happened; the system is finishing it"
 * (QuoteService.EvaluateSendReadinessAsync). Not a defect the rep can fix, so not listed as one.
 */
const DELIVERY_PENDING_CODES: readonly string[] = ['DELIVERY_IN_FLIGHT', 'DELIVERY_STATUS_PENDING'];
/**
 * The readiness codes that mean the delivery under THIS quote number ended for good: the server
 * will never send it again, and says to issue a revision (QuoteService send readiness).
 */
const DELIVERY_DEAD_CODES: readonly string[] = ['DELIVERY_FAILED', 'DELIVERY_OUTCOME_UNCERTAIN'];

/** Where a line's cost comes from, in the rep's words (was the raw code, title-cased). */
const COST_SOURCE_WORDS: Record<string, string> = {
  SELECTED_SUPPLIER_QUOTE: 'Supplier quote',
  INTERNAL_INVENTORY: 'From stock',
  MIXED_INVENTORY_AND_SUPPLIER: 'Stock + supplier',
  INCOMING_INVENTORY: 'Arriving stock',
  COST_SOURCE_PENDING: 'No cost yet',
};

/** Who to ask when this user's role cannot take the step the sentence names. */
const WHO_CAN = 'your sales manager';

/** The label + value pair of the facts row, the same look as the Decide page's. */
const Fact: React.FC<{ label: string; value: React.ReactNode; labelColor?: string; testId?: string }> = ({ label, value, labelColor = 'text.secondary', testId }) => (
  <Box sx={{ minWidth: 0 }} data-testid={testId}>
    <Typography variant="caption" sx={{ display: 'block', color: labelColor, letterSpacing: '.07em', textTransform: 'uppercase', fontWeight: 700, fontSize: '0.68rem', lineHeight: 1.5, mb: 0.25, whiteSpace: 'nowrap' }}>
      {label}
    </Typography>
    <Typography
      variant="body2"
      component="div"
      sx={{ fontWeight: 600, fontSize: '0.9rem', lineHeight: 1.45, minHeight: 30, display: 'flex', alignItems: 'center', flexWrap: 'wrap', columnGap: 0.75, fontVariantNumeric: 'tabular-nums', overflowWrap: 'anywhere', color: 'text.primary' }}
    >
      {value}
    </Typography>
  </Box>
);

const QuoteViewPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const { userData, hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const businessUnitId = userData?.businessUnitId || 0;
  const [moreAnchor, setMoreAnchor] = React.useState<null | HTMLElement>(null);
  const moreMenuId = React.useId();

  const { data: quote, isLoading, isError, error: loadError, refetch } = useQuery({
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

  // Export PDF is how a portal quote (SEC, Aramco) goes out: the rep uploads it to the buyer's
  // portal. It used to die on a toast whenever the prices were unconfirmed or the quote had no
  // validity date, with no way to fix either from here (pilot audit CP-04 / UX-06). Both refusals
  // now open the step that fixes them, and the download runs again once it is done.
  const [pdfConfirmOpen, setPdfConfirmOpen] = React.useState(false);
  const [termsOpen, setTermsOpen] = React.useState(false);
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
    onError: (error: unknown) => {
      const response = (error as { response?: { status?: number; data?: { priceAttestationRequired?: boolean; commercialReviewRequired?: boolean } } })?.response;
      if (response?.status === 409 && response.data?.priceAttestationRequired) {
        setPdfConfirmOpen(true);
        return;
      }
      if (response?.status === 409 && response.data?.commercialReviewRequired && (!quote?.validUntil || !quote?.currencyId)) {
        setTermsOpen(true);
        return;
      }
      toast.error(presentableErrorMessage(error, 'The quote PDF could not be exported.'), { duration: 6000 });
    }
  });
  const confirmPriceForPdfMutation = useMutation({
    mutationFn: ({ source, reference }: { source: PriceAttestationSource; reference: string }) =>
      quoteService.confirmPriceAttestation(Number(id), source, reference),
    onSuccess: () => {
      setPdfConfirmOpen(false);
      queryClient.invalidateQueries({ queryKey: ['quote-price-attestation', Number(id)] });
      queryClient.invalidateQueries({ queryKey: ['quote-send-readiness', id] });
      pdfMutation.mutate();
    },
    onError: (error) => toast.error(presentableErrorMessage(error, 'The price confirmation could not be recorded.'), { duration: 6000 })
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
  const sendableStatus = quote ? ['DRAFT', 'SENT'].includes(quoteCode(quote)) : false;
  const readinessQuery = useQuery({
    queryKey: ['quote-send-readiness', id],
    queryFn: () => quoteService.getSendReadiness(Number(id)),
    enabled: !!id && sendableStatus
  });
  const sendReadiness = readinessQuery.data;
  // The send check has not answered yet. Until it does, nothing is known about what blocks
  // sending, so the page says it is checking and promotes no button (the client heuristics
  // alone used to say "nothing is blocking", then flip when the blockers arrived).
  const readinessChecking = sendableStatus && readinessQuery.isPending;

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
  // A quote that came from an RFQ goes out through the same send window the RFQ page uses: email,
  // or Download PDF and "Mark as submitted" for a customer who takes quotes on their own portal
  // (SEC, Aramco). A portal quote used to stay Draft for ever when it went out from this page.
  const [sendWindowOpen, setSendWindowOpen] = React.useState(false);
  const useSendWindow = Boolean(quote?.rfqId) && hasPermission('RFQ Management', 'view');
  const openSendWindow = () => (useSendWindow ? setSendWindowOpen(true) : setEmailOpen(true));
  // R7: extending the validity of a quote that is already with the customer.
  const [extendValidityOpen, setExtendValidityOpen] = React.useState(false);
  const [followUpOpen, setFollowUpOpen] = React.useState(false);
  const [priceConfirmOpen, setPriceConfirmOpen] = React.useState(false);
  // The recipient AND the words. The rep reviews and may edit the subject and message in the
  // send dialog (owner ask 2026-09-15); both travel with the send once the prices are confirmed.
  const [pendingSend, setPendingSend] = React.useState<{ recipientEmail: string; subject?: string; body?: string }>({ recipientEmail: '' });
  const [holdInfo, setHoldInfo] = React.useState<string | null>(null);
  // The default covering e-mail the server would send, fetched only while the dialog is open so
  // the rep sees — and can change — exactly what the customer will read.
  const emailDraftQuery = useQuery({
    queryKey: ['quote-email-draft', id],
    queryFn: () => quoteService.getEmailDraft(Number(id)),
    enabled: !!id && emailOpen,
    staleTime: 5 * 60 * 1000,
  });
  const sendMutation = useMutation({
    mutationFn: (send: { recipientEmail: string; subject?: string; body?: string }) =>
      send.subject !== undefined || send.body !== undefined
        ? quoteService.sendEmail(Number(id), send.recipientEmail, { subject: send.subject, body: send.body })
        : quoteService.sendEmail(Number(id), send.recipientEmail),
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
      sendMutation.mutate(pendingSend);
    },
    onError: (error: any) => {
      const message = error?.response?.data?.message || 'The price confirmation could not be recorded.';
      toast.error(message, { duration: 6000 });
    }
  });

  // WP-A4: "customer replied", the client's own step, and how it ended, in one window
  // (it replaced the separate "Customer responded" and "Record outcome" buttons).
  const [updateStatusOpen, setUpdateStatusOpen] = React.useState(false);

  // Both of these END a customer-revision review, and both must leave the screen agreeing with
  // itself before the rep is told anything. The readiness list is served by its own query; when
  // only the quote was invalidated, the panel item vanished while the list below it still said
  // "This quote is stale…" until a manual reload. The invalidations are awaited, so the toast is
  // the last thing to happen, not the first.
  const refreshAfterRevisionReview = () => Promise.all([
    queryClient.invalidateQueries({ queryKey: ['quote-detail', id] }),
    queryClient.invalidateQueries({ queryKey: ['quote-send-readiness', id] }),
    queryClient.invalidateQueries({ queryKey: ['quote-price-attestation', Number(id)] }),
  ]);
  // D-04: keeping the old quantities needs a reason. 'send' = the rep pressed Send with the
  // buyer's newer version still open: the keep is recorded, then the send goes on.
  const [keepDialog, setKeepDialog] = React.useState<null | 'keep' | 'send'>(null);
  const resolveImpactMutation = useMutation({
    mutationFn: ({ reason }: { reason: string; thenSend?: boolean }) => quoteService.resolveRevisionImpact(Number(id), reason),
    onSuccess: async (_, { thenSend }) => {
      setKeepDialog(null);
      await refreshAfterRevisionReview();
      if (thenSend) {
        openSendWindow();
        return;
      }
      toast.success('Kept as quoted. The reason is recorded.');
    },
    onError: (error) => toast.error(presentableErrorMessage(error, 'The revision review could not be completed'), { duration: 8000 })
  });
  const applyImpactMutation = useMutation({
    mutationFn: () => quoteService.applyRevisionQuantities(Number(id)),
    onSuccess: async (result) => {
      setKeepDialog(null);
      await refreshAfterRevisionReview();
      const updated = result.linesUpdated === 1 ? '1 line' : `${result.linesUpdated} lines`;
      const notOnQuote = result.linesNotOnQuote.length
        ? ` Line${result.linesNotOnQuote.length === 1 ? '' : 's'} ${result.linesNotOnQuote.join(', ')} ${result.linesNotOnQuote.length === 1 ? 'is' : 'are'} new on the customer's document and not on this quote — add ${result.linesNotOnQuote.length === 1 ? 'it' : 'them'} if you are quoting ${result.linesNotOnQuote.length === 1 ? 'it' : 'them'}.`
        : '';
      const rfq = result.rfqLinesUpdated?.length ? ' The RFQ lines were updated too.' : '';
      const suppliers = result.outdatedSupplierRequests?.length
        ? ` ${result.outdatedSupplierRequests.length} supplier request${result.outdatedSupplierRequests.length === 1 ? '' : 's'} asked for the old quantity: ask again from Sourcing.`
        : '';
      toast.success(`New quantities applied to ${updated} and the quote re-totalled.${rfq}${notOnQuote}${suppliers}`, { duration: notOnQuote || suppliers ? 10000 : 5000 });
    },
    onError: (error) => toast.error(presentableErrorMessage(error, 'The new quantities could not be applied'), { duration: 8000 })
  });

  // VALIDITY_BELOW_BUYER_MINIMUM: one click sets the date the buyer asks for.
  const setValidityMutation = useMutation({
    mutationFn: (validUntil: string) => rfqService.saveQuoteTerms(Number(id), { validUntil }),
    onSuccess: async () => {
      await refreshAfterRevisionReview();
      toast.success('Validity updated to what the buyer asks for.');
    },
    onError: (error) => toast.error(presentableErrorMessage(error, 'The validity date could not be changed'), { duration: 8000 })
  });

  const [awardOpen, setAwardOpen] = React.useState(false);

  // "Enter PO" on the Quotes list lands here with ?action=po: open the PO window once, then drop
  // the flag so a reload or Back does not open it again.
  const poAsked = searchParams.get('action') === 'po';
  React.useEffect(() => {
    if (!poAsked || !quote) return;
    const rest = new URLSearchParams(searchParams);
    rest.delete('action');
    setSearchParams(rest, { replace: true });
    if (quoteCode(quote) === 'ACCEPTED' && hasPermission('Orders', 'create')
      && quote.commercialCaseId && quote.customerId && quote.currencyId) {
      setAwardOpen(true);
    }
  }, [poAsked, quote, searchParams, setSearchParams, hasPermission]);

  if (isLoading) return <Box sx={{ p: 4, display: 'flex', justifyContent: 'center' }}><CircularProgress /></Box>;
  const notFound = !quote && (!isError || (loadError as { response?: { status?: number } } | null)?.response?.status === 404);
  if (notFound) {
    return (
      <Box sx={{ p: 4 }}>
        <Alert severity="info" action={<Button color="inherit" onClick={() => navigate('/sales/quotes')}>Back to Quotes</Button>}>
          This quote doesn't exist or isn't yours to see.
        </Alert>
      </Box>
    );
  }
  if (isError || !quote) return <Box sx={{ p: 4 }}><Alert severity="error" action={<Button color="inherit" onClick={() => refetch()}>Try again</Button>}>We couldn't load this quote.</Alert></Box>;

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
  // Every state test reads the status CODE (quoteState.quoteCode), never the display label: a
  // label can be renamed in Setup, and "Sent" renamed used to make every Sent control vanish.
  const code = quoteCode(quote);
  const isDraftQuote = code === 'DRAFT';
  const isSentQuote = code === 'SENT';
  const isWonQuote = code === 'ACCEPTED';
  const isOrderedQuote = code === 'ORDERED';
  const isClosedQuote = code === 'REJECTED' || code === 'EXPIRED' || (Boolean(quote.outcomeOn) && !isWonQuote && !isOrderedQuote);
  const isUnpricedDraft = isDraftQuote
    && !quote.currencyId
    && quote.quoteItems.every((item) => Number(item.unitPrice || 0) === 0);
  const canEditQuotes = hasPermission('Quotations', 'edit');
  const canEnterPo = hasPermission('Orders', 'create');
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
  // The price-source confirmation (R5) is satisfied INSIDE the send flow: Send opens the recipient
  // dialog, then the price-confirmation dialog, then the quote goes. So that blocker must never
  // disable Send — with it as the last item, a fully priced, dated and formatted quote could not
  // be sent from its own screen at all (found driving the journey on 2026-09-15). It stays in the
  // list below, worded as the step Send will take, and every other blocker still gates.
  const gatingBlockers = (sendReadiness?.blockers ?? []).filter((blocker) => !isAttestationBlocker(blocker));
  const attestationPending = (sendReadiness?.blockers ?? []).some(isAttestationBlocker);
  const serverBlocker = gatingBlockers[0];
  // A send that already happened is not something to fix. When the readiness list's reason is
  // the queued (or delivered-but-status-pending) delivery, the panel says who it went to and
  // when, and offers nothing — the header used to say "queued" while the panel said "Fix the
  // item below, then send the quote."
  const deliveryPending = sendReadiness?.blockers?.find((b) => DELIVERY_PENDING_CODES.includes(b.code)) ?? null;
  const deliveryHandedOver = sendReadiness?.deliveryRequestedOn
    ? dayjs(/Z|[+-]\d\d:\d\d$/.test(sendReadiness.deliveryRequestedOn) ? sendReadiness.deliveryRequestedOn : `${sendReadiness.deliveryRequestedOn}Z`)
    : null;
  const deliveryPendingText = deliveryPending
    ? `Sent to ${sendReadiness?.deliveryRecipient || 'the customer'} at ${deliveryHandedOver?.isValid() ? deliveryHandedOver.format('HH:mm on D MMM') : 'the last send'}. Being delivered — nothing to do.`
    : null;
  const sendBlockedReason: { text: string; link?: { label: string; to: string } } | null = serverBlocker
    ? {
        text: serverBlocker.message,
        link: serverBlocker.setupPath && serverBlocker.setupLabel
          ? { label: `Open ${serverBlocker.setupLabel}`, to: serverBlocker.setupPath }
          : undefined,
      }
    : sendReadiness && (sendReadiness.canSend || (gatingBlockers.length === 0 && attestationPending))
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
  // The buyer's newer version, whether or not an impact row exists (D-01: a quote built after the
  // revision arrived had none, and sent with the old quantities without a word).
  const revisionWarning = (sendReadiness?.warnings ?? []).find((w) => w.code === 'BUYER_REVISION_NEWER') ?? null;
  const otherWarnings = (sendReadiness?.warnings ?? []).filter((w) => w.code !== 'BUYER_REVISION_NEWER');
  const revisionImpactPresentation = quote.revisionImpact === 'INVENTORY_REVALIDATION_REQUIRED'
    ? {
        title: 'Inventory Revalidation Required',
        detail: 'Stock changed after this Quote Draft was prepared. Revalidate inventory before sending it to the customer.',
        action: 'Mark revalidation complete',
        hasQuantityChanges: false,
      }
    : quote.revisionImpact || revisionWarning
      ? {
          // Says which revision ARRIVED, which one the draft was built on, and what changed —
          // from the server's projection of the identity spine's own diff. It used to print the
          // built-from revision as the thing to review against, and nothing about the change.
          ...describeRevisionImpact(quote.revisionImpactDetail ?? revisionWarning?.revision, quote.sourceLeadRevision, isDraftQuote),
          action: 'Keep as quoted',
        }
      : null;
  // A draft can take the customer's new quantities in place. A quote already with the customer
  // cannot — it is revised — so there the only in-panel move is to record the review.
  const isCustomerRevision = (Boolean(quote.revisionImpact) && quote.revisionImpact !== 'INVENTORY_REVALIDATION_REQUIRED')
    || Boolean(revisionWarning);
  const canApplyRevision = isCustomerRevision && isDraftQuote
    && (revisionWarning
      ? revisionWarning.canApply === true
      : quote.revisionImpactDetail == null || revisionImpactPresentation?.hasQuantityChanges === true);
  // Send with the buyer's newer version still open asks first: keep (with a reason) and send, or
  // use the new quantities. Never a dead end, never silent.
  // A draft goes out through the send window (email or the buyer's portal); "Send again" on a
  // quote already with the customer keeps the email chain.
  const startSend = () => (isCustomerRevision && isDraftQuote ? setKeepDialog('send') : isDraftQuote ? openSendWindow() : setEmailOpen(true));
  const keepAsQuoted = () => (quote.revisionImpact === 'INVENTORY_REVALIDATION_REQUIRED'
    ? resolveImpactMutation.mutate({ reason: 'Inventory revalidated' })
    : setKeepDialog('keep'));

  // A newer revision replaces this quote. The revision chain says so for ANY revision (even an
  // unsent draft, which is what the outcome and award services use); the list's field is the
  // fallback while the chain is loading.
  const supersededByNo = revisionInfo?.supersededByQuoteNo ?? quote.supersededByQuoteNo ?? null;
  const supersededById = revisionInfo?.supersededByQuoteId ?? null;
  const isSuperseded = Boolean(supersededByNo);
  // A quote made outside Nexora and uploaded: the customer holds the rep's own file, so Nexora
  // neither sends nor revises it, and has no send checks to show for it. It downloads that file
  // and records what happens next.
  const uploadedFileName = quote.uploadedFileName || null;
  // The delivery under this number ended for good; the server says to issue a revision.
  const deliveryDead = isDraftQuote
    ? (sendReadiness?.blockers?.find((b) => DELIVERY_DEAD_CODES.includes(b.code)) ?? null)
    : null;
  // Revising is offered only where the server allows it: a sent quote whose chain is still open,
  // or a draft whose delivery ended for good (QuoteService revision info).
  const canRevise = canEditQuotes && Boolean(revisionInfo?.canRevise) && !uploadedFileName && !isSuperseded;
  // A quote made outside Nexora, or with no RFQ, has no Nexora cost behind it: no column for it.
  const showCostColumn = !uploadedFileName && Boolean(quote.rfqId);
  // The one status the header shows: the list's words, and "Replaced" wins over Stale.
  const chipQuote = { ...quote, supersededByQuoteNo: supersededByNo };
  const statusWords = quoteStatusWords(chipQuote);

  // Which control is THE next step. Exactly one contained button per state; a contained button
  // that is also disabled points the rep at a dead end, so a blocked draft promotes Edit instead.
  // 'revision': a customer revision is open on a draft. The decision — apply or keep — lives in
  // the panel's list item, and it is THE next step, so Edit steps back to outlined for that state.
  type PrimaryAction = 'send' | 'edit' | 'update' | 'po' | 'pdf' | 'revision' | 'revise' | 'open-newer' | null;
  const primaryAction: PrimaryAction =
    isSuperseded ? (supersededById ? 'open-newer' : null)
      : isOrderedQuote ? 'pdf'
        : isWonQuote ? 'po'
          : isSentQuote ? (isCustomerRevision && canRevise ? 'revise' : 'update')
            : isDraftQuote && (readinessChecking || deliveryPending) ? null
              : isDraftQuote && deliveryDead ? 'revise'
                : isDraftQuote ? (isCustomerRevision ? 'revision' : sendBlockedReason === null ? 'send' : 'edit')
                  : null;
  // What is actually beside the sentence. A delivery in flight says "nothing to do" and offers nothing.
  const panelKey: PrimaryAction = deliveryPendingText && (isDraftQuote || isSentQuote) ? null : primaryAction;
  // The send checks are about sending, so they are listed on a draft only. On a sent quote they
  // only mattered to "Send again", which says its own reason.
  const listedBlockers = !isDraftQuote || uploadedFileName ? [] : (sendReadiness?.blockers ?? []).filter((blocker) => blocker !== deliveryPending);
  const blockerRows = !isDraftQuote || uploadedFileName ? [] : listedBlockers.length
    ? listedBlockers.map((blocker) => ({
        key: blocker.code,
        text: isAttestationBlocker(blocker) && gatingBlockers.length === 0
          ? 'Confirm where the prices came from — your sales manager, or a supplier quote. Press Send to customer; you confirm it there, then the quote goes.'
          : blocker.message,
        link: blocker.setupPath && blocker.setupLabel
          ? { label: `Open ${blocker.setupLabel}`, to: blocker.setupPath }
          : undefined,
      }))
    : sendBlockedReason && !deliveryPending ? [{ key: 'client', ...sendBlockedReason }] : [];
  // Pre-send gates only matter while the quote can still be sent; on an ordered, closed or
  // replaced quote they would argue with the sentence that says it is finished.
  // Nor while a delivery is in flight: the sentence says "nothing to do", so nothing is offered.
  const showPreSendGates = (isDraftQuote || isSentQuote) && !isSuperseded && !uploadedFileName && !deliveryPending;
  const showWarnings = isDraftQuote && otherWarnings.length > 0;
  const showBlockerPanel = blockerRows.length > 0 || showWarnings
    || (showPreSendGates && (supplierValidityWarnings.length > 0 || revisionImpactPresentation !== null));
  const blockerCount = blockerRows.length + (showPreSendGates && revisionImpactPresentation ? 1 : 0) + (showPreSendGates && supplierValidityWarnings.length > 0 ? 1 : 0)
    + (showWarnings ? otherWarnings.length : 0);
  // What must be fixed before sending: the blockers and an open customer revision. Warnings
  // (supplier validity, the buyer's asks) are listed too, but they never stop the send.
  const mustFixCount = blockerRows.length + (showPreSendGates && revisionImpactPresentation ? 1 : 0);
  // The readiness check failed: say so and offer to ask again (never read it as "nothing blocks").
  const readinessRetry = isDraftQuote && readinessQuery.isError;
  const items = (n: number) => (n === 1 ? 'item' : `${n} items`);
  const buyerRevisionNo = quote.revisionImpactDetail?.toRevision ?? revisionWarning?.revision?.toRevision ?? null;
  // The sentence that tells the rep what happens next, in every state, derived from facts the
  // page already holds. It names only controls this user has; otherwise it says who to ask.
  const sentSentence = !canEditQuotes
    ? `With the customer. Ask ${WHO_CAN} to update the status when they reply.`
    : isCustomerRevision
      ? `The buyer sent ${buyerRevisionNo ? `revision ${buyerRevisionNo}` : 'a newer version'} after this quote went out. ${canRevise ? 'Make a revision, or keep it as quoted.' : uploadedFileName ? 'Update the status when they decide.' : 'Keep it as quoted, or update the status.'}`
      : quote.subStatusKind === 'STEP' && quote.subStatusName
        ? `The customer is at ${quote.subStatusName}${quote.subStatusOn ? ` since ${formatDateSafe(quote.subStatusOn)}` : ''}. Update the status when they decide.`
        : quote.respondedOn
          ? `The customer replied on ${formatDateSafe(quote.respondedOn)}. Update the status when they decide: won, lost or expired.`
          : quote.isStale
            ? `No reply for ${quote.daysSinceSent ?? 'several'} days. Chase the customer, then update the status.`
            : 'Waiting for the customer. Update the status when they reply.';
  const draftSentence = !canEditQuotes
    ? `This quote is not sent yet. Ask ${WHO_CAN} to finish and send it.`
    : readinessChecking
      ? 'Checking what could block sending…'
      : deliveryDead
      ? "This quote can't be sent again under its number. Make a revision and send that."
      : isCustomerRevision
        ? (!quote.revisionImpact && revisionWarning
          ? `The buyer sent a newer version (rev ${revisionWarning.revision?.toRevision ?? '?'}). ${canApplyRevision ? 'Apply the new quantities, or keep it as quoted.' : 'Review what changed, then keep it as quoted or edit the lines.'}`
          : canApplyRevision
            ? `Revision ${quote.revisionImpactDetail?.toRevision ?? 'from the customer'} arrived after this draft. Apply the new quantities, or keep it as quoted.`
            : 'A customer revision arrived after this draft. Review what changed, then keep it as quoted or edit the lines.')
        : sendBlockedReason !== null
          ? `Fix the ${items(mustFixCount || 1)} below, then send the quote.`
          // A failed readiness check is "not known", never "nothing is blocking".
          : readinessQuery.isError
            ? "Couldn't check whether anything blocks sending. Try again, or press Send to customer and it is checked then."
            : blockerCount > 0
              ? `Nothing blocks sending, but check the ${items(blockerCount)} below first.`
              : useSendWindow
                ? "Prices are in and nothing is blocking. Send it by email, or download it for the buyer's portal."
                : 'Prices are in and nothing is blocking. Send this quote to the customer.';
  const nextStepText = isSuperseded
    ? `A newer revision replaces this quote. Work on ${supersededByNo} instead.`
    : deliveryPendingText && (isDraftQuote || isSentQuote)
      ? deliveryPendingText
      : isOrderedQuote
        ? `This quote became an order. ${uploadedFileName ? 'Download the quote' : 'Export the PDF'} if the customer needs a copy; nothing else is left to do here.`
        : isWonQuote
          ? (!canEnterPo
            ? `The customer accepted. Ask ${WHO_CAN} to enter their purchase order.`
            : awardQuote
              ? 'The customer accepted. Enter their purchase order to turn this quote into an order.'
              : 'The customer accepted, but this quote has no linked case, customer or currency, so the purchase order cannot be entered yet.')
          : isClosedQuote
            ? `Closed as ${statusWords.label}${quote.outcomeOn ? ` on ${dayjs(quote.outcomeOn).format('D MMM')}` : ''}. Nothing is left to do here.`
            : isSentQuote
              ? sentSentence
              : isDraftQuote
                ? draftSentence
                : 'This quote is closed. Nothing is left to do here.';
  const panelTitle = deliveryPendingText && (isDraftQuote || isSentQuote) ? 'Being delivered'
    : showBlockerPanel && isDraftQuote && !deliveryDead ? (sendBlockedReason !== null ? 'Before this quote can be sent' : 'Check before sending')
      : 'Next step';
  const panelTone: 'info' | 'warning' | 'error' | 'success' = deliveryPendingText && (isDraftQuote || isSentQuote) ? 'info'
    : showBlockerPanel
      ? (isDraftQuote && sendBlockedReason !== null && showPreSendGates && (revisionImpactPresentation || supplierValidityWarnings.length > 0) ? 'error' : 'warning')
      : isDraftQuote && deliveryDead ? 'warning'
        : panelKey === 'send' || isOrderedQuote || isWonQuote ? 'success'
          : 'info';

  const moreMenuOpen = Boolean(moreAnchor);
  const contained = (key: PrimaryAction) => panelKey === key;
  const sendControl = canEditQuotes && !isSuperseded && !uploadedFileName && isDraftQuote
    ? (
      <Tooltip
        describeChild
        title={deliveryPending ? 'Already handed to delivery — nothing to do.'
          : sendBlockedReason ? `${mustFixCount || 1} thing${(mustFixCount || 1) === 1 ? '' : 's'} must be fixed first — see the list below`
            : useSendWindow ? "Email it to the customer, or download it for the buyer's portal and record it as submitted."
              : 'Email the quote to the customer. You confirm where the prices came from first.'}
      >
        <Box
          component="span"
          role={sendBlockedReason ? 'button' : undefined}
          aria-disabled={sendBlockedReason ? 'true' : undefined}
          aria-label={sendBlockedReason ? `Sending is blocked: ${sendBlockedReason.text}` : undefined}
          tabIndex={sendBlockedReason ? 0 : -1}
          sx={{ display: 'inline-flex', borderRadius: 1, '&:focus-visible': { outline: '3px solid', outlineColor: 'primary.main', outlineOffset: 2 } }}
        >
          <Button
            variant={contained('send') ? 'contained' : 'outlined'}
            startIcon={<SendIcon />}
            disabled={sendBlockedReason !== null}
            title={sendBlockedReason?.text}
            onClick={startSend}
            sx={{ fontWeight: contained('send') ? 800 : undefined, whiteSpace: 'nowrap' }}
          >
            Send to customer
          </Button>
        </Box>
      </Tooltip>
    ) : null;
  // Edit only where the server accepts the save: a draft that is not being delivered and whose
  // delivery did not end for good. Every other quote refused the whole edit page on Save.
  const editControl = canEditQuotes && isDraftQuote && !deliveryPending && !deliveryDead && !uploadedFileName ? (
    <Tooltip title="Change prices, validity, discounts and remarks on this draft." describeChild>
      <Button
        variant={contained('edit') ? 'contained' : 'outlined'}
        startIcon={<EditIcon />}
        onClick={() => navigate(`/sales/quotes/edit/${id}`)}
        sx={{ fontWeight: contained('edit') ? 800 : undefined, whiteSpace: 'nowrap' }}
      >
        Edit
      </Button>
    </Tooltip>
  ) : null;
  // On a quote with the customer, the way to change it is a revision; it sits where Edit was.
  const reviseControl = canRevise ? (
    <Tooltip title="Start a new draft from this quote. The customer keeps this one until you send the new one." describeChild>
      <Button
        variant={contained('revise') ? 'contained' : 'outlined'}
        startIcon={reviseMutation.isPending ? <CircularProgress size={18} color="inherit" /> : <ReviseIcon />}
        disabled={reviseMutation.isPending}
        onClick={() => reviseMutation.mutate()}
        sx={{ fontWeight: contained('revise') ? 800 : undefined, whiteSpace: 'nowrap' }}
      >
        {reviseMutation.isPending ? 'Making a revision…' : 'Make a revision'}
      </Button>
    </Tooltip>
  ) : null;
  // One window for everything that happens while the quote is with the customer.
  const updateControl = canEditQuotes && isSentQuote && !isSuperseded ? (
    <Tooltip title="Customer replied, where they are in their process, or how it ended: won, lost or expired." describeChild>
      <Button
        variant={contained('update') ? 'contained' : 'outlined'}
        startIcon={<UpdateStatusIcon />}
        onClick={() => setUpdateStatusOpen(true)}
        sx={{ fontWeight: contained('update') ? 800 : undefined, whiteSpace: 'nowrap' }}
      >
        Update status
      </Button>
    </Tooltip>
  ) : null;
  const poControl = canEnterPo && isWonQuote ? (
    <Tooltip title={!awardQuote ? 'This quote needs a linked commercial case, a customer and a currency before a PO can be entered.' : "Enter the customer's purchase order; it becomes a sales order."} describeChild>
      <Box component="span" role={!awardQuote ? 'button' : undefined} aria-disabled={!awardQuote ? 'true' : undefined} aria-label={!awardQuote ? 'Entering a PO is blocked: this quote needs a linked commercial case, a customer and a currency.' : undefined} tabIndex={!awardQuote ? 0 : -1} sx={{ display: 'inline-flex', borderRadius: 1, '&:focus-visible': { outline: '3px solid', outlineColor: 'primary.main', outlineOffset: 2 } }}>
        <Button
          // Never contained and disabled: a PO that cannot be entered yet is an outlined button.
          variant={contained('po') && awardQuote ? 'contained' : 'outlined'}
          startIcon={<OrderIcon />}
          onClick={() => setAwardOpen(true)}
          disabled={!awardQuote}
          sx={{ fontWeight: 800, whiteSpace: 'nowrap' }}
        >
          Enter PO
        </Button>
      </Box>
    </Tooltip>
  ) : null;
  const pdfControl = uploadedFileName ? (
    <Tooltip title={`The file the customer received: ${uploadedFileName}`} describeChild>
      <Button
        variant={contained('pdf') ? 'contained' : 'outlined'}
        startIcon={<PdfIcon />}
        onClick={() => quoteService.downloadUploadedFile(Number(id), uploadedFileName)
          .catch((error) => toast.error(presentableErrorMessage(error, 'The quote file could not be downloaded.'), { duration: 6000 }))}
        sx={{ whiteSpace: 'nowrap' }}
      >
        Download quote
      </Button>
    </Tooltip>
  ) : (
    <Tooltip title={isUnpricedDraft ? 'Available once the lines are priced.' : 'Download the quotation the customer will receive.'} describeChild>
      <span>
        <Button
          variant={contained('pdf') ? 'contained' : 'outlined'}
          startIcon={<PdfIcon />}
          onClick={() => pdfMutation.mutate()}
          disabled={pdfMutation.isPending || isUnpricedDraft}
          sx={{ whiteSpace: 'nowrap', fontWeight: contained('pdf') ? 800 : undefined }}
        >
          Export PDF
        </Button>
      </span>
    </Tooltip>
  );
  const openNewerControl = supersededById ? (
    <Tooltip title="The revision that replaces this quote. The customer works from that one." describeChild>
      <Button
        variant={contained('open-newer') ? 'contained' : 'outlined'}
        startIcon={<OpenIcon />}
        onClick={() => navigate(`/sales/quotes/view/${supersededById}`)}
        sx={{ fontWeight: 800, whiteSpace: 'nowrap' }}
      >
        Open {supersededByNo}
      </Button>
    </Tooltip>
  ) : null;
  // The control that belongs beside the next-step sentence. The rail keeps the rest, and never
  // repeats the panel's control (one accessible name, once).
  const panelAction = panelKey === 'send' ? sendControl
    : panelKey === 'edit' ? editControl
      : panelKey === 'update' ? updateControl
        : panelKey === 'po' ? poControl
          : panelKey === 'pdf' ? pdfControl
            : panelKey === 'revise' ? reviseControl
              : panelKey === 'open-newer' ? openNewerControl
                : null;
  const sendAgainAllowed = canEditQuotes && isSentQuote && !isSuperseded && !uploadedFileName;
  const showMore = canEditQuotes;
  // The More tooltip names only what the menu actually holds in this state.
  const canExtendHere = Boolean(quote.canExtendValidity) && !isSuperseded;
  const moreItems = [
    'follow-up reminders',
    ...(canExtendHere ? ['extending validity'] : []),
    ...(sendAgainAllowed ? ['sending again'] : []),
    ...(quote.rfqId ? ["the RFQ's sourcing"] : []),
  ];
  const moreList = new Intl.ListFormat('en', { style: 'long', type: 'conjunction' }).format(moreItems);
  const moreTooltip = `${moreList.charAt(0).toUpperCase()}${moreList.slice(1)}.`;
  const refreshAfterSendWindow = () => {
    for (const key of [['quote-detail', id], ['quote-send-readiness', id], ['quote-revisions', id], ['quote-price-attestation', Number(id)], ['quotes']]) {
      queryClient.invalidateQueries({ queryKey: key });
    }
  };

  // The facts a rep opens a quote for, in the Decide page's strip.
  const validityDays = quote.validUntil ? deadlineWords(quote.validUntil) : null;
  const validityLive = (isDraftQuote || isSentQuote) && !isSuperseded;
  const validityDaysLeft = validityDays && validityDays.tone !== 'none'
    ? (() => {
        const text = validityDays.text;
        if (validityDays.tone === 'late') return 'Ended';
        if (text === 'Due today') return 'Ends today';
        if (text === 'Due tomorrow') return 'Ends tomorrow';
        return text;
      })()
    : null;
  const validityValue = quote.validUntil
    ? (
      <Tooltip title={quote.validityExtendedOn ? `Extended on ${formatDateSafe(quote.validityExtendedOn)}` : ''}>
        <span>
          {formatDateSafe(quote.validUntil)}
          {validityLive && validityDaysLeft && (
            <Box component="span" sx={{ ml: 0.75, fontSize: '0.78rem', fontWeight: 700, color: DEADLINE_COLOR[validityDays!.tone] }}>
              {validityDaysLeft}
            </Box>
          )}
          {quote.validityExtendedOn && (
            <Box component="span" sx={{ ml: 0.75, fontSize: '0.78rem', fontWeight: 700, color: 'text.secondary' }}>Extended</Box>
          )}
        </span>
      </Tooltip>
    )
    : isDraftQuote ? 'Not set' : 'Not stated';
  const sendTo = [quote.contactName, quote.customerEmail].filter(Boolean).join(' · ') || 'Not set';
  const linkSx = { fontWeight: 700, textAlign: 'left' } as const;
  const factsRow = (
    <Stack
      direction="row"
      spacing={{ xs: 1.5, sm: 1.25, xl: 2 }}
      useFlexGap
      divider={<Divider orientation="vertical" flexItem sx={{ display: { xs: 'none', sm: 'block' } }} />}
      sx={{ flexWrap: 'wrap', rowGap: 1, alignItems: 'flex-start', minWidth: 0, px: { xs: 1.5, sm: 2 }, py: 0.75, mb: 1.25, borderRadius: 2.5, border: 1, borderColor: 'divider', bgcolor: 'background.paper' }}
    >
      <Fact
        label="Quote no."
        value={(
          <>
            <span>{quote.quoteNo}</span>
            {revisionInfo && revisionInfo.revisionNo > 1 && revisionInfo.revisionOfQuoteNo && (
              revisionInfo.revisionOfQuoteId ? (
                <Link component="button" type="button" underline="hover" onClick={() => navigate(`/sales/quotes/view/${revisionInfo.revisionOfQuoteId}`)} sx={{ ...linkSx, fontSize: '0.78rem' }}>
                  Rev {revisionInfo.revisionNo} · replaces {revisionInfo.revisionOfQuoteNo}
                </Link>
              ) : (
                <Box component="span" sx={{ fontSize: '0.78rem', color: 'text.secondary' }}>Rev {revisionInfo.revisionNo} · replaces {revisionInfo.revisionOfQuoteNo}</Box>
              )
            )}
          </>
        )}
      />
      {(uploadedFileName || quote.externalQuoteReference) && (
        <Fact label="No. on the file" value={quote.externalQuoteReference || 'Not stated'} />
      )}
      <Fact label="Total" value={isUnpricedDraft ? 'Not priced' : formatMoney(quote.totalAmount, quote.currencyCode)} />
      <Fact label="Valid until" value={validityValue} />
      {isSentQuote && !isSuperseded && quote.subStatusKind === 'STEP' && quote.subStatusName && (
        <Fact
          label="Customer stage"
          testId="quote-customer-stage"
          value={`${quote.subStatusName}${quote.subStatusOn ? ` · since ${formatDateSafe(quote.subStatusOn)}` : ''}`}
        />
      )}
      <Fact label="Send to" value={sendTo} />
      <Fact label="Owner" value={quote.ownerName || 'Not set'} />
      <Fact
        label="Came from"
        value={(
          <>
            {quote.rfqId ? (
              <Link component="button" type="button" underline="hover" onClick={() => navigate(`/procurement/rfqs/view/${quote.rfqId}`)} sx={linkSx}>
                {quote.rfqNo ? `RFQ ${quote.rfqNo}` : 'Open RFQ'}
              </Link>
            ) : null}
            {quote.leadId ? (
              <Link component="button" type="button" underline="hover" onClick={() => navigate(`/procurement/leads/view/${quote.leadId}`)} sx={linkSx}>
                Open lead
              </Link>
            ) : null}
            {!quote.nexoraSerial && (
              // Shown, never hidden. A quotation with no commercial case cannot be traced from
              // inquiry to delivery, and an absent mark would read as a rendering gap rather than
              // the defect it is.
              <Tooltip title="This quotation states no commercial case, so it cannot be traced to its inquiry or to delivery. It was created outside the RFQ path.">
                <Box component="span" sx={{ color: 'warning.dark', fontWeight: 700 }}>Not linked to a case</Box>
              </Tooltip>
            )}
            {quote.nexoraSerial && !quote.rfqId && !quote.leadId && 'Not stated'}
          </>
        )}
      />
    </Stack>
  );

  return (
    <Box sx={{ p: { xs: 1, sm: 2 }, maxWidth: 1600, mx: 'auto', minWidth: 0, overflowX: 'hidden' }}>
      {/* WHO AND WHERE IT STANDS: the way back, the customer, the one status, and the actions. */}
      <Stack
        component="section"
        aria-labelledby="quote-customer"
        direction={{ xs: 'column', lg: 'row' }}
        spacing={{ xs: 1, lg: 2.5 }}
        sx={{ alignItems: { lg: 'center' }, justifyContent: 'space-between', mb: 1.25, px: 0.5 }}
      >
        <Box sx={{ minWidth: 0 }}>
          <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center' }}>
            <Link component="button" type="button" variant="caption" onClick={() => navigate('/sales/quotes')} sx={{ fontWeight: 700, textTransform: 'uppercase', textDecoration: 'none', color: 'text.secondary' }}>
              Quotes
            </Link>
            <Typography variant="caption" color="text.disabled">›</Typography>
            <Typography variant="caption" sx={{ fontWeight: 700, textTransform: 'uppercase', color: 'text.primary' }}>{quote.quoteNo}</Typography>
          </Stack>
          <Stack direction="row" useFlexGap spacing={1.25} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
            <Typography id="quote-customer" component="h1" variant="h5" sx={{ fontWeight: 800, fontSize: { xs: '1.3rem', md: '1.45rem', xl: '1.6rem' }, letterSpacing: '-0.015em', lineHeight: 1.2 }}>
              {quote.customerName || 'Customer not set'}
            </Typography>
            <Box data-testid="quote-status" sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.75, minWidth: 0 }}>
              <QuoteStatusChip quote={chipQuote} hideDetail />
              {statusWords.detail && (
                <Typography variant="caption" color="text.secondary" noWrap>{statusWords.detail}</Typography>
              )}
            </Box>
            {uploadedFileName && (
              <Tooltip title={`Made outside Nexora and uploaded: ${uploadedFileName}`}>
                <Chip label="Uploaded" size="small" variant="outlined" sx={{ fontWeight: 700, fontSize: '0.7rem' }} />
              </Tooltip>
            )}
          </Stack>
        </Box>

        {/* The action rail: outlined helpers and More. The one contained button is the Next step
            panel's; the rail never repeats it. */}
        <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'center', flexShrink: 0, position: { lg: 'sticky' }, top: 8, zIndex: 2, p: 0.75, borderRadius: 2, bgcolor: (t) => alpha(t.palette.background.default, 0.72), backdropFilter: 'blur(10px)', '& .MuiButton-root': { minHeight: 36, whiteSpace: 'nowrap' } }}>
          {panelKey !== 'edit' && editControl}
          {panelKey !== 'revise' && reviseControl}
          {panelKey !== 'pdf' && pdfControl}
          {showMore && (
            <>
              <Tooltip title={moreTooltip} describeChild>
                <Button
                  variant="outlined"
                  endIcon={<ExpandMoreIcon />}
                  aria-haspopup="menu"
                  aria-expanded={moreMenuOpen ? 'true' : undefined}
                  aria-controls={moreMenuOpen ? moreMenuId : undefined}
                  onClick={(event) => setMoreAnchor(event.currentTarget)}
                >
                  More
                </Button>
              </Tooltip>
              <Menu id={moreMenuId} anchorEl={moreAnchor} open={moreMenuOpen} onClose={() => setMoreAnchor(null)}>
                {/* A follow-up the rep sets by hand. Delivery creates one automatically when a quote is
                    sent; anything promised in a phone call afterwards had nowhere to go. */}
                <MenuItem onClick={() => { setMoreAnchor(null); setFollowUpOpen(true); }}>
                  <ListItemIcon><FollowUpIcon fontSize="small" /></ListItemIcon>
                  <ListItemText primary="Follow up on this quote" />
                </MenuItem>
                {/*
                  R7: the buyer's most common request — "can you hold your price for another two
                  weeks". Offered only while the quote is live with the customer and has not been
                  superseded; extending records a reason and does NOT create a revision, so the
                  customer keeps looking at the same commercial offer.
                */}
                {canExtendHere && (
                  <MenuItem onClick={() => { setMoreAnchor(null); setExtendValidityOpen(true); }}>
                    <ListItemIcon><ExtendValidityIcon fontSize="small" /></ListItemIcon>
                    <ListItemText primary="Extend validity" />
                  </MenuItem>
                )}
                {sendAgainAllowed && (
                  <MenuItem disabled={sendBlockedReason !== null} onClick={() => { setMoreAnchor(null); startSend(); }}>
                    <ListItemIcon><EmailIcon fontSize="small" /></ListItemIcon>
                    <ListItemText primary="Send again" secondary={sendBlockedReason?.text} />
                  </MenuItem>
                )}
                {quote.rfqId && (
                  <MenuItem onClick={() => { setMoreAnchor(null); navigate(`/procurement/rfqs/${quote.rfqId}/sourcing`); }}>
                    <ListItemIcon><SourcingIcon fontSize="small" /></ListItemIcon>
                    <ListItemText primary="Sourcing & offers" />
                  </MenuItem>
                )}
              </Menu>
            </>
          )}
          {panelKey !== 'send' && sendControl}
          {panelKey !== 'update' && updateControl}
          {panelKey !== 'po' && poControl}
        </Stack>
      </Stack>

      {factsRow}

      {/* The next step, with everything that stops this quote listed under it, each with its own
          way to act on it. One line when there is nothing to list. */}
      <Box sx={{ mb: 2 }}>
        <NextStepPanel
          dense={!showBlockerPanel}
          tone={panelTone}
          title={panelTitle}
          sentence={nextStepText}
          action={panelAction}
          testId="quote-next-step"
        >
          {showBlockerPanel || readinessRetry ? (
          <>
          {showBlockerPanel ? (
          <Stack component="ol" spacing={1} sx={{ m: 0, pl: 2.5 }}>
            {showPreSendGates && revisionImpactPresentation && (
              <Stack component="li" direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ alignItems: { sm: 'center' }, justifyContent: 'space-between' }}>
                <Box>
                  <Typography sx={{ fontWeight: 800 }}>{revisionImpactPresentation.title}</Typography>
                  <Typography variant="body2">{revisionImpactPresentation.detail}</Typography>
                </Box>
                {canEditQuotes && (
                  <Stack direction="row" spacing={1} sx={{ flexShrink: 0 }}>
                    {/* The decision, as two buttons: take the customer's new quantities (the
                        draft is re-totalled and the review recorded), or keep what was quoted.
                        One button — "Mark review complete" — let the draft go out with the OLD
                        quantities and nothing on the screen said so. */}
                    {canApplyRevision && (
                      <Tooltip title="Update the quantities on this draft to the customer's new revision, re-total it, and record the review." describeChild>
                        <Button
                          size="small"
                          variant={primaryAction === 'revision' ? 'contained' : 'outlined'}
                          disabled={applyImpactMutation.isPending || resolveImpactMutation.isPending}
                          onClick={() => applyImpactMutation.mutate()}
                          sx={{ whiteSpace: 'nowrap', fontWeight: 800 }}
                        >
                          {applyImpactMutation.isPending ? 'Applying…' : 'Apply the new quantities'}
                        </Button>
                      </Tooltip>
                    )}
                    <Tooltip title={isCustomerRevision ? 'Leave every quantity and price as quoted and record that the customer revision was reviewed.' : ''} describeChild>
                      <Button
                        color="inherit"
                        size="small"
                        variant={canApplyRevision || primaryAction !== 'revision' ? 'outlined' : 'contained'}
                        disabled={resolveImpactMutation.isPending || applyImpactMutation.isPending}
                        onClick={keepAsQuoted}
                        sx={{ whiteSpace: 'nowrap' }}
                      >
                        {revisionImpactPresentation.action}
                      </Button>
                    </Tooltip>
                  </Stack>
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
            {/* What the buyer asked for, checked against this quote. Warnings, not blockers: each
                names the problem, the rep decides (owner rule: inform, don't obstruct). */}
            {showWarnings && otherWarnings.map((warning) => (
              <Stack key={warning.code} component="li" direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ alignItems: { sm: 'center' }, justifyContent: 'space-between' }}>
                <Typography variant="body2">{warning.message}</Typography>
                {warning.code === 'VALIDITY_BELOW_BUYER_MINIMUM' && warning.suggestedValidUntil && canEditQuotes && (
                  <Button color="inherit" size="small" variant="outlined" sx={{ whiteSpace: 'nowrap' }}
                    disabled={setValidityMutation.isPending}
                    onClick={() => setValidityMutation.mutate(warning.suggestedValidUntil!.split('T')[0])}>
                    Set to {dayjs(warning.suggestedValidUntil.split('T')[0]).format('D MMM YYYY')}
                  </Button>
                )}
              </Stack>
            ))}
            {showPreSendGates && supplierValidityWarnings.length > 0 && (
              <Stack component="li" direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ alignItems: { sm: 'center' }, justifyContent: 'space-between' }}>
                <Box>
                  <Typography sx={{ fontWeight: 800 }}>Supplier price ends before this quote</Typography>
                  <Typography variant="body2">{supplierValidityWarnings.length} priced line{supplierValidityWarnings.length === 1 ? '' : 's'} use an expired, unstated, or shorter supplier quote validity. Check the source offer before sending.</Typography>
                </Box>
                {quote.rfqId && <Button color="inherit" size="small" variant="outlined" startIcon={<SourcingIcon />} onClick={() => navigate(`/procurement/rfqs/${quote.rfqId}/sourcing`)} sx={{ whiteSpace: 'nowrap' }}>Check the offers</Button>}
              </Stack>
            )}
          </Stack>
          ) : null}
          {readinessRetry && (
            <Button size="small" color="inherit" onClick={() => readinessQuery.refetch()} sx={{ mt: showBlockerPanel ? 1 : 0 }}>
              Try again
            </Button>
          )}
          </>
          ) : undefined}
        </NextStepPanel>
      </Box>

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

      <Stack spacing={2}>
        {/* The work: the lines and, directly under them, the totals they add up to. */}
        <Paper variant="outlined" sx={{ borderRadius: 3, maxWidth: '100%', overflow: 'hidden' }}>
          <Box sx={{ px: 2, py: 1.25, borderBottom: '1px solid', borderColor: 'divider' }}><Typography variant="subtitle1" component="h2" sx={{ fontWeight: 800 }}>Quoted items</Typography></Box>
          <Box sx={{ overflowX: 'auto' }}>
            <Table size="small">
              <TableHead sx={{ '& th': { fontWeight: 700, color: 'text.secondary', fontSize: '0.8rem', bgcolor: 'action.hover' } }}>
                <TableRow>
                  <TableCell>Ref</TableCell>
                  <TableCell>Description</TableCell>
                  <TableCell align="right">Qty</TableCell>
                  <TableCell>UOM</TableCell>
                  {showCostColumn && <TableCell>Cost source</TableCell>}
                  <TableCell align="right">Unit price</TableCell>
                  <TableCell align="right">Discount</TableCell>
                  <TableCell align="right">Total excl. VAT</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {quote.quoteItems.map((item, idx) => (
                  <TableRow key={item.id} hover>
                    {/* The buyer's own line reference (their RFQ line, e.g. SAP "00010"); synthetic index only for legacy lines */}
                    <TableCell>{item.customerLineRef || idx + 1}</TableCell>
                    {/* The same line the customer's PDF prints: description, what the buyer calls it
                        (material, maker, part no.), what is offered, and how it is priced. */}
                    <TableCell>
                      <Typography sx={{ fontWeight: 700, fontSize: '0.85rem' }}>{item.productName || 'Item'}</Typography>
                      <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>{item.itemDescription}</Typography>
                      {buyerIdentityLine(item) && <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>{buyerIdentityLine(item)}</Typography>}
                      {item.offeredNote && <Typography variant="caption" color="info.main" sx={{ display: 'block', fontWeight: 700 }}>{item.offeredNote}</Typography>}
                      {pricingStatusText(item) && <Typography variant="caption" color={item.pricingStatus === 'NOT_QUOTED' ? 'error.main' : 'warning.main'} sx={{ display: 'block', fontWeight: 700 }}>{pricingStatusText(item)}</Typography>}
                    </TableCell>
                    <TableCell align="right">{item.quantity}</TableCell>
                    <TableCell>{item.unitOfMeasure || '—'}</TableCell>
                    {showCostColumn && (
                      <TableCell>
                        {/* A failed or loading sourcing read is "not known", never "pending"; and
                            nothing is pending on a quote that can no longer be sent. */}
                        {!sourcingQuery.isSuccess ? (sourcingQuery.isError ? "Couldn't check" : '—') : (() => {
                          const source = sourceFor(item);
                          if (source.source === 'COST_SOURCE_PENDING' && !showPreSendGates) return '—';
                          return <Stack spacing={0.5} sx={{ alignItems: 'flex-start' }}>
                            <Chip size="small" color={source.source === 'COST_SOURCE_PENDING' ? 'warning' : 'info'} variant="outlined" label={COST_SOURCE_WORDS[source.source] ?? source.source} sx={{ fontWeight: 700 }} />
                            {source.offer && <Typography variant="caption" color="text.secondary">{source.offer.supplierName} · {source.offer.quoteReference || 'No supplier reference'} · valid {source.offer.validUntil ? dayjs(source.offer.validUntil).format('DD MMM YYYY') : 'not stated'}</Typography>}
                            {source.rfqItemId > 0 && (
                              <Button size="small" sx={{ px: 0, minHeight: 0 }} aria-label={`View cost evidence for row ${idx + 1}${item.customerLineRef ? ` (line ${item.customerLineRef})` : ''}`} onClick={() => navigate(`/procurement/rfqs/${quote.rfqId}/sourcing`)}>View cost evidence</Button>
                            )}
                          </Stack>;
                        })()}
                      </TableCell>
                    )}
                    <TableCell align="right" className="tabular-nums">{item.pricingStatus === 'TO_FOLLOW' ? 'To follow' : item.pricingStatus === 'NOT_QUOTED' ? 'Not quoted' : Number(item.unitPrice || 0) === 0 ? <Chip size="small" label="No price yet" color="warning" variant="outlined" sx={{ fontWeight: 700 }} /> : formatMoney(item.unitPrice, quote.currencyCode)}</TableCell>
                    <TableCell align="right" className="tabular-nums">
                      {(item.discount ?? 0) > 0 ? (
                        <Typography variant="caption" color="error.main" sx={{ fontWeight: 700 }}>
                          - {formatMoney(item.discount, quote.currencyCode)}
                          <br />
                          ({item.discountTypeName})
                        </Typography>
                      ) : '—'}
                    </TableCell>
                    {/* Ex-VAT, as the PDF prints it: the stored totalAmount carries the line's VAT, so
                        showing it here put 2,876.15 on screen where the customer reads 2,501.00. */}
                    <TableCell align="right" className="tabular-nums" sx={{ fontWeight: 700 }}>{isUnpricedDraft ? 'No price yet' : isUnpricedByChoice(item) ? '—' : formatMoney(lineTotalExVat(item), quote.currencyCode)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Box>
          <Box sx={{ p: 2, borderTop: '1px solid', borderColor: 'divider', display: 'flex', justifyContent: 'flex-end' }}>
            <Card className="tabular-nums" variant="outlined" sx={{ borderRadius: 2, width: '100%', maxWidth: 440 }}>
              <CardContent sx={{ p: 2, '&:last-child': { pb: 2 } }}>
                <Typography variant="subtitle1" component="h2" sx={{ fontWeight: 800, mb: 1.5 }}>Summary</Typography>
                <Stack spacing={1.25}>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2 }}><Typography color="text.secondary">Gross subtotal</Typography><Typography sx={{ fontWeight: 700 }}>{isUnpricedDraft ? 'No price yet' : formatMoney(itemsSubtotal, quote.currencyCode)}</Typography></Box>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2 }}><Typography color="text.secondary">Item discounts</Typography><Typography sx={{ fontWeight: 700, color: !isUnpricedDraft && itemsDiscounts > 0 ? 'error.main' : 'text.primary' }}>{isUnpricedDraft ? '—' : itemsDiscounts > 0 ? `- ${formatMoney(itemsDiscounts, quote.currencyCode)}` : formatMoney(0, quote.currencyCode)}</Typography></Box>
                  {headerDiscount > 0 && <Box sx={{ display: 'flex', justifyContent: 'space-between' }}><Typography color="text.secondary">Header discount</Typography><Typography sx={{ fontWeight: 700, color: 'error.main' }}>- {formatMoney(headerDiscount, quote.currencyCode)}</Typography></Box>}
                  {/* The two rows the panel had no way to show, and without which the numbers on it
                      could not be added up: the base the tax is charged on, and the tax. Same three
                      lines, same order, as the printed quote. */}
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2 }}><Typography color="text.secondary">Total excluding VAT</Typography><Typography sx={{ fontWeight: 700 }}>{isUnpricedDraft ? '—' : totals.hasUnderivedTax ? '—' : formatMoney(totals.netExcludingTax, quote.currencyCode)}</Typography></Box>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2 }}>
                    <Typography color={totals.hasUnderivedTax ? 'warning.main' : 'text.secondary'}>{vatLabel}</Typography>
                    <Typography sx={{ fontWeight: 700 }} color={totals.hasUnderivedTax ? 'warning.main' : undefined}>
                      {totals.hasUnderivedTax ? 'Not derived' : formatMoney(totals.totalTax, quote.currencyCode)}
                    </Typography>
                  </Box>
                  {totals.hasUnderivedTax && <Alert severity="warning" sx={{ py: 0 }}>No output tax rate is configured, so this quote cannot be sent.</Alert>}
                  <Divider />
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2 }}><Typography variant="h6" sx={{ fontWeight: 800 }}>Grand total</Typography><Typography variant="h6" sx={{ fontWeight: 800, color: isUnpricedDraft ? 'warning.main' : 'text.primary' }}>{isUnpricedDraft ? 'No price yet' : formatMoney(quote.totalAmount, quote.currencyCode)}</Typography></Box>
                </Stack>
              </CardContent>
            </Card>
          </Box>
        </Paper>

        {/* The rep's notes print on the customer's PDF and e-mail; the internal draft marker does not, so it is not shown as if it would. */}
        {buyerNotes(quote.headerRemarks) && <Box sx={{ p: 2, bgcolor: 'action.hover', borderRadius: 2, borderLeft: '4px solid', borderColor: 'primary.main' }}><Typography variant="caption" color="text.secondary" sx={{ fontWeight: 800 }}>NOTES TO CUSTOMER</Typography><Typography variant="body2">{buyerNotes(quote.headerRemarks)}</Typography></Box>}

        {/* Evidence and record, folded. Nothing is removed; it is simply not in the rep's way. An
            uploaded quote has no Nexora cost behind it, so it has no evidence fold. */}
        {!uploadedFileName && (
          <Accordion variant="outlined" disableGutters sx={{ borderRadius: 3, '&::before': { display: 'none' } }}>
            <AccordionSummary expandIcon={<ExpandMoreIcon />}>
              <Typography sx={{ fontWeight: 600 }}>Where the prices come from</Typography>
            </AccordionSummary>
            <AccordionDetails>
              <Stack spacing={2} sx={{ alignItems: 'flex-start' }}>
                <Box sx={{ width: '100%' }}><CommercialLineIntelligence stage="quote" recordId={quote.id} /></Box>
                {quote.rfqId && <Button variant="outlined" startIcon={<SourcingIcon />} onClick={() => navigate(`/procurement/rfqs/${quote.rfqId}/sourcing`)}>Sourcing & offers</Button>}
              </Stack>
            </AccordionDetails>
          </Accordion>
        )}

        <Accordion variant="outlined" disableGutters sx={{ borderRadius: 3, '&::before': { display: 'none' } }}>
          <AccordionSummary expandIcon={<ExpandMoreIcon />}>
            <Typography sx={{ fontWeight: 600 }}>Quote record</Typography>
          </AccordionSummary>
          <AccordionDetails>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, sm: 6, md: 3 }}><Fact label="Source RFQ" value={quote.rfqId ? <Link component="button" type="button" underline="hover" onClick={() => navigate(`/procurement/rfqs/view/${quote.rfqId}`)} sx={linkSx}>{quote.rfqNo || `RFQ ${quote.rfqId}`}</Link> : 'None'} /></Grid>
              <Grid size={{ xs: 12, sm: 6, md: 3 }}><Fact label="Owner" value={quote.ownerName || 'Not set'} /></Grid>
              <Grid size={{ xs: 12, sm: 6, md: 3 }}><Fact label="Created" value={`${formatDateSafe(quote.createdDate)}${quote.createdBy ? ` by ${quote.createdBy}` : ''}`} /></Grid>
              <Grid size={{ xs: 12, sm: 6, md: 3 }}><Fact label="Nexora serial" value={quote.nexoraSerial ? <Box component="span" sx={{ fontFamily: 'monospace' }}>{quote.nexoraSerial}</Box> : <Box component="span" sx={{ color: 'warning.dark' }}>Not linked to a commercial case</Box>} /></Grid>
              <Grid size={{ xs: 12, sm: 6, md: 3 }}><Fact label="Source revisions" value={quote.sourceLeadRevision > 0 && quote.sourceRfqRevision > 0 ? `Lead rev ${quote.sourceLeadRevision} · RFQ rev ${quote.sourceRfqRevision}` : '—'} /></Grid>
              {(quote.discountValue || 0) > 0 && (
                <Grid size={{ xs: 12, sm: 6, md: 3 }}><Fact label="Header discount" value={<Box component="span" sx={{ color: 'error.main' }}>{quote.discountTypeName}: {quote.discountValue}</Box>} /></Grid>
              )}
            </Grid>
          </AccordionDetails>
        </Accordion>
      </Stack>

      <UpdateQuoteStatusDialog
        open={updateStatusOpen}
        quote={quote}
        onClose={() => setUpdateStatusOpen(false)}
        invalidateKeys={[['quote-detail', id], ['quotes'], ['quote-revisions', id]]}
      />

      {/* Mounted only while open: it resolves the RFQ's quote and its email draft on open. */}
      {sendWindowOpen && quote.rfqId && (
        <SendQuoteDialog
          open
          rfqId={Number(quote.rfqId)}
          onSent={refreshAfterSendWindow}
          onClose={() => { setSendWindowOpen(false); refreshAfterSendWindow(); }}
        />
      )}

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
        initialEmail={quote.customerEmail || emailDraftQuery.data?.recipientEmail || ''}
        initialSubject={emailDraftQuery.data?.subject}
        initialBody={emailDraftQuery.data?.body}
        attachmentName={emailDraftQuery.data?.attachmentFileName ?? `Quote_${quote.quoteNo}.pdf`}
        draftLoading={emailOpen && emailDraftQuery.isPending}
        loading={sendMutation.isPending}
        composerFields="message"
        confirmLabel="Send quote"
        businessUnitId={businessUnitId}
        customerId={quote.customerId ?? null}
        onCancel={() => setEmailOpen(false)}
        onConfirm={(email, subject, body) => {
          // R5: choosing the recipient no longer sends. The prices are confirmed first; the
          // reviewed words travel with the send.
          setPendingSend({ recipientEmail: email, subject, body });
          setEmailOpen(false);
          setPriceConfirmOpen(true);
        }}
      />

      <PriceConfirmationDialog
        open={pdfConfirmOpen}
        purpose="pdf"
        quoteId={Number(id)}
        quoteNo={quote.quoteNo}
        recipientEmail=""
        submitting={confirmPriceForPdfMutation.isPending || pdfMutation.isPending}
        onCancel={() => setPdfConfirmOpen(false)}
        onConfirm={(source, reference) => confirmPriceForPdfMutation.mutate({ source, reference })}
      />

      <QuoteTermsDialog
        open={termsOpen}
        quoteId={Number(id)}
        businessUnitId={businessUnitId}
        currencyId={quote.currencyId}
        validUntil={quote.validUntil}
        suggestedValidUntil={sendReadiness?.buyerTerms?.requiredValidUntil}
        allowedCurrencies={sendReadiness?.buyerTerms?.allowedCurrencies}
        onCancel={() => setTermsOpen(false)}
        onSaved={async () => {
          setTermsOpen(false);
          await Promise.all([
            queryClient.invalidateQueries({ queryKey: ['quote-detail', id] }),
            queryClient.invalidateQueries({ queryKey: ['quote-send-readiness', id] }),
          ]);
          pdfMutation.mutate();
        }}
      />

      <KeepAsQuotedDialog
        open={keepDialog !== null}
        mode={keepDialog ?? 'keep'}
        message={revisionWarning?.message ?? revisionImpactPresentation?.detail}
        changes={revisionWarning?.revision?.changes ?? quote.revisionImpactDetail?.changes}
        canApply={canApplyRevision}
        busy={resolveImpactMutation.isPending || applyImpactMutation.isPending}
        onApply={() => applyImpactMutation.mutate()}
        onCancel={() => setKeepDialog(null)}
        onKeep={(reason) => resolveImpactMutation.mutate({ reason, thenSend: keepDialog === 'send' })}
      />

      <PriceConfirmationDialog
        open={priceConfirmOpen}
        quoteId={Number(id)}
        quoteNo={quote.quoteNo}
        recipientEmail={pendingSend.recipientEmail}
        submitting={confirmPriceMutation.isPending || sendMutation.isPending}
        onCancel={() => setPriceConfirmOpen(false)}
        onConfirm={(source, reference) => confirmPriceMutation.mutate({ source, reference })}
      />
    </Box>
  );
};

export default QuoteViewPage;
