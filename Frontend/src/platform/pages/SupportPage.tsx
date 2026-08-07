import { Box } from '@mui/material';
import PageHeader from '../components/PageHeader';
import SupportTicketQueue from '../components/SupportTicketQueue';

/**
 * The fleet-wide support desk. Tenant-scoped work happens on the tenant's own Support tab;
 * this is the queue somebody watches, which is why it opens on live tickets across every
 * customer rather than asking which one to look at first.
 */
export default function SupportPage() {
  return (
    <Box>
      <PageHeader
        title="Support"
        subtitle="Every open ticket across the fleet, with the customer's lifecycle state on each row."
      />
      <SupportTicketQueue />
    </Box>
  );
}
