import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import type { GridColDef } from '@mui/x-data-grid';
import LeadsPage from './LeadsPage';

// This file drives a full DataGrid page through jsdom; 5s is not enough on a cold machine.
vi.setConfig({ testTimeout: 30_000 });

/**
 * Assigning a lead has to be doable from the FIRST leads screen.
 *
 * Before this, `/procurement/leads/all` — the screen a rep lives on — could not say who owned a
 * row, let alone change it. Assigning one lead meant: open the lead, open the Owner control, open
 * "Assign to…", pick a name, navigate back. Four clicks and two full page loads, per lead.
 *
 * These tests assert what a person SEES and how many clicks it costs them, because that is the
 * claim being made. `clicks()` counts real `fireEvent.click` calls through one journey.
 */

const getAll = vi.fn();
const getOwnerOptions = vi.fn();
const changeLeadOwner = vi.fn();

vi.mock('../../api/services/leadService', () => ({
  default: {
    getAll: (params: unknown) => getAll(params),
    fetchEmails: vi.fn(),
  },
}));

vi.mock('../../api/services/decisionService', () => ({
  default: { getDecisionSummaries: vi.fn().mockResolvedValue({ summaries: {} }) },
}));

vi.mock('../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/commercialRoutingService')>();
  return {
    ...actual,
    default: {
      getOwnerOptions: () => getOwnerOptions(),
      changeLeadOwner: (leadId: number, body: unknown) => changeLeadOwner(leadId, body),
      getLeadAssignmentHistory: vi.fn().mockResolvedValue([]),
    },
  };
});

/** Captures the definitions the page hands to the per-user column preference layer. */
const arrangedColumns: string[][] = [];
vi.mock('../../hooks/useColumnPreferences', () => ({
  default: () => ({
    columnVisibilityModel: {},
    onColumnVisibilityModelChange: vi.fn(),
    arrangeColumns: (defs: GridColDef[]) => {
      arrangedColumns.push(defs.map((d) => d.field));
      return defs;
    },
    isLoading: false,
    isError: false,
  }),
}));

vi.mock('../../components/common/ColumnPreferences', () => ({ default: () => null }));

const authUser: { id?: number; roleName?: string; isManager?: boolean } = {};
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true, userData: authUser }),
}));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => vi.fn() };
});

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const ME = 2;
const COLLEAGUE = 77;

const UNOWNED = {
  id: 101,
  nexoraSerial: 'NOOR-SONS-LLC-2026-000101',
  rfqno: 'RFQ-101',
  buyersName: 'Aramco',
  clientemail: 'buyer@aramco.test',
  leadSource: 'Email',
  recDate: '2026-08-01T00:00:00Z',
  bidClosingDate: '2026-09-01T00:00:00Z',
  customerMatchStatus: 'UNRESOLVED',
  itemCount: 4,
  assignedToId: null,
  assignedToFullName: null,
  assignmentMethod: 'AUTOMATIC',
  assignmentVersion: 3,
  isAccepted: false,
  isRejected: false,
};

const OWNED_BY_COLLEAGUE = {
  ...UNOWNED,
  id: 102,
  nexoraSerial: 'NOOR-SONS-LLC-2026-000102',
  rfqno: 'RFQ-102',
  assignedToId: COLLEAGUE,
  assignedToFullName: 'Tariq Al-Harbi',
  assignmentMethod: 'MANUAL',
  assignmentVersion: 5,
};

const SECOND_UNOWNED = {
  ...UNOWNED,
  id: 103,
  nexoraSerial: 'NOOR-SONS-LLC-2026-000103',
  rfqno: 'RFQ-103',
  assignmentVersion: 1,
};

const OWNERS = [
  { userId: ME, name: 'Sara Bin Ali', email: 'sara@nexora.test', roleName: 'Sales Rep', isAvailable: true, capacityPercent: 40, eligibilityReason: '' },
  { userId: COLLEAGUE, name: 'Tariq Al-Harbi', email: 'tariq@nexora.test', roleName: 'Sales Rep', isAvailable: true, capacityPercent: 60, eligibilityReason: '' },
  { userId: 9, name: 'Noura Idle', email: 'noura@nexora.test', roleName: 'Sales Rep', isAvailable: false, capacityPercent: 100, eligibilityReason: 'At 100% capacity — no room for another lead.' },
];

