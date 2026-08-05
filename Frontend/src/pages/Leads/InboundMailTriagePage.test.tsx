import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import InboundMailTriagePage from './InboundMailTriagePage';
import { readTriagePage, readTriageRow } from '../../api/services/emailTriageService';

/**
 * What is locked down here is the reason the screen exists:
 *  - the landing view is the REJECTIONS, because that is the only list that can hide a lost deal;
 *  - the machine's reasoning is shown as human wording, never as raw snake_case;
 *  - a rejected message can always be put back, and only with a reason;
 *  - the prose that arrived IS the evidence, so it is rendered next to what came out of it;
 *  - a field the backend has not shipped reads as an absence, never as 0 or a blank cell.
 */

const listTriage = vi.fn();
const reprocess = vi.fn();

vi.mock('../../api/services/emailTriageService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/emailTriageService')>();
  return {
    ...actual,
    default: {
      listTriage: (params: unknown) => listTriage(params),
      reprocess: (id: number, reason: string) => reprocess(id, reason),
    },
  };
});

const hasPermission = vi.fn();

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: (module: string, action?: string) => hasPermission(module, action) }),
}));

const navigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

/** An out-of-office that swallowed a real enquiry thread — the case the owner cares about. */
const NOISE_PAGE = readTriagePage(
  {
    items: [
      {
        id: 41,
        receivedOn: '2026-08-03T06:12:00Z',
        from: 'ops@alfuttaim-contracting.ae',
        subject: 'Automatic reply: Cable tray enquiry',
        outcome: 'Noise',
        reasonCodes: ['auto_submitted_header'],
        hasAttachments: false,
        linkedBatchId: null,
        bodyPreview: 'I am out of the office until 12 August. For urgent matters contact procurement@…',
        bodySubmitted: false,
        attachmentCount: 0,
        extractedItemCount: 0,
      },
      {
        // Deliberately sparse: a deployment that has not shipped the enrichment fields yet.
        id: 42,
        outcome: 'Noise',
        reasonCodes: ['noreply_sender', 'bulk_list_header'],
      },
    ],
    totalCount: 2,
    pageNumber: 1,
    pageSize: 25,
  },
  1,
);

const INQUIRY_PAGE = readTriagePage(
  {
    items: [
      {
        id: 77,
        receivedOn: '2026-08-04T05:40:00Z',
        from: 'buyer@gulfmep.ae',
        subject: 'Requirement — Jebel Ali',
        outcome: 'Inquiry',
        reasonCodes: ['qty_uom_pattern', 'request_verb'],
        hasAttachments: true,
        attachmentNames: ['drawing-rev-b.pdf'],
        attachmentCount: 1,
        linkedBatchId: 'batch-9001',
        leadId: 5150,
        bodyPreview:
          'Hi, please quote 40 nos cable tray 300mm and 12 nos junction box IP65, delivery to Jebel Ali by 20th',
        bodySubmitted: true,
        extractedItemCount: 2,
      },
    ],
    totalCount: 1,
    pageNumber: 1,
    pageSize: 25,
  },
  1,
);

const renderPage = () => {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <InboundMailTriagePage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
};

const rowFor = async (subject: RegExp): Promise<HTMLElement> => {
  const cell = await screen.findByText(subject);
  const row = cell.closest('tr');
  if (!row) throw new Error('row not found');
  return row;
};

beforeEach(() => {
  vi.clearAllMocks();
  hasPermission.mockReturnValue(true);
  listTriage.mockResolvedValue(NOISE_PAGE);
  reprocess.mockResolvedValue({ id: 41, status: 'Queued', batchId: 'batch-42', replayed: false });
});

