import React from "react";
import { isBelowCost, saleFromCost } from "../../../utils/margin";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
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
  InputAdornment,
  MenuItem,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from "@mui/material";
import { alpha } from "@mui/material/styles";
import { CheckCircle, EmojiEvents, Inventory2, LocalOffer, ReceiptLong, WarningAmber } from "@mui/icons-material";
import { useNavigate } from "react-router-dom";
import { useSnackbar } from "notistack";
import stockPriceService, { type PriceReference, type StockLinePrice } from "../../../api/services/stockPriceService";
import currencyService from "../../../api/services/currencyService";
import { useAuth } from "../../../context/AuthContext";
import { formatMoney } from "../../../utils/currency";
import { DAYS_PER, deliveryShortText, deliveryText, deliveryUnitFor, type DeliveryUnit } from "../../../utils/delivery";

/** The calendar day the record carries ("2026-08-28T00:00:00" is 28 Aug everywhere). */
const day = (value?: string | null) => {
  const match = value?.match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (!match) return "";
  return new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
    .toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
};
const round2 = (value: number) => Math.round(value * 100) / 100;
const qty = (value: number) => value.toLocaleString(undefined, { maximumFractionDigits: 2 });
const describeError = (error: unknown, fallback: string) =>
  (error as { response?: { data?: { detail?: string } } })?.response?.data?.detail || fallback;

export const stockPriceQueryKey = (rfqId: number, itemId: number, productId?: number | null) =>
  productId ? ["stock-price", rfqId, itemId, productId] : ["stock-price", rfqId, itemId];

const KIND_LABEL: Record<PriceReference["kind"], string> = { SOLD: "Sold", WON: "Won", QUOTED: "Quoted" };

/** A headline figure the rep can take with one click: last quoted, last won. */
function PriceCard({ title, icon, reference, empty, onUse, quoteCurrency }: {
  title: string;
  icon: React.ReactNode;
  reference?: PriceReference | null;
  empty: string;
  onUse: (price: number) => void;
  /** A past price in another currency is shown, never copied: nothing here converts. */
  quoteCurrency?: string | null;
}) {
  const foreign = Boolean(reference?.currencyCode && quoteCurrency && reference.currencyCode !== quoteCurrency);
  return (
    <Box sx={{ flex: 1, minWidth: 0, p: 1.5, borderRadius: 2, border: 1, borderColor: "divider", bgcolor: "background.paper" }}>
      <Stack direction="row" spacing={0.75} sx={{ alignItems: "center", color: "text.secondary" }}>
        {icon}
        <Typography variant="caption" sx={{ fontWeight: 700, textTransform: "uppercase", letterSpacing: 0.4 }}>{title}</Typography>
      </Stack>
      {reference ? (
        <>
          <Stack direction="row" sx={{ alignItems: "baseline", justifyContent: "space-between", mt: 0.5 }}>
            <Stack direction="row" spacing={0.75} sx={{ alignItems: "center" }}>
              <Typography variant="h6" sx={{ fontWeight: 800 }}>{formatMoney(reference.unitPrice, reference.currencyCode)}</Typography>
              {title === "Last quoted" && reference.kind === "WON" && <Chip size="small" color="success" label="Won" />}
            </Stack>
            {foreign ? (
              <Typography variant="caption" color="text.secondary">in {reference.currencyCode}</Typography>
            ) : (
              <Button size="small" onClick={() => onUse(reference.unitPrice)} aria-label={`Use ${title.toLowerCase()} price`}>Use</Button>
            )}
          </Stack>
          <Typography variant="caption" color="text.secondary" sx={{ display: "block" }} noWrap>
            {reference.customer ?? "Customer"} · qty {qty(reference.quantity)}
          </Typography>
          <Typography variant="caption" color="text.secondary" sx={{ display: "block" }} noWrap>
            {day(reference.on)} · {reference.reference}
          </Typography>
        </>
      ) : (
        <Typography variant="h6" color="text.secondary" sx={{ mt: 0.5, fontWeight: 700 }}>{empty}</Typography>
      )}
    </Box>
  );
}

export interface StockPriceDialogProps {
  open: boolean;
  rfqId: number;
  itemId: number;
  /** Price another accepted maker's product from stock instead of the line's own. */
  productId?: number | null;
  /** Ask a supplier whose price has expired to confirm it again (opens Find supplier). */
  onAskAgain?: (supplierIds: number[]) => void;
  onClose: () => void;
}

/**
 * Price a line that stock covers, in one small window: what is on the shelf, a ready price
 * (selling price, else cost + company margin), and what the part last sold and won at. Nothing
 * is held: the quote says "ex stock, subject to prior sale".
 */
