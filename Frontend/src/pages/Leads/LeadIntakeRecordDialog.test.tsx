import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AxiosError, AxiosHeaders } from 'axios';
import LeadIntakeRecordDialog, { fileFateSentence, intakeOutcomeSentence } from './LeadIntakeRecordDialog';

const getByLead = vi.fn();
vi.mock('../../api/services/intakeRecordService', () => ({
  default: { getByLead: (...a: unknown[]) => getByLead(...a) },
}));

const record = {
  sourceEmail: {
    emailIngestId: 900, mailbox: 'sales@nexora.test', messageId: '<a@b>',
    receivedOn: '2026-08-20T09:00:00Z', rawEmailAvailable: false, parseStatus: 'Success',
  },
  classification: { triageOutcome: 'Inquiry', triageReasonCodes: [], processingPath: 'Structured', externalAiUsed: false },
  message: { from: 'buyer@acme.test', to: 'sales@nexora.test', subject: 'RFQ 8891 — valves', sentOn: null },
  inventory: [
    { kind: 'Body', disposition: 'Enqueued', fileName: 'body.txt' },
    { kind: 'Attachment', disposition: 'Enqueued', fileName: 'boq.xlsx', resultLeadId: 77 },
    { kind: 'Attachment', disposition: 'Skipped', fileName: 'signature.png', skippedReason: 'Image smaller than the attachment threshold' },
    { kind: 'Attachment', disposition: 'Enqueued', fileName: 'drawing.pdf', jobLastError: 'The file is password protected' },
  ],
  otherLeadIds: [78],
  finalStatus: 'CompletedWithFailures',
};

const axiosErrorWith = (status: number) => new AxiosError(
  'refused', 'ERR', undefined,
  {},
  { status, data: {}, statusText: '', headers: new AxiosHeaders(), config: { headers: new AxiosHeaders() } },
);

function renderDialog() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <LeadIntakeRecordDialog leadId={77} open onClose={() => {}} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getByLead.mockResolvedValue(record);
});

/**
 * `IntakeRecordsController` had ZERO frontend callers, so "what did we actually receive?" — the
 * first question asked when a quantity looks wrong — could only be answered from a database
 * console.
 */
describe('What we received', () => {
  it('names every file that arrived and what became of it', async () => {
    renderDialog();

    expect(await screen.findByText('boq.xlsx')).toBeInTheDocument();
    // The file the intake door DROPPED is the one nothing else in the product mentions.
    expect(screen.getByText('signature.png')).toBeInTheDocument();
    expect(screen.getByText(/not read — image smaller than the attachment threshold/i)).toBeInTheDocument();
    expect(screen.getByText(/could not be read — the file is password protected/i)).toBeInTheDocument();
    // The message body is not a file the sender attached, so it is not counted as one.
    expect(screen.getByText(/files that arrived \(3\)/i)).toBeInTheDocument();
    expect(screen.queryByText('body.txt')).not.toBeInTheDocument();
  });

  it('warns in plain words that something was lost, and that the original is gone', async () => {
    renderDialog();

    expect(await screen.findByText(/at least one file could not be read/i)).toBeInTheDocument();
    expect(screen.getByText(/the original email is no longer stored/i)).toBeInTheDocument();
    // No pipeline vocabulary reaches the screen.
    expect(screen.queryByText(/CompletedWithFailures/)).not.toBeInTheDocument();
  });

  it('says one message produced several inquiries', async () => {
    renderDialog();
    expect(await screen.findByText(/this one message produced 2 inquiries/i)).toBeInTheDocument();
  });

  it('presents a tenant without email intake as a plan fact, not an outage', async () => {
    getByLead.mockRejectedValue(axiosErrorWith(403));
    renderDialog();

    expect(await screen.findByText(/not switched on for your company/i)).toBeInTheDocument();
    expect(screen.queryByText(/something went wrong/i)).not.toBeInTheDocument();
  });

  it('says an uploaded inquiry has no received message, rather than showing an error', async () => {
    getByLead.mockRejectedValue(axiosErrorWith(404));
    renderDialog();

    expect(await screen.findByText(/did not arrive by email/i)).toBeInTheDocument();
  });

  it('does show a real failure as a failure', async () => {
    getByLead.mockRejectedValue(axiosErrorWith(500));
    renderDialog();

    expect(await screen.findByText(/we couldn.t load what was received/i)).toBeInTheDocument();
  });
});

describe('plain-English rendering', () => {
  it('never leaves a raw pipeline status as the whole sentence', () => {
    expect(intakeOutcomeSentence('DeadLettered')).toMatch(/could not be read/i);
    expect(intakeOutcomeSentence('DeadLettered')).not.toContain('DeadLettered');
    expect(intakeOutcomeSentence('SomethingNew')).toMatch(/no recorded outcome/i);
  });

  it('does not claim a reason it does not have', () => {
    expect(fileFateSentence({ kind: 'Attachment', disposition: 'Skipped', fileName: 'x.zip' }))
      .toMatch(/recorded no reason/i);
  });
});
