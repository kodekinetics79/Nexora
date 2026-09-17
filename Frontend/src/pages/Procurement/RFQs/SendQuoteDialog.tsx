import React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
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
  FormControlLabel,
  MenuItem,
  Radio,
  RadioGroup,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from "@mui/material";
import { AttachFile, Send, WarningAmber } from "@mui/icons-material";
import { useNavigate } from "react-router-dom";
import { useSnackbar } from "notistack";
import quoteService, { type PriceAttestationSource, type QuoteDTO, type QuoteLineDTO } from "../../../api/services/quoteService";
import rfqService from "../../../api/services/rfqService";
import currencyService from "../../../api/services/currencyService";
import { useAuth } from "../../../context/AuthContext";
import { formatMoney } from "../../../utils/currency";

const EMAIL = /^[^\s@;,]+@[^\s@;,]+\.[^\s@;,]+$/;
const isoDay = (date: Date) => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
const daysFromToday = (days: number) => { const d = new Date(); d.setDate(d.getDate() + days); return isoDay(d); };
const qty = (value: number) => value.toLocaleString(undefined, { maximumFractionDigits: 2 });
const describeError = (error: unknown, fallback: string) => {
  const data = (error as { response?: { data?: { detail?: string; message?: string } } })?.response?.data;
  return data?.detail || data?.message || fallback;
};

// Handled inside this window (currency, validity, unpriced lines, price source), so never listed as blockers.
const HANDLED_HERE = new Set(["QUOTE_INCOMPLETE", "PRICE_ATTESTATION_REQUIRED"]);

const deliveryOf = (line: QuoteLineDTO) => {
  const days = line.deliveryLeadTime;
  const when = !days ? "" : days % 7 === 0 ? `${days / 7} week${days === 7 ? "" : "s"}` : `${days} day${days === 1 ? "" : "s"}`;
  if (line.exStockQuantity && line.exStockQuantity > 0) return `${qty(line.exStockQuantity)} ex stock, balance ${when ? `in ${when}` : "to follow"}`;
  if (days === 0) return "Ex stock";
  return when ? `Delivery ${when}` : "";
};

export interface SendQuoteDialogProps {
  open: boolean;
  rfqId: number;
  onClose: () => void;
  /** Called once the quote was handed over for delivery (it becomes "sent" a moment later). */
  onSent?: () => void;
}

/**
 * Send the RFQ's quote without leaving the RFQ: the lines and total, the currency and how long the
 * prices hold, where the prices came from, and the email the customer gets. Anything only a
 * manager can fix in Setup is named in plain words with a link.
 */
