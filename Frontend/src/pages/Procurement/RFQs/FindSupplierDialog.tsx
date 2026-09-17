import React from "react";
import { useInfiniteQuery, useMutation, useQuery } from "@tanstack/react-query";
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
  FormControlLabel,
  Link,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { Send } from "@mui/icons-material";
import supplierEmailSettingsService from "../../../api/services/supplierEmailSettingsService";
import type { SupplierEmailWordingEdit } from "../../../api/services/procurementService";
import procurementService, {
  type SourcingCase,
  type SupplierDiscoveryHit,
} from "../../../api/services/procurementService";

export const DEFAULT_SUPPLIER_MESSAGE = "Please submit your best pricing and lead times.";
export const RECONFIRM_PRICE_MESSAGE = "Please confirm your current price, availability and validity for this part.";

const PAGE = 10;

/** Evidence that the supplier sold or quoted this part before: those are ticked for the rep, who can untick them. */
const PAST_SUPPLY = new Set(["PRIOR_SUPPLIER_QUOTE", "PURCHASE_HISTORY", "PURCHASE_ORDER_HISTORY", "PREFERRED_SUPPLIER"]);

const roleLabel: Record<string, string> = { Manufacturer: "Maker", Distributor: "Distributor", Reseller: "Reseller" };

export interface FindSupplierLine {
  rfqItemId: number;
  partNumber?: string | null;
  maker?: string | null;
  description?: string | null;
  unitOfMeasure?: string | null;
  requested: number;
  inStock: number;
  toSource: number;
}

interface Props {
  open: boolean;
  line: FindSupplierLine | null;
  /** Opens (or creates) the sourcing record behind the line. Adds the part to the catalogue first when it is not there. */
  openCase: () => Promise<SourcingCase>;
  /** Suppliers to tick when the window opens, e.g. the supplier whose price expired. */
  presetSupplierIds?: number[];
  presetMessage?: string;
  /** What already happened with each supplier on this line, e.g. "Asked 16 Sep · waiting for reply". Shown on the row; asking again stays the rep's choice. */
  earlierRequests?: Map<number, string>;
  onClose: () => void;
  /**
   * For a part not in the catalogue: the window asks whether to add it. The part is kept out while
   * the window is open; on Send with the tick on, this puts it in.
   */
  catalogueChoice?: { addToCatalogue: (productId: number) => Promise<unknown> };
  /** allDone is false when some suppliers could not be sent to; the window then stays open and says why. */
  onSent: (sentTo: string[], allDone: boolean) => void;
}

type Row = {
  key: string;
  name: string;
  email: string | null;
  detail: string;
  role?: string;
  country?: string | null;
  supplierId?: number;
  hitId?: string;
  blocked?: string;
};

/**
 * One small window per RFQ line: who to ask, how many, what to say, Send. The rep's own suppliers
 * come first, then companies found on the internet (makers, distributors, resellers). Every
 * default is a suggestion: ticks, quantity and message can all be changed.
 */
