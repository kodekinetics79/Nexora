import React from "react";
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

export const stockPriceQueryKey = (rfqId: number, itemId: number) => ["stock-price", rfqId, itemId];

const KIND_LABEL: Record<PriceReference["kind"], string> = { SOLD: "Sold", WON: "Won", QUOTED: "Quoted" };

/** A headline figure the rep can take with one click: last sold, last won. */
function PriceCard({ title, icon, reference, onUse }: {
  title: string;
  icon: React.ReactNode;
  reference?: PriceReference | null;
  onUse: (price: number) => void;
}) {
  return (
    <Box sx={{ flex: 1, minWidth: 0, p: 1.5, borderRadius: 2, border: 1, borderColor: "divider", bgcolor: "background.paper" }}>
      <Stack direction="row" spacing={0.75} sx={{ alignItems: "center", color: "text.secondary" }}>
        {icon}
        <Typography variant="caption" sx={{ fontWeight: 700, textTransform: "uppercase", letterSpacing: 0.4 }}>{title}</Typography>
      </Stack>
      {reference ? (
        <>
          <Stack direction="row" sx={{ alignItems: "baseline", justifyContent: "space-between", mt: 0.5 }}>
            <Typography variant="h6" sx={{ fontWeight: 800 }}>{formatMoney(reference.unitPrice, reference.currencyCode)}</Typography>
            <Button size="small" onClick={() => onUse(reference.unitPrice)} aria-label={`Use ${title.toLowerCase()} price`}>Use</Button>
          </Stack>
          <Typography variant="caption" color="text.secondary" sx={{ display: "block" }} noWrap>
            {reference.customer ?? "Customer"} · qty {qty(reference.quantity)}
          </Typography>
          <Typography variant="caption" color="text.secondary" sx={{ display: "block" }} noWrap>
            {day(reference.on)} · {reference.reference}
          </Typography>
        </>
      ) : (
        <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>Nothing yet</Typography>
      )}
    </Box>
  );
}

export interface StockPriceDialogProps {
  open: boolean;
  rfqId: number;
  itemId: number;
  onClose: () => void;
}

/**
 * Price a line that stock covers, in one small window: what is on the shelf, a ready price
 * (selling price, else cost + company margin), and what the part last sold and won at. Nothing
 * is held: the quote says "ex stock, subject to prior sale".
 */
