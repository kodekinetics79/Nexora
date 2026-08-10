import React, { useRef } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Grid, Divider, Table, TableHead,
  TableRow, TableCell, TableBody, Stack, CircularProgress, Alert, AlertTitle, Chip
} from '@mui/material';
import {
  LocalShipping as ShippingIcon,
} from '@mui/icons-material';
import shipmentService from '../../../api/services/shipmentService';
import deliveryService, { DELIVERY_STATUS_LABEL } from '../../../api/services/deliveryService';
import dayjs from 'dayjs';

/**
 * FR-DLM-01. Delivery note for a single shipment.
 *
 * Three rules govern every value on this page, because a receiving bay signs it and the signed
 * copy is the evidence in a quantity dispute:
 *
 *  1. **Every quantity comes from the server, and this page performs no arithmetic.** The note is
 *     assembled by `DeliveryNoteReadService`, which reads the same `DeliveredQuantityLedger` the
 *     invoice ceiling reads — so the REMAINING column on the signed paper and the quantity finance
 *     is allowed to bill cannot disagree. This page previously joined an order response to a
 *     shipment response in the browser and deliberately omitted a remaining column because no
 *     backend field carried cumulative delivered. That field now exists (FR-DLM-02, register item
 *     E50), so the column is here and it is server-computed.
 *
 *  2. **Nothing is invented.** Absences arrive from the server as `gaps` and are printed on the
 *     document. No placeholder carrier, no assumed service level, no default consignee, no
 *     invented issuer address, CR number or VAT registration.
 *
 *  3. **The signature area shows the real POD once one exists.** The two dashed boxes were paper
 *     blanks that captured nothing back into the system. They remain — a driver still needs
 *     somewhere to sign on the printed copy — but once a proof of delivery has been recorded
 *     (FR-DLM-03) the page shows who signed, when, where, and what they actually accepted.
 */

const NOT_RECORDED = '—';

