import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import LeadOwnerControl from './LeadOwnerControl';

vi.setConfig({ testTimeout: 30_000 });

/**
 * The lead detail page's owner control is the ONLY assignment surface a rep could reach before
 * the leads list grew one, and on a fresh tenant it was silently empty: `owner-options` answers
 * with an empty array — not an error — when nobody carries a Sales Rep profile, and the control
 * rendered that as a blank autocomplete with no explanation at all.
 *
 * It also offered "Assign to me" to everybody, including readers governed routing refuses, which
 * failed with a 409 AFTER the click.
 */

const getOwnerOptions = vi.fn();
const changeLeadOwner = vi.fn();

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

const ME = 2;
const COLLEAGUE = 77;

const authUser: { id?: number; isManager?: boolean; roleName?: string } = { id: ME };

/** Handing a lead to a colleague — or taking one off them — is a manager's action. */
const asManager = () => { authUser.isManager = true; authUser.roleName = 'Sales Manager'; };
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ userData: authUser, hasPermission: () => true }),
}));

const OWNERS = [
  { userId: ME, name: 'Sara Bin Ali', email: 'sara@nexora.test', roleName: 'Sales Rep', isAvailable: true, capacityPercent: 40, eligibilityReason: '' },
  { userId: COLLEAGUE, name: 'Tariq Al-Harbi', email: 'tariq@nexora.test', roleName: 'Sales Rep', isAvailable: true, capacityPercent: 60, eligibilityReason: '' },
];

const renderControl = (props: Partial<React.ComponentProps<typeof LeadOwnerControl>> = {}) => {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <LeadOwnerControl
        leadId={101}
        assignedToId={null}
        assignedToName={null}
        assignmentMethod="AUTOMATIC"
        assignmentVersion={3}
        canEdit
        {...props}
      />
    </QueryClientProvider>,
  );
};

/** The owner button is labelled with the current owner, so it is found by any of them. */
const openOwnerMenu = () => fireEvent.click(screen.getByRole('button', { name: /unassigned|tariq al-harbi|sara bin ali/i }));

/** "Assign to me" stays blocked until the server has said whether routing would accept you. */
const enabledAssignToMe = async () => {
  const item = await screen.findByRole('menuitem', { name: /assign to me/i });
  await waitFor(() => expect(item).not.toHaveAttribute('aria-disabled', 'true'));
  return item;
};