const page = (items: unknown[]) => ({ items, totalCount: items.length, pageNumber: 1, pageSize: 10 });

const renderPage = (route = '/procurement/leads/all') => {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <MemoryRouter initialEntries={[route]}>
      <SnackbarProvider>
        <QueryClientProvider client={client}>
          <LeadsPage />
        </QueryClientProvider>
      </SnackbarProvider>
    </MemoryRouter>,
  );
};

/** Counts the clicks a journey costs. A UX claim without a number is an opinion. */
let clickCount = 0;
const click = (element: Element | null) => {
  if (!element) throw new Error('Nothing to click.');
  clickCount += 1;
  fireEvent.click(element);
};

/** Handing a lead to a COLLEAGUE is a manager's action; the server answers 403 otherwise. */
const asManager = () => {
  authUser.isManager = true;
  authUser.roleName = 'Sales Manager';
};

/**
 * The view the GRID last asked for. The page also reads a pageSize-1 unfiltered total to tell a
 * truly empty tenant from a filtered-to-zero list; that read is not the grid's and is skipped.
 */
const lastListView = (): unknown =>
  (getAll.mock.calls
    .map((call) => call[0] as { view?: unknown; pageSize?: number })
    .filter((params) => params.pageSize !== 1)
    .at(-1))?.view;

