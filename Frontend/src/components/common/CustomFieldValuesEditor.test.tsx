import React from 'react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import CustomFieldValuesEditor from './CustomFieldValuesEditor';
import type { CustomFieldBagResponse } from '../../api/services/customFieldService';

vi.mock('../../api/services/customFieldService', () => ({
  default: { getRecordFields: vi.fn(), updateRecordFields: vi.fn() },
}));

const service = (await import('../../api/services/customFieldService')).default as unknown as {
  getRecordFields: ReturnType<typeof vi.fn>;
  updateRecordFields: ReturnType<typeof vi.fn>;
};

const bag = (over: Partial<CustomFieldBagResponse> = {}): CustomFieldBagResponse => ({
  entityType: 'Customer',
  entityId: 7,
  fields: [
    {
      stableKey: 'vendor_code', label: 'Our vendor code', dataType: 'Text',
      isRequired: true, displayOrder: 0, options: [], value: 'TC-9910',
      displayValue: 'TC-9910', requiresManagerAccess: false,
    },
    {
      stableKey: 'credit_days', label: 'Credit days', dataType: 'Integer',
      isRequired: false, displayOrder: 1, options: [], value: 30,
      displayValue: '30', requiresManagerAccess: false,
    },
  ],
  ...over,
});

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider>{children}</SnackbarProvider>
  </QueryClientProvider>
);

describe('CustomFieldValuesEditor', () => {
  beforeEach(() => vi.clearAllMocks());

  it('renders one input per tenant-defined field, with the stored value', async () => {
    service.getRecordFields.mockResolvedValue(bag());

    render(<CustomFieldValuesEditor entityType="Customer" entityId={7} canEdit />, { wrapper });

    await waitFor(() => expect(screen.getByLabelText(/Our vendor code/)).toHaveValue('TC-9910'));
    expect(screen.getByLabelText(/Credit days/)).toHaveValue(30);
  });

  it('renders nothing at all when the tenant has defined no fields', async () => {
    service.getRecordFields.mockResolvedValue(bag({ fields: [] }));

    const { container } = render(
      <CustomFieldValuesEditor entityType="Customer" entityId={7} canEdit />, { wrapper },
    );

    await waitFor(() => expect(service.getRecordFields).toHaveBeenCalled());
    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });

  it('does not ask the server for a record that does not exist yet', () => {
    render(<CustomFieldValuesEditor entityType="Customer" entityId={null} canEdit />, { wrapper });
    expect(service.getRecordFields).not.toHaveBeenCalled();
  });

  it('sends every field on save, and clears a blanked value with null', async () => {
    service.getRecordFields.mockResolvedValue(bag());
    service.updateRecordFields.mockResolvedValue(bag());

    render(<CustomFieldValuesEditor entityType="Customer" entityId={7} canEdit />, { wrapper });
    await waitFor(() => expect(screen.getByLabelText(/Our vendor code/)).toHaveValue('TC-9910'));

    fireEvent.change(screen.getByLabelText(/Our vendor code/), { target: { value: 'TC-0001' } });
    fireEvent.change(screen.getByLabelText(/Credit days/), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: /Save these fields/ }));

    await waitFor(() => expect(service.updateRecordFields).toHaveBeenCalledWith(
      'Customer', 7, { vendor_code: 'TC-0001', credit_days: null },
    ));
  });

  it('shows the server validation message verbatim rather than a generic one', async () => {
    service.getRecordFields.mockResolvedValue(bag());
    service.updateRecordFields.mockRejectedValue({
      response: { status: 400, data: { error: "'Credit days' must be a whole number." } },
    });

    render(<CustomFieldValuesEditor entityType="Customer" entityId={7} canEdit />, { wrapper });
    await waitFor(() => expect(screen.getByLabelText(/Our vendor code/)).toHaveValue('TC-9910'));

    fireEvent.change(screen.getByLabelText(/Credit days/), { target: { value: '12' } });
    fireEvent.click(screen.getByRole('button', { name: /Save these fields/ }));

    await waitFor(() =>
      expect(screen.getByText("'Credit days' must be a whole number.")).toBeInTheDocument());
  });

  it('offers no save control to a user without edit permission', async () => {
    service.getRecordFields.mockResolvedValue(bag());

    render(<CustomFieldValuesEditor entityType="Customer" entityId={7} canEdit={false} />, { wrapper });

    await waitFor(() => expect(screen.getByLabelText(/Our vendor code/)).toBeDisabled());
    expect(screen.queryByRole('button', { name: /Save these fields/ })).not.toBeInTheDocument();
  });

  it('keeps the rest of the form usable when custom fields cannot be loaded', async () => {
    service.getRecordFields.mockRejectedValue(new Error('offline'));

    render(<CustomFieldValuesEditor entityType="Customer" entityId={7} canEdit />, { wrapper });

    await waitFor(() =>
      expect(screen.getByText(/Everything else on\s+this form is unaffected/)).toBeInTheDocument());
  });
});
