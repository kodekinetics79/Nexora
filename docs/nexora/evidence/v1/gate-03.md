# V1 Gate 3 Evidence

Date: 2026-07-27

Status: PASS

## Visible checkpoint

- The authenticated Procurement Handoffs screen shows persisted connector state, source, freshness,
  dispatch backlog, retries, terminal uncertainty, dead letters, stale handoffs and reconciliation differences.
- Signed, tenant-configured callbacks preserve external event and correlation identity, apply only valid
  lifecycle transitions, and retain rejected observations without mutating authoritative commercial values.
- Provider-authoritative handoffs cannot be overwritten by controlled manual reference capture.
- Supplier RFQ dispatch records `SENT` only with a real provider acceptance receipt. Console or missing
  provider configuration is non-delivering and dead-lettered; uncertain provider outcomes are never retried
  automatically.

## Verification

```text
dotnet test ... --filter 'FullyQualifiedName~ProcurementDispatchWorkerTests|FullyQualifiedName~ProcurementIntegrationServiceTests|FullyQualifiedName~Gate03IntegrationContractTests|FullyQualifiedName~ProcurementAuthenticatedHttpTests'
47 passed, 0 failed, 0 skipped

dotnet test ... --filter 'FullyQualifiedName~V1Gate03MigrationPostgreSqlTests|FullyQualifiedName~Release02ProcurementHandoffPostgreSqlTests|FullyQualifiedName~ProcurementDispatchWorkerPostgreSqlTests'
10 passed, 0 failed, 0 skipped; PostgreSQL 16 Testcontainers

npx playwright test --config playwright.commercial-journey-v2.config.ts --grep '36 authenticated'
1 passed, 0 failed, 0 skipped; normal login, real ASP.NET Core API and populated PostgreSQL fixture

dotnet build Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj --no-restore
Succeeded, 0 errors; pre-existing warnings remain

npm run lint
Passed with zero warnings

npm run build
Passed; existing large MUI chunk advisory remains

dotnet ef migrations has-pending-model-changes ...
No changes have been made to the model since the last migration

git diff --check
Passed
```

## Integrity and operations

Migration `20260727171327_V1Gate03IntegrationOperationalVisibility` was exercised against a populated
disposable PostgreSQL mailbox-identity dataset through upgrade, data-preserving downgrade, re-upgrade and
restored-clone upgrade. Separate PostgreSQL tests cover handoff lifecycle, callback append-only/RLS behavior,
dispatch fencing and provider-receipt persistence. The migration refuses downgrade when valid cross-mailbox
Message-ID identity or append-only provider callback evidence would be lost. Production rollback for those
states is restoration of the verified pre-upgrade backup followed by application rollback.

Callback authentication requires both a tenant-scoped authenticated principal and a per-tenant HMAC secret.
Production connectors must use a least-privilege service-principal JWT with the required Orders permission;
shared user credentials are not an acceptable deployment configuration. No integration secret has a committed
fallback.

Screenshot: `docs/nexora/evidence/v1/gate-03-procurement-operational-sync.png`.

No production data, credentials, infrastructure, deployment, push or merge was used.
