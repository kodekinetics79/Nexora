import React from 'react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import CustomFieldsPage from './CustomFieldsPage';
import type { CustomFieldDefinition } from '../../../api/services/customFieldService';

/** MUI renders a select as a button + listbox, not an <input>, so fireEvent.change cannot drive it. */
const chooseFromSelect = async (labelPattern: RegExp, optionName: string) => {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: labelPattern }));
  const option = await screen.findByRole('option', { name: optionName });
  fireEvent.click(option);
};

vi.mock('../../../api/services/customFieldService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/customFieldService')>();
  return {
    ...actual,
    default: {
      listDefinitions: vi.fn(),
      createDefinition: vi.fn(),
      addVersion: vi.fn(),
      reorder: vi.fn(),
      retire: vi.fn(),
      reactivate: vi.fn(),
    },
  };
});

const service = (await import('../../../api/services/customFieldService')).default as unknown as Record<
  string, ReturnType<typeof vi.fn>
>;

const definition = (over: Partial<CustomFieldDefinition> = {}): CustomFieldDefinition => ({
  id: 1,
  entityType: 'Customer',
  stableKey: 'vendor_code',
  status: 'Active',
  activeVersionNumber: 1,
  displayOrder: 0,
  versions: [{
    versionNumber: 1, label: 'Our vendor code', dataType: 'Text', isRequired: false,
    options: [], createdOn: '2026-08-01T00:00:00Z', createdBy: 'admin',
  }],
  createdOn: '2026-08-01T00:00:00Z',
  createdBy: 'admin',
  version: 2,
  ...over,
});

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider>{children}</SnackbarProvider>
  </QueryClientProvider>
);

