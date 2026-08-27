import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle, Paper, Stack, Tab, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Tabs, TextField, Typography } from "@mui/material";
import { Refresh } from "@mui/icons-material";
import { Ban, CheckCircle2, RotateCcw } from "lucide-react";
import { useSnackbar } from "notistack";
import commercialLearningService, { type CurrencyValueSummary, type LearningGovernanceAction, type LearningSignal } from "../../api/services/commercialLearningService";
import { useAuth } from "../../context/AuthContext";

const value = (group?: CurrencyValueSummary) => group?.medianValue == null ? "No verified sample" :
  `${group.currencyCode} ${group.medianValue.toLocaleString(undefined, { maximumFractionDigits: 4 })} (${group.sampleSize})`;

const readable = (text?: string | null) => (text || "Not recorded").replaceAll("_", " ").toLowerCase().replace(/^./, (letter) => letter.toUpperCase());

interface GovernanceDialogState {
  signal: LearningSignal;
  action: LearningGovernanceAction;
}

export default function CommercialMemoryPage() {
  const [tab, setTab] = useState(0);
  const [governanceDialog, setGovernanceDialog] = useState<GovernanceDialogState | null>(null);
  const [governanceReason, setGovernanceReason] = useState("");
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission } = useAuth();
  const canGovernLearning = hasPermission("Dashboard", "edit");
  const openGovernanceDialog = (signal: LearningSignal, action: LearningGovernanceAction) => {
    setGovernanceReason("");
    setGovernanceDialog({ signal, action });
  };
  const closeGovernanceDialog = () => {
    setGovernanceDialog(null);
    setGovernanceReason("");
  };
  const products = useQuery({ queryKey: ["commercial-learning", "products"], queryFn: () => commercialLearningService.getProducts() });
  const suppliers = useQuery({ queryKey: ["commercial-learning", "suppliers"], queryFn: () => commercialLearningService.getSuppliers() });
  const customers = useQuery({ queryKey: ["commercial-learning", "customers"], queryFn: () => commercialLearningService.getCustomers() });
  const salesReps = useQuery({ queryKey: ["commercial-learning", "sales-reps"], queryFn: () => commercialLearningService.getSalesReps() });
  const studio = useQuery({ queryKey: ["commercial-learning", "studio"], queryFn: commercialLearningService.getStudio });
  const governanceMutation = useMutation({
    mutationFn: () => commercialLearningService.governSignal(
      governanceDialog!.signal.signalId,
      governanceDialog!.action,
      { reason: governanceReason.trim(), expectedVersion: governanceDialog!.signal.governanceVersion },
    ),
    onSuccess: async () => {
      const action = governanceDialog?.action ?? "approve";
      closeGovernanceDialog();
      await queryClient.invalidateQueries({ queryKey: ["commercial-learning", "studio"] });
      enqueueSnackbar(`Learning signal ${action === "approve" ? "approved" : action === "disable" ? "disabled" : "restored to its previous state"}.`, { variant: "success" });
    },
    onError: () => enqueueSnackbar("The learning decision could not be recorded. Refresh the signal and try again.", { variant: "error" }),
  });
  const loading = products.isLoading || suppliers.isLoading || customers.isLoading || salesReps.isLoading || studio.isLoading;
  const failed = products.isError || suppliers.isError || customers.isError || salesReps.isError || studio.isError;
  if (loading) return <Box sx={{ minHeight: "60vh", display: "grid", placeItems: "center" }}><CircularProgress /></Box>;
  return <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1500, mx: "auto" }}>
    <Stack direction={{ xs: "column", md: "row" }} spacing={2} sx={{ justifyContent: "space-between", mb: 2 }}>
      <Box><Typography variant="h5" sx={{ fontWeight: 800 }}>Commercial Memory</Typography><Typography color="text.secondary">Verified outcomes, pricing evidence, demand, and approved corrections</Typography></Box>
      <Button startIcon={<Refresh />} onClick={() => { void products.refetch(); void suppliers.refetch(); void customers.refetch(); void salesReps.refetch(); void studio.refetch(); }}>Refresh</Button>
    </Stack>
    {failed && <Alert severity="error" sx={{ mb: 2 }}>Commercial evidence could not be loaded.</Alert>}
    <Paper variant="outlined" sx={{ mb: 2 }}><Tabs value={tab} onChange={(_, next) => setTab(next)} variant="scrollable"><Tab label="Product memory" /><Tab label="Supplier evaluation" /><Tab label="Sales Rep evaluation" /><Tab label="Customer outcomes" /><Tab label="Learning Studio" /></Tabs></Paper>
    {tab === 0 && <TableContainer component={Paper} variant="outlined"><Table size="small"><TableHead><TableRow><TableCell>Product</TableCell><TableCell align="right">Requested</TableCell><TableCell align="right">Quoted</TableCell><TableCell align="right">Decided</TableCell><TableCell align="right">Won / Lost / Pending</TableCell><TableCell align="right">Win rate</TableCell><TableCell>Last won context</TableCell><TableCell>Won selling price median</TableCell><TableCell>Supplier landed median</TableCell><TableCell>Evidence period</TableCell></TableRow></TableHead><TableBody>
      {(products.data ?? []).map((row) => <TableRow key={row.productId}><TableCell><Typography sx={{ fontWeight: 700 }}>{row.partNumber}</Typography><Typography variant="caption">{row.productName}</Typography></TableCell><TableCell align="right">{row.timesRequested}</TableCell><TableCell align="right">{row.timesQuoted}</TableCell><TableCell align="right">{row.decidedCount}</TableCell><TableCell align="right">{row.wonCount} / {row.lostCount} / {row.pendingCount}</TableCell><TableCell align="right">{row.lineWinRatePercent == null ? "Insufficient evidence" : `${row.lineWinRatePercent}%`}</TableCell><TableCell>{row.lastWonContext == null ? "No verified win" : <>{row.lastWonContext.customerQuoteNumber}: {row.lastWonContext.quantity} at {row.lastWonContext.currencyCode} {row.lastWonContext.unitPrice}<Typography variant="caption" sx={{ display: "block" }}>{row.lastWonContext.deliveryLeadTimeDays == null ? "Lead time not recorded" : `${row.lastWonContext.deliveryLeadTimeDays} days`}</Typography></>}</TableCell><TableCell>{value(row.wonSellingPrices[0])}</TableCell><TableCell>{value(row.supplierLandedCosts[0])}</TableCell><TableCell>{new Date(row.periodFrom).toLocaleDateString()} to {new Date(row.periodTo).toLocaleDateString()}<Typography variant="caption" sx={{ display: "block" }}>{row.stockoutBlockedCount} stockout-blocked</Typography></TableCell></TableRow>)}
      {(products.data?.length ?? 0) === 0 && <TableRow><TableCell colSpan={10} align="center">No Product outcome evidence is available yet.</TableCell></TableRow>}
    </TableBody></Table></TableContainer>}
    {tab === 1 && <TableContainer component={Paper} variant="outlined"><Table size="small"><TableHead><TableRow><TableCell>Supplier</TableCell><TableCell align="right">Quote revisions</TableCell><TableCell align="right">Bid completeness</TableCell><TableCell align="right">Eligible offers</TableCell><TableCell align="right">Selected</TableCell><TableCell align="right">Customer Orders supported</TableCell><TableCell align="right">Average response</TableCell><TableCell>Landed-cost median</TableCell><TableCell>Bid quality findings</TableCell></TableRow></TableHead><TableBody>
      {(suppliers.data ?? []).map((row) => <TableRow key={row.supplierId}><TableCell sx={{ fontWeight: 700 }}>{row.supplierName}</TableCell><TableCell align="right">{row.quoteRevisions}</TableCell><TableCell align="right">{row.bidQuality.completenessPercent == null ? "No sample" : `${row.bidQuality.completenessPercent}% (${row.bidQuality.completeOfferCount}/${row.bidQuality.offerCount})`}</TableCell><TableCell align="right">{row.bidQuality.eligibleOfferCount}</TableCell><TableCell align="right">{row.selectedOfferCount}</TableCell><TableCell align="right">{row.supportedWonCount}</TableCell><TableCell align="right">{row.averageResponseDays == null ? "No verified sample" : `${row.averageResponseDays} days`}</TableCell><TableCell>{value(row.landedCosts[0])}</TableCell><TableCell>{row.bidQuality.flags.length === 0 ? "No current exception" : row.bidQuality.flags.slice(0, 3).map((flag) => flag.code.replaceAll("_", " ")).join(", ")}</TableCell></TableRow>)}
      {(suppliers.data?.length ?? 0) === 0 && <TableRow><TableCell colSpan={9} align="center">No verified Supplier Quote evidence is available yet.</TableCell></TableRow>}
    </TableBody></Table></TableContainer>}
    {tab === 2 && <TableContainer component={Paper} variant="outlined"><Table size="small"><TableHead><TableRow><TableCell>Sales Rep</TableCell><TableCell align="right">Owned / weighted coverage</TableCell><TableCell align="right">Won / Lost</TableCell><TableCell align="right">Conversion / value conversion</TableCell><TableCell align="right">First action / Quote turnaround</TableCell><TableCell align="right">Follow-up completion</TableCell><TableCell align="right">Insights</TableCell><TableCell>Evidence-based coaching</TableCell></TableRow></TableHead><TableBody>
      {(salesReps.data ?? []).map((row) => <TableRow key={row.salesRepUserId}><TableCell sx={{ fontWeight: 700 }}>{row.salesRepName}</TableCell><TableCell align="right">{row.ownedOpportunities} / {row.weightedCoverage}</TableCell><TableCell align="right">{row.wonCount} / {row.lostCount}</TableCell><TableCell align="right">{row.conversionRatePercent == null ? "Limited evidence" : `${row.conversionRatePercent}%`} / {row.valueConversionPercent == null ? "—" : `${row.valueConversionPercent}%`}</TableCell><TableCell align="right">{row.firstMeaningfulActionHours == null ? "—" : `${row.firstMeaningfulActionHours}h`} / {row.quoteTurnaroundHours == null ? "—" : `${row.quoteTurnaroundHours}h`}</TableCell><TableCell align="right">{row.followUpCompletionPercent == null ? "No closed sample" : `${row.followUpCompletionPercent}%`}</TableCell><TableCell align="right">{row.insightCaptureCount}</TableCell><TableCell>{row.coachingOpportunity}</TableCell></TableRow>)}
      {(salesReps.data?.length ?? 0) === 0 && <TableRow><TableCell colSpan={8} align="center">No assigned opportunity outcomes are available yet.</TableCell></TableRow>}
    </TableBody></Table></TableContainer>}
    {tab === 3 && <TableContainer component={Paper} variant="outlined"><Table size="small"><TableHead><TableRow><TableCell>Customer</TableCell><TableCell align="right">Inquiries</TableCell><TableCell align="right">Quotes</TableCell><TableCell align="right">Won / Lost / Pending</TableCell><TableCell align="right">Conversion</TableCell><TableCell>Won value median</TableCell><TableCell>Recorded loss factors</TableCell><TableCell>Evidence</TableCell></TableRow></TableHead><TableBody>
      {(customers.data ?? []).map((row) => <TableRow key={row.customerId}><TableCell sx={{ fontWeight: 700 }}>{row.customerName}</TableCell><TableCell align="right">{row.inquiryCount}</TableCell><TableCell align="right">{row.quoteCount}</TableCell><TableCell align="right">{row.wonCount} / {row.lostCount} / {row.pendingCount}</TableCell><TableCell align="right">{row.conversionRatePercent == null ? "Insufficient evidence" : `${row.conversionRatePercent}%`}</TableCell><TableCell>{value(row.wonValues[0])}</TableCell><TableCell>{row.lossReasons.length === 0 ? "None recorded" : row.lossReasons.map((reason) => `${reason.label}: ${reason.count}`).join(", ")}</TableCell><TableCell>{row.evidence.length} linked records</TableCell></TableRow>)}
      {(customers.data?.length ?? 0) === 0 && <TableRow><TableCell colSpan={8} align="center">No Customer Quote outcomes are available yet.</TableCell></TableRow>}
    </TableBody></Table></TableContainer>}
    {tab === 4 && studio.data && <Stack spacing={2}>
      <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr 1fr", md: "repeat(5, 1fr)" }, gap: 2 }}>
        {[["Approved corrections", studio.data.approvedCorrections], ["Conflicts", studio.data.conflictingCorrections], ["Source templates", studio.data.supplierQuoteTemplates], ["Decision-ready products", studio.data.productMemoriesWithDecisions], ["Below threshold", studio.data.productMemoriesBelowThreshold]].map(([label, count]) => <Box key={String(label)}><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="h5" sx={{ fontWeight: 800 }}>{count}</Typography></Box>)}
      </Box>
      {studio.data.conflictingCorrections > 0 && <Alert severity="warning">Conflicting approved corrections require human review before reuse.</Alert>}
      {!canGovernLearning && <Alert severity="info">You can inspect learning evidence here. Dashboard edit permission is required to approve, disable, or roll back a signal.</Alert>}
      <TableContainer component={Paper} variant="outlined"><Table size="small"><TableHead><TableRow><TableCell>Learned signal</TableCell><TableCell>Observed value</TableCell><TableCell align="right">Samples</TableCell><TableCell>Evidence status</TableCell><TableCell>Governance</TableCell><TableCell>Last observed</TableCell><TableCell>Evidence</TableCell>{canGovernLearning && <TableCell align="right">Actions</TableCell>}</TableRow></TableHead><TableBody>{studio.data.recentSignals.map((signal) => {
        const governanceStatus = (signal.governanceStatus || signal.status || "OBSERVED").toUpperCase();
        const governanceAction = signal.governanceAction ? readable(signal.governanceAction) : "No governed decision";
        return <TableRow key={signal.signalId || `${signal.evidenceReference}:${signal.subject}`}><TableCell><Typography sx={{ fontWeight: 700 }}>{signal.subject}</Typography><Typography variant="caption" color="text.secondary">{readable(signal.signalType)}</Typography></TableCell><TableCell>{signal.value}</TableCell><TableCell align="right">{signal.sampleSize}</TableCell><TableCell><Chip size="small" color={signal.status === "CONFLICT_REVIEW" ? "warning" : signal.status === "REUSABLE" || signal.status === "APPROVED" ? "success" : "default"} label={readable(signal.status)} /></TableCell><TableCell><Chip size="small" variant={signal.governanceVersion === 0 ? "outlined" : "filled"} color={governanceStatus === "APPROVED" ? "success" : governanceStatus === "DISABLED" ? "default" : "info"} label={`${governanceAction} · v${signal.governanceVersion}`} />{signal.governedOn && <Typography variant="caption" color="text.secondary" sx={{ display: "block", mt: 0.5 }}>{new Date(signal.governedOn).toLocaleString()}</Typography>}</TableCell><TableCell>{new Date(signal.lastObservedOn).toLocaleString()}</TableCell><TableCell><Typography variant="body2" sx={{ maxWidth: 240, overflowWrap: "anywhere" }}>{signal.evidenceReference}</Typography><Typography variant="caption" color="text.secondary" sx={{ display: "block", mt: 0.5, overflowWrap: "anywhere" }}>Signal {signal.signalId}</Typography></TableCell>{canGovernLearning && <TableCell align="right"><Stack direction="row" spacing={0.5} useFlexGap sx={{ justifyContent: "flex-end", flexWrap: "wrap", minWidth: 260 }}>
          {governanceStatus !== "APPROVED" && <Button size="small" startIcon={<CheckCircle2 size={16} />} onClick={() => openGovernanceDialog(signal, "approve")}>Approve</Button>}
          {governanceStatus !== "DISABLED" && <Button size="small" color="warning" startIcon={<Ban size={16} />} onClick={() => openGovernanceDialog(signal, "disable")}>Disable</Button>}
          {signal.governanceVersion > 0 && <Button size="small" startIcon={<RotateCcw size={16} />} onClick={() => openGovernanceDialog(signal, "rollback")}>Roll back</Button>}
        </Stack></TableCell>}</TableRow>;
      })}{studio.data.recentSignals.length === 0 && <TableRow><TableCell colSpan={canGovernLearning ? 8 : 7} align="center">No correction signals have enough verified evidence yet.</TableCell></TableRow>}</TableBody></Table></TableContainer>
    </Stack>}
    <Dialog open={governanceDialog !== null} onClose={() => !governanceMutation.isPending && closeGovernanceDialog()} fullWidth maxWidth="sm">
      <DialogTitle>{governanceDialog ? `${readable(governanceDialog.action)} learning signal` : "Learning decision"}</DialogTitle>
      <DialogContent dividers><Stack spacing={2}>
        <Alert severity={governanceDialog?.action === "disable" ? "warning" : "info"}>This records a new auditable governance version. The observed evidence remains unchanged.</Alert>
        <Box><Typography variant="caption" color="text.secondary">Signal</Typography><Typography sx={{ fontWeight: 700 }}>{governanceDialog?.signal.subject}</Typography><Typography variant="body2">{governanceDialog?.signal.value}</Typography></Box>
        <TextField required multiline minRows={3} label="Decision reason" value={governanceReason} onChange={(event) => setGovernanceReason(event.target.value)} helperText="Explain the business evidence for this decision." />
      </Stack></DialogContent>
      <DialogActions><Button onClick={closeGovernanceDialog} disabled={governanceMutation.isPending}>Cancel</Button><Button variant="contained" startIcon={governanceDialog?.action === "approve" ? <CheckCircle2 size={17} /> : governanceDialog?.action === "disable" ? <Ban size={17} /> : <RotateCcw size={17} />} disabled={!governanceReason.trim() || governanceMutation.isPending} onClick={() => governanceMutation.mutate()}>{governanceMutation.isPending ? "Recording..." : governanceDialog ? readable(governanceDialog.action) : "Record"}</Button></DialogActions>
    </Dialog>
  </Box>;
}
