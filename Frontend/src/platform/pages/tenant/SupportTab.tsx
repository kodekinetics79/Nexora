import SupportTicketQueue from '../../components/SupportTicketQueue';
import type { Tenant } from '../../types';

/**
 * The same desk as `/platform/support`, locked to one customer. Sharing the component
 * rather than building a second, smaller list is what stops the tenant view from quietly
 * losing a filter — or a transition — the fleet-wide queue has.
 */
export default function SupportTab({ tenant }: { tenant: Tenant }) {
  return <SupportTicketQueue tenantId={tenant.id} tenantName={tenant.name} height={520} />;
}
