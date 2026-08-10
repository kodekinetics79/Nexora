/**
 * Money formatting and parsing for a KSA-first product that legitimately trades in several
 * currencies.
 *
 * One rule underpins this module: **the currency comes from the record.** There is no default
 * symbol and no fallback currency. A Nexora tenant may quote in SAR, USD or EUR, and the screen
 * must show what the record says. Where the record does not carry a currency, a bare number is
 * the honest output — inventing a symbol is precisely how the hardcoded-`$` defect began.
 *
 * `SAR` is correct only as a *provisioning default* for a new tenant. It is never correct as a
 * rendering assumption, so it does not appear anywhere in this file.
 */

/** Rendered in place of a money value that does not exist or is not a finite number. */
const NOT_AVAILABLE = '—';

/**
 * Formats a money amount in the currency the record carries.
 *
 * @param value        the amount; `null`/`undefined`/non-finite renders as an explicit gap
 * @param currencyCode ISO 4217 code from the record. When absent, a bare grouped number is
 *                     returned — this fallback is deliberate and must be preserved.
 */
export const formatMoney = (
  value: number | null | undefined,
  currencyCode?: string | null,
): string => {
  if (value == null || !Number.isFinite(value)) return NOT_AVAILABLE;

  const code = currencyCode?.trim();
  // No currency on the record: a bare number is the truthful rendering.
  if (!code) return value.toLocaleString(undefined, { maximumFractionDigits: 2 });

  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency: code }).format(value);
  } catch {
    // A non-ISO or malformed code must not blank the figure. Show the code beside the number
    // so the unit is still stated, just unformatted.
    return `${code} ${value.toLocaleString(undefined, { maximumFractionDigits: 2 })}`;
  }
};

/**
 * Parses a money value a person typed or pasted.
 *
 * This exists because a parser that strips one hardcoded symbol — `value.replace('$ ', '')` —
 * silently corrupts every amount the moment the displayed currency changes, and already fails
 * today on a pasted `$1,000.00` (no space after the symbol, so the strip is a no-op and
 * `Number()` yields `NaN`).
 *
 * Accepts leading/trailing symbols or ISO codes, whitespace and grouping separators. Returns
 * `null` — never `NaN`, never a truncated value — when the input is not a finite number, so the
 * caller must decide what to do rather than storing a corrupt figure.
 */
export const parseMoneyInput = (raw: string | null | undefined): number | null => {
  if (raw == null) return null;

  // Drop anything that cannot form part of a number: currency symbols, ISO letters, spaces.
  const cleaned = raw.replace(/[^\d.,-]/g, '').trim();
  if (!cleaned || !/\d/.test(cleaned)) return null;

  const lastComma = cleaned.lastIndexOf(',');
  const lastDot = cleaned.lastIndexOf('.');

  let normalised: string;
  if (lastComma > -1 && lastDot > -1) {
    // Both separators present: whichever comes last is the decimal separator.
    normalised =
      lastComma > lastDot
        ? cleaned.replace(/\./g, '').replace(',', '.') // 1.234,56
        : cleaned.replace(/,/g, ''); // 1,234.56
  } else if (lastComma > -1) {
    // A lone comma followed by exactly three digits reads as grouping (1,234); otherwise it is
    // a decimal separator (10,50). Ambiguous by nature — this picks the commoner reading.
    normalised = /,\d{3}$/.test(cleaned) ? cleaned.replace(/,/g, '') : cleaned.replace(',', '.');
  } else {
    normalised = cleaned;
  }

  const value = Number(normalised);
  return Number.isFinite(value) ? value : null;
};
