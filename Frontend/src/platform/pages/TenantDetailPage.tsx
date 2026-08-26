import { useMemo } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Box, Button, Tab, Tabs, Tooltip } from '@mui/material';
import { ArrowBack as BackIcon, DeleteOutlined as DeleteIcon, EditOutlined as EditIcon } from '@mui/icons-material';
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
import ActivationPolicyPanel from './tenant/ActivationPolicyPanel';
import AiGovernanceTab from './tenant/AiGovernanceTab';
import ModulesTab from './tenant/ModulesTab';
import DataStorageTab from './tenant/DataStorageTab';
import ProfileAccessTab from './tenant/ProfileAccessTab';
import ProvisioningDiagnosticsTab from './tenant/ProvisioningDiagnosticsTab';
import UsersTab from './tenant/UsersTab';
import { TENANT_DETAIL_TABS, type TenantDetailTabKey } from './tenantNavigation';

export default function TenantDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const permissions = usePlatformPermissions();
  const availableTabs = useMemo(
    () => TENANT_DETAIL_TABS.filter((entry) =>
      (entry.key !== 'support' || permissions.canAdministerTenants)
      && (entry.key !== 'profile-access' || permissions.canAdministerTenants)
      && (entry.key !== 'users' || permissions.canAdministerTenants)
      && (entry.key !== 'ai-governance' || permissions.isOwner)
      && (entry.key !== 'data-storage' || permissions.isOwner)),
    [permissions.canAdministerTenants, permissions.isOwner],
  );
  // The tab lives in the URL so a link to a customer's offboarding screen opens on it — which is
  // what an operator pastes into a ticket. The legacy key remains stable for existing links.
  const [searchParams, setSearchParams] = useSearchParams();

  const tab = useMemo<TenantDetailTabKey>(() => {
    const requested = searchParams.get('tab');
    return (availableTabs.find((entry) => entry.key === requested)?.key ?? 'overview') as TenantDetailTabKey;
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

  // Read here rather than inside the offboarding tab so the Overview banner and the deletion
  // screen are looking at the same retention clock, not two fetches of it.
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
          <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }}>
            {permissions.canAdministerTenants && (
              <Button size="small" variant="outlined" startIcon={<EditIcon />} onClick={() => openTab('profile-access')}>
                Edit tenant
              </Button>
            )}
            {permissions.isOwner && (
              <Tooltip describeChild title="Open governed tenant offboarding, retention, and deletion controls">
                <Button size="small" variant="outlined" color="error" startIcon={<DeleteIcon />} onClick={() => openTab('lifecycle')}>
                  Offboard / delete tenant
                </Button>
              </Tooltip>
            )}
            <BillingModeChip mode={tenant.billingMode} />
            <PlanChip tier={tenant.planCode ?? 'none'} />
            <TenantStatusChip status={tenant.status} />
          </Stack>
        }
      />

      <Tabs
        value={tab}
        onChange={(_event, next: TenantDetailTabKey) => openTab(next)}
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
          offboardingUnavailable={offboardingQuery.isError}
          onOpenTab={openTab}
          canViewSupport={permissions.canAdministerTenants}
        />
      )}
      {tab === 'activation' && <ActivationPolicyPanel tenant={tenant} />}
      {tab === 'profile-access' && permissions.canAdministerTenants && <ProfileAccessTab tenant={tenant} />}
      {tab === 'users' && permissions.canAdministerTenants && <UsersTab tenant={tenant} />}
      {tab === 'provisioning' && <ProvisioningDiagnosticsTab tenant={tenant} />}
      {tab === 'commercial' && <CommercialTab tenant={tenant} />}
      {tab === 'entitlements' && <ModulesTab tenant={tenant} />}
      {tab === 'support' && permissions.canAdministerTenants && <SupportTab tenant={tenant} />}
      {tab === 'audit' && <AuditTab tenant={tenant} />}
      {tab === 'lifecycle' && <LifecycleTab tenant={tenant} />}
      {tab === 'ai-governance' && permissions.isOwner && <AiGovernanceTab tenant={tenant} />}
      {tab === 'data-storage' && permissions.isOwner && <DataStorageTab tenant={tenant} />}
    </Box>
  );
}
