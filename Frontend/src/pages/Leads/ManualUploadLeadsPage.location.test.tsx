import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const navigate = vi.fn();

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));
vi.mock('../../api/services/leadService', () => ({
  default: { uploadGoverned: vi.fn() },
  readUploadPausedProblem: () => null,
}));

import ManualUploadLeadsPage from './ManualUploadLeadsPage';

const renderPage = () => render(
  <MemoryRouter initialEntries={['/procurement/leads/manual-upload']}>
    <QueryClientProvider client={new QueryClient()}>
      <ManualUploadLeadsPage />
    </QueryClientProvider>
  </MemoryRouter>,
);

describe('Manual document upload ownership', () => {
  beforeEach(() => navigate.mockReset());

  it('is a Leads workflow, not an Inbox tab', () => {
    renderPage();

    expect(screen.getByRole('heading', { name: 'Upload documents' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Inbox' })).not.toBeInTheDocument();
    expect(screen.queryByRole('tablist', { name: 'Inbox views' })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Back to Leads' }));
    expect(navigate).toHaveBeenCalledWith('/procurement/leads/all');
  });
});
