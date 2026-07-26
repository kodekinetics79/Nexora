import { useMemo, useState } from "react";
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
  AssignmentTurnedIn,
  Inventory2,
  LocalShipping,
  Refresh,
  Replay,
  Send,
  ShoppingCartCheckout,
  PriceCheck,
  Insights,
} from "@mui/icons-material";
import { toast } from "react-hot-toast";
import procurementService, {
  type QuoteComparisonResult,
  type SupplierOffer,
  type SupplierPurchaseOrder,
  type SupplierSolicitation,
} from "../../../api/services/procurementService";
import supplierService from "../../../api/services/supplierService";
import currencyService, {
  type CurrencyDTO,
} from "../../../api/services/currencyService";
import warehouseService, {
  type WarehouseDTO,
} from "../../../api/services/warehouseService";
import { useAuth } from "../../../context/AuthContext";
import commercialLearningService from "../../../api/services/commercialLearningService";

const commandKey = (prefix: string) => `${prefix}:${crypto.randomUUID()}`;
const number = (value: unknown) => Number(value || 0);
const money = (value: number, currency = "USD") =>
  new Intl.NumberFormat(undefined, { style: "currency", currency }).format(
    value,
  );
const errorMessage = (error: any, fallback: string) =>
  error?.response?.data?.message ||
  error?.response?.data?.title ||
  error?.message ||
  fallback;
const localCalendarDate = (value: Date) =>
  `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, "0")}-${String(value.getDate()).padStart(2, "0")}`;
const receiptTimestamp = (calendarDate: string, now = new Date()) =>
  calendarDate === localCalendarDate(now)
    ? now.toISOString()
    : new Date(`${calendarDate}T23:59:59.999Z`).toISOString();
const localDateTimeInput = (value: Date) =>
  new Date(value.getTime() - value.getTimezoneOffset() * 60_000)
    .toISOString()
    .slice(0, 16);
const sha256Pattern = /^[a-fA-F0-9]{64}$/;

