import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import RepDirectoryPage from './RepDirectoryPage';

const mocks = vi.hoisted(() => ({
  getRepDirectory: vi.fn(),
  getRepRoutingProfiles: vi.fn(),
  upsertRepRoutingProfile: vi.fn(),
  enqueueSnackbar: vi.fn(),
}));

vi.mock('../../api/services/commercialIntelligenceService', () => ({
  default: {
    getRepDirectory: mocks.getRepDirectory,
    getRepRoutingProfiles: mocks.getRepRoutingProfiles,
    upsertRepRoutingProfile: mocks.upsertRepRoutingProfile,
  },
}));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));
vi.mock('notistack', () => ({
  useSnackbar: () => ({ enqueueSnackbar: mocks.enqueueSnackbar }),
}));

const summary = {
  userId: 41,
  name: 'Samira Saleh',
  email: 'samira@example.test',
  roleName: 'Sales Representative',
  activeLeads: 0,
  overdueLeads: 0,
  openRfqs: 0,
  draftQuotes: 0,
  followUpsDue: 0,
  pipelineGroups: [],
};
const missingProfile = {
  userId: 41,
  name: 'Samira Saleh',
  email: 'samira@example.test',
  roleName: 'Sales Representative',
  hasProfile: false,
  profileEffectiveNow: false,
  isRoutingEligible: null,
  capacityPercent: null,
  distributionWeight: null,
  territoryKeys: [],
  productCategoryKeys: [],
  version: 0,
  isAvailable: false,
  eligibilityReason: 'A governed routing profile is required.',
};
const savedProfile = {
  ...missingProfile,
  hasProfile: true,
  profileEffectiveNow: true,
  isRoutingEligible: true,
  capacityPercent: 100,
  distributionWeight: 1,
  version: 1,
  isAvailable: true,
  eligibilityReason: 'Eligible for governed routing.',
};

describe('RepDirectoryPage routing profile editor', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.getRepDirectory.mockResolvedValue([summary]);
    mocks.getRepRoutingProfiles
      .mockResolvedValueOnce([missingProfile])
      .mockResolvedValue([savedProfile]);
    mocks.upsertRepRoutingProfile.mockResolvedValue(undefined);
  });

  it('keeps the named editor stable until the authoritative row refreshes, then closes it', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <RepDirectoryPage />
        </QueryClientProvider>
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Enable routing' }));
    expect(screen.getByRole('dialog', { name: 'Routing profile — Samira Saleh' })).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Save profile' }));
    await waitFor(() => expect(mocks.getRepRoutingProfiles).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.getByText('Eligible', { exact: true })).toBeVisible();
    expect(screen.getByText(/Set to 100% capacity, weight 1/)).toBeVisible();
    expect(mocks.enqueueSnackbar).toHaveBeenCalledWith('Routing profile saved', { variant: 'success' });
  });
});
