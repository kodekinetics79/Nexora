import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * "Assistants & tools" (Copilot, its approvals and activity, Service BOQs, Commercial memory)
 * was gated on Dashboard/Quotations permissions, so every sales role of every tenant saw it —
 * including tenants that have not bought the AI capability, for whom every row leads nowhere.
 * The group now needs the tenant entitlement `capability.ai`.
 */

const auth = { entitlements: [] as string[] };
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { isManager: false, businessUnitId: 1 },
    hasPermission: () => true,
    hasEntitlement: (key: string) => auth.entitlements.includes(key),
  }),
}));
vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string, fallback?: string) => fallback ?? key }),
}));

import Sidebar from './Sidebar';

const renderRail = () => render(
  <MemoryRouter initialEntries={['/inbox']}><Sidebar collapsed={false} /></MemoryRouter>,
);

beforeEach(() => {
  auth.entitlements = [];
});

describe('the Assistants & tools group', () => {
  it('is absent for a tenant without the AI capability, whatever the role may open', () => {
    renderRail();
    expect(screen.queryByRole('button', { name: /assistants & tools/i })).not.toBeInTheDocument();
    // The other workspaces are untouched by the entitlement.
    expect(screen.getByRole('button', { name: /catalogue & stock/i })).toBeInTheDocument();
  });

  it('is present once the tenant holds capability.ai', () => {
    auth.entitlements = ['capability.ai'];
    renderRail();
    expect(screen.getByRole('button', { name: /assistants & tools/i })).toBeInTheDocument();
  });
});
