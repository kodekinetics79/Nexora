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
import { Edit } from "@mui/icons-material";
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
 * The maker column of an RFQ line: the maker the customer named, any others they accept, and a
 * small "Makers" button to change the list. One box, ";" between makers, like email addresses.
 */
export function LineMakersCell({ rfqId, item, canEdit }: { rfqId: number; item: LineForMakers; canEdit: boolean }) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [open, setOpen] = React.useState(false);
  const accepted = acceptedMakersOf(item);
  const [text, setText] = React.useState("");

  const save = useMutation({
    mutationFn: () => rfqService.saveAcceptedMakers(rfqId, item.id, splitMakers(text).join("; ")),
    onSuccess: (result) => {
      queryClient.invalidateQueries({ queryKey: ["rfq-detail", rfqId] });
      queryClient.invalidateQueries({ queryKey: ["rfq-commercial-intelligence", rfqId] });
      for (const key of ["find-supplier-case", "find-supplier-internet", "find-supplier-email"]) {
        queryClient.invalidateQueries({ queryKey: [key] });
      }
      enqueueSnackbar(result.acceptedMakers.length > 1
        ? `Saved. Suppliers for ${result.acceptedMakers.length} makers will be found and asked.`
        : "Makers saved.", { variant: "success" });
      setOpen(false);
    },
    onError: (error: { response?: { data?: { detail?: string } } }) =>
      enqueueSnackbar(error?.response?.data?.detail || "The makers could not be saved.", { variant: "error" }),
  });

  const preview = splitMakers(text);
  const others = accepted.slice(item.manufacturerName ? 1 : 0);

  return (
    <>
      <Typography sx={{ fontSize: "0.8rem", fontWeight: 700 }}>{item.manufacturerName || (others.length ? "Any of these" : "N/A")}</Typography>
      {item.manufacturerName && (
        <Typography variant="caption" sx={{ color: "text.disabled", display: "block" }}>{item.manufacturerPartNumber || "N/A"}</Typography>
      )}
      {others.length > 0 && (
        <Box sx={{ mt: 0.5 }}>
          {item.manufacturerName && <Typography variant="caption" color="text.secondary">Also accepted</Typography>}
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
        <Button size="small" variant="text" startIcon={<Edit sx={{ fontSize: 14 }} />} sx={{ mt: 0.5, px: 0.5, minWidth: 0 }}
          onClick={() => { setText(accepted.join("; ")); setOpen(true); }}
          aria-label="Change accepted makers">
          Makers
        </Button>
      )}

      <Dialog open={open} onClose={save.isPending ? undefined : () => setOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle sx={{ pb: 0.5 }}>
          <Typography component="span" variant="h6" sx={{ fontWeight: 800, display: "block" }}>Makers the customer accepts</Typography>
          <Typography component="span" variant="body2" color="text.secondary" sx={{ display: "block" }} noWrap>
            {item.productShortDescription || item.productShortName || "This line"}
          </Typography>
        </DialogTitle>
        <DialogContent>
          <TextField
            fullWidth
            multiline
            minRows={2}
            sx={{ mt: 1.5 }}
            label="Makers"
            placeholder="ABB S203-C16; GE THQL32010; Eaton"
            value={text}
            onChange={(event) => setText(event.target.value)}
            helperText='Separate makers with ";". Add the part number after the maker if you know it.'
            slotProps={{ htmlInput: { "aria-label": "Accepted makers" } }}
          />
          {preview.length > 0 && (
            <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: "wrap", mt: 1.5 }}>
              {preview.map((maker) => <Chip key={maker} size="small" color="primary" variant="outlined" label={makerLabel(maker)} />)}
            </Stack>
          )}
          <Typography variant="caption" color="text.secondary" sx={{ display: "block", mt: 1.5 }}>
            Find supplier looks for suppliers of every maker here, and each supplier email lists them all.
          </Typography>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setOpen(false)} disabled={save.isPending}>Cancel</Button>
          <Button variant="contained" onClick={() => save.mutate()} disabled={save.isPending || preview.length > 12}>Save makers</Button>
        </DialogActions>
      </Dialog>
    </>
  );
}