export default function SendQuoteDialog({ open, rfqId, onClose, onSent }: SendQuoteDialogProps) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const { userData } = useAuth();

  // The draft behind this RFQ: the latest one, or a new draft when none was started.
  const quoteIdQuery = useQuery({
    queryKey: ["send-quote-open", rfqId],
    queryFn: async () => {
      const latest = await rfqService.getLatestQuote(rfqId);
      if (latest) return latest;
      const created = await rfqService.prepareQuoteDraft(rfqId);
      return { quoteId: created.id as number, quoteNo: created.quoteNo as string, state: "DRAFT" as const };
    },
    enabled: open,
    retry: false,
    staleTime: 0,
    gcTime: 0,
    meta: { silenceGlobalError: true },
  });
  const quoteId = quoteIdQuery.data?.quoteId;
  const quoteQuery = useQuery({ queryKey: ["send-quote", quoteId], queryFn: () => quoteService.getById(quoteId!), enabled: open && !!quoteId });
  const readinessQuery = useQuery({ queryKey: ["send-quote-readiness", quoteId], queryFn: () => quoteService.getSendReadiness(quoteId!), enabled: open && !!quoteId });
  const attestationQuery = useQuery({ queryKey: ["send-quote-attestation", quoteId], queryFn: () => quoteService.getPriceAttestation(quoteId!), enabled: open && !!quoteId });
  const draftQuery = useQuery({ queryKey: ["send-quote-email", quoteId], queryFn: () => quoteService.getEmailDraft(quoteId!), enabled: open && !!quoteId });
  const quote: QuoteDTO | undefined = quoteQuery.data;

  const currenciesQuery = useQuery({
    queryKey: ["currencies-for-quote", userData?.businessUnitId],
    queryFn: async () => (await currencyService.getAll({ businessUnitId: userData?.businessUnitId, pageNumber: 1, pageSize: 100, isActive: true })).items ?? [],
    enabled: open && !!quote && !quote.currencyId,
    staleTime: 10 * 60 * 1000,
  });
  const currencies: { id: number; code: string; isBaseCurrency?: boolean }[] = currenciesQuery.data ?? [];

  const [currencyId, setCurrencyId] = React.useState<number | "">("");
  const [validUntil, setValidUntil] = React.useState("");
  const [source, setSource] = React.useState<PriceAttestationSource>("SALES_MANAGER");
  const [reference, setReference] = React.useState("");
  const [to, setTo] = React.useState("");
  const [subject, setSubject] = React.useState("");
  const [body, setBody] = React.useState("");
  const seeded = React.useRef(false);

  React.useEffect(() => { if (!open) seeded.current = false; }, [open]);
  React.useEffect(() => {
    if (!open || seeded.current || !quote || !draftQuery.data) return;
    seeded.current = true;
    setValidUntil(quote.validUntil ? quote.validUntil.split("T")[0] : daysFromToday(30));
    setTo(quote.customerEmail || draftQuery.data.recipientEmail || "");
    setSubject(draftQuery.data.subject);
    setBody(draftQuery.data.body);
    setReference(userData?.userName ?? "");
  }, [open, quote, draftQuery.data, userData?.userName]);
  React.useEffect(() => {
    if (quote && !quote.currencyId && currencyId === "" && currencies.length > 0) {
      setCurrencyId((currencies.find((c) => c.isBaseCurrency) ?? currencies[0]).id);
    }
  }, [quote, currencies, currencyId]);

  const lines = quote?.quoteItems ?? [];
  const unpriced = lines.filter((line) => !(line.unitPrice > 0));
  const currencyCode = quote?.currencyCode ?? currencies.find((c) => c.id === currencyId)?.code ?? null;
  const subtotal = lines.reduce((sum, line) => sum + (line.taxableBase ?? 0), 0);
  const tax = lines.reduce((sum, line) => sum + (line.taxAmount ?? 0), 0);
  const attestation = attestationQuery.data;
  const confirmed = attestation?.satisfied === true;
  const setupBlockers = (readinessQuery.data?.blockers ?? [])
    .filter((blocker) => !HANDLED_HERE.has(blocker.code))
    // Tax cannot be worked out on a line with no price; the unpriced lines are already called out.
    .filter((blocker) => !(blocker.code === "OUTPUT_TAX_NOT_DERIVED" && unpriced.length > 0));
  const alreadySent = quoteIdQuery.data && quoteIdQuery.data.state !== "DRAFT";

  const toOk = EMAIL.test(to.trim());
  const validOk = validUntil !== "" && validUntil >= isoDay(new Date());
  const referenceOk = confirmed || reference.trim().length > 0;
  const currencyOk = !!quote?.currencyId || currencyId !== "";
  const reasons = [
    unpriced.length > 0 ? `${unpriced.length} ${unpriced.length === 1 ? "line has" : "lines have"} no price yet` : null,
    !currencyOk ? "Choose a currency" : null,
    !validOk ? "Choose how long the prices hold" : null,
    !referenceOk ? "Say where the prices came from" : null,
    !toOk ? "Enter the customer's email" : null,
    setupBlockers.length > 0 ? "Setup needs finishing first" : null,
  ].filter(Boolean) as string[];

  const send = useMutation({
    mutationFn: async () => {
      const id = quoteId!;
      const termsChanged = (!quote!.currencyId && currencyId !== "") || validUntil !== (quote!.validUntil ?? "").split("T")[0];
      if (termsChanged) {
        await rfqService.saveQuoteTerms(id, { currencyId: quote!.currencyId ? null : (currencyId as number), validUntil });
      }
      if (!confirmed) await quoteService.confirmPriceAttestation(id, source, reference.trim());
      const edited = draftQuery.data && (subject !== draftQuery.data.subject || body !== draftQuery.data.body);
      return quoteService.sendEmail(id, to.trim(), edited ? { subject, body } : undefined);
    },
    onSuccess: (outcome) => {
      for (const key of ["send-quote", "send-quote-readiness", "send-quote-attestation", "send-quote-id", "send-quote-open", "stock-price", "rfq-detail"]) {
        queryClient.invalidateQueries({ queryKey: [key] });
      }
      if (outcome.held) {
        enqueueSnackbar(outcome.message || "A price is below the allowed floor, so the quote is waiting for a manager's approval.", { variant: "warning" });
      } else if (outcome.priceAttestationRequired || outcome.taxDerivationRequired) {
        enqueueSnackbar(outcome.message || "The quote could not be sent yet.", { variant: "error" });
        return;
      } else {
        enqueueSnackbar(`${quote?.quoteNo} ${outcome.delivered ? "sent" : "is on its way"} to ${to.trim()}.`, { variant: "success" });
        onSent?.();
      }
      onClose();
    },
    onError: (error) => enqueueSnackbar(describeError(error, "The quote could not be sent."), { variant: "error" }),
  });

  const loading = quoteIdQuery.isLoading || quoteQuery.isLoading || draftQuery.isLoading;

  return (
    <Dialog open={open} onClose={send.isPending ? undefined : onClose} maxWidth="lg" fullWidth
      slotProps={{ paper: { sx: { height: { md: "88vh" } } } }}>
      <DialogTitle sx={{ pb: 1 }}>
        <Typography component="span" variant="h6" sx={{ fontWeight: 800, display: "block" }}>
          Send quote{quote ? ` ${quote.quoteNo}` : ""}
        </Typography>
        {quote && (
          <Typography component="span" variant="body2" color="text.secondary" sx={{ display: "block" }}>
            To <b>{quote.customerName}</b>{quote.contactName ? ` · ${quote.contactName}` : ""}
          </Typography>
        )}
      </DialogTitle>

      <DialogContent dividers sx={{ p: 0, display: "flex", flexDirection: "column" }}>
        {loading && (
          <Stack direction="row" spacing={1.5} sx={{ alignItems: "center", p: 3 }} role="status">
            <CircularProgress size={18} />
            <Typography variant="body2">Getting the quote ready…</Typography>
          </Stack>
        )}
        {quoteIdQuery.isError && (
          <Alert severity="error" sx={{ m: 2 }}>{describeError(quoteIdQuery.error, "The quote could not be opened.")}</Alert>
        )}
        {alreadySent && quoteIdQuery.data && (
          <Alert severity="info" sx={{ m: 2 }} action={<Button color="inherit" onClick={() => navigate(`/sales/quotes/view/${quoteIdQuery.data!.quoteId}`)}>Open quote</Button>}>
            {quoteIdQuery.data.quoteNo} was already sent. To change prices, use Change price on a line: it makes a new revision you can send here.
          </Alert>
        )}

        {quote && !alreadySent && (
          <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "7fr 5fr" }, flex: 1, minHeight: 0 }}>
            {/* ---- The quote ---- */}
            <Box sx={(theme) => ({ overflowY: "auto", p: 2.5, [theme.breakpoints.up("md")]: { borderRight: `1px solid ${theme.palette.divider}` } })}>
              {setupBlockers.length > 0 && (
                <Alert severity="warning" sx={{ mb: 2 }}>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>A manager needs to finish Setup before this quote can go out:</Typography>
                  {setupBlockers.map((blocker) => (
                    <Stack key={blocker.code} direction="row" spacing={1} sx={{ alignItems: "center", mt: 0.5 }}>
                      <Typography variant="body2" sx={{ flex: 1 }}>{blocker.message}</Typography>
                      {blocker.setupPath && (
                        <Button size="small" color="inherit" onClick={() => { onClose(); navigate(blocker.setupPath!); }}>
                          {blocker.setupLabel || "Open setup"}
                        </Button>
                      )}
                    </Stack>
                  ))}
                </Alert>
              )}

              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>Line</TableCell>
                    <TableCell>Item</TableCell>
                    <TableCell align="right">Qty</TableCell>
                    <TableCell align="right">Unit price</TableCell>
                    <TableCell align="right">Total</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {lines.map((line, index) => {
                    const priced = line.unitPrice > 0;
                    return (
                      <TableRow key={line.id} sx={priced ? undefined : { bgcolor: "warning.lighter" }}>
                        <TableCell sx={{ color: "text.secondary" }}>{line.customerLineRef || index + 1}</TableCell>
                        <TableCell sx={{ maxWidth: 280 }}>
                          <Typography variant="body2" noWrap title={line.itemDescription ?? ""}>{line.itemDescription}</Typography>
                          {deliveryOf(line) && <Typography variant="caption" color="text.secondary">{deliveryOf(line)}</Typography>}
                        </TableCell>
                        <TableCell align="right">{qty(line.quantity)} {line.unitOfMeasure}</TableCell>
                        <TableCell align="right">
                          {priced ? formatMoney(line.unitPrice, currencyCode) : (
                            <Chip size="small" color="warning" icon={<WarningAmber />} label="No price" />
                          )}
                        </TableCell>
                        <TableCell align="right" sx={{ fontWeight: 700 }}>{priced ? formatMoney(line.taxableBase, currencyCode) : "—"}</TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
              {unpriced.length > 0 && (
                <Typography variant="body2" color="warning.main" sx={{ mt: 1, fontWeight: 700 }}>
                  {unpriced.length} {unpriced.length === 1 ? "line has" : "lines have"} no price yet. Close this window and use Price it on {unpriced.length === 1 ? "that line" : "those lines"}.
                </Typography>
              )}

              <Stack spacing={0.5} sx={{ mt: 2, ml: "auto", maxWidth: 320 }}>
                <Stack direction="row" sx={{ justifyContent: "space-between" }}>
                  <Typography variant="body2" color="text.secondary">Total excluding VAT</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>{formatMoney(subtotal, currencyCode)}</Typography>
                </Stack>
                <Stack direction="row" sx={{ justifyContent: "space-between" }}>
                  <Typography variant="body2" color="text.secondary">VAT</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>{formatMoney(tax, currencyCode)}</Typography>
                </Stack>
                <Stack direction="row" sx={{ justifyContent: "space-between", borderTop: 1, borderColor: "divider", pt: 0.5 }}>
                  <Typography variant="subtitle1" sx={{ fontWeight: 800 }}>Total</Typography>
                  <Typography variant="subtitle1" sx={{ fontWeight: 800 }}>{formatMoney(subtotal + tax, currencyCode)}</Typography>
                </Stack>
              </Stack>

              <Stack direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ mt: 3 }}>
                {quote.currencyId ? (
                  <TextField label="Currency" value={quote.currencyCode ?? ""} disabled sx={{ width: 130 }} />
                ) : (
                  <TextField select label="Currency" value={currencyId} sx={{ width: 130 }}
                    onChange={(event) => setCurrencyId(Number(event.target.value))}>
                    {currencies.map((c) => <MenuItem key={c.id} value={c.id}>{c.code}</MenuItem>)}
                  </TextField>
                )}
                <Box>
                  <TextField type="date" label="Prices valid until" value={validUntil} error={validUntil !== "" && !validOk}
                    onChange={(event) => setValidUntil(event.target.value)} slotProps={{ inputLabel: { shrink: true }, htmlInput: { min: isoDay(new Date()) } }} />
                  <Stack direction="row" spacing={0.5} sx={{ mt: 0.75 }}>
                    {[15, 30, 60].map((days) => (
                      <Chip key={days} size="small" label={`${days} days`} variant={validUntil === daysFromToday(days) ? "filled" : "outlined"}
                        color={validUntil === daysFromToday(days) ? "primary" : "default"} onClick={() => setValidUntil(daysFromToday(days))} />
                    ))}
                  </Stack>
                </Box>
              </Stack>

              <Box sx={{ mt: 3 }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>Where the prices came from</Typography>
                {confirmed ? (
                  <Typography variant="body2" color="success.main" sx={{ mt: 0.5 }}>
                    Confirmed{attestation?.confirmedBy ? ` by ${attestation.confirmedBy}` : ""}{attestation?.sourceReference ? ` · ${attestation.sourceReference}` : ""}
                  </Typography>
                ) : (
                  <>
                    {attestation?.supersededByPriceChange && (
                      <Typography variant="caption" color="warning.main">A price changed since the last confirmation, so confirm again.</Typography>
                    )}
                    <RadioGroup row value={source} onChange={(event) => setSource(event.target.value as PriceAttestationSource)}>
                      <FormControlLabel value="SALES_MANAGER" control={<Radio size="small" />} label="Approved by a sales manager" />
                      <FormControlLabel value="SUPPLIER_QUOTE" control={<Radio size="small" />} label="From a supplier quote" />
                    </RadioGroup>
                    <TextField size="small" fullWidth value={reference} onChange={(event) => setReference(event.target.value)}
                      label={source === "SALES_MANAGER" ? "Manager's name" : "Supplier quote number"}
                      placeholder={source === "SALES_MANAGER" ? "e.g. Ahmed Saleh" : "e.g. GST-Q-2026-118"} />
                  </>
                )}
              </Box>
            </Box>

            {/* ---- The email ---- */}
            <Stack spacing={1.5} sx={{ p: 2.5, overflowY: "auto" }}>
              <Typography variant="overline" color="text.secondary">Email to the customer</Typography>
              <TextField label="To" value={to} onChange={(event) => setTo(event.target.value)} error={to !== "" && !toOk}
                helperText={!to ? "No email on the customer's record. Type one." : !toOk ? "Check the email address" : undefined}
                slotProps={{ htmlInput: { "aria-label": "Customer email" } }} />
              <TextField label="Subject" value={subject} onChange={(event) => setSubject(event.target.value)} />
              <TextField label="Message" value={body} onChange={(event) => setBody(event.target.value)} multiline minRows={8} />
              {draftQuery.data?.attachmentFileName && (
                <Chip icon={<AttachFile />} label={draftQuery.data.attachmentFileName} variant="outlined" sx={{ alignSelf: "flex-start" }} />
              )}
            </Stack>
          </Box>
        )}
      </DialogContent>

      <DialogActions sx={{ px: 2.5 }}>
        <Typography variant="caption" color={reasons.length ? "warning.main" : "text.secondary"} sx={{ flex: 1 }}>
          {quote && !alreadySent ? (reasons.length ? reasons.join(" · ") : "The quote PDF is attached to the email.") : ""}
        </Typography>
        <Button onClick={onClose} disabled={send.isPending}>Cancel</Button>
        {quote && !alreadySent && (
          <Button variant="contained" startIcon={send.isPending ? <CircularProgress size={16} color="inherit" /> : <Send />}
            disabled={reasons.length > 0 || send.isPending} onClick={() => send.mutate()}>
            Send quote
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
