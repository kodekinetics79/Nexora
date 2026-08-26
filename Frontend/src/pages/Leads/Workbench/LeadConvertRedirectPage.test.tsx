import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import LeadConvertRedirectPage from './LeadConvertRedirectPage';

const Destination = () => {
  const location = useLocation();
  return <output aria-label="destination">{location.pathname}</output>;
};

const renderLegacyRoute = (path: string, route: string) => render(
  <MemoryRouter initialEntries={[path]}>
    <Routes>
      <Route path={route} element={<LeadConvertRedirectPage />} />
      <Route path="/procurement/leads/:id/workbench" element={<Destination />} />
    </Routes>
  </MemoryRouter>,
);

describe('legacy Lead conversion addresses', () => {
  it.each([
    ['/procurement/leads/492/convert', '/procurement/leads/:id/convert'],
    ['/procurement/rfqs/process/492', '/procurement/rfqs/process/:id'],
  ])('sends %s to the one governed decision workbench', (path, route) => {
    renderLegacyRoute(path, route);

    expect(screen.getByRole('status', { name: 'destination' })).toHaveTextContent(
      '/procurement/leads/492/workbench',
    );
  });
});
