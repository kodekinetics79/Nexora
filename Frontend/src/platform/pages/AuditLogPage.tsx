import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { Box } from '@mui/material';
import { platformApi } from '../api/client';
import { platformKeys } from '../api/queryKeys';
import PageHeader from '../components/PageHeader';
import AuditExplorer from '../components/AuditExplorer';

export default function AuditLogPage() {
  const [searchParams] = useSearchParams();
  const tenantFromUrl = searchParams.get('tenant') ?? '';

  const { data: tenants } = useQuery({
    queryKey: platformKeys.tenants(),
    queryFn: () => platformApi.listTenants(),
    enabled: tenantFromUrl !== '',
  });

  const tenantName = tenants?.find((tenant) => tenant.id === tenantFromUrl)?.name;

  return (
    <Box>
      <PageHeader
        title="Audit Log"
        subtitle={
          tenantFromUrl
            ? `Privileged activity recorded against ${tenantName ?? `tenant ${tenantFromUrl}`}.`
            : 'Every privileged action taken across the platform, with what it changed.'
        }
      />
      <AuditExplorer tenantId={tenantFromUrl || undefined} />
    </Box>
  );
}
