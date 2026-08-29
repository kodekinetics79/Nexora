import { describe, expect, it } from 'vitest';
import {
  looksLikeTechnicalNoise,
  presentableErrorMessage,
  supportDetailText,
  toPresentableError,
} from './apiErrors';

/** Builds the shape axios throws, without depending on axios itself. */
const axiosError = (options: {
  status?: number;
  data?: unknown;
  code?: string;
  message?: string;
  url?: string;
  method?: string;
  withRequest?: boolean;
}) => {
  const error: Record<string, unknown> = {
    message: options.message ?? 'Request failed with status code 500',
    code: options.code,
    config: { url: options.url ?? 'https://api.internal.nexora.test/api/Lead', method: options.method ?? 'get' },
    isAxiosError: true,
  };
  if (options.status !== undefined) {
    error.response = { status: options.status, data: options.data };
  }
  if (options.withRequest) error.request = {};
  return error;
};

describe('toPresentableError — never renders a non-string response body', () => {
  it('does not stringify an object body into the message', () => {
    const result = toPresentableError(axiosError({ status: 500, data: { detail: { nested: 'value' } } }));
    expect(result.message).not.toContain('[object Object]');
    expect(result.message).not.toContain('nested');
  });

  it('does not render an HTML error page as product copy', () => {
    const html = '<!DOCTYPE html><html><head><title>502 Bad Gateway</title></head><body><h1>502</h1></body></html>';
    const result = toPresentableError(axiosError({ status: 502, data: html }));
    expect(result.message).not.toContain('<');
    expect(result.message).not.toContain('502 Bad Gateway');
    expect(result.message).toBe(
      'This part of Nexora is not responding right now. Your data is safe — try again shortly.',
    );
    // The markup is still recoverable for support.
    expect(result.technicalDetail).toContain('body=');
  });

  it('keeps an array body out of the message', () => {
    const result = toPresentableError(axiosError({ status: 400, data: ['a', 'b'] }));
    expect(result.message).not.toContain('[object Object]');
    expect(result.message).not.toBe('a,b');
  });
});

describe('toPresentableError — never surfaces an Error/axios .message', () => {
  it('withholds the axios message and the API hostname on a 500', () => {
    const result = toPresentableError(
      axiosError({
        status: 500,
        message: 'Request failed with status code 500 at https://api.internal.nexora.test',
        url: 'https://api.internal.nexora.test/api/Lead',
      }),
    );
    expect(result.message).not.toContain('Request failed');
    expect(result.message).not.toContain('api.internal.nexora.test');
    expect(result.technicalDetail).toContain('error=Request failed');
    // The support disclosure keeps the path but strips the host.
    expect(result.technicalDetail).toContain('GET /api/Lead');
    expect(result.technicalDetail).not.toContain('api.internal.nexora.test');
  });

  it('withholds the message of a plain thrown Error', () => {
    const result = toPresentableError(new Error('Cannot read properties of undefined (reading foo)'));
    expect(result.message).not.toContain('Cannot read properties');
    expect(result.message).toBe('Something went wrong on our side. Your work is safe and nothing was lost.');
  });

  it('withholds the value of a thrown string', () => {
    const result = toPresentableError('kaboom at /var/task/index.js');
    expect(result.message).not.toContain('kaboom');
  });
});

describe('toPresentableError — demotes operator diagnostics wearing a message field', () => {
  it('does not show the ClamAV/SocketException text the owner saw in the product UI', () => {
    const result = toPresentableError(
      axiosError({ status: 503, data: { message: 'ClamAV is unavailable (SocketException)' } }),
    );
    expect(result.message).not.toContain('ClamAV');
    expect(result.message).not.toContain('SocketException');
    expect(result.technicalDetail).toContain('ClamAV is unavailable (SocketException)');
    expect(result.isRetryable).toBe(true);
  });

  it('demotes a stack trace and a host:port even on a status that allows server text', () => {
    const withStack = toPresentableError(
      axiosError({ status: 400, data: { message: 'Boom\n   at Foo.Bar(Baz.cs:31)' } }),
    );
    expect(withStack.message).not.toContain('at Foo.Bar');

    const withHostPort = toPresentableError(
      axiosError({ status: 400, data: { message: 'Could not connect to clamd:3310' } }),
    );
    expect(withHostPort.message).not.toContain('3310');
  });

  it('flags technical noise but leaves ordinary product copy alone', () => {
    expect(looksLikeTechnicalNoise('ClamAV is unavailable (SocketException)')).toBe(true);
    expect(looksLikeTechnicalNoise('Npgsql.PostgresException: duplicate key')).toBe(true);
    expect(looksLikeTechnicalNoise('ECONNREFUSED')).toBe(true);
    expect(looksLikeTechnicalNoise('This RFQ has already been converted.')).toBe(false);
    expect(looksLikeTechnicalNoise('The bid closing date must be in the future.')).toBe(false);
  });
});

