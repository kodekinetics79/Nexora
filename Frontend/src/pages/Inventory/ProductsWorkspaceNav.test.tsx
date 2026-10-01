import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { clearAllUnsavedWork, setUnsavedWork } from '../../hooks/unsavedWorkRegistry';
import ProductsWorkspaceNav from './ProductsWorkspaceNav';

function LocationProbe() {
  return <output aria-label="location">{useLocation().pathname}</output>;
}

const renderNav = (path = '/inventory/products') => render(
  <MemoryRouter initialEntries={[path]}>
    <ProductsWorkspaceNav />
    <LocationProbe />
  </MemoryRouter>,
);

afterEach(() => {
  clearAllUnsavedWork();
  vi.restoreAllMocks();
});

describe('Products workspace navigation', () => {
  it('uses stable Products workspace labels', () => {
    renderNav();

    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual([
      'Products', 'Incoming & receipts', 'Stock activity', 'Traceability',
    ]);
    expect(screen.getByRole('tab', { name: 'Products' })).toHaveAttribute('aria-selected', 'true');
  });

  it('opens incoming supply and receipts as a separate Products screen', () => {
    renderNav();
    fireEvent.click(screen.getByRole('tab', { name: 'Incoming & receipts' }));
    expect(screen.getByLabelText('location')).toHaveTextContent('/inventory/incoming');
    expect(screen.getByRole('tab', { name: 'Incoming & receipts' })).toHaveAttribute('aria-selected', 'true');
  });

  it('does not discard unsaved pricing changes on a tab click', () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false);
    setUnsavedWork('products', 'Leave without saving?');
    renderNav();

    fireEvent.click(screen.getByRole('tab', { name: 'Incoming & receipts' }));

    expect(window.confirm).toHaveBeenCalledWith('Leave without saving?');
    expect(screen.getByLabelText('location')).toHaveTextContent('/inventory/products');
  });
});
