import React, { useState } from 'react';
import { isAxiosError } from 'axios';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, AlertTitle, Box, Button, Card, CardContent, Chip, Dialog, DialogActions,
  CircularProgress, DialogContent, DialogTitle, Divider, MenuItem, Stack, Table, TableBody, TableCell,
  TableHead, TableRow, TextField, Typography,
} from '@mui/material';
import dayjs from 'dayjs';
import deliveryService, {
  CANNOT_CANCEL_REASON, DELIVERY_EXCEPTION_REASONS, DELIVERY_STATUS_LABEL, OPERATOR_TRANSITIONS,
  SHORTFALL_DECISIONS, type ConfirmDeliveryLineCommand, type DeliveryStatus,
} from '../../../api/services/deliveryService';
import type { ShipmentItemDTO } from '../../../api/services/shipmentService';

/**
 * Gate 7 — FR-DLM-03, FR-DLM-05 and FR-DLM-07 at the shipment.
 *
 * The two dashed signature boxes on the printed delivery note captured nothing back into the
 * system. This is where the signed copy becomes a record: who took receipt, when, where, what they
 * actually accepted, and — when they accepted less than arrived — why, and what the business
 * decided to do about it commercially.
 *
 * Three things this deliberately does not offer, per decision R22 and the narrowed FR-DLM-07:
 * no vehicle, driver or route; no live tracking; and no claims, liability or corrective-action
 * workflow. Damage in transit is the carrier's process, run outside this system.
 */

interface Props {
  shipmentId: number;
  orderId: number;
  deliveryStatus: string;
  items: ShipmentItemDTO[];
  canEdit: boolean;
  /**
   * Whether this user may decide a shortfall (re-supply or credit). The server gates
   * POST /api/delivery/shortfalls/{id}/decision on Shipments:Edit AND Orders:Edit, because the
   * decision commits the order commercially, not only the despatch. A warehouse role that edits
   * shipments but not orders used to see the button and get a 403 after typing a reason.
   * Defaults to canEdit for callers that have not resolved the second permission.
   */
  canDecide?: boolean;
}

const serverMessage = (error: unknown, fallback: string): string => {
  const detail = (error as { response?: { data?: { detail?: string; message?: string } } })
    ?.response?.data;
  // The server's own words, verbatim. The client invents no copy that could contradict them.
  return detail?.detail ?? detail?.message ?? fallback;
};

const responseStatus = (error: unknown) => isAxiosError(error) ? error.response?.status : undefined;
const resultIsUncertain = (error: unknown) => {
  const status = responseStatus(error);
  return status == null || status >= 500;
};