describe('toPresentableError — renders genuine product copy from the server', () => {
  it('uses a clean string message on a 409', () => {
    const result = toPresentableError(
      axiosError({ status: 409, data: { message: 'This review changed. Refresh and retry.' } }),
    );
    expect(result.message).toBe('This review changed. Refresh and retry.');
    expect(result.severity).toBe('warning');
  });

  it('uses a bare string body when it reads like a sentence', () => {
    const result = toPresentableError(axiosError({ status: 422, data: 'This lead is already converted.' }));
    expect(result.message).toBe('This lead is already converted.');
  });

  it('flattens ProblemDetails validation errors', () => {
    const result = toPresentableError(
      axiosError({ status: 400, data: { errors: { rfqno: ['The RFQ number is required.'] } } }),
    );
    expect(result.message).toContain('rfqno: The RFQ number is required.');
  });

  it('never lets a 500 body become product copy even when it is a clean string', () => {
    const result = toPresentableError(axiosError({ status: 500, data: { message: 'Object reference not set' } }));
    expect(result.message).not.toContain('Object reference');
    expect(result.technicalDetail).toContain('Object reference not set');
  });
});

describe('toPresentableError — status semantics', () => {
  it.each([
    [401, false, 'Your session has ended'],
    [403, false, 'You do not have access to this'],
    [404, false, 'Not found'],
    [429, true, 'Too many requests'],
    [503, true, 'The service is temporarily unavailable'],
  ])('maps %i to retryable=%s', (status, retryable, title) => {
    const result = toPresentableError(axiosError({ status: status as number }));
    expect(result.isRetryable).toBe(retryable);
    expect(result.title).toBe(title);
    expect(result.status).toBe(status);
  });

  it('does not let a server string override auth wording on a 403', () => {
    const result = toPresentableError(axiosError({ status: 403, data: { message: 'Policy LeadsCreate denied' } }));
    expect(result.message).toBe('Your role does not permit this action. Ask an administrator if you need it.');
  });
});

describe('toPresentableError — transport failures', () => {
  it('treats a network error as retryable without a status', () => {
    const result = toPresentableError(axiosError({ code: 'ERR_NETWORK', withRequest: true }));
    expect(result.isNetworkFailure).toBe(true);
    expect(result.isRetryable).toBe(true);
    expect(result.status).toBeUndefined();
    expect(result.message).toContain('Check your connection');
  });

  it('recognises a timeout', () => {
    const result = toPresentableError(axiosError({ code: 'ECONNABORTED', withRequest: true }));
    expect(result.title).toBe('The request timed out');
    expect(result.isRetryable).toBe(true);
  });

  it('recognises a cancellation and does not treat it as an error', () => {
    const result = toPresentableError(axiosError({ code: 'ERR_CANCELED' }));
    expect(result.isCanceled).toBe(true);
    expect(result.severity).toBe('info');
  });
});

describe('toPresentableError — misc', () => {
  it('honours a caller fallback message when the server offers nothing safe', () => {
    const result = toPresentableError(axiosError({ status: 500 }), {
      fallbackMessage: 'The batch could not be loaded.',
    });
    expect(result.message).toBe('The batch could not be loaded.');
  });

  it('survives a circular payload without throwing', () => {
    const circular: Record<string, unknown> = { name: 'loop' };
    circular.self = circular;
    expect(() => toPresentableError(axiosError({ status: 500, data: circular }))).not.toThrow();
  });

  it('handles null and undefined', () => {
    expect(toPresentableError(null).message).toBeTruthy();
    expect(toPresentableError(undefined).message).toBeTruthy();
  });

  it('presentableErrorMessage returns just the sentence', () => {
    expect(presentableErrorMessage(axiosError({ status: 404 }))).toBe(
      'This record no longer exists, or it belongs to another organization.',
    );
  });

  it('supportDetailText joins title, message and detail', () => {
    const presented = toPresentableError(axiosError({ status: 500 }));
    const text = supportDetailText(presented);
    expect(text).toContain(presented.title);
    expect(text).toContain(presented.message);
    expect(text).toContain('HTTP 500');
  });
});

