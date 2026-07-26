import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { InboxOutlined, Refresh } from "@mui/icons-material";
import { Alert, Box, Button, Chip, CircularProgress, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Typography } from "@mui/material";
import commercialInboxService from "../../../api/services/commercialInboxService";

export default function CommercialInboxPage() {
  const [status, setStatus] = useState("");
  const query = useQuery({ queryKey: ["commercial-inbox", status], queryFn: () => commercialInboxService.search(status) });
  const items = query.data?.items ?? [];
  return <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1400, mx: "auto" }}>
    <Stack direction={{ xs: "column", sm: "row" }} sx={{ justifyContent: "space-between", gap: 2, mb: 3 }}><Box><Typography variant="h4" sx={{ fontWeight: 800 }}>Commercial Inbox</Typography><Typography color="text.secondary">Classified Customer and Supplier documents awaiting their next commercial action.</Typography></Box><Button startIcon={<Refresh />} onClick={() => query.refetch()}>Refresh</Button></Stack>
    <TextField select size="small" label="Review status" value={status} onChange={(event) => setStatus(event.target.value)} sx={{ minWidth: 230, mb: 2 }}><MenuItem value="">All statuses</MenuItem><MenuItem value="ReviewRequired">Review required</MenuItem><MenuItem value="AutoClassified">Auto classified</MenuItem><MenuItem value="Confirmed">Confirmed</MenuItem><MenuItem value="Rejected">Rejected</MenuItem></TextField>
    {query.isLoading && <Paper variant="outlined" sx={{ p: 5, textAlign: "center" }}><CircularProgress /></Paper>}
    {query.isError && <Alert severity="error">Commercial documents could not be loaded. No empty result has been assumed.</Alert>}
    {!query.isLoading && !query.isError && items.length === 0 && <Paper variant="outlined" sx={{ p: 5, textAlign: "center" }}><InboxOutlined color="disabled" sx={{ fontSize: 44 }} /><Typography>No documents match this status.</Typography></Paper>}
    {items.length > 0 && <TableContainer component={Paper} variant="outlined"><Table><TableHead><TableRow><TableCell>Document</TableCell><TableCell>Classification</TableCell><TableCell>Security / Processing</TableCell><TableCell>Commercial matches</TableCell><TableCell>Projection</TableCell><TableCell>Updated</TableCell></TableRow></TableHead><TableBody>{items.map((item) => <TableRow hover key={item.id}><TableCell><Typography sx={{ fontWeight: 700 }}>{item.originalFileName}</Typography><Typography variant="caption">Source {item.sourceDocumentId}</Typography></TableCell><TableCell><Chip size="small" label={item.documentType.replaceAll("_", " ")} /><Typography variant="caption" sx={{ display: "block", mt: 0.5 }}>{item.reviewStatus.replaceAll("_", " ")} · {Math.round(item.confidence * 100)}%</Typography></TableCell><TableCell>{item.securityStatus}<Typography variant="caption" sx={{ display: "block" }}>{item.processingStatus}</Typography></TableCell><TableCell>Supplier RFQ {item.matches.supplierRfqId ?? "Unmatched"}<Typography variant="caption" sx={{ display: "block" }}>Sourcing Case {item.matches.sourcingCaseId ?? "Unmatched"}</Typography></TableCell><TableCell><Chip size="small" color={item.supplierQuoteProjection.isReady ? "success" : "warning"} label={item.supplierQuoteProjection.state.replaceAll("_", " ")} />{item.supplierQuoteProjection.blockingReasons.map((reason) => <Typography variant="caption" sx={{ display: "block" }} key={reason}>{reason}</Typography>)}</TableCell><TableCell>{new Date(item.updatedOn).toLocaleString()}</TableCell></TableRow>)}</TableBody></Table></TableContainer>}
  </Box>;
}
