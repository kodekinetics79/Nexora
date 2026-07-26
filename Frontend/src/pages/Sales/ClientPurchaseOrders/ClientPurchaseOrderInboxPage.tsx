import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Alert, Box, Button, Chip, CircularProgress, Paper, Stack, Table, TableBody,
  TableCell, TableContainer, TableHead, TableRow, TextField, Typography,
} from '@mui/material';
import { FindInPage, Refresh, Search } from '@mui/icons-material';
import customerAwardService from '../../../api/services/customerAwardService';

const readable = (value: string) => value.replaceAll('_', ' ');

export default function ClientPurchaseOrderInboxPage() {
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const query = useQuery({
    queryKey: ['client-purchase-orders', search],
    queryFn: () => customerAwardService.searchPurchaseOrders(search),
  });

  return <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1600, mx: 'auto' }}>
    <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', mb: 3 }}>
      <Box>
        <Typography variant="h4" component="h1" sx={{ fontWeight: 800 }}>Client PO Inbox</Typography>
        <Typography color="text.secondary">Match End Customer purchase orders to the accepted Customer Quote.</Typography>
      </Box>
      <Button startIcon={<Refresh />} onClick={() => query.refetch()} disabled={query.isFetching}>Refresh</Button>
    </Stack>

    <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
      <TextField fullWidth size="small" label="Search Client PO, Nexora Serial, Customer, or Quote"
        value={search} onChange={(event) => setSearch(event.target.value)}
        slotProps={{ input: { startAdornment: <Search sx={{ mr: 1, color: 'text.secondary' }} /> } }} />
    </Paper>

    {query.isLoading && <Box sx={{ minHeight: 320, display: 'grid', placeItems: 'center' }}><CircularProgress /></Box>}
    {query.isError && <Alert severity="error" action={<Button color="inherit" onClick={() => query.refetch()}>Retry</Button>}>
      Client POs could not be loaded. No empty result has been assumed.
    </Alert>}
    {query.data?.length === 0 && <Paper variant="outlined" sx={{ p: 5, textAlign: 'center' }}>
      <FindInPage color="disabled" sx={{ fontSize: 42 }} />
      <Typography sx={{ fontWeight: 700, mt: 1 }}>No Client POs match this view</Typography>
    </Paper>}
    {query.data && query.data.length > 0 && <TableContainer component={Paper} variant="outlined">
      <Table size="small">
        <TableHead><TableRow>
          <TableCell>Client PO</TableCell><TableCell>Commercial lineage</TableCell><TableCell>Customer</TableCell>
          <TableCell>Match outcome</TableCell><TableCell>Customer Order</TableCell><TableCell>Received</TableCell>
          <TableCell align="right">Action</TableCell>
        </TableRow></TableHead>
        <TableBody>{query.data.map((item) => <TableRow hover key={item.id}>
          <TableCell><Typography sx={{ fontWeight: 800 }}>{item.externalPoNumber}</Typography><Typography variant="caption">{item.internalNumber}</Typography></TableCell>
          <TableCell><Typography sx={{ fontFamily: 'monospace', fontWeight: 700 }}>{item.nexoraSerial}</Typography><Typography variant="caption">{item.quoteNumber || 'Quote match pending'}</Typography></TableCell>
          <TableCell>{item.customerName}</TableCell>
          <TableCell><Chip size="small" color={item.discrepancyCount > 0 ? 'warning' : 'success'} label={readable(item.matchOutcome)} />{item.discrepancyCount > 0 && <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>{item.discrepancyCount} line discrepancy</Typography>}</TableCell>
          <TableCell>{item.customerOrderNumber ? <Button size="small" onClick={() => navigate(`/sales/orders/${item.customerOrderId}`)}>{item.customerOrderNumber}</Button> : <Chip size="small" variant="outlined" label="Not created" />}</TableCell>
          <TableCell>{new Date(item.receivedOn).toLocaleDateString()}</TableCell>
          <TableCell align="right"><Button startIcon={<FindInPage />} onClick={() => navigate(`/sales/client-pos/${item.id}`)}>Review match</Button></TableCell>
        </TableRow>)}</TableBody>
      </Table>
    </TableContainer>}
  </Box>;
}
