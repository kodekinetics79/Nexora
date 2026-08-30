import { useMemo, useState } from "react";
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
  Divider,
  FormControl,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Tooltip,
  Typography,
} from "@mui/material";
import {
  Anchor,
  DirectionsBoat,
  EventAvailable,
  Flight,
  Place,
  Settings,
  Warning,
} from "@mui/icons-material";
import { toast } from "react-hot-toast";
import inboundShipmentService, {
  MILESTONE_LABELS,
  PORT_KIND_LABELS,
  RECEIPT_STATE_LABELS,
  type InboundMilestone,
  type InboundShipment,
  type PortOfEntry,
} from "../../../api/services/inboundShipmentService";
import InboundLeadTimePolicyDialog from "./InboundLeadTimePolicyDialog";

/**
 * FR-MAS-01..05. Inbound supplier shipments for one supplier purchase order.
 *
 * <p>Self-contained on purpose: it owns its own queries, mutations and dialogs so the sourcing
 * workbench only has to render it. The workbench is already 2,700 lines and shared with other
 * work in flight.</p>
 */

const commandKey = (prefix: string) => `${prefix}:${crypto.randomUUID()}`;

/**
 * The server's refusal, in its own words. `detail` carries the sentence that says what is wrong;
 * `title` is only the category. Reading the category first throws the reason away.
 */
const errorMessage = (error: unknown, fallback: string): string => {
  const response = (error as { response?: { data?: Record<string, string> } })?.response;
  return (
    response?.data?.detail ||
    response?.data?.message ||
    (error as { message?: string })?.message ||
    response?.data?.title ||
    fallback
  );
};

const localCalendarDate = (value: Date) =>
  `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, "0")}-${String(
    value.getDate(),
  ).padStart(2, "0")}`;

/**
 * A `YYYY-MM-DD` calendar date rendered in the user's locale WITHOUT `new Date(string)`, which
 * parses a bare date as UTC midnight and can display the previous day. A material available date
 * that reads one day early is a promise nobody made.
 */
const calendarDate = (value?: string | null) => {
  if (!value) return null;
  const [year, month, day] = value.slice(0, 10).split("-").map(Number);
  return Number.isFinite(year) && Number.isFinite(month) && Number.isFinite(day)
    ? new Date(year, month - 1, day).toLocaleDateString()
    : value;
};

const milestoneColor = (
  milestone: InboundMilestone,
): "default" | "success" | "warning" | "error" | "info" => {
  if (milestone === "CANCELLED") return "error";
  if (milestone === "RECEIVED_AT_WAREHOUSE") return "success";
  if (milestone === "READY_AT_FACTORY") return "warning";
  return "info";
};

const portIcon = (kind: PortOfEntry["kind"]) =>
  kind === "AIRPORT" ? <Flight fontSize="small" /> : kind === "SEAPORT"
    ? <DirectionsBoat fontSize="small" />
    : <Place fontSize="small" />;

export interface InboundShipmentsPanelProps {
  purchaseOrderId: number;
  purchaseOrderNumber: string;
  /** Purchase order lines, so a new shipment can be built without a second round trip. */
  lines: Array<{
    id: number;
    description: string;
    orderedQuantity: number;
    receivedQuantity: number;
  }>;
  /** Orders/Edit. Reads stay open to Orders/View. */
  canEdit: boolean;
  /** Products/Create — the same bar the warehouse master is held to. */
  canManagePorts: boolean;
  /** Server `RequireManagerRole`: these values re-derive dates across the whole tenant. */
  canManagePolicy: boolean;
}

