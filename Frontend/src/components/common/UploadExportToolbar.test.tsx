import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import UploadExportToolbar from './UploadExportToolbar';

const snackbar = vi.hoisted(() => vi.fn());
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: snackbar }) }));

const handlers = () => ({
  onDownloadTemplate: vi.fn().mockResolvedValue({ data: 'template' }),
  onUpload: vi.fn().mockResolvedValue({ data: {} }),
  onExport: vi.fn().mockResolvedValue({ data: 'export' }),
});

beforeEach(() => {
  vi.clearAllMocks();
  vi.stubGlobal('URL', { createObjectURL: vi.fn(() => 'blob:test'), revokeObjectURL: vi.fn() });
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('UploadExportToolbar', () => {
  it('keeps the existing three-button layout as the default', () => {
    render(<UploadExportToolbar {...handlers()} />);

    expect(screen.getByRole('button', { name: 'Download Excel Template' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Upload filled template' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Export current data to Excel' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'More' })).not.toBeInTheDocument();
  });

  it('omits import for a read-only user and labels the accessible menu', () => {
    render(<UploadExportToolbar {...handlers()} variant="menu" canUpload={false} />);

    const more = screen.getByRole('button', { name: 'More' });
    expect(more).toHaveAttribute('aria-expanded', 'false');
    fireEvent.click(more);

    expect(screen.getByRole('menu', { name: 'More' })).toBeVisible();
    expect(more).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getAllByRole('menuitem').map((item) => item.textContent)).toEqual([
      'Download template', 'Export products',
    ]);
  });

  it('runs custom actions, respects disabled actions and closes after selection', async () => {
    const recordStock = vi.fn();
    render(<UploadExportToolbar {...handlers()} variant="menu" menuActions={[
      { label: 'Record stock', onClick: recordStock },
      { label: 'Unavailable action', onClick: vi.fn(), disabled: true },
    ]} />);
    fireEvent.click(screen.getByRole('button', { name: 'More' }));

    expect(screen.getByRole('menuitem', { name: 'Unavailable action' })).toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(screen.getByRole('menuitem', { name: 'Record stock' }));

    expect(recordStock).toHaveBeenCalledOnce();
    await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'More' })).toHaveAttribute('aria-expanded', 'false');
  });

  it('downloads through existing handlers and prevents reopening while loading', async () => {
    const props = handlers();
    let complete!: (result: { data: string }) => void;
    props.onDownloadTemplate.mockImplementation(() => new Promise((resolve) => { complete = resolve; }));
    render(<UploadExportToolbar {...props} variant="menu" templateFileName="Products.xlsx" />);
    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Download template' }));

    expect(props.onDownloadTemplate).toHaveBeenCalledOnce();
    expect(screen.getByRole('button', { name: 'More' })).toBeDisabled();
    await act(async () => complete({ data: 'template' }));
    expect(screen.getByRole('button', { name: 'More' })).toBeEnabled();
    expect(HTMLAnchorElement.prototype.click).toHaveBeenCalledOnce();

    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Export products' }));
    await waitFor(() => expect(props.onExport).toHaveBeenCalledOnce());
    await waitFor(() => expect(HTMLAnchorElement.prototype.click).toHaveBeenCalledTimes(2));
  });

  it('opens the file picker without uploading until a file is selected', async () => {
    const props = handlers();
    const fileClick = vi.spyOn(HTMLInputElement.prototype, 'click').mockImplementation(() => undefined);
    render(<UploadExportToolbar {...props} variant="menu" />);
    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Import products' }));

    expect(fileClick).toHaveBeenCalledOnce();
    expect(props.onUpload).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument());
  });

  it('keeps the existing failure message and re-enables the trigger', async () => {
    const props = handlers();
    props.onExport.mockRejectedValue(new Error('offline'));
    render(<UploadExportToolbar {...props} variant="menu" />);
    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Export products' }));

    await waitFor(() => expect(snackbar).toHaveBeenCalledWith(expect.any(String), { variant: 'error' }));
    expect(screen.getByRole('button', { name: 'More' })).toBeEnabled();
  });
});
