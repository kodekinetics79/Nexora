# Gate 5: Performance and Production Readiness

Certification date: 2026-07-27. Scope: Nexora V1 Gate 5 only. Authorized synthetic data and local disposable PostgreSQL were used; no live data or production infrastructure was accessed.

## Decision

- Code acceptance: **GO**. No accepted P0/P1 implementation finding remains open.
- Production deployment: **NO-GO** until the external prerequisites below are verified in the approved Vercel, Render and Neon topology.
- Independent review P1s closed: direct `ExtractionJobs` runtime-role RLS proof, readiness aggregation across dependencies/queues/AI policy, optimized regression thresholds, and this certification record.

## Verification

| Gate | Result |
|---|---|
| Portable backend | `825/825` passed, zero skipped |
| PostgreSQL 16 | `140/140` passed, zero skipped; includes migrations, RLS, direct runtime-role extraction isolation, queues and concurrency |
| Authenticated Playwright | `38/38` passed, zero skipped, one worker, zero retries, 2.9 minutes |
| Backend build | Passed, 0 errors; 4 pre-existing package compatibility warnings |
| Frontend lint | Passed, zero warnings |
| Frontend production build | Passed; initial JS 1,315,324 bytes, 21.85% below the 1,683,028-byte baseline and below the 1,446,856-byte regression budget |
| EF model drift | No pending model changes |
| NuGet audit | No known vulnerable direct or transitive packages |
| Linux image | Docker production image built; Tesseract 5.3.0, Leptonica and `eng.traineddata` verified in the runtime image |
| Diff hygiene | `git diff --check` passed |

Commands:

```bash
dotnet test Backend/ERP_RFQ_Automation.Tests/ERP_RFQ_Automation.Tests.csproj --filter 'Category!=PostgreSQL' --logger 'console;verbosity=minimal'
dotnet test Backend/ERP_RFQ_Automation.Tests/ERP_RFQ_Automation.Tests.csproj --filter 'Category=PostgreSQL' --logger 'console;verbosity=minimal'
dotnet build Backend/ERP_RFQ_Automation.sln --no-restore
dotnet ef migrations has-pending-model-changes --project Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj --startup-project Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj
cd Frontend && npm run lint
cd Frontend && npm run build
cd Frontend && npm run e2e:acceptance
dotnet list Backend/ERP_RFQ_Automation.sln package --vulnerable --include-transitive
cd Frontend && npm audit --json
docker build -f Backend/Dockerfile -t nexora-v1-gate5 Backend
docker run --rm --entrypoint sh nexora-v1-gate5 -c 'tesseract --version && ldconfig -p | grep -q liblept && test -s /app/tessdata/eng.traineddata'
git diff --check
```

## Migration Rehearsal

The local connection was first asserted to be database `nexora_sales_tests` on a loopback host. Its disposable `platform` and `public` schemas were dropped, recreated, upgraded from zero through `20260727182849_V1Gate04LocalFirstAiLearningGovernance`, and populated by `ERP_RFQ_Automation.AcceptanceFixture`. The fixture then supported the complete browser run. No migration was added in Gate 5; historical migrations and model snapshot remained unchanged. Restore/re-upgrade coverage remains in the PostgreSQL suite and prior release records.

## Performance Evidence

These are representative algorithm and scoped-source measurements, not end-to-end throughput claims.

| Scope | Baseline p95 / calls | Gate 5 p95 / calls | Disposition |
|---|---:|---:|---|
| Exact normalized product lookup, 2,000 lines | 0.0536 ms / 4,000 | 0.0013 ms / 2 | Tenant-scoped catalog snapshot and request memoization; 99.95% source-call reduction |
| Local classification, 10,000 lines | 0.5541 ms / 20,000 | 0.0040 ms / 2 | 9,000 linked and 1,000 governed unresolved; no external calls |
| Concurrent PostgreSQL reservations | 160.02 ms | 144.36 ms | 10 reserved, 10 correctly rejected, zero unexpected contention errors |
| Local document fixtures | 151.7 ms | 144.0 ms | 100% local path, 83.3% usable, 16.7% human review, 0% external |

Customer matching, workload routing, ATP and sales aggregation remained within the 10% non-regression tolerance. No external AI, supplier-search or pricing provider was configured or called.

## Security Disposition

`npm audit` reports two high entries representing one inherited React Router advisory, `GHSA-qwww-vcr4-c8h2`. The vulnerable behavior is limited to React Server Components action handling. Nexora uses `BrowserRouter` as a client SPA and contains no RSC server, action endpoint or RSC imports, so the path is unreachable in this deployment. Compensating control: do not enable RSC mode; track and adopt an upstream fixed release when available. NuGet reported no vulnerable packages.

## Deployment Blockers

1. Verify reachable, integrity-checked S3-compatible evidence storage and a reachable production malware scanner from Render; local storage is not acceptable.
2. Configure and verify separate Neon owner/migration and least-privilege runtime connections, then prove `/ready` after a guarded migration.
3. Verify the Vercel build uses `https://nexora-fyjw.onrender.com`, the approved origin is `https://nexora1-ai.vercel.app`, and authenticated production smoke checks pass.
4. Keep external AI disabled by default and prove tenant AI usage, dead-letter queues and dependency blockers reconcile on the production operations screen.

Rollback: revert the Gate 5 application commit and rebuild the prior image. Gate 5 adds no database migration. Preserve all evidence, queue and audit records.
