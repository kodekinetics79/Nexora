# Nexora Capability Register

Status values are `Verified`, `Partial`, `Planned`, and `Blocked`. Evidence is based on code, migrations, and tests; UI observations alone are not accepted.

| Capability | Status | Exact evidence | Release 01 disposition |
|---|---|---|---|
| Tenant authentication and scoped HTTP access | Verified | `Backend/ERP_RFQ_Automation/Program.cs`, `Models/ErpRfqAutomationContext.Tenancy.cs` | Preserve; add missing action permissions and PostgreSQL negative tests. |
| PostgreSQL/Npgsql persistence, migrations, RLS | Verified | `Program.cs`, `Migrations/`, `ERP_RFQ_Automation.Tests/PostgreSql*` | Mandatory portable and PostgreSQL lanes. |
| Immutable Nexora Serial at Lead | Verified | `Models/Lead.CommercialCase.cs`, `Migrations/20260722051308_OperationalizeCommercialLifecycle.cs` | Canonical label is **Nexora Serial**. |
| Nexora Serial across RFQ/Quote/Order/Invoice | Verified | `Models/{Lead,Rfq,Quote,Order}.CommercialIdentity.cs`, migrations `20260724223932` and `20260724230121`, `Release01CommercialIdentityMigrationPostgreSqlTests.cs` | Immutable parent-matched lineage is exposed through invoice data. |
| Customer/contact continuity | Partial | Commercial identity models, `LeadRepository.cs`, migrations and `Release01CommercialIdentityMigrationPostgreSqlTests.cs` | Inheritance and RLS are verified; tenant-qualified customer/contact FKs and delete restrictions remain a release blocker. |
| Governed Lead/RFQ lifecycle | Verified | `CommercialCases/Lifecycle/LifecycleApplicationService.cs`, `LifecyclePolicy.cs` | Keep optimistic version, idempotency, events, and outbox. |
| Governed Quote lifecycle | Verified | `LifecycleApplicationService.cs`, `QuoteService.cs`, `QuoteOutcomeService.cs`, migration quote guard | Send/outcome/order transitions write governed event and outbox records. |
| Governed Order lifecycle | Partial | Order creation is a separate aggregate; status updates remain outside the commercial lifecycle graph | Serial lineage is governed; a versioned Order status event contract remains future work. |
| Append-only commercial event spine | Partial | `CommercialLifecycleEvent` now covers Lead/RFQ/Quote transitions | Specialized response/follow-up events are not yet unified into this spine. |
| Atomic Lead to RFQ conversion | Verified | `Repositories/LeadRepository.cs`, `CommercialIdentityFlowTests.cs` | Serializable conversion, lifecycle event, and outbox with idempotent replay. |
| Truthful Dashboard 1.0 | Partial | `DashboardRepository.cs`, `DashboardRelease01DTOs.cs`, `DashboardRelease01Tests.cs`, `DashboardPage.tsx` | Four KPI paths calculate and reconcile; 14 defined KPIs remain `insufficient_data` pending authoritative event/currency/maturity inputs. |
| Deterministic structured-file extraction | Verified | `Services/DocumentIntelligence/NativeSpreadsheetParser.cs`, `CanonicalRfqNormalizer.cs` | Primary local-first path. |
| Governed PDF/image/LLM extraction | Blocked | `Extraction/ExtractionWorker.cs`, `AI/AiGovernanceService.cs`, `Services/OllamaLlmService.cs` | Fail closed to review, but authoritative provider-class/path measurement and a verified local-model endpoint are absent. |
| Authoritative evidence ledger | Verified | `EvidenceLedgerEntities.cs`, migration `20260724004000`, `EvidenceMigrationUpgradePostgreSqlTests.cs` | Populated upgrades and completed-run immutability are PostgreSQL-tested. |
| Governed upload gateway | Verified | `DocumentIngestionService.cs`, `ManualUploadController.cs`, `ExtractionController.cs`, trust tests | All lead-producing HTTP uploads use inspection, immutable storage, and claims-derived tenancy. |
| Malware inspection in declared Render topology | Partial | `MalwareScannerHealthCheck.cs`, `render.yaml`, `/ready` | Fail-closed readiness is implemented; live Render scanner reachability requires deployment verification. |
| Explainable routing | Verified | `CommercialRouting/CommercialRoutingApplicationService.cs` | Existing reasons and idempotency retained. |
| Measured workload-aware routing | Verified | `CommercialRoutingApplicationService.cs`, `DeterministicRoutingEngine.cs`, routing tests | Uses measured assignments, line load, urgency, availability, and content-bound idempotency. |
| Inventory and supplier discovery | Partial | `Frontend/src/pages/Procurement/RFQs/ProcessRFQPage.tsx` contains client-side matching and simulated request success | Outside Release 01 implementation; retain as an explicitly uncertified extension. |
| Vercel frontend / Render API / Neon PostgreSQL configuration | Partial | `vercel.json`, `render.yaml`, environment-driven backend configuration | Configuration inspected; browser SIT and deployed storage/scanner/worker/Neon readiness are not certified. |

## Certification Rule

A capability moves to `Verified` only when its server contract, tenant isolation, data migration, UI behavior where applicable, and required portable/PostgreSQL tests all pass. Simulated success messages and screenshots do not count as evidence.