export default function StockPriceDialog({ open, rfqId, itemId, onClose }: StockPriceDialogProps) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const { userData, hasPermission } = useAuth();
  const canSaveMargin = hasPermission("Quote Configuration", "edit");

  const viewQuery = useQuery({
    queryKey: stockPriceQueryKey(rfqId, itemId),
    queryFn: () => stockPriceService.get(rfqId, itemId),
    enabled: open,
  });
  const view = viewQuery.data;

  const [price, setPrice] = React.useState("");
  const [margin, setMargin] = React.useState("");
  const [exStock, setExStock] = React.useState(true);
  const [saveMargin, setSaveMargin] = React.useState(false);
  const [marginTouched, setMarginTouched] = React.useState(false);
  const [currencyId, setCurrencyId] = React.useState<number | "">("");
  const seeded = React.useRef<StockLinePrice | null>(null);

  const cost = view?.price.unitCost ?? null;
  const marginFromPrice = (value: number) => (cost && cost > 0 ? round2((value / cost - 1) * 100) : null);

  React.useEffect(() => {
    if (!open) { seeded.current = null; return; }
    if (!view || seeded.current === view) return;
    seeded.current = view;
    const priced = view.onQuote && view.onQuote.unitPrice > 0 ? view.onQuote : null;
    const start = priced?.unitPrice || view.price.unitPrice || null;
    setPrice(start != null ? String(start) : "");
    // A cost with no company margin starts with an empty margin box, not a "0%" nobody chose.
    const derived = start != null && (priced || view.price.source !== "COST_ONLY") ? marginFromPrice(start) : null;
    setMargin(derived != null ? String(derived) : view.price.marginPercent != null ? String(view.price.marginPercent) : "");
    setMarginTouched(false);
    setExStock(priced ? priced.exStock : true);
    setSaveMargin(false);
    setCurrencyId(view.currency?.id ?? "");
  }, [open, view]);

  const quoteHasCurrency = Boolean(view?.onQuote?.currencyCode);
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
    if (cost && value.trim() !== "" && Number.isFinite(n)) setPrice(String(round2(cost * (1 + n / 100))));
  };
  const usePrice = (value: number) => onPriceChange(String(round2(value)));

  const use = useMutation({
    mutationFn: async () => {
      if (saveMargin && marginNumber != null) await stockPriceService.saveMargin(marginNumber);
      return stockPriceService.use(rfqId, itemId, {
        unitPrice: priceNumber,
        exStock,
        currencyId: quoteHasCurrency ? null : currencyId === "" ? null : currencyId,
      });
    },
    onSuccess: (result) => {
      queryClient.invalidateQueries({ queryKey: stockPriceQueryKey(rfqId, itemId) });
      queryClient.invalidateQueries({ queryKey: ["rfq-detail", rfqId] });
      queryClient.invalidateQueries({ queryKey: ["rfq-commercial-intelligence", rfqId] });
      enqueueSnackbar(`Price added to ${result.quoteNo}.`, {
        variant: "success",
        action: <Button color="inherit" size="small" onClick={() => navigate(`/sales/quotes/edit/${result.quoteId}`)}>Open quote</Button>,
      });
      onClose();
    },
    onError: (error) => enqueueSnackbar(describeError(error, "The price could not be added to the quote."), { variant: "error" }),
  });

  const stock = view?.stock;
  const need = view?.requestedQuantity ?? 0;
  const covered = stock ? stock.free >= need : false;
  const unit = view?.unit ?? "";
  const lineTotal = priceOk ? priceNumber * need : null;
  const same = (a?: PriceReference | null, b?: PriceReference | null) => Boolean(a && b && a.kind === b.kind && a.reference === b.reference);
  const earlier = (view?.history ?? []).filter((row) => !same(row, view?.lastSold) && !same(row, view?.lastWon));

  const suggestion = (() => {
    if (!view) return "";
    const p = view.price;
    if (p.source === "SELLING_PRICE") return `Suggested from your selling price ${formatMoney(p.sellingPrice, currencyCode)}`;
    if (p.source === "COST_PLUS_MARGIN") return `Suggested: cost ${formatMoney(p.unitCost, currencyCode)} + ${p.marginPercent}% company margin`;
    if (p.source === "COST_ONLY") return `Cost ${formatMoney(p.unitCost, currencyCode)}. Set a margin to build the price.`;
    return "No selling price or cost on file. Type a price, or use one from history.";
  })();

  return (
    <Dialog open={open} onClose={use.isPending ? undefined : onClose} maxWidth="md" fullWidth>
      <DialogTitle sx={{ pb: 1 }}>
        <Stack direction="row" spacing={1} sx={{ justifyContent: "space-between", alignItems: "center" }}>
          <Box sx={{ minWidth: 0 }}>
            <Typography component="span" variant="h6" sx={{ fontWeight: 800, display: "block" }}>Price from stock</Typography>
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
              label={covered ? "Ex stock" : "Partly in stock"}
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
              <Typography variant="body2">
                On the shelf <b>{qty(stock.onHand)} {unit}</b>
                {stock.places.length > 0 && (
                  <Typography component="span" variant="body2" color="text.secondary">
                    {" "}· {stock.places.map((place) => `${place.warehouse} ${qty(place.onHand)}`).join(" · ")}
                  </Typography>
                )}
              </Typography>
              {stock.heldForOrders > 0 && (
                <Typography variant="body2" color="text.secondary">{qty(stock.heldForOrders)} held for orders · {qty(stock.free)} free</Typography>
              )}
              {!covered && (
                <Typography variant="body2" color="warning.main" sx={{ fontWeight: 700 }}>Short by {qty(Math.max(0, need - stock.free))}</Typography>
              )}
            </Stack>

            <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "1fr 1fr" } }}>
              {/* ---- Your price ---- */}
              <Stack spacing={1.75} sx={(theme) => ({ p: 2.5, [theme.breakpoints.up("md")]: { borderRight: `1px solid ${theme.palette.divider}` } })}>
                <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>Your price</Typography>
                <Stack direction="row" spacing={1}>
                  <TextField
                    label={`Unit price${unit ? ` per ${unit}` : ""}`}
                    value={price}
                    onChange={(event) => onPriceChange(event.target.value)}
                    type="number"
                    error={price.trim() !== "" && !priceOk}
                    slotProps={{
                      htmlInput: { min: 0, step: "any", "aria-label": "Unit price" },
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
                <Typography variant="caption" color="text.secondary" sx={{ mt: "-6px !important" }}>{suggestion}</Typography>

                {cost != null && cost > 0 && (
                  <Stack direction="row" spacing={1.5} sx={{ alignItems: "center" }}>
                    <TextField
                      label="Margin"
                      size="small"
                      type="number"
                      value={margin}
                      onChange={(event) => onMarginChange(event.target.value)}
                      slotProps={{
                        htmlInput: { step: "any", "aria-label": "Margin percent" },
                        input: { endAdornment: <InputAdornment position="end">%</InputAdornment> },
                      }}
                      sx={{ width: 120 }}
                    />
                    <Typography variant="caption" color="text.secondary">
                      on cost {formatMoney(cost, currencyCode)}
                      {companyMargin != null ? ` · company ${companyMargin}%` : ""}
                    </Typography>
                  </Stack>
                )}
                {canSaveMargin && marginTouched && cost != null && cost > 0 && marginNumber != null && Number.isFinite(marginNumber)
                  && marginNumber >= 0 && marginNumber !== companyMargin && (
                  <FormControlLabel
                    sx={{ mt: "-8px !important" }}
                    control={<Checkbox size="small" checked={saveMargin} onChange={(event) => setSaveMargin(event.target.checked)} />}
                    label={<Typography variant="body2">Make {marginNumber}% the company margin for stock</Typography>}
                  />
                )}

                <Box sx={{ p: 1.25, borderRadius: 1.5, bgcolor: "action.hover" }}>
                  <Stack direction="row" sx={{ justifyContent: "space-between", alignItems: "baseline" }}>
                    <Typography variant="body2" color="text.secondary">{qty(need)} {unit} × {priceOk ? formatMoney(priceNumber, currencyCode) : "—"}</Typography>
                    <Typography variant="subtitle1" sx={{ fontWeight: 800 }}>{formatMoney(lineTotal, currencyCode)}</Typography>
                  </Stack>
                </Box>

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
              </Stack>

              {/* ---- What it sold for ---- */}
              <Stack spacing={1.5} sx={{ p: 2.5, bgcolor: (theme) => alpha(theme.palette.text.primary, 0.02) }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>What it sold for</Typography>
                <Stack direction="row" spacing={1.25}>
                  <PriceCard title="Last sold" icon={<ReceiptLong sx={{ fontSize: 16 }} />} reference={view.lastSold} onUse={usePrice} />
                  <PriceCard title="Last won" icon={<EmojiEvents sx={{ fontSize: 16 }} />} reference={view.lastWon} onUse={usePrice} />
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
                {!view.lastSold && !view.lastWon && earlier.length === 0 && (
                  <Typography variant="body2" color="text.secondary">This part has not been quoted or sold before.</Typography>
                )}
              </Stack>
            </Box>
          </>
        )}
      </DialogContent>

      <DialogActions sx={{ px: 2.5 }}>
        <Typography variant="caption" color="text.secondary" sx={{ flex: 1 }}>
          {view?.onQuote ? `Updates this line on ${view.onQuote.quoteNo}.` : "Starts the quote draft with this line priced."} You can still change it on the quote.
        </Typography>
        <Button onClick={onClose} disabled={use.isPending}>Cancel</Button>
        <Button
          variant="contained"
          onClick={() => use.mutate()}
          disabled={!view || !priceOk || use.isPending || (!quoteHasCurrency && currencyId === "")}
          startIcon={use.isPending ? <CircularProgress size={16} color="inherit" /> : <CheckCircle />}
        >
          Use this price
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
  const hints = [
    view.lastSold ? `Last sold ${formatMoney(view.lastSold.unitPrice, view.lastSold.currencyCode)}` : null,
    view.lastWon ? `Last won ${formatMoney(view.lastWon.unitPrice, view.lastWon.currencyCode)}` : null,
  ].filter(Boolean).join(" · ");

  return (
    <Stack spacing={0.5} sx={{ alignItems: "flex-start" }}>
      {priced ? (
        <>
          <Typography variant="caption" sx={{ fontWeight: 700, color: "success.main" }}>
            Priced {formatMoney(priced.unitPrice, priced.currencyCode)} on {priced.quoteNo}{priced.exStock ? " · ex stock" : ""}
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
        {canPrice && (
          <Button size="small" variant={priced ? "outlined" : "contained"} startIcon={<LocalOffer />} onClick={() => setOpen(true)}>
            {priced ? "Change price" : "Price from stock"}
          </Button>
        )}
        {priced && (
          <Button size="small" variant="text" onClick={() => navigate(`/sales/quotes/edit/${priced.quoteId}`)}>Open quote</Button>
        )}
      </Stack>
      {open && <StockPriceDialog open={open} rfqId={rfqId} itemId={itemId} onClose={() => setOpen(false)} />}
    </Stack>
  );
}
