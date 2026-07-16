// ─── Plain-English humanizer ─────────────────────────────────────────────────
//
// Turns internal tool names (search_rfqs, dispatch_rfq_to_supplier, …) into
// calm, everyday phrases a non-technical buyer can read at a glance. Unknown
// tools still render nicely through a snake_case → "Title Case" fallback.
//
// `label`  → a short feed/timeline phrase, e.g. "Looked up RFQs"
// `action` → an imperative phrase for approval questions, e.g. "look up RFQs"
//            (reads naturally after "Nexora wants to …")

export interface HumanTool {
  /** A friendly emoji shown next to the phrase. */
  icon: string;
  /** Short phrase for activity feeds and timelines, e.g. "Looked up RFQs". */
  label: string;
  /** Imperative phrase for "Nexora wants to <action>", e.g. "look up RFQs". */
  action: string;
}

// Keys MUST match the backend agent tool names exactly (Agent/Tools + Agent/Guardrails/AgentToolNames).
const TOOLS: Record<string, HumanTool> = {
  // Read tools
  search_rfqs: { icon: '🔍', label: 'Looked up RFQs', action: 'look up your RFQs' },
  get_rfq: { icon: '📄', label: 'Opened an RFQ', action: 'open this RFQ' },
  search_suppliers: { icon: '🏭', label: 'Looked up suppliers', action: 'look up your suppliers' },
  search_leads: { icon: '📥', label: 'Looked up leads', action: 'look up your leads' },
  search_quotes: { icon: '💬', label: 'Looked up quotes', action: 'look up your quotes' },
  search_orders: { icon: '📦', label: 'Looked up orders', action: 'look up your orders' },
  get_dashboard_summary: { icon: '📊', label: 'Checked your dashboard', action: 'check your dashboard summary' },
  list_solicitations: { icon: '📋', label: 'Checked who was asked to quote', action: 'check which suppliers were asked to quote' },
  compare_supplier_quotes: { icon: '⚖️', label: 'Compared supplier quotes', action: 'compare the supplier quotes' },
  recommend_award: { icon: '⚖️', label: 'Compared quotes and recommended an award', action: 'compare the quotes and recommend who to award' },
  // Action tools (guardrailed)
  dispatch_rfq_to_supplier: { icon: '📤', label: 'Sent an RFQ to a supplier', action: 'send this RFQ to the supplier' },
  send_rfq_to_suppliers: { icon: '📤', label: 'Sent RFQs to suppliers', action: 'send this RFQ to the selected suppliers' },
  capture_supplier_quote: { icon: '💬', label: 'Recorded a supplier quote', action: "record this supplier's quote" },
  award_rfq: { icon: '🏆', label: 'Awarded the RFQ', action: 'award this RFQ to the chosen supplier' },
  create_order_from_quote: { icon: '🧾', label: 'Created an order', action: 'create an order from this quote' },
};

/** snake_case / kebab-case → "Title Case". */
function titleCase(raw: string): string {
  const cleaned = raw.replace(/[_-]+/g, ' ').replace(/\s+/g, ' ').trim();
  if (!cleaned) return 'Action';
  return cleaned.replace(/\b\w/g, (c) => c.toUpperCase());
}

/** Map any tool name (known or not) to a friendly, non-technical description. */
export function humanizeTool(name: string | undefined | null): HumanTool {
  const key = (name ?? '').toLowerCase().trim();
  const known = TOOLS[key];
  if (known) return known;
  const title = titleCase(name ?? '');
  return { icon: '🔧', label: title, action: title.toLowerCase() };
}

/** The full plain-English approval prompt for a queued action. */
export function approvalQuestion(name: string | undefined | null): string {
  return `Nexora wants to ${humanizeTool(name).action} — is that OK?`;
}
