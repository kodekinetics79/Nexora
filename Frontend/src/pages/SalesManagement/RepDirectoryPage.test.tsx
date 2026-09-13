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
  acceptsManualAssignment: false,
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
  acceptsManualAssignment: true,
  eligibilityReason: 'Eligible for governed routing.',
};
const overCapacityProfile = {
  ...savedProfile,
  isAvailable: false,
  acceptsManualAssignment: true,
  measuredCapacityPercent: 0,
  workloadPoints: 127,
  eligibilityReason: 'Configured or measured capacity is exhausted',
};

describe('RepDirectoryPage routing verdicts', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.getRepDirectory.mockResolvedValue([summary]);
  });

  const renderPage = () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <RepDirectoryPage />
        </QueryClientProvider>
      </MemoryRouter>,
    );
  };

  it('calls a rep over the workload ceiling "At capacity", not "Blocked", and says a manager can still assign', async () => {
    mocks.getRepRoutingProfiles.mockResolvedValue([overCapacityProfile]);
    renderPage();

    expect(await screen.findByText('At capacity', { exact: true })).toBeVisible();
    expect(screen.getByText(/127 workload points/)).toBeVisible();
    expect(screen.getByText(/a manager can still assign them by hand/i)).toBeVisible();
    expect(screen.queryByText(/no manual assignment will be accepted/i)).not.toBeInTheDocument();
  });

  it('warns that nothing can be assigned at all only when nobody has an eligible profile', async () => {
    mocks.getRepRoutingProfiles.mockResolvedValue([missingProfile]);
    renderPage();

    expect(await screen.findByText(/no manual assignment will be accepted/i)).toBeVisible();
    expect(screen.getByText('No profile', { exact: true })).toBeVisible();
  });
});

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