describe('LeadsPage — assigning a lead from the list', () => {
  beforeAll(() => {
    // The DataGrid measures its viewport; jsdom ships neither observer.
    class NoopObserver {
      observe() {}
      unobserve() {}
      disconnect() {}
    }
    (globalThis as Record<string, unknown>).ResizeObserver ??= NoopObserver;
    (globalThis as Record<string, unknown>).IntersectionObserver ??= NoopObserver;
    if (typeof globalThis.crypto?.randomUUID !== 'function') {
      Object.defineProperty(globalThis.crypto ?? (globalThis.crypto = {} as Crypto), 'randomUUID', {
        configurable: true,
        value: () => '00000000-0000-4000-8000-000000000000',
      });
    }
  });

  beforeEach(() => {
    vi.clearAllMocks();
    arrangedColumns.length = 0;
    clickCount = 0;
    authUser.id = ME;
    authUser.roleName = 'Sales Rep';
    authUser.isManager = false;
    getAll.mockResolvedValue(page([UNOWNED, OWNED_BY_COLLEAGUE]));
    getOwnerOptions.mockResolvedValue(OWNERS);
    changeLeadOwner.mockResolvedValue({ leadId: 101, assignmentVersion: 4 });
  });

  // -------------------------------------------------------------------------
  // R1 — the column is the control
  // -------------------------------------------------------------------------

  it('showsWhoOwnsEveryRow_withoutOpeningTheLead', async () => {
    renderPage();

    expect(await screen.findByRole('columnheader', { name: /owner/i })).toBeInTheDocument();
    // The owner's name is on the wire for every row and used to be rendered by nothing.
    expect(await screen.findByText('Tariq Al-Harbi')).toBeInTheDocument();
  });

  it('offersTheOwnerColumnToThePerUserColumnPreferences', async () => {
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    // The column goes through arrangeColumns like every other one, so a reader who does not
    // want it can switch it off in the same place they switch off the others.
    expect(arrangedColumns.at(-1)).toContain('assignee');
  });

  it('takesAnUnownedLeadInOneClick', async () => {
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    click(await screen.findByRole('button', { name: /assign to me/i }));

    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledTimes(1));
    expect(clickCount).toBe(1);

    const [leadId, body] = changeLeadOwner.mock.calls[0] as [number, Record<string, unknown>];
    expect(leadId).toBe(101);
    expect(body.assignedToUserId).toBe(ME);
    // The optimistic-concurrency token comes off the row, which is why the list projection had
    // to start carrying it.
    expect(body.expectedAssignmentVersion).toBe(3);
    // Taking your own unowned work is never interrogated.
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(await screen.findByText(/assigned to sara bin ali/i)).toBeInTheDocument();
  });

  it('assignsToSomebodyElseInTwoClicks_withoutLeavingTheList', async () => {
    asManager();
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    click(screen.getByRole('button', { name: /someone else/i }));
    click(await screen.findByRole('menuitem', { name: /tariq al-harbi/i }));

    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledTimes(1));
    expect(clickCount).toBe(2);
    expect((changeLeadOwner.mock.calls[0][1] as Record<string, unknown>).assignedToUserId).toBe(COLLEAGUE);
  });

  it('printsWhyANameCannotTakeALead_ratherThanGreyingItOutSilently', async () => {
    asManager();
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    click(screen.getByRole('button', { name: /someone else/i }));

    const menu = await screen.findByRole('menu', { name: /eligible lead owners/i });
    expect(within(menu).getByText(/at 100% capacity/i)).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: /noura idle/i })).toHaveAttribute('aria-disabled', 'true');
  });

  // -------------------------------------------------------------------------
  // R1 — the honest eligibility empty state
  // -------------------------------------------------------------------------

  it('saysNobodyCanReceiveALeadYet_insteadOfShowingAnEmptyPicker', async () => {
    // A fresh tenant: no user carries a Sales Rep profile, so owner-options answers with an
    // empty array — not an error. This used to render as a blank control that said nothing.
    getOwnerOptions.mockResolvedValue([]);
    asManager();
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    fireEvent.click(screen.getByRole('button', { name: /assign to…|assign to\.\.\./i }));

    expect(await screen.findByText(/nobody in this business unit can currently receive a lead/i)).toBeInTheDocument();
    expect(screen.getByText(/give someone a profile in sales > rep directory/i)).toBeInTheDocument();
  });

  it('doesNotOfferAssignToMe_whenGovernedRoutingWouldRefuseIt_andSaysWhy', async () => {
    // The reader is not in the eligible list at all — "Assign to me" would 409 after the click.
    getOwnerOptions.mockResolvedValue([OWNERS[1]]);
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    // A rep in this position can do nothing at all here, and the notice says exactly that
    // rather than promising an ability the controls no longer offer.
    await waitFor(() => expect(
      screen.getByText(/you cannot pick up inquiries yet/i),
    ).toBeInTheDocument());
    expect(screen.getByText(/sales rep profile/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /assign to me/i })).not.toBeInTheDocument();
  });

  it('tellsAManagerTheyCanStillAssignToOthers_whenRoutingWouldRefuseThemselves', async () => {
    // The same missing profile means something different to a manager: they cannot take a lead,
    // but handing one to a colleague still works. One notice, two truthful readings.
    getOwnerOptions.mockResolvedValue([OWNERS[1]]);
    asManager();
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    await waitFor(() => expect(
      screen.getByText(/you can assign inquiries to other people, but not to yourself yet/i),
    ).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /assign to me/i })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /assign to…|assign to\.\.\./i })).toBeInTheDocument();
  });

  it('doesNotClaimAnEmptyOwnerList_whenTheRequestFailed', async () => {
    getOwnerOptions.mockRejectedValue(new Error('network'));
    asManager();
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    fireEvent.click(screen.getByRole('button', { name: /assign to…|assign to\.\.\./i }));

    expect(await screen.findByText(/couldn't load the list of people who can take this lead/i)).toBeInTheDocument();
    expect(screen.queryByText(/nobody in this business unit can currently receive a lead/i)).not.toBeInTheDocument();
  });

  // -------------------------------------------------------------------------
  // Reason capture — only when it is somebody else's lead
  // -------------------------------------------------------------------------

  it('asksWhy_onlyWhenTheLeadAlreadyBelongsToSomebodyElse', async () => {
    asManager();
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    fireEvent.click(screen.getByRole('button', { name: /reassign/i }));
    fireEvent.click(await screen.findByRole('menuitem', { name: /sara bin ali/i }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/already belongs to tariq al-harbi/i)).toBeInTheDocument();
    // A disabled control that will not say why is a support ticket.
    expect(within(dialog).getByRole('button', { name: /reassign/i })).toBeDisabled();
    expect(within(dialog).getByText(/at least 5 characters/i)).toBeInTheDocument();
    expect(changeLeadOwner).not.toHaveBeenCalled();

    // The server refuses anything under five characters after trimming, so the form does too —
    // and says so, rather than letting the click fail.
    fireEvent.change(within(dialog).getByLabelText(/reason/i), { target: { value: 'sick' } });
    expect(within(dialog).getByRole('button', { name: /reassign/i })).toBeDisabled();
    expect(within(dialog).getByText(/at least 5 characters/i)).toBeInTheDocument();

    fireEvent.change(within(dialog).getByLabelText(/reason/i), { target: { value: 'Tariq is on leave' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /reassign/i }));

    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledTimes(1));
    const body = changeLeadOwner.mock.calls[0][1] as Record<string, unknown>;
    // `comment` is the field the server's reason rule binds — one field, not a speculative twin.
    expect(body.comment).toBe('Tariq is on leave');
    expect(body.assignedToUserId).toBe(ME);
  });

  // -------------------------------------------------------------------------
  // Authority — a control that cannot work is not shown
  // -------------------------------------------------------------------------

  it('doesNotOfferARepTheControlsOnlyAManagerCanUse', async () => {
    // The server answers 403 when a non-manager moves somebody else's lead, and the shared error
    // layer replaces a 403's sentence with a generic one — so a rep who clicked would learn
    // nothing. The controls are simply absent.
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    expect(screen.getByRole('button', { name: /assign to me/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /someone else/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^reassign$/i })).not.toBeInTheDocument();
  });

  it('leavesAColleaguesLeadsAloneForARep_andSaysSoBeforeTheClick', async () => {
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    fireEvent.click(screen.getByRole('checkbox', { name: /select all rows/i }));

    expect(await screen.findByText(/1 of these already belong to someone else/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /assign selected to…|assign selected to\.\.\./i })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /assign selected to me/i }));

    // Only the unowned one is attempted — the colleague's lead is not sent and then refused.
    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledTimes(1));
    expect(changeLeadOwner.mock.calls[0][0]).toBe(101);
  });

  // -------------------------------------------------------------------------
  // R2 — one filter
  // -------------------------------------------------------------------------

  it('opensOnTheReadersOwnWork_notOnEveryRowEverRecorded', async () => {
    renderPage();
    await waitFor(() => expect(lastListView()).toBe(`mine:${ME}`));
  });

  it('opensOnTheUnclaimedPile_forAManager', async () => {
    authUser.isManager = true;
    authUser.roleName = 'Sales Manager';
    renderPage();
    await waitFor(() => expect(lastListView()).toBe('unassigned'));
  });

  it('opensOnEverything_whenTheSessionCannotNameTheReader', async () => {
    // No identity means no working set to compute, and a filter the reader cannot see the reason
    // for is worse than no filter.
    authUser.id = undefined;
    renderPage();
    await waitFor(() => expect(lastListView()).toBeUndefined());
  });

  it('narrowsToUnassignedAndBackToEveryone_fromOneControl', async () => {
    renderPage();
    await screen.findByText('Tariq Al-Harbi');

    click(screen.getByRole('button', { name: /^unassigned$/i }));
    await waitFor(() => expect(lastListView()).toBe('unassigned'));
    // MEASURED: finding the unclaimed pile from the leads list costs one click. Before this it
    // could not be done on this screen at all.
    expect(clickCount).toBe(1);

    fireEvent.click(screen.getByRole('button', { name: /^everyone$/i }));
    await waitFor(() => expect(lastListView()).toBeUndefined());
  });

  it('narrowsTheQueueItIsOn_ratherThanReplacingIt', async () => {
    // Arriving on the Revisions tab and then asking for the unassigned ones means BOTH.
    renderPage('/procurement/leads/all?view=revisions');
    await screen.findByText('Tariq Al-Harbi');

    fireEvent.click(screen.getByRole('button', { name: /^unassigned$/i }));
    await waitFor(() => expect(lastListView()).toBe('revisions,unassigned'));
  });

  it('saysWhichFilterEmptiedTheList_andOffersTheNextStepAsAButton', async () => {
    // The tenant HAS inquiries (the unfiltered pageSize-1 count says 3); the page just has none
    // for this reader. With a true zero the page would say "No inquiries yet" instead.
    getAll.mockImplementation((params: { pageSize?: number }) => Promise.resolve(
      params.pageSize === 1 ? { ...page([]), totalCount: 3 } : page([]),
    ));
    renderPage();

    // The default working set is the reader's own, so "no rows" here means "none of yours".
    expect(await screen.findByText(/nothing is assigned to you/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /show unassigned inquiries/i }));

    expect(await screen.findByText(/every inquiry here already has an owner/i)).toBeInTheDocument();
    await waitFor(() => expect(lastListView()).toBe('unassigned'));

    fireEvent.click(screen.getByRole('button', { name: /clear filters/i }));
    await waitFor(() => expect(lastListView()).toBeUndefined());
    expect(await screen.findByText(/no inquiries yet/i)).toBeInTheDocument();
  });

  // -------------------------------------------------------------------------
  // R3 — bulk, and honest reporting of a partial batch
  // -------------------------------------------------------------------------

  it('assignsEverythingTicked_inOneAction', async () => {
    getAll.mockResolvedValue(page([UNOWNED, SECOND_UNOWNED]));
    renderPage();
    await screen.findAllByRole('button', { name: /assign to me/i });

    click(screen.getByRole('checkbox', { name: /select all rows/i }));
    click(await screen.findByRole('button', { name: /assign selected to me/i }));

    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledTimes(2));
    expect(clickCount).toBe(2);
    expect(await screen.findByText(/2 inquiries assigned to sara bin ali/i)).toBeInTheDocument();
  });

  it('assignsFiftyLeadsToOnePersonInTwoClicks', async () => {
    // The whole point of the exercise. Fifty inquiries, one owner, measured.
    //
    // The rows-per-page control is MUI's own and is left out of the count deliberately: it is a
    // one-off grid setting, not part of the assignment, and it carries no accessible name to
    // address it by (a separate defect, not this one). What is measured here is the assignment
    // itself, with fifty rows on screen.
    const fifty = Array.from({ length: 50 }, (_, index) => ({
      ...UNOWNED,
      id: 200 + index,
      nexoraSerial: `NOOR-SONS-LLC-2026-0002${String(index).padStart(2, '0')}`,
      rfqno: `RFQ-2${index}`,
    }));
    getAll.mockResolvedValue({ items: fifty, totalCount: fifty.length, pageNumber: 1, pageSize: 50 });
    renderPage();
    await waitFor(
      () => expect(screen.getAllByRole('button', { name: /assign to me/i })).toHaveLength(50),
      { timeout: 20_000 },
    );

    // 1: tick every row. 2: assign them.
    click(screen.getByRole('checkbox', { name: /select all rows/i }));
    click(await screen.findByRole('button', { name: /assign selected to me/i }));

    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledTimes(50), { timeout: 20_000 });
    expect(clickCount).toBe(2);
    expect(await screen.findByText(/50 inquiries assigned to sara bin ali/i)).toBeInTheDocument();
  });

  it('namesTheOnesThatFailed_andNeverReportsAPartialBatchAsSuccess', async () => {
    getAll.mockResolvedValue(page([UNOWNED, SECOND_UNOWNED]));
    changeLeadOwner.mockImplementation((leadId: number) => (
      leadId === 103
        ? Promise.reject({
          response: { status: 409, data: 'Lead assignment changed since it was loaded. Refresh and retry.' },
        })
        : Promise.resolve({ leadId, assignmentVersion: 4 })
    ));
    renderPage();
    await screen.findAllByRole('button', { name: /assign to me/i });

    fireEvent.click(screen.getByRole('checkbox', { name: /select all rows/i }));
    fireEvent.click(await screen.findByRole('button', { name: /assign selected to me/i }));

    // The failure is named, with the reason, and it does not evaporate with a snackbar.
    const report = (await screen.findByText(/1 inquiry could not be assigned/i)).closest('.MuiAlert-root');
    expect(report).not.toBeNull();
    expect(within(report as HTMLElement).getByText('NOOR-SONS-LLC-2026-000103')).toBeInTheDocument();
    expect(within(report as HTMLElement).getByText(/lead assignment changed since it was loaded/i)).toBeInTheDocument();
    // And the half that worked is not reported as the whole.
    expect(screen.queryByText(/2 inquiries assigned to/i)).not.toBeInTheDocument();
    expect(await screen.findByText(/1 of 2 assigned to sara bin ali/i)).toBeInTheDocument();
  });
});
