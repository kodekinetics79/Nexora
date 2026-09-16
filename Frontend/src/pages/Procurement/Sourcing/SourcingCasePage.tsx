import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
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
import { ArrowBack, HowToReg, PersonAdd, PersonSearch, Refresh, Send } from "@mui/icons-material";
import { toast } from "react-hot-toast";
import procurementService, {
  type SourcingCaseCandidate,
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
          ? `No supplier on your list is linked to ${sourcingCase.requestedPartNumber || "this part"} yet. ${canAddSupplier
            ? "Add one with this part number or its maker in Tags, then press Refresh candidates."
            : "Ask someone who can add suppliers to add one with this part number or its maker in Tags."}`
          : noneReady
            ? (canApproveInline && approvableCandidates.length > 0
              ? "None of these suppliers can be asked yet. Press Approve for RFQs beside a supplier you trust; the list refreshes on its own."
              : "None of these suppliers can be asked yet. A manager approves them for RFQs on the supplier page, then press Refresh candidates.")
            : sourcingCase.nextAction}
        testId="sourcing-case-next-step"
        action={outreachAlreadySent
          ? <Button variant="contained" onClick={() => navigate(`/procurement/rfqs/${sourcingCase.rfqId}/sourcing`)}>Open sourcing workbench</Button>
          : noKnownSupplier && canAddSupplier
            ? (
              <Tooltip describeChild title="Opens the supplier form with this part number already in Tags, and brings you back here when it is saved.">
                <Button variant="contained" startIcon={<PersonAdd />} onClick={() => navigate(`/suppliers?new=1&tags=${encodeURIComponent(sourcingCase.requestedPartNumber ?? "")}&returnTo=${encodeURIComponent(`/procurement/sourcing-cases/${sourcingCase.id}`)}`)}>
                  Add a supplier
                </Button>
              </Tooltip>
            )
            : undefined}
      />

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
            Suppliers show here when their record names this part number or its maker in Tags, or when they quoted or supplied this product before. No internet search is run.
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
              {selectedSupplierIds.length > 0
                ? `Ask ${selectedSupplierIds.length} supplier${selectedSupplierIds.length === 1 ? "" : "s"}`
                : "Ask the ticked suppliers"}
            </Button>
          </span>
        </Tooltip>
      </Stack>
      {!canPrepare && whyNoPrepare && (
        <Alert severity="info" sx={{ mt: 2 }}>{whyNoPrepare}</Alert>
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
            Your customer's target prices and your margins are never included.
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
