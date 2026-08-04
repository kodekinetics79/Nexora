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
