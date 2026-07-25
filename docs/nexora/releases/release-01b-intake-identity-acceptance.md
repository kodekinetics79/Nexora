# Release 01B: Intake Identity and Acceptance-Gate Closure

## Verdict

**NO-GO (2026-07-25).** No push, merge, deployment, live-data access, or production-infrastructure change occurred. The implementation remains an uncommitted working-tree increment on `release/release-01a-bulk-lead-identity` above preserved commit `cfed985`; baseline commit `9115b70` was not amended or rewritten.

## Identity Decision

The prior Release 01A P0 is withdrawn. `SourceDocumentOccurrence` already provides the durable pre-extraction identity and is committed atomically with immutable evidence and the extraction job before workers can claim it. Canonical `Lead.Id` and Nexora Serial allocation correctly remains post-reconciliation. Release 01B makes the source occurrence explicit on the extraction job, Lead reconciliation occurrence, and AI request ledger.

## Implemented Evidence

- Every governed receipt has its own source occurrence and extraction job, including repeated identical content. Content storage remains deduplicated; canonical Lead reconciliation remains downstream.
- Intake lifecycle states cover accepted, queued, processing, retryable, review-required, resolved, rejected, and dead-letter outcomes. PostgreSQL prevents a job entering `Leased` without its queued durable occurrence.
- One source occurrence may produce multiple logical inquiries from a split workbook. Possible matches complete without a false result Lead ID and leave intake in `ReviewRequired`.
- Email body and attachment receipts retain a shared logical group key. Deterministic cross-document grouping decisions remain an open P1 gate.
- AI request records retain provider class, extraction job, source occurrence, token source, and explicit unpriced cost status. Authoritative OCR pricing and canonical post-resolution cost roll-up remain open P1 gates.
- Customer and Contact ownership is tenant-qualified. Populated migration rejects unowned rows, backfills Contact ownership only from real Customer/Supplier parents, installs direct Contact RLS, tenant-qualified email uniqueness, restricted customer/contact/Lead foreign keys, and parent-tenant validation.
- Signed-JWT HTTP tests exercise the real application, permission handler, tenant context, runtime PostgreSQL role, and RLS for `401`, missing-tenant `403`, denied-role `403`, own-tenant `200`, cross-tenant `404`, and forged-query isolation.
- Dashboard ingestion volume counts durable intake occurrences; Leads Received counts distinct canonical new Leads. Duplicate and revision occurrences do not increase the canonical Lead count.
- Playwright provides 27 desktop/mobile scenarios. Acceptance mode now fails immediately when any mandatory credential or fixture is absent; it cannot silently pass through skipped configuration.

## Migration Evidence

Migration `20260725022734_Release01BIntakeIdentityAcceptance` applies to empty PostgreSQL 16 and to representative populated Release 01A data. The populated rehearsal upgrades, downgrades to `20260724230121_Release01OrderLineage`, and re-upgrades while preserving the Lead ID, Nexora Serial, one Revision 1, Contact tenant, source occurrence ID, extraction-job bridge, and resolved intake status. Shared-database downgrade is not approved; production rollback remains restore-to-new-database or a reviewed forward correction.

## Verification

- Backend build: passed, 0 errors; existing compatibility/nullability warnings remain.
- Portable backend lane: **528 passed, 0 failed, 0 skipped**.
- PostgreSQL 16 lane: **65 passed, 0 failed, 0 skipped**.
- Authenticated HTTP/PostgreSQL focused lane: **6 passed**.
- Populated downgrade/re-upgrade rehearsal: **1 passed**.
- EF pending-model check: no drift.
- Frontend lint and production build: passed; existing large-chunk warning remains.
- Playwright discovery: **27 tests in 5 files**. Acceptance-mode missing-configuration check exits nonzero as required.
- Local reconciliation benchmark: **110 occurrences** (`50 New`, `25 ExactDuplicate`, `25 Revision`, `10 PossibleMatchReviewRequired`), **p50 28.82 ms**, **p95 68.14 ms**, **0 external calls**, and **0 external cost** on the synthetic portable fixture.
- NuGet vulnerability scan: no known vulnerable packages.
- Production npm audit: two high React Router RSC advisory nodes. The current Vite CSR application has no RSC/server-action runtime; forced breaking downgrade was not applied.
- `git diff --check`: passed.

## Open Gates

- **P0 deployment gate:** authenticated browser SIT did not execute because authorized manager/editor/denied-role credentials, disposable fixture IDs, and an upload fixture were not configured.
- **P1:** deterministic multi-document grouping and uncertain-group review are metadata-wired but not certified end to end.
- **P1:** HTTP plus runtime-RLS coverage does not yet include evidence reads, assignment mutations, reconciliation decisions, and Dashboard drill-down routes.
- **P1:** authoritative OCR/model pricing and occurrence-to-canonical cost roll-up are incomplete; unpriced usage is explicit rather than reported as zero cost.
- **P1:** retry/dead-letter intake reconciliation is recoverable in the worker path but is not atomic with every queue transition, including lease-expiry dead-lettering.
- **P1:** callers that omit stable source and batch identifiers can fall back to generated identity inputs, so replay idempotency is not certified for those integration paths.
- **P1:** rejected/quarantined receipts retain evidence but do not yet persist every rejection reason in the new intake lifecycle fields.
- **P1:** KPI rate cohorts need one intake-time/as-of contract before delayed reconciliation can be certified.
- **P1:** supplier-side Contact reassignment audit and composite supplier foreign-key coverage remain incomplete.

## Recommendation

Do not merge or deploy Release 01B. Preserve the increment, configure an authorized disposable acceptance stack, execute all 27 Playwright scenarios without skips, and close or formally accept the remaining P1 gates before requesting deployment authorization.
