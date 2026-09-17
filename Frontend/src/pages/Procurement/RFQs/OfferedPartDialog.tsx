import React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  MenuItem,
  Radio,
  RadioGroup,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { SwapHoriz } from "@mui/icons-material";
import { useSnackbar } from "notistack";
import rfqService from "../../../api/services/rfqService";
import productService from "../../../api/services/productService";
import { useAuth } from "../../../context/AuthContext";

export type OfferedKind = "REPLACEMENT" | "EQUIVALENT";

export interface OfferedPartLine {
  id: number;
  manufacturerName?: string | null;
  manufacturerPartNumber?: string | null;
  productShortDescription?: string | null;
  productShortName?: string | null;
  offeredPartNumber?: string | null;
  offeredMakerName?: string | null;
  offeredKind?: string | null;
  offeredNote?: string | null;
  offeredSpecs?: string | null;
}

/** "Offered: GE THQL32010, replaces ABB AF96-30-00-13" — the sentence the customer reads. */
export const offeredSentence = (line: OfferedPartLine): string | null => {
  const offered = [line.offeredMakerName, line.offeredPartNumber].filter(Boolean).join(" ").trim();
  if (!offered) return null;
  const asked = line.manufacturerPartNumber?.trim();
  const head = line.offeredKind === "EQUIVALENT"
    ? `Offered as an equivalent: ${offered}${asked ? `, in place of ${asked}` : ""}`
    : `Offered: ${offered}${asked ? `, replaces ${asked}` : ""}`;
  return line.offeredNote ? `${head}. ${line.offeredNote}` : head;
};

/**
 * The part asked for is obsolete or discontinued: record what is really being offered. The quote
 * line, the supplier request and the catalogue all follow it, and the swap is remembered.
 */
