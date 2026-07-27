# V1 Gate 1 Evidence

Date: 2026-07-26

Status: PASS

## Visible checkpoint

- Normal authenticated shell exposes permission-filtered Sales Rep, Sales Manager, Sourcing, Inventory, Executive and tenant-admin Today workspaces with persisted operational data and direct actions.
- Customer 360 uses tenant-qualified customer, contact, account ownership, RFQ demand, commercial memory, Quote, Order and follow-up records. Won/lost/conversion share the canonical explicit-outcome-or-customer-award definition; sold-line evidence requires a linked customer-award Order. RFQs, Quotes and Orders provide direct drill-downs.
- Commercial Workspace deep-links from search, explains the matching relationship and composes the existing RFQ workbench, ATP, sourcing, commercial memory, evidence and complete downstream document lineage.

## Verification

```text
dotnet test ... --filter 'FullyQualifiedName~CommercialIntelligenceControllerFocusedTests|FullyQualifiedName~CommercialCaseQueryServiceTests|FullyQualifiedName~CustomerContactAuthorizationTests|FullyQualifiedName~CustomerContextControllerFocusedTests'
34 passed, 0 failed, 0 skipped

dotnet test ... --filter 'Category=PostgreSQL&(FullyQualifiedName~CoreSalesPostgreSqlTests|FullyQualifiedName~AuthenticatedHttpRlsTests)'
13 passed, 0 failed, 0 skipped; PostgreSQL 16 Testcontainers

npx playwright test --config playwright.commercial-journey-v2.config.ts --grep '31 |32 |33 |34 '
4 passed, 0 failed, 0 skipped; real ASP.NET Core API, disposable PostgreSQL and normal UI login

Scenarios: Customer 360 continuity and drill-downs; role-scoped Sales Today actions; relationship-search deep link into Commercial Workspace; Sourcing, Inventory, Executive and tenant-admin Today surfaces.
The role checkpoint reconciled authenticated Supplier inbox, Inventory metric/exception and tenant-user responses to visible UI before exercising each next-action route.

dotnet build Backend/ERP_RFQ_Automation.sln --no-restore
Succeeded, 0 errors; four pre-existing NU1701 compatibility warnings

npm run lint
Passed with zero warnings

npm run build
Passed; existing large-chunk advisory remains

dotnet ef migrations has-pending-model-changes ...
No changes have been made to the model since the last migration

git diff --check
Passed
```

Independent consultant re-review: no remaining P0/P1 findings after role-action reconciliation, Customer 360 drill-down/demand coverage and canonical outcome alignment.

## Migration rehearsal

The existing disposable PostgreSQL acceptance database initially lacked a historical Release 02 column and failed fixture seeding closed. The current migration chain was applied through `20260726205812_Release02ProcurementHandoffHardening`; the fixture then seeded 57 persisted identifiers and all authenticated browser scenarios passed. Gate 1 adds no schema or migration.

No production data, credentials, infrastructure, deployment, push or merge was used.