describe('toPresentableError — plain-string 400/409 bodies (the swallowed-reason class)', () => {
  // The exact body the RFQ create endpoint returned while the honest reason was being swallowed.
  const LEAD_REQUIRED = 'A tenant-owned lead is required so the RFQ belongs to a commercial case.';
  const GENERIC_400 = 'Some of the submitted details were not valid. Review the highlighted fields and try again.';

  it('renders a bare-string 400 body as the user-facing message', () => {
    const result = toPresentableError(
      axiosError({
        status: 400,
        data: LEAD_REQUIRED,
        method: 'post',
        url: 'https://api.internal.nexora.test/api/Rfq',
      }),
      { fallbackMessage: 'The RFQ could not be created.' },
    );
    expect(result.message).toBe(LEAD_REQUIRED);
    expect(result.severity).toBe('warning');
    expect(result.status).toBe(400);
  });

  it('renders a bare-string 409 body as the user-facing message', () => {
    const result = toPresentableError(axiosError({ status: 409, data: 'This RFQ number is already in use.' }));
    expect(result.message).toBe('This RFQ number is already in use.');
  });

  it('unwraps a JSON-encoded string body a transport left quoted', () => {
    const result = toPresentableError(axiosError({ status: 400, data: `"${LEAD_REQUIRED}"` }));
    expect(result.message).toBe(LEAD_REQUIRED);
  });

  it('collapses multi-line server text into one snackbar-safe sentence', () => {
    const result = toPresentableError(
      axiosError({ status: 400, data: 'A tenant-owned lead is required\n   so the RFQ belongs to a commercial case.' }),
    );
    expect(result.message).toBe(LEAD_REQUIRED);
  });

  it('does not render a string body carrying control characters', () => {
    const withNul = `Bad${String.fromCharCode(0)}payload`;
    const result = toPresentableError(axiosError({ status: 400, data: withNul }));
    expect(result.message).toBe(GENERIC_400);
  });

  it('does not render an unbounded string body, but keeps it for support', () => {
    const result = toPresentableError(axiosError({ status: 400, data: 'x'.repeat(400) }));
    expect(result.message).toBe(GENERIC_400);
    expect(result.technicalDetail).toContain('body=xxxx');
  });

  it('does not render a bare HTTP reason phrase as product copy', () => {
    const result = toPresentableError(axiosError({ status: 400, data: 'Bad Request' }));
    expect(result.message).toBe(GENERIC_400);
  });

  it('still never renders a bare-string 500 body', () => {
    const result = toPresentableError(axiosError({ status: 500, data: 'Unhandled failure in RfqController' }));
    expect(result.message).not.toContain('RfqController');
    expect(result.technicalDetail).toContain('Unhandled failure in RfqController');
  });
});

