# V1 Gate 2 Evidence

Date: 2026-07-27

Status: PASS

## Visible checkpoint

- The normal authenticated RFQ view displays persisted readiness, current tenant ATP, Supplier coverage, blockers, an explainable next action and read-only Opportunity Digital Twin scenarios.
- Commercial Memory exposes order-backed Product outcomes, governed Supplier Bid Quality, effective-dated Sales Rep coaching and thresholded Inventory demand evidence.
- Quote Draft creation remains server-blocked until customer, canonical Lead, Nexora Serial, line identity/UOM and stock or approved Supplier-award coverage are complete.
- Immutable Supplier Quote evidence remains unchanged; the latest review decision controls readiness, corrected values update an unselected projection, and any correction after award blocks readiness, pricing, RFQ coverage and procurement until a new revision is approved.
- Customer pricing is fail-closed unless one approved Supplier award covers the whole Quote line. Stock-plus-source and multi-Supplier blended pricing cannot produce an incorrect price or over-procure through the single-award handoff contract.

## Verification

```text
dotnet test ... --filter 'FullyQualifiedName~V1Gate02|FullyQualifiedName~Release02CommercialLearningTests|FullyQualifiedName~ProcurementApplicationServiceTests|FullyQualifiedName~InventoryReservationTests|FullyQualifiedName~CommercialLineResolutionApplicationServiceTests|FullyQualifiedName~ProductControllerTenantAuthorizationTests'
77 passed, 0 failed, 0 skipped

dotnet test ... --filter 'Category=PostgreSQL'
125 passed, 0 failed, 0 skipped; PostgreSQL 16 Testcontainers

npx playwright test --config playwright.commercial-journey-v2.config.ts --grep '3[1-5] '
5 passed, 0 failed, 0 skipped; normal UI login, real ASP.NET Core API and populated PostgreSQL fixture

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

## Data and migration integrity

`20260727042452_V1Gate02CommercialIntelligenceIntegrity` applied to the populated disposable acceptance database. Its preflight fails closed on missing Product/Inventory tenant ownership or cross-tenant historical rows, then installs tenant-qualified Product, Inventory, movement, incoming, reservation, Supplier-offer and Supplier-PO lineage. PostgreSQL negative tests prove cross-tenant Product Alias and Stock Reservation writes fail and verify the composite foreign keys and RLS policies.

The migration does not guess ownership for ambiguous legacy rows. Deployment requires a read-only preflight and authorized correction before upgrade if any row fails. Isolated populated upgrade, downgrade, re-upgrade and restored-clone upgrade rehearsals preserve data and migration history. Production rollback remains restore of the verified pre-upgrade backup followed by application rollback; the reversible `Down` path is certified for isolated rehearsal, not a substitute for that backup.

Inventory reservation retries require equivalent inventory, quantity, Order and line identity. Release and consume are transaction-serialized, versioned and emit append-only lifecycle events; consume also emits one inventory issue movement.

Screenshot: `docs/nexora/evidence/v1/gate-02-opportunity-digital-twin.png`.

No production data, credentials, infrastructure, deployment, push or merge was used.
