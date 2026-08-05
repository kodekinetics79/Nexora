# Platform Admin 360 — Control Plane

## Scope and Result

This increment converts the Platform-Owner plane from a read-mostly console into a governed
control plane: tenant lifecycle, platform IAM, backend-enforced entitlements, consumption
metering with a rate-card billing trace, audited impersonation with revocation, and an
administration UI wired to real APIs. It does not add MFA/SSO, a payment provider, or a
separate admin SPA.

| Capability | Before | After | Certified behavior |
| --- | --- | --- | --- |
| Tenant lifecycle | Provision / suspend / resume | Full | Archive, restore, plan change; validated transitions; reason required; audited in-transaction |
| Platform IAM | No user management; no bootstrap | Full | Owner-only CRUD, role change, deactivate/reactivate, password reset; last-Owner and self-deactivation rails; fail-closed bootstrap seeder |
| Entitlement enforcement | Schema only, enforced nowhere | Enforced | Seats, documents/month, extraction concurrency, WFQ weight resolved from the tenant's plan server-side |
| Tenant suspension | Label only | Enforced | Login denied and API denied for Suspended/Archived tenants; queued extraction work excluded |
| Usage metering → billing | None | Traceable | Source ledgers → meters → rate card → statement lines → finalized statement, with duplicate-charge and immutability protection |
| Impersonation | Issue only | Governed | TTL clamped 5–60 min, session row per `jti`, revocation endpoint, sensitive-read deny-list, dual-actor audit, tenant-visible banner |
| Audit integrity | Fabricated `result` | Truthful | Real `Result` column, working failure filter, platform login success/failure audited, privileged mutations audited inside their transaction |
| Platform Admin UI | 6 read-mostly pages | 9 pages | Tenants, Plans, Users, Billing, Security, Audit, Pipeline, Overview, Tenant detail — all on real APIs |

## Data, API, and Security

- The platform plane keeps its own JWT scheme (`nexora-platform` audience, `scope=platform`),
  default-deny policies, and a separate signing key that is required to differ from the tenant
  key outside Development.
- Tenant-plane enforcement reads the platform plane through **column-level** grants: the tenant
  and identity database roles can read tenant status, plan entitlements, and impersonation
  liveness, and cannot read tenant names, slugs, status reasons, impersonation reasons, or
  operator emails.
- Finalized billing statements and their lines are immutable at the database layer via triggers
  (SQLSTATE 55000), matching the protection already carried by the AI ledger and the platform
  audit log. Statement identity is guarded by a unique `(TenantId, PeriodStartUtc)` index, so a
  retried computation cannot produce a second charge for a period.
- Billing meters read existing source ledgers; no usage events are double-written. Cost and
  margin reporting returns explicit nulls when provider spend is unpriced rather than
  fabricating a number.

## Migrations

- `20260805034514_PlatformAdmin360ControlPlane` — `PlatformAuditLogs.Result` (default
  `success`), `Plans.MonthlyPriceUsd`, `ImpersonationSessions`, and the billing schema
  (`RateCards`, `RateCardLines`, `BillingStatements`, `BillingStatementLines`) with the
  duplicate-charge unique index; re-issues platform-schema grants for the pipeline role and
  preserves the append-only audit revoke.
- `20260805105320_HardenPlatformGrantsAndBillingImmutability` — narrows the tenant/identity
  read surface from table-level to column-level and adds the finalized-statement immutability
  triggers. Written as a corrective migration rather than a rewrite because the first migration
  already had successors.
- EF Core `migrations has-pending-model-changes` reports no drift.

## Accepted Risks (security posture register)

These are deliberate, code-documented contracts, not oversights. They belong in the risk
register so they are accepted rather than discovered:

- **Suspension staleness ≤ 60 s** — tenant status and plan are cached per business unit;
  a suspension takes effect immediately on the node that processed it (cache eviction) and
  within 60 s fleet-wide.
- **Impersonation revocation staleness ≤ 30 s** — same-process revocation is immediate via
  cache eviction; cross-instance revocation is bounded by the 30 s session cache.
- **Enforcement fails open** when the platform plane is unreachable or a tenant predates the
  platform schema (legacy business unit). The branch logs a warning and increments
  `nexora.platform.tenant_access.fail_open`; alerting on that counter is required in production.
- **Impersonation restricts writes and sensitive reads, not all reads.** Ordinary tenant GETs
  remain readable during an impersonated session; file downloads, exports, and processing
  evidence are denied.
- **Per-page pricing is not yet billing-grade.** See below.

## Known Limitation — consumption metering coverage

Per-page and per-resource charging is the intended commercial model, and the metering,
rate-card, and statement machinery supports it. The underlying page signal is not yet complete
enough to price against:

- Text-layer PDFs, emails, and DOCX files record **0 pages**; spreadsheets record
  evidence-bearing worksheets; OCR page counts are capped at 10 per document.
- Supplier-quote uploads and the RFQ/customer/quotation/product/supplier template uploaders do
  not write an extraction run, so they contribute no pages.

Page meters therefore ship with an explicit coverage flag and must not be priced until
ingestion is instrumented to record a true page count at every door.

## Not Ready — invoicing

A finance review of the consumption vertical concluded that statements are a defensible
**usage calculation**, not yet an invoice. Before a real invoice is issued:

1. Decide the v1 billable scope. The recommendation is base subscription + documents +
   external AI tokens; page and storage meters stay read-only until instrumented.
2. Add a correction path — credit note, adjustment, or supersede — **before the first Final
   statement exists**, because Final rows are deliberately immutable in the database.
3. Add invoice identity: gapless numbering, bill-to legal entity, tax determination, issue and
   due dates, and AR linkage — or delegate all of it to a billing-of-record provider and feed
   it usage from these statements.
4. Freeze evidence at finalize: statement lines record quantities and prices, but not the
   source row identifiers behind them, so a disputed quantity cannot be re-derived from
   ledgers that have since moved.
5. Split segregation of duties: one policy currently gates rate-card pricing, statement
   computation, and finalization, so a single billing administrator can set a price and sign
   the statement that uses it.
6. Settle the unit definitions in writing (what counts as "a document", the seat basis, the
   storage basis) and make code and contract agree.
7. Run one full parallel month — meter everything, invoice nobody, reconcile to source
   ledgers and provider cost — before issuing.

## Verification

Recorded in the delivery report accompanying this increment: backend portable lane, PostgreSQL
16 lane (Testcontainers), focused workstream suites, frontend lint/build/unit, and the
PostgreSQL certification of the column-level grants and finalized-statement triggers.

No push, merge, deployment, production infrastructure change, or live-data access was
performed.