const DeliveryConfirmationPanel: React.FC<Props> = ({ shipmentId, orderId, deliveryStatus, items, canEdit, canDecide = canEdit }) => {
  const queryClient = useQueryClient();
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [decisionFor, setDecisionFor] = useState<number | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const proofQuery = useQuery({
    queryKey: ['delivery-confirmation', shipmentId],
    queryFn: () => deliveryService.getConfirmation(shipmentId),
    enabled: shipmentId > 0,
    // Absence (404) and outage are different operator states below. Neither should auto-retry and
    // silently move the action while somebody is deciding whether it is safe to record a POD.
    retry: false,
  });
  const proof = proofQuery.data;
  const proofNotRecorded = (proofQuery.isSuccess && !proof)
    || (proofQuery.isError && responseStatus(proofQuery.error) === 404);
  const proofReadFailed = proofQuery.isError && !proofNotRecorded;

  const ledgerQuery = useQuery({
    queryKey: ['delivered-quantities', orderId],
    queryFn: () => deliveryService.getDeliveredQuantities(orderId),
    enabled: orderId > 0,
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['delivery-confirmation', shipmentId] });
    queryClient.invalidateQueries({ queryKey: ['delivered-quantities', orderId] });
    queryClient.invalidateQueries({ queryKey: ['shipment-details', String(shipmentId)] });
    queryClient.invalidateQueries({ queryKey: ['delivery-note', shipmentId] });
  };

  const transition = useMutation({
    mutationFn: (status: DeliveryStatus) => deliveryService.transition(shipmentId, status),
    onSuccess: () => { setActionError(null); invalidate(); },
    onError: (error) => setActionError(serverMessage(error, 'The status change was refused.')),
  });

  const allowed = OPERATOR_TRANSITIONS[deliveryStatus] ?? [];
  const canConfirm = deliveryStatus === 'DISPATCHED' || deliveryStatus === 'IN_TRANSIT';
  // The "Mark Cancelled" button used to sit here for both of these states and reversed nothing.
  // Its absence is explained rather than left to look like a missing feature.
  const cancellationRefused = canConfirm;

  return (
    <Card sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', mb: 2 }}>
      <CardContent>
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 2 }}>
          <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>DELIVERY</Typography>
          <Chip
            size="small"
            label={DELIVERY_STATUS_LABEL[deliveryStatus] ?? deliveryStatus}
            color={
              deliveryStatus === 'DELIVERED' ? 'success'
                : deliveryStatus === 'DELIVERY_EXCEPTION' ? 'warning'
                  : deliveryStatus === 'CANCELLED' ? 'default' : 'primary'
            }
            sx={{ fontWeight: 700 }}
          />
        </Stack>

        {actionError && (
          <Alert severity="error" sx={{ mb: 2 }} onClose={() => setActionError(null)}>{actionError}</Alert>
        )}

        {proofQuery.isLoading && (
          <Alert severity="info" icon={<CircularProgress size={18} />} sx={{ mb: 2 }}>
            Checking whether proof of delivery has already been recorded…
          </Alert>
        )}
        {proofReadFailed && (
          <Alert severity="error" sx={{ mb: 2 }} action={
            <Button color="inherit" size="small" onClick={() => void proofQuery.refetch()}>
              Retry
            </Button>
          }>
            <AlertTitle>Proof-of-delivery status could not be verified</AlertTitle>
            Recording is locked until the current state can be read. An outage must not look like
            an unconfirmed shipment.
          </Alert>
        )}
        {proofNotRecorded && canConfirm && (
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
            No proof of delivery is recorded for this shipment yet.
          </Typography>
        )}

        <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', mb: 2 }}>
          {canEdit && allowed.map(status => (
            <Button
              key={status}
              size="small"
              variant="outlined"
              disabled={transition.isPending}
              onClick={() => transition.mutate(status)}
            >
              Mark {DELIVERY_STATUS_LABEL[status]}
            </Button>
          ))}
          {canEdit && canConfirm && proofNotRecorded && (
            <Button size="small" variant="contained" onClick={() => setConfirmOpen(true)}>
              Record proof of delivery
            </Button>
          )}
          {allowed.length === 0 && !canConfirm && (
            <Typography variant="caption" color="text.secondary">
              This shipment has reached a terminal state. Re-supply raises a new shipment against
              the outstanding quantity.
            </Typography>
          )}
        </Stack>

        {canEdit && cancellationRefused && (
          <Alert severity="info" sx={{ mb: 2 }}>
            <AlertTitle sx={{ fontWeight: 700 }}>This delivery cannot be cancelled</AlertTitle>
            {CANNOT_CANCEL_REASON}
          </Alert>
        )}

        {/* FR-DLM-02. Despatched and accepted are two numbers, and both are shown. */}
        {ledgerQuery.isLoading && (
          <Alert severity="info" icon={<CircularProgress size={18} />} sx={{ mb: 2 }}>
            Loading cumulative delivery quantities…
          </Alert>
        )}
        {ledgerQuery.isError && (
          <Alert severity="error" sx={{ mb: 2 }} action={
            <Button color="inherit" size="small" onClick={() => void ledgerQuery.refetch()}>
              Retry
            </Button>
          }>
            Cumulative delivery quantities could not be loaded. No quantity has been assumed to be zero.
          </Alert>
        )}
        {ledgerQuery.isSuccess && ledgerQuery.data.length === 0 && (
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
            No cumulative delivery quantities are recorded for this order yet.
          </Typography>
        )}
        {ledgerQuery.data && ledgerQuery.data.length > 0 && (
          <>
            <Divider sx={{ my: 2 }} />
            <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.secondary' }}>
              CUMULATIVE ACROSS EVERY DELIVERY ON THIS ORDER
            </Typography>
            <Table size="small" sx={{ mt: 1 }}>
              <TableHead>
                <TableRow>
                  <TableCell sx={{ fontWeight: 800 }}>Order line</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Awarded</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Despatched</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Awaiting confirmation</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Accepted</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Not accepted</TableCell>
                  <TableCell sx={{ fontWeight: 800 }} align="right">Outstanding</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {ledgerQuery.data.map(line => (
                  <TableRow key={line.orderItemId}>
                    <TableCell>{line.orderItemId}</TableCell>
                    <TableCell align="right">{line.awardedQuantity}</TableCell>
                    <TableCell align="right">{line.despatchedQuantity}</TableCell>
                    <TableCell align="right">{line.awaitingConfirmationQuantity}</TableCell>
                    <TableCell align="right" sx={{ fontWeight: 700 }}>{line.acceptedQuantity}</TableCell>
                    <TableCell align="right" sx={{ color: line.refusedQuantity > 0 ? 'warning.main' : undefined }}>
                      {line.refusedQuantity}
                    </TableCell>
                    <TableCell align="right">{line.outstandingQuantity}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
              Only ACCEPTED units are invoiceable. Despatched units awaiting confirmation are with
              the carrier, not with the customer.
            </Typography>
          </>
        )}

        {proof && (
          <>
            <Divider sx={{ my: 2 }} />
            <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.secondary' }}>
              PROOF OF DELIVERY
            </Typography>
            <Stack spacing={0.5} sx={{ mt: 1 }}>
              <Typography variant="body2">
                Received by <strong>{proof.receivedByName}</strong>
                {proof.receivedByPosition ? ` (${proof.receivedByPosition})` : ''} on{' '}
                {dayjs(proof.receivedOn).format('DD MMM YYYY HH:mm')}
              </Typography>
              <Typography variant="body2" color="text.secondary">
                {proof.hasGpsFix
                  ? `Signed at ${proof.gpsLatitude}, ${proof.gpsLongitude}${proof.gpsAccuracyMeters ? ` (±${proof.gpsAccuracyMeters} m)` : ''}`
                  : 'No GPS fix was captured at signature.'}
              </Typography>
              <Typography variant="body2" color="text.secondary">
                Recorded by {proof.recordedBy} on {dayjs(proof.recordedOn).format('DD MMM YYYY HH:mm')}.
                This record cannot be edited.
              </Typography>
            </Stack>

            {proof.lines.some(l => l.refusedQuantity > 0) && (
              <Box sx={{ mt: 2 }}>
                <Alert severity="warning" sx={{ mb: 1 }}>
                  <AlertTitle>The customer did not accept everything on this note</AlertTitle>
                  The unaccepted units are not invoiceable and the order line is not fulfilled.
                  Record whether the balance will be re-supplied or credited.
                </Alert>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell sx={{ fontWeight: 800 }}>Item</TableCell>
                      <TableCell sx={{ fontWeight: 800 }} align="right">Despatched</TableCell>
                      <TableCell sx={{ fontWeight: 800 }} align="right">Accepted</TableCell>
                      <TableCell sx={{ fontWeight: 800 }}>Reason</TableCell>
                      <TableCell sx={{ fontWeight: 800 }}>Decision</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {proof.lines.filter(l => l.refusedQuantity > 0).map(l => (
                      <TableRow key={l.id}>
                        <TableCell>{l.productName ?? `Line ${l.shipmentItemId}`}</TableCell>
                        <TableCell align="right">{l.despatchedQuantity}</TableCell>
                        <TableCell align="right">{l.acceptedQuantity}</TableCell>
                        <TableCell>{l.exceptionReasonCode}{l.exceptionNote ? ` — ${l.exceptionNote}` : ''}</TableCell>
                        <TableCell>
                          {l.commercialDecision ? (
                            <Typography variant="caption">
                              <strong>{l.commercialDecision}</strong> — {l.commercialDecisionReason}
                              <br />
                              {l.commercialDecisionBy} on {dayjs(l.commercialDecisionOn).format('DD MMM YYYY')}
                            </Typography>
                          ) : canDecide ? (
                            <Button size="small" onClick={() => setDecisionFor(l.id)}>Decide</Button>
                          ) : canEdit ? (
                            <Typography variant="caption" color="text.secondary">
                              Awaiting authorized decision — deciding a shortfall needs Orders edit permission as well as Shipments edit.
                            </Typography>
                          ) : (
                            <Typography variant="caption" color="text.secondary">Awaiting authorized decision</Typography>
                          )}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </Box>
            )}
          </>
        )}
      </CardContent>

      {canEdit && confirmOpen && (
        <ConfirmDeliveryDialog
          shipmentId={shipmentId}
          items={items}
          onClose={() => setConfirmOpen(false)}
          onRecorded={() => { setConfirmOpen(false); invalidate(); }}
        />
      )}
      {canDecide && decisionFor !== null && (
        <ShortfallDecisionDialog
          proofLineId={decisionFor}
          onClose={() => setDecisionFor(null)}
          onRecorded={() => { setDecisionFor(null); invalidate(); }}
        />
      )}
    </Card>
  );
};

/**
 * FR-DLM-03. The capture form.
 *
 * Every line must be answered. There is no "accept all" shortcut that silently fills the value the
 * operator never gave — the accepted quantity is what an invoice is raised against, so a default
 * nobody looked at is a bill nobody checked. The quantities are pre-filled with what was
 * despatched, which is the common case and is still a value the operator sees and can change.
 */
const ConfirmDeliveryDialog: React.FC<{
  shipmentId: number;
  items: ShipmentItemDTO[];
  onClose: () => void;
  onRecorded: () => void;
}> = ({ shipmentId, items, onClose, onRecorded }) => {
  const [receivedByName, setReceivedByName] = useState('');
  const [receivedByPosition, setReceivedByPosition] = useState('');
  const [receivedByContact, setReceivedByContact] = useState('');
  const [receivedOn, setReceivedOn] = useState(dayjs().format('YYYY-MM-DDTHH:mm'));
  const [notes, setNotes] = useState('');
  const [lines, setLines] = useState<ConfirmDeliveryLineCommand[]>(
    items.map(i => ({
      shipmentItemId: i.id ?? 0,
      acceptedQuantity: i.quantity,
      exceptionReasonCode: null,
      exceptionNote: null,
      notes: null,
    })));
  const [evidence, setEvidence] = useState<{
    signature?: number; stamp?: number; photo?: number;
  }>({});
  const [gps, setGps] = useState<{ lat: number; lon: number; acc?: number; at: string } | null>(null);
  const [gpsMessage, setGpsMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [operationKey, setOperationKey] = useState(
    () => `pod-${shipmentId}-${globalThis.crypto?.randomUUID?.() ?? Date.now()}`);
  const [pendingOperation, setPendingOperation] = useState<{
    key: string;
    command: Parameters<typeof deliveryService.confirm>[2];
  } | null>(null);
  const [uncertainResult, setUncertainResult] = useState(false);

  const upload = useMutation({
    mutationFn: ({ kind, file }: { kind: 'SIGNATURE' | 'STAMP' | 'PHOTO'; file: File }) =>
      deliveryService.captureEvidence(shipmentId, kind, file),
  });

  const confirm = useMutation({
    mutationFn: (operation: NonNullable<typeof pendingOperation>) =>
      deliveryService.confirm(shipmentId, operation.key, operation.command),
    onSuccess: onRecorded,
    onError: (e) => {
      if (resultIsUncertain(e)) {
        setUncertainResult(true);
        setError('The result is uncertain. The request may already have recorded this proof of delivery. Check the current status or retry safely with the identical command.');
        return;
      }
      setPendingOperation(null);
      setUncertainResult(false);
      setOperationKey(`pod-${shipmentId}-${globalThis.crypto?.randomUUID?.() ?? Date.now()}`);
      setError(serverMessage(e, 'The delivery confirmation was refused.'));
    },
  });

  const checkStatus = useMutation({
    mutationFn: () => deliveryService.getConfirmation(shipmentId),
    onSuccess: onRecorded,
    onError: (e) => setError(responseStatus(e) === 404
      ? 'No proof is recorded yet. Retry safely to replay the unchanged command.'
      : serverMessage(e, 'The current proof-of-delivery status could not be checked.')),
  });

  const submit = () => {
    if (pendingOperation) {
      confirm.mutate(pendingOperation);
      return;
    }
    const operation = {
      key: operationKey,
      command: {
        receivedByName,
        receivedByPosition: receivedByPosition || null,
        receivedByContact: receivedByContact || null,
        receivedOn: dayjs(receivedOn).toISOString(),
        signatureEvidenceId: evidence.signature ?? null,
        stampEvidenceId: evidence.stamp ?? null,
        photoEvidenceId: evidence.photo ?? null,
        gpsLatitude: gps?.lat ?? null,
        gpsLongitude: gps?.lon ?? null,
        gpsAccuracyMeters: gps?.acc ?? null,
        gpsCapturedOn: gps?.at ?? null,
        notes: notes || null,
        lines,
      },
    };
    setPendingOperation(operation);
    confirm.mutate(operation);
  };

  const formFrozen = confirm.isPending || uncertainResult || checkStatus.isPending;
  const closeLocked = formFrozen || upload.isPending;
  const closeIfSafe = () => {
    if (!closeLocked) onClose();
  };

  const setLine = (shipmentItemId: number, patch: Partial<ConfirmDeliveryLineCommand>) =>
    setLines(prev => prev.map(l => l.shipmentItemId === shipmentItemId ? { ...l, ...patch } : l));

  const captureGps = () => {
    if (!navigator.geolocation) {
      setGpsMessage('This device does not expose a location. The POD will record no fix.');
      return;
    }
    navigator.geolocation.getCurrentPosition(
      position => {
        setGps({
          lat: Number(position.coords.latitude.toFixed(7)),
          lon: Number(position.coords.longitude.toFixed(7)),
          acc: position.coords.accuracy ? Number(position.coords.accuracy.toFixed(2)) : undefined,
          at: new Date(position.timestamp).toISOString(),
        });
        setGpsMessage(null);
      },
      err => setGpsMessage(`No location captured: ${err.message}. The POD will record no fix.`),
      { enableHighAccuracy: true, timeout: 10000 });
  };

  const uploadFor = async (kind: 'SIGNATURE' | 'STAMP' | 'PHOTO', file: File | undefined) => {
    if (!file) return;
    try {
      const result = await upload.mutateAsync({ kind, file });
      setEvidence(prev => ({
        ...prev,
        [kind.toLowerCase() as 'signature' | 'stamp' | 'photo']: result.attachmentId,
      }));
      setError(null);
    } catch (e) {
      setError(serverMessage(e, 'The evidence was refused.'));
    }
  };

  return (
    <Dialog open fullWidth maxWidth="md" onClose={closeIfSafe}>
      <DialogTitle>Record proof of delivery</DialogTitle>
      <DialogContent dividers>
        {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}

        <Box component="fieldset" disabled={formFrozen}
          sx={{ border: 0, p: 0, m: 0, minWidth: 0 }}>
        <Stack spacing={2}>
          <TextField
            label="Received by (name)"
            required
            value={receivedByName}
            onChange={e => setReceivedByName(e.target.value)}
            helperText="Required. An unsigned delivery note is not proof of anything."
          />
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
            <TextField label="Position or department" fullWidth value={receivedByPosition}
              onChange={e => setReceivedByPosition(e.target.value)} />
            <TextField label="Contact (phone or email)" fullWidth value={receivedByContact}
              onChange={e => setReceivedByContact(e.target.value)} />
            <TextField
              label="Received on" type="datetime-local" fullWidth value={receivedOn}
              onChange={e => setReceivedOn(e.target.value)}
              slotProps={{ inputLabel: { shrink: true } }}
              helperText="When the goods changed hands, not when this was keyed in."
            />
          </Stack>

          <Divider />
          <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>What the customer accepted</Typography>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell sx={{ fontWeight: 800 }}>Item</TableCell>
                <TableCell sx={{ fontWeight: 800 }} align="right">Despatched</TableCell>
                <TableCell sx={{ fontWeight: 800 }} align="right">Accepted</TableCell>
                <TableCell sx={{ fontWeight: 800 }}>Reason (required if short)</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {items.map(item => {
                const line = lines.find(l => l.shipmentItemId === item.id);
                const isShort = (line?.acceptedQuantity ?? 0) < item.quantity;
                return (
                  <TableRow key={item.id}>
                    <TableCell>{item.productName}</TableCell>
                    <TableCell align="right">{item.quantity}</TableCell>
                    <TableCell align="right" sx={{ width: 140 }}>
                      <TextField
                        size="small" type="number"
                        slotProps={{ htmlInput: { min: 0, max: item.quantity, step: 'any' } }}
                        value={line?.acceptedQuantity ?? 0}
                        onChange={e => setLine(item.id ?? 0, {
                          acceptedQuantity: e.target.value === '' ? 0 : Number(e.target.value),
                        })}
                      />
                    </TableCell>
                    <TableCell sx={{ minWidth: 260 }}>
                      <TextField
                        select size="small" fullWidth
                        disabled={!isShort}
                        value={line?.exceptionReasonCode ?? ''}
                        onChange={e => setLine(item.id ?? 0, {
                          exceptionReasonCode: e.target.value || null,
                        })}
                      >
                        <MenuItem value="">{isShort ? 'Select a reason' : 'Accepted in full'}</MenuItem>
                        {DELIVERY_EXCEPTION_REASONS.map(r => (
                          <MenuItem key={r.code} value={r.code}>{r.label}</MenuItem>
                        ))}
                      </TextField>
                      {isShort && (
                        <TextField
                          size="small" fullWidth sx={{ mt: 1 }} placeholder="What happened"
                          value={line?.exceptionNote ?? ''}
                          onChange={e => setLine(item.id ?? 0, { exceptionNote: e.target.value || null })}
                        />
                      )}
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>

          <Divider />
          <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>Evidence</Typography>
          <Typography variant="caption" color="text.secondary">
            Optional, and each absence is recorded as an absence. Files are scanned and stored in the
            same immutable evidence store as every other document.
          </Typography>
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
            {(['SIGNATURE', 'STAMP', 'PHOTO'] as const).map(kind => {
              const key = kind.toLowerCase() as 'signature' | 'stamp' | 'photo';
              return (
                <Button key={kind} component="label" variant="outlined" size="small"
                  disabled={upload.isPending}>
                  {evidence[key] ? `${kind} captured` : `Attach ${kind.toLowerCase()}`}
                  <input hidden type="file" accept="image/*,application/pdf"
                    onChange={e => uploadFor(kind, e.target.files?.[0])} />
                </Button>
              );
            })}
          </Stack>

          <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
            <Button variant="outlined" size="small" onClick={captureGps}>
              Stamp location at signature
            </Button>
            <Typography variant="caption" color="text.secondary">
              {gps
                ? `${gps.lat}, ${gps.lon}${gps.acc ? ` (±${gps.acc} m)` : ''}`
                : gpsMessage ?? 'No fix captured. One coordinate, taken once, at signature.'}
            </Typography>
          </Stack>

          <TextField label="Notes" multiline minRows={2} value={notes}
            onChange={e => setNotes(e.target.value)} />
        </Stack>
        </Box>
      </DialogContent>
      <DialogActions>
        {uncertainResult && (
          <Button disabled={checkStatus.isPending || confirm.isPending}
            onClick={() => checkStatus.mutate()}>
            Check current status
          </Button>
        )}
        <Button disabled={closeLocked} onClick={closeIfSafe}>Cancel</Button>
        <Button
          variant="contained"
          disabled={confirm.isPending || checkStatus.isPending || upload.isPending || !receivedByName.trim()}
          onClick={submit}
        >
          {uncertainResult ? 'Retry safely' : 'Record proof of delivery'}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

/** FR-DLM-07, commercial half. Two answers, a mandatory reason, and no workflow. */
const ShortfallDecisionDialog: React.FC<{
  proofLineId: number;
  onClose: () => void;
  onRecorded: () => void;
}> = ({ proofLineId, onClose, onRecorded }) => {
  const [decision, setDecision] = useState<'RESUPPLY' | 'CREDIT' | ''>('');
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);

  const record = useMutation({
    mutationFn: () => deliveryService.recordShortfallDecision(
      proofLineId, decision as 'RESUPPLY' | 'CREDIT', reason),
    onSuccess: onRecorded,
    onError: (e) => setError(serverMessage(e, 'The decision was refused.')),
  });

  return (
    <Dialog open fullWidth maxWidth="sm" onClose={onClose}>
      <DialogTitle>Decide what happens about the shortfall</DialogTitle>
      <DialogContent dividers>
        {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          This records the commercial outcome only. Any claim against the carrier is handled outside
          this system. The decision is recorded once and cannot be edited.
        </Typography>
        <Stack spacing={2}>
          <TextField select label="Decision" value={decision}
            onChange={e => setDecision(e.target.value as 'RESUPPLY' | 'CREDIT')}>
            {SHORTFALL_DECISIONS.map(d => <MenuItem key={d.code} value={d.code}>{d.label}</MenuItem>)}
          </TextField>
          <TextField
            label="Reason" multiline minRows={3} required value={reason}
            onChange={e => setReason(e.target.value)}
            helperText="Required. A decision with no reason behind it tells the next person reading the case nothing."
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={!decision || !reason.trim() || record.isPending}
          onClick={() => record.mutate()}>
          Record decision
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default DeliveryConfirmationPanel;
