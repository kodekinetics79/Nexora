import { Fragment, useMemo, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  FormControl,
  Grid,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Stack,
  Tab,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Tabs,
  TextField,
  Tooltip,
  Typography,
} from "@mui/material";
import {
  ArrowBack,
  OpenInNew,
  AssignmentTurnedIn,
  HowToReg,
  Inventory2,
  LocalShipping,
  Public,
  Refresh,
  Replay,
  Send,
  ShoppingCartCheckout,
  PriceCheck,
  Insights,
  WarningAmber,
} from "@mui/icons-material";
import { toast } from "react-hot-toast";
import procurementService, {
  INCOTERMS_2020,
  type Incoterm,
  type PurchaseOrderLineTradeTerms,
  type QuoteComparisonLine,
  type QuoteComparisonResult,
  type QuoteScoreCriterion,
  type SupplierAcknowledgementStatus,
  type SupplierOffer,
  type SupplierPurchaseOrder,
  type SupplierSolicitation,
} from "../../../api/services/procurementService";
import currencyService, {
  type CurrencyDTO,
} from "../../../api/services/currencyService";
import warehouseService, {
  type WarehouseDTO,
} from "../../../api/services/warehouseService";
import { supplierTierLabel } from "../../../api/services/supplierService";
import {
  cheapestEligibleOffer,
  offerScoreState,
  orderOffersForComparison,
  rankScoredOffers,
  recommendationTradeOff,
  roundedPoints as points,
  warrantyComparisonCell,
} from "../../../utils/supplierComparison";
import {
  WARRANTY_MONTHS_HELPER,
  WARRANTY_WORDING_NOT_CAPTURED_HERE,
  formatWarrantyMonths,
  parseWarrantyMonthsInput,
  warrantyMonthsFieldValue,
} from "../../../utils/warrantyMonths";
import { useAuth } from "../../../context/AuthContext";
import commercialLearningService from "../../../api/services/commercialLearningService";
import InboundShipmentsPanel from "./InboundShipmentsPanel";
import { formatMoney } from "../../../utils/currency";

const commandKey = (prefix: string) => `${prefix}:${crypto.randomUUID()}`;
const number = (value: unknown) => Number(value || 0);
// The default was `currency = "USD"`, on a KSA-first product: a landed cost whose record
// carried no currency was rendered with a dollar sign it had never claimed. utils/currency.ts
// exists to make that impossible — where the record states no currency, a bare grouped number
// is the honest output.
const money = (value: number, currency?: string | null) => formatMoney(value, currency);
/**
 * The refusal the server actually gave, in its own words.
 *
 * The procurement API answers with RFC 7807 ProblemDetails, where `title` is the generic category
 * ("Invalid procurement request", "Procurement conflict") and `detail` carries the sentence that
 * says what is wrong — "A revised lead time is a counter-offer. Record this answer as COUNTERED."
 * Reading `title` first, as this did, showed every buyer the category and threw the reason away, so
 * a refusal with a precise, actionable cause arrived on screen as an unactionable label. `detail`
 * comes first for that reason; `title` survives only as the last resort before the caller's
 * fallback.
 */
const errorMessage = (error: any, fallback: string) =>
  error?.response?.data?.detail ||
  error?.response?.data?.message ||
  error?.message ||
  error?.response?.data?.title ||
  fallback;
const cheapestEligible = (comparison?: QuoteComparisonResult): QuoteComparisonLine | null =>
  cheapestEligibleOffer(comparison?.lines ?? []);

/**
 * The offer's own number for one criterion, in the unit a buyer would say it in. The scorer sends
 * a bare double; showing "1240" where a landed cost belongs, or "14" where days belong, would make
 * the row unreadable as evidence.
 */
const criterionRawValue = (
  criterion: QuoteScoreCriterion,
  currencyCode: string,
): string => {
  if (criterion.rawValue == null) return "not stated";
  switch (criterion.criterion) {
    case "PRICE":
      return money(criterion.rawValue, currencyCode);
    case "LEAD_TIME":
      return `${criterion.rawValue} days`;
    case "WARRANTY":
      // The same words the warranty column uses, so the raw value in the breakdown and the value
      // in the column are recognisably the one fact rather than two renderings of it.
      return formatWarrantyMonths(criterion.rawValue) ?? "not stated";
    case "PAYMENT_TERMS":
      return `${criterion.rawValue} credit days`;
    default:
      return String(criterion.rawValue);
  }
};

const localCalendarDate = (value: Date) =>
  `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, "0")}-${String(value.getDate()).padStart(2, "0")}`;
const receiptTimestamp = (calendarDate: string, now = new Date()) =>
  calendarDate === localCalendarDate(now)
    ? now.toISOString()
    : new Date(`${calendarDate}T23:59:59.999Z`).toISOString();

/**
 * What a goods receipt has to be able to say about the material it is booking in.
 *
 * Product.batchTracking and Product.serialTracking are two switches in the product dialog, and
 * before this existed either one made every goods receipt for that product throw inside the receipt
 * transaction — the refusal named a field no screen offered. The guard was right; the screen was
 * missing. `serials` is held as free text so an operator can paste a column from a packing list.
 */
type LotDraft = {
  lotNumber: string;
  serials: string;
  countryOfOrigin: string;
  supplierBatchReference: string;
  expiryDate: string;
};

const emptyLotDraft = (): LotDraft => ({
  lotNumber: "",
  serials: "",
  countryOfOrigin: "",
  supplierBatchReference: "",
  expiryDate: "",
});

const parseSerials = (raw: string | undefined) =>
  (raw ?? "")
    .split(/[\n,;]+/)
    .map((value) => value.trim())
    .filter((value) => value.length > 0);

const MaxLotIdentifierLength = 80;

/**
 * The same rules Traceability/MaterialLotRecorder.cs enforces, checked before the operator presses
 * Post. The server stays the authority — its message is what gets shown when it refuses — but a
 * receipt that is certain to be rejected should not cost a round trip and a scary error toast.
 */
const lotProblem = (
  line: any,
  quantity: number,
  draft: LotDraft | undefined,
): string | null => {
  if (quantity <= 0) return null;
  if (line.trackingMode === "LOT") {
    const lotNumber = (draft?.lotNumber ?? "").trim();
    if (!lotNumber)
      return "This line is batch-tracked; the supplier's lot or batch number is required.";
    if (lotNumber.length > MaxLotIdentifierLength)
      return "A lot number must be 80 characters or fewer.";
    return null;
  }
  if (line.trackingMode === "SERIAL") {
    const serials = parseSerials(draft?.serials);
    if (serials.length === 0)
      return "This line is serial-tracked; one serial number per received unit is required.";
    if (serials.some((value) => value.length > MaxLotIdentifierLength))
      return "A serial number must be 80 characters or fewer.";
    const seen = new Set(serials.map((value) => value.toLowerCase()));
    if (seen.size !== serials.length)
      return "The same serial number is declared twice.";
    if (serials.length !== quantity)
      return `Receiving ${quantity} unit(s) but ${serials.length} serial number(s) were declared.`;
    return null;
  }
  return null;
};

/**
 * Only what the operator actually stated. A blank origin is left off entirely so the server applies
 * the ordered origin itself — sending the ordered value back as if it had been observed would make
 * "what arrived came from somewhere else" unsayable, and the customs origin-mismatch check would be
 * structurally incapable of firing.
 */
const lotPayload = (line: any, draft: LotDraft | undefined) => {
  if (line.trackingMode === "UNTRACKED") return undefined;
  const lotNumber = (draft?.lotNumber ?? "").trim();
  const serials = parseSerials(draft?.serials);
  const countryOfOrigin = (draft?.countryOfOrigin ?? "").trim();
  const supplierBatchReference = (draft?.supplierBatchReference ?? "").trim();
  const expiryDate = (draft?.expiryDate ?? "").trim();
  return {
    ...(line.trackingMode === "LOT" && lotNumber ? { lotNumber } : {}),
    ...(line.trackingMode === "SERIAL" && serials.length > 0
      ? { serialNumbers: serials }
      : {}),
    ...(countryOfOrigin ? { countryOfOrigin } : {}),
    ...(supplierBatchReference ? { supplierBatchReference } : {}),
    ...(expiryDate ? { expiryDate } : {}),
  };
};
const localDateTimeInput = (value: Date) =>
  new Date(value.getTime() - value.getTimezoneOffset() * 60_000)
    .toISOString()
    .slice(0, 16);
const sha256Pattern = /^[a-fA-F0-9]{64}$/;
/**
 * A `YYYY-MM-DD` calendar date rendered in the buyer's locale WITHOUT going through
 * `new Date(string)`, which parses a bare date as UTC midnight and can therefore display the
 * previous day. A committed ship date that reads one day early is a commitment nobody made.
 */
const calendarDate = (value?: string | null) => {
  if (!value) return null;
  const [year, month, day] = value.slice(0, 10).split("-").map(Number);
  return Number.isFinite(year) && Number.isFinite(month) && Number.isFinite(day)
    ? new Date(year, month - 1, day).toLocaleDateString()
    : value;
};

const statusColor = (
  status: string,
): "default" | "success" | "warning" | "error" | "info" => {
  const normalized = status.replaceAll("_", "").toUpperCase();
  if (
    [
      "SENT",
      "RESPONDED",
      "RECEIVED",
      "ISSUED",
      "APPROVED",
      "ACKNOWLEDGED",
    ].includes(normalized)
  )
    return "success";
  if (
    ["DELIVERYFAILED", "CANCELLED", "EXPIRED", "DECLINED"].includes(normalized)
  )
    return "error";
  if (
    ["PENDINGDISPATCH", "DISPATCHING", "PARTIALLYRECEIVED", "DRAFT"].includes(
      normalized,
    )
  )
    return "warning";
  return "default";
};

/**
 * FR-SPO-03. A supplier's answer is not a status, and it must not be coloured like one.
 *
 * ACCEPTED is the only one that settles anything. COUNTERED and REJECTED both leave the order's
 * status exactly where it was — by design, because neither is agreement — so both must read as
 * work the buyer still owes: a counter needs a decision, a rejection needs re-sourcing. Showing
 * either in a neutral colour would leave them indistinguishable from a normal in-flight order,
 * which is precisely how a refused order sits untouched until the customer chases it.
 */
const acknowledgementColor = (
  status: SupplierAcknowledgementStatus,
): "success" | "warning" | "error" =>
  status === "ACCEPTED" ? "success" : status === "COUNTERED" ? "warning" : "error";

/**
 * FR-SPO-03 read path. What the supplier actually said, on the order it was said about.
 *
 * Before this the answer was write-only: a buyer recorded a counter, the screen did not change
 * because a counter deliberately leaves the status alone, and the revised lead time and the
 * rejection reason — the entire reason for capturing an answer — existed only in the database.
 */
function SupplierAnswer({ order }: { order: SupplierPurchaseOrder }) {
  if (!order.acknowledgementStatus) return null;
  const answered = order.acknowledgementStatus;
  const shipDate = calendarDate(order.committedShipDate);
  return (
    <Alert
      severity={acknowledgementColor(answered)}
      icon={false}
      square
      sx={{ borderRadius: 0 }}
    >
      <Typography variant="body2" sx={{ fontWeight: 700 }}>
        {answered === "ACCEPTED"
          ? "Supplier accepted this order"
          : answered === "COUNTERED"
            ? "Supplier countered — this order is not agreed yet"
            : "Supplier rejected this order"}
      </Typography>
      <Typography
        variant="caption"
        color="text.secondary"
        sx={{ display: "block" }}
      >
        {/* The supplier's person, kept distinct from the Nexora user who keyed it in. */}
        Answered by {order.acknowledgedBy || "an unnamed supplier contact"}
        {order.acknowledgedOn
          ? ` · recorded ${new Date(order.acknowledgedOn).toLocaleString()}`
          : ""}
      </Typography>
      {(order.revisedLeadTimeDays || shipDate) && (
        <Typography variant="body2" sx={{ mt: 0.5 }}>
          {[
            order.revisedLeadTimeDays
              ? `Revised lead time: ${order.revisedLeadTimeDays} day${order.revisedLeadTimeDays === 1 ? "" : "s"}`
              : null,
            shipDate ? `Committed ship date: ${shipDate}` : null,
          ]
            .filter(Boolean)
            .join(" · ")}
        </Typography>
      )}
      {order.acknowledgementNote && (
        <Typography variant="body2" sx={{ mt: 0.5 }}>
          {answered === "REJECTED" ? "Reason: " : "Supplier note: "}
          {order.acknowledgementNote}
        </Typography>
      )}
      {answered !== "ACCEPTED" && (
        <Typography variant="caption" sx={{ display: "block", mt: 0.5 }}>
          {answered === "COUNTERED"
            ? "The order still reads as sent because a counter is not agreement. Accept the revised terms with the supplier, or cancel and re-source."
            : "Nothing will arrive against this order. Cancel it and re-source the line, or the customer demand behind it stays uncovered."}
        </Typography>
      )}
    </Alert>
  );
}

/**
 * Only a solicitation whose delivery FAILED can be retried. The server refuses every other
 * status, so gating the button on the red status colour (which also covers CANCELLED,
 * EXPIRED and DECLINED) offered a Retry that could never succeed and always ended in an
 * error toast. Mirrors ProcurementApplicationService.RetrySolicitationAsync.
 */
const isRetryable = (status: string): boolean =>
  status.replaceAll("_", "").toUpperCase() === "DELIVERYFAILED";

