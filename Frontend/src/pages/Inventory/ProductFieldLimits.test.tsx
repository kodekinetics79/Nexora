import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';

/**
 * The caps the server enforces, told to the user at the keyboard rather than at the Save button.
 *
 * <p>`Products."ProductName"` is varchar(100) and `Products."Description"` is varchar(500), and the
 * request DTOs were tightened to match — so an over-long value is now refused by validation with a
 * readable sentence instead of dying inside the INSERT as Postgres 22001. That refusal is correct
 * and it is still too late: the user has already typed a name, filled a form and pressed Save.</p>
 *
 * <p>What is pinned here:</p>
 * <ul>
 *   <li>the input carries the same number the server does — 100 and 500, not a rounder guess;</li>
 *   <li>a counter is on screen, because `maxLength` stops keystrokes silently and a field that
 *       ignores the keyboard without saying why reads as broken;</li>
 *   <li>the server's own sentence still reaches the user, because this is defence in depth and not
 *       a replacement — paste, autofill and any future column change all route through it.</li>
 * </ul>
 */

const create = vi.fn();
const update = vi.fn();
const getById = vi.fn();

vi.mock('../../api/services/productService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/productService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getCategories: () => Promise.resolve([]),
      getSubCategories: () => Promise.resolve([]),
      getWarehouses: () => Promise.resolve([]),
      getUoms: () => Promise.resolve([]),
      getSuppliers: () => Promise.resolve([]),
      getById: (id: number) => getById(id),
      create: (data: FormData) => create(data),
      update: (id: number, data: FormData) => update(id, data),
    },
  };
});

const hasPermission = vi.fn();
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { userName: 'rep', businessUnitId: 1 },
    hasPermission: (module: string, action?: string) => hasPermission(module, action),
  }),
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string, fallback?: string) => fallback ?? key }),
}));

import ProductFormDialog from './ProductFormDialog';

/** The numbers the columns hold. Kept literal so a widened DTO cannot quietly drag them along. */
const NAME_MAX = 100;
const DESCRIPTION_MAX = 500;

function renderDialog() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <ProductFormDialog open onClose={() => {}} />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

const nameField = () => screen.getByRole('textbox', { name: 'Product Name' });
const descriptionField = () => screen.getByRole('textbox', { name: 'Description' });

beforeEach(() => {
  vi.clearAllMocks();
  hasPermission.mockReturnValue(true);
  create.mockResolvedValue({ id: 1 });
});

describe('Product name and description carry the server caps at the input', () => {
  it('caps Product Name at the width of the column, not at some rounder number', () => {
    renderDialog();

    expect(nameField()).toHaveAttribute('maxlength', String(NAME_MAX));
  });

  it('caps Description at the width of its column too', () => {
    renderDialog();

    expect(descriptionField()).toHaveAttribute('maxlength', String(DESCRIPTION_MAX));
  });

  it('shows where the user is, so a field that stops accepting keystrokes has said why', () => {
    renderDialog();

    expect(screen.getByText(`0/${NAME_MAX}`)).toBeInTheDocument();
    expect(screen.getByText(`0/${DESCRIPTION_MAX}`)).toBeInTheDocument();

    fireEvent.change(nameField(), { target: { value: 'Gate valve' } });
    expect(screen.getByText(`10/${NAME_MAX}`)).toBeInTheDocument();
  });

  it('says the limit is reached at the boundary, rather than letting the keyboard just go dead', () => {
    renderDialog();

    fireEvent.change(nameField(), { target: { value: 'x'.repeat(NAME_MAX) } });

    expect(screen.getByText(`${NAME_MAX}/${NAME_MAX} · limit reached`)).toBeInTheDocument();
  });

  it('keeps the server sentence as the authority — the cap here does not replace it', async () => {
    create.mockRejectedValue({
      response: {
        data: { detail: 'The field ProductName must be a string with a maximum length of 100.' },
      },
    });
    renderDialog();

    fireEvent.change(screen.getByRole('textbox', { name: 'Part No' }), { target: { value: 'VLV-100' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create Product' }));

    await waitFor(() => expect(
      screen.getByText('The field ProductName must be a string with a maximum length of 100.'),
    ).toBeInTheDocument());
  });
});
