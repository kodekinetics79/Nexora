/**
 * One rendering of a machine status code for a human.
 *
 * The codebase had grown roughly ten independent one-line versions of this, with three
 * incompatible casing rules, so the same `PARTIAL_MATCH` arrived on the client-PO inbox as
 * "PARTIAL MATCH", on the duplicate-uploads page as "Partial Match" and on the extraction
 * review detail as "Partial match". A salesperson moving along the RFQ journey saw a different
 * spelling of the same state on each screen.
 *
 * This is a consolidation of those duplicates, not a new abstraction: the helpers it replaces
 * are deleted, and it deliberately stops at one function. It is NOT a per-domain label registry
 * — a code whose rendering needs domain knowledge should carry a label from the server.
 *
 * The rule: split camelCase and PascalCase boundaries, split on underscores, hyphens and
 * whitespace, then title-case each word. Acronyms are the one exception, because
 * `EXTERNAL_PO_CREATED` reading as "External Po Created" is worse than the raw code it replaced.
 * That list is a small constant of business abbreviations this product actually uses, kept here
 * rather than made configurable.
 */

/** Words that must not be title-cased. Extend only when a real code needs it. */
const ACRONYMS = new Set([
  'PO', 'RFQ', 'SLA', 'ATP', 'UOM', 'VAT', 'ZATCA', 'AI', 'API', 'SKU', 'MPN', 'ETA', 'ERP', 'CRM',
]);

/**
 * @param code     the status code from the server, e.g. `REVIEW_REQUIRED` or `KnownInStock`
 * @param fallback rendered when the code is absent — never invent a state for a missing one
 */
export function statusLabel(code: string | null | undefined, fallback = 'Not recorded'): string {
  if (code == null) return fallback;

  const words = code
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .split(/[\s_-]+/)
    .filter(Boolean);
  if (words.length === 0) return fallback;

  return words
    .map((word) =>
      ACRONYMS.has(word.toUpperCase())
        ? word.toUpperCase()
        : word.charAt(0).toUpperCase() + word.slice(1).toLowerCase(),
    )
    .join(' ');
}

export default statusLabel;
