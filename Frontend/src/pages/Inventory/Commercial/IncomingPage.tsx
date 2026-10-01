import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link as RouterLink } from 'react-router-dom';
import { Link, Table, TableBody, TableCell, TableHead, TableRow, Typography } from '@mui/material';
import dayjs from 'dayjs';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import materialTraceabilityService from '../../../api/services/materialTraceabilityService';
import SearchField from '../../../components/common/SearchField';
import { QueryState, ResponsiveTable, StatusChip } from '../../SalesManagement/CommercialPagePrimitives';
import { ProductsWorkspaceShell, ProductsWorkspaceToolbar } from '../ProductsWorkspaceNav';

const recorded = (value?: string | null) => value?.trim() || 'Not recorded';
const date = (value?: string | null) => value ? dayjs(value).format('DD MMM YYYY') : 'Not recorded';
const tableStyles = {
  '& th': { whiteSpace: 'nowrap' },
  '& th:first-of-type, & td:first-of-type': { minWidth: 210 },
  '& th:nth-of-type(2), & td:nth-of-type(2)': { minWidth: 160 },
  '& td': { minWidth: 110 },
};

export default function IncomingPage() {
  const [search, setSearch] = useState('');
  const incomingQuery = useQuery({
    queryKey: ['inventory-intelligence', 'incoming'],
    queryFn: () => commercialIntelligenceService.getIncoming(),
  });
  const receiptsQuery = useQuery({
    queryKey: ['material-lots', 'incoming-receipts'],
    queryFn: () => materialTraceabilityService.searchLots({ limit: 200 }),
  });
  const term = search.trim().toLowerCase();
  const matches = (values: (string | null | undefined)[]) => values.some(value => value?.toLowerCase().includes(term));
  const incoming = (incomingQuery.data ?? []).filter(row => matches([
    row.partNumber, row.productName, row.supplierName, row.supplierCity, row.supplierCountry,
    row.warehouseName, row.purchaseOrderNumber, row.sourceReference,
    ...(row.trackingReferences ?? []), ...(row.supplierInvoiceNumbers ?? []), ...(row.billOfLadingNumbers ?? []),
  ]));
  const receipts = (receiptsQuery.data ?? []).filter(row => matches([
    row.partNumber, row.productName, row.supplierName, row.warehouseName,
    row.purchaseOrderNumber, row.receiptNumber, row.lotNumber, row.supplierInvoiceNumber, row.billOfLadingNumber,
  ]));

  return (
    <ProductsWorkspaceShell title="Incoming & receipts" subtitle="Supplier commitments, delivery locations and the receipts that brought stock into inventory.">
      <ProductsWorkspaceToolbar>
        <SearchField placeholder="Search part, supplier, location or reference" value={search}
          onChange={setSearch} width="min(100%, 380px)" />
        <Typography variant="body2" color="text.secondary">Search the loaded commitments and receipt lots.</Typography>
      </ProductsWorkspaceToolbar>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Supplier invoice and bill of lading (BL) numbers come from goods receipts. Missing references show “Not recorded”.
        Carrier tracking references are listed separately.
      </Typography>

      <Typography component="h2" variant="h6" sx={{ mb: 1 }}>Incoming supply</Typography>
      <QueryState loading={incomingQuery.isLoading} error={incomingQuery.isError} empty={!incoming.length}
        onRetry={() => void incomingQuery.refetch()} hasData={incomingQuery.data !== undefined} updatedAt={incomingQuery.dataUpdatedAt}
        emptyText={term ? 'No loaded incoming commitments match your search.' : 'No incoming supply is recorded.'}>
        <ResponsiveTable label="Incoming supply">
          <Table size="small" sx={tableStyles}>
            <TableHead><TableRow>
              <TableCell>Product</TableCell><TableCell>Part number</TableCell><TableCell>Supplier</TableCell>
              <TableCell>Supplier location</TableCell><TableCell>Receiving warehouse</TableCell><TableCell>PO #</TableCell>
              <TableCell align="right">Ordered</TableCell><TableCell align="right">Received</TableCell>
              <TableCell align="right">Outstanding</TableCell><TableCell>ETA</TableCell><TableCell>Status</TableCell>
              <TableCell>Supplier invoice #</TableCell><TableCell>BL #</TableCell><TableCell>Source reference</TableCell><TableCell>Carrier tracking reference</TableCell>
            </TableRow></TableHead>
            <TableBody>{incoming.map(row => <TableRow hover key={row.id}>
              <TableCell>{recorded(row.productName)}</TableCell><TableCell>{recorded(row.partNumber)}</TableCell>
              <TableCell>{recorded(row.supplierName)}</TableCell>
              <TableCell>{recorded([row.supplierCity, row.supplierCountry].filter(Boolean).join(', '))}</TableCell>
              <TableCell>{recorded(row.warehouseName)}</TableCell><TableCell>{recorded(row.purchaseOrderNumber)}</TableCell>
              <TableCell align="right">{row.orderedQuantity}</TableCell><TableCell align="right">{row.receivedQuantity}</TableCell>
              <TableCell align="right">{Math.max(0, row.orderedQuantity - row.receivedQuantity)}</TableCell>
              <TableCell sx={{ whiteSpace: 'nowrap' }}>{date(row.expectedAt)}</TableCell><TableCell><StatusChip value={row.status} /></TableCell>
              <TableCell>{recorded(row.supplierInvoiceNumbers?.join(', '))}</TableCell>
              <TableCell>{recorded(row.billOfLadingNumbers?.join(', '))}</TableCell>
              <TableCell>{recorded(row.sourceReference)}</TableCell><TableCell>{recorded(row.trackingReferences?.join(', '))}</TableCell>
            </TableRow>)}</TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>
      {(incomingQuery.data?.length ?? 0) >= 250 && <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>Showing the first 250 commitments by ETA.</Typography>}

      <Typography component="h2" variant="h6" sx={{ mt: 3, mb: 1 }}>Received lots</Typography>
      <QueryState loading={receiptsQuery.isLoading} error={receiptsQuery.isError} empty={!receipts.length}
        onRetry={() => void receiptsQuery.refetch()} hasData={receiptsQuery.data !== undefined} updatedAt={receiptsQuery.dataUpdatedAt}
        emptyText={term ? 'No loaded receipt lots match your search.' : 'No receipt lots are recorded. Lots appear after goods are received against a supplier purchase order.'}>
        <ResponsiveTable label="Received lots">
          <Table size="small" sx={tableStyles}>
            <TableHead><TableRow>
              <TableCell>Product</TableCell><TableCell>Part number</TableCell><TableCell>Supplier</TableCell><TableCell>Warehouse</TableCell>
              <TableCell>PO #</TableCell><TableCell>Receipt #</TableCell><TableCell>Lot / serial #</TableCell>
              <TableCell>Received on</TableCell><TableCell align="right">Received</TableCell><TableCell align="right">Remaining</TableCell>
              <TableCell>Supplier invoice #</TableCell><TableCell>BL #</TableCell>
            </TableRow></TableHead>
            <TableBody>{receipts.map(row => <TableRow hover key={row.id}>
              <TableCell>{recorded(row.productName)}</TableCell><TableCell>{recorded(row.partNumber)}</TableCell>
              <TableCell>{recorded(row.supplierName)}</TableCell><TableCell>{recorded(row.warehouseName)}</TableCell>
              <TableCell>{recorded(row.purchaseOrderNumber)}</TableCell><TableCell>{recorded(row.receiptNumber)}</TableCell>
              <TableCell><Link component={RouterLink} to={`/inventory/lots/${row.id}`} sx={{ display: 'inline-flex', alignItems: 'center', minHeight: 44 }}>{row.lotNumber}</Link></TableCell>
              <TableCell sx={{ whiteSpace: 'nowrap' }}>{date(row.receivedOn)}</TableCell>
              <TableCell align="right">{row.quantityReceived}</TableCell><TableCell align="right">{row.quantityRemaining}</TableCell>
              <TableCell>{recorded(row.supplierInvoiceNumber)}</TableCell><TableCell>{recorded(row.billOfLadingNumber)}</TableCell>
            </TableRow>)}</TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>
      {(receiptsQuery.data?.length ?? 0) >= 200 && <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>Showing the 200 most recent receipt lots. Open Traceability for additional lot filters.</Typography>}
    </ProductsWorkspaceShell>
  );
}
