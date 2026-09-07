import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import WatchedFoldersPage from './WatchedFoldersPage';

/**
 * "Add documents to this folder" may only report what the SERVER wrote.
 *
 * FolderService skips a zero-byte upload, a filename left unusable by sanitising and a
 * path-traversal filename, and carries on — so the number of files the browser posted is not the
 * number that landed. The endpoint was changed to answer with `uploaded` and `skipped` precisely
 * because reporting the requested count was the original bug (EmailController.UploadLeadsToFolder),
 * and a green "5 documents placed" over three written files puts two documents nowhere: not in the
 * folder, not in any table, and only in a server log the rep will never read.
 */

const uploadToFolder = vi.fn();
const enqueueSnackbar = vi.fn();

vi.mock('../../api/services/leadService', () => ({
  default: {
    getAll: () => Promise.resolve({ items: [], totalCount: 0, pageNumber: 1, pageSize: 5 }),
    uploadToFolder: (formData: FormData, folderType: string) => uploadToFolder(formData, folderType),
    processAllFolderLeads: vi.fn(),
  },
}));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true, userData: { businessUnitId: 1 } }),
}));

vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar }) }));

const renderPage = () => {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><WatchedFoldersPage /></MemoryRouter>
    </QueryClientProvider>,
  );
};

/** Five ordinary RFQ documents, chosen from a mapped share. */
const fiveDocuments = () =>
  ['boq.pdf', 'drawing.pdf', 'terms.pdf', 'annex.pdf', 'schedule.pdf']
    .map((name) => new File(['x'], name, { type: 'application/pdf' }));

/** The hidden picker behind the first folder card's "Add documents to this folder" button. */
const filePicker = (container: HTMLElement): HTMLInputElement => {
  const input = container.querySelector('input[type="file"]');
  if (!input) throw new Error('no file input rendered');
  return input as HTMLInputElement;
};

beforeEach(() => {
  vi.clearAllMocks();
});

describe('placing documents in a watched folder reports what was written', () => {
  it('warns, with the server’s own counts, when files were skipped', async () => {
    // The exact response EmailController returns when two of five arrive as zero-byte cloud
    // placeholders — the case its own comment names.
    uploadToFolder.mockResolvedValue({
      message:
        '3 of 5 files were uploaded to the Shared leads folder. 2 could not be stored — they were '
        + 'empty, or their filename was rejected.',
      uploaded: 3,
      skipped: 2,
    });
    const { container } = renderPage();

    fireEvent.change(filePicker(container), { target: { files: fiveDocuments() } });

    await waitFor(() => expect(enqueueSnackbar).toHaveBeenCalled());
    const [text, options] = enqueueSnackbar.mock.calls[0];
    // What actually landed, and what did not.
    expect(text).toMatch(/3 of 5 documents placed/i);
    expect(text).toMatch(/2 could not be stored/i);
    // The claim that cost the two documents.
    expect(text).not.toMatch(/^5 documents placed/i);
    expect(options).toMatchObject({ variant: 'warning' });
  });

  it('says success only for the files the server confirms it wrote', async () => {
    uploadToFolder.mockResolvedValue({
      message: '5 files uploaded successfully to the Shared leads folder.',
      uploaded: 5,
      skipped: 0,
    });
    const { container } = renderPage();

    fireEvent.change(filePicker(container), { target: { files: fiveDocuments() } });

    await waitFor(() => expect(enqueueSnackbar).toHaveBeenCalled());
    const [text, options] = enqueueSnackbar.mock.calls[0];
    expect(text).toMatch(/5 documents placed in the shared folder/i);
    expect(text).toMatch(/nothing is read until the folder is swept/i);
    expect(options).toMatchObject({ variant: 'success' });
  });

  it('falls back to the selected count only when the deployment reports neither number', async () => {
    // An older backend acknowledges the write and says nothing more. The browser's own count is
    // then all there is — but it must not be produced while the server IS reporting.
    uploadToFolder.mockResolvedValue({ message: 'Files uploaded.' });
    const { container } = renderPage();

    fireEvent.change(filePicker(container), { target: { files: fiveDocuments() } });

    await waitFor(() => expect(enqueueSnackbar).toHaveBeenCalled());
    const [text, options] = enqueueSnackbar.mock.calls[0];
    expect(text).toMatch(/5 documents placed in the shared folder/i);
    expect(options).toMatchObject({ variant: 'success' });
  });
});
