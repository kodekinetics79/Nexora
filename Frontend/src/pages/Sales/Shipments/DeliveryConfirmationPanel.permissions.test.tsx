import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import DeliveryConfirmationPanel from './DeliveryConfirmationPanel';
import type { ShipmentItemDTO } from '../../../api/services/shipmentService';

const getConfirmation = vi.fn();
const getDeliveredQuantities = vi.fn();
const confirm = vi.fn();
const captureEvidence = vi.fn();

vi.mock('../../../api/services/deliveryService', async importOriginal => {
  const actual = await importOriginal<typeof import('../../../api/services/deliveryService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getConfirmation: (shipmentId: number) => getConfirmation(shipmentId),
      getDeliveredQuantities: (orderId: number) => getDeliveredQuantities(orderId),
      confirm: (shipmentId: number, key: string, command: unknown) => confirm(shipmentId, key, command),
      captureEvidence: (shipmentId: number, kind: string, file: File) =>
        captureEvidence(shipmentId, kind, file),
    },
  };
});

function renderPanel(canEdit: boolean, items: ShipmentItemDTO[] = []) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <DeliveryConfirmationPanel
        shipmentId={70}
        orderId={80}
        deliveryStatus="DISPATCHED"
        items={items}
        canEdit={canEdit}
      />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getConfirmation.mockResolvedValue(null);
  getDeliveredQuantities.mockResolvedValue([]);
  confirm.mockResolvedValue({ id: 1 });
  captureEvidence.mockResolvedValue({ attachmentId: 91 });
});

describe('delivery mutation controls', () => {
  it('keeps delivery state visible but hides every mutation control for view-only users', async () => {
    renderPanel(false);

    expect(await screen.findByText('Dispatched')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /mark in transit/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /record proof of delivery/i })).not.toBeInTheDocument();
  });

  it('shows governed transition and proof actions to shipment editors', async () => {
    renderPanel(true);

    expect(await screen.findByRole('button', { name: /record proof of delivery/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /mark in transit/i })).toBeInTheDocument();
  });

  it('distinguishes an unavailable proof read from confirmed absence and offers retry', async () => {
    getConfirmation.mockRejectedValue({ isAxiosError: true, response: { status: 500 } });

    renderPanel(true);

    expect(await screen.findByText('Proof-of-delivery status could not be verified')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /record proof of delivery/i })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });

  it('treats only a 404 as confirmed absence and enables recording', async () => {
    getConfirmation.mockRejectedValue({ isAxiosError: true, response: { status: 404 } });

    renderPanel(true);

    expect(await screen.findByRole('button', { name: /record proof of delivery/i })).toBeInTheDocument();
    expect(screen.queryByText('Proof-of-delivery status could not be verified')).not.toBeInTheDocument();
  });

  it('names a delivery-ledger outage instead of rendering an empty ledger', async () => {
    getDeliveredQuantities.mockRejectedValue({ isAxiosError: true, response: { status: 500 } });

    renderPanel(true);

    expect(await screen.findByText(/Cumulative delivery quantities could not be loaded/i)).toBeInTheDocument();
    expect(screen.queryByText(/No cumulative delivery quantities are recorded/i)).not.toBeInTheDocument();
  });

  it('does not allow confirmation while selected evidence is still uploading', async () => {
    captureEvidence.mockImplementation(() => new Promise(() => undefined));
    renderPanel(true, [{ id: 701, orderItemId: 801, productName: 'Synthetic valve', quantity: 4 }]);

    fireEvent.click(await screen.findByRole('button', { name: /record proof of delivery/i }));
    fireEvent.change(screen.getByLabelText(/Received by \(name\)/i), { target: { value: 'Synthetic Receiver' } });
    const signature = screen.getByText('Attach signature').closest('label')?.querySelector('input');
    expect(signature).not.toBeNull();
    fireEvent.change(signature!, { target: { files: [new File(['proof'], 'proof.pdf', { type: 'application/pdf' })] } });

    await waitFor(() => expect(captureEvidence).toHaveBeenCalledTimes(1));
    expect(screen.getByRole('button', { name: 'Record proof of delivery' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
  });

  it('freezes an uncertain POD and replays the identical command and key', async () => {
    confirm.mockRejectedValueOnce(new Error('connection dropped')).mockResolvedValueOnce({ id: 1 });
    renderPanel(true, [{ id: 701, orderItemId: 801, productName: 'Synthetic valve', quantity: 4 }]);

    fireEvent.click(await screen.findByRole('button', { name: /record proof of delivery/i }));
    fireEvent.change(screen.getByLabelText(/Received by \(name\)/i), { target: { value: 'Synthetic Receiver' } });
    fireEvent.click(screen.getByRole('button', { name: 'Record proof of delivery' }));

    const retry = await screen.findByRole('button', { name: 'Retry safely' });
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Check current status' })).toBeEnabled();
    expect(screen.getByLabelText(/Received by \(name\)/i)).toBeDisabled();
    fireEvent.click(retry);

    await waitFor(() => expect(confirm).toHaveBeenCalledTimes(2));
    expect(confirm.mock.calls[1]).toEqual(confirm.mock.calls[0]);
  });
});
