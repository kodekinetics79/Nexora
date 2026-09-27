import React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import {
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from "@mui/material";
import { Close, Edit } from "@mui/icons-material";
import { useSnackbar } from "notistack";
import rfqService from "../../../api/services/rfqService";

const APPROVED_KEY = "approved manufacturers";

/** "ABB 1SDA; GE THQL32010 ;; eaton" → ["ABB 1SDA", "GE THQL32010", "eaton"], duplicates dropped. */
export const splitMakers = (text: string | null | undefined): string[] => {
  const seen = new Set<string>();
  return (text ?? "")
    .split(/[;\n\r]+/)
    .map((x) => x.trim().replace(/\s+/g, " "))
    .filter((x) => {
      if (!x || seen.has(x.toLowerCase())) return false;
      seen.add(x.toLowerCase());
      return true;
    });
};

/** The customer's accepted-maker list stored on the line ("Approved manufacturers"), or []. */
export const approvedMakersOf = (extraFields: string | null | undefined): string[] => {
  if (!extraFields) return [];
  try {
    const parsed = JSON.parse(extraFields) as Record<string, unknown>;
    const key = Object.keys(parsed).find((k) => k.trim().toLowerCase() === APPROVED_KEY);
    return key && typeof parsed[key] === "string" ? splitMakers(parsed[key] as string) : [];
  } catch {
    return [];
  }
};

const CORPORATE = new Set(["and", "&", "sons", "son", "company", "co", "co.", "ltd", "ltd.", "limited", "inc", "inc.", "llc", "plc",
  "gmbh", "ag", "bv", "b.v.", "sa", "s.a.", "sas", "pty", "corporation", "corp", "corp.", "est", "est.", "establishment", "the"]);
const NOT_A_BRAND = new Set(["general", "international", "national", "united", "saudi", "arabian", "arabia", "gulf", "al", "the",
  "electric", "electrical", "industries", "industrial", "trading", "power", "global", "american", "european", "middle", "east",
  "new", "first", "royal", "advanced", "modern", "technical", "engineering"]);
const PART = /^[A-Za-z0-9][A-Za-z0-9\-_./]{2,}$/;

/**
 * A short, readable label for one accepted maker as the customer's document wrote it:
 * "ABB ELECTRICAL INDUSTRIES CO. LTD (SA): P/N AF96-30-00-13" → "ABB AF96-30-00-13".
 */
export const makerLabel = (segment: string): string => {
  const name = segment.split(/\s*(?:\(|:|\bvia\b|\s\/\s|\bP\/N\b)/i)[0];
  const words = name.split(/\s+/).filter((w) => w && !CORPORATE.has(w.toLowerCase()) && !(PART.test(w) && /\d/.test(w)));
  const brand = words.length === 0 ? "" : words[0].length >= 3 && !NOT_A_BRAND.has(words[0].toLowerCase()) ? words[0] : words.slice(0, 2).join(" ");
  const parts = [...new Set(segment.replace(/[():]/g, " ").split(/\s+/).filter((w) => PART.test(w) && /\d/.test(w)))];
  return [brand, parts.join(", ")].filter(Boolean).join(" ") || segment;
};

export const isApprovedMakersField = (label: string) => label.trim().toLowerCase() === APPROVED_KEY;

interface LineForMakers {
  id: number;
  manufacturerName?: string | null;
  manufacturerPartNumber?: string | null;
  extraFields?: string | null;
  productShortDescription?: string | null;
  productShortName?: string | null;
}

/** Everything the customer accepts, the line's own maker first: ["ABB S203", "GE THQL32010", "Eaton"]. */
export const acceptedMakersOf = (item: LineForMakers): string[] => {
  const approved = approvedMakersOf(item.extraFields);
  // A part number with no maker name is not a maker: "300012346" (a material code) or "SEL-751"
  // beside "SCHWEITZER SEL-751" only added a confusing chip.
  const primary = item.manufacturerName?.trim()
    ? [item.manufacturerName, item.manufacturerPartNumber].filter(Boolean).join(" ").trim()
    : approved.length > 0 ? "" : (item.manufacturerPartNumber ?? "").trim();
  const maker = item.manufacturerName?.trim().toLowerCase();
  const others = maker ? approved.filter((x) => !x.toLowerCase().startsWith(maker)) : approved;
  return primary ? [primary, ...others] : others;
};

/**
 * The brand column of an RFQ line, saying who decides: the CUSTOMER. "Customer asked for" the brand
 * they named, "Customer also accepts" the others, or "Customer accepts any of" when none is
 * preferred. Owner 2026-09-27: "Makers" was too vague, and "Approved brands" begged the question
 * "approved by whom?". Typing brands separated by ";" was one step too many, so the list is tags.
 */
export function LineMakersCell({ rfqId, item, canEdit }: { rfqId: number; item: LineForMakers; canEdit: boolean }) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [open, setOpen] = React.useState(false);
  const accepted = acceptedMakersOf(item);
  const [brands, setBrands] = React.useState<string[]>([]);
  const [draft, setDraft] = React.useState("");

  // A brand typed but not yet added still counts when the rep presses Save.
  const pending = splitMakers([...brands, draft].join(";"));
  const save = useMutation({
    mutationFn: () => rfqService.saveAcceptedMakers(rfqId, item.id, pending.join("; ")),
    onSuccess: (result) => {
      queryClient.invalidateQueries({ queryKey: ["rfq-detail", rfqId] });
      queryClient.invalidateQueries({ queryKey: ["rfq-commercial-intelligence", rfqId] });
      for (const key of ["find-supplier-case", "find-supplier-internet", "find-supplier-email"]) {
        queryClient.invalidateQueries({ queryKey: [key] });
      }
      enqueueSnackbar(result.acceptedMakers.length > 1
        ? `Saved. Find supplier will look for suppliers of all ${result.acceptedMakers.length} brands.`
        : "Brands saved.", { variant: "success" });
      setOpen(false);
    },
    onError: (error: { response?: { data?: { detail?: string } } }) =>
      enqueueSnackbar(error?.response?.data?.detail || "The brands could not be saved.", { variant: "error" }),
  });

  const add = () => {
    const next = splitMakers([...brands, draft].join(";"));
    setBrands(next);
    setDraft("");
  };
  const others = accepted.slice(item.manufacturerName ? 1 : 0);

  return (
    <>
      {item.manufacturerName ? (
        <>
          <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>Customer asked for</Typography>
          <Typography sx={{ fontSize: "0.8rem", fontWeight: 700 }}>
            {item.manufacturerName}{item.manufacturerPartNumber ? ` ${item.manufacturerPartNumber}` : ""}
          </Typography>
        </>
      ) : others.length === 0 && (
        <Typography variant="caption" color="text.secondary">No brand named by the customer</Typography>
      )}
      {others.length > 0 && (
        <Box sx={{ mt: item.manufacturerName ? 0.5 : 0 }}>
          <Typography variant="caption" color="text.secondary">{item.manufacturerName ? "Customer also accepts" : "Customer accepts any of"}</Typography>
          <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: "wrap", mt: 0.25 }}>
            {others.map((maker) => (
              <Tooltip key={maker} title={maker}>
                <Chip size="small" variant="outlined" label={makerLabel(maker)} />
              </Tooltip>
            ))}
          </Stack>
        </Box>
      )}
      {canEdit && (
        <Tooltip title="Change the brands the customer accepts for this part, as their RFQ or a later message says. Find supplier asks suppliers for each one." describeChild>
          <Button size="small" variant="text" startIcon={<Edit sx={{ fontSize: 14 }} />} sx={{ mt: 0.5, px: 0.5, minWidth: 0 }}
            onClick={() => { setBrands(accepted); setDraft(""); setOpen(true); }}>
            Edit accepted brands
          </Button>
        </Tooltip>
      )}

      <Dialog open={open} onClose={save.isPending ? undefined : () => setOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle sx={{ pb: 0.5 }}>
          <Typography component="span" variant="h6" sx={{ fontWeight: 800, display: "block" }}>Brands / manufacturers the customer accepts</Typography>
          <Typography component="span" variant="body2" color="text.secondary" sx={{ display: "block" }} noWrap>
            {item.productShortDescription || item.productShortName || "This line"}
          </Typography>
        </DialogTitle>
        <DialogContent>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
            Keep this list as the customer gave it in their RFQ, or as they later agreed. Find supplier asks suppliers for each brand, and you can quote any of them.
          </Typography>
          <Stack direction="row" spacing={0.75} useFlexGap sx={{ flexWrap: "wrap", mt: 1.5, minHeight: 32 }}>
            {brands.length === 0 && <Typography variant="body2" color="text.secondary">No brands yet. Add the first one below.</Typography>}
            {brands.map((brand) => (
              <Tooltip key={brand} title={brand}>
                <Chip color="primary" variant="outlined" label={makerLabel(brand)}
                  onDelete={() => setBrands(brands.filter((x) => x !== brand))}
                  deleteIcon={<Close aria-label={`Remove ${makerLabel(brand)}`} />} />
              </Tooltip>
            ))}
          </Stack>
          <Stack direction="row" spacing={1} sx={{ mt: 2, alignItems: "flex-start" }}>
            <TextField
              fullWidth
              size="small"
              label="Add a brand / manufacturer"
              placeholder="e.g. Siemens, or Siemens 3RT2046-1AN20"
              value={draft}
              onChange={(event) => setDraft(event.target.value)}
              onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); if (draft.trim()) add(); } }}
              helperText="Press Enter to add. The part number is optional."
              slotProps={{ htmlInput: { maxLength: 150, "aria-label": "Add a brand" } }}
            />
            <Button variant="outlined" onClick={add} disabled={!draft.trim()} sx={{ flexShrink: 0 }}>Add</Button>
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setOpen(false)} disabled={save.isPending}>Cancel</Button>
          <Button variant="contained" onClick={() => save.mutate()} disabled={save.isPending || pending.length > 12}>Save brands</Button>
        </DialogActions>
      </Dialog>
    </>
  );
}
