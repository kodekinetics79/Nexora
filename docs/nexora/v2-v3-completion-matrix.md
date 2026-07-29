# Nexora V2/V3 Completion Matrix

Maturity: `M0` absent, `M1` UI/CRUD, `M2` workflow, `M3` evidence-based and automated, `M4` explainable optimization, `M5` policy-bounded autonomy.

Status: `Not started`, `In progress`, `Green`, `Blocked`.

| Gate | Capability | Baseline | Target | Status | Acceptance evidence |
|---|---|---:|---:|---|---|
| Track 0 | Durable evidence storage and malware scanning | M2 | M3 | Blocked | Repository fails closed; deployed `/ready` remains `503` until reachable S3-compatible storage and ClamAV configuration are supplied. |
| V2.1 | Commercial Autopilot and Exception Center | M2 | M3 | Green | Deterministic overdue-follow-up and unassigned-routing reconciliation; immutable lineage; one active case per source rule; decision events/outbox; dynamic source coverage; role scope; RLS; two-context concurrency; data-bearing upgrade; two real-backend browser scenarios. See `docs/nexora/releases/v2-gate-01-commercial-autopilot-exception-center.md`. |
| V2.2 | Opportunity Priority and Next-Best Action | M2 | M4 shadow | Green | Local deterministic, stage-aware shadow scoring; immutable evidence/recommendation/outcome/feedback; terminal-case exclusion; bounded reconciliation; owner/manager scope; RLS and lineage triggers. Accuracy remains explicitly unmeasured until an adequate later-outcome cohort exists. See `docs/nexora/releases/v2-gate-02-opportunity-priority-shadow.md`. |
| V2.3 | Predictive Pricing and Digital Twin | M2/M3 | M4 shadow | Blocked | Canonical offer/landed-cost authority, currency-safe totals, no derived-cost floor, governed application path and calibration required. |
| V2.4 | Supplier Bid Quality and Negotiation Intelligence | M3 | M4 shadow | Not started | Bid hygiene remains separate from fulfilment quality; authoritative delivery outcomes required before award influence. |
| V2.5 | Coaching, Customer Health and Revenue Recovery | M3 | M4 shadow | Not started | Quote/order opportunity indicators are separated from revenue claims until accounting outcomes reconcile. |
| V2.6 | Inventory and Demand Optimization | M3 | M4 shadow | Not started | Forecast horizon, service level, lead-time variance, MOQ/carrying cost and forecast-error evidence required. |
| V3.1 | Enterprise external identity foundation | M0 | M3 | Blocked | Separate portal trust domain, invitation, MFA/session, revocation, object authorization and audit contracts. |
| V3.2 | Customer and Supplier Collaboration Portals | M0 | M3 | Not started | Normal portal shell after V3.1; no platform-owner or tenant-console identity reuse. |
| V3.3 | Reusable Connectors and Embedded APIs | M2 | M3 | Not started | Signed callbacks, replay/freshness/idempotency, machine identity, versioning, quotas and tenant-negative HTTP tests. |
| V3.4 | Administration, customization, deployment and white label | M1/M2 | M3 | Not started | Dedicated platform key, fail-closed audit, CSP/HSTS, configuration/version governance and deployment evidence. |
| V3.5 | Trading, FMCG and Contracting editions | M0 | M3 | Not started | Separate domain contracts and representative acceptance corpora; no branding-only certification. |
| V3.6 | Mobile/PWA and Executive ROI | M1 | M3 | Not started | Read-only offline policy, update/error lifecycle, mobile accessibility and reconciled non-fabricated ROI source data. |

## Gate 1 Frozen Acceptance

- Canonical identity: `Lead.Id`; immutable linkage: `BusinessUnitId + CommercialCaseId + NexoraSerial`.
- Supported exception rules: `UnassignedLead` and `OverdueFollowUp`, version `commercial-exceptions-v1`.
- Deterministic recomputation, tenant-scoped deduplication, stale-version rejection, idempotent decisions, append-only events and atomic outbox.
- Individual users see only owned follow-up exceptions; managers see tenant-wide exceptions and can reconcile. HTTP authorization and PostgreSQL RLS both remain mandatory.
- `Acknowledge`, `Resolve`, and reason-required `Dismiss` govern the exception record only. They do not mutate source workflow state.
- Empty results and unavailable source data are distinct. KPI counts reconcile to active source records at one generated timestamp.

## Gate 2 Frozen Acceptance

- Opportunity recommendations are advisory shadow records and cannot mutate commercial workflow, pricing, inventory, awards, or lifecycle state.
- Evidence is local and deterministic. Ambiguous part names, raw stock quantity, mixed-currency value, and margin are excluded from the score.
- Reconciliation is manager-only, permission-gated, idempotent, cursor-bounded to 100 cases, and skips terminal Quote/Order cases while retaining later outcome observation for pre-terminal recommendations.
- Individual users see assigned recommendations only. Managers see the tenant cohort. PostgreSQL RLS and tenant-qualified immutable lineage remain mandatory.
- Accuracy is `null` and labelled not measured until sufficient later outcomes exist; no win-probability or revenue claim is made.
