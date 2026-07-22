# Unified Commercial Case Delivery Program

## Mandate

This program extends the sovereign RFQ delivery program with four non-negotiable product capabilities:

1. A permanent commercial-case identity that survives the complete lead-to-cash lifecycle.
2. Explainable customer matching, ownership, assignment, and unassigned-work management.
3. An exception-first RFQ workspace that presents the commercial position at a glance.
4. Governed tenant custom fields with server-side type, permission, rule, audit, and retention enforcement.

The requirements in this program are additive to `SOVEREIGN-RFQ-DELIVERY-PROGRAM.md`. Neither program can be certified independently because source evidence, commercial identity, routing, workflow, and audit share the same tenant and lifecycle boundaries.

## Executive Verdict

Current capability is a useful RFQ automation pilot with an operational `Lead -> RFQ -> Quote -> Order -> Shipment` chain, assignment, SLA aging, duplicate controls, notifications, quote revisions, extraction review, and tenant filtering.

Enterprise delivery remains a no-go until the P0 gates in this document and the sovereign evidence gates pass with automated evidence. A screen, DTO property, copied reference string, or passing frontend build is not acceptance evidence.

## Aggregate Model

The permanent reference belongs to `CommercialCase`, not to a mutable document:

```text
CommercialCase (MasterReference)
|- Lead and qualification
|- RFQs and revisions
|- Supplier requests and responses
|- Quotes and revisions
|- Follow-ups, tasks, and communications
|- Customer POs and award allocations
|- Sales orders
|- Delivery orders and shipments
|- Proforma, invoices, credits, and debits
|- Payments and receivable allocations
|- Attachments and source evidence
|- Custom-field records
|- Status, assignment, domain-event, and audit history
```

Every downstream entity must use a durable `CommercialCaseId` relationship. Displaying or exporting `MasterReference` is required, but copied text is not a substitute for the foreign key.

## Permanent Reference Invariants

- Generated server-side in the same logical lead-creation workflow.
- Unique within the tenant and protected by a database constraint.
- Immutable in application code and at the database boundary.
- Allocated by concurrency-safe, non-transactional sequencing so rolled-back, deleted, cancelled, duplicate, lost, or archived cases never release a number.
- Configurable by tenant using approved tokens for prefix, year/financial year, business unit, source, and sequence.
- Indexed, searchable, audit-captured, integration-ready, and suitable for QR/barcode rendering.
- Backfilled deterministically for existing leads with reconciliation evidence.

## Lifecycle Invariants

- Lead and RFQ states use named domain values, not controller-owned numeric literals.
- A single transition service validates the current state, target state, actor capability, required reason, expected version, and policy version.
- Every transition creates append-only history with tenant, case, actor/service, timestamp, before/after state, reason, source, correlation ID, and job/request ID.
- Terminal cases can only reopen through an authorized transition; history and prior outcome remain intact.
- Physical deletion of commercial cases or lifecycle documents is prohibited after issuance. Corrections use cancellation, supersession, reversal, credit/debit, or archival semantics.
- Optimistic concurrency rejects stale assignment, review, status, and custom-field commands with `409` and no partial writes.

## Customer Matching and Routing

Matching evidence is evaluated in descending authority:

1. Verified ERP/account/tax/registration identifier.
2. Verified contact email.
3. Approved customer-domain mapping.
4. Normalized phone.
5. Approved customer name or alias.
6. Historical inference that remains below verified evidence.

Automatic matching requires both a tenant threshold and sufficient margin over the next candidate. Ambiguous or unmatched leads enter the identification queue with retained evidence and suggested candidates.

Ownership resolution order is:

```text
Customer exception
-> Product-category ownership
-> Branch ownership
-> Territory ownership
-> Key-account team
-> General customer ownership
-> Active backup or delegate
-> Capacity or round-robin policy
-> Main unassigned queue
```

Manual assignment requires an explicit scope: lead only, customer permanent, customer temporary, branch, product category, or shared/backup. Ownership transfers default to future leads only. Moving existing open cases requires preview, explicit selection/scope, reason, permission, and audit; historical closed cases never move silently.

## Durable Unassigned Queue

An unassigned item is persisted and remains visible until resolved. It records queue type, reason, age, SLA due time, priority, customer candidates, owner suggestion, confidence, duplicate risk, required action, claim/lease state, and resolution.

