import { beforeEach, describe, expect, it, vi } from 'vitest';
import axiosInstance from '../axiosInstance';
import quoteService, { describeQuoteSendOutcome } from './quoteService';

/**
 * "Queued" is not "emailed".
 *
 * `POST /api/Quote/{id}/email` answers 202 with `{ queuedForDelivery, delivered, replayed }`. The
 * normal answer is `queuedForDelivery: true, delivered: false`: a delivery row was written and
 * `QuoteDeliveryWorker` sends it later — or refuses it later, with nobody watching, and the fixed
 * delivery key (`quote:{id}:delivery:v1`) then makes the quote number permanently unsendable.
 *
 * `sendEmail` used to discard that body and return `{ held: false }`, and both quote screens
 * turned that into a green "Quote emailed to the customer". These tests pin that the service
 * carries the server's distinction through, and that the shared wording respects it.
 */

vi.mock('../axiosInstance', () => ({
  default: {
    post: vi.fn(),
  },
}));

const post = vi.mocked(axiosInstance.post);

beforeEach(() => {
  post.mockReset();
});

describe('quoteService.sendEmail', () => {
  it('reports a queued delivery as queued, not delivered', async () => {
    post.mockResolvedValue({
      status: 202,
      data: { queuedForDelivery: true, delivered: false, replayed: false, message: 'Quote delivery queued.' },
    });

    const result = await quoteService.sendEmail(66, 'buyer@customer.test');

    expect(result.held).toBe(false);
    expect(result.queuedForDelivery).toBe(true);
    expect(result.delivered).toBe(false);
  });

  it('reports a completed delivery as delivered', async () => {
    post.mockResolvedValue({
      status: 202,
      data: { queuedForDelivery: false, delivered: true, replayed: true, message: 'Quote delivery was already completed.' },
    });

    const result = await quoteService.sendEmail(66, 'buyer@customer.test');

    expect(result.delivered).toBe(true);
    expect(result.queuedForDelivery).toBe(false);
  });
});

describe('describeQuoteSendOutcome', () => {
  it('never says "emailed" for a send that is only queued', () => {
    const outcome = describeQuoteSendOutcome({ queuedForDelivery: true, delivered: false });

    expect(outcome.delivered).toBe(false);
    expect(outcome.message).toMatch(/queued/i);
    expect(outcome.message).not.toMatch(/emailed/i);
  });

  it('says "emailed" only when the server confirmed delivery', () => {
    const outcome = describeQuoteSendOutcome({ queuedForDelivery: false, delivered: true });

    expect(outcome.delivered).toBe(true);
    expect(outcome.message).toMatch(/emailed to the customer/i);
  });
});
