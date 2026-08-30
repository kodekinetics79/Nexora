import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import InboundLeadTimePolicyDialog from './InboundLeadTimePolicyDialog';

const getPolicy = vi.fn();
const updatePolicy = vi.fn();

vi.mock('../../../api/services/inboundShipmentService', () => ({
  default: {
    getPolicy: () => getPolicy(),
    updatePolicy: (command: unknown) => updatePolicy(command),
  },
}));

const POLICY = {
  businessUnitId: 1,
  customsClearanceLeadDays: 3,
  putawayLeadDays: 2,
  isConfigured: true,
  version: 4,
  modifiedOn: '2026-08-29T10:00:00Z',
  modifiedBy: 'manager@acceptance.local',
  isDefault: false,
};

function renderDialog(canManagePolicy: boolean) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <InboundLeadTimePolicyDialog canEdit={canManagePolicy} onClose={vi.fn()} onSaved={vi.fn()} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getPolicy.mockResolvedValue(POLICY);
  updatePolicy.mockResolvedValue({ ...POLICY, customsClearanceLeadDays: 4 });
});

describe('tenant-wide inbound lead-time authority', () => {
  it('shows current values read-only and exposes no save action to a non-manager', async () => {
    renderDialog(false);

    expect(await screen.findByText(/read-only for your role/i)).toBeInTheDocument();
    expect(screen.getByLabelText('Customs clearance (working days)')).toBeDisabled();
    expect(screen.getByLabelText('Putaway (working days)')).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save lead times' })).not.toBeInTheDocument();
    expect(updatePolicy).not.toHaveBeenCalled();
  });

  it('allows a server-recognized manager to edit and save the policy', async () => {
    renderDialog(true);

    const customs = await screen.findByLabelText('Customs clearance (working days)');
    expect(customs).toBeEnabled();
    fireEvent.change(customs, { target: { value: '4' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save lead times' }));

    await waitFor(() => expect(updatePolicy).toHaveBeenCalledTimes(1));
    expect(updatePolicy.mock.calls[0][0]).toMatchObject({
      customsClearanceLeadDays: 4,
      putawayLeadDays: 2,
    });
  });
});
