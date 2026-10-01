import { useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Box,
  Button,
  Divider,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material';
import { alpha } from '@mui/material/styles';
import {
  ArrowForward,
  HandshakeOutlined,
  Inventory2Outlined,
  LocalShippingOutlined,
  OpenInNew,
  WarningAmberOutlined,
} from '@mui/icons-material';
import { useNavigate } from 'react-router-dom';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { useAuth } from '../../../context/AuthContext';
import {
  MetricGrid,
  PageShell,
  QueryState,
  ResponsiveTable,
  StatusChip,
  formatDateTime,
} from '../../SalesManagement/CommercialPagePrimitives';
import OpeningStockDialog from './OpeningStockDialog';

type RouteAction = {
  label: string;
  path: string;
};

const workflow: Array<{
  title: string;
  description: string;
  action: RouteAction;
  icon: ReactNode;
}> = [
  {
    title: 'Available now',
    description: 'Confirm sellable stock after quality holds and safety stock, warehouse by warehouse.',
    action: { label: 'Check availability', path: '/inventory/availability' },
    icon: <Inventory2Outlined />,
  },
  {
    title: 'Shortfall',
    description: 'See the lines that stock cannot cover and the products already below their minimum.',
    action: { label: 'Review shortages', path: '/inventory/reorder-alerts' },
    icon: <WarningAmberOutlined />,
  },
  {
    title: 'Incoming or buy-to-order',
    description: 'Check ordered quantities and expected dates before committing to a customer.',
    action: { label: 'Track incoming stock', path: '/inventory/incoming' },
    icon: <LocalShippingOutlined />,
  },
  {
    title: 'Supplier route',
    description: 'Use known partners first: In Network, then Extended Network. Discover a new supplier only when neither fits.',
    action: { label: 'Open supplier network', path: '/suppliers' },
    icon: <HandshakeOutlined />,
  },
];

const toolGroups: Array<{ title: string; description: string; actions: RouteAction[] }> = [
  {
    title: 'Catalogue & pricing',
    description: 'Define the items you sell and the commercial defaults used when quoting.',
    actions: [
      { label: 'Products', path: '/inventory/products' },
      { label: 'Pricing sheet', path: '/inventory/pricing-sheet' },
      { label: 'Categories', path: '/inventory/categories' },
    ],
  },
  {
    title: 'Stock position',
    description: 'Understand where stock is, what is committed and how quantities changed.',
    actions: [
      { label: 'Warehouse stock', path: '/inventory/warehouses' },
      { label: 'Stock movements', path: '/inventory/movements' },
    ],
  },
  {
    title: 'Planning & replenishment',
    description: 'Set holding rules and use demand evidence to decide what to replenish.',
    actions: [
      { label: 'Demand', path: '/inventory/demand' },
      { label: 'Stock levels', path: '/inventory/levels' },
      { label: 'Incoming stock', path: '/inventory/incoming' },
    ],
  },
  {
    title: 'Control & traceability',
    description: 'Reconcile physical stock and retain the evidence needed to trace material.',
    actions: [
      { label: 'Count variance', path: '/inventory/count-variance' },
      { label: 'Stock ageing', path: '/inventory/ageing' },
      { label: 'Lots & traceability', path: '/inventory/lots' },
      { label: 'Where-used trace', path: '/inventory/order-trace' },
    ],
  },
];

const exceptionLabel = (value: string) => value
  .replace(/([a-z])([A-Z])/g, '$1 $2')
  .replace(/[_-]+/g, ' ')
  .replace(/^./, (character) => character.toUpperCase());

export default function InventoryOverviewPage() {
  const navigate = useNavigate();
  // This is the screen a customer lands on first, and on day one it is the screen from which the
  // module has to be initialised: no product created through the UI has an inventory row, so no
  // stock grid can show one, so the only way in is a door that needs no row.
  const canEdit = useAuth().hasPermission('Products', 'edit');
  const [opening, setOpening] = useState(false);
  const query = useQuery({
    queryKey: ['inventory-intelligence', 'overview'],
    queryFn: commercialIntelligenceService.getInventoryOverview,
    refetchInterval: 60_000,
    meta: { silenceGlobalError: true },
  });
  const rows = query.data?.exceptions ?? [];

  return (
    <PageShell
      title="Inventory workspace"
      subtitle="Start with available stock, then move outward only as the requirement demands."
      actions={(
        <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', flexWrap: 'wrap' }}>
          <Button variant="outlined" onClick={() => navigate('/suppliers')}>Supplier directory</Button>
          {canEdit && <Button variant="contained" onClick={() => setOpening(true)}>Record opening stock</Button>}
        </Box>
      )}
    >
      <Paper variant="outlined" sx={{ mb: 2.5, overflow: 'hidden' }}>
        <Box sx={{ p: { xs: 2, md: 2.5 }, pb: { xs: 1.5, md: 2 } }}>
          <Typography variant="h6" sx={{ fontWeight: 900 }}>Fulfil from the closest source</Typography>
          <Typography variant="body2" color="text.secondary">
            Follow this sequence for every requested line. Each step keeps the next option visible without skipping evidence.
          </Typography>
        </Box>
        <Divider />
        <Box
          component="ol"
          aria-label="Inventory fulfilment sequence"
          sx={{
            listStyle: 'none',
            m: 0,
            p: 0,
            display: 'grid',
            gridTemplateColumns: { xs: '1fr', md: 'repeat(2, minmax(0, 1fr))', xl: 'repeat(4, minmax(0, 1fr))' },
          }}
        >
          {workflow.map((step, index) => (
            <Box
              component="li"
              key={step.title}
              sx={{
                p: 2,
                minWidth: 0,
                borderRight: {
                  md: index % 2 === 0 ? '1px solid' : 0,
                  xl: index < workflow.length - 1 ? '1px solid' : 0,
                },
                borderBottom: {
                  xs: index < workflow.length - 1 ? '1px solid' : 0,
                  md: index < 2 ? '1px solid' : 0,
                  xl: 0,
                },
                borderColor: 'divider',
              }}
            >
              <Stack direction="row" spacing={1.25} sx={{ alignItems: 'flex-start' }}>
                <Box
                  aria-hidden="true"
                  sx={{
                    width: 36,
                    height: 36,
                    flex: '0 0 auto',
                    borderRadius: 1.5,
                    display: 'grid',
                    placeItems: 'center',
                    color: 'primary.dark',
                    bgcolor: (theme) => alpha(theme.palette.primary.main, 0.12),
                    '& svg': { fontSize: 21 },
                  }}
                >
                  {step.icon}
                </Box>
                <Box sx={{ minWidth: 0 }}>
                  <Typography variant="overline" color="text.secondary" sx={{ fontWeight: 800, lineHeight: 1.2 }}>
                    Step {index + 1}
                  </Typography>
                  <Typography variant="subtitle1" sx={{ fontWeight: 900, lineHeight: 1.25 }}>{step.title}</Typography>
                </Box>
              </Stack>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 1.25, minHeight: { xl: 63 } }}>
                {step.description}
              </Typography>
              <Button
                size="small"
                endIcon={<ArrowForward />}
                onClick={() => navigate(step.action.path)}
                sx={{ mt: 1, px: 0.5, minHeight: 44 }}
              >
                {step.action.label}
              </Button>
            </Box>
          ))}
        </Box>
      </Paper>

      <MetricGrid metrics={query.data?.metrics ?? []} />

      <Box sx={{ mb: 3 }}>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', sm: 'center' }, mb: 1.25 }}>
          <Box>
            <Typography variant="h6" sx={{ fontWeight: 900 }}>Exceptions requiring attention</Typography>
            <Typography variant="body2" color="text.secondary">Shortages and stock conditions that can affect an open commercial requirement.</Typography>
          </Box>
          <Button variant="text" endIcon={<ArrowForward />} onClick={() => navigate('/inventory/reorder-alerts')}>
            All reorder alerts
          </Button>
        </Stack>
        <QueryState
          loading={query.isLoading}
          error={query.isError}
          hasData={query.data !== undefined}
          updatedAt={query.dataUpdatedAt}
          empty={!rows.length}
          onRetry={() => void query.refetch()}
          emptyText="No inventory exceptions require attention."
        >
          <ResponsiveTable label="Inventory exceptions">
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>Part</TableCell>
                  <TableCell>Product</TableCell>
                  <TableCell>Warehouse</TableCell>
                  <TableCell>Exception</TableCell>
                  <TableCell align="right">Available</TableCell>
                  <TableCell align="right">Required</TableCell>
                  <TableCell>Due</TableCell>
                  <TableCell>Action</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {rows.map((row) => (
                  <TableRow hover key={row.id}>
                    <TableCell>{row.partNumber}</TableCell>
                    <TableCell>{row.productName}</TableCell>
                    <TableCell>{row.warehouseName || 'All warehouses'}</TableCell>
                    <TableCell><StatusChip value={exceptionLabel(row.exceptionType)} /></TableCell>
                    <TableCell align="right">{row.availableQuantity}</TableCell>
                    <TableCell align="right">{row.requiredQuantity ?? 'Not recorded'}</TableCell>
                    <TableCell>{formatDateTime(row.dueAt)}</TableCell>
                    <TableCell>
                      {row.productId
                        ? (
                          <Button size="small" endIcon={<OpenInNew />} onClick={() => navigate(`/inventory/products/${row.productId}`)}>
                            Open product
                          </Button>
                        )
                        : 'Product unresolved'}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </ResponsiveTable>
        </QueryState>
      </Box>

      <Box>
        <Typography variant="h6" sx={{ fontWeight: 900 }}>Inventory controls</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1.25 }}>
          Detailed tools stay grouped here and remain searchable in the screen directory.
        </Typography>
        <Paper variant="outlined" sx={{ overflow: 'hidden' }}>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(2, minmax(0, 1fr))' } }}>
            {toolGroups.map((group, index) => (
              <Box
                key={group.title}
                sx={{
                  p: 2,
                  minWidth: 0,
                  borderRight: { md: index % 2 === 0 ? '1px solid' : 0 },
                  borderBottom: {
                    xs: index < toolGroups.length - 1 ? '1px solid' : 0,
                    md: index < toolGroups.length - 2 ? '1px solid' : 0,
                  },
                  borderColor: 'divider',
                }}
              >
                <Typography variant="subtitle1" sx={{ fontWeight: 900 }}>{group.title}</Typography>
                <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>{group.description}</Typography>
                <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.5 }}>
                  {group.actions.map((action) => (
                    <Button key={action.path} size="small" onClick={() => navigate(action.path)} sx={{ minHeight: 40 }}>
                      {action.label}
                    </Button>
                  ))}
                </Box>
              </Box>
            ))}
          </Box>
        </Paper>
      </Box>

      {opening && <OpeningStockDialog open onClose={() => setOpening(false)} />}
    </PageShell>
  );
}
