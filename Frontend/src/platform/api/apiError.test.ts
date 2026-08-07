import { AxiosError, AxiosHeaders } from 'axios';
import { describe, expect, it } from 'vitest';
import { platformErrorMessage } from './apiError';

/**
 * A refusal the operator cannot read is a refusal they cannot act on.
 *
 * The console showed "Provisioning was not accepted" for a rejected field, for a 404 against an
 * API that had not deployed yet, and for a connection that never left the browser. Three
 * different next actions, one sentence.
 */

const FALLBACK = 'Provisioning was not accepted';

const responseError = (status: number, data: unknown): AxiosError => {
  const error = new AxiosError('Request failed', 'ERR_BAD_REQUEST');
  error.response = {
    status, data, statusText: '', headers: new AxiosHeaders(), config: { headers: new AxiosHeaders() },
  };
  return error;
};

describe('platformErrorMessage', () => {
  it('shows the curated guard-rail message verbatim', () => {
    expect(platformErrorMessage(responseError(400, { error: 'A Billable tenant must have a plan.' }), FALLBACK))
      .toBe('A Billable tenant must have a plan.');
  });

  it('names the field when the server rejected one, instead of hiding it', () => {
    // ValidationProblemDetails — what `[ApiController]` returns for every DataAnnotations
    // failure in the product, and the shape the old extractor could not read at all.
    const message = platformErrorMessage(
      responseError(400, {
        type: 'https://tools.ietf.org/html/rfc7231#section-6.5.1',
        title: 'One or more validation errors occurred.',
        status: 400,
        traceId: '00-abc-def-01',
        errors: { 'Tenant.BaseCurrencyCode': ['The field must be a string with a minimum length of 3.'] },
      }),
      FALLBACK,
    );

    expect(message).toContain('Base currency code');
    expect(message).toContain('minimum length of 3');
    // The envelope's own fields are not field names and must never be rendered as one.
    expect(message).not.toContain('traceId');
    expect(message).not.toContain('rfc7231');
  });

  it('reads a bare ModelState dictionary, which has no errors wrapper', () => {
    const message = platformErrorMessage(
      responseError(400, { AdminEmail: ['The AdminEmail field is not a valid e-mail address.'] }),
      FALLBACK,
    );
    expect(message).toContain('Admin email');
    expect(message).toContain('not a valid e-mail address');
  });

  it('reports several rejected fields rather than only the first', () => {
    const message = platformErrorMessage(
      responseError(400, { errors: { CountryCode: ['Too short.'], AdminEmail: ['Required.'] } }),
      FALLBACK,
    );
    expect(message).toContain('Country code');
    expect(message).toContain('Admin email');
  });

  it('calls a missing endpoint a deployment problem, not a bad form', () => {
    // The console and the API deploy separately. Everything else the operator could try —
    // retyping, re-picking a plan — is wasted effort against a backend that is simply older.
    const message = platformErrorMessage(responseError(404, ''), FALLBACK);
    expect(message).toMatch(/API is likely older than this console/i);
  });

  it('distinguishes a request that never reached the server', () => {
    const error = new AxiosError('Network Error', 'ERR_NETWORK');   // no response at all
    expect(platformErrorMessage(error, FALLBACK)).toMatch(/could not reach the server/i);

    const timeout = new AxiosError('timeout', 'ECONNABORTED');
    // A timeout is NOT a refusal: the write may well have landed, so "retry" is bad advice.
    expect(platformErrorMessage(timeout, FALLBACK)).toMatch(/may still have accepted the request/i);
  });

  it('prefers a curated message over a field list when the server sends both', () => {
    expect(platformErrorMessage(
      responseError(400, { error: 'Slug is reserved.', errors: { Slug: ['Bad.'] } }),
      FALLBACK,
    )).toBe('Slug is reserved.');
  });

  it('never renders an HTML error page as though it were a message', () => {
    const message = platformErrorMessage(responseError(502, '<!DOCTYPE html><html>Bad Gateway</html>'), FALLBACK);
    expect(message).not.toContain('DOCTYPE');
    expect(message).toMatch(/unavailable or still starting up/i);
  });

  it('falls back for a non-HTTP error', () => {
    expect(platformErrorMessage(new Error('boom'), FALLBACK)).toBe(FALLBACK);
  });
});
