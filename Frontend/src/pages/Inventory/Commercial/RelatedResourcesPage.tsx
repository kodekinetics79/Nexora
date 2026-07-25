import { useQuery } from '@tanstack/react-query';
import { Button, Paper, Typography } from '@mui/material';
import { useNavigate } from 'react-router-dom';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { useAuth } from '../../../context/AuthContext';
import { PageShell, QueryState } from '../../SalesManagement/CommercialPagePrimitives';

export default function RelatedResourcesPage() {
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const query = useQuery({ queryKey: ['inventory-intelligence', 'related-resources'], queryFn: commercialIntelligenceService.getRelatedResources });
  const rows = query.data ?? [];
  return <PageShell title="Related resources" subtitle="Persisted inventory resources and connected operational workspaces."><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No related inventory resources are available."><Paper variant="outlined" sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(2, minmax(0, 1fr))' } }}>{rows.map(row => { const allowed = !row.requiredModule || hasPermission(row.requiredModule); return <Paper key={row.key} elevation={0} sx={{ p: 2.5, borderRadius: 0, borderBottom: '1px solid', borderRight: { md: '1px solid' }, borderColor: 'divider' }}><Typography sx={{ fontWeight: 800 }}>{row.label}</Typography><Typography variant="body2" color="text.secondary" sx={{ my: 1 }}>{row.description}</Typography>{row.recordCount != null && <Typography variant="caption" sx={{ mb: 1, display: 'block' }}>{row.recordCount.toLocaleString()} records</Typography>}{row.route && allowed && <Button size="small" onClick={() => navigate(row.route!)}>Open resource</Button>}</Paper>; })}</Paper></QueryState></PageShell>;
}
