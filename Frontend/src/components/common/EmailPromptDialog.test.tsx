import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The dialog must not collect what its caller cannot deliver.
 *
 * It is shared by two flows. The RFQ-approval callers (DraftRFQsPage, ViewRFQPage) destructure
 * `(email, subject, body, customerId)` and send all four. The quote-send callers pass only the
 * recipient, because POST /api/Quote/{id}/email takes `recipientEmail` and nothing else — yet the
 * dialog still rendered "Email Subject" and a six-row "Email Message". A rep sending to a named
 * buyer typed a covering note referencing the tender number, clicked the green button, and the
 * note was dropped on the floor with no warning: silent data loss on the one action that touches
 * the customer.
 *
 * The button also read "Confirm & Approve" — an internal approval verb on a customer send — and
 * the helper text said "This RFQ will be linked to the selected customer" on a quote screen.
 */

const { getAll } = vi.hoisted(() => ({ getAll: vi.fn() }));
vi.mock('../../api/services/customerService', () => ({ default: { getAll } }));

import EmailPromptDialog from './EmailPromptDialog';

const onConfirm = vi.fn();

beforeEach(() => {
  vi.clearAllMocks();
  getAll.mockResolvedValue({ items: [] });
});

const renderDialog = (props: Record<string, unknown> = {}) =>
  render(
    <EmailPromptDialog
      open
      businessUnitId={1}
      initialEmail="buyer@aramco.com"
      onCancel={() => {}}
      onConfirm={onConfirm}
      {...props}
    />,
  );

describe('a caller that can only deliver a recipient', () => {
  it('does not offer a subject or message box', () => {
    renderDialog({ composerFields: 'recipient-only' });

    expect(screen.getByLabelText(/Recipient Email/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/Email Subject/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/Email Message/i)).not.toBeInTheDocument();
  });

  it('does not talk about linking an RFQ on a quote send', () => {
    renderDialog({ composerFields: 'recipient-only' });
    expect(screen.queryByText(/This RFQ will be linked/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Linking a customer is recommended/i)).not.toBeInTheDocument();
  });

  it('hands back only the recipient, never a silently-dropped payload', () => {
    renderDialog({ composerFields: 'recipient-only', confirmLabel: 'Send quote' });
    fireEvent.click(screen.getByRole('button', { name: /send quote/i }));

    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onConfirm.mock.calls[0]).toEqual(['buyer@aramco.com']);
  });

  it('names the action as a send rather than an approval', () => {
    renderDialog({ composerFields: 'recipient-only', confirmLabel: 'Send quote' });
    expect(screen.getByRole('button', { name: /send quote/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /confirm & approve/i })).not.toBeInTheDocument();
  });
});

describe('the RFQ approval callers, which do deliver all four fields', () => {
  it('still offers the full composer by default', () => {
    renderDialog();

    expect(screen.getByLabelText(/Email Subject/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/Email Message/i)).toBeInTheDocument();
  });

  it('still hands back subject, body and customer', () => {
    renderDialog();
    fireEvent.change(screen.getByLabelText(/Email Subject/i), {
      target: { value: 'RFQ 812 — approval' },
    });
    fireEvent.change(screen.getByLabelText(/Email Message/i), {
      target: { value: 'Please review.' },
    });
    fireEvent.click(screen.getByRole('button', { name: /confirm & approve/i }));

    expect(onConfirm).toHaveBeenCalledWith(
      'buyer@aramco.com', 'RFQ 812 — approval', 'Please review.', undefined,
    );
  });
});
