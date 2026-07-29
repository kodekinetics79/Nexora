import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Chip, Grid,
  CircularProgress, Divider, Avatar, Stack, Table, TableBody,
  TableCell, TableContainer, TableHead, TableRow, Alert,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Edit as EditIcon,
  Person as CustomerIcon,
  Email as EmailIcon,
  Receipt as BillingIcon,
  LocalShipping as ShippingIcon,
  Contacts as ContactsIcon,
  Insights as InsightsIcon,
  History as HistoryIcon,
  SupervisorAccount as OwnershipIcon,
  OpenInNew as OpenIcon,
} from '@mui/icons-material';
import customerService from '../../api/services/customerService';
import contactService from '../../api/services/contactService';
import intelligenceService from '../../api/services/intelligenceService';
import commercialLearningService from '../../api/services/commercialLearningService';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';

const healthWindow = () => {
  const to = new Date();
  const from = new Date(to);
  from.setUTCDate(from.getUTCDate() - 90);
  return { from: from.toISOString().slice(0, 10), to: to.toISOString().slice(0, 10) };
};

const metricValue = (value: number | null | undefined, suffix = '') => value == null ? 'Insufficient evidence' : `${value.toLocaleString()}${suffix}`;

const InfoRow: React.FC<{ label: string; value: React.ReactNode }> = ({ label, value }) => (
  <Box sx={{ display: 'flex', flexDirection: { xs: 'column', sm: 'row' }, gap: { xs: 0.25, sm: 2 }, py: 0.9, borderBottom: '1px solid', borderColor: 'divider', alignItems: { xs: 'flex-start', sm: 'center' }, minWidth: 0, '&:last-child': { border: 'none' } }}>
    <Typography component="span" sx={{ minWidth: { xs: 0, sm: 160 }, color: 'text.secondary', fontSize: '0.8rem', fontWeight: 600, flexShrink: 0 }}>
      {label}
    </Typography>
    <Box sx={{ fontSize: '0.875rem', fontWeight: 600, minWidth: 0, overflowWrap: 'anywhere' }}>
      {value ?? <Typography component="span" sx={{ color: '#9ca3af', fontSize: '0.875rem' }}>—</Typography>}
    </Box>
  </Box>
);

const Section: React.FC<{ title: string; icon: React.ReactNode; children: React.ReactNode }> = ({ title, icon, children }) => (
  <Box>
    <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.secondary', textTransform: 'uppercase', letterSpacing: '0.08em', display: 'block', mb: 1.5 }}>
      {title}
    </Typography>
    <Paper sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden' }}>
      <Box sx={{ px: 2, py: 1.5, display: 'flex', alignItems: 'center', gap: 1, bgcolor: 'action.hover', borderBottom: '1px solid', borderColor: 'divider' }}>
        <Box sx={{ color: 'primary.main', display: 'flex' }}>{icon}</Box>
        <Typography sx={{ fontWeight: 700, fontSize: '0.8rem' }}>{title}</Typography>
      </Box>
      <Box sx={{ px: 2.5, py: 1.5 }}>{children}</Box>
    </Paper>
  </Box>
);

const CustomerDetailPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { hasPermission } = useAuth();

  const customerQuery = useQuery({
    queryKey: ['customer-detail', Number(id)],
    queryFn: () => customerService.getById(Number(id)),
    enabled: !!id,
  });
  const customer = customerQuery.data;
  const customerId = Number(id);
  const canViewQuotes = hasPermission('Quotations');
  const canViewCommercialContext = canViewQuotes && hasPermission('Orders') && hasPermission('RFQ Management');
  const canViewHealth = canViewCommercialContext && hasPermission('Customers');
  const healthPeriod = React.useMemo(healthWindow, []);
  const contacts = useQuery({
    queryKey: ['customer-contacts', customerId],
    queryFn: () => contactService.getByCustomer(customerId),
    enabled: Number.isFinite(customerId),
  });
  const context = useQuery({
    queryKey: ['customer-intelligence-context', customerId],
    queryFn: () => intelligenceService.getCustomerContext(customerId),
    enabled: Number.isFinite(customerId) && canViewCommercialContext,
  });
  const memory = useQuery({
    queryKey: ['customer-commercial-memory', customerId],
    queryFn: () => commercialLearningService.getCustomer(customerId),
    enabled: Number.isFinite(customerId) && hasPermission('Quotations'),
  });
  const ownership = useQuery({
    queryKey: ['customer-account-ownership', customerId],
    queryFn: async () => {
      const rows = await commercialIntelligenceService.getAccountOwnership({ search: customer?.name });
      return rows.find(row => row.customerId === customerId) ?? null;
    },
    enabled: Number.isFinite(customerId) && !!customer?.name,
  });
  const health = useQuery({
    queryKey: ['customer-health', customerId, healthPeriod.from, healthPeriod.to],
    queryFn: () => commercialIntelligenceService.getCustomerHealth(customerId, healthPeriod.from, healthPeriod.to),
    enabled: Number.isFinite(customerId) && canViewHealth,
  });

  if (customerQuery.isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  if (customerQuery.isError) return <Box sx={{ p: 4 }}><Alert severity="error" action={<Button color="inherit" onClick={() => void customerQuery.refetch()}>Retry</Button>}>Customer details could not be loaded.</Alert></Box>;
  if (!customer) return <Box sx={{ p: 4 }}><Typography>Customer not found.</Typography></Box>;

  return (
    <Box sx={{ p: { xs: 2, sm: 3 }, width: '100%', minWidth: 0 }}>
      {/* Top Bar */}
      <Box sx={{ display: 'flex', flexDirection: { xs: 'column', sm: 'row' }, alignItems: { xs: 'stretch', sm: 'center' }, justifyContent: 'space-between', gap: 1.5, mb: 3 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, minWidth: 0 }}>
          <Button startIcon={<BackIcon />} onClick={() => navigate('/customers')} size="small" variant="outlined" sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5 }}>
            Back
          </Button>
          <Divider orientation="vertical" flexItem />
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            <CustomerIcon sx={{ fontSize: 20, color: 'text.secondary' }} />
            <Typography sx={{ fontWeight: 700, fontSize: '0.9rem', color: 'text.secondary' }}>Customers /</Typography>
            <Typography sx={{ fontWeight: 800, fontSize: '0.9rem' }}>{customer.name}</Typography>
          </Box>
        </Box>
        <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', flexWrap: 'wrap' }}>
          <Chip label={customer.isActive ? 'Active' : 'Inactive'} color={customer.isActive ? 'success' : 'default'} size="small" variant="outlined" />
          {hasPermission('Customers', 'edit') && <Button variant="contained" startIcon={<EditIcon />} onClick={() => navigate('/customers', { state: { editId: customer.id } })} disableElevation sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5 }}>
            Edit Customer
          </Button>}
        </Box>
      </Box>

      {/* Header Card */}
      <Paper sx={{ p: 3, mb: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <Box sx={{ display: 'flex', flexDirection: { xs: 'column', sm: 'row' }, alignItems: { xs: 'flex-start', sm: 'center' }, gap: 2.5 }}>
          <Avatar
            src={customer.imageUrl}
            sx={{ width: 64, height: 64, bgcolor: 'primary.main', fontSize: '1.5rem', fontWeight: 900 }}
          >
            {customer.name?.[0]?.toUpperCase()}
          </Avatar>
          <Box sx={{ flex: 1 }}>
            <Typography variant="h5" sx={{ fontWeight: 900, mb: 0.3 }}>{customer.name}</Typography>
            <Box sx={{ display: 'flex', gap: 1.5, flexWrap: 'wrap', alignItems: 'center' }}>
              {customer.docId && (
                <Typography sx={{ fontFamily: 'monospace', fontSize: '0.8rem', bgcolor: 'action.hover', px: 1, py: 0.2, borderRadius: 1, fontWeight: 700 }}>
                  {customer.docId}
                </Typography>
              )}
              {customer.contactEmail && (
                <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
                  <EmailIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
                  <Typography sx={{ fontSize: '0.85rem', color: 'text.secondary' }}>
                    {customer.contactEmail}
                  </Typography>
                </Stack>
              )}
            </Box>
          </Box>
        </Box>
      </Paper>

      {/* Body */}
      <Grid container spacing={3}>
        <Grid size={{ xs: 12 }}>
          <Section title="Account ownership and active work" icon={<OwnershipIcon sx={{ fontSize: 16 }} />}>
            {ownership.isLoading ? <CircularProgress size={20} /> : ownership.isError ? <Alert severity="error" action={<Button color="inherit" onClick={() => void ownership.refetch()}>Retry</Button>}>Account ownership could not be loaded.</Alert> : ownership.data ? (
              <Grid container spacing={2} sx={{ alignItems: 'center' }}>
                <Grid size={{ xs: 12, sm: 4 }}><InfoRow label="Account owner" value={ownership.data.ownerName ?? 'Unassigned'} /></Grid>
                <Grid size={{ xs: 6, sm: 2 }}><InfoRow label="Open inquiries" value={ownership.data.openLeads} /></Grid>
                <Grid size={{ xs: 6, sm: 2 }}><InfoRow label="Open quotes" value={ownership.data.openQuotes} /></Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <Button variant="outlined" onClick={() => navigate(`/commercial-cases?search=${encodeURIComponent(customer.name)}`)}>
                    Open active commercial work
                  </Button>
                </Grid>
              </Grid>
            ) : <Typography color="text.secondary" variant="body2">No account ownership record is available.</Typography>}
          </Section>
        </Grid>

        <Grid size={{ xs: 12 }}>
          <Section title="Commercial performance" icon={<InsightsIcon sx={{ fontSize: 16 }} />}>
            {!canViewQuotes ? <Alert severity="info">Quotation view permission is required to see commercial evidence.</Alert> : !canViewCommercialContext ? <Alert severity="info">RFQ and order view permissions are required to see combined commercial evidence.</Alert> : context.isError || memory.isError ? <Alert severity="error" action={<Button color="inherit" onClick={() => { void context.refetch(); void memory.refetch(); }}>Retry</Button>}>Commercial evidence could not be loaded.</Alert> : (
              <Grid container spacing={2}>
                {[
                  ['Inquiries', memory.data?.inquiryCount],
                  ['Quotes', context.data?.totalQuotes],
                  ['Won', memory.data?.wonCount],
                  ['Lost', memory.data?.lostCount],
                  ['Win rate', memory.data?.conversionRatePercent == null ? null : `${memory.data.conversionRatePercent}%`],
                  ['Orders, 24 months', context.data?.ordersLast24Months],
                ].map(([label, value]) => <Grid key={String(label)} size={{ xs: 6, sm: 4, lg: 2 }}>
                  <Typography variant="caption" color="text.secondary">{label}</Typography>
                  <Typography sx={{ fontSize: '1.2rem', fontWeight: 800 }}>{value ?? 'Not available'}</Typography>
                </Grid>)}
              </Grid>
            )}
          </Section>
        </Grid>

        <Grid size={{ xs: 12, md: 6 }}>
          <Section title="Follow-up and next action" icon={<HistoryIcon sx={{ fontSize: 16 }} />}>
            {!canViewHealth ? <Alert severity="info">Customer, RFQ, quotation, and order view permissions are required.</Alert> : health.isLoading ? <CircularProgress size={20} /> : health.isError ? <Alert severity="error" action={<Button color="inherit" onClick={() => void health.refetch()}>Retry</Button>}>Server-calculated customer health could not be loaded.</Alert> : health.data ? <>
              {health.data.dataCompleteness.status === 'partial' && <Alert severity="warning" sx={{ mb: 1 }}>This health view is partial because the bounded source limit was reached for: {health.data.dataCompleteness.incompleteSources.join(', ')}.</Alert>}
              <InfoRow label="Account health" value={<Chip size="small" label={health.data.healthBand || 'Insufficient evidence'} color={health.data.healthBand.toLowerCase().includes('risk') ? 'error' : health.data.healthBand.toLowerCase().includes('healthy') ? 'success' : 'default'} />} />
              <InfoRow label="Next action" value={health.data.nextBestAction?.title ?? 'Insufficient evidence for a next action'} />
              <InfoRow label="Action evidence" value={health.data.nextBestAction?.explanation ?? (health.data.healthReasons.join('; ') || 'The server returned no qualifying evidence.')} />
              <InfoRow label="RFQ trend" value={`${health.data.rfqTrend.currentCount} current vs ${health.data.rfqTrend.previousCount} previous; ${metricValue(health.data.rfqTrend.changePercent, '%')} (${health.data.rfqTrend.status})`} />
              <InfoRow label="Quote coverage" value={`${health.data.quoteCoverage.quotedRfqCount} of ${health.data.quoteCoverage.rfqCount}; ${metricValue(health.data.quoteCoverage.coveragePercent, '%')} (${health.data.quoteCoverage.status})`} />
              <InfoRow label="Quote conversion" value={`${metricValue(health.data.conversion.ratePercent, '%')} (${health.data.conversion.status})`} />
              <InfoRow label="Margin status" value={`${health.data.margin.status}${health.data.margin.grossMarginPercent == null ? health.data.margin.reason ? `: ${health.data.margin.reason}` : '' : `: ${health.data.margin.grossMarginPercent.toLocaleString()}%`}`} />
              <InfoRow label="Follow-up" value={`${health.data.followUp.openCount} open; ${health.data.followUp.overdueCount} overdue; ${metricValue(health.data.followUp.effectivenessPercent, '%')} effectiveness`} />
              <InfoRow label="Revision burden" value={`${health.data.revisionBurden.revisionCount} revisions; fields ${health.data.revisionBurden.changedFieldCount}/${health.data.revisionBurden.comparedFieldCount} changed (${metricValue(health.data.revisionBurden.fieldChangePercent, '%')}); lines ${health.data.revisionBurden.changedLineCount}/${health.data.revisionBurden.comparedLineCount} changed (${metricValue(health.data.revisionBurden.lineChangePercent, '%')})`} />
              <InfoRow label="Last commercial activity" value={health.data.lastCommercialActivity ? `${health.data.lastCommercialActivity.reference}${health.data.lastCommercialActivity.occurredOn ? ` on ${new Date(health.data.lastCommercialActivity.occurredOn).toLocaleDateString()}` : ''}` : 'Insufficient evidence'} />
              <InfoRow label="Evidence period" value={`${new Date(health.data.period.from).toLocaleDateString()} to ${new Date(health.data.period.to).toLocaleDateString()}`} />
              {health.data.healthReasons.length > 0 && <Stack component="ul" spacing={0.5} sx={{ pl: 2.5 }}>
                {health.data.healthReasons.map(reason => <Typography component="li" variant="body2" key={reason}>{reason}</Typography>)}
              </Stack>}
              {health.data.opportunities.length > 0 && <Box sx={{ mt: 1 }}>
                <Typography variant="caption" color="text.secondary">Evidence-backed opportunities</Typography>
                {health.data.opportunities.map((item, index) => <Box key={`${item.code}-${item.actionRoute}-${index}`} sx={{ py: 1, borderBottom: '1px solid', borderColor: 'divider', '&:last-child': { borderBottom: 0 } }}>
                  <Typography variant="body2" sx={{ fontWeight: 800 }}>{item.title}</Typography>
                  <Typography variant="caption" color="text.secondary">{item.explanation}</Typography>
                </Box>)}
              </Box>}
              {health.data.nextBestAction?.actionRoute?.startsWith('/') && <Button variant="outlined" endIcon={<OpenIcon />} onClick={() => navigate(health.data!.nextBestAction!.actionRoute)}>Open next action</Button>}
            </> : <Typography color="text.secondary" variant="body2">Insufficient customer-health evidence for this period.</Typography>}
          </Section>
        </Grid>

        <Grid size={{ xs: 12, md: 6 }}>
          <Section title="Loss and no-quote reasons" icon={<InsightsIcon sx={{ fontSize: 16 }} />}>
            {!canViewQuotes ? <Alert severity="info">Quotation view permission is required.</Alert> : memory.isLoading ? <CircularProgress size={20} /> : memory.isError ? <Alert severity="error" action={<Button color="inherit" onClick={() => void memory.refetch()}>Retry</Button>}>Outcome reasons could not be loaded.</Alert> : memory.data?.lossReasons.length ? memory.data.lossReasons.map(reason => (
              <InfoRow key={reason.code} label={reason.label || reason.code} value={`${reason.count} recorded outcome(s)`} />
            )) : <Typography color="text.secondary" variant="body2">No loss or no-quote reason is recorded.</Typography>}
          </Section>
        </Grid>

        <Grid size={{ xs: 12, md: 6 }}>
          <Section title="Contacts" icon={<ContactsIcon sx={{ fontSize: 16 }} />}>
            {contacts.isLoading ? <CircularProgress size={20} /> : contacts.isError ? <Alert severity="error" action={<Button color="inherit" onClick={() => void contacts.refetch()}>Retry</Button>}>Contacts could not be loaded.</Alert> : contacts.data?.length ? contacts.data.map(contact => (
              <InfoRow key={contact.id} label={[contact.firstName, contact.lastName].filter(Boolean).join(' ')} value={contact.email || contact.phoneNo || contact.position} />
            )) : <Typography color="text.secondary" variant="body2">No contacts recorded.</Typography>}
          </Section>
        </Grid>

        <Grid size={{ xs: 12, md: 6 }}>
          <Section title="Accepted price evidence" icon={<HistoryIcon sx={{ fontSize: 16 }} />}>
            {!canViewHealth ? <Alert severity="info">Customer, RFQ, quotation, and order view permissions are required.</Alert> : health.isLoading ? <CircularProgress size={20} /> : health.isError ? <Alert severity="error" action={<Button color="inherit" onClick={() => void health.refetch()}>Retry</Button>}>Accepted price evidence could not be loaded.</Alert> : health.data?.acceptedPrices.length ? <TableContainer sx={{ maxWidth: '100%' }}><Table size="small" aria-label="Accepted customer prices"><TableHead><TableRow><TableCell>Part</TableCell><TableCell>Quantity</TableCell><TableCell>Accepted unit price</TableCell><TableCell>Evidence</TableCell></TableRow></TableHead><TableBody>
              {health.data.acceptedPrices.map((item, index) => <TableRow hover key={`${item.evidence.recordType}-${item.evidence.recordId}-${index}`}><TableCell>{item.partNumber || item.description || 'Part not resolved'}</TableCell><TableCell>{item.quantity.toLocaleString()}</TableCell><TableCell>{item.currencyCode} {item.unitPrice.toLocaleString(undefined, { maximumFractionDigits: 4 })}</TableCell><TableCell>{item.evidence.reference}</TableCell></TableRow>)}
            </TableBody></Table></TableContainer> : <Typography color="text.secondary" variant="body2">Insufficient accepted-price evidence for this period.</Typography>}
          </Section>
        </Grid>

        <Grid size={{ xs: 12 }}>
          <Section title="Recent Customer RFQs" icon={<HistoryIcon sx={{ fontSize: 16 }} />}>
            {!canViewCommercialContext ? <Alert severity="info">Quotation and order view permissions are required.</Alert> : context.isLoading ? <CircularProgress size={20} /> : context.isError ? <Alert severity="error" action={<Button color="inherit" onClick={() => void context.refetch()}>Retry</Button>}>RFQ history could not be loaded.</Alert> : context.data?.recentRfqs.length ? <TableContainer sx={{ maxWidth: '100%' }}><Table size="small"><TableHead><TableRow><TableCell>RFQ</TableCell><TableCell>Received</TableCell><TableCell>Deadline</TableCell><TableCell>Status</TableCell><TableCell>Lines</TableCell><TableCell>Action</TableCell></TableRow></TableHead><TableBody>
              {context.data.recentRfqs.map(rfq => <TableRow hover key={rfq.rfqId}><TableCell>{rfq.rfqNo}</TableCell><TableCell>{new Date(rfq.receivedOn).toLocaleDateString()}</TableCell><TableCell>{rfq.bidClosingOn ? new Date(rfq.bidClosingOn).toLocaleDateString() : 'Not recorded'}</TableCell><TableCell>{rfq.status ?? 'Not recorded'}</TableCell><TableCell>{rfq.lineCount}</TableCell><TableCell><Button size="small" endIcon={<OpenIcon />} onClick={() => navigate(`/procurement/rfqs/view/${rfq.rfqId}`)}>Open</Button></TableCell></TableRow>)}
            </TableBody></Table></TableContainer> : <Typography color="text.secondary" variant="body2">No Customer RFQs recorded.</Typography>}
          </Section>
        </Grid>

        <Grid size={{ xs: 12 }}>
          <Section title="Recent quote outcomes" icon={<HistoryIcon sx={{ fontSize: 16 }} />}>
            {!canViewCommercialContext ? <Alert severity="info">Quotation and order view permissions are required.</Alert> : context.isError ? <Alert severity="error" action={<Button color="inherit" onClick={() => void context.refetch()}>Retry</Button>}>Quote history could not be loaded.</Alert> : context.data?.recentQuotes.length ? <TableContainer sx={{ maxWidth: '100%' }}><Table size="small"><TableHead><TableRow><TableCell>Quote</TableCell><TableCell>Date</TableCell><TableCell>Outcome</TableCell><TableCell>Outcome evidence</TableCell><TableCell>Action</TableCell></TableRow></TableHead><TableBody>
              {context.data.recentQuotes.map(quote => <TableRow hover key={quote.quoteId}><TableCell>{quote.quoteNo}</TableCell><TableCell>{quote.quoteDate ? new Date(quote.quoteDate).toLocaleDateString() : 'Not recorded'}</TableCell><TableCell><Chip size="small" label={quote.outcome} color={quote.outcome === 'won' ? 'success' : quote.outcome === 'lost' ? 'error' : 'default'} /></TableCell><TableCell>{quote.outcomeReasonName || quote.statusValue || 'No decision recorded'}</TableCell><TableCell><Button size="small" endIcon={<OpenIcon />} onClick={() => navigate(`/sales/quotes/view/${quote.quoteId}`)}>Open</Button></TableCell></TableRow>)}
            </TableBody></Table></TableContainer> : <Typography color="text.secondary" variant="body2">No quote history recorded.</Typography>}
          </Section>
        </Grid>

        <Grid size={{ xs: 12 }}>
          <Section title="Recent Customer Orders" icon={<ShippingIcon sx={{ fontSize: 16 }} />}>
            {!canViewCommercialContext ? <Alert severity="info">Quotation and order view permissions are required.</Alert> : context.isLoading ? <CircularProgress size={20} /> : context.isError ? <Alert severity="error" action={<Button color="inherit" onClick={() => void context.refetch()}>Retry</Button>}>Order history could not be loaded.</Alert> : context.data?.recentOrders.length ? <TableContainer sx={{ maxWidth: '100%' }}><Table size="small"><TableHead><TableRow><TableCell>Order</TableCell><TableCell>Date</TableCell><TableCell>Status</TableCell><TableCell>Total</TableCell><TableCell>Action</TableCell></TableRow></TableHead><TableBody>
              {context.data.recentOrders.map(order => <TableRow hover key={order.orderId}><TableCell>{order.orderNo}</TableCell><TableCell>{new Date(order.orderDate).toLocaleDateString()}</TableCell><TableCell>{order.status ?? 'Not recorded'}</TableCell><TableCell>{order.totalAmount.toLocaleString()}</TableCell><TableCell><Button size="small" endIcon={<OpenIcon />} onClick={() => navigate(`/sales/orders/${order.orderId}`)}>Open</Button></TableCell></TableRow>)}
            </TableBody></Table></TableContainer> : <Typography color="text.secondary" variant="body2">No Customer Orders recorded.</Typography>}
          </Section>
        </Grid>

        <Grid size={{ xs: 12 }}>
          <Section title="Demand profile" icon={<InsightsIcon sx={{ fontSize: 16 }} />}>
            {!canViewCommercialContext ? <Alert severity="info">Quotation and order view permissions are required.</Alert> : context.isLoading ? <CircularProgress size={20} /> : context.isError ? <Alert severity="error" action={<Button color="inherit" onClick={() => void context.refetch()}>Retry</Button>}>Demand evidence could not be loaded.</Alert> : context.data?.demandProfile.length ? <TableContainer sx={{ maxWidth: '100%' }}><Table size="small"><TableHead><TableRow><TableCell>Part</TableCell><TableCell>Description</TableCell><TableCell>RFQs</TableCell><TableCell>Requested quantity</TableCell></TableRow></TableHead><TableBody>
              {context.data.demandProfile.map((line, index) => <TableRow hover key={`${line.productId ?? 'unknown'}-${line.partNumber ?? 'unresolved'}-${index}`}><TableCell>{line.partNumber ?? (line.productId ? `Product ${line.productId}` : 'Part not resolved')}</TableCell><TableCell>{line.description ?? 'Description not recorded'}</TableCell><TableCell>{line.inquiryCount}</TableCell><TableCell>{line.requestedQuantity.toLocaleString()}</TableCell></TableRow>)}
            </TableBody></Table></TableContainer> : <Typography color="text.secondary" variant="body2">No RFQ demand history recorded.</Typography>}
          </Section>
        </Grid>

        <Grid size={{ xs: 12, md: 6 }}>
          <Section title="Billing Address" icon={<BillingIcon sx={{ fontSize: 16 }} />}>
            <InfoRow label="Address Line 1" value={customer.billingAddressLine1} />
            <InfoRow label="Address Line 2" value={customer.billingAddressLine2} />
            <InfoRow label="City" value={customer.billingCity} />
            <InfoRow label="State" value={customer.billingState} />
            <InfoRow label="Country" value={customer.billingCountry} />
            <InfoRow label="Postal Code" value={customer.billingPostalCode} />
          </Section>
        </Grid>

        <Grid size={{ xs: 12, md: 6 }}>
          <Section title="Shipping Address" icon={<ShippingIcon sx={{ fontSize: 16 }} />}>
            <InfoRow label="Address Line 1" value={customer.shippingAddressLine1} />
            <InfoRow label="Address Line 2" value={customer.shippingAddressLine2} />
            <InfoRow label="City" value={customer.shippingCity} />
            <InfoRow label="State" value={customer.shippingState} />
            <InfoRow label="Country" value={customer.shippingCountry} />
            <InfoRow label="Postal Code" value={customer.shippingPostalCode} />
          </Section>
        </Grid>
      </Grid>
    </Box>
  );
};

export default CustomerDetailPage;
