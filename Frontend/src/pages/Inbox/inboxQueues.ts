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
  /** Where the whole queue lives, for "See all". */
  seeAllPath: string;
  seeAllLabel: string;
  /** Shown when this queue is genuinely at zero — what happened, not just "nothing here". */
  emptyTitle: string;
  emptyMessage: string;
  /** The button offered on the empty state, so a clear queue is never a dead end. */
  emptyAction: { label: string; path: string; moduleName?: string };
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
    seeAllLabel: 'Open inbound mail',
    emptyTitle: 'No message is waiting on a person',
    emptyMessage:
      'Every message the mailbox has taken either became an inquiry, joined one that already existed, or was decided and closed. A message appears here only if it stops.',
    emptyAction: { label: 'See inbound mail', path: '/procurement/leads/inbound-mail', moduleName: 'Leads' },
    errorFallback: 'Inbound mail could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'documents-to-check',
    title: 'Documents to check',
    purpose: 'A person has to confirm what was read out of these before they can be quoted.',
    moduleName: 'Leads',
    seeAllPath: '/procurement/extraction/review',
    seeAllLabel: 'Open the review queue',
    emptyTitle: 'Every document has been checked',
    emptyMessage:
      'Nothing is waiting on a person. New documents land here automatically as the mailbox and watched folders bring them in.',
    emptyAction: { label: 'Upload a document', path: '/procurement/leads/manual-upload', moduleName: 'Leads' },
    errorFallback: 'The review queue could not be loaded. Nothing has been checked or skipped — try again.',
  },
  {
    key: 'leads-to-own',
    title: 'Enquiries without an owner',
    purpose: 'Nobody is working these yet. Take one, or send it to the rep who should have it.',
    moduleName: 'Leads',
    seeAllPath: '/procurement/leads/outstanding',
    seeAllLabel: 'Open unassigned enquiries',
    emptyTitle: 'Every enquiry has an owner',
    emptyMessage:
      'Nothing is sitting unclaimed. Enquiries arrive here when routing cannot decide who they belong to.',
    emptyAction: { label: 'See all inquiries', path: '/procurement/leads/all', moduleName: 'Leads' },
    errorFallback: 'Unassigned enquiries could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'leads-to-decide',
    title: 'My enquiries awaiting a decision',
    purpose: 'Assigned enquiries stay here while fit, participation, or approved-line promotion still needs attention. Managers see their team’s assigned enquiries here.',
    moduleName: 'Leads',
    seeAllPath: '/procurement/leads/assigned',
    seeAllLabel: 'Open assigned enquiries',
    emptyTitle: 'No assigned enquiry is waiting for a decision',
    emptyMessage:
      'An assigned enquiry stays here until approved Bid lines are promoted to an RFQ, or a full no-bid decision closes it.',
    emptyAction: { label: 'See all inquiries', path: '/procurement/leads/all', moduleName: 'Leads' },
    errorFallback: 'Assigned enquiries awaiting a decision could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'rfqs-in-draft',
    title: 'RFQs still in draft',
    purpose: 'These have been qualified but not yet priced or sent out for sourcing.',
    moduleName: 'RFQ Management',
    seeAllPath: '/procurement/rfqs/draft',
    seeAllLabel: 'Open draft RFQs',
    emptyTitle: 'No RFQ is sitting in draft',
    emptyMessage:
      'A committed participation decision promotes approved Bid lines into a draft RFQ here for review.',
    emptyAction: { label: 'See all inquiries', path: '/procurement/leads/all', moduleName: 'Leads' },
    errorFallback: 'Draft RFQs could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'supplier-replies',
    title: 'Supplier replies to read',
    purpose: 'Suppliers have answered. Check the numbers and accept them onto the RFQ line.',
    moduleName: 'Supplier History',
    seeAllPath: '/procurement/supplier-quotes',
    seeAllLabel: 'Open the supplier quote inbox',
    emptyTitle: 'No supplier reply is waiting',
    emptyMessage:
      'Replies appear here once a supplier answers a request you sent. Send one from an RFQ that needs sourcing.',
    emptyAction: {
      label: 'RFQs needing sourcing',
      path: '/procurement/rfqs/all?state=requires-sourcing',
      moduleName: 'RFQ Management',
    },
    errorFallback: 'The supplier quote inbox could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'quotes-to-send',
    title: 'Quotes not yet sent',
    purpose: 'Priced or part-priced offers the customer has not seen.',
    moduleName: 'Quotations',
    seeAllPath: '/sales/quotes?state=draft',
    seeAllLabel: 'Open draft quotes',
    emptyTitle: 'No quote is waiting to go out',
    emptyMessage:
      'Every offer you have written has been sent. A quote drafted from an RFQ appears here until you send it.',
    emptyAction: { label: 'See sent quotes', path: '/sales/quotes?state=sent', moduleName: 'Quotations' },
    errorFallback: 'Draft quotes could not be loaded. No empty result has been assumed — try again.',
  },
  {
    key: 'client-pos',
    title: 'Customer orders to confirm',
    purpose: 'A customer has sent a purchase order against one of your quotes.',
    moduleName: 'Customer Awards',
    seeAllPath: '/sales/client-pos',
    seeAllLabel: 'Open the client PO inbox',
    emptyTitle: 'No customer purchase order is waiting',
    emptyMessage:
      'A PO lands here when a customer accepts a quote. Chase the quotes you have already sent to bring one in.',
    emptyAction: { label: 'Quotes due a follow-up', path: '/sales/quotes?state=follow-up', moduleName: 'Quotations' },
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
  /** Where the next action happens. */
  path: string;
  /** What the button says. A verb, always. */
  actionLabel: string;
  /** ISO date used only for ordering inside the queue; never rendered raw. */
  sortKey?: string | null;
}
