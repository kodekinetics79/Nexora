# Nexora V1 Completion Matrix

Maturity: `M0` absent, `M1` UI/CRUD, `M2` workflow, `M3` evidence-based and automated,
`M4` explainable optimization, `M5` policy-bounded autonomy.

| Capability | Before | V1 target | Evidence / gap | Implementation decision | Acceptance |
|---|---:|---:|---|---|---|
| Role-based Today | M2 | M3 | `CommercialIntelligenceController.SalesToday`; owner scope was incomplete | Scope individual work to authenticated user; managers retain team control surfaces | Gate 1 passed: individual/manager HTTP scope plus actionable Sales Rep, Sales Manager, Sourcing, Inventory, Executive and tenant-admin Today workspaces |
| Customer 360 | M1 | M3 | Customer detail showed addresses only; commercial memory/context already persisted | Compose contacts, ownership, RFQs, Quotes, Orders, pricing and outcomes without duplicating CRM entities | Gate 1 passed: tenant-qualified ownership, active work, contacts, RFQ demand, direct RFQ/Quote/Order drill-downs, shared canonical outcomes, order-backed sold evidence, follow-ups, reasons, health and next action |
| Opportunity workspace/search | M2 | M3 | `CommercialCaseQueryService` and normal workspace route exist; downstream sourcing/handoff coverage incomplete | Extend the existing case projection and relationship search | Gate 1 passed: relationship explanations, full document trail, RFQ lines, ATP, sourcing, ownership, readiness, memory and evidence |
| RFQ readiness / no-quote recovery | M2 | M3 | Line resolution, current ATP, approved awards and canonical Supplier Quote evidence are reconciled | Deterministic evidence first; Quote Draft blocked until customer, Lead, Nexora Serial, line/UOM and selected fulfilment are complete | Gate 2 passed: evidence links, confidence, user action, SLA/no-quote state and award-backed readiness |
| Product commercial memory | M3 | M3/M4 | Persisted RFQ, Quote, explicit outcome and customer-award Order facts aggregate by product/currency | Preserve M3; expose conversion only from decided evidence and retain currency separation | Gate 2 passed: order-backed wins, last-won context, losses, sample sizes and drill-down evidence |
| Supplier bid quality | M2 | M3 | Supplier Quote evidence, review decisions, projections, awards and customer Orders are joined | Deterministic completeness, freshness, availability, terms, alternate, volatility and outlier findings | Gate 2 passed: reviewed corrections project safely; current eligibility shares governed Supplier and canonical-Quote rules |
| Sales Rep coaching/fairness | M2 | M3 | Effective-dated ownership, contributions, actions, follow-ups and outcomes are persisted | Separate owner/contributor credit; suppress conversion below five decisions and value conversion across currencies | Gate 2 passed: weighted coverage, action/Quote timing, follow-up, insight and evidence-backed coaching |
| Inventory demand / stocking | M3 | M3/M4 | Observed, qualified, quoted, weighted, committed and fulfilled demand are available | Require decided/won, recurring demand and verified lead-time samples; retain review-only stocking decisions | Gate 2 passed: four-factor threshold and plain evidence insufficiency explanation |
| Opportunity Digital Twin | M0 | M3 | Current ATP, approved awards, governed Supplier offers, currencies and validity are reconciled | Read-only deterministic scenarios; missing margin/risk/alternate approval remains explicitly ineligible | Gate 2 passed: normal RFQ view, assumptions, evidence, validity and no autonomous commitment |
| Integration status / sync health | M1 | M3 | Outbox, health checks and handoff sync exist; no unified tenant/admin view | Persisted status projection and authorized replay only | Gate 3 pending |
| Operational visibility | M2 | M3 | Procurement handoff external observations are non-authoritative | Surface source, freshness and authority; never infer fulfilment | Gate 3 pending |
| Local-first AI governance | M3 | M3 | AI reservation/cost ledger and governed extraction exist | Close visibility and direct-provider-call gaps; external remains disabled by default | Gate 4 pending |
| Outcome Learning Studio | M2 | M3 | Commercial learning studio endpoint exists; approval/disable visibility incomplete | Review/approval surface over governed tenant memory | Gate 4 pending |
| Performance / production certification | M2 | M3 | Existing benchmark and release evidence under `docs/nexora/evidence/` | Preserve baselines; record query, browser, security and migration evidence | Gate 5 pending |

## CRUD Ownership

| Module/data | CRUD decision | Authority rule |
|---|---|---|
| Customers, Contacts, Suppliers, Products, Warehouses, currencies and tenant setup | Governed CRUD | Permission checked, tenant scoped, validated and audited; referenced records deactivate instead of destructive deletion |
| Users, roles and permissions | Governed admin CRUD | Manager/platform authorization and tenant boundaries are mandatory |
| Leads, RFQs, Supplier RFQs, Supplier Quotes, Customer Quotes, Client POs, Orders and handoffs | Workflow commands, not generic CRUD | Server-owned identity, lifecycle, idempotency and optimistic concurrency |
| Inventory balances, reservations, receipts and movements | Authoritative commands / integration sync | No arbitrary balance editing; ATP and movements remain server-authoritative |
| Commercial events, ingestion occurrences, revisions, evidence and AI usage | Append-only | No update/delete UI or generic mutation endpoint |
| Commercial memory, KPIs, recommendations, Digital Twin and sync health | Read/review/override | Derived from persisted evidence; overrides create auditable decisions and never rewrite source facts |

Frozen checkpoints: `ed98590`, `8fe156c`, `e7036e4`, `78e4ddf`.
