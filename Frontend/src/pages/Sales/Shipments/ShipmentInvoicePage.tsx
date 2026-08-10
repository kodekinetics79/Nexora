import React, { useRef } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Grid, Divider, Table, TableHead,
  TableRow, TableCell, TableBody, Stack, CircularProgress, Alert, AlertTitle
} from '@mui/material';
import {
  LocalShipping as ShippingIcon,
} from '@mui/icons-material';
import shipmentService from '../../../api/services/shipmentService';
import orderService from '../../../api/services/orderService';
import businessUnitService from '../../../api/services/businessUnitService';
import quoteConfigurationService from '../../../api/services/quoteConfigurationService';
import { useAuth } from '../../../context/AuthContext';
import dayjs from 'dayjs';

/**
 * Delivery note for a single shipment.
 *
 * Two rules govern every value on this page, because a receiving bay signs it and the signed
 * copy is the evidence in a quantity dispute:
 *
 *  1. Every quantity is server data. SHIPPED is this shipment's own line quantity; ORDERED is
 *     the awarded quantity joined from the order by `orderItemId`. There is deliberately no
 *     "remaining" column — remaining is awarded minus *cumulative delivered across all
 *     shipments*, and no backend field carries cumulative delivered (see the banner copy and
 *     the note at the foot of this file). Summing it in the browser would produce a number
 *     that looks authoritative and is not, on a document someone signs.
 *
 *  2. Nothing is invented. Where a value is absent it renders as an explicit gap or the block
 *     is omitted. No placeholder carrier, no assumed service level, no "TBD" weight, no
 *     default consignee, no invented issuer address.
 */

const NOT_RECORDED = '—';

