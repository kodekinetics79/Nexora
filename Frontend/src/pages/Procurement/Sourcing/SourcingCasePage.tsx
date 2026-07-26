import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useParams } from "react-router-dom";
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
  FormControlLabel,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from "@mui/material";
import { ArrowBack, Refresh, Send } from "@mui/icons-material";
import { toast } from "react-hot-toast";
import procurementService, {
  type SourcingCaseCandidate,
} from "../../../api/services/procurementService";
import { useAuth } from "../../../context/AuthContext";

type CandidateLimit = 10 | 20 | 50;

const errorMessage = (error: any, fallback: string) =>
  error?.response?.data?.detail ||
  error?.response?.data?.message ||
  error?.response?.data?.title ||
  error?.message ||
  fallback;

const readableStatus = (value?: string | null) =>
  value?.trim() ? value.replaceAll("_", " ") : "Unknown";

const evidenceDate = (value?: string | null) =>
  value ? new Date(value).toLocaleDateString() : "No recorded activity";

const isCandidateReady = (candidate: SourcingCaseCandidate) =>
  candidate.eligibleForSupplierRfq !== false && Boolean(candidate.contactEmail);

function CandidateEvidence({ candidate }: { candidate: SourcingCaseCandidate }) {
  return (
    <Stack spacing={0.5}>
      <Chip size="small" variant="outlined" label={readableStatus(candidate.evidenceType)} sx={{ alignSelf: "flex-start" }} />
      <Typography variant="caption" color="text.secondary">{candidate.recommendationReason}</Typography>
      <Typography variant="caption" color="text.secondary">
        Evidence refreshed: {evidenceDate(candidate.evidenceFreshOn)}
      </Typography>
    </Stack>
  );
}

