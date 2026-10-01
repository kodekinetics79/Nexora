import React from "react";
import { useInfiniteQuery, useMutation, useQuery } from "@tanstack/react-query";
import {
  Alert,
  Badge,
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
  InputAdornment,
  Link,
  MenuItem,
  Stack,
  Tab,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Tabs,
  TextField,
  Tooltip,
  Typography,
} from "@mui/material";
import { Edit, Lock, Send, Visibility } from "@mui/icons-material";
import supplierEmailSettingsService from "../../../api/services/supplierEmailSettingsService";
import { makerLabel } from "./LineMakers";
import type { SupplierEmailWordingEdit } from "../../../api/services/procurementService";
import procurementService, {
  type SourcingCase,
  type SupplierDiscoveryHit,
} from "../../../api/services/procurementService";

export const DEFAULT_SUPPLIER_MESSAGE = "Please submit your best pricing and lead times.";
export const RECONFIRM_PRICE_MESSAGE = "Please confirm your current price, availability and validity for this part.";

const PAGE = 10;

// Suppliers found before these reasons were reworded keep the old stored text; show it in plain words.
const LEGACY_REASONS: Record<string, string> = {
  "Preferred supplier recorded on the matched Product": "Your preferred supplier for this part",
  "Prior persisted Supplier Quote for this Product": "Quoted you this part before",
  "Prior persisted Supplier Purchase Order for this Product": "You bought this part from them",
  "Persisted supplier metadata matches the requested part or manufacturer": "Listed for this part or maker",
};
const plainReason = (reason: string) => LEGACY_REASONS[reason] ?? reason;

/** Evidence that the supplier sold or quoted this part before: those are ticked for the rep, who can untick them. */
const PAST_SUPPLY = new Set(["PRIOR_SUPPLIER_QUOTE", "PURCHASE_HISTORY", "PURCHASE_ORDER_HISTORY", "PREFERRED_SUPPLIER"]);

const roleLabel: Record<string, string> = { Manufacturer: "Maker", Distributor: "Distributor", Reseller: "Reseller" };

export interface FindSupplierLine {
  rfqItemId: number;
  partNumber?: string | null;
  maker?: string | null;
  /** Every maker the customer accepts, the named one first ("ABB S203", "GE THQL32010", "Eaton"). */
  acceptedMakers?: string[];
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
  knownNetwork?: "In Network" | "Extended Network" | "Other known suppliers";
};

const knownNetworkFor = (tier?: string | null): NonNullable<Row["knownNetwork"]> => {
  if (tier === "TIER_1_PARTNER") return "In Network";
  if (tier === "TIER_2_EXTENDED") return "Extended Network";
  return "Other known suppliers";
};

const KNOWN_NETWORK_ORDER: Record<NonNullable<Row["knownNetwork"]>, number> = {
  "In Network": 0,
  "Extended Network": 1,
  "Other known suppliers": 2,
};