export default function FindSupplierDialog({
  open, line, openCase, presetSupplierIds, presetMessage, earlierRequests, catalogueChoice, onClose, onSent,
}: Props) {
  const [ticked, setTicked] = React.useState<Set<string>>(new Set());
  const [quantity, setQuantity] = React.useState("");
  const [message, setMessage] = React.useState(DEFAULT_SUPPLIER_MESSAGE);
  const [replyBy, setReplyBy] = React.useState("");
  const [failures, setFailures] = React.useState<string[]>([]);
  const [sendFrom, setSendFrom] = React.useState<number | "">("");
  const [savedDefault, setSavedDefault] = React.useState(false);
  const [cc, setCc] = React.useState("");
  const [bcc, setBcc] = React.useState("");
  const copiesTouched = React.useRef(false);
  const [newEmail, setNewEmail] = React.useState("");
  const [newName, setNewName] = React.useState("");
  const [addError, setAddError] = React.useState<string | null>(null);
  const [editing, setEditing] = React.useState(false);
  const [addToCatalogue, setAddToCatalogue] = React.useState(true);
  const [wording, setWording] = React.useState({ subject: "", greeting: "", opening: "", signOff: "" });
  const messageTouched = React.useRef(false);

  // The rep's own default message (or the company's), and the company mailboxes to send from.
  const effective = useQuery({
    queryKey: ["supplier-email-effective"],
    queryFn: supplierEmailSettingsService.getEffective,
    enabled: open,
    staleTime: 60_000,
    retry: false,
  });
  const mailboxes = useQuery({
    queryKey: ["supplier-email-send-from"],
    queryFn: supplierEmailSettingsService.getSendFrom,
    enabled: open,
    staleTime: 60_000,
    retry: false,
  });
  const initialised = React.useRef(false);

  const caseQuery = useQuery({
    queryKey: ["find-supplier-case", line?.rfqItemId],
    queryFn: openCase,
    enabled: open && Boolean(line),
    retry: false,
    staleTime: 0,
    gcTime: 0,
  });
  const sourcingCase = caseQuery.data;

  const internet = useInfiniteQuery({
    queryKey: ["find-supplier-internet", sourcingCase?.id],
    queryFn: ({ pageParam }) => procurementService.discoverSuppliers(sourcingCase!.id, { offset: pageParam, limit: PAGE }),
    initialPageParam: 0,
    getNextPageParam: (last) => (last.status === "Ready" && last.offset + last.limit < last.total ? last.offset + last.limit : undefined),
    enabled: open && Boolean(sourcingCase),
    retry: false,
    staleTime: 60_000,
  });

  const ownRows: Row[] = (sourcingCase?.candidates ?? []).map((candidate) => ({
    key: `s-${candidate.supplierId}`,
    name: candidate.supplierName,
    email: candidate.contactEmail ?? null,
    detail: earlierRequests?.get(candidate.supplierId) ?? candidate.recommendationReason,
    supplierId: candidate.supplierId,
    blocked: candidate.eligibleForSupplierRfq ? undefined : candidate.blockingReasons?.[0] ?? "Cannot be asked",
  }));
  const ownIds = new Set(ownRows.map((row) => row.supplierId));
  const hits: SupplierDiscoveryHit[] = (internet.data?.pages ?? []).flatMap((page) => page.hits);
  const internetRows: Row[] = hits
    .filter((hit) => hit.existingSupplierId === null || !ownIds.has(hit.existingSupplierId))
    .map((hit) => ({
      key: `h-${hit.id}`,
      name: hit.name,
      email: hit.contactEmail,
      detail: hit.why,
      role: roleLabel[hit.role] ?? hit.role,
      country: hit.country,
      hitId: hit.existingSupplierId === null ? hit.id : undefined,
      supplierId: hit.existingSupplierId ?? undefined,
      blocked: hit.contactEmail ? undefined : "No email found",
    }));
  const firstInternetPage = internet.data?.pages[0];
  const moreInternet = internet.hasNextPage;

  // Defaults, set once per opening: past suppliers of this part ticked, quantity = what is short.
  React.useEffect(() => {
    if (!open) {
      initialised.current = false;
      setSendFrom("");
      copiesTouched.current = false;
      setAddToCatalogue(true);
      setEditing(false);
      setNewEmail("");
      setNewName("");
      setAddError(null);
      return;
    }
    if (initialised.current || !sourcingCase || !line) return;
    initialised.current = true;
    const preset = new Set(presetSupplierIds ?? []);
    setTicked(new Set((sourcingCase.candidates ?? [])
      .filter((candidate) => candidate.eligibleForSupplierRfq
        && (preset.has(candidate.supplierId) || (PAST_SUPPLY.has(candidate.evidenceType) && !earlierRequests?.has(candidate.supplierId))))
      .map((candidate) => `s-${candidate.supplierId}`)));
    setQuantity(String(line.toSource > 0 ? line.toSource : line.requested));
    messageTouched.current = false;
    setSavedDefault(false);
    setMessage(presetMessage ?? effective.data?.defaultMessage ?? DEFAULT_SUPPLIER_MESSAGE);
    setReplyBy("");
    setFailures([]);
  }, [open, sourcingCase, line, presetSupplierIds, presetMessage, earlierRequests]);

  // The saved default can arrive after the window opened; use it unless the rep already typed.
  React.useEffect(() => {
    if (open && !presetMessage && !messageTouched.current && effective.data?.defaultMessage) {
      setMessage(effective.data.defaultMessage);
    }
  }, [open, presetMessage, effective.data]);
  React.useEffect(() => {
    if (!open || copiesTouched.current || !effective.data) return;
    setCc((effective.data.defaultCc ?? []).join(", "));
    setBcc((effective.data.defaultBcc ?? []).join(", "));
  }, [open, effective.data]);

  React.useEffect(() => {
    if (!open || sendFrom !== "" || !mailboxes.data) return;
    const preferred = mailboxes.data.mailboxes.find((mailbox) => mailbox.isDefault) ?? mailboxes.data.mailboxes[0];
    if (preferred) setSendFrom(preferred.mailboxId);
  }, [open, sendFrom, mailboxes.data]);

  const splitAddresses = (text: string) =>
    [...new Set(text.split(/[,;\s]+/).map((part) => part.trim().toLowerCase()).filter(Boolean))];
  const ccList = splitAddresses(cc);
  const bccList = splitAddresses(bcc);
  const badCopy = [...ccList, ...bccList].find((address) => !/^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(address));
  const wordingEdit: SupplierEmailWordingEdit | null = editing ? {
    subject: wording.subject.trim() || null,
    greeting: wording.greeting.trim() || null,
    opening: wording.opening.trim() || null,
    signOff: wording.signOff.trim() || null,
  } : null;

  const startEditing = () => {
    if (!editing && effective.data) {
      setWording({
        subject: effective.data.subject, greeting: effective.data.greeting,
        opening: effective.data.opening, signOff: effective.data.signOff,
      });
    }
    setEditing((value) => !value);
  };

  const addByEmail = useMutation({
    mutationFn: () => procurementService.addSupplierByEmail(sourcingCase!.id, newEmail.trim(), newName),
    onSuccess: async (added) => {
      setAddError(null);
      setNewEmail("");
      setNewName("");
      await caseQuery.refetch();
      setTicked((current) => new Set([...current, `s-${added.supplierId}`]));
    },
    onError: (error: unknown) => {
      const e = error as { response?: { data?: { detail?: string; title?: string } }; message?: string };
      setAddError(e?.response?.data?.detail || e?.message || "That supplier could not be added.");
    },
  });

  const saveMyDefault = useMutation({
    mutationFn: async () => {
      const mine = await supplierEmailSettingsService.getMine();
      return supplierEmailSettingsService.saveMine({ defaultMessage: message.trim() || null, signOff: mine.signOff });
    },
    onSuccess: () => setSavedDefault(true),
  });

  const toggle = (key: string) => setTicked((current) => {
    const next = new Set(current);
    if (next.has(key)) next.delete(key); else next.add(key);
    return next;
  });

  const quantityNumber = Number(quantity);
  const quantityOk = Number.isFinite(quantityNumber) && quantityNumber > 0;
  const dueOnIso = replyBy ? new Date(`${replyBy}T17:00:00`).toISOString() : null;

  // The email below is written by the server exactly as it will be sent, and follows what the rep types.
  const [previewInput, setPreviewInput] = React.useState({
    quantity: 0, message: "", dueOn: null as string | null, sendFromMailboxId: null as number | null,
    cc: [] as string[], bcc: [] as string[], wording: null as SupplierEmailWordingEdit | null,
  });
  const copiesKey = `${ccList.join(",")}|${bccList.join(",")}|${badCopy ?? ""}`;
  const wordingKey = JSON.stringify(wordingEdit);
  React.useEffect(() => {
    const timer = window.setTimeout(() => setPreviewInput({
      quantity: quantityOk ? quantityNumber : 0, message, dueOn: dueOnIso, sendFromMailboxId: sendFrom === "" ? null : sendFrom,
      cc: badCopy ? [] : ccList, bcc: badCopy ? [] : bccList, wording: wordingEdit,
    }), 400);
    return () => window.clearTimeout(timer);
  }, [quantityOk, quantityNumber, message, dueOnIso, sendFrom, copiesKey, wordingKey]);
  const preview = useQuery({
    queryKey: ["find-supplier-email", sourcingCase?.id, previewInput],
    queryFn: () => procurementService.previewSupplierRfqEmail(sourcingCase!.id, {
      quantity: previewInput.quantity || null,
      message: previewInput.message.trim() || null,
      dueOn: previewInput.dueOn,
      sendFromMailboxId: previewInput.sendFromMailboxId,
      cc: previewInput.cc,
      bcc: previewInput.bcc,
      wording: previewInput.wording,
    }),
    enabled: open && Boolean(sourcingCase),
    retry: false,
    placeholderData: (previous) => previous,
  });
  const tickedRows = [...ownRows, ...internetRows].filter((row) => ticked.has(row.key) && !row.blocked);

  const send = useMutation({
    mutationFn: async () => {
      if (!sourcingCase) throw new Error("The line is still opening.");
      const hitIds = tickedRows.map((row) => row.hitId).filter((id): id is string => Boolean(id));
      const supplierIds = new Set(tickedRows.map((row) => row.supplierId).filter((id): id is number => typeof id === "number"));
      if (hitIds.length > 0) {
        const adopted = await procurementService.adoptDiscoveredSuppliers(sourcingCase.id, hitIds);
        adopted.adopted.forEach((entry) => supplierIds.add(entry.supplierId));
      }
      const fresh = await procurementService.getSourcingCase(sourcingCase.id);
      const names = new Map(fresh.candidates.map((candidate) => [candidate.supplierId, candidate.supplierName]));
      const askable = [...supplierIds].filter((id) => names.has(id));
      const results = await procurementService.prepareSupplierRfqs(
        fresh.id, askable, fresh.version, crypto.randomUUID(),
        dueOnIso,
        message.trim() || null,
        quantityNumber,
        sendFrom === "" ? null : sendFrom,
        { cc: ccList, bcc: bccList, wording: wordingEdit },
      );
      const failed = results.filter((result) => !result.succeeded).map((result) => {
        const error = result.error as { response?: { data?: { detail?: string; message?: string } }; message?: string } | undefined;
        const reason = error?.response?.data?.detail || error?.response?.data?.message || error?.message || "could not be sent";
        return `${names.get(result.supplierId) ?? "A supplier"}: ${reason}`;
      });
      const sent = results.filter((result) => result.succeeded).map((result) => names.get(result.supplierId) ?? "supplier");
      if (catalogueChoice && addToCatalogue && sent.length > 0 && fresh.productId) {
        await catalogueChoice.addToCatalogue(fresh.productId);
      }
      return { sent, failed, missing: supplierIds.size - askable.length };
    },
    onSuccess: ({ sent, failed, missing }) => {
      if (missing > 0) failed.push(`${missing} ticked supplier${missing === 1 ? "" : "s"} could not be added to this line`);
      if (failed.length === 0) {
        onSent(sent, true);
        return;
      }
      setFailures(failed);
      if (sent.length > 0) {
        // The ones that went are done: untick them so a second press does not ask them twice.
        setTicked((current) => new Set([...current].filter((key) => !tickedRows.some((row) => row.key === key && sent.includes(row.name)))));
        onSent(sent, false);
      }
    },
    onError: (error: unknown) => {
      const e = error as { response?: { data?: { detail?: string; message?: string } }; message?: string };
      setFailures([e?.response?.data?.detail || e?.response?.data?.message || e?.message || "The request could not be sent."]);
    },
  });

  const sendLabel = send.isPending
    ? "Sending…"
    : tickedRows.length === 0 ? "Tick who to ask" : `Send to ${tickedRows.length} supplier${tickedRows.length === 1 ? "" : "s"}`;

  const renderRow = (row: Row) => (
    <Stack key={row.key} direction="row" spacing={1} sx={{ alignItems: "flex-start", py: 0.75, borderBottom: 1, borderColor: "divider" }}>
      <Checkbox
        size="small"
        checked={ticked.has(row.key) && !row.blocked}
        disabled={Boolean(row.blocked)}
        onChange={() => toggle(row.key)}
        slotProps={{ input: { "aria-label": `Ask ${row.name}` } }}
        sx={{ mt: -0.5 }}
      />
      <Box sx={{ minWidth: 0, flex: 1 }}>
        <Stack direction="row" spacing={0.75} useFlexGap sx={{ alignItems: "center", flexWrap: "wrap" }}>
          <Typography variant="body2" sx={{ fontWeight: 700 }}>{row.name}</Typography>
          {row.role && <Chip size="small" variant="outlined" label={row.role} />}
          {row.country && <Typography variant="caption" color="text.secondary">{row.country}</Typography>}
        </Stack>
        <Typography variant="caption" color={row.blocked ? "warning.main" : "text.secondary"} sx={{ display: "block" }}>
          {row.blocked ? row.blocked : row.email}
          {row.detail ? ` · ${row.detail}` : ""}
        </Typography>
      </Box>
    </Stack>
  );

  return (
    <Dialog open={open} onClose={send.isPending ? undefined : onClose} maxWidth="sm" fullWidth>
      <DialogTitle sx={{ pb: 1 }}>
        <Typography component="span" variant="h6" sx={{ fontWeight: 800, display: "block" }}>Find supplier</Typography>
        {line && (
          <>
            {line.description && (
              <Typography component="span" variant="body2" sx={{ display: "block", fontWeight: 600 }}>{line.description}</Typography>
            )}
            <Typography component="span" variant="body2" color="text.secondary" sx={{ display: "block" }}>
              {[line.partNumber && `Part ${line.partNumber}`, line.maker].filter(Boolean).join(" · ")}
              {line.partNumber || line.maker ? " · " : ""}
              {line.inStock > 0 && line.toSource > 0
                ? `${line.inStock} in stock, ${line.toSource} to source`
                : `${line.requested} ${line.unitOfMeasure ?? ""}`.trim()}
            </Typography>
          </>
        )}
      </DialogTitle>
      <DialogContent dividers sx={{ pt: 1 }}>
        {caseQuery.isLoading && (
          <Stack direction="row" spacing={1.5} sx={{ alignItems: "center", py: 3 }} role="status">
            <CircularProgress size={18} />
            <Typography variant="body2">Opening this line…</Typography>
          </Stack>
        )}
        {caseQuery.isError && (
          <Alert severity="error" action={<Button color="inherit" onClick={() => caseQuery.refetch()}>Try again</Button>}>
            {(caseQuery.error as { response?: { data?: { detail?: string } }; message?: string })?.response?.data?.detail
              || (caseQuery.error as Error)?.message || "This line could not be opened."}
          </Alert>
        )}

        {sourcingCase && (
          <>
            <Typography variant="overline" color="text.secondary">Your suppliers</Typography>
            {ownRows.length === 0
              ? <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>None of your suppliers is linked to this part yet.</Typography>
              : ownRows.map(renderRow)}

            <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ mt: 1.5, alignItems: { sm: "flex-start" } }}>
              <TextField
                size="small"
                label="Add a supplier by email"
                placeholder="sales@supplier.com"
                value={newEmail}
                onChange={(event) => { setNewEmail(event.target.value); setAddError(null); }}
                onKeyDown={(event) => { if (event.key === "Enter" && newEmail.trim()) { event.preventDefault(); addByEmail.mutate(); } }}
                error={Boolean(addError)}
                helperText={addError ?? "A supplier you know that is not listed."}
                sx={{ flex: 2 }}
              />
              <TextField
                size="small"
                label="Company name (optional)"
                value={newName}
                onChange={(event) => setNewName(event.target.value)}
                helperText=" "
                sx={{ flex: 1.4 }}
              />
              <Button
                variant="outlined"
                disabled={!newEmail.trim() || addByEmail.isPending}
                onClick={() => addByEmail.mutate()}
                sx={{ mt: { sm: 0.25 }, whiteSpace: "nowrap" }}
              >
                {addByEmail.isPending ? "Adding…" : "Add and tick"}
              </Button>
            </Stack>

            <Typography variant="overline" color="text.secondary" sx={{ display: "block", mt: 1 }}>From the internet</Typography>
            {internet.isLoading && (
              <Stack direction="row" spacing={1.5} sx={{ alignItems: "center", py: 1 }} role="status">
                <CircularProgress size={16} />
                <Typography variant="body2" color="text.secondary">Searching makers, distributors and resellers…</Typography>
              </Stack>
            )}
            {firstInternetPage && firstInternetPage.status !== "Ready" && (
              <Typography variant="body2" color="text.secondary">{firstInternetPage.message}</Typography>
            )}
            {internet.isError && <Typography variant="body2" color="text.secondary">The internet could not be searched just now.</Typography>}
            {internetRows.map(renderRow)}
            {moreInternet && (
              <Button size="small" sx={{ mt: 1 }} disabled={internet.isFetchingNextPage} onClick={() => internet.fetchNextPage()}>
                {internet.isFetchingNextPage ? "Loading…" : "Show 10 more"}
              </Button>
            )}

            <Stack direction="row" spacing={1.5} sx={{ mt: 2.5 }}>
              <TextField
                label="Quantity to ask for"
                size="small"
                type="number"
                value={quantity}
                onChange={(event) => setQuantity(event.target.value)}
                error={!quantityOk}
                helperText={line && line.inStock > 0 ? `${line.requested} requested, ${line.inStock} in stock` : " "}
                slotProps={{ htmlInput: { min: 0, step: "any" } }}
                sx={{ width: 190 }}
              />
              <TextField
                label="Reply by (optional)"
                size="small"
                type="date"
                value={replyBy}
                onChange={(event) => setReplyBy(event.target.value)}
                slotProps={{ inputLabel: { shrink: true } }}
                helperText=" "
                sx={{ width: 190 }}
              />
            </Stack>
            {mailboxes.data && (
              mailboxes.data.mailboxes.length > 0 ? (
                <TextField
                  select
                  label="Send from"
                  size="small"
                  fullWidth
                  value={sendFrom}
                  onChange={(event) => setSendFrom(Number(event.target.value))}
                  helperText={mailboxes.data.replyTo ? `Supplier replies go to ${mailboxes.data.replyTo}` : " "}
                  sx={{ mt: 1 }}
                >
                  {mailboxes.data.mailboxes.map((mailbox) => (
                    <MenuItem key={mailbox.mailboxId} value={mailbox.mailboxId}>
                      {mailbox.address}{mailbox.label && mailbox.label !== mailbox.address ? ` · ${mailbox.label}` : ""}
                    </MenuItem>
                  ))}
                </TextField>
              ) : (
                <Typography variant="caption" color="text.secondary" sx={{ display: "block", mt: 1 }}>
                  Sent under {mailboxes.data.companyName}&apos;s name{mailboxes.data.replyTo ? `; supplier replies go to ${mailboxes.data.replyTo}` : ""}.
                  Your company has no outgoing mailbox set up yet.
                </Typography>
              )
            )}
            <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5} sx={{ mt: 1 }}>
              <TextField
                size="small"
                fullWidth
                label="CC"
                placeholder="colleague@yourcompany.com"
                value={cc}
                onChange={(event) => { copiesTouched.current = true; setCc(event.target.value); }}
                error={Boolean(badCopy && ccList.includes(badCopy))}
                helperText={badCopy && ccList.includes(badCopy) ? `"${badCopy}" is not an email address` : "Separate addresses with commas"}
              />
              <TextField
                size="small"
                fullWidth
                label="BCC"
                value={bcc}
                onChange={(event) => { copiesTouched.current = true; setBcc(event.target.value); }}
                error={Boolean(badCopy && bccList.includes(badCopy))}
                helperText={badCopy && bccList.includes(badCopy) ? `"${badCopy}" is not an email address` : "Suppliers do not see these"}
              />
            </Stack>
            <TextField
              label="Your message"
              size="small"
              multiline
              minRows={2}
              fullWidth
              value={message}
              onChange={(event) => { messageTouched.current = true; setSavedDefault(false); setMessage(event.target.value); }}
              slotProps={{ htmlInput: { maxLength: 2000 } }}
              sx={{ mt: 1 }}
            />
            <Stack direction="row" spacing={1} sx={{ alignItems: "center", mt: 0.5 }}>
              <Link component="button" type="button" variant="caption" underline="hover"
                disabled={saveMyDefault.isPending || !message.trim()}
                onClick={() => saveMyDefault.mutate()}>
                {saveMyDefault.isPending ? "Saving…" : "Save as my default message"}
              </Link>
              {savedDefault && <Typography variant="caption" color="success.main">Saved. Your next requests start with this message.</Typography>}
              {saveMyDefault.isError && <Typography variant="caption" color="error.main">Could not save it just now.</Typography>}
            </Stack>
            {catalogueChoice && (
              <FormControlLabel
                sx={{ mt: 1, display: "flex" }}
                control={<Checkbox size="small" checked={addToCatalogue} onChange={(event) => setAddToCatalogue(event.target.checked)} />}
                label={
                  <Typography variant="body2">
                    Add this part to my catalogue
                    <Typography component="span" variant="caption" color="text.secondary" sx={{ display: "block" }}>
                      {addToCatalogue ? "It is added when you press Send." : "Suppliers are still asked; the part stays out of your catalogue."}
                    </Typography>
                  </Typography>
                }
              />
            )}
            <Link component="button" type="button" variant="body2" underline="hover" sx={{ mt: 1.5, display: "block" }} onClick={startEditing}>
              {editing ? "Use the saved wording" : "Edit the email for this send"}
            </Link>
            {editing && (
              <Stack spacing={1.25} sx={{ mt: 1.5, p: 1.5, border: 1, borderColor: "divider", borderRadius: 1 }}>
                <Typography variant="caption" color="text.secondary">
                  Changes apply to this send only. [Supplier name], [Company name] and [RFQ number] are filled in for each supplier.
                </Typography>
                <TextField size="small" fullWidth label="Subject line" value={wording.subject}
                  onChange={(event) => setWording((current) => ({ ...current, subject: event.target.value }))}
                  slotProps={{ htmlInput: { maxLength: 200 } }} />
                <TextField size="small" fullWidth label="Greeting" value={wording.greeting}
                  onChange={(event) => setWording((current) => ({ ...current, greeting: event.target.value }))}
                  slotProps={{ htmlInput: { maxLength: 200 } }} />
                <TextField size="small" fullWidth multiline minRows={2} label="Opening sentence" value={wording.opening}
                  onChange={(event) => setWording((current) => ({ ...current, opening: event.target.value }))}
                  slotProps={{ htmlInput: { maxLength: 1000 } }} />
                <Box sx={{ p: 1, borderRadius: 1, bgcolor: "action.hover" }}>
                  <Typography variant="caption" color="text.secondary">
                    The part details (description, part number, makers, quantity, needed-by date) are added here and cannot be edited.
                  </Typography>
                </Box>
                <TextField size="small" fullWidth multiline minRows={3} label="Sign-off and signature" value={wording.signOff}
                  onChange={(event) => setWording((current) => ({ ...current, signOff: event.target.value }))}
                  slotProps={{ htmlInput: { maxLength: 1000 } }} />
              </Stack>
            )}

            <Typography variant="overline" color="text.secondary" sx={{ display: "block", mt: 2 }}>What each supplier receives</Typography>
            <Box
              data-testid="supplier-email-preview"
              sx={{ p: 1.5, border: 1, borderColor: "divider", borderRadius: 1, maxHeight: 240, overflowY: "auto", bgcolor: "background.default" }}
            >
              {preview.data ? (
                <>
                  {preview.data.from && (
                    <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>From: {preview.data.from}</Typography>
                  )}
                  {(preview.data.cc?.length ?? 0) > 0 && (
                    <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>CC: {preview.data.cc!.join(", ")}</Typography>
                  )}
                  {(preview.data.bcc?.length ?? 0) > 0 && (
                    <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>BCC: {preview.data.bcc!.join(", ")}</Typography>
                  )}
                  {preview.data.replyTo && (
                    <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>Replies to: {preview.data.replyTo}</Typography>
                  )}
                  <Typography variant="body2" sx={{ fontWeight: 700, mb: 1, mt: 0.5 }}>{preview.data.subject}</Typography>
                  <Typography variant="body2" component="div" sx={{ whiteSpace: "pre-wrap", fontSize: 13, lineHeight: 1.5 }}>
                    {preview.data.body}
                  </Typography>
                </>
              ) : preview.isError ? (
                <Typography variant="body2" color="text.secondary">The email preview could not be loaded; the email is still sent with the details above.</Typography>
              ) : (
                <Typography variant="body2" color="text.secondary">Writing the email…</Typography>
              )}
            </Box>
            <Typography variant="caption" color="text.secondary" sx={{ display: "block", mt: 0.5 }}>
              &lt;supplier name&gt; is replaced with each supplier&apos;s name. Your customer is not named, and their target prices and your margins are never included.
            </Typography>

            {failures.length > 0 && (
              <Alert severity="warning" sx={{ mt: 2 }}>
                {failures.map((failure) => <Box key={failure}>{failure}</Box>)}
              </Alert>
            )}
          </>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={send.isPending}>Cancel</Button>
        <Button
          variant="contained"
          startIcon={send.isPending ? <CircularProgress size={16} color="inherit" /> : <Send />}
          disabled={!sourcingCase || send.isPending || tickedRows.length === 0 || !quantityOk || Boolean(badCopy)}
          onClick={() => send.mutate()}
        >
          {sendLabel}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
