import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { Add, CloudUpload, InboxOutlined, OpenInNew, Refresh } from "@mui/icons-material";
import {
  Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableContainer,
  TableHead, TableRow, TextField, Typography,
} from "@mui/material";
import { toast } from "react-hot-toast";
import { useAuth } from "../../../context/AuthContext";
import supplierQuoteService, {
  type CaptureSupplierQuoteRequest, type SupplierQuoteInboxStatus, type UploadSupplierQuoteRequest,
} from "../../../api/services/supplierQuoteService";

const number = (value: string) => Number(value);
const sha256 = async (value: string) => Array.from(
  new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value))),
).map((byte) => byte.toString(16).padStart(2, "0")).join("");

function CaptureDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const client = useQueryClient();
  const [form, setForm] = useState({ supplierId: "", supplierSolicitationId: "", sourcingCaseId: "",
    nexoraSerial: "", supplierQuoteReference: "", revisionNumber: "1", sourceIdentity: "",
    currencyId: "", validUntil: "", rfqItemId: "", demandLineId: "", partNumber: "",
    description: "", quantity: "1", availableQuantity: "", uom: "EA", unitPrice: "0",
    leadTimeDays: "", paymentTerms: "", incoterms: "", notes: "" });
  const set = (key: keyof typeof form) => (event: React.ChangeEvent<HTMLInputElement>) =>
    setForm((current) => ({ ...current, [key]: event.target.value }));
  const mutation = useMutation({
    mutationFn: async () => {
      const sourceIdentity = form.sourceIdentity.trim();
      const request: CaptureSupplierQuoteRequest = {
        supplierId: number(form.supplierId), supplierSolicitationId: number(form.supplierSolicitationId),
        sourcingCaseId: number(form.sourcingCaseId), nexoraSerial: form.nexoraSerial.trim(),
        supplierQuoteReference: form.supplierQuoteReference.trim(), revisionNumber: number(form.revisionNumber),
        captureChannel: "MANUAL", sourceDocumentId: null, sourceIdentity,
        sourceSha256: await sha256(sourceIdentity), currencyId: number(form.currencyId),
        validUntil: form.validUntil ? `${form.validUntil}T23:59:59Z` : null,
        incoterms: form.incoterms || null, freightAmount: 0, taxAmount: 0,
        paymentTerms: form.paymentTerms || null, notes: form.notes || null, evidence: [],
        lines: [{ lineNumber: 1, rfqItemId: number(form.rfqItemId),
          commercialDemandLineId: number(form.demandLineId), partNumber: form.partNumber || null,
          description: form.description.trim(), quantity: number(form.quantity),
          availableQuantity: form.availableQuantity ? number(form.availableQuantity) : null,
          unitOfMeasure: form.uom.trim(), unitPrice: number(form.unitPrice),
          leadTimeDays: form.leadTimeDays ? number(form.leadTimeDays) : null,
          isAlternate: false, evidence: [] }],
      };
      return supplierQuoteService.capture(request);
    },
    onSuccess: () => { toast.success("Supplier Quote revision captured"); void client.invalidateQueries({ queryKey: ["supplier-quote-inbox"] }); onClose(); },
  });
  const required = [form.supplierId, form.supplierSolicitationId, form.sourcingCaseId, form.nexoraSerial,
    form.supplierQuoteReference, form.sourceIdentity, form.currencyId, form.rfqItemId, form.demandLineId,
    form.description, form.quantity, form.uom, form.unitPrice].every((value) => value.trim());
  return <Dialog open={open} onClose={onClose} fullWidth maxWidth="md">
    <DialogTitle>Capture Supplier Quote</DialogTitle>
    <DialogContent dividers><Stack spacing={2}>
      <Alert severity="info">Use the identifiers shown on the Sourcing Case. Nexora verifies every reference belongs to this tenant and commercial journey.</Alert>
      <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "1fr 1fr" }, gap: 2 }}>
        <TextField required label="Supplier ID" type="number" value={form.supplierId} onChange={set("supplierId")} />
        <TextField required label="Supplier RFQ ID" type="number" value={form.supplierSolicitationId} onChange={set("supplierSolicitationId")} />
        <TextField required label="Sourcing Case ID" type="number" value={form.sourcingCaseId} onChange={set("sourcingCaseId")} />
        <TextField required label="Nexora Serial" value={form.nexoraSerial} onChange={set("nexoraSerial")} />
        <TextField required label="Supplier Quote reference" value={form.supplierQuoteReference} onChange={set("supplierQuoteReference")} />
        <TextField required label="Revision" type="number" value={form.revisionNumber} onChange={set("revisionNumber")} />
        <TextField required label="Source reference" value={form.sourceIdentity} onChange={set("sourceIdentity")} helperText="Email thread, portal response, or offline record" />
        <TextField required label="Currency ID" type="number" value={form.currencyId} onChange={set("currencyId")} />
        <TextField label="Valid until" type="date" value={form.validUntil} onChange={set("validUntil")} slotProps={{ inputLabel: { shrink: true } }} />
        <TextField label="Payment terms" value={form.paymentTerms} onChange={set("paymentTerms")} />
        <TextField label="Incoterms" value={form.incoterms} onChange={set("incoterms")} />
        <TextField label="Notes" value={form.notes} onChange={set("notes")} />
      </Box>
      <Typography variant="h6">Quoted line</Typography>
      <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "1fr 1fr" }, gap: 2 }}>
        <TextField required label="RFQ line ID" type="number" value={form.rfqItemId} onChange={set("rfqItemId")} />
        <TextField required label="Demand line ID" type="number" value={form.demandLineId} onChange={set("demandLineId")} />
        <TextField label="Part number" value={form.partNumber} onChange={set("partNumber")} />
        <TextField required label="Description" value={form.description} onChange={set("description")} />
        <TextField required label="Quantity" type="number" value={form.quantity} onChange={set("quantity")} />
        <TextField label="Available quantity" type="number" value={form.availableQuantity} onChange={set("availableQuantity")} />
        <TextField required label="UOM" value={form.uom} onChange={set("uom")} />
        <TextField required label="Unit price" type="number" value={form.unitPrice} onChange={set("unitPrice")} />
        <TextField label="Lead time (days)" type="number" value={form.leadTimeDays} onChange={set("leadTimeDays")} />
      </Box>
      {mutation.isError && <Alert severity="error">{(mutation.error as any)?.response?.data?.detail ?? "Supplier Quote capture failed."}</Alert>}
    </Stack></DialogContent>
    <DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" disabled={!required || mutation.isPending} onClick={() => mutation.mutate()}>{mutation.isPending ? "Capturing..." : "Capture revision"}</Button></DialogActions>
  </Dialog>;
}

function UploadDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const client = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [form, setForm] = useState({ supplierId: "", supplierSolicitationId: "", sourcingCaseId: "",
    nexoraSerial: "", supplierQuoteReference: "", revisionNumber: "1", currencyId: "" });
  const set = (key: keyof typeof form) => (event: React.ChangeEvent<HTMLInputElement>) =>
    setForm((current) => ({ ...current, [key]: event.target.value }));
  const mutation = useMutation({
    mutationFn: () => supplierQuoteService.upload({ file: file!, supplierId: number(form.supplierId),
      supplierSolicitationId: number(form.supplierSolicitationId), sourcingCaseId: number(form.sourcingCaseId),
      nexoraSerial: form.nexoraSerial.trim(), supplierQuoteReference: form.supplierQuoteReference.trim(),
      revisionNumber: number(form.revisionNumber), currencyId: number(form.currencyId) } satisfies UploadSupplierQuoteRequest),
    onSuccess: (result) => { toast.success(result.supplierQuoteId ? "Supplier Quote extracted for review" : "Document accepted into commercial review"); void client.invalidateQueries({ queryKey: ["supplier-quote-inbox"] }); onClose(); },
  });
  const valid = file && Object.values(form).every((value) => value.trim());
  return <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm"><DialogTitle>Upload Supplier Quote</DialogTitle><DialogContent dividers><Stack spacing={2}>
    <Alert severity="info">CSV and XLSX lines are extracted locally. PDF, Word, and image responses are quarantined, inspected, and held for governed review.</Alert>
    <Button component="label" variant="outlined" startIcon={<CloudUpload />}>{file?.name ?? "Select Supplier Quote file"}<input hidden type="file" accept=".csv,.xlsx,.pdf,.doc,.docx,.png,.jpg,.jpeg,.tif,.tiff" onChange={(event) => setFile(event.target.files?.[0] ?? null)} /></Button>
    <TextField required label="Supplier ID" type="number" value={form.supplierId} onChange={set("supplierId")} />
    <TextField required label="Supplier RFQ ID" type="number" value={form.supplierSolicitationId} onChange={set("supplierSolicitationId")} />
    <TextField required label="Sourcing Case ID" type="number" value={form.sourcingCaseId} onChange={set("sourcingCaseId")} />
    <TextField required label="Nexora Serial" value={form.nexoraSerial} onChange={set("nexoraSerial")} />
    <TextField required label="Supplier Quote reference" value={form.supplierQuoteReference} onChange={set("supplierQuoteReference")} />
    <TextField required label="Revision" type="number" value={form.revisionNumber} onChange={set("revisionNumber")} />
    <TextField required label="Currency ID" type="number" value={form.currencyId} onChange={set("currencyId")} />
    {mutation.isError && <Alert severity="error">{(mutation.error as any)?.response?.data?.detail ?? "Supplier Quote upload failed."}</Alert>}
  </Stack></DialogContent><DialogActions><Button onClick={onClose}>Cancel</Button><Button variant="contained" disabled={!valid || mutation.isPending} onClick={() => mutation.mutate()}>{mutation.isPending ? "Uploading..." : "Upload and extract"}</Button></DialogActions></Dialog>;
}

