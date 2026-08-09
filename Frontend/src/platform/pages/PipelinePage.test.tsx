import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../api/client';
import type { ExtractionJob } from '../types';
import PipelinePage from './PipelinePage';
import { ThemeContextProvider } from '../../context/ThemeContext';

const permission = vi.hoisted(() => ({ isOwner: true }));
vi.mock('../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: permission.isOwner ? 'Owner' : 'ReadOnlyOps', isOwner: permission.isOwner,
    canAdministerTenants: permission.isOwner, canAdministerBilling: permission.isOwner,
    canImpersonate: permission.isOwner, roleUnknown: false,
  }),
}));

const deadLetter: ExtractionJob = {
  id: '9007199254740999', tenantId: '41', tenantName: 'Northwind Aerospace',
  documentName: 'customer-rfq.pdf', status: 'dead_letter', attempts: 3, maxAttempts: 3,
  enqueuedAt: '2026-08-08T12:00:00Z', updatedAt: '2026-08-08T12:05:00Z',
  latencyMs: null, error: 'Processing failed; diagnostic details are restricted.',
};

const renderPage = () => render(
  <MemoryRouter initialEntries={['/platform/pipeline']}>
    <ThemeContextProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SnackbarProvider><PipelinePage /></SnackbarProvider>
      </QueryClientProvider>
    </ThemeContextProvider>
  </MemoryRouter>,
);

beforeEach(() => {
  permission.isOwner = true;
  vi.restoreAllMocks();
  vi.spyOn(platformApi, 'getQueueStats').mockResolvedValue({
    queueDepth: 0, inFlight: 0, deadLetter: 1, processedLast24h: 10,
    avgLatencyMs: 50, successRate: 0.9,
  });
  vi.spyOn(platformApi, 'listTenants').mockResolvedValue([]);
  vi.spyOn(platformApi, 'listJobs').mockResolvedValue([deadLetter]);
});

describe('platform dead-letter recovery', () => {
  it('requires explicit tenant, queue, item, reason and idempotency confirmation', async () => {
    const recover = vi.spyOn(platformApi, 'recoverPlatformDeadLetter').mockResolvedValue({
      queue: 'extraction', itemId: deadLetter.id, tenantId: deadLetter.tenantId,
      status: 'RetryQueued', idempotentReplay: false,
    });
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Recover' }));
    expect(screen.getByText(/Northwind Aerospace \(41\)/)).toBeVisible();
    expect(screen.getByText(/Queue:/).parentElement).toHaveTextContent('extraction');
    expect(screen.getByText(/Item:/).parentElement).toHaveTextContent(deadLetter.id);
    const key = screen.getByDisplayValue(/^platform-dlq-9007199254740999-/) as HTMLInputElement;
    expect(key.value).toMatch(/^platform-dlq-9007199254740999-/);

    const reason = screen.getAllByRole('textbox').find((field) => field.tagName === 'TEXTAREA');
    expect(reason).toBeDefined();
    fireEvent.change(reason!, {
      target: { value: 'Provider configuration corrected and immutable evidence verified.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Queue governed retry' }));

    await waitFor(() => expect(recover).toHaveBeenCalledWith('41', {
      queue: 'extraction', itemId: deadLetter.id,
      reason: 'Provider configuration corrected and immutable evidence verified.',
      idempotencyKey: key.value,
    }));
    expect(await screen.findByText(/queued for governed retry.*Audit evidence refreshed/i)).toBeVisible();
  });

  it('does not offer a recovery mutation to non-Owners', async () => {
    permission.isOwner = false;
    renderPage();
    expect(await screen.findByText('customer-rfq.pdf')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Recover' })).not.toBeInTheDocument();
  });

  it('surfaces the server MFA denial without claiming a retry was queued', async () => {
    vi.spyOn(platformApi, 'recoverPlatformDeadLetter').mockRejectedValue({
      isAxiosError: true,
      response: { status: 403, data: { error: 'A current MFA-authenticated Owner session is required.' } },
    });
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Recover' }));
    const reason = screen.getAllByRole('textbox').find((field) => field.tagName === 'TEXTAREA');
    fireEvent.change(reason!, { target: { value: 'Dependency repaired and evidence verified.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Queue governed retry' }));

    expect(await screen.findByText('A current MFA-authenticated Owner session is required.')).toBeVisible();
    expect(screen.queryByText(/queued for governed retry/i)).not.toBeInTheDocument();
  });
});