describe('CustomFieldsPage', () => {
  beforeEach(() => vi.clearAllMocks());

  it('lists the tenant fields in display order', async () => {
    service.listDefinitions.mockResolvedValue([
      definition({ id: 2, stableKey: 'bravo', displayOrder: 1, versions: [{ ...definition().versions[0], label: 'Bravo' }] }),
      definition({ id: 1, stableKey: 'alpha', displayOrder: 0, versions: [{ ...definition().versions[0], label: 'Alpha' }] }),
    ]);

    render(<CustomFieldsPage />, { wrapper });

    await waitFor(() => expect(screen.getByText('Alpha')).toBeInTheDocument());
    const rows = screen.getAllByRole('row').slice(1); // drop the header row
    expect(rows[0]).toHaveTextContent('Alpha');
    expect(rows[1]).toHaveTextContent('Bravo');
  });

  it('suggests a key from the name but leaves it editable, and calls it permanent', async () => {
    service.listDefinitions.mockResolvedValue([]);

    render(<CustomFieldsPage />, { wrapper });
    await waitFor(() => expect(screen.getByText(/No custom fields yet/)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /Add field/ }));
    fireEvent.change(screen.getByLabelText(/Name shown to users/), {
      target: { value: 'Our Vendor Code' },
    });

    expect(screen.getByLabelText(/Reference key/)).toHaveValue('our_vendor_code');
    expect(screen.getByText(/This is permanent once saved/)).toBeInTheDocument();
  });

  it('locks the key when editing and explains why', async () => {
    service.listDefinitions.mockResolvedValue([definition()]);

    render(<CustomFieldsPage />, { wrapper });
    await waitFor(() => expect(screen.getByText('Our vendor code')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'Edit vendor_code' }));

    expect(screen.getByLabelText(/Reference key/)).toBeDisabled();
    expect(screen.getByText(/renaming it would lose them/)).toBeInTheDocument();
  });

  it('warns before a data-type change that the server may refuse', async () => {
    service.listDefinitions.mockResolvedValue([definition()]);

    render(<CustomFieldsPage />, { wrapper });
    await waitFor(() => expect(screen.getByText('Our vendor code')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Edit vendor_code' }));

    expect(screen.queryByText(/Nexora will refuse this change/)).not.toBeInTheDocument();

    await chooseFromSelect(/Type of information/, 'Number');

    await waitFor(() => expect(screen.getByText(/Nexora will refuse this change/)).toBeInTheDocument());
    expect(screen.getByText(/Retire this field and create a\s+replacement/)).toBeInTheDocument();
  });

  it('says plainly that retiring keeps the data, and requires a reason', async () => {
    service.listDefinitions.mockResolvedValue([definition()]);
    service.retire.mockResolvedValue(definition({ status: 'Retired' }));

    render(<CustomFieldsPage />, { wrapper });
    await waitFor(() => expect(screen.getByText('Our vendor code')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'Retire vendor_code' }));

    expect(screen.getByText(/Retiring is not deleting/)).toBeInTheDocument();
    const confirm = screen.getByRole('button', { name: /Retire field/ });
    expect(confirm).toBeDisabled();

    fireEvent.change(screen.getByLabelText(/Why is it being retired/), {
      target: { value: 'Superseded by the framework reference' },
    });
    await waitFor(() => expect(confirm).toBeEnabled());
    fireEvent.click(confirm);

    await waitFor(() => expect(service.retire).toHaveBeenCalledWith(
      1, 'Superseded by the framework reference',
    ));
  });

  it('offers reactivate — not edit — on a retired field', async () => {
    service.listDefinitions.mockResolvedValue([definition({ status: 'Retired', retirementReason: 'Paused' })]);
    service.reactivate.mockResolvedValue(definition());

    render(<CustomFieldsPage />, { wrapper });
    await waitFor(() => expect(screen.getByText('Our vendor code')).toBeInTheDocument());

    expect(screen.queryByRole('button', { name: 'Edit vendor_code' })).not.toBeInTheDocument();
    expect(screen.getByText(/Retired: Paused/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Reactivate vendor_code' }));
    await waitFor(() => expect(service.reactivate).toHaveBeenCalledWith(1));
  });

  it('reorders as one batch call carrying the whole new order', async () => {
    service.listDefinitions.mockResolvedValue([
      definition({ id: 1, stableKey: 'alpha', displayOrder: 0 }),
      definition({ id: 2, stableKey: 'bravo', displayOrder: 1 }),
    ]);
    service.reorder.mockResolvedValue([]);

    render(<CustomFieldsPage />, { wrapper });
    await waitFor(() => expect(screen.getAllByText('Our vendor code')).toHaveLength(2));

    fireEvent.click(screen.getAllByRole('button', { name: /Move Our vendor code up/ })[1]);

    await waitFor(() => expect(service.reorder).toHaveBeenCalledWith('Customer', [
      { definitionId: 2, displayOrder: 0 },
      { definitionId: 1, displayOrder: 1 },
    ]));
  });

  it('creates a field at the end of the current order', async () => {
    service.listDefinitions.mockResolvedValue([definition()]);
    service.createDefinition.mockResolvedValue(definition());

    render(<CustomFieldsPage />, { wrapper });
    await waitFor(() => expect(screen.getByText('Our vendor code')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /Add field/ }));
    fireEvent.change(screen.getByLabelText(/Name shown to users/), { target: { value: 'Framework expiry' } });
    await chooseFromSelect(/Type of information/, 'Date');
    fireEvent.click(screen.getByRole('button', { name: 'Add field' }));

    await waitFor(() => expect(service.createDefinition).toHaveBeenCalledWith(expect.objectContaining({
      entityType: 'Customer',
      stableKey: 'framework_expiry',
      displayOrder: 1,
      activate: true,
      version: expect.objectContaining({ label: 'Framework expiry', dataType: 'Date' }),
    })));
  });

  it('will not save a choice field with no choices', async () => {
    service.listDefinitions.mockResolvedValue([]);

    render(<CustomFieldsPage />, { wrapper });
    await waitFor(() => expect(screen.getByText(/No custom fields yet/)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /Add field/ }));
    fireEvent.change(screen.getByLabelText(/Name shown to users/), { target: { value: 'Segment' } });
    await chooseFromSelect(/Type of information/, 'Choice');

    await waitFor(() => expect(screen.getByRole('button', { name: 'Add field' })).toBeDisabled());
  });

  it('reports a load failure instead of showing an empty list', async () => {
    service.listDefinitions.mockRejectedValue(new Error('offline'));

    render(<CustomFieldsPage />, { wrapper });

    await waitFor(() =>
      expect(screen.getByText(/No empty result has been assumed/)).toBeInTheDocument());
  });
});
