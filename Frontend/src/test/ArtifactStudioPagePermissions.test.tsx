import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import ArtifactStudioPage from '../pages/PlatformGovernance/ArtifactStudioPage';

let canEdit = true;

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { isSuperAdmin: false },
    hasPermission: (_module: string, action: string = 'view') => action === 'view' || canEdit,
  }),
}));

const listArtifacts = vi.fn();
const getArtifact = vi.fn();

vi.mock('../api/services/platformGovernanceService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/services/platformGovernanceService')>();
  return {
    ...actual,
    platformGovernanceService: {
      listArtifacts: (...args: unknown[]) => listArtifacts(...args),
      getArtifact: (...args: unknown[]) => getArtifact(...args),
    },
  };
});

const ARTIFACT = {
  id: 1,
  artifactType: 'CommercialTaxonomy' as const,
  artifactKey: 'customer-rfq',
  name: 'Customer RFQ',
  description: 'What the platform reads from an inbound RFQ.',
  status: 'Draft',
  currentVersionNumber: 1,
  productionVersionNumber: null,
  version: 1,
  updatedOn: '2026-09-08T12:00:00Z',
  updatedByUserId: 4,
};

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <ArtifactStudioPage title="Commercial Taxonomy" subtitle="Versioned schemas" types={['CommercialTaxonomy']} />
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  canEdit = true;
  listArtifacts.mockResolvedValue([ARTIFACT]);
  getArtifact.mockResolvedValue({ artifact: ARTIFACT, versions: [], events: [] });
});

describe('ArtifactStudioPage authoring permission', () => {
  it('offers the authoring controls to someone the server will accept', async () => {
    renderPage();
    expect(await screen.findByRole('button', { name: /create governed artifact/i })).toBeEnabled();
    expect(screen.queryByText(/Read-only governance view/i)).not.toBeInTheDocument();
  });

  it('keeps the read surface and disables what the server would refuse', async () => {
    // The whole reason this guard sits on the controls rather than on the Setup card: the
    // artifact list, its version history and the activity ledger are all served on Users/View,
    // and they are the audit trail a read-only governance reviewer is entitled to see. Hiding
    // the screen to spare them a refused click would cost them the only door to it.
    canEdit = false;
    renderPage();

    expect(await screen.findByText('Customer RFQ')).toBeVisible();
    expect(screen.getByText(/Read-only governance view/i)).toBeVisible();
    expect(screen.getByRole('button', { name: /create governed artifact/i })).toBeDisabled();
  });

  it('explains the refusal in words rather than grey-ing a control out in silence', async () => {
    canEdit = false;
    renderPage();
    expect(await screen.findByText(/needs edit permission on the Users module/i)).toBeVisible();
  });
});