Required operations include search, filtering, sorting, claim/release, single and bulk assignment, customer matching/creation, duplicate review, escalation, SLA warnings, and per-item conflict results. Queue operations are tenant-safe, optimistic-concurrency protected, and idempotent.

## RFQ Workspace Contract

Canonical route: `/procurement/rfqs/:rfqId`.

The first viewport contains:

- Sticky identity: master reference, RFQ number, customer/contact, owner, branch, source, stage, priority, and deadline.
- Exception strip: next action, blocker, SLA countdown, missing information, overdue tasks, and approval state.
- Commercial position: line coverage, inventory, sourcing, pricing, estimated value, authorized margin, currency, probability, and quote version/status.
- Supplier position: contacted/responded/pending, best price/lead time, expiries, missing coverage, and alternatives.
- Risk and follow-up position: extraction/product/customer confidence, validation failures, approvals, last contact, next follow-up, and overdue actions.

Detailed tabs are Overview, Line Items, Inventory, Supplier Sourcing, Pricing, Quotations, Communications, Follow-Ups and Tasks, Documents and Evidence, Approvals, Audit Timeline, and Custom Fields.

The backend exposes purpose-built, tenant-authorized read models rather than returning an unrestricted entity graph. Large lines and timelines use server-side cursor pagination and frontend virtualization. Margin, credit, pricing, sensitive fields, and actions are capability-filtered on the server.

## Global Search Contract

`GET /api/commercial-cases/search` searches master reference, RFQ, quote, customer PO, order, shipment, invoice, customer/contact identifiers, external references, and authorized line-item identifiers.

Results are tenant-, branch-, role-, and field-authorized before serialization. Every document result includes its master reference and a route to the relevant case workspace tab. Search never relies solely on matching copied reference strings.

## Governed Custom Fields

The model separates stable definition identity from immutable versions, options, conditional rules, dependency edges, entity records, typed values, and value history.

Required controls:

- Tenant and entity scoped stable keys with duplicate/similar-name checks.
- Reserved system names that cannot be shadowed or replaced.
- Plan/entity limits and capability-based administration.
- Type-safe value columns and server-side required/range/length/precision/option/reference validation.
- Field-level visibility and edit capabilities.
- Applicability by company, branch, customer, workflow, role, and effective date.
- Safe JSON rule AST; no client-supplied executable code.
- Dependency graph validation and circular-reference rejection before activation.
- Search/report indexes only where policy enables them.
- Versioned activation, retirement, migration, localization, import/export, and API/report exposure.
- No hard deletion after a definition has values or dependencies.

`LeadItem.ExtraFields` remains extraction evidence for unknown source columns; it is not the custom-field store.

## Domain Events and Audit

Business mutations create an append-only CRM audit event and transactional outbox event in the same transaction. Consumers handle notification, task, escalation, integration, search-index, and reporting side effects idempotently.

Each event carries tenant, branch, commercial case, entity, actor/service, event type, before/after data, reason, source, correlation ID, request/job ID, and idempotency key. Database roles prevent application updates or deletes of audit history.

## P0 Release Gates

| Gate | Objective Evidence |
|---|---|
| Reference concurrency | 1,000 concurrent creations, zero duplicates, retries return the same case, rollbacks never reuse allocated values. |
| Aggregate traceability | Every implemented downstream document joins directly to `CommercialCase`; complete history query uses relationships, not string matching. |
| Lifecycle enforcement | Invalid transitions return `409`; authorized reopen is historicized; no controller/repository can mutate state outside the transition service. |
| Assignment integrity | Routing precedence, threshold, expiry, backup, cross-tenant, concurrency, and idempotency tests pass. |
| Queue visibility | Every unmatched/unowned lead has one active durable work item until an audited resolution. |
| Custom-field governance | Type, required, permission, tenant, retirement, dependency, and cycle tests pass server-side. |
| Workspace reconciliation | Summary counts and deadlines reconcile to underlying records; restricted fields are absent for unauthorized users. |
| Audit/outbox integrity | Mutations and side effects are atomic, append-only, replayable, and deduplicated. |
| Database isolation | PostgreSQL RLS tests fail closed without tenant context and block cross-tenant raw SQL access. |
| SIT and recovery | Clean migration, production upgrade, rollback rehearsal, worker recovery, and DB/object restore complete successfully. |

