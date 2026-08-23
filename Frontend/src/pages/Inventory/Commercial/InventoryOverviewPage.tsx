import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Box, Button, Table, TableBody, TableCell, TableHead, TableRow } from '@mui/material';
import { OpenInNew } from '@mui/icons-material';
import { useNavigate } from 'react-router-dom';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { useAuth } from '../../../context/AuthContext';
import { MetricGrid, PageShell, QueryState, ResponsiveTable, StatusChip, formatDateTime } from '../../SalesManagement/CommercialPagePrimitives';
import OpeningStockDialog from './OpeningStockDialog';

export default function InventoryOverviewPage() {
  const navigate = useNavigate();
  // This is the screen a customer lands on first, and on day one it is the screen from which the
  // module has to be initialised: no product created through the UI has an inventory row, so no
  // stock grid can show one, so the only way in is a door that needs no row.
  const canEdit = useAuth().hasPermission('Products', 'edit');
  const [opening, setOpening] = useState(false);
  const query = useQuery({ queryKey: ['inventory-intelligence', 'overview'], queryFn: commercialIntelligenceService.getInventoryOverview, refetchInterval: 60_000 });
  const rows = query.data?.exceptions ?? [];
  return <PageShell title="Inventory today" subtitle="Availability and supply exceptions affecting current commercial demand." actions={<Box sx={{ display: 'flex', gap: 1, alignItems: 'center', flexWrap: 'wrap' }}>{canEdit && <Button variant="outlined" onClick={() => setOpening(true)}>Record opening stock</Button>}<Button variant="outlined" onClick={() => navigate('/inventory/demand')}>View demand intelligence</Button></Box>}><MetricGrid metrics={query.data?.metrics ?? []} /><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No inventory exceptions require attention."><ResponsiveTable label="Inventory exceptions"><Table size="small"><TableHead><TableRow><TableCell>Part</TableCell><TableCell>Product</TableCell><TableCell>Warehouse</TableCell><TableCell>Exception</TableCell><TableCell align="right">Available</TableCell><TableCell align="right">Required</TableCell><TableCell>Due</TableCell><TableCell>Action</TableCell></TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.id}><TableCell>{row.partNumber}</TableCell><TableCell>{row.productName}</TableCell><TableCell>{row.warehouseName || 'All warehouses'}</TableCell><TableCell><StatusChip value={row.exceptionType} /></TableCell><TableCell align="right">{row.availableQuantity}</TableCell><TableCell align="right">{row.requiredQuantity ?? 'Not recorded'}</TableCell><TableCell>{formatDateTime(row.dueAt)}</TableCell><TableCell>{row.productId ? <Button size="small" endIcon={<OpenInNew />} onClick={() => navigate(`/inventory/products/${row.productId}`)}>Open product</Button> : 'Product unresolved'}</TableCell></TableRow>)}</TableBody></Table></ResponsiveTable></QueryState>{opening && <OpeningStockDialog open onClose={() => setOpening(false)} />}</PageShell>;
}
