import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useParams } from "react-router-dom";
import {
  ArrowBack,
  CompareArrows,
  FactCheck,
  Gavel,
  Refresh,
} from "@mui/icons-material";
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  MenuItem,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from "@mui/material";
import { toast } from "react-hot-toast";
import { isAxiosError } from "axios";
import supplierQuoteService, {
  type NegotiationDisposition,
  type SupplierNegotiationRecommendation,
  type SupplierQuoteEvidence,
  type SupplierQuoteReviewStatus,
} from "../../../api/services/supplierQuoteService";
import CommercialProcessingEvidence from "../../../components/common/CommercialProcessingEvidence";
import { useAuth } from "../../../context/AuthContext";
import { formatWarrantyMonths } from "../../../utils/warrantyMonths";
import { formatMoney } from "../../../utils/currency";


const confidenceLabel = (confidence: number | null) =>
  confidence == null ? "Confidence unavailable" : `${Math.round(confidence * 100)}% confidence`;

const displayDate = (value?: string | null) =>
  value ? new Date(value).toLocaleDateString() : "Not stated";

/// Header fields a reviewer may correct at any time, not only while the field is flagged for
/// review. These are the round's cost build-up: they are what landed cost is made of, and a
/// captured zero is never flagged because a zero is usually correct.
const CORRECTABLE_HEADER_FIELDS = new Set([
  "FreightAmount", "TaxAmount", "DutyAmount", "OtherAmount", "DiscountAmount", "Incoterms",
]);

/// The D group delivers after import clearance, so the Supplier has paid the duty and it is inside
/// the price. Anything else and the duty is the buyer's — a zero there is a gap, not a fact. An
/// absent Incoterm is not flagged: domestic supply carries none and owes no duty, and warning on
/// all of it would teach reviewers to ignore the warning that matters.
const DELIVERED_INCOTERMS = new Set(["DAP", "DPU", "DDP"]);
const isDutyGap = (revision: { incoterms?: string | null; dutyAmount?: number }) => {
  const code = revision.incoterms?.trim().toUpperCase();
  return !!code && !DELIVERED_INCOTERMS.has(code) && (revision.dutyAmount ?? 0) === 0;
};

