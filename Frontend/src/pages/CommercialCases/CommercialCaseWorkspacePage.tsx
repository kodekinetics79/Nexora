import React from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box,
  Breadcrumbs,
  Button,
  Chip,
  Divider,
  Grid,
  Link,
  List,
  ListItemButton,
  Paper,
  Stack,
  Tab,
  Tabs,
  Typography,
  CircularProgress,
  Alert,
} from '@mui/material';
import {
  ArrowForward as OpenIcon,
  NavigateNext as NextIcon,
  Search as SearchIcon,
  Assignment as LeadIcon,
  ReceiptLong as RfqIcon,
  Description as QuoteIcon,
  AssignmentTurnedIn as OrderIcon,
  LocalShipping as ShipmentIcon,
} from '@mui/icons-material';
import commercialCaseService, {
  type CommercialCaseDetail,
  type CommercialCaseDocument,
} from '../../api/services/commercialCaseService';
import SearchField from '../../components/common/SearchField';
import { formatDateSafe } from '../../utils/dates';

const DOC_ORDER: CommercialCaseDocument['documentType'][] = ['Lead', 'RFQ', 'Quote', 'Order', 'Shipment'];

const DataField: React.FC<{ label: string; value: React.ReactNode }> = ({ label, value }) => (
  <Box>
    <Typography
      variant="caption"
      sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 0.35, fontSize: '0.65rem' }}
    >
      {label}
    </Typography>
    <Typography sx={{ fontWeight: 700, color: 'text.primary', lineHeight: 1.45 }}>
      {value}
    </Typography>
  </Box>
);

const typeIcon = (type: CommercialCaseDocument['documentType']) => {
  switch (type) {
    case 'Lead': return <LeadIcon fontSize="small" />;
    case 'RFQ': return <RfqIcon fontSize="small" />;
    case 'Quote': return <QuoteIcon fontSize="small" />;
    case 'Order': return <OrderIcon fontSize="small" />;
    case 'Shipment': return <ShipmentIcon fontSize="small" />;
    default: return <LeadIcon fontSize="small" />;
  }
};

const openDocument = (navigate: ReturnType<typeof useNavigate>, doc: CommercialCaseDocument) => {
  const routes: Record<string, string> = {
    Lead: `/procurement/leads/view/${doc.documentId}`,
    RFQ: `/procurement/rfqs/view/${doc.documentId}`,
    Quote: `/sales/quotes/view/${doc.documentId}`,
    Order: `/sales/orders/${doc.documentId}`,
    Shipment: `/sales/shipments/${doc.documentId}`,
  };
  const target = routes[doc.documentType];
  if (target) navigate(target);
};

const caseAge = (createdOn: string) => {
  const created = new Date(createdOn);
  if (Number.isNaN(created.getTime())) return '—';
  const days = Math.max(0, Math.floor((Date.now() - created.getTime()) / (1000 * 60 * 60 * 24)));
  if (days === 0) return 'Today';
  if (days === 1) return '1 day old';
  return `${days} days old`;
};

