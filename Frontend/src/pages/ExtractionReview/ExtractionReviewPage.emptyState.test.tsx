import type { ComponentType } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import ExtractionReviewPage from './ExtractionReviewPage';

/**
 * The empty review queue said "Successfully persisted Leads that require human validation will
 * appear here." — engineering words (persisted, validation) on the one screen a rep sees when they
 * are caught up. It now says what a rep would say.
 */

const getNeedsReview = vi.fn();
const getReadiness = vi.fn();

vi.mock('../../api/services/extractionReviewService', () => ({
  default: { getNeedsReview: (params: unknown) => getNeedsReview(params) },
}));
vi.mock('../../api/services/operationalReadinessService', () => ({
  default: { get: () => getReadiness() },
}));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7 }, hasPermission: () => true }),
}));
vi.mock('../../components/layout/ViewTabs', () => ({ default: () => null }));

// The grid itself is not under test; only its empty state is, so the mock renders exactly that.
vi.mock('@mui/x-data-grid', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mui/x-data-grid')>();
  return {
    ...actual,
    DataGrid: (props: { rows: unknown[]; slots?: { noRowsOverlay?: ComponentType } }) => {
      const Overlay = props.slots?.noRowsOverlay;
      return props.rows.length === 0 && Overlay ? <Overlay /> : <div data-testid="review-grid" />;
    },
  };
});

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter>
      <ExtractionReviewPage />
    </MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  getNeedsReview.mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 50 });
  getReadiness.mockResolvedValue({
    checkedAt: '2026-09-15T12:00:00Z', deploymentReadiness: 'Healthy', blockingReasons: [], healthChecks: [], queues: [],
    aiExternalDependency: { total: 0, local: 0, external: 0, authorizedExternal: 0, unresolved: 0, externalSharePercent: 0, ceilingPercent: 10, windowSize: 100, ceilingBreached: false },
  });
});

describe('Documents to check — empty state', () => {
  it('speaks the rep’s words, not the pipeline’s', async () => {
    renderPage();
    expect(await screen.findByText("Documents that need a person's check will appear here.")).toBeInTheDocument();
    expect(screen.queryByText(/persisted/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/human validation/i)).not.toBeInTheDocument();
  });
});