const statusColor = (
  status: string,
): "default" | "success" | "warning" | "error" | "info" => {
  const normalized = status.replaceAll("_", "").toUpperCase();
  if (
    ["SENT", "RESPONDED", "RECEIVED", "ISSUED", "APPROVED"].includes(normalized)
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
  const [solicitOpen, setSolicitOpen] = useState(false);
  const [responseSolicitation, setResponseSolicitation] =
    useState<SupplierSolicitation | null>(null);
  const [awardOffer, setAwardOffer] = useState<SupplierOffer | null>(null);
  const [pricingSelection, setPricingSelection] = useState<{ awardId: number; quoteItemId: number; landedUnitCost: number; currencyCode: string } | null>(null);
  const [memoryLineId, setMemoryLineId] = useState<number | null>(null);
  const [poOpen, setPoOpen] = useState(false);
  const [issueOrder, setIssueOrder] =
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
  const suppliersQuery = useQuery({
    queryKey: ["procurement-suppliers", userData?.businessUnitId],
    queryFn: () =>
      supplierService.getAll({
        businessUnitId: userData?.businessUnitId ?? 0,
        pageSize: 500,
      }),
    enabled: !!userData?.businessUnitId && !!rfqId,
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
  const referenceQueries = [suppliersQuery, currenciesQuery, warehousesQuery];
  const referenceDataFailed = referenceQueries.some((query) => query.isError);

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
          <Tooltip title="Back to RFQs">
            <Button
              variant="outlined"
              onClick={() => navigate("/procurement/rfqs/all")}
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
              onClick={() => setSolicitOpen(true)}
              disabled={unresolvedLines.length === 0}
            >
              Request supplier quotes
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
          Required supplier, currency, or warehouse reference data could not
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
              <TableCell align="right">Evidence</TableCell>
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
                <TableCell align="right"><Button size="small" startIcon={<Insights />} onClick={() => setMemoryLineId(line.id)}>Commercial memory</Button></TableCell>
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
                    {canSolicit && statusColor(item.status) === "error" && (
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
                <TableCell align="right">Available</TableCell>
                <TableCell align="right">Unit price</TableCell>
                <TableCell align="right">Landed cost</TableCell>
                <TableCell align="right">Lead time</TableCell>
                <TableCell align="right">Reliability</TableCell>
                <TableCell>Evidence</TableCell>
                <TableCell align="right">Still to source</TableCell>
                <TableCell align="right">Decision</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {workbench.offers.map((offer) => {
                const comparison = comparisonsQuery.data?.[offer.rfqItemId];
                const authoritativeOffer = comparison?.lines.find(
                  (line) => line.supplierQuotedItemId === offer.id,
                );
                const isRecommended =
                  comparison?.recommendedSupplierQuotedItemId === offer.id;
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
                  <TableCell align="right">
                    {authoritativeOffer?.reliability == null
                      ? "Unknown"
                      : `${authoritativeOffer.reliability}%`}
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
                  </TableCell>
                  <TableCell align="right">
                    {remainingRequirement(offer.rfqItemId)}
                  </TableCell>
                  <TableCell align="right">
                    <Stack spacing={0.5} sx={{ alignItems: "flex-end" }}>
                      {isRecommended && (
                        <Chip
                          size="small"
                          color="info"
                          label="Recommended"
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
                    </Stack>
                    <Typography variant="body2" color="text.secondary">
                      {order.supplierName} ·{" "}
                      {money(order.totalValue, order.currencyCode)}
                    </Typography>
                  </Box>
                  <Stack direction="row" spacing={1}>
                    {order.status === "DRAFT" && canIssuePo && (
                      <Button
                        startIcon={<Send />}
                        onClick={() => setIssueOrder(order)}
                      >
                        Issue PO
                      </Button>
                    )}
                    <Button
                      startIcon={<LocalShipping />}
                      disabled={
                        !canReceive ||
                        !["ISSUED", "PARTIALLY_RECEIVED"].includes(order.status)
                      }
                      onClick={() => setReceiptOrder(order)}
                    >
                      Record receipt
                    </Button>
                  </Stack>
                </Stack>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell>Description</TableCell>
                      <TableCell align="right">Ordered</TableCell>
                      <TableCell align="right">Received</TableCell>
                      <TableCell align="right">Open</TableCell>
                      <TableCell align="right">Landed unit cost</TableCell>
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
                        <TableCell align="right">{line.openQuantity}</TableCell>
                        <TableCell align="right">
                          {money(line.landedUnitCost, order.currencyCode)}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </Paper>
            ))
          )}
        </Stack>
      )}

      {rfqId && (
        <SolicitationDialog
          open={solicitOpen}
          onClose={() => setSolicitOpen(false)}
          rfqId={rfqId}
          lines={unresolvedLines}
          suppliers={suppliersQuery.data?.items ?? []}
          referenceDataError={suppliersQuery.isError}
          onRetryReferenceData={() => suppliersQuery.refetch()}
          onSaved={(partial: boolean) => {
            if (!partial) setSolicitOpen(false);
            refresh();
            setTab(1);
          }}
        />
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

function SolicitationDialog({
  open,
  onClose,
  rfqId,
  lines,
  suppliers,
  referenceDataError,
  onRetryReferenceData,
  onSaved,
}: any) {
  const [supplierIds, setSupplierIds] = useState<number[]>([]);
  const [lineIds, setLineIds] = useState<number[]>(
    lines.map((line: any) => line.id),
  );
  const [dueOn, setDueOn] = useState("");
  const [operationId] = useState(() => crypto.randomUUID());
  const mutation = useMutation({
    mutationFn: () =>
      procurementService.createSolicitations(rfqId, {
        supplierIds,
        rfqItemIds: lineIds,
        dueOn: dueOn || undefined,
        operationId,
      }),
    onSuccess: (results) => {
      const succeeded = results.filter((result) => result.succeeded).length;
      const failed = results.length - succeeded;
      if (failed === 0) {
        toast.success(`${succeeded} supplier request${succeeded === 1 ? "" : "s"} queued`);
      } else if (succeeded > 0) {
        toast.error(`${succeeded} request${succeeded === 1 ? "" : "s"} queued; ${failed} failed. Review the solicitation list before retrying.`);
      } else {
        toast.error("No supplier requests were queued");
      }
      if (succeeded > 0) onSaved(failed > 0);
    },
    onError: (error) =>
      toast.error(errorMessage(error, "Could not create supplier requests")),
  });
  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Request supplier quotes</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {referenceDataError && (
            <Alert
              severity="error"
              action={<Button onClick={onRetryReferenceData}>Retry</Button>}
            >
              Suppliers could not be loaded.
            </Alert>
          )}
          <Alert severity="info">
            Delivery status is confirmed by the server. Queued requests are not
            shown as sent until the provider accepts them.
          </Alert>
          <FormControl fullWidth>
            <InputLabel>Suppliers</InputLabel>
            <Select
              multiple
              label="Suppliers"
              value={supplierIds}
              onChange={(e) => setSupplierIds(e.target.value as number[])}
            >
              {suppliers.map((s: any) => (
                <MenuItem key={s.id} value={s.id}>
                  {s.name}
                  {s.contactEmail ? ` · ${s.contactEmail}` : ""}
                </MenuItem>
              ))}
            </Select>
          </FormControl>
          <FormControl fullWidth>
            <InputLabel>RFQ lines</InputLabel>
            <Select
              multiple
              label="RFQ lines"
              value={lineIds}
              onChange={(e) => setLineIds(e.target.value as number[])}
            >
              {lines.map((line: any) => (
                <MenuItem key={line.id} value={line.id}>
                  {line.partNumber || `Line ${line.id}`} · Shortfall{" "}
                  {line.shortfallQuantity}
                </MenuItem>
              ))}
            </Select>
          </FormControl>
          <TextField
            type="date"
            label="Response due"
            value={dueOn}
            onChange={(e) => setDueOn(e.target.value)}
            slotProps={{ inputLabel: { shrink: true } }}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          startIcon={<Send />}
          disabled={
            supplierIds.length === 0 ||
            lineIds.length === 0 ||
            referenceDataError ||
            mutation.isPending
          }
          onClick={() => mutation.mutate()}
        >
          Queue requests
        </Button>
      </DialogActions>
    </Dialog>
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
    (field: keyof ResponseLineForm) =>
    (event: { target: { value: unknown } }) =>
      setLineForms((current) => ({
        ...current,
        [rfqItemId]: {
          ...(current[rfqItemId] ?? responseLineDefaults()),
          [field]: number(event.target.value),
        },
      }));
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
      value.minimumOrderQuantity >= 0
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
            autoFocus
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
          .map((line: any) => ({
            purchaseOrderLineId: line.id,
            quantity: number(quantities[line.id]),
          })),
      }),
    onSuccess: () => {
      toast.success("Receipt posted and inventory movement recorded");
      onSaved();
    },
    onError: (error) =>
      toast.error(errorMessage(error, "Could not post the receipt")),
  });
  const invalid = order.lines.some(
    (line: any) =>
      number(quantities[line.id]) < 0 ||
      number(quantities[line.id]) > line.openQuantity,
  );
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
                <TableRow key={line.id}>
                  <TableCell>{line.description}</TableCell>
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
