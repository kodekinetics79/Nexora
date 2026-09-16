import { useEffect, useMemo, useRef, useState } from "react";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useParams, useSearchParams } from "react-router-dom";
import {
  Link,
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
  FormControlLabel,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
} from "@mui/material";
import { ArrowBack, GroupAdd, HowToReg, PersonAdd, PersonSearch, Refresh, Send, TravelExplore } from "@mui/icons-material";
import { toast } from "react-hot-toast";
import procurementService, {
  type AdoptedDiscoveredSupplier,
  type SourcingCaseCandidate,
  type SupplierDiscoveryHit,
  type SupplierDiscoveryResult,
  type SupplierDiscoveryRole,
} from "../../../api/services/procurementService";
import supplierService from "../../../api/services/supplierService";
import {
  APPROVE_FOR_RFQS_LABEL, approveForRfqs, approveForRfqsReason, blockerWords, presetClearsAllBlockers,
} from "../../Suppliers/supplierRfqReadiness";
import NextStepPanel from "../../../components/common/NextStepPanel";
import { useAuth } from "../../../context/AuthContext";
import { statusLabel } from "../../../utils/statusLabels";
import { REFRESH_ON_RETURN_PARAM } from "./sourcingCaseReturn";

type CandidateLimit = 10 | 20 | 50;

const errorMessage = (error: any, fallback: string) =>
  error?.response?.data?.detail ||
  error?.response?.data?.message ||
  error?.response?.data?.title ||
  error?.message ||
  fallback;

const evidenceDate = (value?: string | null) =>
  value ? new Date(value).toLocaleDateString() : "No recorded activity";

const isCandidateReady = (candidate: SourcingCaseCandidate) =>
  candidate.eligibleForSupplierRfq === true && Boolean(candidate.contactEmail);

/** A buyer's words for the sourcing states this screen shows; any other code falls back to its label. */
const CASE_STATUS_WORDS: Record<string, string> = {
  DRAFT: "Getting started", INTERNAL_SEARCH: "Checking your suppliers", DISCOVERY_REQUIRED: "No supplier yet",
  CANDIDATES_READY: "Choose suppliers", SUPPLIERS_SELECTED: "Suppliers chosen", OUTREACH_READY: "Ready to send",
  OUTREACH_SENT: "Suppliers asked", RESPONSES_PARTIAL: "Some replies in", RESPONSES_COMPLETE: "All replies in",
  COMPARISON_READY: "Compare offers", NEGOTIATION: "Negotiating", AWARD_REVIEW: "Award in review",
  SUPPLIER_SELECTED: "Supplier chosen", CUSTOMER_QUOTE_READY: "Ready to quote", CLOSED: "Closed", CANCELLED: "Cancelled",
};
const caseStatusWords = (code?: string | null) => (code && CASE_STATUS_WORDS[code]) || statusLabel(code);

/** Why a supplier is on the list, in the buyer's words (SourcingCandidateEvidenceTypes). The server's reason is one hover away. */
const EVIDENCE_WORDS: Record<string, string> = {
  PREFERRED_SUPPLIER: "Preferred supplier for this product",
  PRIOR_SUPPLIER_QUOTE: "Quoted this product before",
  PURCHASE_HISTORY: "Bought from them before",
  PURCHASE_ORDER_HISTORY: "Supplied this product before",
  SUPPLIER_METADATA: "Tags name this part or its maker",
};

function CandidateEvidence({ candidate }: { candidate: SourcingCaseCandidate }) {
  return (
    <Stack spacing={0.25}>
      <Tooltip describeChild title={candidate.recommendationReason}>
        <Typography variant="body2" sx={{ fontWeight: 600 }}>
          {EVIDENCE_WORDS[candidate.evidenceType] ?? statusLabel(candidate.evidenceType)}
        </Typography>
      </Tooltip>
      <Typography variant="caption" color="text.secondary">
        Match #{candidate.rank} · strength <span className="tabular-nums">{candidate.evidenceScore}</span> · last activity {evidenceDate(candidate.evidenceFreshOn).toLowerCase()}
      </Typography>
    </Stack>
  );
}

/** The internet list arrives ten at a time; the first page loads by itself when fewer than ten known suppliers match. */
const DISCOVERY_PAGE = 10;

/** One chip per row, three theme colours that are not the brass accent: maker, distributor, reseller. */
const ROLE_CHIP_COLOR: Record<SupplierDiscoveryRole, "success" | "info" | "secondary"> = {
  Manufacturer: "success", Distributor: "info", Reseller: "secondary",
};

/** "Searched for: Schneider Electric LV431831 · also acceptable: ABB, Siemens" — null and empty parts are left out. */
const searchedForLine = (searchedFor: SupplierDiscoveryResult["searchedFor"]) => {
  const subject = [searchedFor.maker, searchedFor.partNumber].filter(Boolean).join(" ") || searchedFor.description;
  const alsoAcceptable = searchedFor.acceptableMakers.filter(Boolean);
  return `Searched for: ${subject}${alsoAcceptable.length ? ` · also acceptable: ${alsoAcceptable.join(", ")}` : ""}`;
};

/** The sentence the supplier email carries unless the rep writes their own (the server's default). */
const DEFAULT_SUPPLIER_MESSAGE = "Please submit your best pricing and lead times.";
const SUPPLIER_MESSAGE_MAX = 2000;

/** "Respond by" exactly as the email prints it: the calendar date of the chosen deadline, else the standing phrase. */
const respondByLabel = (localDateTime: string) => {
  if (!localDateTime) return "Please respond promptly";
  const instant = new Date(localDateTime);
  return Number.isNaN(instant.getTime()) ? "Please respond promptly" : instant.toISOString().slice(0, 10);
};

