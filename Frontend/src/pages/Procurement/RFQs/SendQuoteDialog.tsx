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
import stockPriceService from "../../../api/services/stockPriceService";
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

type LineChoice = "ESTIMATE" | "TO_FOLLOW" | "NOT_QUOTED";
const NOT_QUOTED_REASONS = ["No supplier response in time", "Discontinued by manufacturer", "Not available", "Outside our scope"];

/**
 * A line with no price when the bid is closing: send it as "Price to follow", an estimate
 * "subject to confirmation", or "Not quoted" with a reason, so the full quote can still go out.
 */
function LineChoiceControl({ quoteId, rfqId, line, currencyCode, onSaved }: {
  quoteId: number; rfqId: number; line: QuoteLineDTO; currencyCode: string | null; onSaved: () => void;
}) {
  const { enqueueSnackbar } = useSnackbar();
  const [mode, setMode] = React.useState<LineChoice | null>(null);
  const [estimate, setEstimate] = React.useState("");
  const [reason, setReason] = React.useState("");
  const current = line.pricingStatus ?? null;

  // The estimate starts from what this part was last quoted or won at, or a supplier's last price.
  const record = useQuery({
    queryKey: ["stock-price", rfqId, line.rfqItemId],
    queryFn: () => stockPriceService.get(rfqId, line.rfqItemId!),
    enabled: mode === "ESTIMATE" && !!line.rfqItemId,
  });
  React.useEffect(() => {
    if (mode !== "ESTIMATE" || estimate !== "" || !record.data) return;
    const v = record.data;
    const hint = v.trackRecord.lastQuoted?.unitPrice ?? v.trackRecord.lastWon?.unitPrice ?? v.supplierPrices?.[0]?.cost ?? v.price.unitPrice;
    if (hint) setEstimate(String(hint));
  }, [mode, record.data, estimate]);

  const save = useMutation({
    mutationFn: (body: { status: LineChoice | null; note?: string | null; unitPrice?: number | null }) =>
      rfqService.saveLinePricing(quoteId, line.id, body),
    onSuccess: () => { setMode(null); onSaved(); },
    onError: (error) => enqueueSnackbar(describeError(error, "The line could not be updated."), { variant: "error" }),
  });

  const estimateNumber = Number(estimate);
  const chip = (choice: LineChoice, label: string) => (
    <Chip key={choice} size="small" label={label} clickable disabled={save.isPending}
      color={current === choice || mode === choice ? "primary" : "default"}
      variant={current === choice ? "filled" : "outlined"}
      onClick={() => (choice === "TO_FOLLOW" ? save.mutate({ status: "TO_FOLLOW" }) : setMode(choice))} />
  );

  return (
    <Box sx={{ mt: 0.75 }}>
      {current ? (
        <Typography variant="caption" sx={{ display: "block", fontWeight: 700, color: current === "NOT_QUOTED" ? "error.main" : "warning.dark" }}>
          {current === "TO_FOLLOW" ? "Price to follow" : current === "NOT_QUOTED" ? "Not quoted" : "Estimate, subject to confirmation"}
          {line.pricingNote ? `: ${line.pricingNote}` : ""}
        </Typography>
      ) : (
        <Typography variant="caption" color="warning.dark" sx={{ display: "block", fontWeight: 700 }}>No price yet. Send it as:</Typography>
      )}
      <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: "wrap", mt: 0.5 }}>
        {chip("TO_FOLLOW", "Price to follow")}
        {chip("ESTIMATE", "Estimate")}
        {chip("NOT_QUOTED", "Not quoting")}
      </Stack>
      {mode === "ESTIMATE" && (
        <Stack direction="row" spacing={1} sx={{ mt: 1, alignItems: "center" }}>
          <TextField size="small" type="number" label={`Estimated price${currencyCode ? ` (${currencyCode})` : ""}`} value={estimate}
            onChange={(event) => setEstimate(event.target.value)} sx={{ width: 170 }}
            slotProps={{ htmlInput: { min: 0, step: "any", "aria-label": "Estimated price" } }} />
          <Button size="small" variant="contained" disabled={!(estimateNumber > 0) || save.isPending}
            onClick={() => save.mutate({ status: "ESTIMATE", unitPrice: estimateNumber })}>Use estimate</Button>
          <Button size="small" onClick={() => setMode(null)}>Cancel</Button>
        </Stack>
      )}
      {mode === "NOT_QUOTED" && (
        <Box sx={{ mt: 1 }}>
          <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: "wrap" }}>
            {NOT_QUOTED_REASONS.map((text) => (
              <Chip key={text} size="small" label={text} variant={reason === text ? "filled" : "outlined"} onClick={() => setReason(text)} />
            ))}
          </Stack>
          <Stack direction="row" spacing={1} sx={{ mt: 1, alignItems: "center" }}>
            <TextField size="small" label="Reason the customer sees" value={reason} onChange={(event) => setReason(event.target.value)}
              sx={{ flex: 1 }} slotProps={{ htmlInput: { maxLength: 300, "aria-label": "Reason not quoted" } }} />
            <Button size="small" variant="contained" disabled={!reason.trim() || save.isPending}
              onClick={() => save.mutate({ status: "NOT_QUOTED", note: reason.trim() })}>Save</Button>
            <Button size="small" onClick={() => setMode(null)}>Cancel</Button>
          </Stack>
        </Box>
      )}
    </Box>
  );
}

