import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import {
  Alert, AlertTitle, Box, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle, Divider,
  FormControl, InputLabel, MenuItem, Paper, Select, Stack, Table, TableBody, TableCell, TableHead,
  TableRow, TextField, Typography,
} from '@mui/material';
import { useAuth } from '../../../context/AuthContext';
import materialTraceabilityService, {
  CERTIFICATE_TYPES, QUARANTINE_REASON_CODES,
  type CertificateType, type LotWhereFromDTO, type MaterialTraceGapDTO, type QuarantineReasonCode,
} from '../../../api/services/materialTraceabilityService';
import { PageShell, QueryState, ResponsiveTable, formatDateTime } from '../../SalesManagement/CommercialPagePrimitives';
import ApiErrorNotice from '../../../components/common/ApiErrorNotice';

/**
 * FR-MTR-02/03/05. One lot: where it came from, what paperwork it holds, where it went, and the
 * quarantine controls.
 *
 * The gap panel is deliberately at the top and is never collapsed away. A trace screen that only
 * shows what it knows teaches the reader that what it shows is complete, and that is the exact
 * mistake this product has already paid for twice.
 */

const commandKey = (prefix: string) => `${prefix}:${crypto.randomUUID()}`;

function GapPanel({ gaps }: { gaps: MaterialTraceGapDTO[] }) {
  if (!gaps.length) return null;
  return (
    <Alert severity="warning" sx={{ mb: 2.5 }}>
      <AlertTitle>Traceability gaps ({gaps.length})</AlertTitle>
      <Stack spacing={0.75}>
        {gaps.map((gap) => (
          <Typography variant="body2" key={`${gap.kind}:${gap.subject}:${gap.subjectId}`}>
            <strong>{gap.kind}</strong> — {gap.reference}
            {gap.quantity != null ? ` (${gap.quantity})` : ''}: {gap.detail}
          </Typography>
        ))}
      </Stack>
    </Alert>
  );
}

function Field({ label, value }: { label: string; value?: string | number | null }) {
  return (
    <Box sx={{ minWidth: 0 }}>
      <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>{label}</Typography>
      <Typography variant="body2" sx={{ overflowWrap: 'anywhere' }}>{value ?? '—'}</Typography>
    </Box>
  );
}

