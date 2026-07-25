# Release 01A: Bulk Lead Identity and Revision Intelligence

## Verdict

**NO-GO.** The implementation is preserved on `release/release-01a-bulk-lead-identity`; no push, merge, deployment, live-data access or production-infrastructure change occurred.

## Implemented Contract

- Preserves `Lead.Id` and the tenant-unique `CommercialCaseReference` Nexora Serial.
- Adds tenant-scoped batches, occurrences, immutable Lead/item revisions, structured differences, possible-match candidates, identity audit events, downstream impacts and source-document links.
- Classifies trusted resends as exact duplicates, customer/RFQ content changes as revisions, unresolved similarity as possible-match review, and unmatched inquiries as new.
- Exact duplicates and revisions do not route as new opportunities. Quote revisions retain commercial identity.
- Batch reconciliation, revision timeline, candidate evidence, downstream impact and governed review decisions are available in the React UI.
- Structured extraction is deterministic. Unstructured extraction records local versus external provider class; local loopback is default and external dependency is fail-closed above 10%.

## Database Evidence

Migrations `20260725003207_Release01ACanonicalLeadIdentity`, `20260725004658_Release01ASourceDocumentLineage`, and `20260725010019_Release01AAiProviderClassification` add composite tenant foreign keys, indexes, PostgreSQL RLS for `nexora_tenant_app`, usage-only sequence grants, append-only triggers, provider classification and conservative Revision 1 backfill. A disposable PostgreSQL 16 rehearsal upgrades populated Release 01 data, downgrades to `20260724230121_Release01OrderLineage`, and re-upgrades without changing the Lead ID, Nexora Serial, or creating a second Revision 1. Shared or production databases were not accessed.

## Independent Review Disposition

- Fixed P0: template uploads now enter the governed ingestion gateway instead of creating Leads directly.
- Fixed P0: logical inquiries split from one source use distinct source keys and cannot collapse into one occurrence.
- Fixed P0: revision promotion updates both the Lead header and current LeadItems while retaining immutable historical snapshots.
- Fixed P1: exact lookup is global and indexed, PostgreSQL reconciliation is serialized per identity key, review decisions are transactional/idempotent, source-document provenance is append-only, external AI is capped before invocation, and sequence grants are usage-only.
- Formally open: pre-extraction canonical identity, automatic multi-document grouping, authoritative occurrence-to-AI-cost linkage, and authenticated HTTP plus browser acceptance.

## Verification Snapshot

- Portable backend lane: 518 passed, 0 failed, 0 skipped.
- PostgreSQL 16 lane: 58 passed, 0 failed, 0 skipped. This includes empty/populated migration, downgrade/re-upgrade, runtime RLS, append-only provenance, and concurrent duplicate reconciliation.
- Backend build: passed with 0 errors and four compatibility warnings for legacy `OpenXmlPowerTools` and `System.Management.Automation.dll` packages.
- EF model drift: none. Frontend lint and production build: passed. `git diff --check`: passed.
- NuGet vulnerability scan: no known vulnerable packages. Production npm audit: two high React Router RSC advisories; the Vite CSR application has no RSC/server-action execution path, so reachability is not established. A forced breaking downgrade was not applied.
- Synthetic local classification benchmark: 110 occurrences; New 50, Exact Duplicate 25, Revision 25, Possible Match 10; 22.88 ms p50, 60.93 ms p95; 456,869,528 aggregate allocated bytes; zero external calls and zero external cost. This is a classification benchmark, not an extraction benchmark.

## Browser Acceptance

Authenticated browser SIT did not run because the in-app browser could not initialize with the available sandbox metadata. Upload/batch navigation, possible-match decisions, revision comparison, role denial, KPI reconciliation, error states, and responsive layout therefore remain unaccepted deployment gates.

## Open Acceptance Blockers

- P0: a genuinely new Lead ID and Nexora Serial are still created after extraction, not before asynchronous extraction begins.
- P1: automatic grouping of multiple source documents or email attachments into one logical inquiry is not proven end to end.
- P1: authenticated browser SIT for upload, match review, revision comparison, role denial, KPI reconciliation and responsive behavior remains a deployment gate.
- P1: authenticated TestServer plus PostgreSQL coverage does not yet prove HTTP authorization and runtime-role RLS together for every new endpoint.
- P1: provider class is measured and capped, but ingestion occurrences are not linked to authoritative AI request cost records; the zero-cost benchmark covers local classification only.
- Release 01 customer/contact composite-FK and broader Dashboard completeness blockers remain inherited and are not widened by this slice.

## Rollback

Production rollback is restore-to-new-database or a reviewed forward corrective migration. Do not downgrade a shared database. Retain the Release 01A history tables whenever application code may have written occurrences or revisions.