describe('toPresentableError — RFC 7807 ProblemDetails', () => {
  const TRACE = '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01';
  const LEAD_REQUIRED = 'A tenant-owned lead is required so the RFQ belongs to a commercial case.';
  const problem = (overrides: Record<string, unknown> = {}) => ({
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
    title: 'Bad Request',
    status: 400,
    traceId: TRACE,
    ...overrides,
  });

  it('renders `detail` as the message, never the stock title', () => {
    const result = toPresentableError(axiosError({ status: 400, data: problem({ detail: LEAD_REQUIRED }) }));
    expect(result.message).toBe(LEAD_REQUIRED);
    expect(result.message).not.toBe('Bad Request');
  });

  it('discloses the traceId even when the detail was rendered as the message', () => {
    const result = toPresentableError(axiosError({ status: 400, data: problem({ detail: LEAD_REQUIRED }) }));
    expect(result.technicalDetail).toContain(`traceId=${TRACE}`);
  });

  it('falls back to status copy when the body offers only a stock title, keeping the traceId', () => {
    const result = toPresentableError(axiosError({ status: 400, data: problem() }));
    expect(result.message).toBe(
      'Some of the submitted details were not valid. Review the highlighted fields and try again.',
    );
    expect(result.technicalDetail).toContain(`traceId=${TRACE}`);
  });

  it('demotes a technical `detail` but still discloses the body and the traceId', () => {
    const result = toPresentableError(
      axiosError({ status: 400, data: problem({ detail: 'Npgsql.PostgresException: insert violates foreign key' }) }),
    );
    expect(result.message).not.toContain('Npgsql');
    expect(result.technicalDetail).toContain('Npgsql.PostgresException');
    expect(result.technicalDetail).toContain(`traceId=${TRACE}`);
  });

  it('flattens ValidationProblemDetails and keeps the traceId', () => {
    const result = toPresentableError(
      axiosError({
        status: 400,
        data: problem({
          title: 'One or more validation errors occurred.',
          errors: { 'Rfqitems[0].Quantity': ['The Quantity field is required.'] },
        }),
      }),
    );
    expect(result.message).toContain('Rfqitems[0].Quantity: The Quantity field is required.');
    expect(result.technicalDetail).toContain(`traceId=${TRACE}`);
  });

  it('renders a 409 ProblemDetails detail', () => {
    const result = toPresentableError(
      axiosError({
        status: 409,
        data: problem({ status: 409, title: 'Conflict', detail: 'This RFQ was modified by another user. Reload before saving.' }),
      }),
    );
    expect(result.message).toBe('This RFQ was modified by another user. Reload before saving.');
  });

  it('keeps a bounded governed evidence refusal actionable instead of falling back to its title', () => {
    const detail = 'Bid revision line 9223372036854775807 cannot be committed or promoted because '
      + 'the current source lacks exact evidence for item identity/description, quantity, unit of measure. '
      + 'Record those source fields or complete a governed extraction approval for the current revision '
      + 'with actor, timestamp, reason, and before/after snapshots.';

    expect(detail.length).toBeGreaterThan(300);
    const result = toPresentableError(axiosError({
      status: 409,
      data: problem({ status: 409, title: 'Participation refused', detail }),
    }));

    expect(result.message).toBe(detail);
    expect(result.message).not.toBe('Participation refused');
  });

  it('still rejects an otherwise-safe server message above the product-copy bound', () => {
    const detail = `A governed action was refused. ${'Review the source evidence. '.repeat(25)}`;

    expect(detail.length).toBeGreaterThan(500);
    const result = toPresentableError(axiosError({
      status: 409,
      data: problem({ status: 409, title: 'Participation refused', detail }),
    }));

    expect(result.message).toBe('Participation refused');
  });
});

/*
  2026-08-12: evidence storage was repointed at a misspelled bucket and four uploads were each
  answered "upload this file again". A 503 normally means "we blinked, try again shortly", so the
  status-derived copy discarded the server's diagnosis and restored that advice at every door the
  upload page had not hand-wired.
*/
describe('toPresentableError — the document-storage refusal outranks its 503', () => {
  const refusal = (isConfigurationFault: boolean) => ({
    type: 'https://nexora.app/problems/document-storage-unavailable',
    title: 'Uploads are paused — document storage is unavailable',
    detail: isConfigurationFault
      ? 'Document storage is not configured, so uploads are paused. Retrying will not help until an administrator corrects the document storage settings.'
      : 'Document storage is unavailable, so uploads are paused. This can clear on its own — try again shortly, and tell an administrator if it persists.',
    status: 503,
    errorCode: 'evidence_storage_unavailable',
    isConfigurationFault,
  });

  it('renders the server sentence instead of the generic 503 copy', () => {
    const result = toPresentableError(axiosError({ status: 503, data: refusal(true) }));
    expect(result.message).toContain('Document storage is not configured');
    expect(result.message).not.toContain('try again shortly');
    expect(result.title).toBe('Uploads are paused — document storage is unavailable');
  });

  it('refuses to call a configuration fault retryable', () => {
    expect(toPresentableError(axiosError({ status: 503, data: refusal(true) })).isRetryable).toBe(false);
  });

  it('still allows a retry when the store is merely unreachable', () => {
    const result = toPresentableError(axiosError({ status: 503, data: refusal(false) }));
    expect(result.isRetryable).toBe(true);
    expect(result.message).toContain('can clear on its own');
  });

  it('never renders a sentence that leaked infrastructure detail', () => {
    const leaky = { ...refusal(true), detail: 'The specified bucket does not exist: NexoraB2 (AmazonS3Exception)' };
    const result = toPresentableError(axiosError({ status: 503, data: leaky }));
    expect(result.message).not.toContain('NexoraB2');
    expect(result.message).not.toContain('AmazonS3Exception');
    expect(result.message).toContain('document storage');
  });
});
