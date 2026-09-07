import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../api/client';
import DeploymentDatabasePanel from './DeploymentDatabasePanel';
import type { PlatformDataBoundaryManifest } from '../types';

/**
 * The dead end this closes.
 *
 * The server refuses to record the deployment's database while other tenants are still registered
 * against a different one — correctly: letting the claim be rewritten under assets that carry the
 * old value is exactly how a residency control gets satisfied by editing a string.
 *
 * But the refusal said "re-register or move them first" and named no control anywhere that does
 * it. One tenant left over from a test run therefore blocked activation for every other tenant on
 * the deployment, permanently, with the operator staring at a red toast and no button.
 *
 * The way out is offered only on the OBSERVED path — where the value was read from the live
 * connection rather than typed — because moving a registration to an observed value is correcting
 * evidence, not asserting a claim.
 */
const manifest: PlatformDataBoundaryManifest = {
  configured: false,
  primaryPostgreSqlScope: null,
  boundaries: [],
  defects: [],
  configurationKey: 'Platform:DataBoundaries',
  source: 'none',
  observation: {
    host: 'ep-super-sea-admna6dt-pooler.c-2.us-east-1.aws.neon.tech', providerName: 'Neon',
    opaqueProviderReference: 'neon-ep-super-sea-admna6dt', region: 'us-east-1',
    basis: 'Read from the database host this process is connected to.', isUsable: true,
  },
  recordedBy: null,
  recordedOn: null,
  recordedBasis: null,
};

const conflictError = {
  response: { data: { conflictingTenantIds: [3], canReregister: true } },
};

const renderPanel = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider><DeploymentDatabasePanel manifest={manifest} /></SnackbarProvider>
  </QueryClientProvider>,
);

describe('deployment database conflict', () => {
  beforeEach(() => vi.restoreAllMocks());

  it('offers a way out instead of a sentence the operator cannot act on', async () => {
    const record = vi.spyOn(platformApi, 'recordPlatformDataBoundary')
      .mockRejectedValueOnce(conflictError)
      .mockResolvedValueOnce({} as never);

    renderPanel();
    fireEvent.click(await screen.findByRole('button', { name: /for every tenant/ }));

    // It names the tenant, says why it is there, and says what it blocks.
    expect(await screen.findByText(/1 tenant is still registered against a different database/i)).toBeVisible();
    expect(screen.getByText(/Tenant 3/)).toBeVisible();
    expect(screen.getByText(/no tenant can finish activation/i)).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: /Move it onto this database/i }));

    await waitFor(() => expect(record).toHaveBeenCalledTimes(2));
    // The retry asks for the move explicitly; the first attempt never did.
    expect(record.mock.calls[0][0]).toMatchObject({ reregisterConflictingTenants: false });
    expect(record.mock.calls[1][0]).toMatchObject({ reregisterConflictingTenants: true });
  });

  it('does not offer the move when the value was typed rather than observed', async () => {
    // canReregister is the server's answer, not the console's guess: a typed claim must never be
    // able to drag tenant registrations along behind it.
    vi.spyOn(platformApi, 'recordPlatformDataBoundary')
      .mockRejectedValueOnce({ response: { data: { conflictingTenantIds: [3], canReregister: false } } });

    renderPanel();
    fireEvent.click(await screen.findByRole('button', { name: /for every tenant/ }));

    expect(await screen.findByText(/1 tenant is still registered against a different database/i)).toBeVisible();
    expect(screen.queryByRole('button', { name: /Move it onto this database/i })).not.toBeInTheDocument();
    expect(screen.getByText(/satisfied by editing a string/i)).toBeVisible();
  });
});
