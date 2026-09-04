import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import OutstandingLeadsPage from './OutstandingLeadsPage';
import AssignedLeadsPage from './AssignedLeadsPage';

/**
 * Both queues posted to `POST /api/UnAssignedLead/assign` — MANAGER-ONLY — from screens any
 * Leads:Edit user can open. So a rep either got a 403 whose sentence the error layer replaces
 * with a generic one, or (once the control was hidden behind `isManager`) no control and no
 * explanation at all. `PUT /api/commercial-routing/leads/{id}/owner` is the path that resolves
 * the caller's rank against the lead's CURRENT owner, which is the rule the product actually has.
 */

const getOutstandingLeads = vi.fn();
const getAssignedLeads = vi.fn();
const getUsersForAssignment = vi.fn();
const assignLead = vi.fn();
const changeLeadOwner = vi.fn();
const getOwnerOptions = vi.fn();

vi.mock('../../api/services/leadService', () => ({
  default: {
    getOutstandingLeads: (...a: unknown[]) => getOutstandingLeads(...a),
    getAssignedLeads: (...a: unknown[]) => getAssignedLeads(...a),
    getUsersForAssignment: (...a: unknown[]) => getUsersForAssignment(...a),
    assignLead: (...a: unknown[]) => assignLead(...a),
  },
  assignabilityNote: () => 'note',
}));

vi.mock('../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/commercialRoutingService')>();
  return {
    ...actual,
    default: {
      changeLeadOwner: (...a: unknown[]) => changeLeadOwner(...a),
      getOwnerOptions: () => getOwnerOptions(),
    },
  };
});

const ME = 7;
const COLLEAGUE = 9;
let authUser: Record<string, unknown> = { id: ME, businessUnitId: 1, isManager: false, isSuperAdmin: false };
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ userData: authUser, hasPermission: () => true }),
}));

vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));
vi.mock('./ResolveClientDialog', () => ({ default: () => null }));
vi.mock('./ClientCell', () => ({ default: () => null, clientDisplayName: () => 'Acme' }));
vi.mock('../../components/layout/ViewTabs', () => ({ default: () => null }));

const unownedLead = {
  id: 501, rfqno: 'RFQ-501', buyersName: 'Acme', clientemail: 'a@b.test',
  recDate: '2026-08-01T00:00:00Z', acceptedDate: '2026-08-01T00:00:00Z',
  leadSource: 'Email', businessUnitId: 1, itemCount: 3,
  assignedToId: null, assignedToFullName: null, assignmentVersion: 4,
};
const colleaguesLead = { ...unownedLead, id: 502, assignedToId: COLLEAGUE, assignedToFullName: 'Tariq Al-Harbi', assignmentVersion: 6 };

const eligibleMe = {
  userId: ME, name: 'Sara Bin Ali', email: 's@n.test', roleName: 'Sales Rep',
  isAvailable: true, capacityPercent: 40, eligibilityReason: '',
};

function renderPage(page: 'outstanding' | 'assigned') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>{page === 'outstanding' ? <OutstandingLeadsPage /> : <AssignedLeadsPage />}</MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  authUser = { id: ME, businessUnitId: 1, isManager: false, isSuperAdmin: false };
  getOwnerOptions.mockResolvedValue([eligibleMe]);
  changeLeadOwner.mockResolvedValue({ leadId: 501, assignmentVersion: 5 });
  getOutstandingLeads.mockResolvedValue({ items: [unownedLead], totalCount: 1 });
  getAssignedLeads.mockResolvedValue({ items: [colleaguesLead], totalCount: 1 });
});

describe('Outstanding inquiries — a rep can pick up unowned work', () => {
  it('gives a non-manager the assign verb, through the endpoint that permits it', async () => {
    renderPage('outstanding');

    const take = await screen.findByRole('button', { name: /assign to me/i });
    fireEvent.click(take);

    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledWith(501, expect.objectContaining({
      assignedToUserId: ME,
      // The optimistic-concurrency token the ownership endpoint demands, from the row itself.
      expectedAssignmentVersion: 4,
    })));
    // The manager-only queue endpoint is never touched.
    expect(assignLead).not.toHaveBeenCalled();
  });

  it('says why when routing will not accept the reader, instead of hiding the control silently', async () => {
    getOwnerOptions.mockResolvedValue([
      { ...eligibleMe, isAvailable: false, eligibilityReason: 'Your workload is at 100% of capacity.' },
    ]);
    renderPage('outstanding');

    expect(await screen.findByText(/your workload is at 100% of capacity/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /assign to me/i })).not.toBeInTheDocument();
  });

  it('does not report an owner-list outage as "nobody can take it"', async () => {
    getOwnerOptions.mockRejectedValue(new Error('network down'));
    renderPage('outstanding');

    expect(await screen.findByText(/we couldn.t check who can take these inquiries/i)).toBeInTheDocument();
  });
});

describe('Assigned inquiries — moving somebody else’s work', () => {
  it('prints why a rep cannot move a colleague’s inquiry rather than failing after the click', async () => {
    renderPage('assigned');

    expect(await screen.findByText('Tariq Al-Harbi')).toBeInTheDocument();
    expect(screen.getByText(/only a manager can move it/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /change the owner of/i })).not.toBeInTheDocument();
  });

  it('lets a manager reassign, and asks why because it already belongs to someone', async () => {
    authUser = { id: ME, businessUnitId: 1, isManager: true, isSuperAdmin: false };
    getOwnerOptions.mockResolvedValue([eligibleMe, {
      userId: COLLEAGUE, name: 'Tariq Al-Harbi', email: 't@n.test', roleName: 'Sales Rep',
      isAvailable: true, capacityPercent: 20, eligibilityReason: '',
    }]);
    renderPage('assigned');

    expect(await screen.findByText('Tariq Al-Harbi')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /change the owner of rfq-501/i }));
    fireEvent.click(await screen.findByRole('menuitem', { name: /sara bin ali/i }));

    expect(await screen.findByText('Why is this moving?')).toBeInTheDocument();
    expect(changeLeadOwner).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText(/reason/i), { target: { value: 'Tariq is on leave until the 3rd' } });
    fireEvent.click(screen.getByRole('button', { name: 'Reassign' }));

    await waitFor(() => expect(changeLeadOwner).toHaveBeenCalledWith(502, expect.objectContaining({
      assignedToUserId: ME,
      expectedAssignmentVersion: 6,
      comment: 'Tariq is on leave until the 3rd',
    })));
    expect(assignLead).not.toHaveBeenCalled();
  });
});