export interface SendQuoteDialogProps {
  open: boolean;
  rfqId: number;
  onClose: () => void;
  /** Called once the quote was handed over for delivery (it becomes "sent" a moment later). */
  onSent?: () => void;
  /** The customer's deadline. Once it has passed, Send asks first; it never refuses. */
  deadline?: string | null;
}

/**
 * Send the RFQ's quote without leaving the RFQ: the lines and total, the currency and how long the
 * prices hold, where the prices came from, and the email the customer gets. Anything only a
 * manager can fix in Setup is named in plain words with a link.
 */
export default function SendQuoteDialog({ open, rfqId, onClose, onSent, deadline }: SendQuoteDialogProps) {
  const [confirmLate, setConfirmLate] = React.useState(false);
  // The deadline is a calendar day at the customer's end; it has passed once that day is over.
  const deadlineDay = (() => {
    const match = deadline?.match(/^(\d{4})-(\d{2})-(\d{2})/);
    return match ? new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3])) : null;
  })();
  const deadlinePassed = deadlineDay != null && deadlineDay.getTime() + 24 * 60 * 60 * 1000 <= Date.now();
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
  // The email shows the quote total: when a line choice changes it, follow the new wording unless
  // the rep has already edited the email themselves.
  const shownDraft = React.useRef<{ subject: string; body: string } | null>(null);
  React.useEffect(() => {
    const draft = draftQuery.data;
    if (!draft) return;
    const previous = shownDraft.current;
    if (previous && subject === previous.subject && body === previous.body) {
      setSubject(draft.subject);
      setBody(draft.body);
    }
    shownDraft.current = { subject: draft.subject, body: draft.body };
  }, [draftQuery.data]);
  React.useEffect(() => {
    if (quote && !quote.currencyId && currencyId === "" && currencies.length > 0) {
      setCurrencyId((currencies.find((c) => c.isBaseCurrency) ?? currencies[0]).id);
    }
  }, [quote, currencies, currencyId]);

  const lines = quote?.quoteItems ?? [];
  // Lines still to decide: no price and not sent "to follow" or "not quoted".
  const unpriced = lines.filter((line) => !(line.unitPrice > 0) && line.pricingStatus !== "TO_FOLLOW" && line.pricingStatus !== "NOT_QUOTED");
  const refreshQuote = () => {
    for (const key of ["send-quote", "send-quote-readiness", "send-quote-attestation", "send-quote-email", "stock-price"]) queryClient.invalidateQueries({ queryKey: [key] });
  };
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
                    const status = line.pricingStatus ?? null;
                    const needsChoice = !priced || status !== null;
                    return (
                      <TableRow key={line.id} sx={!priced && !status ? { bgcolor: "warning.lighter" } : undefined}>
                        <TableCell sx={{ color: "text.secondary", verticalAlign: "top" }}>{line.customerLineRef || index + 1}</TableCell>
                        <TableCell sx={{ maxWidth: 320 }}>
                          <Typography variant="body2" noWrap title={line.itemDescription ?? ""}>{line.itemDescription}</Typography>
                          {priced && deliveryOf(line) && <Typography variant="caption" color="text.secondary">{deliveryOf(line)}</Typography>}
                          {needsChoice && quoteId && (
                            <LineChoiceControl quoteId={quoteId} rfqId={rfqId} line={line} currencyCode={currencyCode} onSaved={refreshQuote} />
                          )}
                        </TableCell>
                        <TableCell align="right" sx={{ verticalAlign: "top" }}>{qty(line.quantity)} {line.unitOfMeasure}</TableCell>
                        <TableCell align="right" sx={{ verticalAlign: "top" }}>
                          {status === "TO_FOLLOW" ? "To follow" : status === "NOT_QUOTED" ? "Not quoted" : priced ? (
                            <>
                              {formatMoney(line.unitPrice, currencyCode)}
                              {status === "ESTIMATE" && <Typography variant="caption" color="warning.dark" sx={{ display: "block" }}>estimate</Typography>}
                            </>
                          ) : (
                            <Chip size="small" color="warning" icon={<WarningAmber />} label="No price" />
                          )}
                        </TableCell>
                        <TableCell align="right" sx={{ fontWeight: 700, verticalAlign: "top" }}>{priced ? formatMoney(line.taxableBase, currencyCode) : "—"}</TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
              {unpriced.length > 0 && (
                <Typography variant="body2" color="warning.main" sx={{ mt: 1, fontWeight: 700 }}>
                  {unpriced.length} {unpriced.length === 1 ? "line has" : "lines have"} no price yet. Price {unpriced.length === 1 ? "it" : "them"} on the RFQ, or send {unpriced.length === 1 ? "it" : "them"} as Price to follow, an Estimate, or Not quoting.
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
            disabled={reasons.length > 0 || send.isPending} onClick={() => (deadlinePassed ? setConfirmLate(true) : send.mutate())}>
            Send quote
          </Button>
        )}
      </DialogActions>
      {/* Owner ruling 2026-09-26: a passed deadline informs, it never blocks. */}
      <Dialog open={confirmLate} onClose={() => setConfirmLate(false)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>The deadline has passed</DialogTitle>
        <DialogContent>
          <Typography variant="body2">
            This RFQ's deadline was {deadlineDay?.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" })}.
            Do you really want to send the quote?
          </Typography>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirmLate(false)}>Cancel</Button>
          <Button variant="contained" onClick={() => { setConfirmLate(false); send.mutate(); }}>Send anyway</Button>
        </DialogActions>
      </Dialog>
    </Dialog>
  );
}