const ShipmentInvoicePage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const { userData } = useAuth();
  const businessUnitId = userData?.businessUnitId || 0;
  const printRef = useRef<HTMLDivElement>(null);

  const { data: shipment, isLoading: isLoadingShipment } = useQuery({
    queryKey: ['shipment-delivery-note', id],
    queryFn: () => shipmentService.getById(Number(id), businessUnitId),
    enabled: !!id,
  });

  const orderQuery = useQuery({
    queryKey: ['order-details', shipment?.orderId],
    queryFn: () => orderService.getById(Number(shipment?.orderId), businessUnitId),
    enabled: !!shipment?.orderId,
  });
  const order = orderQuery.data;

  // Issuer identity. The only server-held sources are the business unit's name and the
  // per-BU document configuration (address/phone/email). Neither carries a CR number or a
  // VAT registration number, so this document states neither.
  const { data: businessUnit } = useQuery({
    queryKey: ['business-unit', businessUnitId],
    queryFn: () => businessUnitService.getById(businessUnitId),
    enabled: businessUnitId > 0,
  });

  const { data: documentConfig } = useQuery({
    queryKey: ['quote-configuration', businessUnitId],
    queryFn: () => quoteConfigurationService.getByBusinessUnitId(businessUnitId),
    enabled: businessUnitId > 0,
    retry: false,
  });

  if (isLoadingShipment) return <Box sx={{ display: 'flex', justifyContent: 'center', p: 5 }}><CircularProgress /></Box>;
  if (!shipment) return <Typography color="error">Shipment not found</Typography>;

  const issuerName = businessUnit?.businessUnitName?.trim() || null;
  const issuerAddress = documentConfig?.companyAddress?.trim() || null;
  const issuerPhone = documentConfig?.companyPhone?.trim() || null;
  const issuerEmail = documentConfig?.companyEmail?.trim() || null;
  const hasIssuerContact = Boolean(issuerAddress || issuerPhone || issuerEmail);

  // Awarded quantity per order line, keyed by order item id. Absent until the order query
  // resolves, and absent for any shipment line whose order line is not in the payload.
  const orderedByOrderItemId = new Map<number, { quantity: number; productName?: string; description?: string }>(
    (order?.items ?? []).map(line => [line.id, line]),
  );
  const orderedQuantitiesResolved = Boolean(order);

  const consigneeName = order?.customerName?.trim() || null;
  const consigneeAddress = shipment.shippingAddress?.trim() || null;

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
        {!hasIssuerContact && (
          <Alert severity="warning" sx={{ mb: 4 }}>
            <AlertTitle>This delivery note is not ready to issue</AlertTitle>
            No issuing address, phone or email is configured for this business unit, so this
            document does not identify its sender. Set the company address, phone and email in
            the business unit&apos;s document configuration before sending or signing it.
          </Alert>
        )}

        {/* Company Header */}
        <Grid container spacing={4} sx={{ mb: 6 }}>
          <Grid size={{ xs: 6 }}>
            {issuerName ? (
              <Typography variant="h4" sx={{ fontWeight: 900, color: 'primary.main', mb: 1 }}>
                {issuerName}
              </Typography>
            ) : (
              <Typography variant="body2" sx={{ fontStyle: 'italic', color: 'text.disabled', mb: 1 }}>
                Issuing business unit not identified
              </Typography>
            )}
            {hasIssuerContact && (
              <Typography variant="body2" sx={{ color: 'text.secondary', whiteSpace: 'pre-line' }}>
                {[issuerAddress, issuerPhone && `T: ${issuerPhone}`, issuerEmail && `E: ${issuerEmail}`]
                  .filter(Boolean)
                  .join('\n')}
              </Typography>
            )}
          </Grid>
          <Grid size={{ xs: 6 }} sx={{ textAlign: 'right' }}>
            <Typography variant="h3" sx={{ fontWeight: 900, color: 'grey.300', mb: 2 }}>DELIVERY NOTE</Typography>
            <Stack spacing={0.5}>
              <Typography variant="body2"><strong>Shipment #:</strong> {shipment.shipmentNo}</Typography>
              <Typography variant="body2"><strong>Ship Date:</strong> {dayjs(shipment.shipmentDate).format('DD MMM YYYY')}</Typography>
              <Typography variant="body2"><strong>Order #:</strong> {shipment.orderNo}</Typography>
              {shipment.carrier && (
                <Typography variant="body2"><strong>Carrier:</strong> {shipment.carrier}</Typography>
              )}
            </Stack>
          </Grid>
        </Grid>

        {/* Logistics tracking — rendered only when the shipment actually carries these values. */}
        {(shipment.trackingNumber || shipment.serviceLevel || shipment.estimatedDeliveryDate) && (
          <Box sx={{ mb: 6, p: 3, bgcolor: 'primary.50', borderRadius: 2, display: 'flex', alignItems: 'center', gap: 3 }}>
            <ShippingIcon sx={{ fontSize: '3rem', color: 'primary.main' }} />
            <Box>
              <Typography variant="subtitle1" sx={{ fontWeight: 800, color: 'primary.main' }}>
                LOGISTICS TRACKING
              </Typography>
              <Typography variant="body2">
                {[
                  shipment.trackingNumber && `Tracking Number: ${shipment.trackingNumber}`,
                  shipment.serviceLevel && `Service: ${shipment.serviceLevel}`,
                  shipment.estimatedDeliveryDate && `Est. Delivery: ${dayjs(shipment.estimatedDeliveryDate).format('DD MMM YYYY')}`,
                ].filter(Boolean).join('  |  ')}
              </Typography>
            </Box>
          </Box>
        )}

        {/* Consignee / shipment status */}
        <Grid container spacing={4} sx={{ mb: 6 }}>
          <Grid size={{ xs: 6 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 1, color: 'text.secondary', textTransform: 'uppercase' }}>Consignee (Deliver To)</Typography>
            {consigneeName ? (
              <Typography variant="h6" sx={{ fontWeight: 700 }}>{consigneeName}</Typography>
            ) : (
              <Typography variant="body2" sx={{ fontStyle: 'italic', color: 'text.disabled' }}>
                {orderQuery.isLoading ? 'Loading customer…' : 'Customer not recorded'}
              </Typography>
            )}
            <Typography variant="body2" sx={{ color: 'text.secondary', mt: 0.5, whiteSpace: 'pre-line' }}>
              {consigneeAddress ?? 'No delivery address recorded'}
            </Typography>
          </Grid>
          <Grid size={{ xs: 6 }} sx={{ textAlign: 'right' }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 1, color: 'text.secondary', textTransform: 'uppercase' }}>Shipment Details</Typography>
            <Typography variant="body2">
              <strong>Shipment Status:</strong> {shipment.status?.toUpperCase() || NOT_RECORDED}
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
              <TableCell sx={{ fontWeight: 900, py: 1.5 }} align="right">SHIPPED QTY<br />(THIS SHIPMENT)</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {shipment.items.map((item, index) => {
              const orderedLine = orderedByOrderItemId.get(item.orderItemId);
              const productName = item.productName?.trim() || orderedLine?.productName?.trim() || null;
              const lineNote = item.notes?.trim() || orderedLine?.description?.trim() || null;
              return (
                <TableRow key={item.id ?? index} sx={{ '& td': { borderBottom: '1px solid #eee', py: 2 } }}>
                  <TableCell>{index + 1}</TableCell>
                  <TableCell>
                    {productName ? (
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>{productName}</Typography>
                    ) : (
                      <Typography variant="body2" sx={{ fontStyle: 'italic', color: 'text.disabled' }}>Product not recorded</Typography>
                    )}
                    {lineNote && <Typography variant="caption" color="text.secondary">{lineNote}</Typography>}
                  </TableCell>
                  <TableCell align="right">
                    {orderedLine ? orderedLine.quantity : NOT_RECORDED}
                  </TableCell>
                  <TableCell align="right" sx={{ fontWeight: 700 }}>{item.quantity}</TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>

        {/*
          The single most important sentence on this document. Without it a signature against a
          part shipment reads as acknowledgement of the whole order.
        */}
        <Typography variant="caption" sx={{ display: 'block', mb: 6, color: 'text.secondary' }}>
          This note covers the quantities despatched on this shipment only. Signing it acknowledges
          receipt of the SHIPPED QTY shown above and is not an acknowledgement that the order is
          complete.
          {!orderedQuantitiesResolved && ' Ordered quantities could not be loaded for this document.'}
        </Typography>

        {/* Delivery Confirmation Area */}
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
            </Box>
          </Grid>
        </Grid>

        {/* Footer */}
        <Box sx={{ mt: 8, pt: 2, borderTop: '1px solid #eee', textAlign: 'center' }}>
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            {documentConfig?.footerText?.trim() || 'This is a computer generated document.'}
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
