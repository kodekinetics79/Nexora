import { useQuery } from '@tanstack/react-query';
import { Alert, Box, Chip, CircularProgress, Paper, Stack, Typography } from '@mui/material';
import { platformGovernanceService } from '../../api/services/platformGovernanceService';
import ArtifactStudioPage from './ArtifactStudioPage';

export default function IntegrationHubPage() {
  const sdk = useQuery({ queryKey: ['connector-sdk'], queryFn: platformGovernanceService.getConnectorSdk });
  const contract = sdk.isLoading ? <Box sx={{ py: 3, textAlign: 'center' }}><CircularProgress size={24} /></Box>
    : sdk.isError || !sdk.data ? <Alert severity="error" sx={{ mb: 2 }}>The connector SDK contract is unavailable.</Alert>
    : <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
      <Stack sx={{ gap: 1.5 }}>
        <Box><Typography variant="subtitle1" sx={{ fontWeight: 750 }}>Connector SDK v{sdk.data.contractVersion}</Typography><Typography variant="body2" color="text.secondary">{sdk.data.secretRule}</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">Certified connector families</Typography><Stack direction="row" sx={{ gap: .75, flexWrap: 'wrap', mt: .5 }}>{sdk.data.connectorTypes.map((type) => <Chip key={type} size="small" label={type} />)}</Stack></Box>
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, gap: 2 }}><Box><Typography variant="caption" color="text.secondary">Commercial operations</Typography><Typography variant="body2">{sdk.data.commercialOperations.join(' · ')}</Typography></Box><Box><Typography variant="caption" color="text.secondary">Delivery guarantees</Typography><Typography variant="body2">{sdk.data.deliveryGuarantees.join(' · ')}</Typography></Box></Box>
        <Box><Typography variant="caption" color="text.secondary">Webhook envelope</Typography><Typography component="code" variant="body2" sx={{ display: 'block', overflowWrap: 'anywhere' }}>{sdk.data.webhookEnvelope}</Typography></Box>
      </Stack>
    </Paper>;
  return <ArtifactStudioPage title="Integration Hub & Connector SDK"
    subtitle="Versioned tenant connections, mappings, delivery controls and certified contracts"
    types={['Connector']} beforeContent={contract} />;
}
