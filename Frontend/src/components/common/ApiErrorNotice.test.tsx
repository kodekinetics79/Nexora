import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import ApiErrorNotice from './ApiErrorNotice';

const serverError = (status: number, data: unknown) => ({
  message: 'Request failed with status code 500',
  isAxiosError: true,
  config: { url: 'https://api.internal.nexora.test/api/LeadIngestion/batches/abc', method: 'get' },
  response: { status, data },
});

describe('ApiErrorNotice', () => {
  it('renders safe copy and keeps backend diagnostics out of the visible headline', () => {
    render(<ApiErrorNotice error={serverError(503, { message: 'ClamAV is unavailable (SocketException)' })} />);

    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByText('The service is temporarily unavailable')).toBeInTheDocument();
    expect(screen.queryByText(/SocketException/)).not.toBeInTheDocument();
    expect(screen.queryByText(/ClamAV/)).not.toBeInTheDocument();
  });

  it('offers the support disclosure collapsed by default', () => {
    render(<ApiErrorNotice error={serverError(500, { detail: { nested: 'value' } })} />);

    const toggle = screen.getByRole('button', { name: /Technical detail \(for support\)/i });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('does not render an object body as [object Object]', () => {
    render(<ApiErrorNotice error={serverError(500, { detail: { nested: 'value' } })} />);
    expect(screen.queryByText(/\[object Object\]/)).not.toBeInTheDocument();
  });

  it('uses the caller fallback message when the server offers nothing safe', () => {
    render(<ApiErrorNotice error={serverError(500, {})} fallbackMessage="The batch could not be loaded." />);
    expect(screen.getByText('The batch could not be loaded.')).toBeInTheDocument();
  });
});

describe('ApiErrorNotice — a 404 on a list', () => {
  // What the Inbox rendered, six times, when the needs-review route answered 404 with its own
  // request line as the body: a red panel titled "Not found" whose sentence was "GET
  // /api/Lead/needs-review". A rep reads that as "your work is not found" plus a code.
  const listNotFound = {
    message: 'Request failed with status code 404',
    isAxiosError: true,
    config: { url: 'https://api.internal.nexora.test/api/Lead/needs-review', method: 'get' },
    response: { status: 404, data: 'GET /api/Lead/needs-review' },
  };

  it('says the list could not be loaded and keeps the path in the support disclosure', () => {
    render(<ApiErrorNotice error={listNotFound} context="list" onRetry={() => {}} />);

    expect(screen.getByText('This list could not be loaded')).toBeInTheDocument();
    expect(screen.queryByText('Not found')).not.toBeInTheDocument();
    // The path is not in the visible copy: only inside the collapsed disclosure, unmounted until opened.
    expect(screen.queryByText(/\/api\/Lead\/needs-review/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /technical detail/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /try again/i })).toBeInTheDocument();
  });

  it('never shows an API path as the headline even without list context', () => {
    render(<ApiErrorNotice error={listNotFound} />);
    expect(screen.queryByText(/\/api\//)).not.toBeInTheDocument();
  });
});
