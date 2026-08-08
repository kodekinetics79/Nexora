import { useMemo } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Box, Button, Tab, Tabs } from '@mui/material';
import { ArrowBack as BackIcon } from '@mui/icons-material';
import Stack from '../components/Flex';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import PageHeader from '../components/PageHeader';
import { BillingModeChip, PlanChip, TenantStatusChip } from '../components/StatusChip';
import { ErrorState, LoadingState } from '../components/States';
import OverviewTab from './tenant/OverviewTab';
import CommercialTab from './tenant/CommercialTab';
import SupportTab from './tenant/SupportTab';
import AuditTab from './tenant/AuditTab';
import LifecycleTab from './tenant/LifecycleTab';

const TABS = [
  { key: 'overview', label: 'Overview' },
  { key: 'commercial', label: 'Commercial' },
  { key: 'support', label: 'Support' },
  { key: 'audit', label: 'Audit' },
  { key: 'lifecycle', label: 'Lifecycle' },
] as const;

type TabKey = (typeof TABS)[number]['key'];

export default function TenantDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const permissions = usePlatformPermissions();
  const availableTabs = useMemo(
    () => TABS.filter((entry) => entry.key !== 'support' || permissions.canAdministerTenants),
    [permissions.canAdministerTenants],
  );
  // The tab lives in the URL so a link to a customer's lifecycle screen is a link that
  // opens on it — which is what an operator pastes into a ticket.
  const [searchParams, setSearchParams] = useSearchParams();

  const tab = useMemo<TabKey>(() => {
    const requested = searchParams.get('tab');
    return (availableTabs.find((entry) => entry.key === requested)?.key ?? 'overview') as TabKey;
  }, [availableTabs, searchParams]);

  const openTab = (next: string) => {
    const params = new URLSearchParams(searchParams);
    params.set('tab', next);
    setSearchParams(params, { replace: true });
  };

  const tenantQuery = useQuery({
    queryKey: platformKeys.tenant(id),
    queryFn: () => platformApi.getTenant(id),
    enabled: id !== '',
  });

  // Read here rather than inside the Lifecycle tab so the Overview banner and the
  // Lifecycle screen are looking at the same retention clock, not two fetches of it.
  const offboardingQuery = useQuery({
    queryKey: platformKeys.offboarding(id),
    queryFn: () => platformApi.getOffboarding(id),
    enabled: id !== '',
  });

  const tenant = tenantQuery.data;

  if (tenantQuery.isLoading) return <LoadingState label="Loading tenant…" minHeight="60vh" />;
  if (tenantQuery.isError || !tenant) {
    return (
      <Box>
        <Button startIcon={<BackIcon />} onClick={() => navigate('/platform/tenants')} sx={{ mb: 2 }}>
          Back to tenants
        </Button>
        <ErrorState
          message={platformErrorMessage(tenantQuery.error, 'This tenant could not be loaded.')}
          onRetry={() => tenantQuery.refetch()}
        />
      </Box>
    );
  }

  return (
    <Box>
      <Button startIcon={<BackIcon />} onClick={() => navigate('/platform/tenants')} sx={{ mb: 1.5 }} color="inherit">
        Tenants
      </Button>
      <PageHeader
        title={tenant.name}
        subtitle={tenant.legalName ?? tenant.slug}
        actions={
          <Stack direction="row" spacing={1}>
            <BillingModeChip mode={tenant.billingMode} />
            <PlanChip tier={tenant.planCode ?? 'none'} />
            <TenantStatusChip status={tenant.status} />
          </Stack>
        }
      />

      <Tabs
        value={tab}
        onChange={(_event, next: TabKey) => openTab(next)}
        variant="scrollable"
        scrollButtons="auto"
        aria-label="Tenant operations"
        sx={{ mb: 2.5, borderBottom: '1px solid', borderColor: 'divider' }}
      >
        {availableTabs.map((entry) => (
          <Tab
            key={entry.key}
            value={entry.key}
            label={entry.label}
            // The Commercial tab is Owner|BillingAdmin end to end. It stays visible so the
            // separation of duties is legible, and explains itself when opened.
            sx={{ fontWeight: 700, opacity: entry.key === 'commercial' && !permissions.canAdministerBilling ? 0.6 : 1 }}
          />
        ))}
      </Tabs>

      {tab === 'overview' && (
        <OverviewTab
          tenant={tenant}
          offboarding={offboardingQuery.data}
          onOpenTab={openTab}
          canViewSupport={permissions.canAdministerTenants}
        />
      )}
      {tab === 'commercial' && <CommercialTab tenant={tenant} />}
      {tab === 'support' && permissions.canAdministerTenants && <SupportTab tenant={tenant} />}
      {tab === 'audit' && <AuditTab tenant={tenant} />}
      {tab === 'lifecycle' && <LifecycleTab tenant={tenant} />}
    </Box>
  );
}