describe('LeadOwnerControl', () => {
  beforeAll(() => {
    if (typeof globalThis.crypto?.randomUUID !== 'function') {
      Object.defineProperty(globalThis.crypto ?? (globalThis.crypto = {} as Crypto), 'randomUUID', {
        configurable: true,
        value: () => '00000000-0000-4000-8000-000000000000',
      });
    }
  });

  beforeEach(() => {
    vi.clearAllMocks();
    authUser.id = ME;
    authUser.isManager = false;
    authUser.roleName = 'Sales Rep';
    getOwnerOptions.mockResolvedValue(OWNERS);
    changeLeadOwner.mockResolvedValue({ leadId: 101, assignmentVersion: 4 });
  });

  it('saysNobodyCanReceiveALead_insteadOfRenderingAnEmptyPicker', async () => {
    getOwnerOptions.mockResolvedValue([]);
    asManager();
    renderControl();

    openOwnerMenu();
    fireEvent.click(await screen.findByRole('menuitem', { name: /assign to…|assign to\.\.\./i }));

    expect(await screen.findByText(/nobody in this business unit can currently receive a lead/i)).toBeInTheDocument();
    expect(screen.getByText(/give someone a profile in sales > rep directory/i)).toBeInTheDocument();
  });

  it('disablesAssignToMeWithTheReasonPrinted_whenRoutingWouldRefuseTheReader', async () => {
    getOwnerOptions.mockResolvedValue([OWNERS[1]]);
    renderControl();

    openOwnerMenu();
    const assignToMe = await screen.findByRole('menuitem', { name: /assign to me/i });
    await waitFor(() => expect(assignToMe).toHaveAttribute('aria-disabled', 'true'));
    // Blocked, and it says why — rather than 409-ing after the click.
    expect(within(assignToMe).getByText(/sales rep profile/i)).toBeInTheDocument();
    expect(changeLeadOwner).not.toHaveBeenCalled();
  });

  it('explainsItselfInsteadOfOpeningAnEmptyMenu_whenARepMayDoNoneOfIt', async () => {
    // A rep looking at a COLLEAGUE's lead may not take it, may not give it away and may not put
    // it down. Every item filtered out leaves an empty box where an explanation belongs.
    renderControl({ assignedToId: COLLEAGUE, assignedToName: 'Tariq Al-Harbi', assignmentMethod: 'MANUAL', assignmentVersion: 5 });

    openOwnerMenu();

    const menu = await screen.findByRole('menu');
    expect(within(menu).getByText(/this inquiry belongs to tariq al-harbi/i)).toBeInTheDocument();
    expect(within(menu).getByText(/only a manager can move a lead that is already somebody's/i)).toBeInTheDocument();
    expect(within(menu).queryByRole('menuitem', { name: /assign to…|assign to\.\.\./i })).not.toBeInTheDocument();
    expect(within(menu).queryByText(/put it back in the pool/i)).not.toBeInTheDocument();
  });

  it('letsARepPutDownTheirOwnLead_inThePoolWordingRatherThanUnassign', async () => {
    // Unassigning no longer strands an enquiry — it returns to the routing queue — and a rep is
    // allowed to put down work that is theirs.
    renderControl({ assignedToId: ME, assignedToName: 'Sara Bin Ali', assignmentMethod: 'MANUAL', assignmentVersion: 4 });

    openOwnerMenu();

    const menu = await screen.findByRole('menu');
    expect(within(menu).getByText(/put it back in the pool/i)).toBeInTheDocument();
    expect(within(menu).getByText(/goes back on the queue and can be picked up again/i)).toBeInTheDocument();
    expect(within(menu).queryByRole('menuitem', { name: /assign to…|assign to\.\.\./i })).not.toBeInTheDocument();

    fireEvent.click(within(menu).getByText(/put it back in the pool/i));
    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledTimes(1));
    expect((changeLeadOwner.mock.calls[0][1] as Record<string, unknown>).action).toBe(1);
  });

  it('assignsToMeWithoutAskingWhy_whenNobodyOwnsTheLead', async () => {
    renderControl();

    openOwnerMenu();
    fireEvent.click(await enabledAssignToMe());

    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledTimes(1));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    const body = changeLeadOwner.mock.calls[0][1] as Record<string, unknown>;
    expect(body.assignedToUserId).toBe(ME);
    expect(body.expectedAssignmentVersion).toBe(3);
  });

  it('costsThreeClicksOnTheDetailPage_whichIsWhyTheListNeededItsOwnControl', async () => {
    // The BEFORE figure, measured on the surviving leg of the old journey. This control is the
    // only place a lead could be assigned until the list grew a column: Owner button, "Assign
    // to…", pick a name. Three clicks — and it is reachable only by opening the lead and then
    // navigating back, which is the two page loads the list version removes.
    asManager();
    renderControl();

    let clicks = 0;
    const click = (element: Element) => { clicks += 1; fireEvent.click(element); };

    click(screen.getByRole('button', { name: /unassigned/i }));
    click(await screen.findByRole('menuitem', { name: /assign to…|assign to\.\.\./i }));
    click(await screen.findByRole('menuitem', { name: /tariq al-harbi/i }));

    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledTimes(1));
    expect(clicks).toBe(3);
  });

  it('asksWhy_whenTakingALeadOffSomebodyElse', async () => {
    asManager();
    renderControl({ assignedToId: COLLEAGUE, assignedToName: 'Tariq Al-Harbi', assignmentMethod: 'MANUAL', assignmentVersion: 5 });

    openOwnerMenu();
    fireEvent.click(await enabledAssignToMe());

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/already belongs to tariq al-harbi/i)).toBeInTheDocument();
    expect(changeLeadOwner).not.toHaveBeenCalled();

    fireEvent.change(within(dialog).getByLabelText(/reason/i), { target: { value: 'Covering while he is away' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /reassign/i }));

    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledTimes(1));
    const body = changeLeadOwner.mock.calls[0][1] as Record<string, unknown>;
    expect(body.comment).toBe('Covering while he is away');
    expect(body.assignedToUserId).toBe(ME);
  });
});
