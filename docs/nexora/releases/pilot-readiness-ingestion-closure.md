# Pilot Readiness: Ingestion Closure

Date: 2026-07-30

## Decision

- Application candidate: GO for an authorized staging pilot rehearsal.
- Production deployment: NO-GO until durable evidence storage and a separate reachable ClamAV service are configured and the existing production dead letters are reviewed.
- Live observations at certification time: Vercel 200, Render `/health` 200, Render `/ready` 503.

## Delivered

- Tenant Operations exposes extraction dead letters using failure categories rather than provider details or storage paths.
- Authorized operators can verify the immutable source, rerun malware scanning, and queue an idempotent retry without re-upload.
- Missing source, malware, integrity failure, scanner outage, storage outage, and retry-queued results remain distinct.
- Source-unavailable dispositions are append-only and stop presenting an impossible retry as active queue work. Integrity, malware, scanner, and storage failures continue to block readiness.
- Recovery events use tenant-qualified foreign keys, forced PostgreSQL RLS, least-privilege grants, and an append-only database trigger.
- Legacy dead letters without intake lineage reconstruct the corpus, batch, source-document, and occurrence linkage atomically before queueing, so the worker claim trigger can process the same stored source safely.

Normal click path: `Tenant Administration -> Operations -> Lead extraction exceptions -> Verify and retry`.

## Production Configuration

Required application variables:

```text
EvidenceStorage__Provider=S3
EvidenceStorage__ServiceUrl
EvidenceStorage__Region
EvidenceStorage__AccessKeyId
EvidenceStorage__SecretAccessKey
EvidenceStorage__Bucket
EvidenceStorage__ForcePathStyle
DocumentInspection__ClamAV__Host
DocumentInspection__ClamAV__Port
DocumentInspection__ClamAV__Timeout
DocumentInspection__ClamAV__ChunkBytes
DocumentInspection__ClamAV__MaximumResponseBytes
DocumentInspection__ClamAV__SignatureVersion
ConnectionStrings__DefaultConnection
ConnectionStrings__MigrationConnection
Database__ApplyMigrationsOnStartup
Database__AllowManagedOwnerRoleMigrationCompatibility
```

The runtime connection must remain least privilege. The migration connection must use the Neon owner/direct endpoint. Render must use `/ready` as its deployment health check only after S3 and ClamAV are reachable.

## Verification

- Portable backend: 999 passed, 0 failed, 0 skipped.
- PostgreSQL 16: 196 passed, 0 failed, 0 skipped.
- Focused dead-letter tests: 9 portable and 3 PostgreSQL passed, 0 failed, 0 skipped.
- Authenticated Playwright: 2 passed, 0 failed, 0 skipped against real HTTP, PostgreSQL RLS, S3-compatible storage, and ClamAV. The scenarios covered successful stored-source recovery without re-upload and terminal source-object-unavailable disposition.
- Populated PostgreSQL migration: applied through a separate owner connection; runtime role was denied migration-history access.
- Strict readiness: 200 with dependencies healthy; 503 independently when ClamAV or S3-compatible storage was stopped; restored to 200 when each dependency recovered.
- Ingestion acceptance: clean CSV reconciled, EICAR quarantined, exact duplicate produced one canonical Lead, scanner outage recovered from the same stored object without re-upload.
- Frontend lint and production build passed. Backend build passed. EF model drift passed. `git diff --check` passed.
- NuGet vulnerability scan: no known vulnerable packages.
- Independent review: all eight accepted P1 findings were fixed and retested, covering authorization, job-scoped idempotency, concurrency, legacy S3 paths, monotonic security outcomes, HTTP error semantics, runtime-role execution, and browser recovery coverage. No accepted P0/P1 application defect remains in this increment.

## Advisory Disposition

`react-router-dom` is pinned to 7.18.2. The npm advisory database still reports the high-severity RSC action CSRF advisory with no non-vulnerable released version. Nexora uses client-only `BrowserRouter` and does not configure React Router data actions, server actions, SSR, RSC, or the affected action request path. The advisory is therefore not reachable in the deployed architecture, but remains an open dependency watch item and must be upgraded when a patched release becomes available.

## Production Closure

1. Provide durable S3-compatible storage and separate ClamAV endpoints without weakening `/ready`.
2. Configure all required variables in Render and verify the runtime/migration role split.
3. Deploy this candidate to staging, apply `20260730104456_PilotReadinessDeadLetterOperations`, and repeat the authenticated browser and outage-recovery scenarios.
4. With explicit live-data authorization, classify the 22 existing production dead letters as recoverable or source unavailable and retry only verified clean sources.
5. Promote only when Render `/health` and `/ready` both return 200 and the tenant Operations page has no unresolved P0/P1 exception.
