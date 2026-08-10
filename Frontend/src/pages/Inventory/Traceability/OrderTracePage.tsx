import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import {
  Alert, AlertTitle, Box, Button, Chip, Paper, Stack, Table, TableBody, TableCell, TableHead,
  TableRow, TextField, Typography,
} from '@mui/material';
import materialTraceabilityService from '../../../api/services/materialTraceabilityService';
import { PageShell, QueryState, ResponsiveTable } from '../../SalesManagement/CommercialPagePrimitives';

/**
 * FR-MTR-03 where-used. Which lots fulfilled a sales order — and, just as loudly, how many units
 * left the building with no lot stated at all.
 *
 * The untraced figure is rendered as an error chip, not omitted and not softened. A line showing
 * "100 shipped, 60 traced" is a recall this business cannot execute, and the person looking at the
 * screen is the only one who can fix it.
 */
export default function OrderTracePage() {
  const { orderId } = useParams<{ orderId?: string }>();
  const navigate = useNavigate();
  const [entered, setEntered] = useState(orderId ?? '');

  const targetOrderId = Number(orderId);
  const query = useQuery({
    queryKey: ['material-lots', 'order-trace', targetOrderId],
    queryFn: () => materialTraceabilityService.getOrderTrace(targetOrderId),
    enabled: Number.isFinite(targetOrderId) && targetOrderId > 0,
  });
  const trace = query.data;

  return (
    <PageShell
      title={trace ? `Where-used — ${trace.orderNumber}` : 'Where-used trace'}
      subtitle="Every material lot that fulfilled this sales order, and every unit that no lot explains."
      actions={(
        <Stack direction="row" spacing={1.5}>
          <TextField
            size="small"
            label="Sales order id"
            value={entered}
            onChange={(event) => setEntered(event.target.value)}
            sx={{ width: 180 }}
          />
          <Button variant="contained" onClick={() => navigate(`/inventory/order-trace/${entered.trim()}`)}>
            Trace
          </Button>
        </Stack>
      )}
    >
      {!orderId && (
        <Paper variant="outlined" sx={{ p: 4, textAlign: 'center' }}>
          <Typography color="text.secondary">
            Enter a sales order id to trace the material that fulfilled it.
          </Typography>
        </Paper>
      )}

      {orderId && (
        <QueryState
          loading={query.isLoading}
          error={query.isError}
          empty={!trace}
          onRetry={() => void query.refetch()}
          emptyText="That sales order was not found."
        >
          {trace && (
            <>
              {trace.gaps.length > 0 && (
                <Alert severity="warning" sx={{ mb: 2.5 }}>
                  <AlertTitle>Traceability gaps ({trace.gaps.length})</AlertTitle>
                  <Stack spacing={0.75}>
                    {trace.gaps.map((gap) => (
                      <Typography variant="body2" key={`${gap.kind}:${gap.subject}:${gap.subjectId}`}>
                        <strong>{gap.kind}</strong> — {gap.reference}
                        {gap.quantity != null ? ` (${gap.quantity})` : ''}: {gap.detail}
                      </Typography>
                    ))}
                  </Stack>
                </Alert>
              )}

              <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                Commercial case: {trace.nexoraSerial ?? 'none stated'}
              </Typography>

              {trace.lines.map((line) => (
                <Box key={line.orderItemId} sx={{ mb: 3 }}>
                  <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 1 }}>
                    <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>
                      {line.partNumber ?? line.description ?? `Line ${line.orderItemId}`}
                    </Typography>
                    <Chip size="small" variant="outlined" label={`Ordered ${line.orderedQuantity}`} />
                    <Chip size="small" variant="outlined" label={`Shipped ${line.shippedQuantity}`} />
                    <Chip size="small" variant="outlined" color="success" label={`Traced ${line.declaredQuantity}`} />
                    {line.untracedQuantity > 0 && (
                      <Chip size="small" color="error" sx={{ fontWeight: 700 }}
                        label={`Untraced ${line.untracedQuantity}`} />
                    )}
                  </Stack>
                  <ResponsiveTable label={`Lots for line ${line.orderItemId}`}>
                    <Table size="small">
                      <TableHead>
                        <TableRow>
                          <TableCell>Lot / serial</TableCell>
                          <TableCell>Tracking</TableCell>
                          <TableCell align="right">Quantity</TableCell>
                          <TableCell>Supplier</TableCell>
                          <TableCell>Supplier PO</TableCell>
                          <TableCell>Origin</TableCell>
                          <TableCell>Manufacturer</TableCell>
                          <TableCell>Delivery note</TableCell>
                          <TableCell>Lot status</TableCell>
                          <TableCell>Certificates now</TableCell>
                        </TableRow>
                      </TableHead>
                      <TableBody>
                        {line.lots.length === 0 && (
                          <TableRow>
                            <TableCell colSpan={10}>
                              <Typography variant="body2" color="text.secondary">
                                No lot has been declared against this line.
                              </Typography>
                            </TableCell>
                          </TableRow>
                        )}
                        {line.lots.map((lot) => (
                          <TableRow hover key={lot.consumptionId}>
                            <TableCell>
                              <Button size="small" onClick={() => navigate(`/inventory/lots/${lot.materialLotId}`)}>
                                {lot.lotNumber}
                              </Button>
                            </TableCell>
                            <TableCell>{lot.trackingMode}</TableCell>
                            <TableCell align="right">{lot.quantity}</TableCell>
                            <TableCell>{lot.supplierName ?? lot.supplierId}</TableCell>
                            <TableCell>{lot.purchaseOrderNumber ?? lot.supplierPurchaseOrderId}</TableCell>
                            <TableCell>{lot.countryOfOrigin ?? '—'}</TableCell>
                            <TableCell>{lot.manufacturerName ?? '—'}</TableCell>
                            <TableCell>{lot.shipmentNumber ?? 'Not despatched'}</TableCell>
                            <TableCell>
                              {lot.lotStatus === 'QUARANTINED'
                                ? <Chip size="small" color="error" label="Quarantined" sx={{ fontWeight: 700 }} />
                                : <Chip size="small" variant="outlined" color="success" label="Available" />}
                            </TableCell>
                            <TableCell>
                              <Chip
                                size="small"
                                variant="outlined"
                                color={lot.certificateStateNow === 'EXPIRED' ? 'error'
                                  : lot.certificateStateNow === 'EXPIRING_SOON' ? 'warning' : 'default'}
                                label={lot.certificateStateNow.replace(/_/g, ' ')}
                              />
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </ResponsiveTable>
                </Box>
              ))}
            </>
          )}
        </QueryState>
      )}
    </PageShell>
  );
}