const CommercialCaseWorkspacePage: React.FC = () => {
  const navigate = useNavigate();
  const { id } = useParams<{ id?: string }>();
  const [query, setQuery] = React.useState('');
  const [tab, setTab] = React.useState(0);

  const searchTerm = query.trim();
  const { data: searchResults, isLoading: searchLoading } = useQuery({
    queryKey: ['commercial-cases', 'search', searchTerm],
    queryFn: () => commercialCaseService.search(searchTerm, 25),
    enabled: searchTerm.length >= 2,
  });

  const selectedCaseId = React.useMemo(() => {
    if (id && Number.isFinite(Number(id))) return Number(id);
    return searchResults?.[0]?.id;
  }, [id, searchResults]);

  const { data: detail, isLoading: detailLoading } = useQuery({
    queryKey: ['commercial-case', selectedCaseId],
    queryFn: () => commercialCaseService.getById(selectedCaseId ?? 0),
    enabled: !!selectedCaseId,
  });

  const selectedResult = React.useMemo(
    () => searchResults?.find(item => item.id === selectedCaseId) ?? null,
    [searchResults, selectedCaseId]
  );

  React.useEffect(() => {
    setTab(0);
  }, [selectedCaseId]);

  const documentsByType = React.useMemo(() => {
    const entries = new Map<CommercialCaseDocument['documentType'], CommercialCaseDocument[]>();
    for (const type of DOC_ORDER) entries.set(type, []);
    for (const doc of detail?.documents ?? []) {
      const current = entries.get(doc.documentType) ?? [];
      current.push(doc);
      entries.set(doc.documentType, current);
    }
    return entries;
  }, [detail]);

  const counts = React.useMemo(() => {
    const docs = detail?.documents ?? [];
    return {
      leads: docs.filter(doc => doc.documentType === 'Lead').length,
      rfqs: docs.filter(doc => doc.documentType === 'RFQ').length,
      quotes: docs.filter(doc => doc.documentType === 'Quote').length,
      orders: docs.filter(doc => doc.documentType === 'Order').length,
      shipments: docs.filter(doc => doc.documentType === 'Shipment').length,
    };
  }, [detail]);

  const renderWorkspaceDetail = (current: CommercialCaseDetail) => (
    <Stack spacing={2.5}>
      <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
        <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { xs: 'flex-start', lg: 'center' } }}>
          <Box>
            <Stack direction="row" spacing={1.25} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
              <Typography sx={{ fontSize: '1.45rem', fontWeight: 950, letterSpacing: '-0.02em' }}>
                {current.masterReference}
              </Typography>
              <Chip
                label={current.currentStatus ?? 'Open'}
                size="small"
                sx={{ fontWeight: 900, textTransform: 'uppercase', height: 24 }}
                color="primary"
                variant="outlined"
              />
              <Chip label={caseAge(current.createdOn)} size="small" sx={{ fontWeight: 800, height: 24 }} />
            </Stack>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75, fontWeight: 600 }}>
              {current.buyerName || 'Unknown buyer'}
              {current.customerEmail ? ` · ${current.customerEmail}` : ''}
            </Typography>
          </Box>
          <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }}>
            <Chip icon={<LeadIcon />} label={`Lead ${counts.leads}`} size="small" />
            <Chip icon={<RfqIcon />} label={`RFQs ${counts.rfqs}`} size="small" />
            <Chip icon={<QuoteIcon />} label={`Quotes ${counts.quotes}`} size="small" />
            <Chip icon={<OrderIcon />} label={`Orders ${counts.orders}`} size="small" />
            <Chip icon={<ShipmentIcon />} label={`Shipments ${counts.shipments}`} size="small" />
          </Stack>
        </Stack>

        <Box sx={{ mt: 2.5 }}>
          <Tabs value={tab} onChange={(_, value) => setTab(value)} sx={{ minHeight: 40 }}>
            <Tab label={`Overview (${current.documents.length})`} />
            <Tab label={`Documents (${current.documents.length})`} />
            <Tab label={`Activity (${current.statusHistory.length})`} />
          </Tabs>
        </Box>
      </Paper>

      {tab === 0 && (
        <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
          <Grid container spacing={2.5}>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Master Reference" value={current.masterReference} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Lead ID" value={current.leadId} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Business Unit" value={current.businessUnitId} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Buyer" value={current.buyerName ?? '—'} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Customer Email" value={current.customerEmail ?? '—'} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Opportunity" value={current.opportunityNumber ?? '—'} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Received" value={formatDateSafe(current.createdOn)} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Customer RFQ" value={current.customerRfqNumber ?? '—'} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Allocation Number" value={current.allocationNumber} />
            </Grid>
          </Grid>
        </Paper>
      )}

      {tab === 1 && (
        <Stack spacing={2}>
          {DOC_ORDER.map(type => {
            const docs = documentsByType.get(type) ?? [];
            if (docs.length === 0) return null;
            return (
              <Paper key={type} sx={{ p: 2.5, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
                <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 2 }}>
                  {typeIcon(type)}
                  <Typography sx={{ fontWeight: 900, textTransform: 'uppercase', letterSpacing: '0.025em' }}>
                    {type}
                  </Typography>
                  <Chip label={docs.length} size="small" sx={{ height: 20, fontWeight: 900 }} />
                </Stack>
                <Stack spacing={1}>
                  {docs.map(doc => (
                    <Stack
                      key={`${doc.documentType}-${doc.documentId}`}
                      direction="row"
                      spacing={1.5}
                      sx={{
                        alignItems: 'center',
                        justifyContent: 'space-between',
                        p: 1.5,
                        border: '1px solid',
                        borderColor: 'divider',
                        borderRadius: 1.5,
                      }}
                    >
                      <Box>
                        <Typography sx={{ fontWeight: 800 }}>{doc.reference}</Typography>
                        <Typography variant="caption" color="text.secondary">
                          {doc.status ?? 'Open'}
                          {doc.occurredOn ? ` · ${formatDateSafe(doc.occurredOn)}` : ''}
                        </Typography>
                      </Box>
                      <Button
                        size="small"
                        variant="outlined"
                        startIcon={<OpenIcon />}
                        onClick={() => openDocument(navigate, doc)}
                        sx={{ fontWeight: 800, borderRadius: 2 }}
                      >
                        Open
                      </Button>
                    </Stack>
                  ))}
                </Stack>
              </Paper>
            );
          })}
        </Stack>
      )}

      {tab === 2 && (
        <Stack spacing={1.5}>
          {current.statusHistory.length === 0 && (
            <Alert severity="info" sx={{ borderRadius: 2 }}>
              No workspace activity has been recorded yet.
            </Alert>
          )}
          {current.statusHistory.map(event => (
            <Paper key={event.id} sx={{ p: 2.25, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
              <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} sx={{ justifyContent: 'space-between' }}>
                <Box>
                  <Typography sx={{ fontWeight: 900 }}>
                    {event.previousStatus ?? 'None'} → {event.newStatus ?? 'None'}
                  </Typography>
                  <Typography variant="body2" color="text.secondary">
                    {event.eventType} · {event.actorSource}
                    {event.changedBy ? ` · ${event.changedBy}` : ''}
                  </Typography>
                </Box>
                <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 700 }}>
                  {formatDateSafe(event.changedOn)}
                </Typography>
              </Stack>
              {event.reason && (
                <Typography variant="body2" sx={{ mt: 1, color: 'text.primary' }}>
                  {event.reason}
                </Typography>
              )}
              <Stack direction="row" spacing={1} sx={{ mt: 1, flexWrap: 'wrap' }}>
                {event.aggregateType && <Chip label={event.aggregateType} size="small" variant="outlined" />}
                {event.correlationId && <Chip label={`Correlation ${event.correlationId}`} size="small" variant="outlined" />}
                {event.requestReference && <Chip label={`Request ${event.requestReference}`} size="small" variant="outlined" />}
                {event.reasonCode && <Chip label={`Reason ${event.reasonCode}`} size="small" variant="outlined" />}
              </Stack>
            </Paper>
          ))}
        </Stack>
      )}
    </Stack>
  );

  return (
    <Box sx={{ p: 3, maxWidth: 1800, mx: 'auto' }}>
      <Breadcrumbs separator={<NextIcon sx={{ fontSize: 14 }} />} sx={{ mb: 2 }}>
        <Link component="button" variant="caption" onClick={() => navigate('/dashboard')} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
          Dashboard
        </Link>
        <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 900, textTransform: 'uppercase' }}>
          Commercial Workspace
        </Typography>
      </Breadcrumbs>

      <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { xs: 'flex-start', lg: 'center' }, mb: 2.5 }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 950, letterSpacing: '-0.02em' }}>
            Commercial Workspace
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
            Search a lead by its permanent reference and open the full commercial trail in one place.
          </Typography>
        </Box>
        <Box sx={{ minWidth: { xs: '100%', lg: 420 }, width: { xs: '100%', lg: 420 } }}>
          <SearchField
            width="100%"
            value={query}
            onChange={setQuery}
            placeholder="Search by master reference, buyer, RFQ, quote, order, shipment..."
          />
        </Box>
      </Stack>

      <Grid container spacing={2.5}>
        <Grid size={{ xs: 12, lg: 4 }}>
          <Paper sx={{ p: 2.5, borderRadius: 2, border: '1px solid', borderColor: 'divider', minHeight: 640 }}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1.75 }}>
              <SearchIcon fontSize="small" />
              <Typography sx={{ fontWeight: 900, textTransform: 'uppercase', letterSpacing: '0.025em' }}>
                Search Results
              </Typography>
            </Stack>
            <Divider sx={{ mb: 2 }} />

            {searchTerm.length < 2 && (
              <Alert severity="info" sx={{ borderRadius: 2 }}>
                Enter at least two characters to search the workspace.
              </Alert>
            )}

            {searchLoading && (
              <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
                <CircularProgress size={28} />
              </Box>
            )}

            {!searchLoading && searchTerm.length >= 2 && (searchResults?.length ?? 0) === 0 && (
              <Alert severity="warning" sx={{ borderRadius: 2 }}>
                No commercial cases matched your search.
              </Alert>
            )}

            <List sx={{ mt: 1 }}>
              {(searchResults ?? []).map(item => (
                <ListItemButton
                  key={item.id}
                  selected={item.id === selectedCaseId}
                  onClick={() => navigate(`/commercial-cases/${item.id}`)}
                  sx={{
                    mb: 1,
                    borderRadius: 2,
                    border: '1px solid',
                    borderColor: item.id === selectedCaseId ? 'primary.main' : 'divider',
                    alignItems: 'flex-start',
                  }}
                >
                  <Stack spacing={0.75} sx={{ width: '100%' }}>
                    <Stack direction="row" spacing={1} sx={{ justifyContent: 'space-between', alignItems: 'center' }}>
                      <Typography sx={{ fontWeight: 900, fontFamily: 'monospace', color: 'primary.main' }}>
                        {item.masterReference}
                      </Typography>
                      <Chip label={item.status ?? 'Open'} size="small" sx={{ height: 20, fontWeight: 800 }} />
                    </Stack>
                    <Typography sx={{ fontWeight: 700 }}>{item.buyerName ?? 'Unknown buyer'}</Typography>
                    <Typography variant="body2" color="text.secondary">
                      {item.customerRfqNumber ?? 'No customer RFQ'}{item.customerEmail ? ` · ${item.customerEmail}` : ''}
                    </Typography>
                    <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', pt: 0.5 }}>
                      <Chip label={`RFQs ${item.rfqCount}`} size="small" variant="outlined" />
                      <Chip label={`Quotes ${item.quoteCount}`} size="small" variant="outlined" />
                      <Chip label={`Orders ${item.orderCount}`} size="small" variant="outlined" />
                      <Chip label={`Shipments ${item.shipmentCount}`} size="small" variant="outlined" />
                    </Stack>
                  </Stack>
                </ListItemButton>
              ))}
            </List>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, lg: 8 }}>
          {detailLoading && (
            <Paper sx={{ p: 4, borderRadius: 2, border: '1px solid', borderColor: 'divider', minHeight: 640 }}>
              <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
                <CircularProgress />
              </Box>
            </Paper>
          )}

          {!detailLoading && detail && renderWorkspaceDetail(detail)}

          {!detailLoading && !detail && (
            <Paper sx={{ p: 4, borderRadius: 2, border: '1px solid', borderColor: 'divider', minHeight: 640 }}>
              <Alert severity="info" sx={{ borderRadius: 2 }}>
                Search for a commercial case to open the master reference, document trail, and activity timeline.
              </Alert>
            </Paper>
          )}

          {!detailLoading && selectedResult && !detail && (
            <Paper sx={{ p: 4, borderRadius: 2, border: '1px solid', borderColor: 'divider', minHeight: 640 }}>
              <Alert severity="warning" sx={{ borderRadius: 2 }}>
                We found {selectedResult.masterReference}, but the detail view could not load.
              </Alert>
            </Paper>
          )}
        </Grid>
      </Grid>
    </Box>
  );
};

export default CommercialCaseWorkspacePage;