function QuarantineDialog({ lot, onClose, onSaved }: { lot: LotWhereFromDTO; onClose: () => void; onSaved: () => void }) {
  const [reasonCode, setReasonCode] = useState<QuarantineReasonCode>('SUPPLIER_RECALL');
  const [reason, setReason] = useState('');
  const [idempotencyKey] = useState(() => commandKey(`quarantine-lot:${lot.materialLotId}`));

  const mutation = useMutation({
    mutationFn: () => materialTraceabilityService.quarantineLot(lot.materialLotId, {
      expectedVersion: lot.version, reasonCode, reason, idempotencyKey,
    }),
    onSuccess: () => { onSaved(); onClose(); },
  });

  return (
    <Dialog open fullWidth maxWidth="sm" onClose={onClose}>
      <DialogTitle>Quarantine lot {lot.lotNumber}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Alert severity="warning">
            {lot.quantityRemaining} unit(s) stop being available to promise immediately. Stock already
            held for a customer order is released back and listed afterwards, so those orders can be
            re-sourced.
          </Alert>
          {mutation.isError && <ApiErrorNotice error={mutation.error} fallbackMessage="The lot could not be quarantined." />}
          <FormControl size="small" fullWidth>
            <InputLabel id="quarantine-reason-code">Reason code</InputLabel>
            <Select
              labelId="quarantine-reason-code"
              label="Reason code"
              value={reasonCode}
              onChange={(event) => setReasonCode(event.target.value as QuarantineReasonCode)}
            >
              {QUARANTINE_REASON_CODES.map((code) => <MenuItem key={code} value={code}>{code.replace(/_/g, ' ')}</MenuItem>)}
            </Select>
          </FormControl>
          <TextField
            label="Why this lot is being held"
            required
            multiline
            minRows={3}
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            helperText="Recorded against the lot and kept. A hold nobody can explain later is not a control."
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          color="error"
          disabled={!reason.trim() || mutation.isPending}
          onClick={() => mutation.mutate()}
        >
          Quarantine
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function ReleaseDialog({ lot, onClose, onSaved }: { lot: LotWhereFromDTO; onClose: () => void; onSaved: () => void }) {
  const [reason, setReason] = useState('');
  const [idempotencyKey] = useState(() => commandKey(`release-lot:${lot.materialLotId}`));

  const mutation = useMutation({
    mutationFn: () => materialTraceabilityService.releaseLot(lot.materialLotId, {
      expectedVersion: lot.version, reason, idempotencyKey,
    }),
    onSuccess: () => { onSaved(); onClose(); },
  });

  return (
    <Dialog open fullWidth maxWidth="sm" onClose={onClose}>
      <DialogTitle>Release lot {lot.lotNumber} from quarantine</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Alert severity="info">
            Held since {formatDateTime(lot.quarantinedOn)} by {lot.quarantinedBy ?? 'unknown'} —{' '}
            {lot.quarantineReasonCode}: {lot.quarantineReason}
          </Alert>
          {mutation.isError && <ApiErrorNotice error={mutation.error} fallbackMessage="The lot could not be released." />}
          <TextField
            label="Authority for this release"
            required
            multiline
            minRows={3}
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            helperText="Who authorised it and on what evidence. Recorded against the lot and in the audit ledger."
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={!reason.trim() || mutation.isPending} onClick={() => mutation.mutate()}>
          Release
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function CertificateDialog({ lot, onClose, onSaved }: { lot: LotWhereFromDTO; onClose: () => void; onSaved: () => void }) {
  const [file, setFile] = useState<File | null>(null);
  const [certificateType, setCertificateType] = useState<CertificateType>('CERTIFICATE_OF_CONFORMITY');
  const [certificateNumber, setCertificateNumber] = useState('');
  const [issuedBy, setIssuedBy] = useState('');
  const [issuedOn, setIssuedOn] = useState('');
  const [expiresOn, setExpiresOn] = useState('');

  const mutation = useMutation({
    mutationFn: () => materialTraceabilityService.attachCertificate(lot.materialLotId, {
      file: file!, certificateType,
      certificateNumber: certificateNumber || undefined,
      issuedBy: issuedBy || undefined,
      issuedOn: issuedOn || undefined,
      expiresOn: expiresOn || undefined,
    }),
    onSuccess: () => { onSaved(); onClose(); },
  });

  return (
    <Dialog open fullWidth maxWidth="sm" onClose={onClose}>
      <DialogTitle>File a certificate against lot {lot.lotNumber}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {mutation.isError && <ApiErrorNotice error={mutation.error} fallbackMessage="The certificate could not be filed." />}
          <FormControl size="small" fullWidth>
            <InputLabel id="certificate-type">Certificate type</InputLabel>
            <Select
              labelId="certificate-type"
              label="Certificate type"
              value={certificateType}
              onChange={(event) => setCertificateType(event.target.value as CertificateType)}
            >
              {CERTIFICATE_TYPES.map((type) => <MenuItem key={type} value={type}>{type.replace(/_/g, ' ')}</MenuItem>)}
            </Select>
          </FormControl>
          <Button variant="outlined" component="label">
            {file ? file.name : 'Choose document'}
            <input hidden type="file" onChange={(event) => setFile(event.target.files?.[0] ?? null)} />
          </Button>
          <TextField size="small" label="Certificate number" value={certificateNumber} onChange={(e) => setCertificateNumber(e.target.value)} />
          <TextField size="small" label="Issued by" value={issuedBy} onChange={(e) => setIssuedBy(e.target.value)} />
          <Stack direction="row" spacing={2}>
            <TextField size="small" type="date" label="Issued on" slotProps={{ inputLabel: { shrink: true } }} value={issuedOn} onChange={(e) => setIssuedOn(e.target.value)} fullWidth />
            <TextField
              size="small"
              type="date"
              label="Expires on"
              slotProps={{ inputLabel: { shrink: true } }}
              value={expiresOn}
              onChange={(e) => setExpiresOn(e.target.value)}
              helperText="Leave blank when the document does not expire."
              fullWidth
            />
          </Stack>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={!file || mutation.isPending} onClick={() => mutation.mutate()}>Upload</Button>
      </DialogActions>
    </Dialog>
  );
}

export default function LotDetailPage() {
  const { lotId } = useParams<{ lotId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canEdit = hasPermission('Products', 'edit');

  const [quarantining, setQuarantining] = useState(false);
  const [releasing, setReleasing] = useState(false);
  const [uploading, setUploading] = useState(false);

  const materialLotId = Number(lotId);
  const queryKey = ['material-lots', 'detail', materialLotId];
  const query = useQuery({
    queryKey,
    queryFn: () => materialTraceabilityService.getLot(materialLotId),
    enabled: Number.isFinite(materialLotId) && materialLotId > 0,
  });
  const lot = query.data;

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey });
    void queryClient.invalidateQueries({ queryKey: ['material-lots'] });
  };

  return (
    <PageShell
      title={lot ? `Lot ${lot.lotNumber}` : 'Lot'}
      subtitle="Where-from trace, compliance certificates and quarantine control."
      actions={lot && (
        <Stack direction="row" spacing={1.5}>
          <Button onClick={() => navigate('/inventory/lots')}>Back to lots</Button>
          {canEdit && <Button variant="outlined" onClick={() => setUploading(true)}>Add certificate</Button>}
          {canEdit && (lot.status === 'QUARANTINED'
            ? <Button variant="contained" onClick={() => setReleasing(true)}>Release from quarantine</Button>
            : <Button variant="contained" color="error" onClick={() => setQuarantining(true)}>Quarantine</Button>)}
        </Stack>
      )}
    >
      <QueryState
        loading={query.isLoading}
        error={query.isError}
        empty={!lot}
        onRetry={() => void query.refetch()}
        emptyText="This lot was not found."
      >
        {lot && (
          <>
            {lot.status === 'QUARANTINED' && (
              <Alert severity="error" sx={{ mb: 2.5 }}>
                <AlertTitle>Quarantined — not allocatable</AlertTitle>
                {lot.quarantineReasonCode}: {lot.quarantineReason} · held by {lot.quarantinedBy} on{' '}
                {formatDateTime(lot.quarantinedOn)}. This lot cannot be reserved or declared against a
                customer fulfilment until it is released.
              </Alert>
            )}

            <GapPanel gaps={lot.gaps} />

            <Paper variant="outlined" sx={{ p: 2.5, mb: 2.5 }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 900, mb: 1.5 }}>Where this material came from</Typography>
              <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(4, minmax(0, 1fr))' }, gap: 2 }}>
                <Field label="Tracking" value={lot.trackingMode} />
                <Field label="Part" value={lot.partNumber ? `${lot.partNumber} — ${lot.productName ?? ''}` : lot.productName} />
                <Field label="Warehouse" value={lot.warehouseName ?? lot.warehouseId} />
                <Field label="Received" value={formatDateTime(lot.receivedOn)} />
                <Field label="Goods receipt" value={lot.receiptNumber ?? lot.goodsReceiptId} />
                <Field label="Supplier PO" value={lot.purchaseOrderNumber ?? lot.supplierPurchaseOrderId} />
                <Field label="Supplier" value={lot.supplierName ?? lot.supplierId} />
                <Field label="Demand source" value={lot.purchaseOrderDemandSource} />
                <Field label="Customer RFQ" value={lot.rfqNumber} />
                <Field label="Commercial case" value={lot.nexoraSerial} />
                <Field label="Quantity received" value={lot.quantityReceived} />
                <Field label="Quantity remaining" value={lot.quantityRemaining} />
                <Field label="Country of origin (received)" value={lot.countryOfOrigin} />
                <Field label="Country of origin (ordered)" value={lot.orderedCountryOfOrigin} />
                <Field label="Manufacturer" value={lot.manufacturerName} />
                <Field label="Manufacturer part number" value={lot.manufacturerPartNumber} />
                <Field label="HS code" value={lot.hsCode} />
                <Field label="Supplier batch reference" value={lot.supplierBatchReference} />
                <Field label="Manufactured" value={lot.manufactureDate} />
                <Field label="Material expiry" value={lot.expiryDate} />
              </Box>
              {lot.releasedOn && (
                <>
                  <Divider sx={{ my: 2 }} />
                  <Typography variant="body2" color="text.secondary">
                    Last released {formatDateTime(lot.releasedOn)} by {lot.releasedBy} — {lot.releaseReason}
                  </Typography>
                </>
              )}
            </Paper>

            <Typography variant="subtitle2" sx={{ fontWeight: 900, mb: 1 }}>
              Compliance certificates ({lot.certificates.length})
            </Typography>
            <ResponsiveTable label="Lot certificates">
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>Type</TableCell>
                    <TableCell>Number</TableCell>
                    <TableCell>Issued by</TableCell>
                    <TableCell>Issued</TableCell>
                    <TableCell>Expires</TableCell>
                    <TableCell>State</TableCell>
                    <TableCell>Document</TableCell>
                    <TableCell>Filed</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {lot.certificates.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={8}>
                        <Typography variant="body2" color="text.secondary">
                          No certificate is held against this lot. A customs or customer compliance
                          request cannot be answered from the record.
                        </Typography>
                      </TableCell>
                    </TableRow>
                  )}
                  {lot.certificates.map((certificate) => (
                    <TableRow hover key={certificate.id}>
                      <TableCell>{certificate.certificateType.replace(/_/g, ' ')}</TableCell>
                      <TableCell>{certificate.certificateNumber ?? '—'}</TableCell>
                      <TableCell>{certificate.issuedBy ?? '—'}</TableCell>
                      <TableCell>{certificate.issuedOn ?? '—'}</TableCell>
                      <TableCell>{certificate.expiresOn ?? 'Does not expire'}</TableCell>
                      <TableCell>
                        <Chip
                          size="small"
                          variant="outlined"
                          color={certificate.expiryState === 'EXPIRED' ? 'error'
                            : certificate.expiryState === 'EXPIRING_SOON' ? 'warning'
                              : certificate.expiryState === 'VALID' ? 'success' : 'default'}
                          label={certificate.expiryState.replace(/_/g, ' ')}
                        />
                      </TableCell>
                      <TableCell>
                        <Button
                          size="small"
                          href={materialTraceabilityService.certificateDownloadUrl(certificate.attachmentId)}
                          target="_blank"
                          rel="noopener"
                        >
                          {certificate.fileName}
                        </Button>
                      </TableCell>
                      <TableCell>{formatDateTime(certificate.uploadedOn)} · {certificate.uploadedBy}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </ResponsiveTable>

            <Typography variant="subtitle2" sx={{ fontWeight: 900, mt: 3, mb: 1 }}>
              Where this lot went ({lot.fulfilledInto.length})
            </Typography>
            <ResponsiveTable label="Lot fulfilments">
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>Sales order</TableCell>
                    <TableCell>Delivery note</TableCell>
                    <TableCell>Commercial case</TableCell>
                    <TableCell align="right">Quantity</TableCell>
                    <TableCell>Compliance at despatch</TableCell>
                    <TableCell>Declared</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {lot.fulfilledInto.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={6}>
                        <Typography variant="body2" color="text.secondary">
                          This lot has not been declared against any customer fulfilment yet.
                        </Typography>
                      </TableCell>
                    </TableRow>
                  )}
                  {lot.fulfilledInto.map((fulfilment) => (
                    <TableRow hover key={fulfilment.consumptionId}>
                      <TableCell>
                        <Button size="small" onClick={() => navigate(`/inventory/order-trace/${fulfilment.orderId}`)}>
                          {fulfilment.orderNumber ?? fulfilment.orderId}
                        </Button>
                      </TableCell>
                      <TableCell>{fulfilment.shipmentNumber ?? 'Not despatched'}</TableCell>
                      <TableCell>{fulfilment.nexoraSerial ?? '—'}</TableCell>
                      <TableCell align="right">{fulfilment.quantity}</TableCell>
                      <TableCell>
                        {fulfilment.complianceStateAtDeclaration === 'CERTIFICATE_EXPIRED' ? (
                          <Chip size="small" color="error" variant="outlined"
                            label={`Expired — ${fulfilment.complianceOverrideBy ?? 'override'}`} />
                        ) : (
                          <Chip size="small" variant="outlined"
                            label={fulfilment.complianceStateAtDeclaration.replace(/_/g, ' ')} />
                        )}
                      </TableCell>
                      <TableCell>{formatDateTime(fulfilment.declaredOn)} · {fulfilment.declaredBy}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </ResponsiveTable>

            {quarantining && <QuarantineDialog lot={lot} onClose={() => setQuarantining(false)} onSaved={refresh} />}
            {releasing && <ReleaseDialog lot={lot} onClose={() => setReleasing(false)} onSaved={refresh} />}
            {uploading && <CertificateDialog lot={lot} onClose={() => setUploading(false)} onSaved={refresh} />}
          </>
        )}
      </QueryState>
    </PageShell>
  );
}