const ResolutionChip = ({ resolution }: { resolution: string }) => {
  const colors: Record<
    string,
    "success" | "warning" | "error" | "info" | "default"
  > = {
    IN_STOCK: "success",
    PARTIAL: "warning",
    INCOMING: "info",
    SHORTAGE: "error",
    UNKNOWN: "error",
    POSSIBLE_MATCH: "warning",
  };
  return (
    <Chip
      size="small"
      label={resolution.replaceAll("_", " ")}
      color={colors[resolution] || "default"}
    />
  );
};

function SourcingWorkbenchPage() {
  const { rfqId: routeRfqId } = useParams<{ rfqId?: string }>();
  const rfqId = routeRfqId ? Number(routeRfqId) : undefined;
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { userData, hasPermission } = useAuth();
  const [tab, setTab] = useState(0);
  const [responseSolicitation, setResponseSolicitation] =
    useState<SupplierSolicitation | null>(null);
  const [awardOffer, setAwardOffer] = useState<SupplierOffer | null>(null);
  const [pricingSelection, setPricingSelection] = useState<{ awardId: number; quoteItemId: number; landedUnitCost: number; currencyCode: string } | null>(null);
  const [memoryLineId, setMemoryLineId] = useState<number | null>(null);
  const [poOpen, setPoOpen] = useState(false);
  const [approveOrder, setApproveOrder] =
    useState<SupplierPurchaseOrder | null>(null);
  const [issueOrder, setIssueOrder] =
    useState<SupplierPurchaseOrder | null>(null);
  const [acknowledgeOrder, setAcknowledgeOrder] =
    useState<SupplierPurchaseOrder | null>(null);
  const [tradeTermsOrder, setTradeTermsOrder] =
    useState<SupplierPurchaseOrder | null>(null);
  const [receiptOrder, setReceiptOrder] =
    useState<SupplierPurchaseOrder | null>(null);
  const retryKeys = useMemo(() => new Map<number, string>(), []);

  const queryKey = ["procurement-sourcing-workbench", rfqId ?? "all"];
  const workbenchQuery = useQuery({
    queryKey,
    queryFn: () => procurementService.getWorkbench(rfqId),
    retry: 1,
  });
  const currenciesQuery = useQuery({
    queryKey: ["procurement-currencies", userData?.businessUnitId],
    queryFn: () =>
      currencyService.getAll({
        businessUnitId: userData?.businessUnitId ?? 0,
        pageSize: 500,
      }),
    enabled: !!userData?.businessUnitId && !!rfqId,
  });
  const warehousesQuery = useQuery({
    queryKey: ["procurement-warehouses", userData?.businessUnitId],
    queryFn: () =>
      warehouseService.getAll({
        businessUnitId: userData?.businessUnitId ?? 0,
        pageSize: 500,
      }),
    enabled: !!userData?.businessUnitId && !!rfqId,
  });
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey });
    void queryClient.invalidateQueries({
      queryKey: ["procurement-quote-comparisons", rfqId],
    });
  };
  const workbench = workbenchQuery.data;

  const comparisonLineIds = useMemo(
    () => [...new Set((workbench?.offers ?? []).map((offer) => offer.rfqItemId))],
    [workbench?.offers],
  );
  const comparisonsQuery = useQuery({
    queryKey: ["procurement-quote-comparisons", rfqId, comparisonLineIds],
    queryFn: async () => {
      const comparisons = await Promise.all(
        comparisonLineIds.map((lineId) =>
          procurementService.getQuoteComparison(lineId),
        ),
      );
      return Object.fromEntries(
        comparisons.map((comparison) => [comparison.rfqItemId, comparison]),
      ) as Record<number, QuoteComparisonResult>;
    },
    enabled: comparisonLineIds.length > 0,
  });

  /**
   * Rank within the RFQ line, so "82" is never presented without the field it beat. Only scored
   * offers are ranked: an offer with a missing weighted value has no score, and inventing a last
   * place for it would be the same as scoring it zero.
   */
  const scoreRanks = useMemo(() => {
    const ranks = new Map<number, { rank: number; of: number }>();
    Object.values(comparisonsQuery.data ?? {}).forEach((comparison) => {
      rankScoredOffers(comparison.lines).forEach((rank, id) => ranks.set(id, rank));
    });
    return ranks;
  }, [comparisonsQuery.data]);

  /**
   * The rows in the order the comparison ranked them: best score first, then offers that are
   * awardable but have no score, then offers that cannot be awarded. Rendering the raw API order
   * would leave a buyer changing the weights and watching nothing move.
   *
   * This is the order of the rows and nothing else. Which offers can be awarded is decided by
   * `Eligible` on each line, exactly as before.
   */
  const orderedOffers = useMemo(
    () =>
      orderOffersForComparison(workbench?.offers ?? [], (offer) =>
        comparisonsQuery.data?.[offer.rfqItemId]?.lines.find(
          (line) => line.supplierQuotedItemId === offer.id,
        ),
      ),
    [workbench?.offers, comparisonsQuery.data],
  );

  const remainingRequirement = (lineId: number) => {
    const line = workbench?.lines.find((candidate) => candidate.id === lineId);
    if (!line) return 0;
    return Math.max(0, line.shortfallQuantity);
  };

  const unresolvedLines = useMemo(
    () =>
      workbench?.lines.filter(
        (line) =>
          remainingRequirement(line.id) > 0,
      ) ?? [],
    [workbench],
  );
  const failedSolicitations =
    workbench?.solicitations.filter(
      (s) => s.status.replaceAll("_", "").toUpperCase() === "DELIVERYFAILED",
    ) ?? [];
  const blockedOffers = Object.values(comparisonsQuery.data ?? {}).flatMap(
    (comparison) => comparison.lines.filter((line) => !line.eligible),
  );
  const approvedUnconverted =
    workbench?.awards.filter(
      (award) =>
        !award.purchaseOrderId &&
        ["APPROVED", "SPLIT_APPROVED"].includes(award.status),
    ) ?? [];
  const canSolicit =
    hasPermission("RFQ Management", "edit") &&
    hasPermission("Supplier History", "create");
  const canCapture = hasPermission("Supplier History", "create");
  const canAward = hasPermission("Supplier History", "edit");
  const canCreatePo =
    hasPermission("Orders", "create") &&
    hasPermission("Supplier History", "edit");
  const canIssuePo = hasPermission("Orders", "edit");
  const canReceive =
    hasPermission("Orders", "edit") && hasPermission("Products", "edit");
  const referenceQueries = [currenciesQuery, warehousesQuery];
  const referenceDataFailed = referenceQueries.some((query) => query.isError);

  const openSourcingCase = useMutation({
    mutationFn: async (line: (typeof unresolvedLines)[number]) =>
      line.sourcingCaseId
        ? { id: line.sourcingCaseId }
        : procurementService.createOrOpenSourcingCase(line.rfqId, line.id, 10),
    onSuccess: (sourcingCase) => navigate(`/procurement/sourcing-cases/${sourcingCase.id}`),
    onError: (error) => toast.error(errorMessage(error, "The governed Sourcing Case could not be opened.")),
  });

  const retryMutation = useMutation({
    mutationFn: (id: number) =>
      procurementService.retrySolicitation(
        id,
        retryKeys.get(id) ?? (() => {
          const key = commandKey(`solicitation-retry:${id}`);
          retryKeys.set(id, key);
          return key;
        })(),
      ),
    onSuccess: (_, id) => {
      retryKeys.delete(id);
      toast.success("Retry queued");
      refresh();
    },
    onError: (error) =>
      toast.error(errorMessage(error, "Could not queue the retry")),
  });

  if (workbenchQuery.isLoading) {
    return (
      <Box sx={{ minHeight: "60vh", display: "grid", placeItems: "center" }}>
        <CircularProgress />
      </Box>
    );
  }
  if (workbenchQuery.isError) {
    return (
      <Box sx={{ p: { xs: 2, md: 3 } }}>
        <Alert
          severity="error"
          action={
            <Button onClick={() => workbenchQuery.refetch()}>Retry</Button>
          }
        >
          {errorMessage(
            workbenchQuery.error,
            "The sourcing workspace could not be loaded.",
          )}
        </Alert>
      </Box>
    );
  }
  if (!workbench) return null;

  return (
    <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1600, mx: "auto" }}>
      <Stack
        direction={{ xs: "column", md: "row" }}
        spacing={2}
        sx={{ justifyContent: "space-between", mb: 2 }}
      >
        <Stack direction="row" spacing={1.5} sx={{ alignItems: "center" }}>
          {/*
            This went to the RFQ LIST, which threw away the identity of the RFQ the reader was
            standing on: leaving the workbench for the RFQ that sent you here cost two clicks and a
            scan of a paginated grid. The whole 3,000-line screen contained two `navigate` calls and
            neither of them went back to its own RFQ.
          */}
          <Tooltip title={rfqId ? "Back to this RFQ" : "Back to RFQs"}>
            <Button
              variant="outlined"
              aria-label={rfqId ? "Back to this RFQ" : "Back to RFQs"}
              onClick={() =>
                navigate(rfqId ? `/procurement/rfqs/view/${rfqId}` : "/procurement/rfqs/all")
              }
              sx={{ minWidth: 40, px: 1 }}
            >
              <ArrowBack />
            </Button>
          </Tooltip>
          <Box>
            <Typography variant="h5" sx={{ fontWeight: 800 }}>
              Sourcing Workbench
            </Typography>
            <Stack
              direction="row"
              spacing={1}
              sx={{ alignItems: "center", flexWrap: "wrap" }}
            >
              <Typography variant="body2" color="text.secondary">
                {workbench.rfqNumber || "All active sourcing"}
              </Typography>
              {workbench.nexoraSerial && (
                <Chip
                  size="small"
                  label={workbench.nexoraSerial}
                  variant="outlined"
                />
              )}
              {workbench.customerName && (
                <Typography variant="body2">
                  {workbench.customerName}
                </Typography>
              )}
            </Stack>
          </Box>
        </Stack>
        <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}>
          {/*
            The workbench already HELD the customer quote — it reads `customerQuoteDraft.lines`, and
            its "Price customer quote" dialog writes onto those lines — while offering no way to
            open the quote it had just priced. Sourcing and quoting were a closed trap on the normal
            path: the only exits were the RFQ list and a sourcing case. This is the door out.
          */}
          {workbench.customerQuoteDraft && (
            <Button
              startIcon={<OpenInNew />}
              onClick={() => navigate(`/sales/quotes/view/${workbench.customerQuoteDraft!.quoteId}`)}
              sx={{ fontWeight: 700 }}
            >
              {`Open quote ${workbench.customerQuoteDraft.quoteNumber}`}
            </Button>
          )}
          <Button
            startIcon={<Refresh />}
            onClick={refresh}
          >
            Refresh
          </Button>
          {rfqId && canSolicit && (
            <Button
              variant="contained"
              startIcon={<Send />}
              onClick={() => unresolvedLines[0] && openSourcingCase.mutate(unresolvedLines[0])}
              disabled={unresolvedLines.length === 0}
            >
              Open governed sourcing
            </Button>
          )}
        </Stack>
      </Stack>

      {(unresolvedLines.length > 0 ||
        failedSolicitations.length > 0 ||
        blockedOffers.length > 0) && (
        <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
          <Typography sx={{ fontWeight: 800, mb: 1 }}>
            Needs attention
          </Typography>
          <Stack spacing={1}>
            {unresolvedLines.length > 0 && (
              <Alert severity="warning">
                {unresolvedLines.length} RFQ line
                {unresolvedLines.length === 1 ? "" : "s"} still require sourcing
                or review.
              </Alert>
            )}
            {failedSolicitations.length > 0 && (
              <Alert severity="error">
                {failedSolicitations.length} supplier delivery attempt
                {failedSolicitations.length === 1 ? "" : "s"} failed. Review the
                error evidence and retry.
              </Alert>
            )}
            {blockedOffers.length > 0 && (
              <Alert severity="info">
                {blockedOffers.length} supplier offer
                {blockedOffers.length === 1 ? "" : "s"} cannot be awarded until
                missing commercial evidence is resolved.
              </Alert>
            )}
          </Stack>
        </Paper>
      )}

      {referenceDataFailed && (
        <Alert
          severity="error"
          sx={{ mb: 2 }}
          action={
            <Button
              startIcon={<Refresh />}
              onClick={() =>
                referenceQueries
                  .filter((query) => query.isError)
                  .forEach((query) => query.refetch())
              }
            >
              Retry
            </Button>
          }
        >
          Required currency or warehouse reference data could not
          be loaded. Related actions are unavailable until it is restored.
        </Alert>
      )}

      <Paper variant="outlined" sx={{ mb: 2, overflow: "hidden" }}>
        <Tabs
          value={tab}
          onChange={(_, value) => setTab(value)}
          variant="scrollable"
          scrollButtons="auto"
        >
          <Tab label={`Coverage (${workbench.lines.length})`} />
          <Tab label={`Solicitations (${workbench.solicitations.length})`} />
          <Tab label={`Supplier offers (${workbench.offers.length})`} />
          <Tab label={`Purchase orders (${workbench.purchaseOrders.length})`} />
        </Tabs>
      </Paper>

      {tab === 0 && (
        <DataTable empty="No RFQ lines are available.">
          <TableHead>
            <TableRow>
              <TableCell>Part / description</TableCell>
              <TableCell align="right">Requested</TableCell>
              <TableCell align="right">Available</TableCell>
              <TableCell align="right">Reserved</TableCell>
              <TableCell align="right">Shortfall</TableCell>
              <TableCell align="right">Still to source</TableCell>
              <TableCell>Resolution</TableCell>
              <TableCell>Checked</TableCell>
              <TableCell align="right">Action</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {workbench.lines.map((line) => (
              <TableRow key={line.id}>
                <TableCell>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>
                    {line.partNumber || "Unresolved part"}
                  </Typography>
                  <Typography variant="caption" color="text.secondary">
                    {line.description}
                  </Typography>
                </TableCell>
                <TableCell align="right">{line.requestedQuantity}</TableCell>
                <TableCell align="right">{line.availableQuantity}</TableCell>
                <TableCell align="right">{line.reservedQuantity}</TableCell>
                <TableCell align="right">
                  <Typography
                    color={
                      line.shortfallQuantity > 0 ? "error.main" : "success.main"
                    }
                    sx={{ fontWeight: 800 }}
                  >
                    {line.shortfallQuantity}
                  </Typography>
                </TableCell>
                <TableCell align="right">
                  <Typography
                    color={
                      remainingRequirement(line.id) > 0
                        ? "error.main"
                        : "success.main"
                    }
                    sx={{ fontWeight: 800 }}
                  >
                    {remainingRequirement(line.id)}
                  </Typography>
                </TableCell>
                <TableCell>
                  <ResolutionChip resolution={line.resolution} />
                </TableCell>
                <TableCell>
                  {line.resolutionCheckedOn
                    ? new Date(line.resolutionCheckedOn).toLocaleString()
                    : "Not verified"}
                </TableCell>
                <TableCell align="right">
                  <Stack direction="row" spacing={0.5} sx={{ justifyContent: "flex-end" }}>
                    <Button size="small" startIcon={<Insights />} onClick={() => setMemoryLineId(line.id)}>Memory</Button>
                    {line.shortfallQuantity > 0 && canSolicit && (
                      <Button size="small" variant="outlined" onClick={() => openSourcingCase.mutate(line)}>
                        {line.sourcingCaseId ? "Open case" : "Create case"}
                      </Button>
                    )}
                  </Stack>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </DataTable>
      )}

      {tab === 1 && (
        <DataTable empty="No supplier solicitations have been created.">
          <TableHead>
            <TableRow>
              <TableCell>Supplier</TableCell>
              <TableCell>Status</TableCell>
              <TableCell>Delivery evidence</TableCell>
              <TableCell>Attempts</TableCell>
              <TableCell>Updated</TableCell>
              <TableCell align="right">Action</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {workbench.solicitations.map((item) => (
              <TableRow key={item.id}>
                <TableCell>
                  <Typography sx={{ fontWeight: 700 }}>
                    {item.supplierName}
                  </Typography>
                  <Typography variant="caption" color="text.secondary">
                    {item.supplierEmail || "No email on record"}
                  </Typography>
                </TableCell>
                <TableCell>
                  <Chip
                    size="small"
                    label={item.status.replaceAll("_", " ")}
                    color={statusColor(item.status)}
                  />
                </TableCell>
                <TableCell>
                  <Typography variant="body2">
                    {item.providerReference || "Awaiting provider confirmation"}
                  </Typography>
                  {item.lastErrorCode && (
                    <Typography variant="caption" color="error">
                      {item.lastErrorCode}
                    </Typography>
                  )}
                </TableCell>
                <TableCell>{item.attemptCount}</TableCell>
                <TableCell>
                  {new Date(item.updatedOn).toLocaleString()}
                </TableCell>
                <TableCell align="right">
                  <Stack
                    direction="row"
                    spacing={1}
                    sx={{ justifyContent: "flex-end" }}
                  >
                    {canSolicit && isRetryable(item.status) && (
                      <Button
                        size="small"
                        startIcon={<Replay />}
                        disabled={retryMutation.isPending}
                        onClick={() => retryMutation.mutate(item.id)}
                      >
                        Retry
                      </Button>
                    )}
                    <Button
                      size="small"
                      onClick={() => setResponseSolicitation(item)}
                      sx={{ display: canCapture ? "inline-flex" : "none" }}
                      disabled={!["SENT", "RESPONDED"].includes(
                        item.status.replaceAll("_", "").toUpperCase(),
                      )}
                    >
                      Capture response
                    </Button>
                  </Stack>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </DataTable>
      )}

      {tab === 2 && (
        <Stack spacing={2}>
          {comparisonsQuery.isError && (
            <Alert
              severity="error"
              action={
                <Button onClick={() => comparisonsQuery.refetch()}>
                  Retry
                </Button>
              }
            >
              Authoritative supplier comparison is unavailable. Awards are
              blocked until eligibility can be verified.
            </Alert>
          )}
          <DataTable empty="No structured supplier offers have been captured.">
            <TableHead>
              <TableRow>
                <TableCell>Supplier / reference</TableCell>
                <TableCell>RFQ line</TableCell>
                <TableCell>Tier</TableCell>
                <TableCell align="right">Available</TableCell>
                <TableCell align="right">Unit price</TableCell>
                <TableCell align="right">Landed cost</TableCell>
                <TableCell align="right">Lead time</TableCell>
                <TableCell>Warranty</TableCell>
                <TableCell>Payment terms</TableCell>
                <TableCell align="right">Reliability</TableCell>
                <TableCell align="right">Weighted score</TableCell>
                <TableCell>How the score is made up</TableCell>
                <TableCell>Evidence</TableCell>
                <TableCell align="right">Still to source</TableCell>
                <TableCell align="right">Decision</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {orderedOffers.map((offer) => {
                const comparison = comparisonsQuery.data?.[offer.rfqItemId];
                const authoritativeOffer = comparison?.lines.find(
                  (line) => line.supplierQuotedItemId === offer.id,
                );
                const scoreState = offerScoreState(authoritativeOffer);
                const warrantyCell = warrantyComparisonCell(authoritativeOffer);
                const isRecommended =
                  comparison?.recommendedSupplierQuotedItemId === offer.id;
                const ranking = scoreRanks.get(offer.id);
                const cheapest = cheapestEligible(comparison);
                const isCheapest =
                  cheapest?.supplierQuotedItemId === offer.id;
                const award = workbench.awards.find((item) => item.supplierQuotedItemId === offer.id);
                const quoteLine = workbench.customerQuoteDraft?.lines.find((item) => item.rfqItemId === offer.rfqItemId);
                return (
                  <TableRow key={offer.id}>
                  <TableCell>
                    <Typography sx={{ fontWeight: 700 }}>
                      {offer.supplierName}
                    </Typography>
                    <Typography variant="caption">
                      {offer.quoteReference || "No supplier reference"} · Rev{" "}
                      {offer.quoteRevision}
                    </Typography>
                  </TableCell>
                  <TableCell>#{offer.rfqItemId}</TableCell>
                  {/* Tier annotates and orders; it never gates. It is shown as plain text, apart
                      from the eligibility chips, so it cannot be read as an approval. */}
                  <TableCell>
                    <Typography variant="caption">
                      {supplierTierLabel(authoritativeOffer?.supplierTier)}
                    </Typography>
                  </TableCell>
                  <TableCell align="right">
                    {offer.availableQuantity ?? "Unknown"}
                  </TableCell>
                  <TableCell align="right">
                    {money(offer.unitPrice, offer.currencyCode)}
                  </TableCell>
                  <TableCell align="right">
                    {authoritativeOffer?.landedUnitCost == null
                      ? "Incomplete"
                      : money(
                          authoritativeOffer.landedUnitCost,
                          offer.currencyCode,
                        )}
                  </TableCell>
                  <TableCell align="right">
                    {authoritativeOffer?.leadTimeDays == null
                      ? "Unknown"
                      : `${authoritativeOffer.leadTimeDays} days`}
                  </TableCell>
                  {/* The captured period leads, because it is the number the warranty points were
                      computed from and the buyer has to be able to check the row against it. The
                      supplier's wording stays underneath rather than being replaced — it carries
                      the conditions the period does not. With no period captured the wording still
                      leads, and the line beneath it says the number is missing so the row agrees
                      with its own score column instead of contradicting it. */}
                  <TableCell>
                    <Typography variant="caption" sx={{ display: "block" }}>
                      {warrantyCell.headline}
                    </Typography>
                    {warrantyCell.detail && (
                      <Typography
                        variant="caption"
                        color="text.secondary"
                        sx={{ display: "block" }}
                      >
                        {warrantyCell.detail}
                      </Typography>
                    )}
                  </TableCell>
                  <TableCell>
                    <Typography variant="caption" sx={{ display: "block" }}>
                      {authoritativeOffer?.paymentTerms || "Not stated"}
                    </Typography>
                    {/* Blank is not zero: an uncaptured credit term means payment terms cannot be
                        scored for this supplier, and saying "0 days" would invent an agreement. */}
                    <Typography variant="caption" color="text.secondary">
                      {authoritativeOffer?.creditDays == null
                        ? "No credit days captured"
                        : `${authoritativeOffer.creditDays} credit days`}
                    </Typography>
                  </TableCell>
                  {/* Kept as a display-only column. It is an operator-typed spreadsheet value, not
                      a measured outcome, so it carries no weight in the score. */}
                  <TableCell align="right">
                    {authoritativeOffer?.reliability == null
                      ? "Unknown"
                      : `${authoritativeOffer.reliability}%`}
                  </TableCell>
                  <TableCell align="right">
                    {scoreState.status === "SCORED" ? (
                      <Stack spacing={0.25} sx={{ alignItems: "flex-end" }}>
                        <Typography sx={{ fontWeight: 800 }}>
                          {scoreState.headline}
                        </Typography>
                        {ranking && (
                          <Typography variant="caption" color="text.secondary">
                            Rank {ranking.rank} of {ranking.of}
                          </Typography>
                        )}
                      </Stack>
                    ) : (
                      // Two different silences, told apart. R-F: a missing value is never scored as
                      // zero, and that offer stays awardable — the Approve button is gated on
                      // eligibility alone, never on the score. A BLOCKED offer is also unscored,
                      // but for the opposite reason, and it is never dressed as the first case.
                      <Typography
                        variant="caption"
                        color={
                          scoreState.status === "NOT_SCORED"
                            ? "warning.main"
                            : "text.secondary"
                        }
                        sx={{ fontWeight: scoreState.status === "PENDING" ? 400 : 700 }}
                      >
                        {scoreState.headline}
                      </Typography>
                    )}
                  </TableCell>
                  {/* Every criterion's raw value AND the points it earned, in the row and not
                      behind a hover: a score a buyer cannot add up is a black box, and the last
                      line of this cell is the sum they can check. */}
                  <TableCell>
                    {scoreState.status !== "SCORED" || !authoritativeOffer ? (
                      <Typography
                        variant="caption"
                        color={
                          scoreState.status === "BLOCKED"
                            ? "warning.main"
                            : "text.secondary"
                        }
                      >
                        {scoreState.detail}
                      </Typography>
                    ) : (
                      <Stack spacing={0.25}>
                        {(authoritativeOffer.scoreBreakdown ?? []).map((criterion) => (
                          <Typography key={criterion.criterion} variant="caption">
                            <Box component="span" sx={{ fontWeight: 700 }}>
                              {criterion.label}
                            </Box>{" "}
                            {criterionRawValue(criterion, offer.currencyCode)} ·{" "}
                            {criterion.pointsEarned == null
                              ? "no points"
                              : points(criterion.pointsEarned)}{" "}
                            of {criterion.weight}
                          </Typography>
                        ))}
                        <Typography
                          variant="caption"
                          sx={{
                            fontWeight: 800,
                            borderTop: "1px solid",
                            borderColor: "divider",
                            pt: 0.25,
                          }}
                        >
                          Total {points(scoreState.score)} of 100
                        </Typography>
                      </Stack>
                    )}
                  </TableCell>
                  <TableCell>
                    {!authoritativeOffer ? (
                      <Chip size="small" label="Checking eligibility" />
                    ) : authoritativeOffer.blockers.length > 0 ? (
                      authoritativeOffer.blockers.map((reason) => (
                        <Chip
                          key={reason}
                          size="small"
                          color="warning"
                          label={reason}
                          sx={{ mr: 0.5, mb: 0.5 }}
                        />
                      ))
                    ) : (
                      <Chip size="small" color="success" label="Complete" />
                    )}
                    {/* Cost-completeness warnings are separate from blockers: the offer is
                        awardable, but its landed cost looks incomplete — an EXW or FOB quote
                        recording no customs duty, for example. Rendered here rather than hidden
                        behind a tooltip, because an underpriced offer wins the comparison on
                        landed cost and nothing else on this screen would say why. */}
                    {(authoritativeOffer?.costWarnings ?? []).map((warning) => (
                      <Tooltip key={warning} title={warning}>
                        <Chip
                          size="small"
                          color="warning"
                          variant="outlined"
                          icon={<WarningAmber />}
                          label="Cost may be incomplete"
                          sx={{ mr: 0.5, mb: 0.5 }}
                        />
                      </Tooltip>
                    ))}
                  </TableCell>
                  <TableCell align="right">
                    {remainingRequirement(offer.rfqItemId)}
                  </TableCell>
                  <TableCell align="right">
                    <Stack spacing={0.5} sx={{ alignItems: "flex-end" }}>
                      {/* The score chip sits BESIDE the landed-cost fact and never replaces it.
                          When the recommendation is not the cheapest offer, it says what the
                          preference cost and what it bought, and the plain "Lowest landed cost"
                          chip stays on the offer that actually is cheapest. */}
                      {isRecommended && authoritativeOffer?.weightedScore != null ? (
                        <Chip
                          size="small"
                          color="info"
                          sx={{
                            height: "auto",
                            "& .MuiChip-label": {
                              whiteSpace: "normal",
                              display: "block",
                              py: 0.5,
                              textAlign: "right",
                            },
                          }}
                          label={`Best weighted score ${points(authoritativeOffer.weightedScore)} — ${recommendationTradeOff(
                            authoritativeOffer,
                            cheapest,
                            (value) => money(value, offer.currencyCode),
                          )}`}
                        />
                      ) : (
                        isRecommended && (
                          <Chip
                            size="small"
                            color="info"
                            label="Lowest eligible landed cost"
                          />
                        )
                      )}
                      {isCheapest && !isRecommended && (
                        <Chip
                          size="small"
                          variant="outlined"
                          label="Lowest landed cost"
                        />
                      )}
                      <Button
                        size="small"
                        variant="contained"
                        startIcon={<AssignmentTurnedIn />}
                        disabled={
                          !authoritativeOffer?.eligible ||
                          remainingRequirement(offer.rfqItemId) <= 0
                        }
                        sx={{ display: canAward ? "inline-flex" : "none" }}
                        onClick={() => setAwardOffer(offer)}
                      >
                        {remainingRequirement(offer.rfqItemId) <= 0
                          ? "Covered"
                          : offer.awarded
                            ? "Award more"
                            : "Approve"}
                      </Button>
                      {award && quoteLine && canAward && hasPermission("Quotations", "edit") && (
                        <Button size="small" startIcon={<PriceCheck />} onClick={() => setPricingSelection({
                          awardId: award.id, quoteItemId: quoteLine.quoteItemId,
                          landedUnitCost: award.landedUnitCost, currencyCode: award.currencyCode,
                        })}>Price customer quote</Button>
                      )}
                    </Stack>
                  </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </DataTable>
          {canCreatePo && approvedUnconverted.length > 0 && (
            <Box sx={{ display: "flex", justifyContent: "flex-end" }}>
              <Button
                variant="contained"
                color="success"
                startIcon={<ShoppingCartCheckout />}
                onClick={() => setPoOpen(true)}
              >
                Create supplier PO
              </Button>
            </Box>
          )}
        </Stack>
      )}

      {tab === 3 && (
        <Stack spacing={2}>
          {workbench.purchaseOrders.length === 0 ? (
            <Paper variant="outlined" sx={{ p: 5, textAlign: "center" }}>
              <Inventory2 color="disabled" />
              <Typography color="text.secondary">
                No authoritative supplier purchase orders exist yet.
              </Typography>
            </Paper>
          ) : (
            workbench.purchaseOrders.map((order) => (
              <Paper
                key={order.id}
                variant="outlined"
                sx={{ overflow: "hidden" }}
              >
                <Stack
                  direction={{ xs: "column", md: "row" }}
                  spacing={1}
                  sx={{
                    p: 2,
                    bgcolor: "action.hover",
                    justifyContent: "space-between",
                  }}
                >
                  <Box>
                    <Stack
                      direction="row"
                      spacing={1}
                      sx={{ alignItems: "center" }}
                    >
                      <Typography sx={{ fontWeight: 800 }}>
                        {order.purchaseOrderNumber}
                      </Typography>
                      <Chip
                        size="small"
                        label={order.status.replaceAll("_", " ")}
                        color={statusColor(order.status)}
                      />
                      {/*
                        FR-SPO-03. A countered or rejected order keeps the status it had — by
                        design, because neither is agreement. That makes this chip the only signal
                        on the card that the supplier has answered at all, so it sits beside the
                        status rather than below the fold.
                      */}
                      {order.acknowledgementStatus && (
                        <Chip
                          size="small"
                          variant="outlined"
                          label={`Supplier ${order.acknowledgementStatus.toLowerCase()}`}
                          color={acknowledgementColor(
                            order.acknowledgementStatus,
                          )}
                        />
                      )}
                    </Stack>
                    <Typography variant="body2" color="text.secondary">
                      {order.supplierName} ·{" "}
                      {money(order.totalValue, order.currencyCode)}
                    </Typography>
                    {/*
                      FR-SPO-01. Who authorised the spend, shown on the order rather than buried in
                      an audit log — it is the fact a second buyer needs before releasing it.
                    */}
                    {order.approvedBy && (
                      <Typography
                        variant="caption"
                        color="text.secondary"
                        sx={{ display: "block" }}
                      >
                        Approved by {order.approvedBy}
                        {order.approvedOn
                          ? ` · ${new Date(order.approvedOn).toLocaleString()}`
                          : ""}
                      </Typography>
                    )}
                    {/*
                      FR-SPO-06. The shipping terms a customs broker asks for first. Shown on the
                      card because an order whose Incoterm is blank cannot be cleared, and that is
                      worth noticing before dispatch rather than at the border.
                    */}
                    {(order.incoterm ||
                      order.portOfLoading ||
                      order.portOfDischarge) && (
                      <Typography
                        variant="caption"
                        color="text.secondary"
                        sx={{ display: "block" }}
                      >
                        {[
                          order.incoterm,
                          order.portOfLoading || order.portOfDischarge
                            ? `${order.portOfLoading ?? "—"} → ${order.portOfDischarge ?? "—"}`
                            : null,
                        ]
                          .filter(Boolean)
                          .join(" · ")}
                      </Typography>
                    )}
                  </Box>
                  <Stack
                    direction="row"
                    spacing={1}
                    sx={{ flexWrap: "wrap", rowGap: 1 }}
                  >
                    {order.status === "DRAFT" && canIssuePo && (
                      <Button
                        startIcon={<HowToReg />}
                        onClick={() => setApproveOrder(order)}
                      >
                        Approve PO
                      </Button>
                    )}
                    {/*
                      FR-SPO-06. Terms are editable exactly while the order is ours — DRAFT or
                      APPROVED. After dispatch the server refuses, because the Incoterm the supplier
                      holds and the Incoterm we hold must not diverge silently.
                    */}
                    {["DRAFT", "APPROVED"].includes(order.status) &&
                      canIssuePo && (
                        <Button
                          startIcon={<Public />}
                          onClick={() => setTradeTermsOrder(order)}
                        >
                          Trade terms
                        </Button>
                      )}
                    {order.status === "APPROVED" && canIssuePo && (
                      <Button
                        startIcon={<Send />}
                        onClick={() => setIssueOrder(order)}
                      >
                        Issue PO
                      </Button>
                    )}
                    {/*
                      FR-SPO-03. Offered only on an order that has actually reached the supplier
                      (SENT, or the legacy ISSUED that conflated approved with sent) AND does not
                      already carry an answer. An order that has been answered is refused by the
                      server with a 409, so leaving the button live was a dead end that told the
                      buyer nothing until they had already clicked it.
                    */}
                    {["SENT", "ISSUED"].includes(order.status) &&
                      !order.acknowledgementStatus &&
                      canIssuePo && (
                        <Button
                          startIcon={<AssignmentTurnedIn />}
                          onClick={() => setAcknowledgeOrder(order)}
                        >
                          Record supplier answer
                        </Button>
                      )}
                    <Button
                      startIcon={<LocalShipping />}
                      disabled={
                        !canReceive ||
                        // Mirrors SupplierPurchaseOrderStatuses.OpenForReceipt. ACKNOWLEDGED and
                        // the states beyond it were missing, so a supplier accepting the order was
                        // the very act that hid the receipt button for it. SENT is here for the
                        // same reason: release writes SENT, ISSUED is the legacy word for the same
                        // fact, and omitting either hides the button for a real dispatched order.
                        ![
                          "SENT",
                          "ISSUED",
                          "ACKNOWLEDGED",
                          "IN_PRODUCTION",
                          "SHIPPED",
                          "PARTIALLY_RECEIVED",
                        ].includes(order.status)
                      }
                      onClick={() => setReceiptOrder(order)}
                    >
                      Record receipt
                    </Button>
                  </Stack>
                </Stack>
                <SupplierAnswer order={order} />
                <Box sx={{ overflowX: "auto" }}>
                  <Table size="small" sx={{ minWidth: 760 }}>
                    <TableHead>
                      <TableRow>
                        <TableCell>Description</TableCell>
                        <TableCell align="right">Ordered</TableCell>
                        <TableCell align="right">Received</TableCell>
                        <TableCell align="right">Open</TableCell>
                        <TableCell align="right">Landed unit cost</TableCell>
                        {/* FR-SPO-06. What the customs declaration is built from. */}
                        <TableCell>HS code</TableCell>
                        <TableCell>Country of origin</TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {order.lines.map((line) => (
                        <TableRow key={line.id}>
                          <TableCell>{line.description}</TableCell>
                          <TableCell align="right">
                            {line.orderedQuantity}
                          </TableCell>
                          <TableCell align="right">
                            {line.receivedQuantity}
                          </TableCell>
                          <TableCell align="right">
                            {line.openQuantity}
                          </TableCell>
                          <TableCell align="right">
                            {money(line.landedUnitCost, order.currencyCode)}
                          </TableCell>
                          {/*
                            Missing customs data is shown as missing rather than as an empty cell,
                            because "not captured" is the finding a buyer needs before the shipment
                            reaches a border.
                          */}
                          <TableCell>
                            {line.hsCode || (
                              <Typography
                                variant="caption"
                                color="warning.main"
                              >
                                Not set
                              </Typography>
                            )}
                          </TableCell>
                          <TableCell>
                            {line.countryOfOrigin || (
                              <Typography
                                variant="caption"
                                color="warning.main"
                              >
                                Not set
                              </Typography>
                            )}
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </Box>
                {/*
                  FR-MAS-01..05. Inbound supplier shipments hang off the supplier PO (decision R3),
                  so they belong on the order card rather than on a screen of their own. The panel
                  owns its own queries and dialogs — this page is already long enough.
                */}
                <InboundShipmentsPanel
                  purchaseOrderId={order.id}
                  purchaseOrderNumber={order.purchaseOrderNumber}
                  lines={order.lines.map((line) => ({
                    id: line.id,
                    description: line.description,
                    orderedQuantity: line.orderedQuantity,
                    receivedQuantity: line.receivedQuantity,
                  }))}
                  canEdit={canIssuePo}
                  canManagePorts={hasPermission("Products", "create")}
                />
              </Paper>
            ))
          )}
        </Stack>
      )}

      {responseSolicitation && (
        <ResponseDialog
          solicitation={responseSolicitation}
          lines={workbench.lines.filter((line) =>
            responseSolicitation.requestedRfqItemIds?.includes(line.id),
          )}
          currencies={currenciesQuery.data?.items ?? []}
          referenceDataError={currenciesQuery.isError}
          onRetryReferenceData={() => currenciesQuery.refetch()}
          onClose={() => setResponseSolicitation(null)}
          onSaved={() => {
            setResponseSolicitation(null);
            refresh();
            setTab(2);
          }}
        />
      )}
      {awardOffer && (
        <AwardDialog
          offer={awardOffer}
          remainingQuantity={remainingRequirement(awardOffer.rfqItemId)}
          onClose={() => setAwardOffer(null)}
          onSaved={() => {
            setAwardOffer(null);
            refresh();
          }}
        />
      )}
      {pricingSelection && (
        <CustomerPricingDialog selection={pricingSelection} onClose={() => setPricingSelection(null)}
          onSaved={() => { setPricingSelection(null); refresh(); }} />
      )}
      {memoryLineId && <CommercialMemoryDialog rfqItemId={memoryLineId} onClose={() => setMemoryLineId(null)} />}
      {poOpen && rfqId && (
        <PurchaseOrderDialog
          rfqId={rfqId}
          awards={approvedUnconverted}
          warehouses={warehousesQuery.data?.items ?? []}
          referenceDataError={warehousesQuery.isError}
          onRetryReferenceData={() => warehousesQuery.refetch()}
          onClose={() => setPoOpen(false)}
          onSaved={() => {
            setPoOpen(false);
            refresh();
            setTab(3);
          }}
        />
      )}
      {receiptOrder && (
        <ReceiptDialog
          order={receiptOrder}
          warehouses={warehousesQuery.data?.items ?? []}
          referenceDataError={warehousesQuery.isError}
          onRetryReferenceData={() => warehousesQuery.refetch()}
          onClose={() => setReceiptOrder(null)}
          onSaved={() => {
            setReceiptOrder(null);
            refresh();
          }}
        />
      )}
      {approveOrder && (
        <ApprovePurchaseOrderDialog
          order={approveOrder}
          onClose={() => setApproveOrder(null)}
          onSaved={() => {
            setApproveOrder(null);
            refresh();
          }}
        />
      )}
      {issueOrder && (
        <IssuePurchaseOrderDialog
          order={issueOrder}
          onClose={() => setIssueOrder(null)}
          onSaved={() => {
            setIssueOrder(null);
            refresh();
          }}
        />
      )}
      {acknowledgeOrder && (
        <AcknowledgePurchaseOrderDialog
          order={acknowledgeOrder}
          onClose={() => setAcknowledgeOrder(null)}
          onSaved={() => {
            setAcknowledgeOrder(null);
            refresh();
          }}
        />
      )}
      {tradeTermsOrder && (
        <TradeTermsDialog
          order={tradeTermsOrder}
          onClose={() => setTradeTermsOrder(null)}
          onSaved={() => {
            setTradeTermsOrder(null);
            refresh();
          }}
        />
      )}
    </Box>
  );
}

function DataTable({
  children,
  empty,
}: {
  children: React.ReactNode;
  empty: string;
}) {
  const body = (children as any[])?.[1];
  const count = body?.props?.children?.length ?? 0;
  return (
    <Paper variant="outlined" sx={{ overflowX: "auto" }}>
      <Table size="small" sx={{ minWidth: 900 }}>
        {children}
      </Table>
      {count === 0 && (
        <Box sx={{ p: 5, textAlign: "center" }}>
          <Typography color="text.secondary">{empty}</Typography>
        </Box>
      )}
    </Paper>
  );
}

type ResponseLineForm = {
  currencyId: number;
  quantity: number;
  unitPrice: number;
  availableQuantity: number;
  leadTimeDays: number;
  reliabilitySnapshot: number;
  freightCost: number;
  dutyCost: number;
  otherCost: number;
  taxAmount: number;
  discountAmount: number;
  /**
   * Held as raw text, alone among these fields, because every other value here is coerced to a
   * number and defaults to 0 — and 0 months is a supplier who offered no warranty, which is a
   * different statement from nobody having recorded one. An empty string is the only way this form
   * can say "not captured", and it is what an untouched field must stay.
   */
  warrantyMonths: string;
  minimumOrderQuantity: number;
};

const responseLineDefaults = (): ResponseLineForm => ({
  currencyId: 0,
  quantity: 0,
  unitPrice: 0,
  availableQuantity: 0,
  leadTimeDays: 0,
  reliabilitySnapshot: 0,
  freightCost: 0,
  dutyCost: 0,
  otherCost: 0,
  taxAmount: 0,
  discountAmount: 0,
  // An RFQ line arrives with no warranty recorded against it, and that is exactly what the field
  // must show: blank. Routed through the same hydration the rest of the app uses so a future
  // caller that does have a value cannot render a null as 0.
  warrantyMonths: warrantyMonthsFieldValue(null),
  minimumOrderQuantity: 0,
});

function ResponseDialog({
  solicitation,
  lines,
  currencies,
  referenceDataError,
  onRetryReferenceData,
  onClose,
  onSaved,
}: any) {
  const [rfqItemId, setRfqItemId] = useState<number>(lines[0]?.id ?? 0);
  const [form, setForm] = useState({
    quoteReference: "",
    quoteRevision: 1,
    validUntil: "",
  });
  const [lineForms, setLineForms] = useState<Record<number, ResponseLineForm>>(
    () =>
      Object.fromEntries(
        lines.map((line: any) => [line.id, responseLineDefaults()]),
      ),
  );
  const [includedLineIds, setIncludedLineIds] = useState<number[]>(
    () => lines.map((line: any) => line.id),
  );
  const [idempotencyKey] = useState(() =>
    commandKey(`response:${solicitation.id}`),
  );
  const set = (field: string) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm((current) => ({
      ...current,
      [field]:
        e.target.type === "number" ? number(e.target.value) : e.target.value,
    }));
  const selectedForm = lineForms[rfqItemId] ?? responseLineDefaults();
  const setLine =
    (field: Exclude<keyof ResponseLineForm, "warrantyMonths">) =>
    (event: { target: { value: unknown } }) =>
      setLineForms((current) => ({
        ...current,
        [rfqItemId]: {
          ...(current[rfqItemId] ?? responseLineDefaults()),
          [field]: number(event.target.value),
        },
      }));
  // Kept apart from setLine so the typed warranty is never pushed through number(), which turns an
  // empty field into 0 and a half-typed "-" into NaN.
  const setLineWarrantyMonths = (event: { target: { value: string } }) =>
    setLineForms((current) => ({
      ...current,
      [rfqItemId]: {
        ...(current[rfqItemId] ?? responseLineDefaults()),
        warrantyMonths: event.target.value,
      },
    }));
  const warrantyMonths = parseWarrantyMonthsInput(selectedForm.warrantyMonths);
  const includedLines = lines.filter((line: any) =>
    includedLineIds.includes(line.id),
  );
  const allLinesValid = includedLines.every((line: any) => {
    const value = lineForms[line.id];
    return (
      value?.currencyId > 0 &&
      value.quantity > 0 &&
      value.unitPrice > 0 &&
      value.leadTimeDays > 0 &&
      value.reliabilitySnapshot > 0 &&
      value.reliabilitySnapshot <= 100 &&
      value.freightCost >= 0 &&
      value.dutyCost >= 0 &&
      value.otherCost >= 0 &&
      value.taxAmount >= 0 &&
      value.discountAmount >= 0 &&
      value.minimumOrderQuantity >= 0 &&
      // Blank passes: a warranty nobody captured is a valid response. Only a value the field
      // refuses — negative, fractional, or beyond the accepted ceiling — blocks the save, and it
      // is checked on every included line so a bad value cannot hide on an unselected tab.
      parseWarrantyMonthsInput(value.warrantyMonths).error === null
    );
  });
  const mutation = useMutation({
    mutationFn: () =>
      procurementService.captureSupplierResponse(solicitation.id, {
        quoteReference: form.quoteReference,
        quoteRevision: form.quoteRevision,
        validUntil: form.validUntil,
        lines: includedLines.map((line: any) => {
          const value = lineForms[line.id];
          return {
            rfqItemId: line.id,
            productId: line.productId ?? null,
            quantity: value.quantity,
            unitPrice: value.unitPrice,
            currencyId: value.currencyId,
            availableQuantity: value.availableQuantity || undefined,
            leadTimeDays: value.leadTimeDays || undefined,
            reliabilitySnapshot: value.reliabilitySnapshot || undefined,
            freightCost: value.freightCost,
            dutyCost: value.dutyCost,
            otherCost: value.otherCost,
            taxAmount: value.taxAmount,
            discountAmount: value.discountAmount,
            minimumOrderQuantity: value.minimumOrderQuantity || undefined,
            // Explicitly null rather than dropped when blank: the line is recorded as having no
            // captured warranty, which is what the comparison needs in order to say so.
            warrantyMonths: parseWarrantyMonthsInput(value.warrantyMonths).value,
          };
        }),
      }, idempotencyKey),
    onSuccess: () => {
      toast.success("Structured supplier response captured");
      onSaved();
    },
    onError: (error) =>
      toast.error(errorMessage(error, "Could not capture the response")),
  });
  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="md">
      <DialogTitle>Capture response · {solicitation.supplierName}</DialogTitle>
      <DialogContent>
        {lines.length === 0 && (
          <Alert severity="error" sx={{ mb: 2 }}>
            This solicitation has no verified RFQ line linkage. Reload after the
            server contract is upgraded; no response can be captured safely.
          </Alert>
        )}
        {referenceDataError && (
          <Alert
            severity="error"
            sx={{ mb: 2 }}
            action={<Button onClick={onRetryReferenceData}>Retry</Button>}
          >
            Currencies could not be loaded.
          </Alert>
        )}
        <Grid container spacing={2} sx={{ mt: 0 }}>
          <Grid size={{ xs: 12, md: 6 }}>
            <TextField
              fullWidth
              required
              label="Supplier quote reference"
              value={form.quoteReference}
              onChange={set("quoteReference")}
            />
          </Grid>
          <Grid size={{ xs: 6, md: 3 }}>
            <TextField
              fullWidth
              type="number"
              label="Revision"
              value={form.quoteRevision}
              onChange={set("quoteRevision")}
            />
          </Grid>
          <Grid size={{ xs: 6, md: 3 }}>
            <FormControl fullWidth>
              <InputLabel>Currency</InputLabel>
              <Select
                label="Currency"
                value={selectedForm.currencyId || ""}
                onChange={(event) =>
                  setLine("currencyId")(event)
                }
              >
                {currencies
                  .filter((currency: CurrencyDTO) => currency.isActive)
                  .map((currency: CurrencyDTO) => (
                    <MenuItem key={currency.id} value={currency.id}>
                      {currency.code} · {currency.currencyName}
                    </MenuItem>
                  ))}
              </Select>
            </FormControl>
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <FormControl fullWidth>
              <InputLabel>RFQ line</InputLabel>
              <Select
                label="RFQ line"
                value={rfqItemId}
                onChange={(e) => setRfqItemId(number(e.target.value))}
              >
                {lines.map((line: any) => (
                  <MenuItem key={line.id} value={line.id}>
                    {line.partNumber || `Line ${line.id}`} · {line.description}
                    {includedLineIds.includes(line.id)
                      ? lineForms[line.id]?.unitPrice > 0
                        ? " · Included"
                        : " · Included, incomplete"
                      : " · Not quoted"}
                  </MenuItem>
                ))}
              </Select>
            </FormControl>
          </Grid>
          <Grid size={{ xs: 12 }}>
            <Alert severity="info">
              Select only the lines present in this supplier response. Values
              are required only for included lines and are saved atomically.
            </Alert>
          </Grid>
          <Grid size={{ xs: 12 }}>
            <Stack spacing={0.5}>
              {lines.map((line: any) => (
                <Stack
                  key={line.id}
                  direction="row"
                  sx={{ alignItems: "center" }}
                >
                  <Checkbox
                    slotProps={{
                      input: {
                        "aria-label": `Include ${line.partNumber || `RFQ line ${line.id}`}`,
                      },
                    }}
                    checked={includedLineIds.includes(line.id)}
                    onChange={(event) =>
                      setIncludedLineIds((current) =>
                        event.target.checked
                          ? [...current, line.id]
                          : current.filter((id) => id !== line.id),
                      )
                    }
                  />
                  <Typography variant="body2">
                    {line.partNumber || `RFQ line ${line.id}`} · {line.description}
                  </Typography>
                </Stack>
              ))}
            </Stack>
          </Grid>
          <Grid size={{ xs: 6, md: 3 }}>
            <TextField
              fullWidth
              type="number"
              label="Quoted quantity"
              value={selectedForm.quantity}
              onChange={setLine("quantity")}
            />
          </Grid>
          <Grid size={{ xs: 6, md: 3 }}>
            <TextField
              fullWidth
              type="number"
              label="Available quantity"
              value={selectedForm.availableQuantity}
              onChange={setLine("availableQuantity")}
            />
          </Grid>
          <Grid size={{ xs: 6, md: 3 }}>
            <TextField
              fullWidth
              type="number"
              label="Unit price"
              value={selectedForm.unitPrice}
              onChange={setLine("unitPrice")}
            />
          </Grid>
          <Grid size={{ xs: 6, md: 3 }}>
            <TextField
              fullWidth
              type="number"
              label="Lead time (days)"
              value={selectedForm.leadTimeDays}
              onChange={setLine("leadTimeDays")}
            />
          </Grid>
          <Grid size={{ xs: 6, md: 3 }}>
            <TextField
              fullWidth
              type="number"
              label="Warranty (months)"
              value={selectedForm.warrantyMonths}
              onChange={setLineWarrantyMonths}
              error={Boolean(warrantyMonths.error)}
              slotProps={{ htmlInput: { min: 0 } }}
              // The same sentence the Supplier Quote inbox shows over the same field, plus the one
              // clause that is only true here: this command carries the period and not the wording.
              helperText={
                warrantyMonths.error ??
                `${WARRANTY_MONTHS_HELPER} ${WARRANTY_WORDING_NOT_CAPTURED_HERE}`
              }
            />
          </Grid>
          <Grid size={{ xs: 6, md: 3 }}>
            <TextField
              fullWidth
              type="number"
              slotProps={{ htmlInput: { min: 0, max: 100 } }}
              label="Supplier reliability (%)"
              value={selectedForm.reliabilitySnapshot}
              onChange={setLine("reliabilitySnapshot")}
            />
          </Grid>
          <Grid size={{ xs: 4 }}>
            <TextField
              fullWidth
              type="number"
              label="Freight"
              value={selectedForm.freightCost}
              onChange={setLine("freightCost")}
            />
          </Grid>
          <Grid size={{ xs: 6, md: 3 }}>
            <TextField
              fullWidth
              type="number"
              label="Tax amount"
              value={selectedForm.taxAmount}
              onChange={setLine("taxAmount")}
              slotProps={{ htmlInput: { min: 0 } }}
            />
          </Grid>
          <Grid size={{ xs: 6, md: 3 }}>
            <TextField
              fullWidth
              type="number"
              label="Discount amount"
              value={selectedForm.discountAmount}
              onChange={setLine("discountAmount")}
              slotProps={{ htmlInput: { min: 0 } }}
            />
          </Grid>
          <Grid size={{ xs: 6, md: 3 }}>
            <TextField
              fullWidth
              type="number"
              label="Minimum order quantity"
              value={selectedForm.minimumOrderQuantity}
              onChange={setLine("minimumOrderQuantity")}
              slotProps={{ htmlInput: { min: 0 } }}
            />
          </Grid>
          <Grid size={{ xs: 4 }}>
            <TextField
              fullWidth
              type="number"
              label="Duty"
              value={selectedForm.dutyCost}
              onChange={setLine("dutyCost")}
            />
          </Grid>
          <Grid size={{ xs: 4 }}>
            <TextField
              fullWidth
              type="number"
              label="Other cost"
              value={selectedForm.otherCost}
              onChange={setLine("otherCost")}
            />
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <TextField
              fullWidth
              type="date"
              label="Valid until"
              value={form.validUntil}
              onChange={set("validUntil")}
              slotProps={{ inputLabel: { shrink: true } }}
            />
          </Grid>
        </Grid>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          disabled={
            !form.quoteReference ||
            !form.validUntil ||
            includedLines.length === 0 ||
            !allLinesValid ||
            referenceDataError ||
            mutation.isPending
          }
          onClick={() => mutation.mutate()}
        >
          Save response
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function AwardDialog({ offer, remainingQuantity, onClose, onSaved }: any) {
  const maximumQuantity = Math.min(
    remainingQuantity,
    offer.availableQuantity || offer.quantity,
  );
  const [quantity, setQuantity] = useState(
    maximumQuantity,
  );
  const [rationale, setRationale] = useState("Best eligible commercial offer");
  const [idempotencyKey] = useState(() => commandKey(`award:${offer.id}`));
  const mutation = useMutation({
    mutationFn: () =>
      procurementService.approveAward({
        supplierQuotedItemId: offer.id,
        quantity,
        rationale,
        expectedQuoteVersion: offer.version,
        idempotencyKey,
      }),
    onSuccess: () => {
      toast.success("Supplier offer approved");
      onSaved();
    },
    onError: (error) =>
      toast.error(errorMessage(error, "Could not approve the offer")),
  });
  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Approve supplier offer</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="warning">
            This records an authoritative commercial decision. The selected
            offer, cost, quantity, approver, and rationale are preserved.
          </Alert>
          <Typography>
            <strong>{offer.supplierName}</strong> ·{" "}
            {money(offer.landedUnitCost, offer.currencyCode)} landed unit cost
          </Typography>
          <TextField
            type="number"
            label="Award quantity"
            value={quantity}
            onChange={(e) => setQuantity(number(e.target.value))}
            helperText={`${remainingQuantity} remains to source; this offer can cover up to ${maximumQuantity}.`}
            slotProps={{ htmlInput: { min: 0, max: maximumQuantity } }}
            error={quantity > maximumQuantity}
          />
          <TextField
            multiline
            minRows={3}
            label="Decision rationale"
            value={rationale}
            onChange={(e) => setRationale(e.target.value)}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          disabled={
            quantity <= 0 ||
            quantity > maximumQuantity ||
            !rationale.trim() ||
            mutation.isPending
          }
          onClick={() => mutation.mutate()}
        >
          Approve award
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function CustomerPricingDialog({ selection, onClose, onSaved }: any) {
  const [margin, setMargin] = useState(20);
  const [rationale, setRationale] = useState("Approved supplier award and target margin");
  const [idempotencyKey] = useState(() => commandKey(`customer-pricing:${selection.awardId}`));
  const sellingPrice = margin >= 95 ? 0 : selection.landedUnitCost / (1 - margin / 100);
  const mutation = useMutation({
    mutationFn: () => procurementService.applyCustomerQuotePricing({
      quoteItemId: selection.quoteItemId,
      sourcingAwardId: selection.awardId,
      targetMarginPercent: margin,
      rationale,
      idempotencyKey,
    }),
    onSuccess: () => { toast.success("Customer Quote pricing updated with supplier lineage"); onSaved(); },
    onError: (error) => toast.error(errorMessage(error, "Could not update Customer Quote pricing")),
  });
  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Price Customer Quote line</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="info">Supplier cost remains confidential and is preserved separately from the customer selling price.</Alert>
          <Typography>Approved landed cost: <strong>{money(selection.landedUnitCost, selection.currencyCode)}</strong></Typography>
          <TextField type="number" label="Target margin (%)" value={margin}
            onChange={(event) => setMargin(number(event.target.value))}
            slotProps={{ htmlInput: { min: 0, max: 94.99, step: 0.25 } }}
            error={margin < 0 || margin >= 95} />
          <Typography>Calculated customer unit price: <strong>{money(sellingPrice, selection.currencyCode)}</strong></Typography>
          <TextField multiline minRows={3} label="Pricing rationale" value={rationale}
            onChange={(event) => setRationale(event.target.value)} />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={margin < 0 || margin >= 95 || !rationale.trim() || mutation.isPending}
          onClick={() => mutation.mutate()}>Apply pricing</Button>
      </DialogActions>
    </Dialog>
  );
}

function CommercialMemoryDialog({ rfqItemId, onClose }: { rfqItemId: number; onClose: () => void }) {
  const query = useQuery({ queryKey: ["commercial-memory-card", rfqItemId], queryFn: () => commercialLearningService.getLineCard(rfqItemId) });
  const card = query.data;
  return <Dialog open onClose={onClose} fullWidth maxWidth="md"><DialogTitle>Commercial memory</DialogTitle><DialogContent dividers>
    {query.isLoading && <Box sx={{ display: "grid", placeItems: "center", p: 4 }}><CircularProgress /></Box>}
    {query.isError && <Alert severity="error">Commercial evidence could not be loaded.</Alert>}
    {card && <Stack spacing={2}>
      <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}><Chip label={card.nexoraSerial} /><Typography color="text.secondary">RFQ line {card.rfqItemId}</Typography></Stack>
      {card.product ? <Box><Typography sx={{ fontWeight: 800 }}>{card.product.partNumber} · {card.product.productName}</Typography><Typography>{card.product.timesRequested} requests · {card.product.timesQuoted} quoted · {card.product.decidedCount} decided · {card.product.wonCount} won · {card.product.pendingCount} pending</Typography><Typography color="text.secondary">Evidence period {new Date(card.product.periodFrom).toLocaleDateString()} to {new Date(card.product.periodTo).toLocaleDateString()}</Typography></Box> : <Alert severity="warning">Resolve the Product identity to unlock Product commercial memory.</Alert>}
      {card.inventory && <Box><Typography sx={{ fontWeight: 700 }}>Demand evidence</Typography><Typography>Observed {card.inventory.observedDemand} · Quoted {card.inventory.quotedDemand} · Weighted {card.inventory.probabilityWeightedDemand} · Committed {card.inventory.committedDemand}</Typography><Typography color="text.secondary">{card.inventory.recommendation}</Typography></Box>}
      <Box><Typography sx={{ fontWeight: 700 }}>Supplier contribution</Typography>{card.suppliers.length === 0 ? <Typography color="text.secondary">No canonical Supplier offers are linked yet.</Typography> : card.suppliers.map((supplier) => <Typography key={supplier.supplierId}>{supplier.supplierName}: {supplier.quoteRevisions} revisions, {supplier.selectedOfferCount} selected, {supplier.supportedWonCount} supported wins</Typography>)}</Box>
      <Alert severity="info">{card.nextAction}</Alert>
    </Stack>}
  </DialogContent><DialogActions><Button onClick={onClose}>Close</Button></DialogActions></Dialog>;
}

function PurchaseOrderDialog({
  rfqId,
  awards,
  warehouses,
  referenceDataError,
  onRetryReferenceData,
  onClose,
  onSaved,
}: any) {
  const groups = useMemo(() => {
    const values = new Map<string, any[]>();
    for (const award of awards) {
      const key = `${award.supplierId}:${award.currencyId}`;
      values.set(key, [...(values.get(key) ?? []), award]);
    }
    return [...values.entries()];
  }, [awards]);
  const [groupKey, setGroupKey] = useState(groups[0]?.[0] ?? "");
  const [selectedAwardIds, setSelectedAwardIds] = useState<number[]>(
    groups[0]?.[1].map((award: any) => award.id) ?? [],
  );
  const [warehouseId, setWarehouseId] = useState(0);
  const [expectedOn, setExpectedOn] = useState("");
  const [incoterm, setIncoterm] = useState<string>("");
  const [portOfLoading, setPortOfLoading] = useState("");
  const [portOfDischarge, setPortOfDischarge] = useState("");
  const [idempotencyKey] = useState(() => commandKey(`po:${rfqId}`));
  const available = groups.find(([key]) => key === groupKey)?.[1] ?? [];
  const selected = available.filter((award: any) =>
    selectedAwardIds.includes(award.id),
  );
  const mutation = useMutation({
    mutationFn: () =>
      procurementService.createPurchaseOrder(rfqId, {
        awardIds: selected.map((award) => award.id),
        supplierId: selected[0].supplierId,
        currencyId: selected[0].currencyId,
        warehouseId,
        expectedOn,
        idempotencyKey,
        // FR-SPO-06. Optional here — the broker's answer often arrives later, and the terms stay
        // correctable until the order is dispatched.
        incoterm: incoterm ? (incoterm as Incoterm) : undefined,
        portOfLoading: portOfLoading.trim() || undefined,
        portOfDischarge: portOfDischarge.trim() || undefined,
      }),
    onSuccess: (result) => {
      toast.success(`Supplier purchase order ${result.purchaseOrderNumber} created`);
      onSaved();
    },
    onError: (error) =>
      toast.error(errorMessage(error, "Could not create the purchase order")),
  });
  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Create supplier purchase order</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {referenceDataError && (
            <Alert
              severity="error"
              action={<Button onClick={onRetryReferenceData}>Retry</Button>}
            >
              Warehouses could not be loaded.
            </Alert>
          )}
          <Alert severity="info">
            Only approved awards for the same supplier and currency can be
            combined.
          </Alert>
          <FormControl fullWidth>
            <InputLabel>Supplier award group</InputLabel>
            <Select
              label="Supplier award group"
              value={groupKey}
              onChange={(event) => {
                const nextKey = String(event.target.value);
                const nextAwards =
                  groups.find(([key]) => key === nextKey)?.[1] ?? [];
                setGroupKey(nextKey);
                setSelectedAwardIds(
                  nextAwards.map((award: any) => award.id),
                );
              }}
            >
              {groups.map(([key, items]) => (
                <MenuItem key={key} value={key}>
                  {items[0].supplierName} · {items[0].currencyCode} ·{" "}
                  {items.length} line{items.length === 1 ? "" : "s"}
                </MenuItem>
              ))}
            </Select>
          </FormControl>
          <Box>
            <Typography variant="body2" sx={{ fontWeight: 800, mb: 0.5 }}>
              Award lines for this PO
            </Typography>
            <Stack spacing={0.5}>
              {available.map((award: any) => (
                <Stack
                  key={award.id}
                  direction="row"
                  sx={{ alignItems: "center" }}
                >
                  <Checkbox
                    checked={selectedAwardIds.includes(award.id)}
                    onChange={(event) =>
                      setSelectedAwardIds((current) =>
                        event.target.checked
                          ? [...current, award.id]
                          : current.filter((id) => id !== award.id),
                      )
                    }
                  />
                  <Typography variant="body2">
                    RFQ line #{award.rfqItemId} · Qty {award.quantity} · {money(
                      award.landedUnitCost,
                      award.currencyCode,
                    )}
                  </Typography>
                </Stack>
              ))}
            </Stack>
          </Box>
          <Alert severity="info">
            The purchase order number is assigned by the server when the order
            is created.
          </Alert>
          <FormControl fullWidth required>
            <InputLabel>Receiving warehouse</InputLabel>
            <Select
              label="Receiving warehouse"
              value={warehouseId || ""}
              onChange={(event) => setWarehouseId(number(event.target.value))}
            >
              {warehouses
                .filter((warehouse: WarehouseDTO) => warehouse.isActive)
                .map((warehouse: WarehouseDTO) => (
                  <MenuItem key={warehouse.id} value={warehouse.id}>
                    {warehouse.warehouseCode} · {warehouse.warehouseName}
                  </MenuItem>
                ))}
            </Select>
          </FormControl>
          {/*
            FR-SPO-06. Shipping terms are optional at creation and correctable from the order card
            until it is dispatched, so a buyer who does not have them yet is not blocked from
            raising the order.
          */}
          <FormControl fullWidth>
            <InputLabel id="create-po-incoterm-label">
              Incoterm (optional)
            </InputLabel>
            <Select
              labelId="create-po-incoterm-label"
              label="Incoterm (optional)"
              value={incoterm}
              onChange={(event) => setIncoterm(event.target.value)}
            >
              <MenuItem value="">
                <em>Not set</em>
              </MenuItem>
              {INCOTERMS_2020.map((code) => (
                <MenuItem key={code} value={code}>
                  {code}
                </MenuItem>
              ))}
            </Select>
          </FormControl>
          <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
            <TextField
              fullWidth
              label="Port of loading (optional)"
              value={portOfLoading}
              onChange={(event) => setPortOfLoading(event.target.value)}
              slotProps={{ htmlInput: { maxLength: 120 } }}
            />
            <TextField
              fullWidth
              label="Port of discharge (optional)"
              value={portOfDischarge}
              onChange={(event) => setPortOfDischarge(event.target.value)}
              slotProps={{ htmlInput: { maxLength: 120 } }}
            />
          </Stack>
          <TextField
            required
            type="date"
            label="Expected delivery"
            value={expectedOn}
            onChange={(event) => setExpectedOn(event.target.value)}
            slotProps={{ inputLabel: { shrink: true } }}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          disabled={
            selected.length === 0 ||
            referenceDataError ||
            warehouseId <= 0 ||
            !expectedOn ||
            mutation.isPending
          }
          onClick={() => mutation.mutate()}
        >
          Create PO
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * FR-SPO-01. Approving a drafted supplier purchase order.
 *
 * There is no approver field: the server takes the approver from the authenticated principal, and
 * refuses the approval outright if that user also approved the sourcing award behind these lines.
 * The dialog says so up front, because the refusal is a policy decision the buyer should expect
 * rather than an error to puzzle over.
 */
function ApprovePurchaseOrderDialog({ order, onClose, onSaved }: any) {
  const [idempotencyKey] = useState(() => commandKey(`approve-po:${order.id}`));
  const mutation = useMutation({
    mutationFn: () =>
      procurementService.approvePurchaseOrder(order.id, {
        expectedVersion: order.version,
        idempotencyKey,
      }),
    onSuccess: () => {
      toast.success(
        `Supplier purchase order ${order.purchaseOrderNumber} approved for release`,
      );
      onSaved();
    },
    onError: (error) =>
      toast.error(errorMessage(error, "Could not approve the purchase order")),
  });

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Approve purchase order · {order.purchaseOrderNumber}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="info">
            Approval authorises {money(order.totalValue, order.currencyCode)} of
            committed spend with {order.supplierName}. It is recorded against
            your user and cannot be issued to the supplier until it is approved.
          </Alert>
          <Alert severity="warning">
            Segregation of duties: if you approved the sourcing award behind
            these lines, this approval will be refused and a second buyer must
            grant it.
          </Alert>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          disabled={mutation.isPending}
          onClick={() => mutation.mutate()}
        >
          Approve PO
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function IssuePurchaseOrderDialog({ order, onClose, onSaved }: any) {
  const [deliveryEvidenceReference, setDeliveryEvidenceReference] = useState("");
  const [deliveryEvidenceSha256, setDeliveryEvidenceSha256] = useState("");
  const [deliveredOn, setDeliveredOn] = useState(() =>
    localDateTimeInput(new Date()),
  );
  const [idempotencyKey] = useState(() => commandKey(`issue-po:${order.id}`));
  const evidenceHashIsValid = sha256Pattern.test(deliveryEvidenceSha256.trim());
  const deliveredOnDate = new Date(deliveredOn);
  const deliveredOnIsValid =
    deliveredOn.trim().length > 0 &&
    !Number.isNaN(deliveredOnDate.getTime()) &&
    deliveredOnDate.getTime() <= Date.now() + 5 * 60_000;
  const mutation = useMutation({
    mutationFn: () =>
      procurementService.issuePurchaseOrder(order.id, {
        expectedVersion: order.version,
        deliveryEvidenceReference: deliveryEvidenceReference.trim(),
        deliveryEvidenceSha256: deliveryEvidenceSha256.trim().toLowerCase(),
        deliveredOn: deliveredOnDate.toISOString(),
        idempotencyKey,
      }),
    onSuccess: () => {
      toast.success(`Supplier purchase order ${order.purchaseOrderNumber} issued`);
      onSaved();
    },
    onError: (error) =>
      toast.error(errorMessage(error, "Could not issue the purchase order")),
  });

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Issue purchase order · {order.purchaseOrderNumber}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="info">
            Issuing confirms the supplier received this order. Record the
            provider receipt, sent-message ID, or controlled delivery evidence.
          </Alert>
          <TextField
            fullWidth
            required
            label="Delivery evidence reference"
            value={deliveryEvidenceReference}
            onChange={(event) => setDeliveryEvidenceReference(event.target.value)}
            helperText="Use a durable provider or document reference, never message content."
          />
          <TextField
            fullWidth
            required
            label="Delivery evidence SHA-256"
            value={deliveryEvidenceSha256}
            onChange={(event) => setDeliveryEvidenceSha256(event.target.value)}
            error={
              deliveryEvidenceSha256.length > 0 && !evidenceHashIsValid
            }
            helperText="Enter the 64-character hexadecimal hash of the delivered evidence."
            slotProps={{ htmlInput: { maxLength: 64, spellCheck: false } }}
          />
          <TextField
            fullWidth
            required
            type="datetime-local"
            label="Delivered on"
            value={deliveredOn}
            onChange={(event) => setDeliveredOn(event.target.value)}
            error={deliveredOn.length > 0 && !deliveredOnIsValid}
            helperText="Recorded in your local time and submitted to the server as UTC."
            slotProps={{ inputLabel: { shrink: true } }}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          disabled={
            !deliveryEvidenceReference.trim() ||
            !evidenceHashIsValid ||
            !deliveredOnIsValid ||
            mutation.isPending
          }
          onClick={() => mutation.mutate()}
        >
          Issue PO
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * FR-SPO-03. Recording what the supplier said about an order that has been sent to them.
 *
 * Nexora has no supplier portal, so this is a buyer keying in a phone call or an email. The two
 * identities on the record are deliberately separate: the supplier's person is typed in here, the
 * Nexora user who recorded it is taken by the server from the session. The dialog labels the field
 * as the supplier's person for exactly that reason — a buyer typing their own name would attribute
 * the supplier's commitment to our own staff.
 *
 * Only ACCEPTED moves the order to ACKNOWLEDGED. A counter and a rejection are answers, not
 * agreement, and the copy here says so rather than letting a buyer believe a countered order is
 * settled.
 */
function AcknowledgePurchaseOrderDialog({ order, onClose, onSaved }: any) {
  const [status, setStatus] =
    useState<SupplierAcknowledgementStatus>("ACCEPTED");
  const [acknowledgedBy, setAcknowledgedBy] = useState("");
  const [revisedLeadTimeDays, setRevisedLeadTimeDays] = useState("");
  const [committedShipDate, setCommittedShipDate] = useState("");
  const [note, setNote] = useState("");
  const [refusal, setRefusal] = useState<string | null>(null);
  const [idempotencyKey] = useState(() =>
    commandKey(`acknowledge-po:${order.id}`),
  );

  const isCounter = status === "COUNTERED";
  const isRejection = status === "REJECTED";
  // A revised lead time IS the counter — the server refuses one under any other answer. A ship
  // date is different: accepting the order AND naming the day it ships is one coherent answer, and
  // it is the answer that arms the ship-date reminder. Only a rejection has no schedule at all.
  const showLeadTime = isCounter;
  const showShipDate = !isRejection;
  const leadTime = revisedLeadTimeDays.trim()
    ? Number(revisedLeadTimeDays)
    : null;
  const leadTimeIsUsable =
    leadTime === null || (Number.isInteger(leadTime) && leadTime > 0);
  // Mirrors the server's preconditions exactly, so the button is only dead when the request would
  // certainly be refused. Everything else is left to the server, whose message is shown verbatim.
  // The lead-time rules are only applied to a counter, because a lead time typed and then
  // abandoned by switching answer is not sent at all — disabling the button over a field the
  // buyer can no longer see is a dead end with no visible cause.
  const submittable =
    acknowledgedBy.trim().length > 0 &&
    (!isRejection || note.trim().length > 0) &&
    (!isCounter ||
      (leadTimeIsUsable &&
        (leadTime !== null || committedShipDate.trim().length > 0)));

  const mutation = useMutation({
    mutationFn: () =>
      procurementService.acknowledgePurchaseOrder(order.id, {
        expectedVersion: order.version,
        acknowledgementStatus: status,
        acknowledgedBy: acknowledgedBy.trim(),
        revisedLeadTimeDays: showLeadTime ? leadTime : null,
        committedShipDate: showShipDate
          ? committedShipDate.trim() || null
          : null,
        note: note.trim() || null,
        idempotencyKey,
      }),
    onSuccess: (result) => {
      toast.success(
        result.acknowledgementStatus === "ACCEPTED"
          ? `${order.purchaseOrderNumber} acknowledged by the supplier`
          : `Supplier answer recorded on ${order.purchaseOrderNumber}: ${result.acknowledgementStatus.toLowerCase()}`,
      );
      onSaved();
    },
    onError: (error) => {
      const message = errorMessage(
        error,
        "Could not record the supplier's answer.",
      );
      setRefusal(message);
      toast.error(message);
    },
  });

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>
        Record supplier answer · {order.purchaseOrderNumber}
      </DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="info">
            {order.supplierName} has this order. Record what they answered. Only
            an acceptance marks the order acknowledged — a counter or a
            rejection is recorded against the order but still leaves you a
            decision to make.
          </Alert>
          {refusal && (
            <Alert severity="error" onClose={() => setRefusal(null)}>
              {refusal}
            </Alert>
          )}
          <FormControl fullWidth>
            <InputLabel id="supplier-answer-label">
              Supplier&apos;s answer
            </InputLabel>
            <Select
              labelId="supplier-answer-label"
              label="Supplier's answer"
              value={status}
              onChange={(event) => {
                setStatus(event.target.value as SupplierAcknowledgementStatus);
                setRefusal(null);
              }}
            >
              <MenuItem value="ACCEPTED">
                Accepted — the order stands as sent
              </MenuItem>
              <MenuItem value="COUNTERED">
                Countered — accepted on different terms
              </MenuItem>
              <MenuItem value="REJECTED">
                Rejected — the supplier will not supply
              </MenuItem>
            </Select>
          </FormControl>
          <TextField
            fullWidth
            required
            label={`Supplier contact who answered (at ${order.supplierName})`}
            value={acknowledgedBy}
            onChange={(event) => setAcknowledgedBy(event.target.value)}
            helperText="The person at the supplier who gave this answer — not your own name. Your user is recorded separately as the person who keyed it in."
            slotProps={{ htmlInput: { maxLength: 255 } }}
          />
          {showLeadTime && (
            <TextField
              fullWidth
              type="number"
              label="Revised lead time (days)"
              value={revisedLeadTimeDays}
              onChange={(event) => setRevisedLeadTimeDays(event.target.value)}
              error={revisedLeadTimeDays.trim().length > 0 && !leadTimeIsUsable}
              helperText="Whole days, greater than zero. Give this or a committed ship date. A revised lead time is what makes this a counter, so it belongs to no other answer."
              slotProps={{ htmlInput: { min: 1, step: 1 } }}
            />
          )}
          {showShipDate && (
            <TextField
              fullWidth
              type="date"
              label={
                isCounter
                  ? "Committed ship date"
                  : "Committed ship date (optional)"
              }
              value={committedShipDate}
              onChange={(event) => setCommittedShipDate(event.target.value)}
              helperText="The date the supplier commits to ship. This is the date the ship-date reminder chases, so recording it is what gets the buyer warned before it passes."
              slotProps={{ inputLabel: { shrink: true } }}
            />
          )}
          <TextField
            fullWidth
            multiline
            minRows={2}
            required={isRejection}
            label={
              isRejection
                ? "Supplier's reason for rejecting"
                : "Note (optional)"
            }
            value={note}
            onChange={(event) => setNote(event.target.value)}
            helperText={
              isRejection
                ? "A rejection has to say why, or nobody downstream knows whether to re-source or re-price."
                : "The supplier's own words, if there is anything worth carrying forward."
            }
            slotProps={{ htmlInput: { maxLength: 1000 } }}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!submittable || mutation.isPending}
          onClick={() => {
            setRefusal(null);
            mutation.mutate();
          }}
        >
          Record answer
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * FR-SPO-06. Correcting the shipping and customs terms of an order that has not yet been sent.
 *
 * The server treats an omitted field as LEAVE UNCHANGED, never as clear — a sparse correction is
 * the normal case and a narrow edit must not be able to wipe the Incoterm the rest of the order
 * depends on. This dialog therefore submits only fields the buyer actually changed, and says out
 * loud that emptying a box will not blank the stored value, because silently ignoring an edit the
 * buyer made is worse than refusing it.
 */
function TradeTermsDialog({ order, onClose, onSaved }: any) {
  const [incoterm, setIncoterm] = useState<string>(order.incoterm ?? "");
  const [portOfLoading, setPortOfLoading] = useState<string>(
    order.portOfLoading ?? "",
  );
  const [portOfDischarge, setPortOfDischarge] = useState<string>(
    order.portOfDischarge ?? "",
  );
  const [lineTerms, setLineTerms] = useState<
    Record<number, { hsCode: string; countryOfOrigin: string }>
  >(() =>
    Object.fromEntries(
      (order.lines ?? []).map((line: any) => [
        line.id,
        {
          hsCode: line.hsCode ?? "",
          countryOfOrigin: line.countryOfOrigin ?? "",
        },
      ]),
    ),
  );
  const [refusal, setRefusal] = useState<string | null>(null);
  const [idempotencyKey] = useState(() =>
    commandKey(`trade-terms:${order.id}`),
  );

  const edited = (next: string, before?: string | null) =>
    next.trim() !== (before ?? "").trim();
  const blanked = (next: string, before?: string | null) =>
    (before ?? "").trim().length > 0 && next.trim().length === 0;

  const clearedFields = [
    blanked(incoterm, order.incoterm) ? "Incoterm" : null,
    blanked(portOfLoading, order.portOfLoading) ? "Port of loading" : null,
    blanked(portOfDischarge, order.portOfDischarge) ? "Port of discharge" : null,
    ...(order.lines ?? []).flatMap((line: any) => [
      blanked(lineTerms[line.id]?.hsCode ?? "", line.hsCode)
        ? `HS code on ${line.description}`
        : null,
      blanked(lineTerms[line.id]?.countryOfOrigin ?? "", line.countryOfOrigin)
        ? `Country of origin on ${line.description}`
        : null,
    ]),
  ].filter(Boolean) as string[];

  const lineEdits: PurchaseOrderLineTradeTerms[] = (order.lines ?? [])
    .map((line: any) => {
      const next = lineTerms[line.id] ?? { hsCode: "", countryOfOrigin: "" };
      const hsCode =
        edited(next.hsCode, line.hsCode) && !blanked(next.hsCode, line.hsCode)
          ? next.hsCode.trim()
          : undefined;
      const countryOfOrigin =
        edited(next.countryOfOrigin, line.countryOfOrigin) &&
        !blanked(next.countryOfOrigin, line.countryOfOrigin)
          ? next.countryOfOrigin.trim()
          : undefined;
      return hsCode === undefined && countryOfOrigin === undefined
        ? null
        : { lineId: line.id, hsCode, countryOfOrigin };
    })
    .filter(Boolean) as PurchaseOrderLineTradeTerms[];

  const sendIncoterm =
    edited(incoterm, order.incoterm) && !blanked(incoterm, order.incoterm);
  const sendPortOfLoading =
    edited(portOfLoading, order.portOfLoading) &&
    !blanked(portOfLoading, order.portOfLoading);
  const sendPortOfDischarge =
    edited(portOfDischarge, order.portOfDischarge) &&
    !blanked(portOfDischarge, order.portOfDischarge);
  const hasChanges =
    sendIncoterm ||
    sendPortOfLoading ||
    sendPortOfDischarge ||
    lineEdits.length > 0;

  const mutation = useMutation({
    mutationFn: () =>
      procurementService.amendPurchaseOrderTradeTerms(order.id, {
        expectedVersion: order.version,
        incoterm: sendIncoterm ? (incoterm as Incoterm) : undefined,
        portOfLoading: sendPortOfLoading ? portOfLoading.trim() : undefined,
        portOfDischarge: sendPortOfDischarge
          ? portOfDischarge.trim()
          : undefined,
        lines: lineEdits.length > 0 ? lineEdits : undefined,
        idempotencyKey,
      }),
    onSuccess: () => {
      toast.success(`Trade terms updated on ${order.purchaseOrderNumber}`);
      onSaved();
    },
    onError: (error) => {
      const message = errorMessage(error, "Could not update the trade terms.");
      setRefusal(message);
      toast.error(message);
    },
  });

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="md">
      <DialogTitle>Trade terms · {order.purchaseOrderNumber}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="info">
            Incoterm, ports and per-line customs data travel with this order to
            the customs broker. They can be corrected until the order is sent to{" "}
            {order.supplierName}; after that the supplier holds a copy and
            changing them is a re-issue, not an edit.
          </Alert>
          {refusal && (
            <Alert severity="error" onClose={() => setRefusal(null)}>
              {refusal}
            </Alert>
          )}
          {clearedFields.length > 0 && (
            <Alert severity="warning">
              A term can be corrected but not blanked here:{" "}
              {clearedFields.join(", ")} will keep the value already stored.
              Cancel and re-raise the order to remove a term entirely.
            </Alert>
          )}
          <FormControl fullWidth>
            <InputLabel id="incoterm-label">Incoterm</InputLabel>
            <Select
              labelId="incoterm-label"
              label="Incoterm"
              value={incoterm}
              onChange={(event) => {
                setIncoterm(event.target.value);
                setRefusal(null);
              }}
            >
              <MenuItem value="">
                <em>Not set</em>
              </MenuItem>
              {INCOTERMS_2020.map((code) => (
                <MenuItem key={code} value={code}>
                  {code}
                </MenuItem>
              ))}
            </Select>
          </FormControl>
          <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
            <TextField
              fullWidth
              label="Port of loading"
              value={portOfLoading}
              onChange={(event) => setPortOfLoading(event.target.value)}
              slotProps={{ htmlInput: { maxLength: 120 } }}
            />
            <TextField
              fullWidth
              label="Port of discharge"
              value={portOfDischarge}
              onChange={(event) => setPortOfDischarge(event.target.value)}
              slotProps={{ htmlInput: { maxLength: 120 } }}
            />
          </Stack>
          <Divider textAlign="left">
            <Typography variant="caption" color="text.secondary">
              Customs data per line
            </Typography>
          </Divider>
          {(order.lines ?? []).map((line: any) => (
            <Stack
              key={line.id}
              direction={{ xs: "column", sm: "row" }}
              spacing={2}
              sx={{ alignItems: { sm: "center" } }}
            >
              <Typography variant="body2" sx={{ flex: 1, minWidth: 0 }}>
                {line.description}
              </Typography>
              <TextField
                label="HS code"
                value={lineTerms[line.id]?.hsCode ?? ""}
                onChange={(event) =>
                  setLineTerms((current) => ({
                    ...current,
                    [line.id]: {
                      hsCode: event.target.value,
                      countryOfOrigin:
                        current[line.id]?.countryOfOrigin ?? "",
                    },
                  }))
                }
                slotProps={{ htmlInput: { maxLength: 20 } }}
                sx={{ width: { xs: "100%", sm: 180 } }}
              />
              <TextField
                label="Country of origin"
                value={lineTerms[line.id]?.countryOfOrigin ?? ""}
                onChange={(event) =>
                  setLineTerms((current) => ({
                    ...current,
                    [line.id]: {
                      hsCode: current[line.id]?.hsCode ?? "",
                      countryOfOrigin: event.target.value,
                    },
                  }))
                }
                slotProps={{ htmlInput: { maxLength: 100 } }}
                sx={{ width: { xs: "100%", sm: 220 } }}
              />
            </Stack>
          ))}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!hasChanges || mutation.isPending}
          onClick={() => {
            setRefusal(null);
            mutation.mutate();
          }}
        >
          Save trade terms
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function ReceiptDialog({
  order,
  warehouses,
  referenceDataError,
  onRetryReferenceData,
  onClose,
  onSaved,
}: any) {
  const [warehouseId, setWarehouseId] = useState(
    order.lines[0]?.warehouseId ?? 0,
  );
  const [receiptNumber, setReceiptNumber] = useState("");
  const [receivedOn, setReceivedOn] = useState(
    localCalendarDate(new Date()),
  );
  const [idempotencyKey] = useState(() => commandKey(`receipt:${order.id}`));
  const [quantities, setQuantities] = useState<Record<number, number>>(
    Object.fromEntries(
      order.lines.map((line: any) => [line.id, line.openQuantity]),
    ),
  );
  // The traceability declaration, per line. Country of origin starts EMPTY rather than
  // pre-filled from the ordered origin: the server falls back to the ordered value on its own,
  // and pre-filling it would make "what arrived differs from what was ordered" impossible to
  // state, which is the whole point of capturing it.
  const [lots, setLots] = useState<Record<number, LotDraft>>(
    Object.fromEntries(
      order.lines.map((line: any) => [line.id, emptyLotDraft()]),
    ),
  );
  const setLotField = (lineId: number, field: keyof LotDraft, value: string) =>
    setLots((current) => ({
      ...current,
      [lineId]: { ...(current[lineId] ?? emptyLotDraft()), [field]: value },
    }));
  // Mirrors Traceability/MaterialLotRecorder.cs. The server is still the authority and its
  // refusals are shown verbatim; this only stops the operator from posting a receipt that is
  // already known to be refused.
  const lotProblems: Record<number, string | null> = Object.fromEntries(
    order.lines.map((line: any) => [
      line.id,
      lotProblem(line, number(quantities[line.id]), lots[line.id]),
    ]),
  );
  const mutation = useMutation({
    mutationFn: () =>
      procurementService.postReceipt(order.id, {
        warehouseId,
        receiptNumber,
        receivedOn: receiptTimestamp(receivedOn),
        expectedPurchaseOrderVersion: order.version,
        idempotencyKey,
        lines: order.lines
          .filter((line: any) => number(quantities[line.id]) > 0)
          .map((line: any) => {
            const lot = lotPayload(line, lots[line.id]);
            return {
              purchaseOrderLineId: line.id,
              quantity: number(quantities[line.id]),
              ...(lot ? { lot } : {}),
            };
          }),
      }),
    onSuccess: () => {
      toast.success("Receipt posted and inventory movement recorded");
      onSaved();
    },
    onError: (error) =>
      toast.error(errorMessage(error, "Could not post the receipt")),
  });
  const invalid =
    order.lines.some(
      (line: any) =>
        number(quantities[line.id]) < 0 ||
        number(quantities[line.id]) > line.openQuantity,
    ) || Object.values(lotProblems).some((problem) => problem !== null);
  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="md">
      <DialogTitle>Record receipt · {order.purchaseOrderNumber}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {referenceDataError && (
            <Alert
              severity="error"
              action={<Button onClick={onRetryReferenceData}>Retry</Button>}
            >
              Warehouses could not be loaded.
            </Alert>
          )}
          <Alert severity="info">
            Posting creates an immutable receipt and inventory movement. Partial
            receipts keep the PO open.
          </Alert>
          <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
            <TextField
              fullWidth
              required
              label="Receipt reference"
              value={receiptNumber}
              onChange={(event) => setReceiptNumber(event.target.value)}
            />
            <FormControl fullWidth required>
              <InputLabel>Warehouse</InputLabel>
              <Select
                label="Warehouse"
                value={warehouseId || ""}
                onChange={(event) => setWarehouseId(number(event.target.value))}
              >
                {warehouses
                  .filter((warehouse: WarehouseDTO) => warehouse.isActive)
                  .map((warehouse: WarehouseDTO) => (
                    <MenuItem key={warehouse.id} value={warehouse.id}>
                      {warehouse.warehouseCode} · {warehouse.warehouseName}
                    </MenuItem>
                  ))}
              </Select>
            </FormControl>
            <TextField
              fullWidth
              type="date"
              label="Received on"
              value={receivedOn}
              onChange={(e) => setReceivedOn(e.target.value)}
              slotProps={{ inputLabel: { shrink: true } }}
            />
          </Stack>
          <Divider />
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Description</TableCell>
                <TableCell align="right">Open</TableCell>
                <TableCell align="right">Receive now</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {order.lines.map((line: any) => (
                <Fragment key={line.id}>
                  <TableRow>
                    <TableCell>
                      <Stack spacing={0.5}>
                        <span>{line.description}</span>
                        {line.trackingMode === "LOT" && (
                          <Chip
                            size="small"
                            color="info"
                            variant="outlined"
                            label="Batch tracked"
                            sx={{ alignSelf: "flex-start" }}
                          />
                        )}
                        {line.trackingMode === "SERIAL" && (
                          <Chip
                            size="small"
                            color="info"
                            variant="outlined"
                            label="Serial tracked"
                            sx={{ alignSelf: "flex-start" }}
                          />
                        )}
                      </Stack>
                    </TableCell>
                    <TableCell align="right">{line.openQuantity}</TableCell>
                    <TableCell align="right">
                      <TextField
                        size="small"
                        type="number"
                        value={quantities[line.id] ?? 0}
                        onChange={(e) =>
                          setQuantities((current) => ({
                            ...current,
                            [line.id]: number(e.target.value),
                          }))
                        }
                        error={number(quantities[line.id]) > line.openQuantity}
                        sx={{ width: 130 }}
                      />
                    </TableCell>
                  </TableRow>
                  {line.trackingMode !== "UNTRACKED" &&
                    number(quantities[line.id]) > 0 && (
                      <TableRow>
                        <TableCell colSpan={3} sx={{ pt: 0 }}>
                          <Stack spacing={1.5} sx={{ pb: 1 }}>
                            {line.trackingMode === "LOT" ? (
                              <TextField
                                size="small"
                                required
                                fullWidth
                                label="Supplier lot / batch number"
                                value={lots[line.id]?.lotNumber ?? ""}
                                onChange={(e) =>
                                  setLotField(line.id, "lotNumber", e.target.value)
                                }
                                error={Boolean(lotProblems[line.id])}
                                helperText="As printed on the supplier's packing list or label."
                              />
                            ) : (
                              <TextField
                                size="small"
                                required
                                fullWidth
                                multiline
                                minRows={2}
                                label={`Serial numbers · one per received unit (${
                                  parseSerials(lots[line.id]?.serials).length
                                } of ${number(quantities[line.id])})`}
                                value={lots[line.id]?.serials ?? ""}
                                onChange={(e) =>
                                  setLotField(line.id, "serials", e.target.value)
                                }
                                error={Boolean(lotProblems[line.id])}
                                helperText="One per line, or separated by commas."
                              />
                            )}
                            <Stack
                              direction={{ xs: "column", sm: "row" }}
                              spacing={1.5}
                            >
                              <TextField
                                size="small"
                                fullWidth
                                label="Country of origin as received"
                                value={lots[line.id]?.countryOfOrigin ?? ""}
                                onChange={(e) =>
                                  setLotField(
                                    line.id,
                                    "countryOfOrigin",
                                    e.target.value,
                                  )
                                }
                                helperText={
                                  line.countryOfOrigin
                                    ? `Ordered as ${line.countryOfOrigin}. Leave blank if it matches.`
                                    : "No origin was stated on the order line."
                                }
                              />
                              <TextField
                                size="small"
                                fullWidth
                                type="date"
                                label="Expiry date"
                                value={lots[line.id]?.expiryDate ?? ""}
                                onChange={(e) =>
                                  setLotField(line.id, "expiryDate", e.target.value)
                                }
                                slotProps={{ inputLabel: { shrink: true } }}
                                helperText="Drives first-expiring-first-out picking. Leave blank if the material does not expire."
                              />
                              <TextField
                                size="small"
                                fullWidth
                                label="Supplier batch reference"
                                value={
                                  lots[line.id]?.supplierBatchReference ?? ""
                                }
                                onChange={(e) =>
                                  setLotField(
                                    line.id,
                                    "supplierBatchReference",
                                    e.target.value,
                                  )
                                }
                                helperText="The supplier's own reference, when it differs from the lot number."
                              />
                            </Stack>
                            {lotProblems[line.id] && (
                              <Alert severity="warning">
                                {lotProblems[line.id]}
                              </Alert>
                            )}
                          </Stack>
                        </TableCell>
                      </TableRow>
                    )}
                </Fragment>
              ))}
            </TableBody>
          </Table>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          disabled={
            !receiptNumber.trim() ||
            !receivedOn ||
            referenceDataError ||
            warehouseId <= 0 ||
            invalid ||
            !Object.values(quantities).some((q) => q > 0) ||
            mutation.isPending
          }
          onClick={() => mutation.mutate()}
        >
          Post receipt
        </Button>
      </DialogActions>
    </Dialog>
  );
}

export default SourcingWorkbenchPage;
