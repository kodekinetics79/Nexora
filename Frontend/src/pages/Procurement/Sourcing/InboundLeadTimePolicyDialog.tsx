import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import {
  Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack, TextField, Typography,
} from "@mui/material";
import { toast } from "react-hot-toast";
import inboundShipmentService from "../../../api/services/inboundShipmentService";

const messageFrom = (error: unknown): string => {
  const response = (error as { response?: { data?: Record<string, string> } })?.response;
  return response?.data?.detail || response?.data?.message
    || (error as { message?: string })?.message || "Could not save the lead times.";
};

/**
 * The two tenant-wide inputs behind every material-available date. The backend protects updates
 * with `RequireManagerRole`; `canEdit` is the server-resolved `userData.isManager` mirror.
 */
export default function InboundLeadTimePolicyDialog({
  canEdit,
  onClose,
  onSaved,
}: {
  canEdit: boolean;
  onClose: () => void;
  onSaved: () => void;
}) {
  const policy = useQuery({
    queryKey: ["inbound-logistics-policy"],
    queryFn: () => inboundShipmentService.getPolicy(),
  });
  const [customs, setCustoms] = useState<string | null>(null);
  const [putaway, setPutaway] = useState<string | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [idempotencyKey] = useState(() => `inbound-policy:${crypto.randomUUID()}`);
  const customsValue = customs ?? (policy.data?.customsClearanceLeadDays?.toString() ?? "");
  const putawayValue = putaway ?? (policy.data?.putawayLeadDays?.toString() ?? "");
  const parsed = (value: string) => (value.trim() === "" ? null : Number(value));
  const usable = (value: string) => {
    const number = parsed(value);
    return number === null || (Number.isInteger(number) && number >= 0 && number <= 365);
  };
  const submittable = usable(customsValue) && usable(putawayValue)
    && (parsed(customsValue) !== null || parsed(putawayValue) !== null);

  const mutation = useMutation({
    mutationFn: () => inboundShipmentService.updatePolicy({
      customsClearanceLeadDays: parsed(customsValue),
      putawayLeadDays: parsed(putawayValue),
      idempotencyKey,
    }),
    onSuccess: () => {
      toast.success("Inbound lead times saved. Material available dates have been re-derived.");
      onSaved();
    },
    onError: (error) => {
      const message = messageFrom(error);
      setRefusal(message);
      toast.error(message);
    },
  });

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Inbound lead times</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="info">
            Material available date = shipment ETA + customs clearance + putaway, counted in{" "}
            <strong>working days</strong> with a Friday–Saturday weekend. Once a shipment has
            actually arrived or cleared customs, the real date replaces the estimate and the
            allowance that has already been spent is no longer added.
          </Alert>
          {!canEdit && (
            <Alert severity="info">
              These tenant-wide values are read-only for your role. A Manager, Administrator, or
              Owner can change them because every material-available date is recalculated.
            </Alert>
          )}
          {refusal && <Alert severity="error" onClose={() => setRefusal(null)}>{refusal}</Alert>}
          {policy.data && !policy.data.isConfigured && (
            <Alert severity="warning">
              These are not set, so no material available date is derived anywhere in this business
              unit. Leaving them blank is safer than a guess, but nothing downstream has a date to
              work from.
            </Alert>
          )}
          <TextField fullWidth type="number" label="Customs clearance (working days)"
            value={customsValue} disabled={!canEdit} error={!usable(customsValue)}
            onChange={(event) => setCustoms(event.target.value)}
            helperText="0–365. Zero is a real assertion — same-day clearance — and is different from leaving it blank."
            slotProps={{ inputLabel: { shrink: true }, htmlInput: { min: 0, max: 365 } }} />
          <TextField fullWidth type="number" label="Putaway (working days)"
            value={putawayValue} disabled={!canEdit} error={!usable(putawayValue)}
            onChange={(event) => setPutaway(event.target.value)}
            helperText="From the goods reaching the warehouse to being available to promise."
            slotProps={{ inputLabel: { shrink: true }, htmlInput: { min: 0, max: 365 } }} />
          {policy.data?.modifiedBy && (
            <Typography variant="caption" color="text.secondary">
              Last changed by {policy.data.modifiedBy}
              {policy.data.modifiedOn ? ` on ${new Date(policy.data.modifiedOn).toLocaleString()}` : ""}
            </Typography>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Close</Button>
        {canEdit && (
          <Button variant="contained" disabled={!submittable || mutation.isPending}
            onClick={() => mutation.mutate()}>
            {mutation.isPending ? "Saving…" : "Save lead times"}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
