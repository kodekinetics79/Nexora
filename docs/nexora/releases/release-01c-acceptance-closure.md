# Release 01C Acceptance Closure and Transactional Intake Hardening

## Status

**NO-GO (2026-07-25).** Release 01C backend, PostgreSQL, migration, fixture-browser, build, and lint gates pass. Deployment remains blocked because the 27 authenticated browser scenarios have not run against the real ASP.NET/PostgreSQL acceptance stack, and authoritative OCR usage/cost accounting is incomplete. No push, merge, deployment, production credential, live-data, or production-infrastructure action occurred.

## Diff and migration audit

The reported 15,838 deleted lines is not supported by Git. Against protected commit `9115b70`, the working tree is 77 files with 50,968 insertions and 316 deletions. Against `cfed985`, it is 51 files with 1,546 insertions and 249 deletions. No files are deleted. The apparent scale is dominated by generated EF designer metadata of roughly 15,000 lines per migration. Historical migrations from `9115b70` and `cfed985` were not rewritten or deleted.

Release 01C adds:

- `20260725035352_Release01CTransactionalIntakeHardening`
- `20260725041211_Release01CTenantContactMetadata`

The migrations add structured occurrence errors, extraction/OCR cost status, atomic PostgreSQL job-to-occurrence transitions, guarded historical cost backfill, exactly-one Contact parent, and an EF-modeled tenant-qualified Supplier/Contact key. The second migration refuses Suppliers without tenant ownership before making `Suppliers.BUID` mandatory.

## Closed implementation gates

- Intake occurrence, extraction job, and reconciliation transitions are fenced and transactional on PostgreSQL; portable-provider transitions preserve equivalent behavior.
- Retry identity is stable only when a durable source identity is supplied. Separate identical manual receipts without one now remain separate occurrences.
- Multi-document correlation requires corroborating customer/RFQ identity plus similarity; ambiguous submissions create Possible Match Review candidates.
- Rejected occurrences persist category, code, and JSON details.
- Extraction runs persist processing and OCR cost status; historical rows are explicitly `HistoricalUnpriced` / `HistoricalUnknown`.
- Dashboard identity KPIs use intake-time cohorts, distinct occurrence numerators, one PostgreSQL cohort query, and an explicit `asOf` boundary.
- Contact ownership is represented in EF and PostgreSQL with tenant-qualified Customer and Supplier relationships.
- Critical HTTP tests use signed JWTs, the real application host, PostgreSQL runtime role, permissions, tenant context, and RLS.
- CI remote acceptance now sets `E2E_FIXTURE_MODE=false` and runs the zero-skip `e2e:acceptance` command.

## Verification evidence

- Backend Release build: passed, 0 errors; 175 existing compiler/package compatibility warnings.
- Portable backend: **534 passed, 0 failed, 0 skipped**.
- PostgreSQL 16: **77 passed, 0 failed, 0 skipped**.
- Focused authenticated HTTP/RLS: **12 passed**.
- EF model drift: no pending model changes.
- Frontend lint: passed with zero warnings.
- Frontend production build: passed; existing large-chunk advisory remains.
- Browser fixture contract: **27 passed, 0 failed, 0 skipped** in 1.4 minutes, one worker, no retries, desktop and mobile.
- `git diff --check`: passed.
- NuGet vulnerability scan: no vulnerable packages.
- npm production audit: two high findings from the same React Router RSC CSRF advisory. The app is a client-rendered Vite SPA and has no RSC/action endpoint, so the vulnerable path is unreachable; forced downgrade is not accepted without regression testing.
- Structured local benchmark: 10,000 rows, five runs, p50 176.50 ms, p95 204.66 ms, 360,589,888 allocated bytes, zero external calls.
- Identity benchmark: 110 occurrences (`50 New`, `25 ExactDuplicate`, `25 Revision`, `10 PossibleMatchReviewRequired`), p50 25.10 ms, p95 70.85 ms, 433,285,232 allocated bytes, zero external calls and cost.

## Migration and restore rehearsal

A disposable PostgreSQL 16 database applied all **49** migrations, including both Release 01C migrations. The populated automated test upgraded Release 01B data, backfilled historical extraction cost status, downgraded to Release 01B, and re-upgraded while preserving Lead identity, Nexora Serial, revision history, Contact tenant, occurrence/job linkage, and the new composite FK.

Separately, `pg_dump -Fc` backed up the migrated database and `pg_restore` restored it into a fresh database. The representative marker row, all 49 migration-history rows, latest migration ID, and tenant-qualified Supplier/Contact FK were verified. Re-upgrade reported the restored database current. PostgreSQL 18 client against server 16 emitted one ignored `transaction_timeout` setting warning; no data or schema check failed. Disposable resources were removed.

## Open blockers

- **P0:** the 27/27 browser result uses a loopback fixture with synthetic auth and API responses. Real-backend authenticated Playwright remains unexecuted and is a deployment gate.
- **P1:** the fixture upload response does not prove backend ingestion, idempotent retry, or fallback behavior; backend/PostgreSQL tests cover those independently, not end to end in a browser.
- **P1:** OCR execution lacks a dedicated usage ledger with page/unit count, rate version, and operation-to-job linkage. `LocalNoCharge` is not sufficient proof that OCR did or did not run.
- **P1:** unavailable external rates still aggregate through the legacy decimal occurrence cost field; extraction-run status preserves `RateUnavailable`, but occurrence-level unknown versus zero is not fully modeled.
- **P1:** possible-group review is represented through occurrence/group correlation and match candidates, not a dedicated immutable grouping-decision aggregate.
- **P1:** evidence hash/integrity failures fail closed and log, but do not yet persist a dedicated security-incident lifecycle event.
- **P1:** critical HTTP/RLS coverage does not yet include every assignment mutation and every possible-match decision mutation.

## Exact commands

```bash
dotnet build Backend/ERP_RFQ_Automation.sln --configuration Release --no-restore
dotnet test Backend/ERP_RFQ_Automation.Tests/ERP_RFQ_Automation.Tests.csproj --configuration Release --filter 'Category!=PostgreSQL' --no-build --logger 'console;verbosity=minimal'
dotnet test Backend/ERP_RFQ_Automation.Tests/ERP_RFQ_Automation.Tests.csproj --configuration Release --filter 'Category=PostgreSQL' --no-build --logger 'console;verbosity=minimal'
dotnet ef migrations has-pending-model-changes --no-build --project Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj --startup-project Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj
dotnet list Backend/ERP_RFQ_Automation.sln package --vulnerable --include-transitive
npm run lint
npm run build
npm run e2e:list
npm run e2e:acceptance
npm audit --omit=dev
git diff --check
```

## Recommendation

Do not merge or deploy Release 01C. Preserve the branch, run the existing 27 scenarios against an authorized disposable real-backend/PostgreSQL stack with fixture mode disabled, then close authoritative OCR/cost linkage and the remaining security lifecycle/HTTP mutation gaps. Re-run all gates before requesting merge or deployment authorization.
