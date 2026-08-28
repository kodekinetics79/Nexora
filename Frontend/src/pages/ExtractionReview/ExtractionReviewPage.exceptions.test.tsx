import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import ExtractionReviewPage from './ExtractionReviewPage';

const getNeedsReview = vi.fn();
const getReadiness = vi.fn();
const hasPermission = vi.fn();
const navigate = vi.fn();

vi.mock('../../api/services/extractionReviewService', () => ({
  default: { getNeedsReview: (params: unknown) => getNeedsReview(params) },
}));

vi.mock('../../api/services/operationalReadinessService', () => ({
  default: { get: () => getReadiness() },
}));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 7 },
    hasPermission: (module: string, action?: string) => hasPermission(module, action),
  }),
}));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

vi.mock('../../components/layout/ViewTabs', () => ({ default: () => null }));

vi.mock('@mui/x-data-grid', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mui/x-data-grid')>();
  return {
    ...actual,
    DataGrid: () => <div data-testid="review-grid" />,
  };
});

const renderPage = () => {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ExtractionReviewPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
};

describe('Extraction Review exception boundary', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getNeedsReview.mockResolvedValue({
      items: [], totalCount: 0, pageNumber: 1, pageSize: 50,
    });
    getReadiness.mockResolvedValue({
      checkedAt: '2026-08-28T12:00:00Z',
      deploymentReadiness: 'Unhealthy',
      blockingReasons: ['Lead extraction has 50 dead-letter item(s).'],
      healthChecks: [],
      queues: [
        { key: 'extraction', label: 'Lead extraction', pending: 0, inFlight: 0, deadLetter: 50 },
      ],
      aiLast30Days: { total: 50, local: 0, external: 0, unresolved: 0, externalSharePercent: 0 },
    });
  });

  it('explains that dead-letter jobs stopped before the reviewable-Lead queue', async () => {
    hasPermission.mockImplementation((module: string, action?: string) => (
      module === 'Users' && action === 'view'
    ));

    renderPage();

    expect(await screen.findByText(/50 extraction exceptions stopped before a reviewable Lead was created/i)).toBeInTheDocument();
    screen.getByRole('button', { name: /manage exceptions/i }).click();
    expect(navigate).toHaveBeenCalledWith('/admin/operations');
  });

  it('does not request or expose operational counts to a reviewer without Users view', async () => {
    hasPermission.mockReturnValue(false);

    renderPage();

    await waitFor(() => expect(getNeedsReview).toHaveBeenCalled());
    expect(getReadiness).not.toHaveBeenCalled();
    expect(screen.queryByText(/extraction exceptions stopped before/i)).not.toBeInTheDocument();
  });
});
