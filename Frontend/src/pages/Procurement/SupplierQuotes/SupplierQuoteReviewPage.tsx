import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useParams } from "react-router-dom";
import { ArrowBack, CompareArrows, FactCheck } from "@mui/icons-material";
import { Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Typography } from "@mui/material";
import { toast } from "react-hot-toast";
import supplierQuoteService, { type SupplierQuoteEvidence, type SupplierQuoteReviewStatus } from "../../../api/services/supplierQuoteService";
import CommercialProcessingEvidence from "../../../components/common/CommercialProcessingEvidence";

export default function SupplierQuoteReviewPage() {
  const id = Number(useParams().supplierQuoteId);
  const navigate = useNavigate();
  const client = useQueryClient();
  const [selected, setSelected] = useState<{ revisionId: number; evidence: SupplierQuoteEvidence } | null>(null);
  const [status, setStatus] = useState<SupplierQuoteReviewStatus>("ACCEPTED");
  const [correctedValue, setCorrectedValue] = useState("");
  const [reason, setReason] = useState("");
  const query = useQuery({ queryKey: ["supplier-quote", id], queryFn: () => supplierQuoteService.getById(id), enabled: id > 0 });
  const mutation = useMutation({ mutationFn: () => supplierQuoteService.reviewEvidence(id, selected!.revisionId, selected!.evidence.id, { status, correctedValue: status === "CORRECTED" ? correctedValue : null, reason }),
    onSuccess: () => { toast.success("Review decision recorded"); setSelected(null); setReason(""); setCorrectedValue(""); void client.invalidateQueries({ queryKey: ["supplier-quote", id] }); void client.invalidateQueries({ queryKey: ["supplier-quote-inbox"] }); } });
  const projection = useMutation({ mutationFn: () => supplierQuoteService.projectForComparison(id, query.data!.version),
    onSuccess: () => { toast.success("Supplier Quote is available in offer comparison"); navigate(`/procurement/rfqs/${query.data!.rfqId}/sourcing`); },
    onError: () => toast.error("Supplier Quote could not be added to comparison") });
  if (query.isLoading) return <Box sx={{ p: 5, textAlign: "center" }}><CircularProgress /></Box>;
  if (query.isError || !query.data) return <Box sx={{ p: 3 }}><Alert severity="error">Supplier Quote could not be loaded.</Alert></Box>;
  const quote = query.data;
  return <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1400, mx: "auto" }}>
    <Button startIcon={<ArrowBack />} onClick={() => navigate("/procurement/supplier-quotes")} sx={{ mb: 1 }}>Supplier Quote Inbox</Button>
    <Stack direction={{ xs: "column", md: "row" }} sx={{ justifyContent: "space-between", gap: 2, mb: 3 }}><Box><Typography variant="h4" sx={{ fontWeight: 800 }}>{quote.supplierQuoteReference}</Typography><Typography color="text.secondary">{quote.supplierName} · {quote.nexoraSerial} · Sourcing Case {quote.sourcingCaseId}</Typography></Box><Stack direction="row" spacing={1} sx={{ alignItems: "center" }}><Chip color={quote.inboxStatus === "READY_FOR_COMPARISON" ? "success" : "warning"} label={quote.inboxStatus.replaceAll("_", " ")} />{quote.inboxStatus === "READY_FOR_COMPARISON" && <Button variant="contained" startIcon={<CompareArrows />} disabled={projection.isPending} onClick={() => projection.mutate()}>Compare offer</Button>}</Stack></Stack>
    <CommercialProcessingEvidence resource="supplier-quotes" id={quote.supplierQuoteId} />
    {quote.revisions.slice().reverse().map((revision) => <Paper variant="outlined" key={revision.revisionId} sx={{ mb: 2, p: 2 }}>
      <Stack direction="row" sx={{ justifyContent: "space-between", mb: 2 }}><Typography variant="h6">Revision {revision.revisionNumber}</Typography><Typography color="text.secondary">{revision.captureChannel.replaceAll("_", " ")} · {new Date(revision.capturedOn).toLocaleString()}</Typography></Stack>
      <Typography sx={{ fontWeight: 700, mb: 1 }}>Commercial lines</Typography>
      <TableContainer><Table size="small"><TableHead><TableRow><TableCell>Line</TableCell><TableCell>Part / Description</TableCell><TableCell align="right">Quantity</TableCell><TableCell align="right">Available</TableCell><TableCell align="right">Unit price</TableCell><TableCell align="right">Lead time</TableCell></TableRow></TableHead><TableBody>{revision.lines.map((line) => <TableRow key={line.id}><TableCell>{line.lineNumber}</TableCell><TableCell>{line.partNumber ?? "No part number"}<Typography variant="caption" sx={{ display: "block" }}>{line.description}</Typography></TableCell><TableCell align="right">{line.quantity}</TableCell><TableCell align="right">{line.availableQuantity ?? "Not stated"}</TableCell><TableCell align="right">{line.unitPrice}</TableCell><TableCell align="right">{line.leadTimeDays == null ? "Not stated" : `${line.leadTimeDays} days`}</TableCell></TableRow>)}</TableBody></Table></TableContainer>
      <Typography sx={{ fontWeight: 700, mt: 3, mb: 1 }}>Field evidence</Typography>
      <TableContainer><Table size="small"><TableHead><TableRow><TableCell>Field</TableCell><TableCell>Source value</TableCell><TableCell>Normalized</TableCell><TableCell>Confidence</TableCell><TableCell>Method</TableCell><TableCell>Decision</TableCell><TableCell align="right">Action</TableCell></TableRow></TableHead><TableBody>{revision.evidence.map((evidence) => <TableRow key={evidence.id}><TableCell>{evidence.fieldName}</TableCell><TableCell>{evidence.originalValue ?? "Not present"}</TableCell><TableCell>{evidence.correctedValue ?? evidence.normalizedValue ?? "Not resolved"}</TableCell><TableCell>{Math.round(evidence.confidence * 100)}%</TableCell><TableCell>{evidence.method.replaceAll("_", " ")}</TableCell><TableCell><Chip size="small" color={evidence.latestReviewStatus ? "success" : evidence.reviewRequired ? "warning" : "default"} label={evidence.latestReviewStatus ?? (evidence.reviewRequired ? "REVIEW REQUIRED" : "NO REVIEW")} /></TableCell><TableCell align="right">{evidence.reviewRequired && !evidence.latestReviewStatus && <Button size="small" startIcon={<FactCheck />} onClick={() => setSelected({ revisionId: revision.revisionId, evidence })}>Review</Button>}</TableCell></TableRow>)}</TableBody></Table></TableContainer>
    </Paper>)}
    <Dialog open={Boolean(selected)} onClose={() => setSelected(null)} fullWidth maxWidth="sm"><DialogTitle>Record review decision</DialogTitle><DialogContent dividers><Stack spacing={2}><Alert severity="info">The source evidence remains immutable. This decision is appended to the audit history.</Alert><TextField label="Field" value={selected?.evidence.fieldName ?? ""} disabled /><TextField select label="Decision" value={status} onChange={(event) => setStatus(event.target.value as SupplierQuoteReviewStatus)}><MenuItem value="ACCEPTED">Accept extracted value</MenuItem><MenuItem value="CORRECTED">Record correction</MenuItem><MenuItem value="REJECTED">Reject value</MenuItem></TextField>{status === "CORRECTED" && <TextField required label="Corrected value" value={correctedValue} onChange={(event) => setCorrectedValue(event.target.value)} />}<TextField required multiline minRows={3} label="Review reason" value={reason} onChange={(event) => setReason(event.target.value)} />{mutation.isError && <Alert severity="error">Review decision could not be saved.</Alert>}</Stack></DialogContent><DialogActions><Button onClick={() => setSelected(null)}>Cancel</Button><Button variant="contained" disabled={!reason.trim() || (status === "CORRECTED" && !correctedValue.trim()) || mutation.isPending} onClick={() => mutation.mutate()}>Record decision</Button></DialogActions></Dialog>
  </Box>;
}