const ShipmentInvoicePage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const printRef = useRef<HTMLDivElement>(null);
  const shipmentId = Number(id);

  const { data: note, isLoading, isError, error } = useQuery({
    queryKey: ['delivery-note', shipmentId],
    queryFn: () => deliveryService.getNote(shipmentId),
    enabled: Number.isFinite(shipmentId) && shipmentId > 0,
  });

  // The tenant's own logistics fields are not part of the governed note contract; they are read
  // from the shipment record itself and rendered only when actually present.
  const { data: shipment } = useQuery({
    queryKey: ['shipment-logistics', shipmentId],
    queryFn: () => shipmentService.getById(shipmentId),
    enabled: Number.isFinite(shipmentId) && shipmentId > 0,
  });

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', p: 5 }}><CircularProgress /></Box>;
  if (isError) {
    return (
      <Alert severity="error" sx={{ m: 3 }}>
        <AlertTitle>This delivery note could not be loaded</AlertTitle>
        {(error as { message?: string })?.message ?? 'The server refused the request.'}
      </Alert>
    );
  }
  if (!note) return <Typography color="error">Shipment not found</Typography>;

  const { issuer, proof } = note;
  const hasIssuerContact = Boolean(issuer.addressLine || issuer.phone || issuer.email);

  return (
    <Box sx={{ bgcolor: 'white', minHeight: '100vh', display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
      <Box
        ref={printRef}
        sx={{
          p: { xs: 3, md: 6 },
          width: '100%',
          maxWidth: '1000px',
          bgcolor: 'white',
          '@media print': {
            p: 0,
            maxWidth: 'none',
          }
        }}
      >
        {/*
          Every gap the server found, on the face of the document. A missing sender, an unmapped
          region or an unlinked customer PO is a reason not to issue this note, and burying that in
          a console log or a tooltip is how an incomplete document gets signed.
        */}
        {note.gaps.length > 0 && (
          <Alert severity={hasIssuerContact ? 'info' : 'warning'} sx={{ mb: 4 }}>
            <AlertTitle>
              {hasIssuerContact
                ? 'What this delivery note does not state'
                : 'This delivery note is not ready to issue'}
            </AlertTitle>
            <Box component="ul" sx={{ m: 0, pl: 2.5 }}>
              {note.gaps.map(gap => <li key={gap}>{gap}</li>)}
            </Box>
          </Alert>
        )}

        {/* Company Header */}
        <Grid container spacing={4} sx={{ mb: 6 }}>
          <Grid size={{ xs: 6 }}>
            {issuer.legalName ? (
              <Typography variant="h4" sx={{ fontWeight: 900, color: 'primary.main', mb: 1 }}>
                {issuer.legalName}
              </Typography>
            ) : (
              <Typography variant="body2" sx={{ fontStyle: 'italic', color: 'text.disabled', mb: 1 }}>
                Issuing business unit not identified
              </Typography>
            )}
            {hasIssuerContact && (
              <Typography variant="body2" sx={{ color: 'text.secondary', whiteSpace: 'pre-line' }}>
                {[issuer.addressLine, issuer.phone && `T: ${issuer.phone}`, issuer.email && `E: ${issuer.email}`]
                  .filter(Boolean)
                  .join('\n')}
              </Typography>
            )}
            {issuer.taxRegistrationNumber && (
              <Typography variant="body2" sx={{ color: 'text.secondary', mt: 0.5 }}>
                VAT Registration: {issuer.taxRegistrationNumber}
              </Typography>
            )}
          </Grid>
          <Grid size={{ xs: 6 }} sx={{ textAlign: 'right' }}>
            <Typography variant="h3" sx={{ fontWeight: 900, color: 'grey.300', mb: 2 }}>DELIVERY NOTE</Typography>
            <Stack spacing={0.5}>
              <Typography variant="body2"><strong>Shipment #:</strong> {note.shipmentNo}</Typography>
              <Typography variant="body2"><strong>Ship Date:</strong> {dayjs(note.shipmentDate).format('DD MMM YYYY')}</Typography>
              <Typography variant="body2"><strong>Order #:</strong> {note.orderNo}</Typography>
              <Typography variant="body2">
                <strong>Customer PO:</strong>{' '}
                {note.customerPurchaseOrderNumber
                  ? `${note.customerPurchaseOrderNumber}${note.customerPurchaseOrderDate ? ` (${dayjs(note.customerPurchaseOrderDate).format('DD MMM YYYY')})` : ''}`
                  : NOT_RECORDED}
              </Typography>
              <Typography variant="body2">
                <strong>Case:</strong> {note.nexoraSerial ?? NOT_RECORDED}
              </Typography>
              {note.carrier && (
                <Typography variant="body2"><strong>Carrier:</strong> {note.carrier}</Typography>
              )}
            </Stack>
          </Grid>
        </Grid>

        {/* Logistics tracking — rendered only when the shipment actually carries these values. */}
        {(note.trackingNumber || shipment?.serviceLevel || shipment?.estimatedDeliveryDate) && (
          <Box sx={{ mb: 6, p: 3, bgcolor: 'primary.50', borderRadius: 2, display: 'flex', alignItems: 'center', gap: 3 }}>
            <ShippingIcon sx={{ fontSize: '3rem', color: 'primary.main' }} />
            <Box>
              <Typography variant="subtitle1" sx={{ fontWeight: 800, color: 'primary.main' }}>
                LOGISTICS TRACKING
              </Typography>
              <Typography variant="body2">
                {[
                  note.trackingNumber && `Tracking Number: ${note.trackingNumber}`,
                  shipment?.serviceLevel && `Service: ${shipment.serviceLevel}`,
                  shipment?.estimatedDeliveryDate && `Est. Delivery: ${dayjs(shipment.estimatedDeliveryDate).format('DD MMM YYYY')}`,
                ].filter(Boolean).join('  |  ')}
              </Typography>
            </Box>
          </Box>
        )}

        {/* Consignee / delivery status */}
        <Grid container spacing={4} sx={{ mb: 6 }}>
          <Grid size={{ xs: 6 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 1, color: 'text.secondary', textTransform: 'uppercase' }}>Consignee (Deliver To)</Typography>
            {note.customerName ? (
              <Typography variant="h6" sx={{ fontWeight: 700 }}>{note.customerName}</Typography>
            ) : (
              <Typography variant="body2" sx={{ fontStyle: 'italic', color: 'text.disabled' }}>
                Customer not recorded
              </Typography>
            )}
            <Typography variant="body2" sx={{ color: 'text.secondary', mt: 0.5, whiteSpace: 'pre-line' }}>
              {note.shippingAddress ?? 'No delivery address recorded'}
            </Typography>
            {/*
              FR-DLM-01: the governed region, read from the tenant's own city master rather than
              parsed out of the address above. An unmapped address says so.
            */}
            <Typography variant="body2" sx={{ mt: 0.5 }}>
              <strong>Region:</strong>{' '}
              {note.deliveryRegionName
                ? [note.deliveryCityName, note.deliveryRegionName, note.deliveryCountryName].filter(Boolean).join(', ')
                : <Box component="span" sx={{ fontStyle: 'italic', color: 'text.disabled' }}>Not mapped to a governed region</Box>}
            </Typography>
          </Grid>
          <Grid size={{ xs: 6 }} sx={{ textAlign: 'right' }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 1, color: 'text.secondary', textTransform: 'uppercase' }}>Delivery Status</Typography>
            <Typography variant="body2">
              {DELIVERY_STATUS_LABEL[note.deliveryStatus] ?? note.deliveryStatus}
            </Typography>
          </Grid>
        </Grid>

        {/* Items Table */}
        <Table sx={{ mb: 2 }}>
          <TableHead>
            <TableRow sx={{ borderTop: '2px solid black', borderBottom: '2px solid black' }}>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }}>S.NO</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }}>PRODUCT DESCRIPTION</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }} align="right">ORDERED QTY</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }} align="right">SHIPPED QTY<br />(THIS NOTE)</TableCell>
              <TableCell sx={{ fontWeight: 900, py: 1.5 }} align="right">REMAINING<br />(AFTER THIS NOTE)</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {note.lines.map((line, index) => (
              <TableRow key={line.shipmentItemId} sx={{ '& td': { borderBottom: '1px solid #eee', py: 2 } }}>
                <TableCell>{index + 1}</TableCell>
                <TableCell>
                  {line.productName ? (
                    <Typography variant="body2" sx={{ fontWeight: 700 }}>{line.productName}</Typography>
                  ) : (
                    <Typography variant="body2" sx={{ fontStyle: 'italic', color: 'text.disabled' }}>Product not recorded</Typography>
                  )}
                  {line.description && <Typography variant="caption" color="text.secondary">{line.description}</Typography>}
                  {/* FR-DLM-01: the material lots that physically left on this note. */}
                  {line.lots.length > 0 && (
                    <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary', mt: 0.5 }}>
                      Lots: {line.lots.map(lot =>
                        `${lot.lotNumber} (${lot.quantity}${lot.countryOfOrigin ? `, ${lot.countryOfOrigin}` : ''})`
                      ).join('; ')}
                    </Typography>
                  )}
                </TableCell>
                <TableCell align="right">
                  {line.orderedQuantity}{line.unitOfMeasure ? ` ${line.unitOfMeasure}` : ''}
                </TableCell>
                <TableCell align="right" sx={{ fontWeight: 700 }}>
                  {line.despatchedQuantity}{line.unitOfMeasure ? ` ${line.unitOfMeasure}` : ''}
                </TableCell>
                <TableCell align="right">
                  {line.remainingQuantity}{line.unitOfMeasure ? ` ${line.unitOfMeasure}` : ''}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>

        {/*
          The single most important sentence on this document. Without it a signature against a
          part shipment reads as acknowledgement of the whole order.
        */}
        <Typography variant="caption" sx={{ display: 'block', mb: 6, color: 'text.secondary' }}>
          This note covers the quantities despatched on this shipment only. Signing it acknowledges
          receipt of the SHIPPED QTY shown above and is not an acknowledgement that the order is
          complete. REMAINING is the awarded quantity less what this customer has already accepted
          on earlier deliveries, less this note.
        </Typography>

        {proof ? (
          /* FR-DLM-03. A recorded proof of delivery replaces the blank boxes with the real record. */
          <Box sx={{ p: 3, border: '2px solid black', borderRadius: 2, mb: 4 }}>
            <Stack direction="row" spacing={2} sx={{ alignItems: 'center', mb: 2 }}>
              <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>PROOF OF DELIVERY</Typography>
              <Chip
                size="small"
                label={DELIVERY_STATUS_LABEL[proof.outcome] ?? proof.outcome}
                color={proof.outcome === 'DELIVERED' ? 'success' : 'warning'}
              />
            </Stack>
            <Grid container spacing={3}>
              <Grid size={{ xs: 12, md: 6 }}>
                <Typography variant="body2"><strong>Received by:</strong> {proof.receivedByName}</Typography>
                <Typography variant="body2">
                  <strong>Position:</strong> {proof.receivedByPosition ?? NOT_RECORDED}
                </Typography>
                <Typography variant="body2">
                  <strong>Contact:</strong> {proof.receivedByContact ?? NOT_RECORDED}
                </Typography>
                <Typography variant="body2">
                  <strong>Received on:</strong> {dayjs(proof.receivedOn).format('DD MMM YYYY HH:mm')}
                </Typography>
                <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                  Recorded by {proof.recordedBy} on {dayjs(proof.recordedOn).format('DD MMM YYYY HH:mm')}
                </Typography>
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                {/*
                  A coordinate is stamped at the moment of signature and nowhere else — decision R22
                  keeps GPS as POD evidence and out of any tracking feed. "No fix" is shown as no
                  fix rather than as 0,0.
                */}
                <Typography variant="body2">
                  <strong>Location at signature:</strong>{' '}
                  {proof.hasGpsFix
                    ? `${proof.gpsLatitude}, ${proof.gpsLongitude}${proof.gpsAccuracyMeters ? ` (±${proof.gpsAccuracyMeters} m)` : ''}`
                    : <Box component="span" sx={{ fontStyle: 'italic', color: 'text.disabled' }}>No GPS fix captured</Box>}
                </Typography>
                {proof.gpsCapturedOn && (
                  <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                    Fix taken {dayjs(proof.gpsCapturedOn).format('DD MMM YYYY HH:mm')}
                  </Typography>
                )}
                <Stack direction="row" spacing={2} sx={{ mt: 1 }} className="print-none">
                  {([
                    ['Signature', proof.signatureEvidenceId],
                    ['Stamp', proof.stampEvidenceId],
                    ['Photo', proof.photoEvidenceId],
                  ] as const).map(([label, attachmentId]) => (
                    <Box key={label}>
                      <Typography variant="caption" sx={{ display: 'block', fontWeight: 700 }}>{label}</Typography>
                      {attachmentId ? (
                        <a href={deliveryService.evidenceUrl(attachmentId)} target="_blank" rel="noreferrer">
                          <Box
                            component="img"
                            src={deliveryService.evidenceUrl(attachmentId)}
                            alt={`${label} evidence`}
                            sx={{ maxHeight: 90, maxWidth: 160, border: '1px solid #ccc' }}
                          />
                        </a>
                      ) : (
                        <Typography variant="caption" sx={{ fontStyle: 'italic', color: 'text.disabled' }}>
                          Not captured
                        </Typography>
                      )}
                    </Box>
                  ))}
                </Stack>
              </Grid>
            </Grid>

            {proof.lines.some(l => l.refusedQuantity > 0) && (
              <Box sx={{ mt: 3 }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 900, mb: 1 }}>
                  NOT ACCEPTED AT THE DOOR
                </Typography>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell sx={{ fontWeight: 800 }}>Item</TableCell>
                      <TableCell sx={{ fontWeight: 800 }} align="right">Despatched</TableCell>
                      <TableCell sx={{ fontWeight: 800 }} align="right">Accepted</TableCell>
                      <TableCell sx={{ fontWeight: 800 }} align="right">Not accepted</TableCell>
                      <TableCell sx={{ fontWeight: 800 }}>Reason</TableCell>
                      <TableCell sx={{ fontWeight: 800 }}>Commercial decision</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {proof.lines.filter(l => l.refusedQuantity > 0).map(l => (
                      <TableRow key={l.id}>
                        <TableCell>{l.productName ?? `Line ${l.shipmentItemId}`}</TableCell>
                        <TableCell align="right">{l.despatchedQuantity}</TableCell>
                        <TableCell align="right">{l.acceptedQuantity}</TableCell>
                        <TableCell align="right">{l.refusedQuantity}</TableCell>
                        <TableCell>
                          {l.exceptionReasonCode}
                          {l.exceptionNote ? ` — ${l.exceptionNote}` : ''}
                        </TableCell>
                        <TableCell>
                          {l.commercialDecision
                            ? `${l.commercialDecision} — ${l.commercialDecisionReason}`
                            : <Box component="span" sx={{ fontStyle: 'italic', color: 'warning.main' }}>
                                Awaiting a decision
                              </Box>}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </Box>
            )}
          </Box>
        ) : (
          /* No POD yet. The printed copy still needs somewhere to sign. */
          <Grid container spacing={8}>
            <Grid size={{ xs: 6 }}>
              <Box sx={{ p: 3, border: '1px dashed grey', borderRadius: 2 }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 4 }}>SHIPPER&apos;S SIGNATURE</Typography>
                <Divider sx={{ borderBottomWidth: 1, borderColor: 'black', mb: 1 }} />
                <Typography variant="caption">Dispatch Officer Name &amp; Date</Typography>
              </Box>
            </Grid>
            <Grid size={{ xs: 6 }}>
              <Box sx={{ p: 3, border: '1px dashed grey', borderRadius: 2 }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 4 }}>RECEIVER&apos;S SIGNATURE / STAMP</Typography>
                <Divider sx={{ borderBottomWidth: 1, borderColor: 'black', mb: 1 }} />
                <Typography variant="caption">Confirmed received in good condition</Typography>
                <Typography variant="caption" sx={{ display: 'block', mt: 1, color: 'warning.main' }} className="print-none">
                  This is a paper blank. Record the signed copy against the shipment so the accepted
                  quantities reach the system.
                </Typography>
              </Box>
            </Grid>
          </Grid>
        )}

        {/* Footer */}
        <Box sx={{ mt: 8, pt: 2, borderTop: '1px solid #eee', textAlign: 'center' }}>
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            This is a computer generated document.
          </Typography>
        </Box>
      </Box>

      {/* Global CSS for Print */}
      <style>
        {`
          @media print {
            body { background: white !important; margin: 0; padding: 0; }
            .print-none { display: none !important; }
            header, footer, nav { display: none !important; }
          }
        `}
      </style>
    </Box>
  );
};

export default ShipmentInvoicePage;