describe('InboundMailTriagePage', () => {
  it('landsOnRejections_soALostDealIsTheFirstThingVisible', async () => {
    renderPage();
    await waitFor(() => expect(listTriage).toHaveBeenCalled());
    expect(listTriage).toHaveBeenCalledWith(expect.objectContaining({ outcome: 'Noise' }));
    expect(screen.getByRole('tab', { name: /rejected as noise/i })).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByText(/Automatic reply: Cable tray enquiry/)).toBeInTheDocument();
  });

  it('rendersReasonCodesAsHumanWording_notRawCodes', async () => {
    renderPage();
    const row = await rowFor(/Automatic reply: Cable tray enquiry/);
    expect(within(row).getByText('Auto-submitted')).toBeInTheDocument();
    expect(within(row).queryByText('auto_submitted_header')).not.toBeInTheDocument();
  });

  it('rendersAbsentFieldsAsNotReported_neverAsBlankOrZero', async () => {
    renderPage();
    const sparse = await rowFor(/\(no subject\)/);
    // Row 42 carries neither sender nor received date; both must say so out loud.
    expect(within(sparse).getAllByText('Not reported').length).toBeGreaterThanOrEqual(2);
  });

  it('showsTheOriginalProseBesideWhatCameOutOfIt', async () => {
    listTriage.mockResolvedValue(INQUIRY_PAGE);
    renderPage();
    const row = await rowFor(/Requirement — Jebel Ali/);
    fireEvent.click(within(row).getByRole('button', { name: /show message/i }));

    const detail = await screen.findByRole('region', { name: /message and extraction/i });
    expect(within(detail).getByText(/please quote 40 nos cable tray 300mm/)).toBeInTheDocument();
    expect(within(detail).getByText(/Line items extracted from this message: 2/)).toBeInTheDocument();
  });

  it('statesTheBodyVersusAttachmentSplit', async () => {
    listTriage.mockResolvedValue(INQUIRY_PAGE);
    renderPage();
    const row = await rowFor(/Requirement — Jebel Ali/);
    fireEvent.click(within(row).getByRole('button', { name: /show message/i }));

    const detail = await screen.findByRole('region', { name: /message and extraction/i });
    expect(
      within(detail).getByText(
        /The message text was submitted for extraction\. 1 attachment came with this message and is extracted separately/,
      ),
    ).toBeInTheDocument();
    expect(within(detail).getByText('drawing-rev-b.pdf')).toBeInTheDocument();
  });

  it('saysSoWhenTheMessageTextIsNotExposedYet', async () => {
    renderPage();
    const sparse = await rowFor(/\(no subject\)/);
    fireEvent.click(within(sparse).getByRole('button', { name: /show message/i }));

    const detail = await screen.findByRole('region', { name: /message and extraction/i });
    expect(within(detail).getByText(/message text is not exposed by this deployment yet/i)).toBeInTheDocument();
  });

  it('refusesToOverturnADecisionWithoutAReason', async () => {
    renderPage();
    const row = await rowFor(/Automatic reply: Cable tray enquiry/);
    fireEvent.click(within(row).getByRole('button', { name: /reprocess as inquiry/i }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: /^reprocess as inquiry$/i }));

    expect(reprocess).not.toHaveBeenCalled();
    expect(await within(dialog).findByText(/give a reason/i)).toBeInTheDocument();
  });

  it('sendsTheMessageBackWithTheReason', async () => {
    renderPage();
    const row = await rowFor(/Automatic reply: Cable tray enquiry/);
    fireEvent.click(within(row).getByRole('button', { name: /reprocess as inquiry/i }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/why is this an inquiry/i), {
      target: { value: 'Real enquiry hidden behind an auto-reply.' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: /^reprocess as inquiry$/i }));

    await waitFor(() => expect(reprocess).toHaveBeenCalledWith(41, 'Real enquiry hidden behind an auto-reply.'));
    expect(await screen.findByText(/was sent back through extraction as an inquiry/i)).toBeInTheDocument();
  });

  it('hidesTheOverrideWhenTheRoleCannotUseIt', async () => {
    hasPermission.mockReturnValue(false);
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);
    expect(screen.queryByRole('button', { name: /reprocess as inquiry/i })).not.toBeInTheDocument();
    expect(screen.getByText(/needs the Leads permission/i)).toBeInTheDocument();
  });

  it('queriesTheSelectedOutcomeWhenTheTabChanges', async () => {
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);
    listTriage.mockResolvedValue(INQUIRY_PAGE);

    fireEvent.click(screen.getByRole('tab', { name: /^extracted$/i }));

    await waitFor(() => expect(listTriage).toHaveBeenCalledWith(expect.objectContaining({ outcome: 'Inquiry' })));
    expect(await screen.findByText(/Requirement — Jebel Ali/)).toBeInTheDocument();
  });

  it('explainsAMissingBackendInsteadOfShowingAFailure', async () => {
    listTriage.mockRejectedValue({ response: { status: 404 }, isAxiosError: true });
    renderPage();
    expect(await screen.findByText(/not available in this deployment yet/i)).toBeInTheDocument();
    expect(screen.getByText(/no message has been hidden/i)).toBeInTheDocument();
  });

  it('surfacesARealFailureWithARetry', async () => {
    listTriage.mockRejectedValue({ response: { status: 500, data: {} }, isAxiosError: true });
    renderPage();
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /try again/i })).toBeInTheDocument();
  });
});

describe('readTriageRow', () => {
  it('keepsAbsenceDistinctFromFalseAndZero', () => {
    const row = readTriageRow({ id: 9, outcome: 'Uncertain' });
    expect(row.hasAttachments).toBeNull();
    expect(row.attachmentCount).toBeNull();
    expect(row.extractedItemCount).toBeNull();
    expect(row.attachmentNamesReported).toBe(false);
    expect(row.reasonCodes).toEqual([]);
  });

  it('derivesAttachmentPresenceFromNamesWhenTheFlagIsAbsent', () => {
    const row = readTriageRow({ id: 9, outcome: 'Inquiry', attachmentNames: ['boq.xlsx', ' '] });
    expect(row.attachmentNames).toEqual(['boq.xlsx']);
    expect(row.attachmentCount).toBe(1);
    expect(row.hasAttachments).toBe(true);
    expect(row.attachmentNamesReported).toBe(true);
  });

  it('readsABareArrayResponseAsOnePage', () => {
    const page = readTriagePage([{ id: 1, outcome: 'Noise' }], 3);
    expect(page.items).toHaveLength(1);
    expect(page.pageNumber).toBe(3);
    expect(page.pageSize).toBeNull();
  });
});