export default function StockPriceDialog({ open, rfqId, itemId, productId, onAskAgain, onClose }: StockPriceDialogProps) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const { userData, hasPermission } = useAuth();
  const canSaveMargin = hasPermission("Quote Configuration", "edit");

  const viewQuery = useQuery({
    queryKey: stockPriceQueryKey(rfqId, itemId, productId),
    queryFn: () => stockPriceService.get(rfqId, itemId, productId),
    enabled: open,
  });
  const view = viewQuery.data;

  const [price, setPrice] = React.useState("");
  const [margin, setMargin] = React.useState("");
  const [exStock, setExStock] = React.useState(true);
  const [saveMargin, setSaveMargin] = React.useState(false);
  const [marginTouched, setMarginTouched] = React.useState(false);
  const [currencyId, setCurrencyId] = React.useState<number | "">("");
  // A line not covered by stock is priced from a supplier's price: which one, and its delivery time.
  const [chosenSupplier, setChosenSupplier] = React.useState<{ name: string; cost: number } | null>(null);
  const [leadValue, setLeadValue] = React.useState("");
  const [leadUnit, setLeadUnit] = React.useState<DeliveryUnit>("weeks");
  const [sendStockNow, setSendStockNow] = React.useState(true);
  const seeded = React.useRef<StockLinePrice | null>(null);

  const partial = !view?.coveredByStock ? view?.partial ?? null : null;
  const need0 = view?.requestedQuantity ?? 0;
  // Partly in stock: the shelf part at stock cost and the rest at the chosen supplier's price, blended.
  const blend = (supplierCost: number) => partial?.stockUnitCost != null && need0 > 0
    ? (partial.fromStock * partial.stockUnitCost + partial.toOrder * supplierCost) / need0
    : supplierCost;
  const cost = chosenSupplier ? blend(chosenSupplier.cost) : view?.price.unitCost ?? null;
  const marginFromPrice = (value: number) => (cost && cost > 0 ? round2((value / cost - 1) * 100) : null);

  React.useEffect(() => {
    if (!open) { seeded.current = null; return; }
    if (!view || seeded.current === view) return;
    seeded.current = view;
    // A draft line keeps the price already on it, and a sent line starts from the price the customer
    // was given: a rep who only changes the delivery time must not reprice the line without typing.
    // Today's suggestion is offered beside it with a Use button.
    const priced = view.onQuote && view.onQuote.unitPrice > 0 && view.onQuote.state === "DRAFT" ? view.onQuote : null;
    const sentBefore = view.onQuote && view.onQuote.unitPrice > 0 && view.onQuote.state !== "DRAFT" ? view.onQuote : null;
    // A cost with no margin is not a price: offering it would quote the part at no profit on one click.
    const suggested = view.price.source === "COST_ONLY" ? null : view.price.unitPrice;
    const start = priced?.unitPrice || sentBefore?.unitPrice || suggested || null;
    setPrice(start != null ? String(start) : "");
    // A cost with no company margin starts with an empty margin box, not a "0%" nobody chose.
    const derived = start != null && (priced || view.price.source !== "COST_ONLY") ? marginFromPrice(start) : null;
    setMargin(derived != null ? String(derived) : view.price.marginPercent != null ? String(view.price.marginPercent) : "");
    setMarginTouched(false);
    setExStock(priced ? priced.exStock : true);
    setSaveMargin(false);
    setCurrencyId(view.currency?.id ?? "");
    // Only a price in the quote's currency can be the cost; nothing here converts.
    const quoteCurrency = view.onQuote?.currencyCode ?? view.currency?.code ?? null;
    const best = (view.supplierPrices ?? [])
      .filter((x) => x.valid && Boolean(quoteCurrency) && x.currencyCode === quoteCurrency)
      .sort((a, b) => a.cost - b.cost)[0];
    setChosenSupplier(!priced && (view.price.source === "SUPPLIER_PLUS_MARGIN" || view.price.source === "BLENDED_PLUS_MARGIN") && best ? { name: best.supplierName, cost: best.cost } : null);
    setSendStockNow(priced ? priced.exStockQuantity != null && priced.exStockQuantity > 0 : true);
    setLead(priced?.leadTimeDays && priced.leadTimeDays > 0 ? priced.leadTimeDays : best?.leadTimeDays ?? sentBefore?.leadTimeDays ?? null);
  }, [open, view]);

  function setLead(days: number | null | undefined) {
    if (!days || days <= 0) { setLeadValue(""); setLeadUnit("weeks"); return; }
    const unit = deliveryUnitFor(days);
    setLeadValue(String(days / DAYS_PER[unit]));
    setLeadUnit(unit);
  }

  const quoteHasCurrency = Boolean(view?.onQuote?.currencyCode);
  const sent = view?.onQuote?.state === "SENT";
  const decided = view?.onQuote?.state === "DECIDED";
  const currenciesQuery = useQuery({
    queryKey: ["currencies-for-quote", userData?.businessUnitId],
    queryFn: async () => (await currencyService.getAll({ businessUnitId: userData?.businessUnitId, pageNumber: 1, pageSize: 100, isActive: true })).items ?? [],
    enabled: open && Boolean(view) && !quoteHasCurrency,
    staleTime: 10 * 60 * 1000,
  });
  const currencies: { id: number; code: string }[] = currenciesQuery.data ?? [];
  const currencyCode = view?.onQuote?.currencyCode
    ?? currencies.find((c) => c.id === currencyId)?.code
    ?? view?.currency?.code ?? null;

  const priceNumber = Number(price);
  const priceOk = price.trim() !== "" && Number.isFinite(priceNumber) && priceNumber > 0;
  const marginNumber = margin.trim() === "" ? null : Number(margin);
  const companyMargin = view?.price.marginPercent ?? null;

  const onPriceChange = (value: string) => {
    setPrice(value);
    const n = Number(value);
    if (value.trim() !== "" && Number.isFinite(n) && n > 0) {
      const derived = marginFromPrice(n);
      if (derived != null) setMargin(String(derived));
    }
  };
  const onMarginChange = (value: string) => {
    setMargin(value);
    setMarginTouched(true);
    const n = Number(value);
    if (cost && value.trim() !== "" && Number.isFinite(n)) setPrice(String(saleFromCost(cost, n)));
  };
  const usePrice = (value: number) => onPriceChange(String(round2(value)));

  const use = useMutation({
    mutationFn: async () => {
      if (saveMargin && marginNumber != null) await stockPriceService.saveMargin(marginNumber);
      return stockPriceService.use(rfqId, itemId, {
        unitPrice: priceNumber,
        exStock: covered && exStock,
        ...(!covered && leadDays ? { leadTimeDays: leadDays } : {}),
        ...(partial && sendStockNow ? { exStockQuantity: partial.fromStock } : {}),
        currencyId: quoteHasCurrency ? null : currencyId === "" ? null : currencyId,
        ...(sent ? { reviseIfSent: true } : {}),
        ...(productId ? { productId } : {}),
      });
    },
    onSuccess: (result) => {
      queryClient.invalidateQueries({ queryKey: stockPriceQueryKey(rfqId, itemId) });
      queryClient.invalidateQueries({ queryKey: ["rfq-detail", rfqId] });
      queryClient.invalidateQueries({ queryKey: ["rfq-commercial-intelligence", rfqId] });
      queryClient.invalidateQueries({ queryKey: ["stock-price"] });
      queryClient.invalidateQueries({ queryKey: ["other-makers-in-stock"] });
      // The RFQ header shows the latest quote; a price can create the draft or a new revision.
      queryClient.invalidateQueries({ queryKey: ["send-quote-id"] });
      enqueueSnackbar(sent ? `New revision ${result.quoteNo} made with this price. Send it when ready.` : `Price added to ${result.quoteNo}.`, {
        variant: "success",
        action: <Button color="inherit" size="small" onClick={() => navigate(`/sales/quotes/edit/${result.quoteId}`)}>Open quote</Button>,
      });
      onClose();
    },
    onError: (error) => enqueueSnackbar(describeError(error, "The price could not be added to the quote."), { variant: "error" }),
  });

  const stock = view?.stock;
  const need = view?.requestedQuantity ?? 0;
  const covered = view?.coveredByStock ?? false;
  const leadNumber = Number(leadValue);
  const leadOk = leadValue.trim() === "" || (Number.isInteger(leadNumber) && leadNumber > 0 && leadNumber * DAYS_PER[leadUnit] <= 730);
  const leadDays = leadValue.trim() !== "" && leadOk ? leadNumber * DAYS_PER[leadUnit] : null;
  const supplierPrices = view?.supplierPrices ?? [];
  const useSupplierPrice = (option: { supplierName: string; cost: number; leadTimeDays?: number | null }) => {
    setChosenSupplier({ name: option.supplierName, cost: option.cost });
    // The rep's own margin if they typed one here; otherwise the usual margin, never a figure
    // back-calculated from an old hand-typed price.
    const m = marginTouched && marginNumber != null && Number.isFinite(marginNumber) ? marginNumber : companyMargin ?? marginNumber ?? 0;
    setMargin(String(m));
    setPrice(String(saleFromCost(blend(option.cost), m)));
    if (option.leadTimeDays) setLead(option.leadTimeDays);
  };
  const unit = view?.unit ?? "";
  const lineTotal = priceOk ? priceNumber * need : null;
  const earlier = view?.history ?? [];
  const track = view?.trackRecord;

  // The ladder shows cost, sale price and quote price as rows; this line only speaks when one of
  // them cannot be worked out, and says what the rep can do about it.
  const hint = (() => {
    if (!view) return "";
    if (cost == null || cost <= 0) {
      if (supplierPrices.length > 0 && !supplierPrices.some((x) => x.valid)) {
        return "The supplier price has expired. Ask them again, or type a quote price.";
      }
      if (supplierPrices.some((x) => x.valid) && !chosenSupplier) {
        return `The supplier price is not in ${currencyCode ?? "the quote currency"}, so it cannot be the cost here. Type a quote price.`;
      }
      return "No cost on file. Type a quote price, or use a past price on the right.";
    }
    if (!priceOk) return "Type a margin on cost or a quote price.";
    return "";
  })();
  const sellingPrice = view?.price.sellingPrice != null && view.price.sellingPrice > 0 ? view.price.sellingPrice : null;
  const profitEach = cost != null && cost > 0 && priceOk ? round2(priceNumber - cost) : null;
  const sentPrice = view?.onQuote && view.onQuote.unitPrice > 0 && view.onQuote.state !== "DRAFT" ? view.onQuote : null;
  const todaysSuggestion = view && view.price.source !== "COST_ONLY" ? view.price.unitPrice : null;
  // One row of the ladder: a label on the left, a figure on the right.
  const ladderRow = (label: React.ReactNode, value: React.ReactNode, strong = false) => (
    <Stack direction="row" sx={{ justifyContent: "space-between", alignItems: "baseline", gap: 1 }}>
      <Typography variant="body2" color={strong ? "text.primary" : "text.secondary"} sx={{ fontWeight: strong ? 700 : 400 }}>{label}</Typography>
      <Typography variant="body2" className="tabular-nums" sx={{ fontWeight: strong ? 800 : 600, whiteSpace: "nowrap" }}>{value}</Typography>
    </Stack>
  );
  const sectionTitle = (text: string, note?: string) => (
    <Stack direction="row" spacing={1} sx={{ alignItems: "baseline" }}>
      <Typography variant="caption" sx={{ fontWeight: 800, textTransform: "uppercase", letterSpacing: 0.5 }}>{text}</Typography>
      {note && <Typography variant="caption" color="text.secondary">{note}</Typography>}
    </Stack>
  );

  return (
    <Dialog open={open} onClose={use.isPending ? undefined : onClose} maxWidth="md" fullWidth>
      <DialogTitle sx={{ pb: 1 }}>
        <Stack direction="row" spacing={1} sx={{ justifyContent: "space-between", alignItems: "center" }}>
          <Box sx={{ minWidth: 0 }}>
            <Typography component="span" variant="h6" sx={{ fontWeight: 800, display: "block" }}>{view && covered ? "Price from stock" : "Price this line"}</Typography>
            {view && (
              <Typography component="span" variant="body2" color="text.secondary" sx={{ display: "block" }} noWrap>
                <b>{view.description ?? "Item"}</b>
                {view.partNumber ? ` · Part ${view.partNumber}` : ""}{view.maker ? ` · ${view.maker}` : ""}
              </Typography>
            )}
          </Box>
          {view && (
            <Chip
              icon={covered ? <CheckCircle /> : <WarningAmber />}
              color={covered ? "success" : "warning"}
              variant="outlined"
              label={covered ? "Ex stock" : (stock?.free ?? 0) > 0 ? "Partly in stock" : "To order"}
            />
          )}
        </Stack>
      </DialogTitle>

      <DialogContent dividers sx={{ p: 0 }}>
        {viewQuery.isLoading && (
          <Stack direction="row" spacing={1.5} sx={{ alignItems: "center", p: 3 }} role="status">
            <CircularProgress size={18} />
            <Typography variant="body2">Checking stock and prices…</Typography>
          </Stack>
        )}
        {viewQuery.isError && (
          <Alert severity="error" sx={{ m: 2 }} action={<Button color="inherit" onClick={() => viewQuery.refetch()}>Try again</Button>}>
            {describeError(viewQuery.error, "Stock and prices could not be loaded.")}
          </Alert>
        )}

        {view && stock && (
          <>
            {view.onQuote && (sent || decided) && (
              <Alert severity={decided ? "warning" : "info"} sx={{ borderRadius: 0 }}>
                {decided
                  ? `The customer has decided on ${view.onQuote.quoteNo} at ${formatMoney(view.onQuote.unitPrice, view.onQuote.currencyCode)}. This price is final.`
                  : `Sent to the customer on ${view.onQuote.quoteNo} at ${formatMoney(view.onQuote.unitPrice, view.onQuote.currencyCode)}. Saving makes a new revision; the customer sees nothing until you send it.`}
              </Alert>
            )}
            {view.otherMaker && (
              <Alert severity="info" icon={<LocalOffer fontSize="small" />} sx={{ borderRadius: 0 }}>
                Offering <b>{view.otherMaker.label}</b>, one of the makers the customer accepts. The quote line will name it.
              </Alert>
            )}
            {/* ---- The shelf, in one strip ---- */}
            <Stack direction={{ xs: "column", sm: "row" }} spacing={0} sx={(theme) => ({
              px: 2.5, py: 1.5, gap: { xs: 1, sm: 4 }, alignItems: { sm: "center" },
              bgcolor: alpha(covered ? theme.palette.success.main : theme.palette.warning.main, 0.06),
              borderBottom: 1, borderColor: "divider",
            })}>
              <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}>
                <Inventory2 fontSize="small" color={covered ? "success" : "warning"} />
                <Typography variant="body2">
                  Customer wants <b>{qty(need)} {unit}</b>
                </Typography>
              </Stack>
              {stock.onHand <= 0 ? (
                <Typography variant="body2" color="text.secondary">Not in stock</Typography>
              ) : (
              <Typography variant="body2">
                On the shelf <b>{qty(stock.onHand)} {unit}</b>
                {stock.places.length > 0 && (
                  <Typography component="span" variant="body2" color="text.secondary">
                    {" "}· {stock.places.map((place) => `${place.warehouse} ${qty(place.onHand)}`).join(" · ")}
                  </Typography>
                )}
              </Typography>
              )}
              {stock.heldForOrders > 0 && (
                <Typography variant="body2" color="text.secondary">{qty(stock.heldForOrders)} held for orders · {qty(stock.free)} free</Typography>
              )}
              {!covered && stock.onHand > 0 && (
                <Typography variant="body2" color="warning.main" sx={{ fontWeight: 700 }}>Short by {qty(Math.max(0, need - stock.free))}</Typography>
              )}
            </Stack>

            <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "1fr 1fr" } }}>
              {/* ---- Cost, sale price, quote price: what it costs us, what we list it at, what we quote ---- */}
              <Stack spacing={1.5} sx={(theme) => ({ p: 2.5, [theme.breakpoints.up("md")]: { borderRight: `1px solid ${theme.palette.divider}` } })}>
                <Box>
                  {sectionTitle("Cost", "what it costs us")}
                  <Stack spacing={0.25} sx={{ mt: 0.5 }}>
                    {chosenSupplier && partial?.stockUnitCost != null ? (
                      <>
                        {ladderRow(`${qty(partial.fromStock)} from stock`, `${formatMoney(partial.stockUnitCost, currencyCode)} each`)}
                        {ladderRow(`${qty(partial.toOrder)} from ${chosenSupplier.name}`, `${formatMoney(chosenSupplier.cost, currencyCode)} each`)}
                      </>
                    ) : chosenSupplier ? (
                      ladderRow(`${chosenSupplier.name} price`, `${formatMoney(chosenSupplier.cost, currencyCode)} each`)
                    ) : null}
                    {ladderRow(`Cost per ${unit || "unit"}`, formatMoney(cost != null && cost > 0 ? cost : 0, currencyCode), true)}
                    {!chosenSupplier && view.costSource === "PRICE_SHEET" && (
                      <Typography variant="caption" color="text.secondary">Landed cost from the pricing sheet.</Typography>
                    )}
                    {!chosenSupplier && view.costSource === "STOCK_RECORD" && (
                      <Typography variant="caption" color="warning.main">
                        From the stock record, copied when the stock was counted. The pricing sheet has no landed cost for this part.
                      </Typography>
                    )}
                  </Stack>
                </Box>

                <Box>
                  {sectionTitle("Sale price", "our list price")}
                  <Stack spacing={0.25} sx={{ mt: 0.5 }}>
                    {ladderRow(`Sale price per ${unit || "unit"}`, formatMoney(sellingPrice ?? 0, currencyCode), true)}
                    {sellingPrice == null && view.sheet && !view.sheet.usable && (view.sheet.salePrice ?? view.sheet.landedCost) != null ? (
                      <Typography variant="caption" color="warning.main">
                        The pricing sheet has this part in {view.sheet.currencyCode}
                        {view.sheet.salePrice != null ? ` (sale price ${formatMoney(view.sheet.salePrice, view.sheet.currencyCode)})` : ""}, not {currencyCode}. Nothing is converted.
                      </Typography>
                    ) : sellingPrice == null ? (
                      <Typography variant="caption" color="text.secondary">
                        Set by a manager on the{" "}
                        <Box component="a" href="/inventory/pricing-sheet" target="_blank" rel="noopener" sx={{ color: "primary.main", fontWeight: 700 }}>
                          pricing sheet
                        </Box>.
                      </Typography>
                    ) : (
                      <Typography variant="caption" color="text.secondary">From the pricing sheet.</Typography>
                    )}
                  </Stack>
                </Box>

                <Box>
                  {sectionTitle("Quote price", "to this customer")}
                  <Stack direction="row" spacing={1} sx={{ mt: 1 }}>
                    <TextField
                      label={`Quote price${unit ? ` per ${unit}` : ""}`}
                      value={price}
                      onChange={(event) => onPriceChange(event.target.value)}
                      type="number"
                      error={price.trim() !== "" && !priceOk}
                      slotProps={{
                        htmlInput: { min: 0, step: "any", "aria-label": "Quote price" },
                        input: { sx: { fontSize: 22, fontWeight: 800 } },
                      }}
                      sx={{ flex: 1 }}
                    />
                    {quoteHasCurrency ? (
                      <TextField label="Currency" value={view.onQuote?.currencyCode ?? ""} disabled sx={{ width: 104 }} />
                    ) : (
                      <TextField
                        select
                        label="Currency"
                        value={currencyId}
                        onChange={(event) => setCurrencyId(event.target.value === "" ? "" : Number(event.target.value))}
                        sx={{ width: 104 }}
                      >
                        {view.currency && !currencies.some((c) => c.id === view.currency!.id) && (
                          <MenuItem value={view.currency.id}>{view.currency.code}</MenuItem>
                        )}
                        {currencies.map((c) => <MenuItem key={c.id} value={c.id}>{c.code}</MenuItem>)}
                      </TextField>
                    )}
                  </Stack>
                  {/* UX-11: said where the price is typed, not only in the total further down. */}
                  {priceOk && isBelowCost(cost, priceNumber) && (
                    <Typography variant="caption" color="error.main" sx={{ display: "block", mt: 0.5, fontWeight: 800 }}>
                      Below cost ({formatMoney(cost, currencyCode)} per {unit || "unit"})
                    </Typography>
                  )}
                  {hint && <Typography variant="caption" color="text.secondary" sx={{ display: "block", mt: 0.5 }}>{hint}</Typography>}
                  {sentPrice && todaysSuggestion != null && priceOk && todaysSuggestion !== priceNumber && (
                    <Stack direction="row" spacing={1} sx={{ alignItems: "center", mt: 0.5 }}>
                      <Typography variant="caption" color="text.secondary">
                        Today's suggestion {formatMoney(todaysSuggestion, currencyCode)}
                      </Typography>
                      <Button size="small" onClick={() => usePrice(todaysSuggestion)} aria-label="Use today's suggestion">Use</Button>
                    </Stack>
                  )}
                </Box>

                {cost != null && cost > 0 && (
                  <Stack direction="row" spacing={1.5} sx={{ alignItems: "center" }}>
                    <TextField
                      label="Margin on cost"
                      size="small"
                      type="number"
                      value={margin}
                      onChange={(event) => onMarginChange(event.target.value)}
                      slotProps={{
                        htmlInput: { step: "any", "aria-label": "Margin on cost percent" },
                        input: { endAdornment: <InputAdornment position="end">%</InputAdornment> },
                      }}
                      sx={{ width: 150 }}
                    />
                    {companyMargin != null && (
                      <Typography variant="caption" color="text.secondary">Usual {companyMargin}%</Typography>
                    )}
                  </Stack>
                )}
                {canSaveMargin && marginTouched && cost != null && cost > 0 && marginNumber != null && Number.isFinite(marginNumber)
                  && marginNumber >= 0 && marginNumber !== companyMargin && (
                  <FormControlLabel
                    sx={{ alignItems: "flex-start", "& .MuiCheckbox-root": { pt: 0.25 } }}
                    control={<Checkbox size="small" checked={saveMargin} onChange={(event) => setSaveMargin(event.target.checked)} />}
                    label={
                      <Box>
                        <Typography variant="body2">Use {marginNumber}% {covered ? "for all stock items" : "as your usual margin"} from now on</Typography>
                        <Typography variant="caption" color="text.secondary">
                          Items without a sale price will start at cost + {marginNumber}%
                        </Typography>
                      </Box>
                    }
                  />
                )}

                <Box sx={{ p: 1.25, borderRadius: 1.5, bgcolor: "action.hover" }}>
                  <Stack direction="row" sx={{ justifyContent: "space-between", alignItems: "baseline" }}>
                    <Typography variant="body2" color="text.secondary">{qty(need)} {unit} × {priceOk ? formatMoney(priceNumber, currencyCode) : "—"}</Typography>
                    <Typography variant="subtitle1" className="tabular-nums" sx={{ fontWeight: 800 }}>{formatMoney(lineTotal, currencyCode)}</Typography>
                  </Stack>
                  {profitEach != null && (
                    <Typography variant="body2" className="tabular-nums" sx={{ mt: 0.5, fontWeight: 700, color: profitEach < 0 ? "error.main" : "success.main" }}>
                      {profitEach < 0
                        ? `Below cost: a loss of ${formatMoney(round2(-profitEach * need), currencyCode)} on this line`
                        : `Profit ${formatMoney(profitEach, currencyCode)} per ${unit || "unit"} · ${formatMoney(round2(profitEach * need), currencyCode)} on this line`}
                    </Typography>
                  )}
                </Box>

                {!covered ? (
                  <Box>
                    {partial && (
                      <FormControlLabel
                        sx={{ mb: 0.75 }}
                        control={<Checkbox size="small" checked={sendStockNow} onChange={(event) => setSendStockNow(event.target.checked)}
                          slotProps={{ input: { "aria-label": "Send the stock part straight away" } }} />}
                        label={<Typography variant="body2">Send the {qty(partial.fromStock)} in stock straight away</Typography>}
                      />
                    )}
                    <Stack direction="row" spacing={1} sx={{ alignItems: "flex-start" }}>
                      <TextField
                        label={partial && sendStockNow ? "Balance in" : "Delivery time"}
                        size="small"
                        type="number"
                        value={leadValue}
                        onChange={(event) => setLeadValue(event.target.value)}
                        error={!leadOk}
                        helperText={!leadOk ? "Whole number, up to 2 years (24 months)" : undefined}
                        slotProps={{ htmlInput: { min: 1, step: 1, "aria-label": "Delivery time" } }}
                        sx={{ width: 130 }}
                      />
                      <TextField select size="small" value={leadUnit} onChange={(event) => setLeadUnit(event.target.value as DeliveryUnit)}
                        slotProps={{ htmlInput: { "aria-label": "Delivery time unit" } }} sx={{ width: 110 }}>
                        <MenuItem value="days">days</MenuItem>
                        <MenuItem value="weeks">weeks</MenuItem>
                        <MenuItem value="months">months</MenuItem>
                      </TextField>
                    </Stack>
                    <Typography variant="caption" color="text.secondary">
                      {(() => {
                        const when = leadDays ? deliveryText(leadDays) : null;
                        if (partial && sendStockNow) return `Quote prints "Delivery: ${qty(partial.fromStock)} ex stock, balance ${when ? `in ${when}` : "to follow"}"`;
                        return when ? `Quote prints "Delivery: ${when}"` : "Leave empty to print no delivery time";
                      })()}
                    </Typography>
                  </Box>
                ) : (
                <Tooltip title="Nothing is held now. Stock is held for the customer once they send a purchase order." placement="bottom-start">
                  <FormControlLabel
                    control={<Checkbox checked={exStock} onChange={(event) => setExStock(event.target.checked)} slotProps={{ input: { "aria-label": "Offer ex stock" } }} />}
                    label={
                      <Box>
                        <Typography variant="body2" sx={{ fontWeight: 700 }}>Offer ex stock</Typography>
                        <Typography variant="caption" color="text.secondary">Quote prints "Ex stock, subject to prior sale"</Typography>
                      </Box>
                    }
                  />
                </Tooltip>
                )}
              </Stack>

              {/* ---- Supplier prices, then your record on this part ---- */}
              <Stack spacing={1.5} sx={{ p: 2.5, bgcolor: (theme) => alpha(theme.palette.text.primary, 0.02) }}>
                {supplierPrices.length > 0 && (
                  <Box>
                    <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 0.5 }}>Supplier prices</Typography>
                    <Stack spacing={0.75}>
                      {supplierPrices.map((option) => {
                        const chosen = chosenSupplier?.name === option.supplierName && chosenSupplier.cost === option.cost;
                        const otherCurrency = option.currencyCode && currencyCode && option.currencyCode !== currencyCode;
                        return (
                          <Stack key={option.id} direction="row" spacing={1} sx={(theme) => ({
                            alignItems: "center", p: 1, borderRadius: 1.5, border: 1,
                            borderColor: chosen ? theme.palette.primary.main : theme.palette.divider,
                            bgcolor: chosen ? alpha(theme.palette.primary.main, 0.06) : "background.paper",
                            opacity: option.valid ? 1 : 0.6,
                          })}>
                            <Box sx={{ flex: 1, minWidth: 0 }}>
                              <Typography variant="body2" sx={{ fontWeight: 700 }} noWrap>{option.supplierName}</Typography>
                              <Typography variant="caption" color="text.secondary" sx={{ display: "block" }} noWrap>
                                {option.leadTimeDays ? `Delivery ${deliveryShortText(option.leadTimeDays)} · ` : ""}
                                {option.valid ? (option.validUntil ? `valid to ${day(option.validUntil)}` : "no expiry given") : `expired ${day(option.validUntil)}`}
                                {option.forThisRequest ? "" : " · earlier request"}
                              </Typography>
                            </Box>
                            <Box sx={{ textAlign: "right" }}>
                              <Typography variant="body2" sx={{ fontWeight: 800 }}>{formatMoney(option.cost, option.currencyCode)}</Typography>
                              {otherCurrency && <Typography variant="caption" color="warning.main">not in {currencyCode}</Typography>}
                            </Box>
                            {option.valid ? (
                              <Button size="small" variant={chosen ? "contained" : "text"} onClick={() => useSupplierPrice(option)}
                                aria-label={`Use ${option.supplierName} price`}>
                                {chosen ? "Using" : "Use"}
                              </Button>
                            ) : onAskAgain ? (
                              <Button size="small" variant="outlined" onClick={() => { onClose(); onAskAgain([option.supplierId]); }}
                                aria-label={`Ask ${option.supplierName} again`}>
                                Ask again
                              </Button>
                            ) : (
                              <Chip size="small" label="Expired" />
                            )}
                          </Stack>
                        );
                      })}
                    </Stack>
                  </Box>
                )}
                <Stack direction="row" sx={{ justifyContent: "space-between", alignItems: "baseline" }}>
                  <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>Your record on this part</Typography>
                  {track && track.timesQuoted > 0 && (
                    <Typography variant="caption" color="text.secondary">
                      Quoted {track.timesQuoted} {track.timesQuoted === 1 ? "time" : "times"} · won {track.timesWon}
                    </Typography>
                  )}
                </Stack>
                <Stack direction="row" spacing={1.25}>
                  <PriceCard title="Last quoted" icon={<ReceiptLong sx={{ fontSize: 16 }} />} reference={track?.lastQuoted} empty="Never quoted" onUse={usePrice} />
                  <PriceCard title="Last won" icon={<EmojiEvents sx={{ fontSize: 16 }} />} reference={track?.lastWon} empty="Never won" onUse={usePrice} />
                </Stack>
                {earlier.length > 0 && (
                  <Box>
                    <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>Other recent prices</Typography>
                    <Stack spacing={0.25} sx={{ mt: 0.5 }}>
                      {earlier.map((row) => (
                        <Stack key={`${row.kind}-${row.reference}-${row.unitPrice}`} direction="row" spacing={1}
                          sx={{ alignItems: "center", py: 0.5, borderBottom: 1, borderColor: "divider" }}>
                          <Chip size="small" icon={<LocalOffer sx={{ fontSize: 14 }} />} label={KIND_LABEL[row.kind]} variant="outlined" sx={{ width: 84 }} />
                          <Typography variant="body2" sx={{ fontWeight: 700, minWidth: 90 }}>{formatMoney(row.unitPrice, row.currencyCode)}</Typography>
                          <Typography variant="caption" color="text.secondary" noWrap sx={{ flex: 1, minWidth: 0 }}>
                            {row.customer ?? "Customer"} · {day(row.on)}
                          </Typography>
                          <Button size="small" onClick={() => usePrice(row.unitPrice)}>Use</Button>
                        </Stack>
                      ))}
                    </Stack>
                  </Box>
                )}
                {!track?.lastQuoted && !track?.lastWon && earlier.length === 0 && (
                  <Typography variant="body2" color="text.secondary">This part has not been quoted or sold before.</Typography>
                )}
              </Stack>
            </Box>
          </>
        )}
      </DialogContent>

      <DialogActions sx={{ px: 2.5 }}>
        <Typography variant="caption" color="text.secondary" sx={{ flex: 1 }}>
          {decided ? "" : sent ? "The sent quote stays as it is." : view?.onQuote ? `Updates this line on ${view.onQuote.quoteNo}. You can still change it on the quote.` : "Starts the quote draft with this line priced. You can still change it on the quote."}
        </Typography>
        <Button onClick={onClose} disabled={use.isPending}>Cancel</Button>
        <Button
          variant="contained"
          onClick={() => use.mutate()}
          disabled={!view || decided || !priceOk || !leadOk || use.isPending || (!quoteHasCurrency && currencyId === "")}
          startIcon={use.isPending ? <CircularProgress size={16} color="inherit" /> : <CheckCircle />}
        >
          {sent ? "Save as new revision" : "Save quote price"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * The RFQ line's next-action cell when stock covers it: one sentence, the two prices a rep
 * checks first, and one button. After pricing it says where the price went.
 */
export function StockLineAction({ rfqId, itemId, canPrice }: { rfqId: number; itemId: number; canPrice: boolean }) {
  const navigate = useNavigate();
  const [open, setOpen] = React.useState(false);
  const query = useQuery({ queryKey: stockPriceQueryKey(rfqId, itemId), queryFn: () => stockPriceService.get(rfqId, itemId) });
  const view = query.data;

  if (query.isLoading) return <Typography variant="caption" color="text.secondary">Checking stock…</Typography>;
  if (!view) {
    return <Chip size="small" icon={<Inventory2 />} color="success" variant="outlined" label="In stock" />;
  }

  const unit = view.unit ? ` ${view.unit}` : "";
  const priced = view.onQuote && view.onQuote.unitPrice > 0 ? view.onQuote : null;
  const hints = recordHint(view);

  return (
    <Stack spacing={0.5} sx={{ alignItems: "flex-start" }}>
      {priced ? (
        <>
          <Typography variant="caption" sx={{ fontWeight: 700, color: "success.main" }}>
            {priced.state === "DRAFT" ? "Priced" : "Quoted"} {formatMoney(priced.unitPrice, priced.currencyCode)} on {priced.quoteNo}
            {priced.state === "SENT" ? " · sent" : priced.state === "DECIDED" ? " · customer decided" : ""}{priced.exStock ? " · ex stock" : ""}
          </Typography>
          <Typography variant="caption" color="text.secondary">{qty(view.stock.onHand)}{unit} on the shelf</Typography>
        </>
      ) : (
        <>
          <Typography variant="caption" sx={{ fontWeight: 700, color: "success.main" }}>
            All {qty(view.requestedQuantity)} in stock · {qty(view.stock.onHand)}{unit} on the shelf
          </Typography>
          {hints && <Typography variant="caption" color="text.secondary">{hints}</Typography>}
        </>
      )}
      <Stack direction="row" spacing={0.75} useFlexGap sx={{ flexWrap: "wrap" }}>
        {canPrice && priced?.state !== "DECIDED" && (
          <Button size="small" variant={priced ? "outlined" : "contained"} startIcon={<LocalOffer />} onClick={() => setOpen(true)}>
            {priced ? "Change price" : "Price from stock"}
          </Button>
        )}
        {priced && (
          <Button size="small" variant="text" onClick={() => navigate(priced.state === "DRAFT" ? `/sales/quotes/edit/${priced.quoteId}` : `/sales/quotes/view/${priced.quoteId}`)}>Open quote</Button>
        )}
      </Stack>
      {open && <StockPriceDialog open={open} rfqId={rfqId} itemId={itemId} onClose={() => setOpen(false)} />}
    </Stack>
  );
}

/**
 * For a line that needs sourcing: stock of another maker the customer accepts, with one button to
 * price it from stock. Shows nothing when there is none.
 */
export function OtherMakerStockAction({ rfqId, itemId, canPrice }: { rfqId: number; itemId: number; canPrice: boolean }) {
  const [openFor, setOpenFor] = React.useState<number | null>(null);
  const query = useQuery({
    queryKey: ["other-makers-in-stock", rfqId, itemId],
    queryFn: () => stockPriceService.otherMakers(rfqId, itemId),
    staleTime: 60_000,
  });
  const options = query.data ?? [];
  const lineQuery = useQuery({
    queryKey: stockPriceQueryKey(rfqId, itemId),
    queryFn: () => stockPriceService.get(rfqId, itemId),
    enabled: options.length > 0,
  });
  if (options.length === 0) return null;
  const priced = lineQuery.data?.onQuote && lineQuery.data.onQuote.unitPrice > 0 ? lineQuery.data.onQuote : null;

  return (
    <Stack spacing={0.5} sx={{ alignItems: "flex-start" }}>
      {priced && (
        <Typography variant="caption" sx={{ fontWeight: 700, color: "success.main" }}>
          {priced.state === "DRAFT" ? "Priced" : "Quoted"} {formatMoney(priced.unitPrice, priced.currencyCode)} on {priced.quoteNo}{priced.exStock ? " · ex stock" : ""}
        </Typography>
      )}
      {options.slice(0, 2).map((option) => (
        <Stack key={option.productId} spacing={0.25} sx={{ alignItems: "flex-start" }}>
          <Typography variant="caption" sx={{ fontWeight: 700, color: "success.main" }}>
            {option.label} in stock · {qty(option.free)}
          </Typography>
          {canPrice && (
            <Button size="small" variant={priced ? "outlined" : "contained"} startIcon={<LocalOffer />} onClick={() => setOpenFor(option.productId)}
              aria-label={priced ? `Change price · ${option.label}` : `Price from stock · ${option.label}`}>
              {priced ? "Change price" : "Price from stock"}
            </Button>
          )}
        </Stack>
      ))}
      {openFor != null && (
        <StockPriceDialog open rfqId={rfqId} itemId={itemId} productId={openFor} onClose={() => setOpenFor(null)} />
      )}
    </Stack>
  );
}

/** "Last quoted SAR 130.00 · Never won", from the company's record on the part. */
const recordHint = (view: StockLinePrice) => {
  const track = view.trackRecord;
  if (!track.lastQuoted && !track.lastWon) return "Never quoted before";
  return [
    track.lastQuoted ? `Last quoted ${formatMoney(track.lastQuoted.unitPrice, track.lastQuoted.currencyCode)}` : null,
    track.lastWon ? `Last won ${formatMoney(track.lastWon.unitPrice, track.lastWon.currencyCode)}` : "Never won",
  ].filter(Boolean).join(" · ");
};

const deliveryShort = (days?: number | null, fromStock?: number | null) => {
  const when = !days ? "" : deliveryText(days);
  if (fromStock && fromStock > 0) return ` · ${qty(fromStock)} ex stock, balance ${when || "to follow"}`;
  return when ? ` · ${when}` : "";
};

/**
 * For a known line that stock does not cover: where its price stands on the quote, the company's
 * record on the part, and "Price it" (from a supplier's price or by hand, with a delivery time).
 */
export function LinePriceAction({ rfqId, itemId, canPrice, primary, onAskAgain }: {
  rfqId: number; itemId: number; canPrice: boolean; primary: boolean; onAskAgain?: (supplierIds: number[]) => void;
}) {
  const [open, setOpen] = React.useState(false);
  const query = useQuery({ queryKey: stockPriceQueryKey(rfqId, itemId), queryFn: () => stockPriceService.get(rfqId, itemId) });
  // Same cached read as OtherMakerStockAction: when another accepted maker is on the shelf, that is
  // the line's main action and pricing the named part becomes the quieter alternative.
  const otherMakers = useQuery({
    queryKey: ["other-makers-in-stock", rfqId, itemId],
    queryFn: () => stockPriceService.otherMakers(rfqId, itemId),
    staleTime: 60_000,
  });
  const otherInStock = (otherMakers.data ?? []).length > 0;
  const view = query.data;
  if (!view || view.coveredByStock) return null;
  const priced = view.onQuote && view.onQuote.unitPrice > 0 ? view.onQuote : null;
  // A price from an earlier request that has run out (this request's own expired prices are
  // already called out on the line): say so, and offer to ask that supplier again.
  const prices = view.supplierPrices ?? [];
  const staleElsewhere = !priced && !prices.some((x) => x.valid) ? prices.find((x) => !x.valid && !x.forThisRequest) : undefined;

  return (
    <Stack spacing={0.5} sx={{ alignItems: "flex-start" }}>
      {staleElsewhere && (
        <Typography variant="caption" color="warning.main" sx={{ fontWeight: 700 }}>
          {staleElsewhere.supplierName} price expired {day(staleElsewhere.validUntil)}
        </Typography>
      )}
      {priced && !otherInStock ? (
        <Typography variant="caption" sx={{ fontWeight: 700, color: "success.main" }}>
          {priced.state === "DRAFT" ? "Priced" : "Quoted"} {formatMoney(priced.unitPrice, priced.currencyCode)} on {priced.quoteNo}
          {priced.state === "SENT" ? " · sent" : priced.state === "DECIDED" ? " · customer decided" : ""}{deliveryShort(priced.leadTimeDays, priced.exStockQuantity)}
        </Typography>
      ) : priced || otherInStock ? null : (
        <Typography variant="caption" color="text.secondary">{recordHint(view)}</Typography>
      )}
      <Stack direction="row" spacing={0.75} useFlexGap sx={{ flexWrap: "wrap" }}>
        {staleElsewhere && onAskAgain && (
          <Button size="small" variant="outlined" onClick={() => onAskAgain([staleElsewhere.supplierId])}>Ask again</Button>
        )}
        {canPrice && priced?.state !== "DECIDED" && (
          otherInStock ? (
            <Button size="small" variant="text" onClick={() => setOpen(true)}>Price the named part instead</Button>
          ) : (
            <Button size="small" variant={primary && !priced ? "contained" : "outlined"} startIcon={<LocalOffer />} onClick={() => setOpen(true)}>
              {priced ? "Change price" : "Price it"}
            </Button>
          )
        )}
      </Stack>
      {open && <StockPriceDialog open rfqId={rfqId} itemId={itemId} onAskAgain={onAskAgain} onClose={() => setOpen(false)} />}
    </Stack>
  );
}