## Delivery Increments

1. Commercial-case identity, reference policy/sequence, backfill, search-by-reference, and status history.
2. Sovereign corpus/document/page/region/field-evidence ledger linked to commercial cases.
3. Lifecycle transition service, optimistic concurrency, CRM audit, and transactional outbox.
4. Customer identifiers, ownership versions, routing decisions, assignment history, and durable unassigned queue.
5. Workspace summary/search APIs and exception-first RFQ overview.
6. Governed custom-field definitions, typed values, permissions, retirement, and conditional rules.
7. Direct case links across sourcing, quote, order, shipment, attachment, evidence, and current integration payloads.
8. Award-to-cash entities for partial awards, delivery orders, invoices/credits, payments, and allocations.
9. PostgreSQL RLS, object authorization, malware/quarantine, AI gateway, and append-only audit hardening.
10. Playwright, Testcontainers, load, security, accessibility, recovery, and deployment certification.

## Implementation Ledger

| Increment | Status | Acceptance State |
|---|---|---|
| Permanent reference foundation | Implemented | Immutable aggregate, global non-reuse sequence, tenant formatting, backfill, database triggers, status history, search/detail API, and tenant-isolation tests pass. Clean PostgreSQL 17 migration verified. The 1,000-worker concurrency certification remains a release gate. |
| Evidence-ledger domain | Foundation implemented | Corpus, source document, page, region, canonical inquiry/line, and field-evidence model, constraints, indexes, migration, and focused tests pass. Object storage, malware quarantine, and ingestion dual-write remain pending. |
| Customer routing domain | Operational increment implemented | Normalized identifier administration, customer ownership, deterministic matching, assignment ledger, durable queue, claim/release, single and bulk assignment, idempotency, optimistic versions, notifications, extraction integration, reconciliation worker, tenant-authorized APIs, and historical backfills pass. Capacity calendars, ownership-transfer preview, and full routing administration UI remain pending. |
| Governed custom fields | Operational increment implemented | Manager-governed definition/version activation and retirement, typed entity values, enforced conditional rules, options/dependencies and cycle validation, field-level view/edit capabilities, sensitive-field filtering, request-bound idempotency, optimistic concurrency, append-only history, tenant-aware database constraints, module-authorized APIs, limits, and migration backfills pass. Administration and entity-form UI, applicability scopes, localization, import/export, reporting, and aggregate-specific object authorization remain pending. |
| Lifecycle command service | Pending | Commercial-case aggregate prerequisite is available; transition policy, optimistic concurrency, audit, and outbox implementation is next. |
| Workspace and global search | Started | Tenant-authorized commercial-case search and lifecycle detail read model implemented; RFQ workspace summary, authorization shaping, UI, and line-item search remain pending. |
| Full traceability and award-to-cash | Pending | Requires staged migration and reconciliation plan. |
| Enterprise certification | Pending | Current verdict remains no-go. |

## Verification Record

Evidence captured for the current operational increment on 2026-07-21:

- Backend regression: 192 tests passing, including reference immutability, status history, routing, queue persistence/leases/bulk results, ambiguous matching, evidence, custom-field lifecycle/value governance, enforced conditional rules, incompatible-version rejection, sensitive-field capabilities, request-bound idempotency/concurrency, dependency cycles, search, and cross-tenant denial.
- Frontend production build: TypeScript and Vite build pass; the existing large-chunk advisory remains non-blocking technical debt.
- Database integration: all 15 migrations apply successfully from zero on PostgreSQL 17. A seeded upgrade rehearsal proved historical assignment-ledger creation, durable queue creation for accepted unowned leads, normalized customer/contact identifier backfill, duplicate soft-name matching, custom-field access/value-version backfills, and collision-free legacy history idempotency keys. The custom-field schema also certifies its retry-safe concurrent unique index, tenant-aware composite foreign keys, typed-value constraints, and eight append-only/no-delete governance triggers.
- Migration semantics: reference allocation rollback/non-reuse and post-rollback monotonic allocation verified against PostgreSQL.
- Source hygiene: `git diff --check` passes.

## Independent Acceptance Protocol

Every increment requires implementation evidence, automated tests, security/privacy review, product/RFQ acceptance, data/migration review, regression results, and an independent go/no-go verdict. Status is based on working code and measured behavior, never feature claims.
