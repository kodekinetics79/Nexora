import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import type { Tenant } from '../types';
import { TenantNameLink } from './TenantsPage';

const tenant = {
  id: '42',
  name: 'Acme Industrial',
  slug: 'acme-industrial',
} as Tenant;

describe('TenantsPage tenant identity', () => {
  it('is a keyboard-focusable semantic link while preserving direct navigation', () => {
    render(
      <MemoryRouter initialEntries={['/platform/tenants']}>
        <Routes>
          <Route path="/platform/tenants" element={<TenantNameLink tenant={tenant} />} />
          <Route path="/platform/tenants/:id" element={<h1>Tenant detail destination</h1>} />
        </Routes>
      </MemoryRouter>,
    );

    const link = screen.getByRole('link', { name: 'Acme Industrial' });
    expect(link).toHaveAttribute('href', '/platform/tenants/42');

    link.focus();
    expect(link).toHaveFocus();

    fireEvent.click(link);
    expect(screen.getByRole('heading', { name: 'Tenant detail destination' })).toBeVisible();
  });
});
