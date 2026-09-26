import type { QuoteRevisionImpactDTO, QuoteRevisionLineChangeDTO } from '../../../api/services/quoteService';

/**
 * The words the rep reads when a customer revision lands on a quote.
 *
 * Derived from the server's projection, never from the quote alone: the screen used to print
 * "must be reviewed against Lead Revision 3" — 3 being the revision the quote was BUILT from —
 * and nothing about what changed. A rep cannot decide "apply or keep" from that sentence.
 */

const formatQuantity = (value?: string | null): string => {
  if (value === null || value === undefined || value === '') return '—';
  const n = Number(value);
  return Number.isFinite(n) ? n.toLocaleString('en-US', { maximumFractionDigits: 4 }) : value;
};

/** "line 2 quantity 20 → 35", "line 5 added (qty 4)", "line 3 removed". */
export const describeRevisionChange = (change: QuoteRevisionLineChangeDTO): string => {
  const line = `line ${change.line}`;
  switch (change.field) {
    case 'added':
      return change.to ? `${line} added (qty ${formatQuantity(change.to)})` : `${line} added`;
    case 'removed':
      return `${line} removed`;
    case 'changed':
      return `${line} changed`;
    case 'quantity':
      return `${line} quantity ${formatQuantity(change.from)} → ${formatQuantity(change.to)}`;
    default:
      return `${line} ${change.field} ${change.from ?? '—'} → ${change.to ?? '—'}`;
  }
};

export interface RevisionImpactWords {
  title: string;
  /** The sentence under the title: what arrived, what it was built on, what changed. */
  detail: string;
  /** True when at least one line quantity moved, so "Apply the new quantities" has work to do. */
  hasQuantityChanges: boolean;
}

/**
 * @param detail the server's projection; null on an older server that sends only the type string.
 * @param fallbackSourceRevision `quote.sourceLeadRevision`, used only when `detail` is null.
 * @param isDraft a draft can take the change in place; a sent quote is revised.
 */
export const describeRevisionImpact = (
  detail: QuoteRevisionImpactDTO | null | undefined,
  fallbackSourceRevision: number,
  isDraft: boolean,
): RevisionImpactWords => {
  const built = isDraft ? 'this draft' : 'this quote was sent';
  if (!detail) {
    return {
      title: 'Customer revision received',
      detail: `A newer customer revision arrived after ${built} (built on revision ${fallbackSourceRevision}). `
        + 'The quote still carries the earlier figures.',
      hasQuantityChanges: false,
    };
  }
  const changes = detail.changes ?? [];
  const changed = changes.length > 0
    ? ` Changed: ${changes.map(describeRevisionChange).join(' · ')}.`
    : ' No line changed; the customer amended header details only.';
  return {
    title: 'Customer revision received',
    detail: `Revision ${detail.toRevision} arrived after ${built} (built on revision ${detail.fromRevision}).${changed}`,
    hasQuantityChanges: changes.some((c) => c.field === 'quantity'),
  };
};