export default function OfferedPartDialog({ open, rfqId, line, onClose }: {
  open: boolean; rfqId: number; line: OfferedPartLine; onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { userData } = useAuth();

  const [kind, setKind] = React.useState<OfferedKind>("REPLACEMENT");
  const [partNumber, setPartNumber] = React.useState("");
  const [maker, setMaker] = React.useState("");
  const [note, setNote] = React.useState("");
  const [specs, setSpecs] = React.useState("");
  const [productId, setProductId] = React.useState<number | "">("");
  const seeded = React.useRef(false);

  React.useEffect(() => {
    if (!open) { seeded.current = false; return; }
    if (seeded.current) return;
    seeded.current = true;
    setKind((line.offeredKind as OfferedKind) ?? "REPLACEMENT");
    setPartNumber(line.offeredPartNumber ?? "");
    setMaker(line.offeredMakerName ?? line.manufacturerName ?? "");
    setNote(line.offeredNote ?? "");
    setSpecs(line.offeredSpecs ?? "");
    setProductId("");
  }, [open, line]);

  // The catalogue part for what is offered, so stock and suppliers follow the real item.
  const matches = useQuery({
    queryKey: ["offered-part-search", userData?.businessUnitId, partNumber.trim()],
    queryFn: () => productService.getAll({
      businessUnitId: userData?.businessUnitId || 0, pageNumber: 1, pageSize: 10,
      search: partNumber.trim(), isActive: true,
    }),
    enabled: open && partNumber.trim().length >= 3,
    staleTime: 60_000,
  });
  const products: { id: number; partNo: string; productName?: string | null }[] = matches.data?.items ?? [];

  const save = useMutation({
    mutationFn: (clear: boolean) => rfqService.saveOfferedPart(rfqId, line.id, clear ? {} : {
      partNumber: partNumber.trim(), makerName: maker.trim() || null, kind,
      note: note.trim() || null, specs: specs.trim() || null,
      productId: productId === "" ? null : productId,
    }),
    onSuccess: (_result, clear) => {
      for (const key of ["rfq-detail", "rfq-commercial-intelligence", "stock-price", "other-makers-in-stock",
        "find-supplier-case", "find-supplier-email", "send-quote", "send-quote-email"]) {
        queryClient.invalidateQueries({ queryKey: [key] });
      }
      enqueueSnackbar(clear ? "The line is offered as the customer asked again." : "Saved. The quote and supplier requests now use this part.", { variant: "success" });
      onClose();
    },
    onError: (error: { response?: { data?: { detail?: string } } }) =>
      enqueueSnackbar(error?.response?.data?.detail || "The offered part could not be saved.", { variant: "error" }),
  });

  const ready = partNumber.trim().length > 0 && (kind === "REPLACEMENT" || specs.trim().length > 0);
  const asked = [line.manufacturerName, line.manufacturerPartNumber].filter(Boolean).join(" ") || "the part asked for";

  return (
    <Dialog open={open} onClose={save.isPending ? undefined : onClose} maxWidth="sm" fullWidth>
      <DialogTitle sx={{ pb: 0.5 }}>
        <Typography component="span" variant="h6" sx={{ fontWeight: 800, display: "block" }}>Offer a different part</Typography>
        <Typography component="span" variant="body2" color="text.secondary" sx={{ display: "block" }} noWrap>
          {line.productShortDescription || line.productShortName || "This line"} · asked for {asked}
        </Typography>
      </DialogTitle>
      <DialogContent>
        <RadioGroup value={kind} onChange={(event) => setKind(event.target.value as OfferedKind)}>
          <FormControlLabel value="REPLACEMENT" control={<Radio size="small" />}
            label={<Typography variant="body2">The maker replaced it with a new part</Typography>} />
          <FormControlLabel value="EQUIVALENT" control={<Radio size="small" />}
            label={<Typography variant="body2">Discontinued: offering an equivalent</Typography>} />
        </RadioGroup>

        <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5} sx={{ mt: 1 }}>
          <TextField label="Part number offered" value={partNumber} onChange={(event) => setPartNumber(event.target.value)}
            sx={{ flex: 1 }} slotProps={{ htmlInput: { maxLength: 100, "aria-label": "Part number offered" } }} />
          <TextField label="Maker" value={maker} onChange={(event) => setMaker(event.target.value)}
            sx={{ flex: 1 }} slotProps={{ htmlInput: { maxLength: 150, "aria-label": "Maker offered" } }} />
        </Stack>

        {products.length > 0 && (
          <TextField select fullWidth sx={{ mt: 1.5 }} label="Use this part from your catalogue (optional)"
            value={productId} onChange={(event) => setProductId(event.target.value === "" ? "" : Number(event.target.value))}
            helperText="Links stock, suppliers and pricing to what you are really offering.">
            <MenuItem value="">Not in the catalogue</MenuItem>
            {products.map((product) => (
              <MenuItem key={product.id} value={product.id}>{product.partNo} · {product.productName}</MenuItem>
            ))}
          </TextField>
        )}

        <TextField fullWidth sx={{ mt: 1.5 }} label="Note for the customer" value={note} onChange={(event) => setNote(event.target.value)}
          placeholder={kind === "EQUIVALENT" ? "e.g. Original discontinued by the maker, no direct successor" : "e.g. Maker's successor part, same footprint"}
          slotProps={{ htmlInput: { maxLength: 300, "aria-label": "Note for the customer" } }} />

        {kind === "EQUIVALENT" && (
          <TextField fullWidth multiline minRows={3} sx={{ mt: 1.5 }} label="Specs for the customer to evaluate" value={specs}
            onChange={(event) => setSpecs(event.target.value)} required
            placeholder="e.g. 3P, 250A, 36kA, 415V, thermal-magnetic, IEC 60947-2, same mounting"
            helperText="Printed under the line so the customer can judge the equivalent."
            slotProps={{ htmlInput: { maxLength: 600, "aria-label": "Specs for the customer" } }} />
        )}

        {offeredSentence({ ...line, offeredPartNumber: partNumber, offeredMakerName: maker, offeredKind: kind, offeredNote: note }) && (
          <Alert icon={<SwapHoriz fontSize="small" />} severity="info" sx={{ mt: 2 }}>
            <Typography variant="body2">
              {offeredSentence({ ...line, offeredPartNumber: partNumber, offeredMakerName: maker, offeredKind: kind, offeredNote: note })}
            </Typography>
            {kind === "EQUIVALENT" && specs.trim() && (
              <Typography variant="caption" color="text.secondary">Specification: {specs.trim()}</Typography>
            )}
          </Alert>
        )}
      </DialogContent>
      <DialogActions>
        {line.offeredPartNumber && (
          <Button color="inherit" sx={{ mr: "auto" }} disabled={save.isPending} onClick={() => save.mutate(true)}>Offer as asked</Button>
        )}
        <Button onClick={onClose} disabled={save.isPending}>Cancel</Button>
        <Button variant="contained" disabled={!ready || save.isPending} onClick={() => save.mutate(false)}>Save</Button>
      </DialogActions>
    </Dialog>
  );
}

/** The line's own row: what is being offered instead, and a way to change it. */
export function OfferedPartCell({ rfqId, line, canEdit }: { rfqId: number; line: OfferedPartLine; canEdit: boolean }) {
  const [open, setOpen] = React.useState(false);
  const sentence = offeredSentence(line);
  return (
    <>
      {sentence && (
        <Box sx={{ mt: 0.5 }}>
          <Chip size="small" color="info" variant="outlined" icon={<SwapHoriz sx={{ fontSize: 14 }} />}
            label={`${line.offeredMakerName ? `${line.offeredMakerName} ` : ""}${line.offeredPartNumber}`} />
          <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>
            {line.offeredKind === "EQUIVALENT" ? "equivalent offered" : "replaces the part asked for"}
          </Typography>
        </Box>
      )}
      {canEdit && (
        <Button size="small" variant="text" startIcon={<SwapHoriz sx={{ fontSize: 14 }} />} sx={{ mt: 0.25, px: 0.5, minWidth: 0 }}
          onClick={() => setOpen(true)} aria-label="Offer a different part">
          {sentence ? "Change part offered" : "Part replaced?"}
        </Button>
      )}
      {open && <OfferedPartDialog open rfqId={rfqId} line={line} onClose={() => setOpen(false)} />}
    </>
  );
}