function SourcingCasePage() {
  const { caseId } = useParams<{ caseId: string }>();
  const sourcingCaseId = Number(caseId);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const [candidateLimit, setCandidateLimit] = useState<CandidateLimit>(10);
  const [selectedSupplierIds, setSelectedSupplierIds] = useState<number[]>([]);
  const [previewOpen, setPreviewOpen] = useState(false);

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
  const canPrepare =
    hasPermission("RFQ Management", "edit") &&
    hasPermission("Supplier History", "create");

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

  const prepareSupplierRfqs = useMutation({
    mutationFn: async () => {
      if (!query.data) throw new Error("The sourcing case is not loaded.");
      return procurementService.prepareSupplierRfqs(
        query.data.id,
        selectedSupplierIds,
        query.data.version,
        crypto.randomUUID(),
      );
    },
    onSuccess: (results) => {
      toast.success(`${results.length} Supplier RFQ${results.length === 1 ? "" : "s"} prepared.`);
      setPreviewOpen(false);
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
          <Button variant="outlined" aria-label="Back to Customer RFQs" onClick={() => navigate("/procurement/rfqs/all?state=requires-sourcing")} sx={{ minWidth: 40, px: 1 }}>
            <ArrowBack />
          </Button>
          <Box>
            <Typography variant="h5" component="h1" sx={{ fontWeight: 800 }}>Sourcing Case</Typography>
            <Stack direction="row" spacing={1} sx={{ mt: 0.5, alignItems: "center", flexWrap: "wrap" }}>
              <Typography variant="body2" sx={{ fontWeight: 700 }}>Customer RFQ #{sourcingCase.rfqId}</Typography>
              {sourcingCase.nexoraSerial && <Chip size="small" variant="outlined" label={sourcingCase.nexoraSerial} />}
              <Chip size="small" label={readableStatus(sourcingCase.status)} />
            </Stack>
          </Box>
        </Stack>
        <Button variant="outlined" onClick={() => navigate(`/procurement/rfqs/${sourcingCase.rfqId}/sourcing`)}>
          Open sourcing workbench
        </Button>
      </Stack>

      <Paper variant="outlined" sx={{ p: 2.5, mb: 3 }}>
        <Stack direction={{ xs: "column", md: "row" }} spacing={3} divider={<Divider flexItem orientation="vertical" />}>
          <Box sx={{ flex: 1 }}>
            <Typography variant="caption" color="text.secondary">PART / DESCRIPTION</Typography>
            <Typography sx={{ fontWeight: 800 }}>{sourcingCase.requestedPartNumber || "Unresolved part"}</Typography>
            <Typography variant="body2" color="text.secondary">{sourcingCase.description}</Typography>
          </Box>
          <Box>
            <Typography variant="caption" color="text.secondary">REQUESTED</Typography>
            <Typography sx={{ fontWeight: 800 }}>{sourcingCase.requestedQuantity}</Typography>
          </Box>
          <Box>
            <Typography variant="caption" color="text.secondary">COMPANY AVAILABLE</Typography>
            <Typography sx={{ fontWeight: 800 }}>{sourcingCase.stockQuantity}</Typography>
          </Box>
          <Box>
            <Typography variant="caption" color="text.secondary">TO SOURCE</Typography>
            <Typography color="error.main" sx={{ fontWeight: 800 }}>{sourcingCase.unfulfilledQuantity}</Typography>
          </Box>
        </Stack>
      </Paper>

      <Stack direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ alignItems: { sm: "center" }, justifyContent: "space-between", mb: 2 }}>
        <Box>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>Known Supplier candidates</Typography>
          <Typography variant="body2" color="text.secondary">
            Tenant records only. Approval, contact readiness, history, and freshness remain visible.
          </Typography>
        </Box>
        <Stack direction="row" spacing={1} sx={{ alignItems: "center", flexWrap: "wrap" }}>
          <ToggleButtonGroup exclusive size="small" value={candidateLimit} onChange={changeLimit} aria-label="Supplier candidate count">
            {[10, 20, 50].map((limit) => <ToggleButton key={limit} value={limit} aria-label={`Show ${limit} Supplier candidates`}>{limit}</ToggleButton>)}
          </ToggleButtonGroup>
          <Button startIcon={<Refresh />} onClick={() => refreshCandidates.mutate(candidateLimit)} disabled={refreshCandidates.isPending}>
            Refresh candidates
          </Button>
        </Stack>
      </Stack>

      {refreshCandidates.isError && (
        <Alert severity="error" sx={{ mb: 2 }}>Candidate refresh failed. The last verified result remains visible.</Alert>
      )}

      {candidates.length === 0 ? (
        <Paper variant="outlined" sx={{ p: 4, textAlign: "center" }}>
          <Typography sx={{ fontWeight: 700 }}>No known Supplier candidates found</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
            No tenant Supplier history matched this demand line. No external search was started.
          </Typography>
        </Paper>
      ) : (
        <Paper variant="outlined" sx={{ overflow: "hidden" }}>
          <Box sx={{ overflowX: "auto" }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell padding="checkbox">Select</TableCell>
                  <TableCell>Supplier</TableCell>
                  <TableCell>Approval / readiness</TableCell>
                  <TableCell>Commercial history</TableCell>
                  <TableCell>Evidence and freshness</TableCell>
                  <TableCell>RFQ readiness</TableCell>
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
                      <Typography variant="body2" sx={{ fontWeight: 800 }}>{candidate.supplierName}</Typography>
                      <Typography variant="caption" color="text.secondary">
                        {candidate.contactEmail || "No contact email"}
                      </Typography>
                    </TableCell>
                    <TableCell>
                      <Stack direction="row" spacing={0.5} sx={{ flexWrap: "wrap" }}>
                        <Chip size="small" variant="outlined" label={`Rank ${candidate.rank}`} />
                        <Chip size="small" variant="outlined" label={readableStatus(candidate.governanceStatus || candidate.approvalStatus)} />
                        <Chip size="small" variant="outlined" label={readableStatus(candidate.readinessStatus)} />
                      </Stack>
                    </TableCell>
                    <TableCell>
                      <Typography variant="body2">Persisted tenant relationship</Typography>
                      <Typography variant="caption" color="text.secondary">Evidence strength {candidate.evidenceScore}</Typography>
                    </TableCell>
                    <TableCell><CandidateEvidence candidate={candidate} /></TableCell>
                    <TableCell>
                      {isCandidateReady(candidate) ? (
                        <Chip size="small" color="success" label="Ready" />
                      ) : (
                        <Stack spacing={0.5}>
                          <Chip size="small" color="warning" label="Blocked" />
                          {(candidate.blockingReasons?.length ? candidate.blockingReasons : ["A dispatch contact is required."]).map((reason) => (
                            <Typography key={reason} variant="caption" color="text.secondary">{reason}</Typography>
                          ))}
                        </Stack>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Box>
        </Paper>
      )}

      <Stack direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ mt: 2, justifyContent: "space-between", alignItems: { sm: "center" } }}>
        <Typography variant="body2" color="text.secondary">
          {eligibleCandidates.length} of {candidates.length} candidates are ready · {selectedSupplierIds.length} selected
        </Typography>
        <Button
          variant="contained"
          startIcon={<Send />}
          disabled={!canPrepare || selectedSupplierIds.length === 0}
          onClick={() => setPreviewOpen(true)}
        >
          Prepare Supplier RFQ
        </Button>
      </Stack>
      {!canPrepare && (
        <Alert severity="info" sx={{ mt: 2 }}>Your role can review candidates but cannot prepare Supplier RFQs.</Alert>
      )}

      <Dialog open={previewOpen} onClose={() => setPreviewOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle>Prepare Supplier RFQs</DialogTitle>
        <DialogContent dividers>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            One governed Supplier RFQ will be prepared for each selected Supplier for this demand line.
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
          <Alert severity="info" sx={{ mt: 2 }}>
            Customer target prices and margin data are not included.
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
            {prepareSupplierRfqs.isPending ? "Preparing…" : "Create Supplier RFQs"}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}

export default SourcingCasePage;
