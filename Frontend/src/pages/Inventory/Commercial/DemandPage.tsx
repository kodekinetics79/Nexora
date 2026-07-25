import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Table, TableBody, TableCell, TableHead, TableRow, TextField } from '@mui/material';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { PageShell, QueryState, ResponsiveTable, StatusChip, formatDateTime } from '../../SalesManagement/CommercialPagePrimitives';

export default function DemandPage() {
  const [search, setSearch] = useState('');
  const query = useQuery({ queryKey: ['inventory-intelligence', 'demand', search], queryFn: () => commercialIntelligenceService.getDemand({ search: search || undefined }) });
  const rows = query.data ?? [];
  return <PageShell title="Demand" subtitle="Consolidated commercial demand compared with available and incoming supply." actions={<TextField size="small" label="Search part or product" value={search} onChange={event => setSearch(event.target.value)} />}><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No open inventory demand matches this view."><ResponsiveTable label="Inventory demand"><Table size="small"><TableHead><TableRow><TableCell>Part</TableCell><TableCell>Product</TableCell><TableCell align="right">Open demand</TableCell><TableCell align="right">Available</TableCell><TableCell align="right">Incoming</TableCell><TableCell align="right">Shortfall</TableCell><TableCell>Earliest need</TableCell><TableCell>Status</TableCell></TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.productId}><TableCell>{row.partNumber}</TableCell><TableCell>{row.productName}</TableCell><TableCell align="right">{row.openDemand}</TableCell><TableCell align="right">{row.available}</TableCell><TableCell align="right">{row.incoming}</TableCell><TableCell align="right">{row.shortfall}</TableCell><TableCell>{formatDateTime(row.earliestNeedAt)}</TableCell><TableCell><StatusChip value={row.shortfall > 0 ? 'Supply shortfall' : 'Covered'} /></TableCell></TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