/** The quantity the supplier is asked for is the shortfall, not the customer's full line; say so when they differ. */
const quantityAskedLabel = (line: { unfulfilledQuantity: number; requestedQuantity: number; unitOfMeasure?: string | null }) => {
  const unit = line.unitOfMeasure ? ` ${line.unitOfMeasure}` : "";
  const asked = `${line.unfulfilledQuantity}${unit}`;
  return line.unfulfilledQuantity < line.requestedQuantity
    ? `${asked} (the rest of the ${line.requestedQuantity}${unit} comes from stock)`
    : asked;
};

function SourcingCasePage() {
  const { caseId } = useParams<{ caseId: string }>();
  const sourcingCaseId = Number(caseId);
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const queryClient = useQueryClient();
  const { hasPermission, userData } = useAuth();
  const [candidateLimit, setCandidateLimit] = useState<CandidateLimit>(10);
  const [selectedSupplierIds, setSelectedSupplierIds] = useState<number[]>([]);
  const [previewOpen, setPreviewOpen] = useState(false);
  const [responseDueOn, setResponseDueOn] = useState("");
  // The rep's words to the suppliers, pre-filled with the sentence every supplier RFQ carries.
  const [supplierMessage, setSupplierMessage] = useState(DEFAULT_SUPPLIER_MESSAGE);
  // Internet discovery: the rep asked for it by hand (when ten known suppliers already match), the
  // hits ticked to add, and what the server made of the ones already added (so a row can say "Added").
  const [discoveryRequested, setDiscoveryRequested] = useState(false);
  const [tickedHitIds, setTickedHitIds] = useState<string[]>([]);
  const [adoptedByHitId, setAdoptedByHitId] = useState<Record<string, AdoptedDiscoveredSupplier>>({});

  const queryKey = ["sourcing-case", sourcingCaseId];
  const query = useQuery({
    queryKey,
    queryFn: () => procurementService.getSourcingCase(sourcingCaseId),
    enabled: Number.isInteger(sourcingCaseId) && sourcingCaseId > 0,
    retry: 1,
  });

  useEffect(() => {
    if (query.data?.searchLimit && query.data.searchLimit !== candidateLimit) {
      setCandidateLimit(query.data.searchLimit);
    }
  }, [candidateLimit, query.data?.searchLimit]);

  const candidates = query.data?.candidates ?? [];
  const eligibleCandidates = useMemo(
    () => candidates.filter(isCandidateReady),
    [candidates],
  );
  const selectedCandidates = candidates.filter((candidate) =>
    selectedSupplierIds.includes(candidate.supplierId),
  );
  const outreachAlreadySent = ["OUTREACH_SENT", "RESPONSES_PARTIAL", "RESPONSES_COMPLETE",
    "COMPARISON_READY", "NEGOTIATION", "AWARD_REVIEW", "SUPPLIER_SELECTED",
    "CUSTOMER_QUOTE_READY", "CLOSED", "CANCELLED"].includes(query.data?.status ?? "");
  const canPrepare =
    hasPermission("RFQ Management", "edit") &&
    hasPermission("Supplier History", "create") &&
    !outreachAlreadySent;
  const candidatesFrozen = ["OUTREACH_READY", "OUTREACH_SENT", "RESPONSES_PARTIAL", "RESPONSES_COMPLETE",
    "COMPARISON_READY", "NEGOTIATION", "AWARD_REVIEW", "SUPPLIER_SELECTED",
    "CUSTOMER_QUOTE_READY", "CLOSED", "CANCELLED"].includes(query.data?.status ?? "");
  const canRefreshCandidates = hasPermission("Supplier History", "edit") && !candidatesFrozen;
  // Before outreach, an empty or all-blocked candidate list is the rep's problem to solve on this screen,
  // so the panel says why and offers the next move instead of repeating the server's "Review discovery options".
  const canAddSupplier = hasPermission("Suppliers", "create");
  const noKnownSupplier = !candidatesFrozen && candidates.length === 0;
  const noneReady = !candidatesFrozen && candidates.length > 0 && eligibleCandidates.length === 0;
  // A manager may approve a supplier for RFQs from the row — the same guarded write the supplier
  // page makes (server: manager role + Suppliers: Edit) — but only when approval alone would make
  // it askable. A missing email, an inactive record or a High risk verdict is not fixed by a click here.
  const canApproveInline = userData.isManager === true && hasPermission("Suppliers", "edit") && !candidatesFrozen;
  const approvableCandidates = candidates.filter(
    (candidate) => !isCandidateReady(candidate) && presetClearsAllBlockers(candidate.blockingReasons),
  );
  // Same wording pattern as the "cannot prepare Supplier RFQs" notice below: a disabled control
  // says which of its two gates is shut, so the reader knows whether to ask for a permission or
  // simply that the step has passed.
  const whyNoPrepare = !(hasPermission("RFQ Management", "edit") && hasPermission("Supplier History", "create"))
    ? "Your role can review candidates but cannot prepare Supplier RFQs."
    : outreachAlreadySent
      ? "Supplier RFQs have already been sent for this case. Capture the replies in the sourcing workbench."
      : selectedSupplierIds.length === 0
        ? "Tick the suppliers you want to ask."
        : null;
  const whyNoRefresh = !hasPermission("Supplier History", "edit")
    ? "Your role can review candidates but cannot refresh them."
    : candidatesFrozen
      ? "Candidates are fixed once a Supplier RFQ has been prepared for this case."
      : null;
  // The button and the panel sentence are derived from the same tick count, so the sentence names
  // the button exactly as it reads and changes the moment a supplier is ticked or unticked.
  const tickedCount = selectedSupplierIds.length;
  const askLabel = tickedCount > 0 ? `Ask ${tickedCount} supplier${tickedCount === 1 ? "" : "s"}` : "Ask the ticked suppliers";
  const tickSentence = !canPrepare
    ? whyNoPrepare
    : tickedCount === 0
      ? "Tick the suppliers you want to ask; each gets its own RFQ."
      : `${tickedCount} supplier${tickedCount === 1 ? "" : "s"} ticked. Press ${askLabel}; each gets its own RFQ.`;

  // Suppliers from the internet. Only someone who can add suppliers sees them, since adding is the
  // one thing the list is for; and once a Supplier RFQ is prepared the candidate list is fixed anyway.
  // The first page loads by itself when fewer than ten known suppliers match; otherwise on demand.
  const discoveryOffered = canAddSupplier && !candidatesFrozen && Boolean(query.data);
  const discoveryEnabled = discoveryOffered && (candidates.length < DISCOVERY_PAGE || discoveryRequested);
  const discovery = useInfiniteQuery({
    queryKey: ["sourcing-case-discovery", sourcingCaseId],
    queryFn: ({ pageParam }) =>
      procurementService.discoverSuppliers(sourcingCaseId, { offset: pageParam, limit: DISCOVERY_PAGE }),
    initialPageParam: 0,
    getNextPageParam: (last) =>
      last.status === "Ready" && last.offset + last.hits.length < last.total ? last.offset + last.hits.length : undefined,
    enabled: discoveryEnabled,
    retry: false,
    // The server keeps its own copy of the search; a window focus must not run it again.
    staleTime: Infinity,
  });
  const discoveryFirstPage = discovery.data?.pages[0];
  const discoveryHits = useMemo(
    () => (discovery.data?.pages ?? []).flatMap((page) => page.hits),
    [discovery.data],
  );
  const discoveryReady = discoveryFirstPage?.status === "Ready";
  const discoverySearching = discoveryEnabled && discovery.isPending;
  // The server's sentence for every non-Ready status is shown as written; a transport failure gets
  // the one sentence the server could not send.
  const discoveryMessage = discoveryFirstPage && discoveryFirstPage.status !== "Ready"
    ? discoveryFirstPage.message
    : discovery.isError
      ? "The internet could not be searched just now."
      : null;
  const discoveryRemaining = discoveryFirstPage ? Math.max(0, discoveryFirstPage.total - discoveryHits.length) : 0;
  const addableHitIds = discoveryHits
    .filter((hit) => hit.existingSupplierId === null && !adoptedByHitId[hit.id])
    .map((hit) => hit.id);
  const tickedHits = tickedHitIds.filter((id) => addableHitIds.includes(id));
  const tickedHitCount = tickedHits.length;
  const addLabel = tickedHitCount > 0 ? `Add ${tickedHitCount} to my suppliers` : "Add the ticked suppliers";
  const partWords = query.data?.requestedPartNumber || "this part";

  const adoptSuppliers = useMutation({
    mutationFn: (hitIds: string[]) => procurementService.adoptDiscoveredSuppliers(sourcingCaseId, hitIds),
    onSuccess: async (result) => {
      setAdoptedByHitId((current) => ({
        ...current,
        ...Object.fromEntries(result.adopted.map((adopted) => [adopted.hitId, adopted])),
      }));
      setTickedHitIds([]);
      const count = result.adopted.length;
      toast.success(`Added ${count} supplier${count === 1 ? "" : "s"}. They are now in your list above; approve them for RFQs to ask them.`);
      // The server re-ran the candidate search on adopt, so the known list above is refetched here.
      await query.refetch();
    },
    onError: (error) => toast.error(errorMessage(error, "The suppliers could not be added to your list.")),
  });

  const toggleHit = (hit: SupplierDiscoveryHit) => {
    setTickedHitIds((current) =>
      current.includes(hit.id) ? current.filter((id) => id !== hit.id) : [...current, hit.id],
    );
  };

  const refreshCandidates = useMutation({
    mutationFn: (limit: CandidateLimit) =>
      procurementService.refreshSourcingCaseCandidates(sourcingCaseId, limit, query.data?.version ?? 0),
    onSuccess: (result) => {
      queryClient.setQueryData(queryKey, (current: typeof query.data) => current ? {
        ...current,
        searchLimit: result.requestedLimit,
        version: result.version,
        candidates: result.candidates,
      } : current);
      setSelectedSupplierIds((current) =>
        current.filter((supplierId) =>
          result.candidates.some(
            (candidate) => candidate.supplierId === supplierId && isCandidateReady(candidate),
          ),
        ),
      );
    },
    onError: (error) => toast.error(errorMessage(error, "Known supplier candidates could not be refreshed.")),
  });

  // Back from "Add a supplier": the suppliers page appends ?refresh=1 to the return address and the
  // case runs the search itself, so the new supplier is listed without the rep pressing Refresh.
  // The flag is stripped once acted on, so a reload does not search again.
  const returnRefreshHandled = useRef(false);
  const refreshOnReturn = searchParams.get(REFRESH_ON_RETURN_PARAM) === "1";
  const refreshMutate = refreshCandidates.mutate;
  useEffect(() => {
    if (!refreshOnReturn || !query.data || returnRefreshHandled.current) return;
    returnRefreshHandled.current = true;
    if (canRefreshCandidates) refreshMutate(query.data.searchLimit ?? candidateLimit);
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      next.delete(REFRESH_ON_RETURN_PARAM);
      return next;
    }, { replace: true });
  }, [refreshOnReturn, query.data, canRefreshCandidates, refreshMutate, candidateLimit, setSearchParams]);

  // Fetch the supplier for its concurrency token, record the working combination with a reason that
  // names this screen (it lands on the supplier's change history), then let the list refresh itself.
  const approveCandidate = useMutation({
    mutationFn: async (candidate: SourcingCaseCandidate) => {
      const supplier = await supplierService.getById(candidate.supplierId);
      const decision = approveForRfqs(supplier);
      if (!decision) {
        throw new Error(`${candidate.supplierName} carries a High or Blocked risk verdict. A manager lowers it on the supplier page first.`);
      }
      await supplierService.govern(supplier.id, {
        ...decision,
        expectedConcurrencyToken: supplier.concurrencyToken ?? "",
        reason: approveForRfqsReason(userData.userName || userData.email, "from the sourcing case"),
      });
      return candidate;
    },
    onSuccess: (candidate) => {
      toast.success(`${candidate.supplierName} approved for RFQs.`);
      refreshMutate(candidateLimit);
    },
    onError: (error) => toast.error(errorMessage(error, "The supplier could not be approved.")),
  });

  const prepareSupplierRfqs = useMutation({
    mutationFn: async () => {
      if (!query.data) throw new Error("The sourcing case is not loaded.");
      return procurementService.prepareSupplierRfqs(
        query.data.id,
        selectedSupplierIds,
        query.data.version,
        crypto.randomUUID(),
        // <input type="datetime-local"> yields a local wall-clock string with no
        // zone. The API rejects non-UTC and past deadlines, so convert to a real
        // UTC instant here rather than sending the raw field value.
        responseDueOn ? new Date(responseDueOn).toISOString() : null,
        // The same message goes to every supplier in this send; blank keeps the standard sentence.
        supplierMessage.trim() || null,
      );
    },
    onSuccess: async (results) => {
      const succeeded = results.filter((result) => result.succeeded);
      const failed = results.find((result) => !result.succeeded);
      setSelectedSupplierIds((current) =>
        current.filter((supplierId) => !succeeded.some((result) => result.supplierId === supplierId)),
      );
      setPreviewOpen(false);
      await query.refetch();
      if (succeeded.length > 0) {
        toast.success(`${succeeded.length} supplier RFQ${succeeded.length === 1 ? "" : "s"} queued for email.`);
      }
      if (failed) {
        toast.error(errorMessage(failed.error, "A Supplier RFQ could not be prepared. Completed suppliers remain queued; retry the remaining selection."));
        return;
      }
      if (query.data) navigate(`/procurement/rfqs/${query.data.rfqId}/sourcing`);
    },
    onError: (error) => toast.error(errorMessage(error, "Supplier RFQs could not be prepared.")),
  });

  const changeLimit = (_: React.MouseEvent<HTMLElement>, value: CandidateLimit | null) => {
    if (!value || value === candidateLimit) return;
    setCandidateLimit(value);
    refreshCandidates.mutate(value);
  };

  const toggleCandidate = (candidate: SourcingCaseCandidate) => {
    if (!isCandidateReady(candidate)) return;
    setSelectedSupplierIds((current) =>
      current.includes(candidate.supplierId)
        ? current.filter((id) => id !== candidate.supplierId)
        : [...current, candidate.supplierId],
    );
  };

  if (!Number.isInteger(sourcingCaseId) || sourcingCaseId <= 0) {
    return <Box sx={{ p: 3 }}><Alert severity="error">The sourcing case reference is invalid.</Alert></Box>;
  }

  if (query.isLoading) {
    return <Box sx={{ minHeight: "60vh", display: "grid", placeItems: "center" }}><CircularProgress aria-label="Loading sourcing case" /></Box>;
  }

  if (query.isError) {
    return (
      <Box sx={{ p: { xs: 2, md: 3 } }}>
        <Alert severity="error" action={<Button color="inherit" onClick={() => query.refetch()}>Retry</Button>}>
          {errorMessage(query.error, "The sourcing case could not be loaded.")}
        </Alert>
      </Box>
    );
  }

  if (!query.data) return null;
  const sourcingCase = query.data;

  return (
    <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1600, mx: "auto" }}>
      <Stack direction={{ xs: "column", md: "row" }} spacing={2} sx={{ justifyContent: "space-between", mb: 3 }}>
        <Stack direction="row" spacing={1.5} sx={{ alignItems: "flex-start" }}>
          <Button variant="outlined" startIcon={<ArrowBack />} aria-label="Back to this RFQ" onClick={() => navigate(`/procurement/rfqs/view/${sourcingCase.rfqId}`)} sx={{ borderRadius: 2, borderColor: "divider", color: "text.secondary", whiteSpace: "nowrap" }}>
            Back to RFQ
          </Button>
          <Box>
            <Typography variant="h5" component="h1" sx={{ fontWeight: 800, letterSpacing: "-0.01em" }}>Ask suppliers</Typography>
            <Stack direction="row" spacing={1} sx={{ mt: 0.5, alignItems: "center", flexWrap: "wrap" }}>
              <Typography variant="body2" sx={{ fontWeight: 700 }}>Customer RFQ #{sourcingCase.rfqId}</Typography>
              {sourcingCase.nexoraSerial && <Chip size="small" variant="outlined" label={sourcingCase.nexoraSerial} />}
              <Chip size="small" label={caseStatusWords(sourcingCase.status)} sx={{ fontWeight: 700 }} />
            </Stack>
          </Box>
        </Stack>
        {!outreachAlreadySent && (
          <Tooltip describeChild title="Every supplier RFQ, reply and offer for this customer RFQ, in one place.">
            <Button variant="outlined" onClick={() => navigate(`/procurement/rfqs/${sourcingCase.rfqId}/sourcing`)} sx={{ alignSelf: { md: "flex-start" } }}>
              Open sourcing workbench
            </Button>
          </Tooltip>
        )}
      </Stack>

      <Paper variant="outlined" sx={{ p: 2.5, mb: 3 }}>
        <Stack direction={{ xs: "column", md: "row" }} spacing={3} divider={<Divider flexItem orientation="vertical" />}>
          <Box sx={{ flex: 1 }}>
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 600, letterSpacing: "0.02em" }}>Part</Typography>
            <Typography sx={{ fontWeight: 800 }}>{sourcingCase.requestedPartNumber || "Part number not recorded"}</Typography>
            <Typography variant="body2" color="text.secondary">{sourcingCase.description}</Typography>
          </Box>
          <Box>
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 600, letterSpacing: "0.02em" }}>Requested</Typography>
            <Typography className="tabular-nums" sx={{ fontWeight: 800 }}>{sourcingCase.requestedQuantity}</Typography>
          </Box>
          <Box>
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 600, letterSpacing: "0.02em" }}>Available</Typography>
            <Typography className="tabular-nums" sx={{ fontWeight: 800 }}>{sourcingCase.stockQuantity}</Typography>
          </Box>
          <Box>
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 600, letterSpacing: "0.02em" }}>To source</Typography>
            <Typography className="tabular-nums" color="error.main" sx={{ fontWeight: 800 }}>{sourcingCase.unfulfilledQuantity}</Typography>
          </Box>
        </Stack>
      </Paper>

      <NextStepPanel
        tone={sourcingCase.status === "OUTREACH_SENT" ? "success" : noKnownSupplier || noneReady ? "warning" : "info"}
        title="Next step"
        sentence={noKnownSupplier
          // The sentence follows the internet list: found some → tick them; ticked → name the button.
          ? discoveryReady && discoveryHits.length > 0
            ? tickedHitCount > 0
              ? `${tickedHitCount} ticked. Press ${addLabel}; ${tickedHitCount === 1 ? "it joins" : "they join"} your list above.`
              : `No supplier on your list is linked to ${partWords} yet. We found ${discoveryFirstPage?.total ?? discoveryHits.length} on the internet — tick the ones to add, or add one yourself.`
            : `No supplier on your list is linked to ${partWords} yet. ${canAddSupplier
              ? "Add one with this part number or its maker in Tags, then press Refresh candidates."
              : "Ask someone who can add suppliers to add one with this part number or its maker in Tags."}`
          : noneReady
            ? (canApproveInline && approvableCandidates.length > 0
              ? "None of these suppliers can be asked yet. Press Approve for RFQs beside a supplier you trust; the list refreshes on its own."
              : "None of these suppliers can be asked yet. A manager approves them for RFQs on the supplier page, then press Refresh candidates.")
            : !outreachAlreadySent && eligibleCandidates.length > 0
              ? tickSentence
              : sourcingCase.nextAction}
        testId="sourcing-case-next-step"
        action={outreachAlreadySent
          ? <Button variant="contained" onClick={() => navigate(`/procurement/rfqs/${sourcingCase.rfqId}/sourcing`)}>Open sourcing workbench</Button>
          : noKnownSupplier && canAddSupplier
            ? (
              <Tooltip describeChild title="Opens the supplier form with this part number already in Tags, and brings you back here when it is saved.">
                {/* Filled until an internet supplier is ticked; then the filled button is the one that adds the ticks. */}
                <Button variant={tickedHitCount > 0 ? "outlined" : "contained"} startIcon={<PersonAdd />} onClick={() => navigate(`/suppliers?new=1&tags=${encodeURIComponent(sourcingCase.requestedPartNumber ?? "")}&returnTo=${encodeURIComponent(`/procurement/sourcing-cases/${sourcingCase.id}`)}`)}>
                  Add a supplier
                </Button>
              </Tooltip>
            )
            : undefined}
      >
        {noKnownSupplier && discoveryMessage ? (
          <Typography variant="body2" sx={{ fontWeight: 500 }}>{discoveryMessage}</Typography>
        ) : undefined}
      </NextStepPanel>

      <Stack direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ alignItems: { sm: "center" }, justifyContent: "space-between", mb: 2 }}>
        <Box>
          <Typography variant="h6" sx={{ fontWeight: 700 }}>Suppliers for this part</Typography>
          <Typography variant="body2" color="text.secondary">
            From your own supplier list, strongest link first. Tick everyone you want to ask; each gets its own RFQ.
          </Typography>
        </Box>
        <Stack direction="row" spacing={1} sx={{ alignItems: "center", flexWrap: "wrap" }}>
          <ToggleButtonGroup exclusive size="small" value={candidateLimit} onChange={changeLimit} aria-label="How many suppliers to show">
            {[10, 20, 50].map((limit) => <ToggleButton key={limit} value={limit} aria-label={`Show up to ${limit} suppliers`}>{limit}</ToggleButton>)}
          </ToggleButtonGroup>
          <Tooltip title={whyNoRefresh ?? "Looks through your supplier list again, including suppliers you just added or approved."}>
            {/* Focusable only while disabled, so a keyboard user can reach the reason (same
                treatment as the mailbox-check button on the leads list). */}
            <span tabIndex={canRefreshCandidates ? undefined : 0}>
              <Button startIcon={<Refresh />} onClick={() => refreshCandidates.mutate(candidateLimit)} disabled={!canRefreshCandidates || refreshCandidates.isPending}>
                Refresh candidates
              </Button>
            </span>
          </Tooltip>
        </Stack>
      </Stack>

      {refreshCandidates.isError && (
        <Alert severity="error" sx={{ mb: 2 }} action={<Button color="inherit" onClick={() => refreshCandidates.mutate(candidateLimit)}>Try again</Button>}>The supplier list could not be refreshed. The last list is still shown.</Alert>
      )}

      {candidates.length === 0 ? (
        <Paper variant="outlined" sx={{ p: { xs: 3, md: 5 }, textAlign: "center", borderStyle: "dashed", bgcolor: "action.hover" }}>
          <PersonSearch aria-hidden sx={{ fontSize: 40, color: "text.disabled", mb: 1 }} />
          <Typography sx={{ fontWeight: 700 }}>No supplier is linked to this part yet</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, maxWidth: 620, mx: "auto", textWrap: "pretty" }}>
            Suppliers show here when their record names this part number or its maker in Tags, or when they quoted or supplied this product before.{discoveryOffered ? " Suppliers from the internet are listed below." : ""}
          </Typography>
        </Paper>
      ) : (
        <Paper variant="outlined" sx={{ overflow: "hidden" }}>
          <Box sx={{ overflowX: "auto" }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell padding="checkbox" sx={{ fontWeight: 700 }}>Ask</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Supplier</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Why listed</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Approval</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Can be asked</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {candidates.map((candidate) => (
                  <TableRow key={candidate.id} hover selected={selectedSupplierIds.includes(candidate.supplierId)}>
                    <TableCell padding="checkbox">
                      <Checkbox
                        checked={selectedSupplierIds.includes(candidate.supplierId)}
                        disabled={!isCandidateReady(candidate)}
                        onChange={() => toggleCandidate(candidate)}
                        slotProps={{ input: { "aria-label": `Select ${candidate.supplierName}` } }}
                      />
                    </TableCell>
                    <TableCell>
                      <Link component="button" type="button" variant="body2" underline="hover" onClick={() => navigate(`/suppliers/${candidate.supplierId}`)} sx={{ fontWeight: 800, textAlign: "left", display: "block" }}>
                        {candidate.supplierName}
                      </Link>
                      <Typography variant="caption" color="text.secondary">
                        {candidate.contactEmail || "No contact email"}
                      </Typography>
                    </TableCell>
                    <TableCell><CandidateEvidence candidate={candidate} /></TableCell>
                    <TableCell>
                      <Stack direction="row" spacing={0.5} sx={{ flexWrap: "wrap" }}>
                        <Chip size="small" variant="outlined" label={statusLabel(candidate.governanceStatus || candidate.approvalStatus)} />
                        <Chip size="small" variant="outlined" label={statusLabel(candidate.readinessStatus)} />
                      </Stack>
                    </TableCell>
                    <TableCell>
                      {isCandidateReady(candidate) ? (
                        <Chip size="small" color="success" variant="outlined" label="Yes" sx={{ fontWeight: 700 }} />
                      ) : (() => {
                        // Every blocker, in the buyer's words; nothing hides behind "3 more".
                        const reasons = candidate.blockingReasons?.length ? candidate.blockingReasons : ["A verified dispatch contact is required"];
                        const approvable = presetClearsAllBlockers(reasons);
                        const approvingThis = approveCandidate.isPending && approveCandidate.variables?.supplierId === candidate.supplierId;
                        return (
                          <Stack spacing={0.5} sx={{ alignItems: "flex-start", maxWidth: 280 }}>
                            <Chip size="small" color="warning" variant="outlined" sx={{ fontWeight: 700 }}
                              label={!candidate.contactEmail ? "Needs a contact email" : approvable ? "Needs approval" : "Blocked"} />
                            <Box component="ul" sx={{ m: 0, pl: 2 }}>
                              {reasons.map((reason) => (
                                <Typography key={reason} component="li" variant="caption" color="text.secondary" sx={{ display: "list-item" }}>
                                  {blockerWords(reason)}
                                </Typography>
                              ))}
                            </Box>
                            {canApproveInline && approvable && (
                              <Tooltip describeChild title="Records Approved, Verified, Compliance cleared, Risk Low and Ready for RFQs on this supplier, with a reason naming you and this case, then refreshes the list.">
                                <Button size="small" variant="outlined" startIcon={<HowToReg />} onClick={() => approveCandidate.mutate(candidate)} disabled={approveCandidate.isPending}>
                                  {approvingThis ? "Approving…" : APPROVE_FOR_RFQS_LABEL}
                                </Button>
                              </Tooltip>
                            )}
                          </Stack>
                        );
                      })()}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Box>
        </Paper>
      )}

      <Stack direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ mt: 2, justifyContent: "space-between", alignItems: { sm: "center" } }}>
        <Typography variant="body2" color="text.secondary" className="tabular-nums">
          {candidates.length > 0 ? `${eligibleCandidates.length} of ${candidates.length} can be asked · ${selectedSupplierIds.length} ticked` : ""}
        </Typography>
        <Tooltip title={whyNoPrepare ?? "Sends each ticked supplier its own numbered RFQ by email. You confirm the list first."} describeChild>
          <span>
            <Button
              variant={canPrepare && selectedSupplierIds.length > 0 ? "contained" : "outlined"}
              startIcon={<Send />}
              disabled={!canPrepare || selectedSupplierIds.length === 0}
              onClick={() => setPreviewOpen(true)}
            >
              {askLabel}
            </Button>
          </span>
        </Tooltip>
      </Stack>
      {!canPrepare && whyNoPrepare && (
        <Alert severity="info" sx={{ mt: 2 }}>{whyNoPrepare}</Alert>
      )}

      {discoveryOffered && (
        <Box component="section" aria-labelledby="internet-suppliers-heading" sx={{ mt: 4 }}>
          <Stack direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ alignItems: { sm: "center" }, justifyContent: "space-between", mb: 2 }}>
            <Box>
              <Typography id="internet-suppliers-heading" variant="h6" sx={{ fontWeight: 700 }}>From the internet</Typography>
              <Typography variant="body2" color="text.secondary">
                Makers first, then distributors, then resellers. Tick the ones to add; they join your list above and can be asked once approved.
              </Typography>
              {discoveryFirstPage && (
                <Typography variant="caption" color="text.secondary" sx={{ display: "block", mt: 0.5 }}>
                  {searchedForLine(discoveryFirstPage.searchedFor)}
                </Typography>
              )}
            </Box>
            {!discoveryEnabled && (
              <Tooltip describeChild title="Looks on the internet for makers, distributors and resellers of this part.">
                <Button size="small" variant="outlined" startIcon={<TravelExplore />} onClick={() => setDiscoveryRequested(true)}>
                  Search the internet
                </Button>
              </Tooltip>
            )}
          </Stack>

          {discoverySearching && (
            <Stack direction="row" spacing={1.5} sx={{ alignItems: "center", py: 1.5 }} role="status">
              <CircularProgress size={18} aria-hidden />
              <Typography variant="body2" color="text.secondary">Searching the internet for {partWords}…</Typography>
            </Stack>
          )}

          {/* One sentence, once. When the list above is empty the Next step panel already carries the
              server's sentence, so this section repeats it only when a transport error needs its
              Try again button, or when known suppliers exist and the panel is talking about them. */}
          {!discoverySearching && discoveryMessage && (!noKnownSupplier || discovery.isError) && (
            <Alert
              severity="info"
              action={discovery.isError ? <Button color="inherit" onClick={() => discovery.refetch()}>Try again</Button> : undefined}
            >
              {discoveryMessage}
            </Alert>
          )}

          {discoveryReady && discoveryHits.length > 0 && (
            <>
              <Paper variant="outlined" sx={{ overflow: "hidden" }} data-testid="internet-suppliers">
                <Box sx={{ overflowX: "auto" }}>
                  <Table size="small">
                    <TableHead>
                      <TableRow>
                        <TableCell padding="checkbox" sx={{ fontWeight: 700 }}>Add</TableCell>
                        <TableCell sx={{ fontWeight: 700 }}>Supplier</TableCell>
                        <TableCell sx={{ fontWeight: 700 }}>Why listed</TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {discoveryHits.map((hit) => {
                        const adopted = adoptedByHitId[hit.id];
                        const ticked = tickedHits.includes(hit.id);
                        return (
                          <TableRow key={hit.id} hover selected={ticked}>
                            <TableCell padding="checkbox">
                              {adopted ? (
                                <Chip size="small" color="success" variant="outlined" label="Added" sx={{ fontWeight: 700 }} />
                              ) : hit.existingSupplierId !== null ? null : (
                                <Checkbox
                                  checked={ticked}
                                  onChange={() => toggleHit(hit)}
                                  disabled={adoptSuppliers.isPending}
                                  slotProps={{ input: { "aria-label": `Add ${hit.name}` } }}
                                />
                              )}
                            </TableCell>
                            <TableCell>
                              <Stack direction="row" spacing={1} sx={{ alignItems: "center", flexWrap: "wrap" }}>
                                <Link href={hit.website} target="_blank" rel="noopener noreferrer" variant="body2" underline="hover" sx={{ fontWeight: 800 }}>
                                  {hit.name}
                                </Link>
                                <Chip
                                  size="small"
                                  variant="outlined"
                                  color={ROLE_CHIP_COLOR[hit.role]}
                                  label={hit.role}
                                  data-testid="discovery-role"
                                  sx={{ fontWeight: 700 }}
                                />
                                {hit.country && <Typography variant="caption" color="text.secondary">{hit.country}</Typography>}
                              </Stack>
                              <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>
                                {adopted ? (
                                  adopted.needsContactEmail ? (
                                    <>
                                      No email found — add one on the{" "}
                                      <Link component="button" type="button" variant="caption" underline="hover" onClick={() => navigate(`/suppliers/${adopted.supplierId}`)}>
                                        supplier page
                                      </Link>
                                      {" "}before asking.
                                    </>
                                  ) : adopted.contactEmail
                                ) : hit.existingSupplierId !== null ? (
                                  <Link component="button" type="button" variant="caption" underline="hover" onClick={() => navigate(`/suppliers/${hit.existingSupplierId}`)}>
                                    Already on your list
                                  </Link>
                                ) : (
                                  hit.contactEmail || hit.domain
                                )}
                              </Typography>
                            </TableCell>
                            <TableCell>
                              <Typography variant="body2" sx={{ maxWidth: 520, textWrap: "pretty" }}>{hit.why}</Typography>
                            </TableCell>
                          </TableRow>
                        );
                      })}
                    </TableBody>
                  </Table>
                </Box>
              </Paper>

              <Stack direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ mt: 2, justifyContent: "space-between", alignItems: { sm: "center" } }}>
                <Stack direction="row" spacing={2} sx={{ alignItems: "center" }}>
                  <Typography variant="body2" color="text.secondary" className="tabular-nums">{tickedHitCount} ticked</Typography>
                  {discovery.hasNextPage && (
                    <Button size="small" onClick={() => discovery.fetchNextPage()} disabled={discovery.isFetchingNextPage}>
                      {discovery.isFetchingNextPage ? "Loading…" : `Show ${Math.min(DISCOVERY_PAGE, discoveryRemaining)} more`}
                    </Button>
                  )}
                </Stack>
                <Tooltip describeChild title={tickedHitCount === 0 ? "Tick the suppliers you want on your list." : "Adds each ticked supplier to your list with this part in Tags. A manager approves them for RFQs before they can be asked."}>
                  <span tabIndex={tickedHitCount === 0 ? 0 : undefined}>
                    <Button
                      variant={tickedHitCount > 0 ? "contained" : "outlined"}
                      startIcon={<GroupAdd />}
                      disabled={tickedHitCount === 0 || adoptSuppliers.isPending}
                      onClick={() => adoptSuppliers.mutate(tickedHits)}
                    >
                      {adoptSuppliers.isPending ? "Adding…" : addLabel}
                    </Button>
                  </span>
                </Tooltip>
              </Stack>
            </>
          )}
        </Box>
      )}

      <Dialog open={previewOpen} onClose={() => setPreviewOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Send supplier RFQs</DialogTitle>
        <DialogContent dividers>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            Each supplier below gets its own numbered RFQ by email. Replies are captured on the sourcing workbench.
          </Typography>
          <Stack spacing={1}>
            {selectedCandidates.map((candidate) => (
              <FormControlLabel
                key={candidate.supplierId}
                control={<Checkbox checked readOnly />}
                label={`${candidate.supplierName} · ${candidate.contactEmail || "No contact email"}`}
              />
            ))}
          </Stack>
          <Typography id="supplier-rfq-preview-heading" variant="subtitle2" sx={{ mt: 3, mb: 1, fontWeight: 700 }}>
            What each supplier receives
          </Typography>
          <Paper
            component="section"
            variant="outlined"
            aria-labelledby="supplier-rfq-preview-heading"
            data-testid="supplier-rfq-preview"
            sx={{ p: 2 }}
          >
            <Typography variant="body2">{"Dear <supplier name>,"}</Typography>
            <Typography variant="body2" sx={{ mt: 1.5, textWrap: "pretty" }}>
              Your company invites you to submit a quotation for the following request. We&apos;d appreciate your best
              pricing and lead times.
            </Typography>
            {query.data && (
              <Table size="small" aria-label="The request as the supplier sees it" sx={{ mt: 1.5, "& td": { border: 0, px: 0, py: 0.5 } }}>
                <TableBody>
                  <TableRow>
                    <TableCell sx={{ color: "text.secondary", width: "40%" }}>Part number</TableCell>
                    <TableCell sx={{ fontWeight: 600 }}>{query.data.requestedPartNumber || "Not stated"}</TableCell>
                  </TableRow>
                  <TableRow>
                    <TableCell sx={{ color: "text.secondary" }}>Description</TableCell>
                    <TableCell>{query.data.description}</TableCell>
                  </TableRow>
                  <TableRow>
                    <TableCell sx={{ color: "text.secondary" }}>Maker</TableCell>
                    <TableCell>{query.data.manufacturer || "Not stated"}</TableCell>
                  </TableRow>
                  <TableRow>
                    <TableCell sx={{ color: "text.secondary" }}>Quantity</TableCell>
                    <TableCell className="tabular-nums">{quantityAskedLabel(query.data)}</TableCell>
                  </TableRow>
                  {query.data.requiredOn && (
                    <TableRow>
                      <TableCell sx={{ color: "text.secondary" }}>Needed by</TableCell>
                      <TableCell>{query.data.requiredOn.slice(0, 10)}</TableCell>
                    </TableRow>
                  )}
                </TableBody>
              </Table>
            )}
            <Typography variant="body2" sx={{ mt: 1.5 }}>
              Respond by: <strong>{respondByLabel(responseDueOn)}</strong>
            </Typography>
            <TextField
              label="Your message to the suppliers"
              value={supplierMessage}
              onChange={(event: React.ChangeEvent<HTMLInputElement>) => setSupplierMessage(event.target.value)}
              multiline
              minRows={3}
              maxRows={8}
              fullWidth
              sx={{ mt: 2 }}
              slotProps={{ htmlInput: { maxLength: SUPPLIER_MESSAGE_MAX } }}
              helperText={
                supplierMessage.trim()
                  ? "Goes to every supplier in this send, after the request details."
                  : `Leave it blank and the email says "${DEFAULT_SUPPLIER_MESSAGE}"`
              }
            />
          </Paper>
          <TextField
            type="datetime-local"
            label="Supplier response deadline (optional)"
            value={responseDueOn}
            onChange={(event: React.ChangeEvent<HTMLInputElement>) => setResponseDueOn(event.target.value)}
            fullWidth
            sx={{ mt: 2 }}
            slotProps={{ inputLabel: { shrink: true } }}
            helperText="Leave blank for no deadline. The supplier sees it on the RFQ."
          />
          <Alert severity="info" sx={{ mt: 2 }}>
            Your customer is not named, and their target prices and your margins are never included.
          </Alert>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setPreviewOpen(false)}>Cancel</Button>
          <Button
            variant="contained"
            startIcon={<Send />}
            onClick={() => prepareSupplierRfqs.mutate()}
            disabled={prepareSupplierRfqs.isPending || selectedSupplierIds.length === 0}
          >
            {prepareSupplierRfqs.isPending ? "Sending…" : `Send ${selectedSupplierIds.length} RFQ${selectedSupplierIds.length === 1 ? "" : "s"}`}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}

export default SourcingCasePage;
