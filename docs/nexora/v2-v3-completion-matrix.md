# Nexora V2/V3 Completion Matrix

Maturity: `M0` absent, `M1` UI/CRUD, `M2` workflow, `M3` evidence-based and automated, `M4` explainable optimization, `M5` policy-bounded autonomy.

Status: `Not started`, `In progress`, `Green`, `Blocked`.

| Gate | Capability | Baseline | Target | Status | Acceptance evidence |
|---|---|---:|---:|---|---|
| Track 0 | Durable evidence storage and malware scanning | M2 | M3 | Blocked | Repository fails closed; deployed `/ready` remains `503` until reachable S3-compatible storage and ClamAV configuration are supplied. |
| V2.1 | Commercial Autopilot and Exception Center | M2 | M3 | Green | Deterministic overdue-follow-up and unassigned-routing reconciliation; immutable lineage; one active case per source rule; decision events/outbox; dynamic source coverage; role scope; RLS; two-context concurrency; data-bearing upgrade; two real-backend browser scenarios. See `docs/nexora/releases/v2-gate-01-commercial-autopilot-exception-center.md`. |
| V2.2 | Opportunity Priority and Next-Best Action | M2 | M4 shadow | Green M3; M4 blocked | Local deterministic priority plus seven persisted, visible commercial components: single-currency value, evidenced win likelihood proxy, currency-qualified margin, urgency, customer quality proxy, persisted current-revision fulfilment confidence, and sourcing effort. The ECV contract requires complete evidence, remains currency-denominated and uncalibrated, is never compared across currencies, and never mutates workflow. Current Product cost has no authoritative currency, so the real resolver fails closed with no ECV. Immutable lineage/outcome/feedback, coherent Quote/Order cohort, terminal exclusion, bounded reconciliation, owner/manager scope, RLS, automated populated migration rehearsal, and authenticated desktop/mobile acceptance passed at M3. See `docs/nexora/releases/v2-gate-02-opportunity-priority-shadow.md`. |
| V2.3 | Predictive Pricing and Digital Twin | M2/M3 | M4 shadow | Green M3; M4 blocked | Nine local deterministic fulfilment scenarios, currency-safe totals, evidence-qualified pricing cohorts and governed application boundaries are visible in the normal RFQ workspace. Calibration, immutable run/outcome persistence, inventory cost currency, alternate approval and governed FX remain M4 blockers. See `docs/nexora/releases/v2-gate-03-pricing-digital-twin.md`. |
| V2.4 | Supplier Bid Quality and Negotiation Intelligence | M3 | M4 shadow | Green M3; M4 blocked | Deterministic bid quality, comparable Supplier cohorts, governed negotiation recommendations and append-only manager decisions are live in the normal Supplier Quote route. Calibrated response/outcome cohorts and autonomous communication policy remain M4 blockers. See `docs/nexora/releases/v2-gate-04-supplier-bid-negotiation.md`. |
| V2.5 | Coaching, Customer Health and Revenue Recovery | M3 | M4 shadow | Green M3; M4 blocked | Server-authoritative Sales Today coaching, Customer 360 health and recoverable commercial work use bounded tenant-scoped evidence, explicit completeness, append-only manager acknowledgement and normal authenticated routes. Calibrated coaching effectiveness, causal outcome attribution and policy-approved automation remain M4 blockers. See `docs/nexora/releases/v2-gate-05-sales-coaching-customer-health-recovery.md`. |
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
- Evidence is local and deterministic. Ambiguous or duplicate Product identifiers, raw stock quantity, mixed-currency value, and costs without authoritative currency are excluded from the score and Expected Commercial Value.
- Expected Commercial Value is `evidenced win likelihood × expected gross profit × customer quality × fulfilment confidence ÷ estimated sourcing effort`. It is present only when all inputs are available and explicitly labelled `shadow_unvalidated`; the current real resolver does not meet the margin input contract because Product cost has no currency authority.
- Seven separately visible components carry value, unit, sample size, confidence, status, evidence time, and plain-language evidence. Win likelihood and customer quality are identified as bounded evidence proxies, not calibrated predictions.
- Fulfilment confidence requires persisted resolution coverage for every Lead line and becomes unavailable when incomplete, unresolved, or older than 72 hours. Tenant-wide priority ordering never compares currency-denominated ECV values across currencies.
- Reconciliation is manager-only, permission-gated, idempotent, cursor-bounded to 100 cases, and skips terminal Quote/Order cases while retaining later outcome observation for pre-terminal recommendations.
- Individual users see assigned recommendations only. Managers see the tenant cohort. PostgreSQL RLS and tenant-qualified immutable lineage remain mandatory.
- Accuracy is `null` and labelled not measured until sufficient later outcomes exist; no win-probability or revenue claim is made.
- Gate 2 is accepted at M3 maturity. M4 shadow remains blocked until currency-qualified real-path cost and adequate calibration evidence exist. Production activation remains outside this gate, and automatic execution remains prohibited.

## Gate 5 Frozen Acceptance

- Coaching and recovery are deterministic, local and advisory. They cannot mutate Lead, RFQ, Quote, Order, pricing, sourcing, inventory or financial state.
- Managers may view the tenant cohort and append an evidence-bound acknowledgement; individual users see only their assigned cohort. Tenant identity comes only from the authenticated context and remains protected by EF filters and PostgreSQL RLS.
- Revenue recovery includes viable unquoted RFQs and open Quotes without governed follow-up. Terminal Quotes, non-positive demand, expired RFQs and unknown execution outcomes are excluded rather than guessed.
- Customer Health exposes RFQ trend, Quote coverage and decisions, conversion, accepted prices, margin evidence status, revision burden, follow-up effectiveness, commercial activity, opportunities and one explainable next action. Field and line revision denominators remain separate.
- Every bounded source discloses whether its cohort is complete. A source-limit hit produces `partial` data, never a silent complete result.
- Manager acknowledgements are append-only, optimistic and idempotent. Concurrent reuse of one key replays the same request and rejects a different request body.
- Gate 5 is accepted at M3 maturity. M4 remains blocked until coaching and recovery outcomes are sufficiently observed and calibrated; no causal revenue-lift claim or autonomous action is permitted.