export default function SupplierQuoteInboxPage() {
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const [status, setStatus] = useState<"ALL" | SupplierQuoteInboxStatus>("ALL");
  const [captureOpen, setCaptureOpen] = useState(false);
  const [uploadOpen, setUploadOpen] = useState(false);
  const query = useQuery({ queryKey: ["supplier-quote-inbox", status],
    queryFn: () => supplierQuoteService.getInbox(status === "ALL" ? undefined : status),
    enabled: hasPermission("Supplier History") });
  const items = useMemo(() => query.data ?? [], [query.data]);
  return <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1400, mx: "auto" }}>
    <Stack direction={{ xs: "column", sm: "row" }} sx={{ justifyContent: "space-between", gap: 2, mb: 3 }}>
      <Box><Typography variant="h4" sx={{ fontWeight: 800 }}>Supplier Quote Inbox</Typography><Typography color="text.secondary">Review persisted Supplier responses before comparison and pricing.</Typography></Box>
      <Stack direction="row" spacing={1}><Button startIcon={<Refresh />} onClick={() => query.refetch()}>Refresh</Button>{hasPermission("Supplier History", "create") && <><Button variant="outlined" startIcon={<CloudUpload />} onClick={() => setUploadOpen(true)}>Upload Supplier Quote</Button><Button variant="contained" startIcon={<Add />} onClick={() => setCaptureOpen(true)}>Capture Supplier Quote</Button></>}</Stack>
    </Stack>
    <TextField select size="small" label="Status" value={status} onChange={(event) => setStatus(event.target.value as typeof status)} sx={{ minWidth: 230, mb: 2 }}>
      <MenuItem value="ALL">All statuses</MenuItem><MenuItem value="REVIEW_REQUIRED">Review required</MenuItem><MenuItem value="READY_FOR_COMPARISON">Ready for comparison</MenuItem>
    </TextField>
    {query.isLoading && <Paper variant="outlined" sx={{ p: 5, textAlign: "center" }}><CircularProgress /></Paper>}
    {query.isError && <Alert severity="error">Supplier Quotes could not be loaded. No empty result has been assumed.</Alert>}
    {!query.isLoading && !query.isError && items.length === 0 && <Paper variant="outlined" sx={{ p: 5, textAlign: "center" }}><InboxOutlined color="disabled" sx={{ fontSize: 44 }} /><Typography>No Supplier Quotes match this status.</Typography></Paper>}
    {items.length > 0 && <TableContainer component={Paper} variant="outlined"><Table><TableHead><TableRow><TableCell>Supplier / Quote</TableCell><TableCell>Commercial lineage</TableCell><TableCell>Revision</TableCell><TableCell>Status</TableCell><TableCell>Updated</TableCell><TableCell align="right">Action</TableCell></TableRow></TableHead><TableBody>{items.map((item) => <TableRow hover key={item.supplierQuoteId}><TableCell><Typography sx={{ fontWeight: 700 }}>{item.supplierName}</Typography><Typography variant="body2">{item.supplierQuoteReference}</Typography></TableCell><TableCell>{item.nexoraSerial}<Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>Sourcing Case {item.sourcingCaseId}</Typography></TableCell><TableCell>{item.currentRevisionNumber}</TableCell><TableCell><Chip size="small" color={item.inboxStatus === "READY_FOR_COMPARISON" ? "success" : "warning"} label={item.inboxStatus.replaceAll("_", " ")} />{item.reviewRequiredCount > 0 && <Typography variant="caption" sx={{ display: "block" }}>{item.reviewRequiredCount} fields</Typography>}</TableCell><TableCell>{new Date(item.updatedOn).toLocaleString()}</TableCell><TableCell align="right"><Button endIcon={<OpenInNew />} onClick={() => navigate(`/procurement/supplier-quotes/${item.supplierQuoteId}`)}>Review</Button></TableCell></TableRow>)}</TableBody></Table></TableContainer>}
    <CaptureDialog open={captureOpen} onClose={() => setCaptureOpen(false)} />
    <UploadDialog open={uploadOpen} onClose={() => setUploadOpen(false)} />
  </Box>;
}
