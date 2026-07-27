# V1 Gate 4 Evidence

Date: 2026-07-27

Status: PASS WITH DEPLOYMENT PREREQUISITE

## Visible checkpoint

- The normal authenticated RFQ, Supplier Quote and Client PO screens expose their canonical Lead,
  Nexora Serial, intake occurrence, extraction job/run, OCR result, local/external provider use and
  attributable external cost state.
- Unknown or local-compute cost is shown as unpriced, never as zero. Provider internals and request
  correlation values are not returned on the normal commercial evidence surface.
- Learning Studio derives tenant-scoped signals from persisted commercial corrections. Approve,
  disable and rollback are append-only, versioned, reasoned and idempotent decisions.
- External AI remains disabled by default, is tenant checked, redacted, centrally governed and capped
  by document token budgets and the policy limit of ten percent. Whole-document external fallback is
  blocked when no bounded regions are available.

## Verification

```text
dotnet test ... --filter 'FullyQualifiedName~AiGovernanceServiceTests|FullyQualifiedName~ProcessingEvidenceTests|FullyQualifiedName~LearningGovernanceTests|FullyQualifiedName~ChunkedExtractionServiceTests'
43 passed, 0 failed, 0 skipped

dotnet test ... --filter 'FullyQualifiedName~V1Gate04PostgreSqlTests'
3 passed, 0 failed, 0 skipped; PostgreSQL 16 Testcontainers

dotnet test ... --filter 'FullyQualifiedName~AuthenticatedHttpRlsTests.Processing_evidence_requires_auth_permission_and_authenticated_tenant_scope'
1 passed; 401 unauthenticated, 403 denied role, 200 own tenant and 404 cross-tenant

dotnet test ... --filter 'Category!=PostgreSQL'
823 passed, 0 failed, 0 skipped

dotnet test ... --filter 'Category=PostgreSQL'
138 passed, 0 failed, 0 skipped; PostgreSQL 16 Testcontainers

npx playwright test --config playwright.commercial-journey-v2.config.ts --grep 'local-first processing evidence'
1 passed, 0 failed, 0 skipped; 11.0 seconds; normal login, ASP.NET Core API and populated PostgreSQL
```

The 12-document authorized benchmark covered email, CSV, multi-sheet XLSX, native PDF, scanned PDF,
DOCX, image, Supplier Quote, Client PO, duplicate, forwarded copy and revision. All processing remained
local and no external provider was called. Native and deterministic formats completed locally. The two
OCR-dependent fixtures produced governed failed/review outcomes on macOS ARM because the native Tesseract
runtime is unavailable on that host. The production Linux image installs Tesseract and Leptonica, but a
Linux image smoke test remains a deployment prerequisite; this evidence does not claim production OCR
certification.

## Migration and governance

Migration `20260727182849_V1Gate04LocalFirstAiLearningGovernance` was applied to a populated disposable
PostgreSQL acceptance database, rolled back to `20260727171327_V1Gate03IntegrationOperationalVisibility`,
and re-applied. Rollback backfills nullable cost values before restoring the prior non-null column. RLS,
least-privilege grants, cross-tenant insert rejection, append-only governance and rollback-version checks
were verified independently in PostgreSQL.

Independent review reported no P0 findings. All accepted P1 findings were fixed and retested: configured-rate
cost compatibility, tenant-context mismatch rejection, durable governed signals, downgrade null-cost handling
and bounded external fallback. Accepted P2 hardening added rollback validation, removed provider internals from
normal responses and added authenticated HTTP/RLS coverage.

Screenshot: `docs/nexora/evidence/v1/gate-04-processing-learning.png`.

No production data, credentials, infrastructure, deployment, push or merge was used.
