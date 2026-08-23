import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The worst of the four: a failed read painted a BLANK WHITE PANEL.
 *
 * The countries column was `loadingCountries ? <spinner> : filteredCountries.map(...)` — a map with
 * no length guard, over `(countries || []).filter(...)`. When GET /api/Country 500s the array is
 * empty, the map yields nothing, and the panel renders as an empty <ul>: no rows, no message, no
 * error, not even MUI's bare "No rows". Nothing on screen to read and nothing to act on.
 *
 * An operator reads white space as "this tenant has no countries" and starts re-entering reference
 * data that is already there — and the states and cities columns stay locked behind a country
 * selection that can never be made, so the whole screen is inert with no explanation.
 */

const { countriesGetAll, statesGetAll, citiesGetAll } = vi.hoisted(() => ({
  countriesGetAll: vi.fn(), statesGetAll: vi.fn(), citiesGetAll: vi.fn(),
}));

vi.mock('../../../api/services/countryService', () => ({
  default: { getAll: countriesGetAll, create: vi.fn(), update: vi.fn(), delete: vi.fn(), getById: vi.fn() },
}));
vi.mock('../../../api/services/stateService', () => ({
  default: { getAll: statesGetAll, create: vi.fn(), update: vi.fn(), delete: vi.fn(), getById: vi.fn() },
}));
vi.mock('../../../api/services/cityService', () => ({
  default: { getAll: citiesGetAll, create: vi.fn(), update: vi.fn(), delete: vi.fn(), getById: vi.fn() },
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (k: string, f?: string) => f ?? k }),
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 1 }, hasPermission: () => true }),
}));

import LocationMaster from './LocationMaster';

/** What `src/api/axiosInstance.ts` re-rejects on a 500 — the AxiosError, untouched. */
const serverOutage = (url: string) => Object.assign(new Error('Request failed with status code 500'), {
  isAxiosError: true,
  code: 'ERR_BAD_RESPONSE',
  config: { method: 'get', url },
  request: {},
  response: { status: 500, data: '', headers: {} },
});

/** `CountryDTO` / `StateDTO` from the services — buid and isActive are part of the real shape. */
const saudi = {
  countryId: 1, countryCode: 'SA', countryName: 'Saudi Arabia',
  description: null, buid: 1, isActive: true,
};
const riyadh = {
  stateId: 11, stateCode: 'RD', stateName: 'Riyadh Province', countryId: 1,
  buid: 1, description: null, isActive: true,
};

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <LocationMaster />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  statesGetAll.mockResolvedValue([riyadh]);
  citiesGetAll.mockResolvedValue([]);
});

describe('Setup › Locations, when the countries read fails', () => {
  it('never leaves the panel blank — something readable is always on it', async () => {
    countriesGetAll.mockRejectedValue(serverOutage('/api/Country'));
    renderPage();

    await screen.findByText(/Countries could not be loaded/i);

    // The exact pre-fix artefact: a <ul> rendered with zero children and zero text. If any list on
    // this screen has no text in it, the operator is looking at white space again.
    const textlessLists = screen.queryAllByRole('list').filter((l) => !l.textContent?.trim());
    expect(textlessLists).toHaveLength(0);
  });

  it('does not present the failure as "there are no countries"', async () => {
    countriesGetAll.mockRejectedValue(serverOutage('/api/Country'));
    renderPage();

    await screen.findByText(/Countries could not be loaded/i);
    expect(screen.queryByText(/No countries found/i)).not.toBeInTheDocument();
    expect(screen.getByText(/Nothing has been removed/i)).toBeInTheDocument();
  });

  it('offers a retry on the column that failed', async () => {
    countriesGetAll.mockRejectedValue(serverOutage('/api/Country'));
    renderPage();

    expect(await screen.findByRole('button', { name: /Try again/i })).toBeInTheDocument();
  });
});

describe('Setup › Locations, when a dependent read fails', () => {
  it('does not tell the operator a country has no states when the states read failed', async () => {
    // "No states found" is the same lie one column across: it is printed from an empty array the
    // failed read produced, not from anything the server said.
    countriesGetAll.mockResolvedValue([saudi]);
    statesGetAll.mockRejectedValue(serverOutage('/api/State'));
    renderPage();

    (await screen.findByText('Saudi Arabia')).click();

    await screen.findByText(/States could not be loaded/i);
    expect(screen.queryByText(/No states found/i)).not.toBeInTheDocument();
  });
});

describe('Setup › Locations, when the read succeeds', () => {
  it('says so plainly when the tenant genuinely has no countries', async () => {
    // The other half of the blank-panel fix: an empty READ must also produce words. This column
    // never had the length guard the states and cities columns already had.
    countriesGetAll.mockResolvedValue([]);
    renderPage();

    expect(await screen.findByText(/No countries found/i)).toBeInTheDocument();
    expect(screen.queryByText(/Countries could not be loaded/i)).not.toBeInTheDocument();
  });

  it('lists the countries it read, with no error surface', async () => {
    countriesGetAll.mockResolvedValue([saudi]);
    renderPage();

    // Deliberately NOT anchored on the <ul>: the broken page renders the list element immediately
    // (spinner inside it) and the fixed page only after the read lands, so a `findByRole('list')`
    // here would pass and fail for reasons that have nothing to do with the defect.
    expect(await screen.findByText('Saudi Arabia')).toBeInTheDocument();
    expect(screen.queryByText(/Countries could not be loaded/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/No countries found/i)).not.toBeInTheDocument();
  });
});