export default function InboundShipmentsPanel({
  purchaseOrderId,
  purchaseOrderNumber,
  lines,
  canEdit,
  canManagePorts,
  canManagePolicy,
}: InboundShipmentsPanelProps) {
  const queryClient = useQueryClient();
  const [createOpen, setCreateOpen] = useState(false);
  const [milestoneShipment, setMilestoneShipment] = useState<InboundShipment | null>(null);
  const [trackingShipment, setTrackingShipment] = useState<InboundShipment | null>(null);
  const [policyOpen, setPolicyOpen] = useState(false);

  const shipmentsKey = ["inbound-shipments", purchaseOrderId] as const;
  const shipments = useQuery({
    queryKey: shipmentsKey,
    queryFn: () => inboundShipmentService.getForPurchaseOrder(purchaseOrderId),
  });
  const ports = useQuery({
    queryKey: ["ports-of-entry"],
    queryFn: () => inboundShipmentService.getPortsOfEntry(false),
  });
  const policy = useQuery({
    queryKey: ["inbound-logistics-policy"],
    queryFn: () => inboundShipmentService.getPolicy(),
  });

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: shipmentsKey });
    void queryClient.invalidateQueries({ queryKey: ["inbound-logistics-policy"] });
  };

  const openQuantities = useMemo(() => {
    const shipped = new Map<number, number>();
    for (const shipment of shipments.data ?? []) {
      if (shipment.milestone === "CANCELLED") continue;
      for (const line of shipment.lines) {
        shipped.set(
          line.purchaseOrderLineId,
          (shipped.get(line.purchaseOrderLineId) ?? 0) + line.shippedQuantity,
        );
      }
    }
    return lines.map((line) => ({
      ...line,
      alreadyShipped: shipped.get(line.id) ?? 0,
      unshipped: Math.max(0, line.orderedQuantity - (shipped.get(line.id) ?? 0)),
    }));
  }, [lines, shipments.data]);

  const rows = shipments.data ?? [];

  return (
    <Box sx={{ p: 2, borderTop: 1, borderColor: "divider" }}>
      <Stack
        direction={{ xs: "column", sm: "row" }}
        spacing={1}
        sx={{ alignItems: { sm: "center" }, justifyContent: "space-between", mb: 1 }}
      >
        <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}>
          <Anchor fontSize="small" color="action" />
          <Typography variant="subtitle2" sx={{ fontWeight: 700 }}>
            Inbound shipments
          </Typography>
          <Chip size="small" variant="outlined" label={`${rows.length}`} />
        </Stack>
        <Stack direction="row" spacing={1}>
          <Button size="small" startIcon={<Settings />} onClick={() => setPolicyOpen(true)}>
            {canManagePolicy ? "Lead times" : "View lead times"}
          </Button>
          {canEdit && (
            <Button
              size="small"
              variant="outlined"
              startIcon={<DirectionsBoat />}
              disabled={openQuantities.every((line) => line.unshipped <= 0)}
              onClick={() => setCreateOpen(true)}
            >
              Record shipment
            </Button>
          )}
        </Stack>
      </Stack>

      {/*
        FR-MAS-03. The derivation has two inputs and both live on one tenant policy row. Saying so
        HERE, next to the dates that are missing because of it, is the difference between a fixable
        gap and a blank column nobody can explain.
      */}
      {policy.data && !policy.data.isConfigured && (
        <Alert severity="warning" sx={{ mb: 1 }}>
          Customs-clearance and putaway lead times have not been set for this business unit, so no
          material available date can be derived for any shipment. Set them under{" "}
          <strong>Lead times</strong>.
        </Alert>
      )}

      {shipments.isLoading ? (
        <Stack direction="row" spacing={1} sx={{ alignItems: "center", py: 2 }}>
          <CircularProgress size={18} />
          <Typography variant="body2" color="text.secondary">
            Loading shipments…
          </Typography>
        </Stack>
      ) : shipments.isError ? (
        <Alert severity="error">
          {errorMessage(shipments.error, "Could not load inbound shipments.")}
        </Alert>
      ) : rows.length === 0 ? (
        <Typography variant="body2" color="text.secondary">
          No inbound shipment has been recorded against this order yet.
        </Typography>
      ) : (
        <Box sx={{ overflowX: "auto" }}>
          <Table size="small" sx={{ minWidth: 900 }}>
            <TableHead>
              <TableRow>
                <TableCell>Shipment</TableCell>
                <TableCell>Milestone</TableCell>
                <TableCell>Entry location</TableCell>
                <TableCell>Carrier / tracking</TableCell>
                <TableCell>ETA</TableCell>
                <TableCell>Material available</TableCell>
                <TableCell align="right">Quantity</TableCell>
                <TableCell>Goods receipt</TableCell>
                <TableCell />
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map((shipment) => (
                <TableRow key={shipment.id}>
                  <TableCell>
                    <Typography variant="body2" sx={{ fontWeight: 700 }}>
                      {shipment.shipmentNumber}
                    </Typography>
                    {shipment.cancellationReason && (
                      <Typography variant="caption" color="error">
                        {shipment.cancellationReason}
                      </Typography>
                    )}
                  </TableCell>
                  <TableCell>
                    <Chip
                      size="small"
                      label={MILESTONE_LABELS[shipment.milestone]}
                      color={milestoneColor(shipment.milestone)}
                    />
                    <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>
                      {calendarDate(shipment.milestoneOccurredOn)}
                    </Typography>
                  </TableCell>
                  <TableCell>{shipment.portOfEntryName ?? "—"}</TableCell>
                  <TableCell>
                    {shipment.carrierName ?? "—"}
                    {shipment.trackingReference && (
                      <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>
                        {shipment.trackingReference}
                      </Typography>
                    )}
                  </TableCell>
                  <TableCell>{calendarDate(shipment.etaDate) ?? "—"}</TableCell>
                  <TableCell>
                    <MaterialAvailableCell shipment={shipment} />
                  </TableCell>
                  <TableCell align="right">
                    {shipment.lines.reduce((total, line) => total + line.shippedQuantity, 0)}
                  </TableCell>
                  <TableCell>
                    <GoodsReceiptCell shipment={shipment} />
                  </TableCell>
                  <TableCell align="right">
                    {canEdit && shipment.allowedNextMilestones.length > 0 && (
                      <Stack direction="row" spacing={1} sx={{ justifyContent: "flex-end" }}>
                        <Button size="small" onClick={() => setTrackingShipment(shipment)}>
                          Tracking
                        </Button>
                        <Button
                          size="small"
                          variant="outlined"
                          onClick={() => setMilestoneShipment(shipment)}
                        >
                          Milestone
                        </Button>
                      </Stack>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Box>
      )}

      {createOpen && (
        <CreateShipmentDialog
          purchaseOrderId={purchaseOrderId}
          purchaseOrderNumber={purchaseOrderNumber}
          lines={openQuantities}
          ports={ports.data ?? []}
          canManagePorts={canManagePorts}
          onPortsChanged={() => void queryClient.invalidateQueries({ queryKey: ["ports-of-entry"] })}
          onClose={() => setCreateOpen(false)}
          onSaved={() => {
            setCreateOpen(false);
            refresh();
          }}
        />
      )}

      {milestoneShipment && (
        <MilestoneDialog
          shipment={milestoneShipment}
          ports={ports.data ?? []}
          onClose={() => setMilestoneShipment(null)}
          onSaved={() => {
            setMilestoneShipment(null);
            refresh();
          }}
        />
      )}

      {trackingShipment && (
        <TrackingDialog
          shipment={trackingShipment}
          onClose={() => setTrackingShipment(null)}
          onSaved={() => {
            setTrackingShipment(null);
            refresh();
          }}
        />
      )}

      {policyOpen && (
        <InboundLeadTimePolicyDialog
          canEdit={canManagePolicy}
          onClose={() => setPolicyOpen(false)}
          onSaved={() => {
            setPolicyOpen(false);
            refresh();
          }}
        />
      )}
    </Box>
  );
}

/**
 * FR-MAS-03. The derived date, and — on hover — the arithmetic that produced it.
 *
 * A derived date with no visible inputs is a number a user either trusts blindly or ignores, and
 * both are bad outcomes for a date a customer promise rests on.
 */
function MaterialAvailableCell({ shipment }: { shipment: InboundShipment }) {
  const availability = shipment.materialAvailability;
  if (!availability.date) {
    return (
      <Tooltip title={availability.unavailableReason ?? "Not derived."}>
        <Stack direction="row" spacing={0.5} sx={{ alignItems: "center" }}>
          <Warning fontSize="small" color="warning" />
          <Typography variant="body2" color="text.secondary">
            Not derived
          </Typography>
        </Stack>
      </Tooltip>
    );
  }

  const basis = (availability.basisKind ?? "ETA").replaceAll("_", " ").toLowerCase();
  const explanation =
    `${calendarDate(availability.basisDate)} (${basis})` +
    ` + ${availability.customsClearanceDays ?? 0} working day(s) customs clearance` +
    ` + ${availability.putawayDays ?? 0} working day(s) putaway` +
    ` = ${calendarDate(availability.date)}. Weekends (Friday and Saturday) are excluded.`;

  const late =
    shipment.customerRequiredOn != null &&
    availability.date > shipment.customerRequiredOn;

  return (
    <Tooltip title={explanation}>
      <Stack direction="row" spacing={0.5} sx={{ alignItems: "center" }}>
        <EventAvailable fontSize="small" color={late ? "error" : "success"} />
        <Typography variant="body2" color={late ? "error" : undefined}>
          {calendarDate(availability.date)}
        </Typography>
      </Stack>
    </Tooltip>
  );
}

/**
 * The Gate 4 / Gate 5 seam, on screen.
 *
 * "Received at warehouse" is a logistics milestone: a truck reached the gate. It is NOT a goods
 * receipt — that counts what actually arrived, names the lot or serial numbers, and moves quantity
 * on hand, and only a person standing at the dock can do it. So a shipment can legitimately read
 * "received at warehouse · not booked in", and this cell says exactly that instead of letting the
 * milestone imply a receipt that never happened.
 */
function GoodsReceiptCell({ shipment }: { shipment: InboundShipment }) {
  if (shipment.receiptState === "NOT_APPLICABLE") {
    return (
      <Typography variant="body2" color="text.secondary">
        —
      </Typography>
    );
  }

  const outstanding = shipment.outstandingReceiptQuantity;
  const arrived = shipment.milestone === "RECEIVED_AT_WAREHOUSE";
  const chase = arrived && outstanding > 0;

  return (
    <Tooltip
      title={
        outstanding > 0
          ? `${shipment.receiptedQuantity} of ${
              shipment.receiptedQuantity + outstanding
            } unit(s) booked in. Post a goods receipt against ${
              shipment.purchaseOrderNumber
            } to take the rest into stock — that is what creates the material lots and moves quantity on hand.`
          : "Every unit on this shipment has been counted in by a goods receipt, and its material lots exist."
      }
    >
      <Stack direction="row" spacing={0.5} sx={{ alignItems: "center" }}>
        <Chip
          size="small"
          variant={outstanding > 0 ? "outlined" : "filled"}
          color={outstanding === 0 ? "success" : chase ? "warning" : "default"}
          label={RECEIPT_STATE_LABELS[shipment.receiptState]}
        />
        {outstanding > 0 && (
          <Typography variant="caption" color={chase ? "warning.main" : "text.secondary"}>
            {outstanding} outstanding
          </Typography>
        )}
      </Stack>
    </Tooltip>
  );
}

interface ShipmentLineDraft {
  id: number;
  description: string;
  orderedQuantity: number;
  receivedQuantity: number;
  alreadyShipped: number;
  unshipped: number;
}

/**
 * FR-MAS-05. One purchase order line can be split across several shipments, so the dialog offers
 * every line that still has unshipped quantity and pre-fills the remainder rather than the whole
 * ordered amount.
 */
function CreateShipmentDialog({
  purchaseOrderId,
  purchaseOrderNumber,
  lines,
  ports,
  canManagePorts,
  onPortsChanged,
  onClose,
  onSaved,
}: {
  purchaseOrderId: number;
  purchaseOrderNumber: string;
  lines: ShipmentLineDraft[];
  ports: PortOfEntry[];
  canManagePorts: boolean;
  onPortsChanged: () => void;
  onClose: () => void;
  onSaved: () => void;
}) {
  const shippable = lines.filter((line) => line.unshipped > 0);
  const [quantities, setQuantities] = useState<Record<number, string>>(() =>
    Object.fromEntries(shippable.map((line) => [line.id, String(line.unshipped)])),
  );
  const [carrierName, setCarrierName] = useState("");
  const [trackingReference, setTrackingReference] = useState("");
  const [etaDate, setEtaDate] = useState("");
  const [portOfEntryId, setPortOfEntryId] = useState<number | "">("");
  const [readyOn, setReadyOn] = useState(() => localCalendarDate(new Date()));
  const [refusal, setRefusal] = useState<string | null>(null);
  const [idempotencyKey] = useState(() => commandKey(`inbound-shipment:${purchaseOrderId}`));

  const selected = shippable
    .map((line) => ({ line, quantity: Number(quantities[line.id] ?? "0") }))
    .filter(({ quantity }) => Number.isFinite(quantity) && quantity > 0);
  const overShipped = selected.some(({ line, quantity }) => quantity > line.unshipped);
  const submittable = selected.length > 0 && !overShipped;

  const importPorts = useMutation({
    mutationFn: () =>
      inboundShipmentService.importStandardSaudiPorts(commandKey("import-saudi-ports")),
    onSuccess: () => {
      toast.success("Standard Saudi entry locations loaded.");
      onPortsChanged();
    },
    onError: (error) => toast.error(errorMessage(error, "Could not load the standard locations.")),
  });

  const mutation = useMutation({
    mutationFn: () =>
      inboundShipmentService.create({
        purchaseOrderId,
        lines: selected.map(({ line, quantity }) => ({
          purchaseOrderLineId: line.id,
          quantity,
        })),
        carrierName: carrierName.trim() || null,
        trackingReference: trackingReference.trim() || null,
        etaDate: etaDate.trim() || null,
        portOfEntryId: portOfEntryId === "" ? null : portOfEntryId,
        readyAtFactoryOn: readyOn,
        idempotencyKey,
      }),
    onSuccess: (shipment) => {
      toast.success(`${shipment.shipmentNumber} recorded as ready at factory.`);
      onSaved();
    },
    onError: (error) => {
      const message = errorMessage(error, "Could not record the shipment.");
      setRefusal(message);
      toast.error(message);
    },
  });

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="md">
      <DialogTitle>Record inbound shipment · {purchaseOrderNumber}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="info">
            A shipment starts at <strong>ready at factory</strong> and moves forward from there. One
            order line can be split across several shipments; the quantities below are what is left
            unshipped on each line.
          </Alert>
          {refusal && (
            <Alert severity="error" onClose={() => setRefusal(null)}>
              {refusal}
            </Alert>
          )}

          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Line</TableCell>
                <TableCell align="right">Ordered</TableCell>
                <TableCell align="right">Already shipped</TableCell>
                <TableCell align="right">Unshipped</TableCell>
                <TableCell align="right">Ship now</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {shippable.map((line) => {
                const value = Number(quantities[line.id] ?? "0");
                return (
                  <TableRow key={line.id}>
                    <TableCell>{line.description}</TableCell>
                    <TableCell align="right">{line.orderedQuantity}</TableCell>
                    <TableCell align="right">{line.alreadyShipped}</TableCell>
                    <TableCell align="right">{line.unshipped}</TableCell>
                    <TableCell align="right" sx={{ width: 140 }}>
                      <TextField
                        size="small"
                        type="number"
                        value={quantities[line.id] ?? ""}
                        error={Number.isFinite(value) && value > line.unshipped}
                        onChange={(event) =>
                          setQuantities((current) => ({
                            ...current,
                            [line.id]: event.target.value,
                          }))
                        }
                        slotProps={{ htmlInput: { min: 0, max: line.unshipped, step: "any" } }}
                      />
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
          {overShipped && (
            <Alert severity="error">
              A line cannot be shipped for more than remains unshipped against it. The server
              refuses the write, and the purchase order line carries a database constraint that
              refuses it again.
            </Alert>
          )}

          <Divider />

          <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
            <TextField
              fullWidth
              label="Carrier or freight forwarder"
              value={carrierName}
              onChange={(event) => setCarrierName(event.target.value)}
              slotProps={{ htmlInput: { maxLength: 255 } }}
            />
            <TextField
              fullWidth
              label="Tracking reference"
              value={trackingReference}
              onChange={(event) => setTrackingReference(event.target.value)}
              helperText="Bill of lading, air waybill or container number."
              slotProps={{ htmlInput: { maxLength: 160 } }}
            />
          </Stack>

          <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
            <TextField
              fullWidth
              type="date"
              label="Ready at factory"
              value={readyOn}
              onChange={(event) => setReadyOn(event.target.value)}
              slotProps={{ inputLabel: { shrink: true } }}
            />
            <TextField
              fullWidth
              type="date"
              label="Carrier ETA"
              value={etaDate}
              onChange={(event) => setEtaDate(event.target.value)}
              helperText="Estimated arrival at the Saudi entry point. The material available date is built from this."
              slotProps={{ inputLabel: { shrink: true } }}
            />
          </Stack>

          <PortSelect
            ports={ports}
            value={portOfEntryId}
            onChange={setPortOfEntryId}
            canManagePorts={canManagePorts}
            importing={importPorts.isPending}
            onImport={() => importPorts.mutate()}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!submittable || mutation.isPending}
          onClick={() => mutation.mutate()}
        >
          {mutation.isPending ? "Recording…" : "Record shipment"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * FR-MAS-01. The milestone options come from the shipment's own `allowedNextMilestones`, which the
 * server computed. Nothing here re-implements the ladder.
 */
function MilestoneDialog({
  shipment,
  ports,
  onClose,
  onSaved,
}: {
  shipment: InboundShipment;
  ports: PortOfEntry[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const [milestone, setMilestone] = useState<InboundMilestone>(
    shipment.allowedNextMilestones[0] ?? "DEPARTED_ORIGIN",
  );
  const [occurredOn, setOccurredOn] = useState(() => localCalendarDate(new Date()));
  const [portOfEntryId, setPortOfEntryId] = useState<number | "">(shipment.portOfEntryId ?? "");
  const [etaDate, setEtaDate] = useState("");
  const [note, setNote] = useState("");
  const [refusal, setRefusal] = useState<string | null>(null);
  const [idempotencyKey] = useState(() => commandKey(`milestone:${shipment.id}`));

  const isCancellation = milestone === "CANCELLED";
  const needsPort = milestone === "ARRIVED_SAUDI_PORT";
  const isArrival = milestone === "RECEIVED_AT_WAREHOUSE";
  const submittable =
    occurredOn.trim().length > 0 &&
    (!isCancellation || note.trim().length > 0) &&
    (!needsPort || portOfEntryId !== "" || shipment.portOfEntryId != null);

  const mutation = useMutation({
    mutationFn: () =>
      inboundShipmentService.recordMilestone(shipment.id, {
        expectedVersion: shipment.version,
        milestone,
        occurredOn,
        portOfEntryId: portOfEntryId === "" ? null : portOfEntryId,
        etaDate: etaDate.trim() || null,
        note: note.trim() || null,
        idempotencyKey,
      }),
    onSuccess: (result) => {
      toast.success(
        `${result.shipmentNumber}: ${MILESTONE_LABELS[result.milestone].toLowerCase()}` +
          (result.materialAvailability.date
            ? ` · material available ${calendarDate(result.materialAvailability.date)}`
            : ""),
      );
      onSaved();
    },
    onError: (error) => {
      const message = errorMessage(error, "Could not record the milestone.");
      setRefusal(message);
      toast.error(message);
    },
  });

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Record milestone · {shipment.shipmentNumber}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="info">
            This shipment is currently{" "}
            <strong>{MILESTONE_LABELS[shipment.milestone].toLowerCase()}</strong>. Milestones only
            move forward — a step that did not happen (a domestic shipment never reaches a port) can
            be skipped, but nothing can be undone.
          </Alert>
          {refusal && (
            <Alert severity="error" onClose={() => setRefusal(null)}>
              {refusal}
            </Alert>
          )}

          {/*
            The Gate 4/Gate 5 seam, stated where somebody is about to walk into it. This milestone
            records that the goods reached the warehouse. It does not book them into stock, create
            material lots or move quantity on hand — only a goods receipt does that, because only a
            person at the dock can count what actually arrived and name the batch or serials.
          */}
          {isArrival && (
            <Alert severity="warning">
              This records that the goods <strong>reached</strong> the warehouse. It does not book
              them into stock: post a goods receipt against {shipment.purchaseOrderNumber} to count
              what arrived, record the lot or serial numbers, and move quantity on hand. Until then
              the shipment shows as <strong>not booked in</strong>.
            </Alert>
          )}

          <FormControl fullWidth>
            <InputLabel id={`milestone-${shipment.id}`}>Milestone</InputLabel>
            <Select
              labelId={`milestone-${shipment.id}`}
              label="Milestone"
              value={milestone}
              onChange={(event) => {
                setMilestone(event.target.value as InboundMilestone);
                setRefusal(null);
              }}
            >
              {shipment.allowedNextMilestones.map((option) => (
                <MenuItem key={option} value={option}>
                  {MILESTONE_LABELS[option]}
                </MenuItem>
              ))}
            </Select>
          </FormControl>

          <TextField
            fullWidth
            required
            type="date"
            label="Happened on"
            value={occurredOn}
            onChange={(event) => setOccurredOn(event.target.value)}
            helperText="Cannot be in the future, or before the last recorded milestone."
            slotProps={{ inputLabel: { shrink: true } }}
          />

          {needsPort && (
            <PortSelect
              ports={ports}
              value={portOfEntryId}
              onChange={setPortOfEntryId}
              required
              helperText="Required on arrival. Reporting clearance performance by location is the reason this is a controlled list rather than free text."
            />
          )}

          {!isCancellation && (
            <TextField
              fullWidth
              type="date"
              label="Revised ETA (optional)"
              value={etaDate}
              onChange={(event) => setEtaDate(event.target.value)}
              slotProps={{ inputLabel: { shrink: true } }}
            />
          )}

          <TextField
            fullWidth
            multiline
            minRows={2}
            required={isCancellation}
            label={isCancellation ? "Reason for cancelling" : "Note (optional)"}
            value={note}
            onChange={(event) => setNote(event.target.value)}
            helperText={
              isCancellation
                ? "The quantity on this shipment goes back to the purchase order line. Nothing else records why."
                : undefined
            }
            slotProps={{ htmlInput: { maxLength: 1000 } }}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          color={isCancellation ? "error" : "primary"}
          disabled={!submittable || mutation.isPending}
          onClick={() => mutation.mutate()}
        >
          {mutation.isPending ? "Recording…" : "Record milestone"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * FR-MAS-02, manual path. There is no carrier API behind this: the Phase 1 carrier subset (decision
 * D4) is undecided, and a fabricated integration would be a mock in a production path.
 */
function TrackingDialog({
  shipment,
  onClose,
  onSaved,
}: {
  shipment: InboundShipment;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [carrierName, setCarrierName] = useState(shipment.carrierName ?? "");
  const [trackingReference, setTrackingReference] = useState(shipment.trackingReference ?? "");
  const [etaDate, setEtaDate] = useState(shipment.etaDate?.slice(0, 10) ?? "");
  const [clearEta, setClearEta] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [idempotencyKey] = useState(() => commandKey(`tracking:${shipment.id}`));

  const mutation = useMutation({
    mutationFn: () =>
      inboundShipmentService.updateTracking(shipment.id, {
        expectedVersion: shipment.version,
        carrierName: carrierName.trim() || null,
        trackingReference: trackingReference.trim() || null,
        etaDate: clearEta ? null : etaDate.trim() || null,
        clearEta,
        idempotencyKey,
      }),
    onSuccess: (result) => {
      toast.success(`${result.shipmentNumber} updated.`);
      onSaved();
    },
    onError: (error) => {
      const message = errorMessage(error, "Could not update the shipment.");
      setRefusal(message);
      toast.error(message);
    },
  });

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Carrier and ETA · {shipment.shipmentNumber}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="info">
            Updates are keyed in from what the carrier or freight forwarder told you. Nexora has no
            live carrier feed, so nothing here refreshes on its own.
          </Alert>
          {refusal && (
            <Alert severity="error" onClose={() => setRefusal(null)}>
              {refusal}
            </Alert>
          )}
          <TextField
            fullWidth
            label="Carrier or freight forwarder"
            value={carrierName}
            onChange={(event) => setCarrierName(event.target.value)}
            slotProps={{ htmlInput: { maxLength: 255 } }}
          />
          <TextField
            fullWidth
            label="Tracking reference"
            value={trackingReference}
            onChange={(event) => setTrackingReference(event.target.value)}
            slotProps={{ htmlInput: { maxLength: 160 } }}
          />
          <TextField
            fullWidth
            type="date"
            label="ETA"
            value={etaDate}
            disabled={clearEta}
            onChange={(event) => setEtaDate(event.target.value)}
            helperText="The material available date is derived from this until the shipment actually arrives."
            slotProps={{ inputLabel: { shrink: true } }}
          />
          <Button size="small" onClick={() => setClearEta((current) => !current)}>
            {clearEta ? "Keep the ETA" : "Withdraw the ETA"}
          </Button>
          {clearEta && (
            <Alert severity="warning">
              Withdrawing the ETA stops the material available date being derived for this shipment
              until a new one is recorded. That is the honest state when a forwarder no longer stands
              behind a date.
            </Alert>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={mutation.isPending} onClick={() => mutation.mutate()}>
          {mutation.isPending ? "Saving…" : "Save"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/** FR-MAS-01. The named entry-point picker, driven by the tenant's own governed master. */
function PortSelect({
  ports,
  value,
  onChange,
  canManagePorts = false,
  importing = false,
  onImport,
  required = false,
  helperText,
}: {
  ports: PortOfEntry[];
  value: number | "";
  onChange: (value: number | "") => void;
  canManagePorts?: boolean;
  importing?: boolean;
  onImport?: () => void;
  required?: boolean;
  helperText?: string;
}) {
  const grouped = useMemo(() => {
    const map = new Map<PortOfEntry["kind"], PortOfEntry[]>();
    for (const port of ports) {
      map.set(port.kind, [...(map.get(port.kind) ?? []), port]);
    }
    return [...map.entries()];
  }, [ports]);

  if (ports.length === 0) {
    return (
      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="body2" color="text.secondary" gutterBottom>
          No entry locations are set up for this business unit yet, so an arrival cannot name one.
        </Typography>
        {canManagePorts && onImport ? (
          <Button size="small" variant="outlined" disabled={importing} onClick={onImport}>
            {importing ? "Loading…" : "Load the standard Saudi entry locations"}
          </Button>
        ) : (
          <Typography variant="caption" color="text.secondary">
            Ask an administrator to add them.
          </Typography>
        )}
      </Paper>
    );
  }

  return (
    <FormControl fullWidth required={required}>
      <InputLabel id="port-of-entry-label">Entry location</InputLabel>
      <Select<number | "">
        labelId="port-of-entry-label"
        label="Entry location"
        value={value}
        onChange={(event) =>
          onChange(event.target.value === "" ? "" : Number(event.target.value))
        }
      >
        <MenuItem value="">
          <em>Not stated</em>
        </MenuItem>
        {grouped.flatMap(([kind, kindPorts]) =>
          kindPorts.map((port) => (
            <MenuItem key={port.id} value={port.id}>
              <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}>
                {portIcon(kind)}
                <span>
                  {port.name}
                  {port.city ? ` · ${port.city}` : ""} ({PORT_KIND_LABELS[kind]})
                </span>
              </Stack>
            </MenuItem>
          )),
        )}
      </Select>
      {helperText && (
        <Typography variant="caption" color="text.secondary" sx={{ mt: 0.5 }}>
          {helperText}
        </Typography>
      )}
    </FormControl>
  );
}
