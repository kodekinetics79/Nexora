/**
 * What the Inbox asks for, and in what order.
 *
 * The queue is deliberately a straight line down the commercial spine — work arrives, becomes an
 * enquiry, becomes a request we can price, becomes an offer, becomes an order. A rep reading top
 * to bottom is reading their own process, so "what do I do next" is answered by position rather
 * than by a score nobody can audit. There is no weighting model here on purpose: Opportunity
 * Priority is a hand-tuned heuristic whose accuracy has never been measured, and a landing screen
 * is the last place to present an unmeasured ranking as an instruction.
 *
 * Every queue below is an EXISTING endpoint that an existing screen already reads. The Inbox adds
 * no server surface; it removes the navigation between them.
 */

export type QueueKey =
  | 'mail-to-rescue'
  | 'documents-to-check'
  | 'leads-to-own'
  | 'leads-to-decide'
  | 'rfqs-in-draft'
  | 'supplier-replies'
  | 'quotes-to-send'
  | 'client-pos';

export interface QueueDefinition {
  key: QueueKey;
  /** What is waiting, in the rep's words. Never a status code. */
  title: string;
  /** Why these are here and what doing one accomplishes. */
  purpose: string;
  /** Permission module that must be granted for this queue to be asked for at all. */
  moduleName: string;
  /** Where the whole queue lives: "See all", and where a clear queue's name goes. */
  seeAllPath: string;
  /** Domain wording when the request fails and the server says nothing renderable. */
  errorFallback: string;
}

/** How many rows of each queue the Inbox shows before deferring to "See all". */
export const INBOX_PREVIEW_ROWS = 5;

export const INBOX_QUEUES: readonly QueueDefinition[] = [
  {
    // FIRST, because it is the first thing that happens: mail arrives. Everything below this is
    // work that already became something, and a message that stopped never did — it has no lead,
    // so `documents-to-check` (GET /api/Lead/needs-review) cannot show it and neither could any
    // other queue here. Until this existed the landing screen said "You are clear." over stranded
    // inbound mail, and the only screen that would have shown it is one the reader had just been
    // told there was no reason to open.
    key: 'mail-to-rescue',
    title: 'Mail that needs a person',
    purpose: 'These messages arrived and produced no inquiry. Nothing moves them until somebody looks.',
    moduleName: 'Leads',
    seeAllPath: '/procurement/leads/inbound-mail',
    errorFallback: 'Inbound mail could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'documents-to-check',
    title: 'Documents to check',
    purpose: 'A person has to confirm what was read out of these before they can be quoted.',
    moduleName: 'Leads',
    seeAllPath: '/procurement/extraction/review',
    errorFallback: 'The review queue could not be loaded. Nothing has been checked or skipped — try again.',
  },
  {
    key: 'leads-to-own',
    title: 'Enquiries without an owner',
    purpose: 'Nobody is working these yet. Take one, or send it to the rep who should have it.',
    moduleName: 'Leads',
    seeAllPath: '/procurement/leads/outstanding',
    errorFallback: 'Unassigned enquiries could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'leads-to-decide',
    title: 'Enquiries to decide',
    purpose: 'Assigned enquiries stay here while fit, participation, or approved-line promotion still needs attention. Managers see their team’s assigned enquiries here.',
    moduleName: 'Leads',
    seeAllPath: '/procurement/leads/assigned',
    errorFallback: 'Assigned enquiries awaiting a decision could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'rfqs-in-draft',
    title: 'RFQs still in draft',
    purpose: 'These have been qualified but not yet priced or sent out for sourcing.',
    moduleName: 'RFQ Management',
    seeAllPath: '/procurement/rfqs/draft',
    errorFallback: 'Draft RFQs could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'supplier-replies',
    title: 'Supplier replies to read',
    purpose: 'Suppliers have answered. Check the numbers and accept them onto the RFQ line.',
    moduleName: 'Supplier History',
    seeAllPath: '/procurement/supplier-quotes',
    errorFallback: 'The supplier quote inbox could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'quotes-to-send',
    title: 'Quotes not yet sent',
    purpose: 'Priced or part-priced offers the customer has not seen.',
    moduleName: 'Quotations',
    seeAllPath: '/sales/quotes?state=draft',
    errorFallback: 'Draft quotes could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'client-pos',
    title: 'Customer orders to confirm',
    purpose: 'A customer has sent a purchase order against one of your quotes.',
    moduleName: 'Customer Awards',
    seeAllPath: '/sales/client-pos',
    errorFallback: 'Client purchase orders could not be loaded. No empty result has been assumed — try again.',
  },
];

/** One row of work, normalised so the Inbox renders every queue the same way. */
export interface InboxItem {
  /** Unique within its queue. */
  id: string | number;
  /** The reference a person would say out loud — an RFQ number, a quote number, a PO number. */
  reference: string;
  /** Who it is for or from. */
  party: string;
  /** One extra fact that decides urgency — a deadline, an age, a line count. */
  detail?: string;
  /** Colours the detail when it is a deadline: already past, or today/tomorrow. */
  tone?: 'late' | 'soon';
  /** Where the next action happens. */
  path: string;
  /** What the button says. A verb, always. */
  actionLabel: string;
  /** ISO date used only for ordering inside the queue; never rendered raw. */
  sortKey?: string | null;
}