/**
 * One small window per RFQ line: who to ask, how many, what to say, Send. The rep's own suppliers
 * come first, grouped by the customer's In Network and Extended Network tiers, then companies
 * found on the internet (makers, distributors, resellers). Every default is a suggestion: ticks,
 * quantity and message can all be changed.
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
  const [extraTo, setExtraTo] = React.useState("");
  const [tab, setTab] = React.useState<"own" | "internet">("own");
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
    // One quiet retry, then the tab says so itself with a Try again button; no red toast on top.
    retry: 1,
    staleTime: 60_000,
    meta: { silenceGlobalError: true },
  });

  const ownRows: Row[] = (sourcingCase?.candidates ?? [])
    .map((candidate) => ({
      key: `s-${candidate.supplierId}`,
      name: candidate.supplierName,
      email: candidate.contactEmail ?? null,
      detail: earlierRequests?.get(candidate.supplierId) ?? plainReason(candidate.recommendationReason),
      supplierId: candidate.supplierId,
      blocked: candidate.eligibleForSupplierRfq ? undefined : candidate.blockingReasons?.[0] ?? "Cannot be asked",
      knownNetwork: knownNetworkFor(candidate.supplierTier),
    }))
    .sort((left, right) => KNOWN_NETWORK_ORDER[left.knownNetwork!] - KNOWN_NETWORK_ORDER[right.knownNetwork!]);
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
      setExtraTo("");
      setTab("own");
      return;
    }
    if (initialised.current || !sourcingCase || !line) return;
    initialised.current = true;
    const preset = new Set(presetSupplierIds ?? []);
    // "Ask again" names exactly who to ask; only a plain Find supplier pre-ticks past suppliers.
    setTicked(new Set((sourcingCase.candidates ?? [])
      .filter((candidate) => candidate.eligibleForSupplierRfq
        && (preset.size > 0
          ? preset.has(candidate.supplierId)
          : PAST_SUPPLY.has(candidate.evidenceType) && !earlierRequests?.has(candidate.supplierId)))
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
    setCc((effective.data.defaultCc ?? []).join("; "));
    setBcc((effective.data.defaultBcc ?? []).join("; "));
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
  const isEmail = (address: string) => /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(address);
  const extraToList = splitAddresses(extraTo);
  const badTo = extraToList.find((address) => !isEmail(address));
  const badCopy = [...ccList, ...bccList].find((address) => !isEmail(address));
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
      // Addresses typed into To join the supplier list for this part (an address a supplier already
      // uses brings that supplier), then are asked like any ticked supplier.
      for (const address of extraToList) {
        const added = await procurementService.addSupplierByEmail(sourcingCase.id, address, null);
        supplierIds.add(added.supplierId);
      }
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
        setExtraTo("");
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

  const recipientCount = tickedRows.length + extraToList.length;
  const sendLabel = send.isPending
    ? "Sending…"
    : recipientCount === 0 ? "Tick who to ask" : `Send to ${recipientCount} supplier${recipientCount === 1 ? "" : "s"}`;

  // The server's email, split so the part lines can be shown as the locked block they are.
  const body = preview.data?.body ?? "";
  const partStart = body.indexOf("RFQ number:");
  const respondAt = body.indexOf("Respond by:");
  const partEnd = respondAt >= 0 ? (body.indexOf("\n", respondAt) >= 0 ? body.indexOf("\n", respondAt) : body.length) : -1;
  const hasPartBlock = partStart >= 0 && partEnd > partStart;
  const bodyBefore = hasPartBlock ? body.slice(0, partStart).replace(/^Request for Quotation[^\n]*\n+/, "").trim() : body;
  const partBlock = hasPartBlock ? body.slice(partStart, partEnd).trim() : "";
  const bodyAfter = hasPartBlock ? body.slice(partEnd).trim() : "";

  // No known route for this part: open new-supplier discovery rather than an empty tab.
  const tabTouched = React.useRef(false);
  React.useEffect(() => {
    if (!open) { tabTouched.current = false; return; }
    if (!tabTouched.current && sourcingCase && ownRows.length === 0 && tab === "own") setTab("internet");
  }, [open, sourcingCase, ownRows.length, tab]);

  const ownTicked = ownRows.filter((row) => ticked.has(row.key) && !row.blocked).length;
  const internetTicked = internetRows.filter((row) => ticked.has(row.key) && !row.blocked).length;
  const rows = tab === "own" ? ownRows : internetRows;

  const supplierTable = (
    <Table size="small" stickyHeader aria-label={tab === "own" ? "Your known suppliers" : "Suppliers found on the internet"}>
      <TableHead>
        <TableRow>
          <TableCell padding="checkbox" />
          <TableCell sx={{ fontWeight: 700 }}>Supplier</TableCell>
          <TableCell sx={{ fontWeight: 700 }}>Email</TableCell>
        </TableRow>
      </TableHead>
      <TableBody>
        {rows.map((row, index) => {
          const isTicked = ticked.has(row.key) && !row.blocked;
          const startsKnownNetwork = tab === "own"
            && row.knownNetwork !== rows[index - 1]?.knownNetwork;
          return (
            <React.Fragment key={row.key}>
              {startsKnownNetwork && (
                <TableRow>
                  <TableCell colSpan={3} sx={{ py: 0.75, bgcolor: "action.hover", borderBottomColor: "divider" }}>
                    <Typography variant="caption" sx={{ fontWeight: 800, color: "text.primary" }}>
                      {row.knownNetwork}
                    </Typography>
                  </TableCell>
                </TableRow>
              )}
              <TableRow hover selected={isTicked} sx={{ opacity: row.blocked ? 0.6 : 1 }}>
                <TableCell padding="checkbox">
                  <Checkbox
                    size="small"
                    checked={isTicked}
                    disabled={Boolean(row.blocked)}
                    onChange={() => toggle(row.key)}
                    slotProps={{ input: { "aria-label": `Ask ${row.name}` } }}
                  />
                </TableCell>
                <TableCell sx={{ maxWidth: 220 }}>
                  <Stack direction="row" spacing={0.5} useFlexGap sx={{ alignItems: "center", flexWrap: "wrap" }}>
                    <Typography variant="body2" sx={{ fontWeight: 600, lineHeight: 1.3 }} noWrap title={row.name}>{row.name}</Typography>
                    {row.role && <Chip size="small" variant="outlined" label={row.role} sx={{ height: 18, fontSize: 11 }} />}
                    {row.country && <Typography variant="caption" color="text.secondary">{row.country}</Typography>}
                  </Stack>
                  {row.detail && (
                    <Tooltip title={row.detail} placement="bottom-start">
                      <Typography variant="caption" color="text.secondary" noWrap sx={{ display: "block" }}>{row.detail}</Typography>
                    </Tooltip>
                  )}
                </TableCell>
                <TableCell sx={{ maxWidth: 210 }}>
                  {row.blocked
                    ? <Typography variant="caption" color="warning.main">{row.blocked}</Typography>
                    : <Typography variant="body2" noWrap title={row.email ?? ""} sx={{ fontSize: 13 }}>{row.email}</Typography>}
                </TableCell>
              </TableRow>
            </React.Fragment>
          );
        })}
      </TableBody>
    </Table>
  );

  const markers = (
    <Typography variant="caption" color="text.secondary">
      Markers filled in for each supplier: <b>[Supplier name]</b> · <b>[Company name]</b> · <b>[RFQ number]</b>
    </Typography>
  );

  return (
    <Dialog open={open} onClose={send.isPending ? undefined : onClose} maxWidth="lg" fullWidth
      slotProps={{ paper: { sx: { height: { md: "88vh" } } } }}>
      <DialogTitle sx={{ pb: 1 }}>
        <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ justifyContent: "space-between", alignItems: { sm: "center" } }}>
          <Box sx={{ minWidth: 0 }}>
            <Typography component="span" variant="h6" sx={{ fontWeight: 800, display: "block" }}>Find supplier</Typography>
            {line && (
              <Typography component="span" variant="body2" color="text.secondary" sx={{ display: "block" }} noWrap>
                <b>{line.description}</b>
                {line.partNumber ? ` · Part ${line.partNumber}` : ""}
                {(line.acceptedMakers?.length ?? 0) > 1
                  ? ` · Makers accepted: ${line.acceptedMakers!.map(makerLabel).join(", ")}`
                  : line.maker ? ` · ${line.maker}` : ""}
              </Typography>
            )}
          </Box>
          {sourcingCase && line && (
            <Stack direction="row" spacing={1} sx={{ alignItems: "center", flexShrink: 0 }}>
              <TextField
                label="Quantity to ask for"
                size="small"
                type="number"
                value={quantity}
                onChange={(event) => setQuantity(event.target.value)}
                error={!quantityOk}
                slotProps={{
                  htmlInput: { min: 0, step: "any" },
                  input: { endAdornment: <InputAdornment position="end">{line.unitOfMeasure ?? ""}</InputAdornment> },
                }}
                helperText={line.inStock > 0 ? `${line.inStock} in stock` : undefined}
                sx={{ width: 150 }}
              />
              <TextField
                label="Reply by"
                size="small"
                type="date"
                value={replyBy}
                onChange={(event) => setReplyBy(event.target.value)}
                slotProps={{ inputLabel: { shrink: true } }}
                sx={{ width: 160 }}
              />
            </Stack>
          )}
        </Stack>
      </DialogTitle>
      <DialogContent dividers sx={{ p: 0, display: "flex", flexDirection: "column" }}>
        {caseQuery.isLoading && (
          <Stack direction="row" spacing={1.5} sx={{ alignItems: "center", p: 3 }} role="status">
            <CircularProgress size={18} />
            <Typography variant="body2">Opening this line…</Typography>
          </Stack>
        )}
        {caseQuery.isError && (
          <Alert severity="error" sx={{ m: 2 }} action={<Button color="inherit" onClick={() => caseQuery.refetch()}>Try again</Button>}>
            {(caseQuery.error as { response?: { data?: { detail?: string } }; message?: string })?.response?.data?.detail
              || (caseQuery.error as Error)?.message || "This line could not be opened."}
          </Alert>
        )}

        {sourcingCase && (
          <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "5fr 7fr" }, flex: 1, minHeight: 0 }}>
            {/* ---- Who to ask ---- */}
            <Box sx={{ display: "flex", flexDirection: "column", minHeight: 0, borderRight: { md: 1 }, borderColor: "divider" }}>
              <Tabs value={tab} onChange={(_, value) => { tabTouched.current = true; setTab(value); }} variant="fullWidth" sx={{ borderBottom: 1, borderColor: "divider", minHeight: 42 }}>
                <Tab value="own" sx={{ minHeight: 42 }} label={
                  <Badge color="primary" badgeContent={ownTicked} invisible={ownTicked === 0}>
                    <Box sx={{ pr: ownTicked ? 1.5 : 0 }}>Your suppliers ({ownRows.length})</Box>
                  </Badge>} />
                <Tab value="internet" sx={{ minHeight: 42 }} label={
                  <Badge color="primary" badgeContent={internetTicked} invisible={internetTicked === 0}>
                    <Box sx={{ pr: internetTicked ? 1.5 : 0 }}>
                      Source from internet {internet.isLoading ? "…" : internet.isError ? "" : `(${internetRows.length}${moreInternet ? "+" : ""})`}
                    </Box>
                  </Badge>} />
              </Tabs>
              <Box sx={{ flex: 1, overflowY: "auto", minHeight: { xs: 240, md: 0 } }}>
                {tab === "own" && ownRows.length === 0 && (
                  <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
                    No In Network or Extended Network supplier is linked to this part yet. Source from internet, or type an address in To.
                  </Typography>
                )}
                {tab === "internet" && internet.isLoading && (
                  <Stack direction="row" spacing={1.5} sx={{ alignItems: "center", p: 2 }} role="status">
                    <CircularProgress size={16} />
                    <Typography variant="body2" color="text.secondary">Searching makers, distributors and resellers…</Typography>
                  </Stack>
                )}
                {tab === "internet" && firstInternetPage && firstInternetPage.status !== "Ready" && (
                  <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{firstInternetPage.message}</Typography>
                )}
                {tab === "internet" && internet.isError && (
                  <Stack spacing={1} sx={{ p: 2, alignItems: "flex-start" }}>
                    <Typography variant="body2" color="text.secondary">
                      The internet search did not answer this time. Your In Network and Extended Network suppliers, and typed emails, still work.
                    </Typography>
                    <Button size="small" variant="outlined" onClick={() => internet.refetch()}>Try again</Button>
                  </Stack>
                )}
                {rows.length > 0 && supplierTable}
                {tab === "internet" && moreInternet && (
                  <Button size="small" sx={{ m: 1 }} disabled={internet.isFetchingNextPage} onClick={() => internet.fetchNextPage()}>
                    {internet.isFetchingNextPage ? "Loading…" : "Show 10 more"}
                  </Button>
                )}
              </Box>
              {catalogueChoice && (
                <FormControlLabel
                  sx={{ px: 2, py: 0.5, borderTop: 1, borderColor: "divider", m: 0 }}
                  control={<Checkbox size="small" checked={addToCatalogue} onChange={(event) => setAddToCatalogue(event.target.checked)} />}
                  label={<Typography variant="body2">Add this part to my catalogue {addToCatalogue ? "" : <Typography component="span" variant="caption" color="text.secondary">(suppliers are still asked)</Typography>}</Typography>}
                />
              )}
            </Box>

            {/* ---- The email ---- */}
            <Box sx={{ display: "flex", flexDirection: "column", minHeight: 0, overflowY: "auto", p: 2, bgcolor: "background.default" }}>
              <Stack spacing={1}>
                {mailboxes.data && mailboxes.data.mailboxes.length > 0 ? (
                  <TextField select label="From" size="small" fullWidth value={sendFrom}
                    onChange={(event) => setSendFrom(Number(event.target.value))}>
                    {mailboxes.data.mailboxes.map((mailbox) => (
                      <MenuItem key={mailbox.mailboxId} value={mailbox.mailboxId}>
                        {mailbox.address}{mailbox.label && mailbox.label !== mailbox.address ? ` · ${mailbox.label}` : ""}
                      </MenuItem>
                    ))}
                  </TextField>
                ) : (
                  <Typography variant="caption" color="text.secondary">
                    From: {preview.data?.from ?? `${mailboxes.data?.companyName ?? "Your company"}`}
                  </Typography>
                )}

                <Box sx={{ border: 1, borderColor: badTo ? "error.main" : "divider", borderRadius: 1, px: 1.25, py: 0.75, bgcolor: "background.paper" }}>
                  <Stack direction="row" spacing={0.75} useFlexGap sx={{ alignItems: "center", flexWrap: "wrap" }}>
                    <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, minWidth: 22 }}>To</Typography>
                    {tickedRows.map((row) => (
                      <Tooltip key={row.key} title={row.email ?? ""}>
                        <Chip size="small" label={row.name} onDelete={() => toggle(row.key)} />
                      </Tooltip>
                    ))}
                    <TextField
                      variant="standard"
                      size="small"
                      value={extraTo}
                      onChange={(event) => setExtraTo(event.target.value)}
                      placeholder={tickedRows.length === 0 ? "Tick suppliers on the left, or type emails; separate with ;" : "Add more emails; separate with ;"}
                      slotProps={{ input: { disableUnderline: true }, htmlInput: { "aria-label": "Add supplier emails to To" } }}
                      sx={{ flex: 1, minWidth: 200 }}
                    />
                  </Stack>
                </Box>
                <Typography variant="caption" color={badTo ? "error.main" : "text.secondary"} sx={{ mt: -0.5 }}>
                  {badTo ? `"${badTo}" is not an email address.` : "Each supplier gets their own email. Typed addresses are added to your suppliers for this part."}
                </Typography>

                <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
                  <TextField size="small" fullWidth label="CC" placeholder="name@company.com; name2@company.com" value={cc}
                    onChange={(event) => { copiesTouched.current = true; setCc(event.target.value); }}
                    error={Boolean(badCopy && ccList.includes(badCopy))}
                    helperText={badCopy && ccList.includes(badCopy) ? `"${badCopy}" is not an email address` : undefined} />
                  <TextField size="small" fullWidth label="BCC" placeholder="Separate with ;" value={bcc}
                    onChange={(event) => { copiesTouched.current = true; setBcc(event.target.value); }}
                    error={Boolean(badCopy && bccList.includes(badCopy))}
                    helperText={badCopy && bccList.includes(badCopy) ? `"${badCopy}" is not an email address` : undefined} />
                </Stack>
              </Stack>

              <Divider sx={{ my: 1.5 }} />

              <Stack direction="row" sx={{ alignItems: "center", justifyContent: "space-between", mb: 1 }}>
                <Typography variant="overline" color="text.secondary">What each supplier receives</Typography>
                <Button size="small" startIcon={editing ? <Visibility /> : <Edit />} onClick={startEditing}>
                  {editing ? "Done editing" : "Edit email"}
                </Button>
              </Stack>

              <Box data-testid="supplier-email-preview"
                sx={{ p: 2, border: 1, borderColor: editing ? "primary.main" : "divider", borderRadius: 1, bgcolor: "background.paper" }}>
                {editing ? (
                  <Stack spacing={1.25}>
                    {markers}
                    <TextField size="small" fullWidth label="Subject" value={wording.subject}
                      onChange={(event) => setWording((current) => ({ ...current, subject: event.target.value }))}
                      slotProps={{ htmlInput: { maxLength: 200 } }} />
                    <TextField size="small" fullWidth label="Greeting" value={wording.greeting}
                      onChange={(event) => setWording((current) => ({ ...current, greeting: event.target.value }))}
                      slotProps={{ htmlInput: { maxLength: 200 } }} />
                    <TextField size="small" fullWidth multiline minRows={2} label="Opening" value={wording.opening}
                      onChange={(event) => setWording((current) => ({ ...current, opening: event.target.value }))}
                      slotProps={{ htmlInput: { maxLength: 1000 } }} />
                    <Box sx={{ p: 1.25, borderRadius: 1, bgcolor: "action.hover", position: "relative" }}>
                      <Stack direction="row" spacing={0.5} sx={{ alignItems: "center", mb: 0.5 }}>
                        <Lock sx={{ fontSize: 14 }} color="action" />
                        <Typography variant="caption" color="text.secondary">Part details, added by Nexora</Typography>
                      </Stack>
                      <Typography variant="body2" component="div" sx={{ whiteSpace: "pre-wrap", fontSize: 13 }}>{partBlock || "…"}</Typography>
                    </Box>
                    <TextField size="small" fullWidth multiline minRows={2} label="Your message" value={message}
                      onChange={(event) => { messageTouched.current = true; setSavedDefault(false); setMessage(event.target.value); }}
                      slotProps={{ htmlInput: { maxLength: 2000 } }} />
                    <Stack direction="row" spacing={1} sx={{ alignItems: "center", mt: -0.75 }}>
                      <Link component="button" type="button" variant="caption" underline="hover"
                        disabled={saveMyDefault.isPending || !message.trim()} onClick={() => saveMyDefault.mutate()}>
                        {saveMyDefault.isPending ? "Saving…" : "Save as my default message"}
                      </Link>
                      {savedDefault && <Typography variant="caption" color="success.main">Saved.</Typography>}
                      {saveMyDefault.isError && <Typography variant="caption" color="error.main">Could not save it just now.</Typography>}
                    </Stack>
                    <Typography variant="body2" color="text.secondary" sx={{ fontSize: 13 }}>
                      Please reply to this email with your price, availability, lead time and how long your price is valid.
                    </Typography>
                    <TextField size="small" fullWidth multiline minRows={3} label="Sign-off and signature" value={wording.signOff}
                      onChange={(event) => setWording((current) => ({ ...current, signOff: event.target.value }))}
                      slotProps={{ htmlInput: { maxLength: 1000 } }} />
                  </Stack>
                ) : preview.data ? (
                  <>
                    <Typography variant="body2" sx={{ fontWeight: 700, mb: 1.5 }}>{preview.data.subject}</Typography>
                    {hasPartBlock ? (
                      <>
                        <Typography variant="body2" component="div" sx={{ whiteSpace: "pre-wrap", fontSize: 13.5 }}>{bodyBefore}</Typography>
                        <Box sx={{ my: 1.5, p: 1.25, borderRadius: 1, bgcolor: "action.hover" }}>
                          <Stack direction="row" spacing={0.5} sx={{ alignItems: "center", mb: 0.5 }}>
                            <Lock sx={{ fontSize: 14 }} color="action" />
                            <Typography variant="caption" color="text.secondary">Part details</Typography>
                          </Stack>
                          <Typography variant="body2" component="div" sx={{ whiteSpace: "pre-wrap", fontSize: 13 }}>{partBlock}</Typography>
                        </Box>
                        <Typography variant="body2" component="div" sx={{ whiteSpace: "pre-wrap", fontSize: 13.5 }}>{bodyAfter}</Typography>
                      </>
                    ) : (
                      <Typography variant="body2" component="div" sx={{ whiteSpace: "pre-wrap", fontSize: 13.5 }}>{body}</Typography>
                    )}
                  </>
                ) : preview.isError ? (
                  <Typography variant="body2" color="text.secondary">The email preview could not be loaded; the email is still sent with the part details.</Typography>
                ) : (
                  <Typography variant="body2" color="text.secondary">Writing the email…</Typography>
                )}
              </Box>
              <Typography variant="caption" color="text.secondary" sx={{ display: "block", mt: 0.75 }}>
                {preview.data?.replyTo ? `Supplier replies go to ${preview.data.replyTo}. ` : ""}
                Your customer is not named, and their target prices and your margins are never included.
              </Typography>

              {failures.length > 0 && (
                <Alert severity="warning" sx={{ mt: 2 }}>
                  {failures.map((failure) => <Box key={failure}>{failure}</Box>)}
                </Alert>
              )}
            </Box>
          </Box>
        )}
      </DialogContent>
      <DialogActions sx={{ px: 2 }}>
        <Typography variant="caption" color="text.secondary" sx={{ mr: "auto" }}>
          {recipientCount > 0 ? `${recipientCount} supplier${recipientCount === 1 ? "" : "s"} will get their own email` : "Tick suppliers on the left or type emails in To"}
        </Typography>
        <Button onClick={onClose} disabled={send.isPending}>Cancel</Button>
        <Button
          variant="contained"
          startIcon={send.isPending ? <CircularProgress size={16} color="inherit" /> : <Send />}
          disabled={!sourcingCase || send.isPending || recipientCount === 0 || !quantityOk || Boolean(badCopy) || Boolean(badTo)}
          onClick={() => send.mutate()}
        >
          {sendLabel}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
