import type { CurrencyDTO } from '../../../api/services/currencyService';

/**
 * The rules behind the manual order form, without a DOM.
 *
 * A manual order is the document the customer is invoiced from, and finance refuses to issue an
 * invoice against an order that names no currency. The form therefore has to state one, and the
 * sensible starting point is the tenant's own base currency — a setting the customer filled in
 * during setup, not something inferred from a country table.
 */

/** The currency the form starts on: the tenant's base currency, or the only active one. Never a guess. */
export function defaultCurrencyId(currencies: readonly CurrencyDTO[] | undefined): number | null {
  if (!currencies || currencies.length === 0) return null;
  const active = currencies.filter((c) => c.isActive !== false);
  const base = active.filter((c) => c.isBaseCurrency);
  if (base.length === 1) return base[0].id;
  if (active.length === 1) return active[0].id;
  return null;
}

export interface CreateOrderFormState {
  customerId: number | null;
  currencyId: number | null;
  itemCount: number;
}

/** Everything that stops the order being raised, in the words the rep should read. Empty = can save. */
export function createOrderBlockers(state: CreateOrderFormState): string[] {
  const blockers: string[] = [];
  if (!state.customerId) blockers.push('Please select a customer');
  if (!state.currencyId) blockers.push('Please choose the currency the customer will be invoiced in');
  if (state.itemCount === 0) blockers.push('Please add at least one item before saving');
  return blockers;
}