export default function SupplierQuoteReviewPage() {
  const id = Number(useParams().supplierQuoteId);
  const navigate = useNavigate();
  const client = useQueryClient();
  const { hasPermission } = useAuth();
  const canReview = hasPermission("Supplier History", "edit");
  const canNegotiate = hasPermission("Supplier Negotiation", "edit");
  const [selected, setSelected] = useState<{
    revisionId: number;
    evidence: SupplierQuoteEvidence;
  } | null>(null);
  const [status, setStatus] = useState<SupplierQuoteReviewStatus>("ACCEPTED");
  const [correctedValue, setCorrectedValue] = useState("");
  const [reason, setReason] = useState("");
  const [recommendation, setRecommendation] =
    useState<SupplierNegotiationRecommendation | null>(null);
  const [disposition, setDisposition] =
    useState<NegotiationDisposition>("PREPARED");
  const [decisionReason, setDecisionReason] = useState("");
  const [decisionKey, setDecisionKey] = useState("");

  const query = useQuery({
    queryKey: ["supplier-quote", id],
    queryFn: () => supplierQuoteService.getById(id),
    enabled: id > 0,
  });
  const negotiationQuery = useQuery({
    queryKey: ["supplier-quote-negotiation", id],
    queryFn: () => supplierQuoteService.getNegotiation(id),
    enabled: id > 0,
    retry: 1,
  });
  const mutation = useMutation({
    mutationFn: () =>
      supplierQuoteService.reviewEvidence(
        id,
        selected!.revisionId,
        selected!.evidence.id,
        {
          status,
          correctedValue: status === "CORRECTED" ? correctedValue : null,
          reason,
        },
      ),
    onSuccess: () => {
      toast.success("Review decision recorded");
      setSelected(null);
      setReason("");
      setCorrectedValue("");
      void client.invalidateQueries({ queryKey: ["supplier-quote", id] });
      void client.invalidateQueries({ queryKey: ["supplier-quote-inbox"] });
    },
    onError: () => toast.error("Review decision could not be saved"),
  });
  const projection = useMutation({
    mutationFn: () => supplierQuoteService.projectForComparison(id, query.data!.version),
    onSuccess: () => {
      toast.success("Supplier Quote is available in offer comparison");
      navigate(`/procurement/rfqs/${query.data!.rfqId}/sourcing`);
    },
    onError: () => toast.error("Supplier Quote could not be added to comparison"),
  });
  const decisionMutation = useMutation({
    mutationFn: () =>
      supplierQuoteService.recordNegotiationDecision(
        id,
        {
          recommendationCode: recommendation!.code,
          disposition,
          reason: decisionReason.trim(),
          expectedQuoteVersion: negotiationQuery.data!.quoteVersion,
        },
        decisionKey,
      ),
    onSuccess: () => {
      toast.success("Negotiation decision recorded");
      setRecommendation(null);
      setDecisionReason("");
      setDisposition("PREPARED");
      setDecisionKey("");
      void client.invalidateQueries({ queryKey: ["supplier-quote-negotiation", id] });
      void client.invalidateQueries({ queryKey: ["supplier-quote", id] });
    },
    onError: async (error) => {
      if (isAxiosError(error) && error.response?.status === 409) {
        setRecommendation(null);
        setDecisionReason("");
        setDisposition("PREPARED");
        setDecisionKey("");
        await client.invalidateQueries({ queryKey: ["supplier-quote-negotiation", id] });
        await negotiationQuery.refetch();
        toast.error("Supplier Quote changed; guidance refreshed. Review and try again.");
        return;
      }
      toast.error("Negotiation decision could not be recorded");
    },
  });

  const openDecision = (item: SupplierNegotiationRecommendation) => {
    setRecommendation(item);
    setDisposition("PREPARED");
    setDecisionReason("");
    setDecisionKey(crypto.randomUUID());
  };

  if (query.isLoading)
    return (
      <Box sx={{ p: 5, textAlign: "center" }} aria-label="Loading Supplier Quote">
        <CircularProgress />
      </Box>
    );
  if (query.isError || !query.data)
    return (
      <Box sx={{ p: 3 }}>
        <Alert
          severity="error"
          action={
            <Button color="inherit" startIcon={<Refresh />} onClick={() => void query.refetch()}>
              Retry
            </Button>
          }
        >
          Supplier Quote could not be loaded.
        </Alert>
      </Box>
    );

  const quote = query.data;
  const negotiation = negotiationQuery.data;
  const currentRound = negotiation?.currentRound;
  return (
    <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1400, mx: "auto" }}>
      <Button
        startIcon={<ArrowBack />}
        onClick={() => navigate("/procurement/supplier-quotes")}
        sx={{ mb: 1 }}
      >
        Supplier Quote Inbox
      </Button>
      <Stack
        direction={{ xs: "column", md: "row" }}
        sx={{ justifyContent: "space-between", gap: 2, mb: 3 }}
      >
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 800 }}>
            {quote.supplierQuoteReference}
          </Typography>
          <Typography color="text.secondary">
            {quote.supplierName} · {quote.nexoraSerial} · Sourcing Case {quote.sourcingCaseId}
          </Typography>
        </Box>
        <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ alignItems: { sm: "center" } }}>
          <Chip
            color={quote.inboxStatus === "READY_FOR_COMPARISON" ? "success" : "warning"}
            label={quote.inboxStatus.replaceAll("_", " ")}
          />
          {quote.inboxStatus === "READY_FOR_COMPARISON" && canReview && (
            <Button
              variant="contained"
              startIcon={<CompareArrows />}
              disabled={projection.isPending}
              onClick={() => projection.mutate()}
            >
              Compare offer
            </Button>
          )}
        </Stack>
      </Stack>

      {!canReview && (
        <Alert severity="info" sx={{ mb: 2 }}>
          Supplier History edit permission is required to review evidence or project this offer.
        </Alert>
      )}
      <CommercialProcessingEvidence resource="supplier-quotes" id={quote.supplierQuoteId} />

      <Paper component="section" variant="outlined" aria-labelledby="negotiation-heading" sx={{ mb: 2, p: { xs: 2, md: 2.5 } }}>
        <Stack direction={{ xs: "column", md: "row" }} sx={{ justifyContent: "space-between", gap: 1, mb: 2 }}>
          <Box>
            <Typography id="negotiation-heading" variant="h6" sx={{ fontWeight: 800 }}>
              Bid quality and negotiation guidance
            </Typography>
            <Typography variant="body2" color="text.secondary">
              Observed Supplier terms and evidence-backed guidance for the current Quote round.
            </Typography>
          </Box>
          {currentRound && <Chip label={`Round ${currentRound.roundNumber}`} variant="outlined" />}
        </Stack>

        {negotiationQuery.isLoading && (
          <Stack direction="row" spacing={1.5} sx={{ alignItems: "center", py: 3 }}>
            <CircularProgress size={22} />
            <Typography>Loading bid quality and negotiation guidance…</Typography>
          </Stack>
        )}
        {negotiationQuery.isError && (
          <Alert
            severity="error"
            action={
              <Button color="inherit" startIcon={<Refresh />} onClick={() => void negotiationQuery.refetch()}>
                Retry
              </Button>
            }
          >
            Negotiation guidance could not be loaded. No decision can be recorded until it is available.
          </Alert>
        )}
        {negotiation && (
          <Stack spacing={2.5}>
            {currentRound ? (
              <Box>
                <Typography sx={{ fontWeight: 700, mb: 1 }}>Current commercial terms</Typography>
                <Box
                  sx={{
                    display: "grid",
                    gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))", lg: "repeat(4, minmax(0, 1fr))" },
                    gap: 1.5,
                  }}
                >
                  {[
                    ["Currency", currentRound.currencyCode || "Not stated"],
                    ["Valid until", displayDate(currentRound.validUntil)],
                    ["Incoterms", currentRound.incoterms || "Not stated"],
                    ["Payment terms", currentRound.paymentTerms || "Not stated"],
                    ["Freight", currentRound.freightAmount == null ? "Not stated" : formatMoney(currentRound.freightAmount, currentRound.currencyCode)],
                    ["Tax", currentRound.taxAmount == null ? "Not stated" : formatMoney(currentRound.taxAmount, currentRound.currencyCode)],
                  ].map(([label, value]) => (
                    <Box key={label}>
                      <Typography variant="caption" color="text.secondary">{label}</Typography>
                      <Typography sx={{ fontWeight: 700, overflowWrap: "anywhere" }}>{value}</Typography>
                    </Box>
                  ))}
                </Box>
              </Box>
            ) : (
              <Alert severity="info">No current negotiation round is available for this Supplier Quote.</Alert>
            )}

            <Divider />
            <Box>
              <Typography sx={{ fontWeight: 700, mb: 1 }}>Bid quality</Typography>
              {negotiation.bidQuality.length === 0 ? (
                <Alert severity="success">No bid-quality exceptions were reported for the current round.</Alert>
              ) : (
                <Stack spacing={1}>
                  {negotiation.bidQuality.map((flag) => (
                    <Box component="details" key={flag.code} sx={{ borderBottom: "1px solid", borderColor: "divider", pb: 1 }}>
                      <Box component="summary" sx={{ cursor: "pointer", py: 0.5 }}>
                        <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ alignItems: { sm: "center" } }}>
                          <Typography sx={{ fontWeight: 700 }}>{flag.label}</Typography>
                          <Chip
                            size="small"
                            color={flag.severity === "CRITICAL" ? "error" : flag.severity === "WARNING" ? "warning" : "info"}
                            label={flag.blocking ? `${flag.severity} · BLOCKING` : flag.severity}
                          />
                          <Chip size="small" variant="outlined" label={confidenceLabel(flag.confidence ?? null)} />
                          <Chip size="small" variant="outlined" label={`Evidence ${flag.sampleSize ?? flag.evidence.length}`} />
                        </Stack>
                      </Box>
                      <Box sx={{ pl: { sm: 2 }, pt: 1 }}>
                        <Typography variant="body2">{flag.explanation}</Typography>
                        {flag.evidence.map((item) => (
                          <Typography key={`${item.label}-${item.value}`} variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                            <strong>{item.label}:</strong> {item.value}{item.source ? ` · ${item.source}` : ""}
                          </Typography>
                        ))}
                        {flag.limitations.map((item) => (
                          <Typography key={item} variant="caption" color="text.secondary" sx={{ display: "block", mt: 0.5 }}>
                            Limitation: {item}
                          </Typography>
                        ))}
                      </Box>
                    </Box>
                  ))}
                </Stack>
              )}
            </Box>

            <Divider />
            <Box>
              <Typography sx={{ fontWeight: 700, mb: 1 }}>Negotiation guidance</Typography>
              {negotiation.decisionsTruncated && (
                <Alert severity="info" sx={{ mb: 1.5 }}>
                  Showing the latest {negotiation.decisions.length} of {negotiation.decisionTotal} negotiation decisions.
                </Alert>
              )}
              {negotiation.recommendations.length === 0 ? (
                <Alert severity="info">No evidence-backed negotiation recommendation is available.</Alert>
              ) : (
                <Stack spacing={1.5}>
                  {negotiation.recommendations.map((item) => {
                    const previousDecision = negotiation.decisions.find(
                      (decision) => decision.recommendationCode === item.code &&
                        decision.supplierQuoteRevisionId === quote.revisions.find(
                          (revision) => revision.revisionNumber === quote.currentRevisionNumber,
                        )?.revisionId,
                    );
                    return (
                      <Box key={item.code} sx={{ borderBottom: "1px solid", borderColor: "divider", pb: 1.5 }}>
                        <Stack direction={{ xs: "column", md: "row" }} sx={{ justifyContent: "space-between", gap: 1 }}>
                          <Box sx={{ minWidth: 0 }}>
                            <Typography sx={{ fontWeight: 700 }}>{item.title}</Typography>
                            <Typography variant="body2">{item.summary}</Typography>
                            <Stack direction="row" spacing={1} useFlexGap sx={{ mt: 1, flexWrap: "wrap" }}>
                              <Chip size="small" label={confidenceLabel(item.confidence)} />
                              <Chip size="small" variant="outlined" label={`Evidence ${item.sampleSize ?? item.evidence.length}`} />
                              {item.targetValue != null && (
                                <Chip size="small" color="info" label={`Target ${item.targetUnit ? `${item.targetValue} ${item.targetUnit}` : item.targetValue}`} />
                              )}
                              {previousDecision && (
                                <Chip size="small" color="success" label={previousDecision.disposition.replaceAll("_", " ")} />
                              )}
                            </Stack>
                          </Box>
                          {canNegotiate && !previousDecision && (
                            <Button startIcon={<Gavel />} onClick={() => openDecision(item)} sx={{ alignSelf: { md: "flex-start" } }}>
                              Record decision
                            </Button>
                          )}
                        </Stack>
                        <Box component="details" sx={{ mt: 1 }}>
                          <Box component="summary" sx={{ cursor: "pointer", fontWeight: 700 }}>
                            Evidence and limitations
                          </Box>
                          <Typography variant="body2" sx={{ mt: 1 }}>{item.rationale}</Typography>
                          {item.evidence.map((evidence) => (
                            <Typography key={`${evidence.label}-${evidence.value}`} variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                              <strong>{evidence.label}:</strong> {evidence.value}{evidence.source ? ` · ${evidence.source}` : ""}
                            </Typography>
                          ))}
                          {item.constraints.map((constraint) => (
                            <Typography key={constraint} variant="body2" sx={{ mt: 0.5 }}>Constraint: {constraint}</Typography>
                          ))}
                          {item.limitations.map((limitation) => (
                            <Typography key={limitation} variant="caption" color="text.secondary" sx={{ display: "block", mt: 0.5 }}>
                              Limitation: {limitation}
                            </Typography>
                          ))}
                        </Box>
                      </Box>
                    );
                  })}
                </Stack>
              )}
              {!canNegotiate && negotiation.recommendations.length > 0 && (
                <Alert severity="info" sx={{ mt: 1.5 }}>
                  Supplier Negotiation edit permission is required to record a decision.
                </Alert>
              )}
            </Box>
          </Stack>
        )}
      </Paper>

      {quote.revisions.slice().reverse().map((revision) => (
        <Paper variant="outlined" key={revision.revisionId} sx={{ mb: 2, p: 2 }}>
          <Stack direction={{ xs: "column", sm: "row" }} sx={{ justifyContent: "space-between", gap: 1, mb: 2 }}>
            <Typography variant="h6">Revision {revision.revisionNumber}</Typography>
            <Typography color="text.secondary">
              {revision.captureChannel.replaceAll("_", " ")} · {new Date(revision.capturedOn).toLocaleString()}
            </Typography>
          </Stack>
          {/* The charge block. Landed cost is built from these, and until now the review screen
              showed lines and evidence only — so the reviewer asked to confirm a Supplier response
              could not see the freight, and could not see that the duty was zero on an EXW round. */}
          <Typography sx={{ fontWeight: 700, mb: 1 }}>Round charges</Typography>
          <Stack direction="row" sx={{ flexWrap: "wrap", gap: 1, mb: 1 }}>
            <Chip size="small" variant="outlined" label={`Incoterm ${revision.incoterms ?? "not stated"}`} />
            <Chip size="small" variant="outlined" label={`Freight ${(revision.freightAmount ?? 0).toLocaleString()}`} />
            <Chip size="small" variant="outlined" label={`Supplier tax ${(revision.taxAmount ?? 0).toLocaleString()}`} />
            <Chip size="small" variant="outlined" color={isDutyGap(revision) ? "warning" : "default"}
              label={`Duty ${(revision.dutyAmount ?? 0).toLocaleString()}`} />
            <Chip size="small" variant="outlined" label={`Other ${(revision.otherAmount ?? 0).toLocaleString()}`} />
            <Chip size="small" variant="outlined" label={`Discount ${(revision.discountAmount ?? 0).toLocaleString()}`} />
          </Stack>
          {isDutyGap(revision) && (
            <Alert severity="warning" sx={{ mb: 2 }}>
              {revision.incoterms} is not a delivered term, so the Supplier has not cleared KSA customs and
              the duty is not inside this price — but this round records no duty. Correct the DutyAmount
              below if the buyer knows it; landed cost, and every price derived from it, is understated until then.
            </Alert>
          )}
          <Typography sx={{ fontWeight: 700, mb: 1 }}>Commercial lines</Typography>
          <TableContainer>
            <Table size="small">
              <TableHead><TableRow><TableCell>Line</TableCell><TableCell>Part / Description</TableCell><TableCell align="right">Quantity</TableCell><TableCell align="right">Available</TableCell><TableCell align="right">Unit price</TableCell><TableCell align="right">Lead time</TableCell><TableCell align="right">Warranty</TableCell></TableRow></TableHead>
              <TableBody>
                {revision.lines.map((line) => (
                  <TableRow key={line.id}>
                    <TableCell>{line.lineNumber}</TableCell>
                    <TableCell>{line.partNumber ?? "No part number"}<Typography variant="caption" sx={{ display: "block" }}>{line.description}</Typography></TableCell>
                    <TableCell align="right">{line.quantity}</TableCell>
                    <TableCell align="right">{line.availableQuantity ?? "Not stated"}</TableCell>
                    <TableCell align="right">
                      {revision.revisionNumber === quote.currentRevisionNumber && currentRound?.currencyCode
                        ? formatMoney(line.unitPrice, currentRound.currencyCode)
                        : `Currency ${revision.currencyId} ${line.unitPrice.toLocaleString(undefined, { maximumFractionDigits: 2 })}`}
                    </TableCell>
                    <TableCell align="right">{line.leadTimeDays == null ? "Not stated" : `${line.leadTimeDays} days`}</TableCell>
                    {/* The reviewer is the one person positioned to challenge either warranty value,
                        so both are shown: the period the comparison can rank, and the wording it
                        cannot. A captured value with no reader is how a field quietly stops meaning
                        anything. */}
                    <TableCell align="right">
                      {formatWarrantyMonths(line.warrantyMonths) ?? (line.warranty ? null : "Not stated")}
                      {line.warranty && (
                        <Typography variant="caption" sx={{ display: "block" }}>{line.warranty}</Typography>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
          <Typography sx={{ fontWeight: 700, mt: 3, mb: 1 }}>Field evidence</Typography>
          <TableContainer>
            <Table size="small">
              <TableHead><TableRow><TableCell>Field</TableCell><TableCell>Source value</TableCell><TableCell>Normalized</TableCell><TableCell>Confidence</TableCell><TableCell>Method</TableCell><TableCell>Decision</TableCell><TableCell align="right">Action</TableCell></TableRow></TableHead>
              <TableBody>
                {revision.evidence.map((evidence) => (
                  <TableRow key={evidence.id}>
                    <TableCell>{evidence.fieldName}</TableCell>
                    <TableCell>{evidence.originalValue ?? "Not present"}</TableCell>
                    <TableCell>{evidence.correctedValue ?? evidence.normalizedValue ?? "Not resolved"}</TableCell>
                    <TableCell>{Math.round(evidence.confidence * 100)}%</TableCell>
                    <TableCell>{evidence.method.replaceAll("_", " ")}</TableCell>
                    <TableCell><Chip size="small" color={evidence.latestReviewStatus ? "success" : evidence.reviewRequired ? "warning" : "default"} label={evidence.latestReviewStatus ?? (evidence.reviewRequired ? "REVIEW REQUIRED" : "NO REVIEW")} /></TableCell>
                    <TableCell align="right">
                      {/* Round charges stay correctable after they have been decided once. A
                          captured charge is never "review required" — a zero freight is usually the
                          truth — but it is the field most often wrong, and gating correction on
                          reviewRequired left the only control over it unreachable from the screen
                          that shows it. */}
                      {canReview && (CORRECTABLE_HEADER_FIELDS.has(evidence.fieldName)
                        || (evidence.reviewRequired && !evidence.latestReviewStatus)) && (
                        <Button size="small" startIcon={<FactCheck />} onClick={() => setSelected({ revisionId: revision.revisionId, evidence })}>Review</Button>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
        </Paper>
      ))}

      <Dialog open={Boolean(selected)} onClose={() => setSelected(null)} fullWidth maxWidth="sm">
        <DialogTitle>Record review decision</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2}>
            <Alert severity="info">The source evidence remains immutable. This decision is appended to the audit history.</Alert>
            <TextField label="Field" value={selected?.evidence.fieldName ?? ""} disabled />
            <TextField select label="Decision" value={status} onChange={(event) => setStatus(event.target.value as SupplierQuoteReviewStatus)}>
              <MenuItem value="ACCEPTED">Accept extracted value</MenuItem>
              <MenuItem value="CORRECTED">Record correction</MenuItem>
              <MenuItem value="REJECTED">Reject value</MenuItem>
            </TextField>
            {status === "CORRECTED" && <TextField required label="Corrected value" value={correctedValue} onChange={(event) => setCorrectedValue(event.target.value)} />}
            <TextField required multiline minRows={3} label="Review reason" value={reason} onChange={(event) => setReason(event.target.value)} />
            {mutation.isError && <Alert severity="error">Review decision could not be saved.</Alert>}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setSelected(null)}>Cancel</Button>
          <Button variant="contained" disabled={!reason.trim() || (status === "CORRECTED" && !correctedValue.trim()) || mutation.isPending} onClick={() => mutation.mutate()}>Record decision</Button>
        </DialogActions>
      </Dialog>

      <Dialog open={Boolean(recommendation)} onClose={() => setRecommendation(null)} fullWidth maxWidth="sm">
        <DialogTitle>Record negotiation decision</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2}>
            <Alert severity="info">
              This records a governed decision only. It does not contact the Supplier or create an award.
            </Alert>
            <TextField label="Recommendation" value={recommendation?.title ?? ""} disabled />
            <TextField
              select
              label="Disposition"
              value={disposition}
              onChange={(event) => setDisposition(event.target.value as NegotiationDisposition)}
            >
              <MenuItem value="PREPARED">Prepare for negotiation</MenuItem>
              <MenuItem value="DEFERRED">Defer</MenuItem>
              <MenuItem value="DISMISSED">Dismiss</MenuItem>
            </TextField>
            <TextField
              required
              multiline
              minRows={3}
              label="Decision reason"
              value={decisionReason}
              onChange={(event) => setDecisionReason(event.target.value)}
            />
            {decisionMutation.isError && (
              <Alert severity="error">
                The decision was not recorded. Refresh the guidance if the Quote changed, then retry.
              </Alert>
            )}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setRecommendation(null)}>Cancel</Button>
          <Button
            variant="contained"
            disabled={!decisionReason.trim() || decisionMutation.isPending || !decisionKey}
            onClick={() => decisionMutation.mutate()}
          >
            Record decision
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
