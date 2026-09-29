import type { QuoteDTO } from '../../../api/services/quoteService';
import { formatDeadlineDate } from '../../../utils/dates';

/**
 * The one reading of a quote's state, shared by the Quotes list and the quote page so the two
 * can never say different things (design review 2026-09-28).
 *
 * Every test is on the status CODE, never on the display label: the label can be renamed in
 * Setup, and the quote page used to compare `statusValue === 'Sent'`, so a renamed "Sent" made
 * Send, Customer responded and Record outcome vanish without a word.
 */

/** DRAFT, SENT, ACCEPTED, REJECTED, EXPIRED, ORDERED, or '' when unknown. Code first, then the label, upper-cased. */
export const quoteCode = (quote: Pick<QuoteDTO, 'statusCode' | 'statusValue'>): string =>
  (quote.statusCode || quote.statusValue || '').trim().toUpperCase();

export type QuoteTone = 'neutral' | 'info' | 'success' | 'warning' | 'error';

export interface QuoteStatusWords {
  /** The chip. */
  label: string;
  tone: QuoteTone;
  /** The line under the chip. */
  detail?: string;
  /** More on hover, when there is more to say. */
  hint?: string;
}

const day = (value?: string | null) => (value ? formatDeadlineDate(value, '') : '');
const ago = (days?: number | null) =>
  days == null ? '' : days === 0 ? 'today' : `${days} day${days === 1 ? '' : 's'} ago`;

/**
 * What the Status column and the quote page's status chip say.
 *
 * Priority: replaced → closed (the client's own ending wins over Won / Lost / Expired) → sent (the
 * client's own step, then customer replied, then no reply for too long, then plain sent) → draft.
 */
export function quoteStatusWords(quote: QuoteDTO): QuoteStatusWords {
  const code = quoteCode(quote);
  const reason = [quote.outcomeReasonName, quote.outcomeNote].filter(Boolean).join(' — ') || undefined;
  const ending = quote.subStatusKind === 'ENDING' ? quote.subStatusName : null;

  if (quote.supersededByQuoteNo) {
    return { label: 'Replaced', tone: 'neutral', detail: `By ${quote.supersededByQuoteNo}`,
      hint: 'A newer revision exists. Work on that one.' };
  }
  if (code === 'ORDERED') {
    // Always "Won" once the order exists: an ending such as "Verbal award, awaiting PO" would be
    // untrue here. The client's word stays on hover.
    return { label: 'Won', tone: 'success', detail: 'Order placed', hint: [ending, reason].filter(Boolean).join(' — ') || undefined };
  }
  if (code === 'ACCEPTED') {
    return { label: ending || 'Won', tone: 'success', detail: quote.outcomeReasonName || (quote.outcomeOn ? `Won ${day(quote.outcomeOn)}` : undefined), hint: reason };
  }
  if (code === 'REJECTED') {
    return { label: ending || 'Lost', tone: 'error', detail: quote.outcomeReasonName || (quote.outcomeOn ? `Lost ${day(quote.outcomeOn)}` : undefined), hint: reason };
  }
  if (code === 'EXPIRED') {
    return { label: ending || 'Expired', tone: 'neutral', detail: quote.outcomeReasonName || (quote.outcomeOn ? `Expired ${day(quote.outcomeOn)}` : undefined), hint: reason };
  }
  if (code === 'SENT') {
    const sent = quote.daysSinceSent != null ? `Sent ${ago(quote.daysSinceSent)}` : 'Sent';
    // The customer still holds this quote; the revision that will replace it is not out yet.
    if (quote.pendingRevisionQuoteNo) {
      return { label: 'Sent', tone: 'info', detail: `Revision ${quote.pendingRevisionQuoteNo} not sent yet` };
    }
    if (quote.subStatusKind === 'STEP' && quote.subStatusName) {
      return { label: quote.subStatusName, tone: 'info', detail: quote.subStatusOn ? `Since ${day(quote.subStatusOn)}` : sent };
    }
    if (quote.respondedOn) {
      return { label: 'Customer replied', tone: 'info', detail: `Replied ${day(quote.respondedOn)}` };
    }
    if (quote.isStale) {
      return { label: 'No reply', tone: 'warning', detail: sent };
    }
    return { label: 'Sent', tone: 'info', detail: ago(quote.daysSinceSent) || undefined };
  }
  if (code === 'DRAFT') {
    const unpriced = !quote.currencyId && Number(quote.totalAmount || 0) === 0;
    return { label: 'Draft', tone: 'neutral', detail: unpriced ? 'Not priced' : 'Not sent yet' };
  }
  return { label: quote.statusValue || '—', tone: 'neutral' };
}

export type QuoteMove = 'finish' | 'update' | 'po' | 'view' | 'revision';

export interface QuoteNextMove {
  move: QuoteMove;
  /** The row's one button. */
  label: string;
}

/**
 * The one thing to do next with a quote, as the row's single button (the list) and as the quote
 * page's filled button family. Replaces the list's eye "Open" + download + ⋮ (owner: "old school").
 *
 * - Draft → Finish (the quote page lists what is missing and holds Send)
 * - Sent, not replaced → Update status (customer step, replied, or how it ended, in one window)
 * - Won, no order yet → Enter PO
 * - Everything else (replaced, lost, expired, ordered, or no permission) → View
 */
export function quoteNextMove(
  quote: QuoteDTO,
  can: { edit: boolean; enterPo: boolean },
): QuoteNextMove {
  const code = quoteCode(quote);
  if (quote.supersededByQuoteNo) return { move: 'view', label: 'View' };
  // Its status can only move on the revision, once that is sent: finish the revision.
  if (quote.pendingRevisionId) return can.edit ? { move: 'revision', label: 'Finish revision' } : { move: 'view', label: 'View' };
  if (code === 'DRAFT' && can.edit) return { move: 'finish', label: 'Finish' };
  if (code === 'SENT' && can.edit) return { move: 'update', label: 'Update status' };
  if (code === 'ACCEPTED' && can.enterPo) return { move: 'po', label: 'Enter PO' };
  return { move: 'view', label: 'View' };
}
