# Current Architecture

## Runtime Topology

- React 19, TypeScript, Vite, MUI, TanStack Query frontend under `Frontend/`; Vercel target `nexora1-ai.vercel.app`.
- ASP.NET Core 8 API and hosted workers under `Backend/ERP_RFQ_Automation/`; Render target `nexora-fyjw.onrender.com`.
- EF Core with Npgsql and PostgreSQL/Neon; PostgreSQL migrations include JSONB, sequences, triggers, grants, and row-level security.
- Evidence objects use the configured durable object store. Local disk is development-only and cannot satisfy production durability.

## Execution Trace

1. Upload/email ingestion enters a quarantine and inspection gateway, then creates an immutable source occurrence and idempotent extraction job.
2. Structured CSV/XLSX processing is deterministic and local. PDF, image, and legacy document processing uses local text/OCR first and governed LLM fallback.
3. Extraction produces logical inquiry candidates. The Release 01A reconciliation service classifies each as new, exact duplicate, revision, or possible match before routing; only a genuinely new inquiry creates a Lead and immutable Nexora Serial.
4. Lead qualification and conversion create an RFQ in a serializable transaction with inherited customer/contact identity, lifecycle event, and outbox record.
5. RFQ processing creates Quotes; quote send/outcome/order transitions use optimistic lifecycle commands and append-only events. Orders inherit the same commercial identity, which is exposed on invoice data.
6. Dashboard `release-01` computes one tenant/role-scoped snapshot with a shared cohort, timestamp, definitions, and complete drill-down identifiers. The legacy endpoint remains permission-gated but is not a certified KPI source.

## Release 01 Shared Contracts

## Release 01A Identity Contract

- `Lead.Id` remains the technical canonical key and `CommercialCaseReference` remains the Nexora Serial. No competing identifier was added.
- `LeadIngestionBatch` groups one intake session; `LeadIngestionOccurrence` records every logical arrival; `LeadRevision` and `LeadItemRevision` are immutable snapshots.
- `LeadOccurrenceDocument` is a tenant-qualified many-to-many bridge between logical occurrences and authoritative `source_documents`.
- Deterministic resolution order is trusted source identity, customer-scoped exact content, customer plus RFQ reference, local line fingerprint similarity, then human review. Unresolved similarity never auto-merges.
- Exact duplicates reuse the Lead/current revision and never route. Revisions reuse the Lead/serial, increment the revision, retain ownership and record RFQ/Quote/Order impacts without mutating committed artifacts.
- Runtime access combines claims-derived tenant scope, module permission attributes, EF filters, composite tenant foreign keys and PostgreSQL RLS using `nexora.business_unit_id`.
- Local loopback Ollama is the default unstructured provider. Any configured remote endpoint is classified external, requires a key and consent, and is blocked before invocation if its share of the rolling successful AI-request window would exceed 10%.

### Commercial Document Identity

Every KPI-eligible Lead, Customer RFQ, Quote, and Order carries this server-authoritative identity:

`BusinessUnitId + CommercialCaseId + NexoraSerial + CustomerId? + ContactId?`

Lead, RFQ, and Quote additionally carry `LifecycleVersion`; Order status versioning remains a separate future contract.

- `NexoraSerial` is the immutable display name for `CommercialCaseReference`.
- Downstream stages inherit identity server-side. Clients cannot independently pair an RFQ, customer, contact, or quote.
- A missing customer/contact is explicit `Unresolved`; it is never replaced with a synthetic customer.
- Existing unambiguous relationships are backfilled. Ambiguous legacy rows are excluded from certified KPIs and reported for review.
- Tenant-qualified composite keys and RLS are complementary requirements.

### Domain Ownership

- Lead: received, verified, review, qualified, disqualified, converted.
- RFQ: requirements validation, inventory resolution, sourcing, pricing readiness.
- Procurement: net-shortage resolution -> tenant-scoped supplier solicitation/outbox -> structured supplier quote revisions -> evidence-based comparison -> split award -> draft supplier PO -> controlled evidence-reference issue/incoming supply -> partial/final goods receipt -> immutable inventory movement.
- Quote: draft, approved, sent, follow-up, revision, won, lost, partial, expired, no-quote.
- Order and Invoice remain separate domains; Order stores inherited identity and Invoice reads it from Order without inventing another serial.
- Commercial Case state is a projection of append-only events, not another freely mutable workflow.

### Commercial Event Spine

Events are tenant-scoped, append-only, idempotent, correlation-aware, and linked to Commercial Case and source aggregate. Release 01 event types are:

`LEAD_RECEIVED`, `LEAD_VERIFIED`, `LEAD_REVIEW_REQUIRED`, `LEAD_ASSIGNED`, `LEAD_QUALIFIED`, `LEAD_DISQUALIFIED`, `RFQ_CREATED`, `QUOTE_READY`, `QUOTE_SENT`, `QUOTE_RESPONDED`, `QUOTE_WON`, `QUOTE_LOST`, `QUOTE_PARTIAL`, `NO_QUOTE`, `FOLLOW_UP_DUE`, `FOLLOW_UP_COMPLETED`, `ORDER_CREATED`.

Event writes and the applicable aggregate mutation/outbox record share one database transaction. Historical events cannot be updated or deleted by the runtime role.

### Dashboard Read Contract

Dashboard API version `release-01` returns one `generatedAt`, tenant-scoped filter window, role scope, KPI definition version, values with explicit insufficient-data states, and drill-down record identifiers. The dashboard does not combine independently refreshed endpoint totals into a funnel.

### Lead Intelligence

The processing order is deterministic parser and normalizer, local text/OCR, tenant-approved local model, then explicitly consented external provider. Each run records processing path, versions, duration, review result, correlation, Commercial Case where known, and evidence links. Provider failure or incomplete evidence routes to human review and is never presented as a verified no-match.

### Routing

Routing is deterministic and explainable. Candidate scoring uses measured open assignments, line-item load, SLA/deadline pressure, sourcing/review burden, follow-up burden, availability, and configured skills. Idempotency keys are bound to a request hash. Missing capacity evidence produces an ownership-based result without a false capacity claim.

## Deployment Boundaries

Production secrets remain environment-only. Render `/ready` validates database, durable object-store write/read capability, malware scanner reachability, extraction-worker freshness, and procurement-dispatch-worker freshness. This release task does not deploy or mutate live data.

## V2/V3 Shared Contracts

- `Lead.Id` remains the canonical inquiry identity. `CommercialCase` remains immutable lineage and an event projection; it does not become a competing workflow aggregate.
- A commercial exception is a derived, tenant-scoped coordination record anchored by `BusinessUnitId + CommercialCaseId + NexoraSerial`. It never replaces Lead, RFQ, Quote, Order, follow-up, routing, sourcing, pricing, or finance state.
- Gate 1 detection is deterministic and limited to authoritative overdue follow-up and unassigned routing sources. Unsupported or unavailable source signals are omitted and reported as unknown, never inferred as healthy.
- Exception decisions are optimistic, idempotent, append-only, and outboxed. Reconciliation may detect, refresh, reopen, or resolve a derived exception, but cannot execute authoritative commercial actions.
- Autopilot begins in `Observe` and `Recommend` modes. Pricing, awards, purchase-order issue, external communication, inventory commitment, and lifecycle outcomes remain validated human commands.
- Predictive features begin in shadow mode with persisted feature/evidence versions, sample size, confidence, outcome comparison, override, approval, rollback, and tenant-local learning.
- External portal identity is a separate trust domain from tenant and platform-owner authentication. No portal route is accepted before invitation, MFA/session, object authorization, revocation, and audit contracts are certified.
