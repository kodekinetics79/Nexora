import { describe, expect, it } from 'vitest';

/**
 * A source-level guard for one specific, repeated defect: a money figure rendered with a literal
 * currency symbol instead of the one the record carries.
 *
 * `formatMoney` and its own tests already prove the FORMATTER never invents a symbol. They cannot
 * prove a screen uses it. The Orders, Order View, Edit Quote and Shipment screens each printed
 * `$ {amount.toLocaleString()}` while the record they were displaying carried a CurrencyId — and on
 * the Orders surface the backend DTO had already been extended with `CurrencyCode` specifically so
 * these screens could stop doing it. The value reached the browser and no screen read it, because
 * the frontend interface never declared the field.
 *
 * This is deliberately a text scan rather than a render test: the failure is that a screen never
 * asks the record for its currency at all, which no amount of rendering with fixture data reveals.
 */
const MONEY_SYMBOL_RENDER = /[$£€]\s*\{|\{\s*['"`][$£€]/;

// Vite resolves this at build time, so no filesystem API — and therefore no Node types — is needed.
const sources = import.meta.glob('./**/*.tsx', { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

describe('core journey money rendering', () => {
  it('never renders an amount with a hardcoded currency symbol', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(0); // the glob must actually match something

    const offenders = Object.entries(sources)
      .filter(([, source]) => {
        // Strip comments: prose about the "$" defect is not the defect. Then neutralise `${...}`
        // template interpolation, which is not a currency symbol.
        const code = source
          .replace(/\/\*[\s\S]*?\*\//g, '')
          .replace(/^\s*\/\/.*$/gm, '')
          .replace(/\$\{/g, '@{');
        return MONEY_SYMBOL_RENDER.test(code);
      })
      .map(([path]) => path);

    expect(offenders).toEqual([]);
  });
});
