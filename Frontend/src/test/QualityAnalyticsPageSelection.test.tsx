import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import QualityAnalyticsPage from '../pages/PlatformGovernance/QualityAnalyticsPage';

const getQualityAnalytics = vi.fn();

vi.mock('../api/services/platformGovernanceService', () => ({
  platformGovernanceService: {
    getQualityAnalytics: (windowDays: number, drilldown?: string) =>
      getQualityAnalytics(windowDays, drilldown),
  },
}));

const metric = (key: string, label: string, drilldownKey: string, value: number) => ({
  key, label, value, unit: '%', numerator: 1, denominator: 10,
  definition: `${label} definition.`, evidenceStatus: 'Measured', drilldownKey,
});

const VIEW = {
  from: '2026-08-09T00:00:00Z',
  to: '2026-09-08T00:00:00Z',
  metrics: [
    metric('straight-through', 'Straight-through processing', 'terminal-intake', 90),
    // Two metrics, one cohort — the shape the fix was written for.
    metric('external-dependency', 'External AI dependency', 'external-ai', 100),
    metric('unauthorized-external-dependency', 'Unauthorized external AI dependency', 'external-ai', 0),
  ],
  exceptionCauses: [],
  records: [],
  recommendations: [{
    priority: 'Monitor',
    title: 'No threshold breach in the selected cohort',
    recommendation: 'Continue collecting validated outcomes.',
    evidence: 'Measured rates remain within default governance thresholds.',
    drilldownKey: 'terminal-intake',
    metricKey: '',
  }],
  definitionVersion: 'v1',
  accuracyLimitation: 'None of the rates on this page is an extraction accuracy.',
};

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <QualityAnalyticsPage />
  </QueryClientProvider>,
);

describe('Quality Analytics metric selection', () => {
  beforeEach(() => {
    getQualityAnalytics.mockReset();
    getQualityAnalytics.mockResolvedValue(VIEW);
  });

  const card = (label: string) =>
    screen.findByRole('button', { name: `View evidence for ${label}` });

  it('lights exactly one of the two cards that share the external-ai cohort', async () => {
    renderPage();
    fireEvent.click(await card('External AI dependency'), { detail: 0 });
    await waitFor(async () =>
      expect(await card('External AI dependency')).toHaveAttribute('aria-pressed', 'true'));
    expect(await card('Unauthorized external AI dependency'))
      .toHaveAttribute('aria-pressed', 'false');
    expect(await screen.findByText('External AI dependency definition.')).toBeInTheDocument();

    fireEvent.click(await card('Unauthorized external AI dependency'), { detail: 0 });
    await waitFor(async () => expect(await card('Unauthorized external AI dependency'))
      .toHaveAttribute('aria-pressed', 'true'));
    expect(await card('External AI dependency')).toHaveAttribute('aria-pressed', 'false');
    expect(await screen.findByText('Unauthorized external AI dependency definition.'))
      .toBeInTheDocument();
  });

  it('keeps an explanation on screen after a recommendation is opened', async () => {
    renderPage();
    fireEvent.click(await card('Straight-through processing'), { detail: 0 });
    expect(await screen.findByText('Straight-through processing definition.')).toBeInTheDocument();

    // The Monitor fallback carries MetricKey "" from the backend. Selecting it changes the
    // record cohort to terminal-intake — the straight-through cohort — while blanking the
    // explanation and un-highlighting every card, so the table silently reloads under a
    // caption that no longer says what it is showing.
    fireEvent.click(screen.getByRole('button',
      { name: 'Review recommendation: No threshold breach in the selected cohort' }), { detail: 0 });
    expect(await screen.findByText('Straight-through processing definition.')).toBeInTheDocument();
    expect(await card('Straight-through processing')).toHaveAttribute('aria-pressed', 'true');
  });
});
