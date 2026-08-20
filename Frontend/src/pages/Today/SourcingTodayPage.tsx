import { useQuery } from '@tanstack/react-query';
import { Button, Chip, Table, TableBody, TableCell, TableHead, TableRow } from '@mui/material';
import { OpenInNew } from '@mui/icons-material';
import { useNavigate } from 'react-router-dom';
import supplierQuoteService from '../../api/services/supplierQuoteService';
import { PageShell, QueryState, ResponsiveTable, formatDateTime } from '../SalesManagement/CommercialPagePrimitives';
import { statusLabel } from '../../utils/statusLabels';

export default function SourcingTodayPage() {
  const navigate = useNavigate();
  const query = useQuery({ queryKey: ['sourcing-today'], queryFn: () => supplierQuoteService.getInbox(), refetchInterval: 60_000 });
  const rows = query.data ?? [];
  const reviewRequired = rows.filter(row => row.inboxStatus === 'REVIEW_REQUIRED').length;
  const ready = rows.filter(row => row.inboxStatus === 'READY_FOR_COMPARISON').length;
  return <PageShell title="Sourcing today" subtitle={`${reviewRequired} Supplier Quote(s) need review; ${ready} are ready for comparison.`} actions={<Button variant="outlined" onClick={() => navigate('/procurement/rfqs/all?state=requires-sourcing')}>Open sourcing queue</Button>}>
    <QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No Supplier Quote responses require sourcing attention.">
      <ResponsiveTable label="Sourcing attention"><Table size="small"><TableHead><TableRow><TableCell>Supplier</TableCell><TableCell>Quote</TableCell><TableCell>Nexora Serial</TableCell><TableCell>Status</TableCell><TableCell>Review fields</TableCell><TableCell>Updated</TableCell><TableCell>Action</TableCell></TableRow></TableHead><TableBody>
        {rows.map(row => <TableRow hover key={row.supplierQuoteId}><TableCell>{row.supplierName}</TableCell><TableCell>{row.supplierQuoteReference}</TableCell><TableCell>{row.nexoraSerial}</TableCell><TableCell><Chip size="small" label={statusLabel(row.inboxStatus)} color={row.inboxStatus === 'REVIEW_REQUIRED' ? 'warning' : 'success'} /></TableCell><TableCell>{row.reviewRequiredCount}</TableCell><TableCell>{formatDateTime(row.updatedOn)}</TableCell><TableCell><Button size="small" endIcon={<OpenInNew />} onClick={() => navigate(`/procurement/supplier-quotes/${row.supplierQuoteId}`)}>Review</Button></TableCell></TableRow>)}
      </TableBody></Table></ResponsiveTable>
    </QueryState>
  </PageShell>;
}
