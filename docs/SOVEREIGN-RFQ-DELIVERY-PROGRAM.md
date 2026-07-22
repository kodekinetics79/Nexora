# Nexora Sovereign RFQ Intelligence Delivery Program

Status: active operating plan  
Owner model: CTO + Product Development + Sales/Commercial + Independent QA/Security review  
Source prompt: `MASTER IMPLEMENTATION PROMPT - Sovereign High-Volume RFQ Document Intelligence Engine`

## Executive Verdict

Nexora is not yet production-ready for the full sovereign high-volume document-intelligence promise.

The current product has a real RFQ/quote/order/shipment application spine, platform tenant administration, tenant isolation tests, RBAC tests, a durable extraction queue, chunked extraction, and deterministic spreadsheet normalization. Those are strong foundations. The gap is that the master prompt requires a first-class evidence ledger and distributed document model across corpus, document, page, region, inquiry, line item, field, and evidence. Today, that model is only partially present and not the authoritative path for every format.

Release posture:

- Pilot/demo readiness: conditionally ready after targeted smoke tests.
- Enterprise shared-tenant production: not ready.
- 10,000-page / 100,000-line-item sovereign extraction: not ready until the P0/P1 gates below pass with evidence.

## Specialist Teams

Each module has a responsible team and an independent reviewer. Teams can work in parallel only when write scopes do not overlap.

| Team | Primary Scope | Independent Review |
|---|---|---|
| Principal Architecture | target architecture, module boundaries, ADRs | Enterprise readiness auditor |
| Document Intelligence | ingestion, native parsers, OCR, segmentation | QA architect |
| Data Architecture | canonical entities, evidence schema, migrations | PostgreSQL/performance engineer |
| Backend Platform | APIs, queue, workers, validation services | Security architect |
| Frontend Workbench | review UX, evidence viewer, bulk review | Product director + QA |
| Security/Privacy | tenant isolation, upload safety, AI gateway, audit | Red-team reviewer |
| RFQ/Procurement SME | commercial fields, workflow, quote readiness | Sales director |
| SRE/DevOps | health, observability, recovery, deployment gates | DR auditor |
| FinOps | model routing, token/cost budgets, local-first savings | Product finance reviewer |

## Current Architecture Discovered

Backend:

- ASP.NET Core 8 API in `Backend/ERP_RFQ_Automation`.
- PostgreSQL via EF Core in `Models/ErpRfqAutomationContext*.cs`.
- Tenant/platform control plane under `Platform/`.
- Durable extraction queue under `Extraction/` using PostgreSQL claim/lease/retry/dead-letter behavior.
- Local deterministic spreadsheet normalization under `Services/DocumentIntelligence/CanonicalRfqNormalizer.cs`.
- RFQ/quote/order/shipment workflow under controllers, repositories, and services.

Frontend:

- React 19 + TypeScript + MUI in `Frontend/src`.
- Operational pages for leads, RFQs, quotes, orders, suppliers, products, extraction review, and platform admin.

Verification baseline on 2026-07-22:

- `dotnet test Backend/ERP_RFQ_Automation.sln --no-restore`: 106 passed, 0 failed, 0 skipped.
- `npm run build` in `Frontend`: passed with existing Vite chunk-size warning.

Known warnings:

- NuGet moderate vulnerabilities: `MailKit 4.14.1`, `MimeKit 4.14.0`.
- Compatibility warnings: `OpenXmlPowerTools 4.5.3.2`, `System.Management.Automation.dll 10.0.10586`.
- Several nullable-reference warnings across controllers, repositories, and services.

## Specialist Findings

Architecture review:

- The durable queue, bounded worker pool, unified content-hash ingestion, production reader, chunked extraction, and canonical spreadsheet normalizer are real foundations.
- The release blocker is the missing durable evidence graph. Current persistence still lands mainly in `Lead` and `LeadItem`; `CanonicalRfqDocument` is a DTO-level model, not the system of record.
- `ProductionDocumentReader` loads complete files into memory and flattens PDF/DOCX structures.
- Scanned-PDF OCR is capped to the first 10 pages, which violates page completeness for high-volume scanned RFQs.
- The next architecture move is an evidence-first data model plus page-level processing.

QA and readiness review:

- Current backend tests pass, but queue claim/lease/complete/fail and worker end-to-end paths need Postgres/Testcontainers SIT.
- Frontend has no automated regression suite beyond build.
- Observability is registered, but extraction business metrics are not emitted from ingestion, worker, chunking, OCR, or LLM paths.
- CI is not yet a release gate for vulnerabilities, migrations, Docker build, contract tests, Playwright smoke, security scans, or artifact provenance.

Security/red-team review:

- EF global query filters are useful, but they are not database-level RLS. Current null tenant context intentionally disables filters, which is not fail-closed enough for the sovereign target.
- File download is authenticated but still path-based; it must become object-authorized by tenant-owned attachment/evidence id.
- External AI calls are not governed by a privacy-minimizing gateway; chunking is not the same as minimization.
- Malware scanning, quarantine, archive limits, and dangerous document handling are missing from RFQ ingestion.
- SMTP testing accepts caller-supplied host/port and disables certificate validation; this must be controlled before enterprise release.

Data architecture and PostgreSQL review:

- The existing PostgreSQL queue is a sound pilot foundation: it has tenant/content-hash uniqueness, claim/lease behavior, and `FOR UPDATE SKIP LOCKED` concurrency.
- The missing durable evidence graph is also the primary database blocker. Source evidence currently exists only in DTOs, while final `Lead` and `LeadItem` records have no document/page/region evidence foreign keys.
- `Attachment` is not yet a sovereign object-storage boundary. It needs tenant ownership, content hash, bucket/key/version, encryption, quarantine, retention, and recovery metadata instead of relying on a polymorphic local file path.
- EF graph insertion in `ExtractionWorker` will bottleneck at 100,000 line items. Evidence ingestion needs bounded batches, PostgreSQL `COPY`, or staging-table merge while retaining idempotency.
- Partitioning should be introduced selectively after measurement: keep the core transactional model simple, but make page, region, field-evidence, and extraction-run tables partition-ready by tenant and time.
- Database and immutable evidence objects must be restored as one recoverable system using PostgreSQL point-in-time recovery, object versioning, and post-restore hash verification.

Product/Sales and RFQ SME review:

- Commercially strong areas: RFQ/quote/order/shipment spine, tenant/platform admin, pricing foundations, review queue, sourcing/BOQ groundwork.
- Competitive gaps: source-grounded review workbench, customer template learning, catalogue aliases, manufacturer normalization, UOM/pack-size conversion, substitutions, historical learning, quote approval/margin/tax/FX controls.
- Current commercial readiness scores: ERP workflow 7/10, platform admin 6/10, review queue 5/10, document intelligence 4/10, catalogue matching 5/10, smart pricing 6/10, sovereign AI claim 3/10.

## Target Architecture

The production document-intelligence path must become:

```text
Secure Ingestion
→ File Validation and Malware Scanning
→ Immutable Source and Evidence Storage
→ File/Page/Region/Template Fingerprinting
→ Native Format Extraction
→ Selective Page or Region OCR
→ Layout and Table Reconstruction
→ Document and Inquiry Segmentation
→ Local Deterministic Extraction
→ Local Normalization and Candidate Generation
→ Catalogue/Historical/Master-Data Matching
→ Deterministic Validation
→ Contradiction and Consensus
→ Confidence and Risk Classification
→ Canonical Records or Privacy-Minimized Uncertainty Capsules
→ Controlled External AI Gateway
→ Human Review
→ Final Canonical RFQ Records
→ Continuous Local Learning
```

The authoritative data model must represent:

```text
Corpus
→ Document
→ Page
→ Region
→ Inquiry
→ Line Item
→ Field
→ Evidence
```

Every entity must include tenant ownership, durable identifier, content hash or evidence link, processing version, status, confidence/risk metadata, validation result, and audit history.

## P0 Release Gates

These must be closed before Nexora is positioned as delivery-ready for enterprise production.

| Gate | Required Evidence |
|---|---|
| Page-level completeness | Every uploaded page is processed, assigned, ignored with reason, quarantined, or exceptioned. |
| Field evidence persistence | Every critical field links to source evidence, not only summary strings. |
| Secure file intake | Magic-byte validation, size/page/decompression limits, malware scanning hook, quarantine states. |
| Native-first extraction | PDF/Office/CSV/XLSX/email parsers preserve structure before OCR or AI. |
| Selective OCR | OCR only for low-native-text pages/regions, with confidence and coordinates. |
| Tenant isolation | Negative cross-tenant API, DB query-filter, queue, file, export, and AI-gateway tests. |
| Controlled AI gateway | No direct provider calls from modules; privacy minimization, schema validation, audit, budgets, kill switch. |
| Deterministic validation | Quantity, UOM, currency, dates, duplicate lines, totals, and customer/product matching validated by code. |
| Review workbench | Exception-first review with source evidence, edit/reject/split/merge, audit trail, and concurrency control. |
| Recovery proof | Worker crash/restart resumes without duplicate RFQs or lost page/inquiry progress. |
| Scale evidence | Representative benchmark for large digital/scanned documents, dense tables, and concurrent tenants. |
| Storage recovery | Database and immutable evidence objects restore to a mutually consistent point with hash verification. |

## P0 Findings Ledger

| ID | Finding | Evidence | Required Fix |
|---|---|---|---|
| DOC-01 | Missing durable corpus/document/page/region/field/evidence model | `DTOs/DocumentIntelligence/CanonicalRfqDocument.cs`; `Models/Lead*.cs` | Add canonical evidence entities, migration, APIs, and persistence wiring. |
| DOC-02 | Scanned PDF OCR only processes first 10 pages | `Extraction/ProductionDocumentReader.cs` | Replace document-level OCR cap with page/region jobs and completeness ledger. |
| DOC-03 | Document reader loads full files into memory | `Extraction/ProductionDocumentReader.cs` | Stream and partition files into bounded page/sheet/attachment jobs. |
| DOC-04 | Structure is flattened before extraction | `ProductionDocumentReader` PDF/DOCX paths | Preserve layout, table, page, row, column, and coordinate metadata. |
| DATA-01 | Attachments rely on local paths and lack immutable object identity/recovery metadata | `Models/Attachment.cs`; `Extraction/DocumentIngestionService.cs` | Store tenant, content hash, bucket/key/version, encryption, quarantine, retention, and restore metadata. |
| DATA-02 | High-volume extraction persists large EF object graphs | `Extraction/ExtractionWorker.cs` | Add bounded batch writes using `COPY` or staging/merge, with idempotency and transaction tests. |
| DATA-03 | Page/region/evidence growth has no partition and retention design | Extraction and document-intelligence persistence | Make high-growth tables partition-ready and establish measured partition/retention thresholds. |
| SEC-ING-01 | RFQ ingestion trusts extension and lacks enterprise quarantine controls | `DocumentIngestionService.cs`; `Controllers/ExtractionController.cs`; `Security/FileContentValidator.cs` | Add file signature detection, malware scan interface, limits, quarantine, and rejection audit. |
| SEC-RLS-01 | Tenant isolation is EF-filter based and fail-open for null tenant context, not DB RLS | `Models/ErpRfqAutomationContext.Tenancy.cs`; `MultiTenancy/ITenantContext.cs` | Add Postgres RLS policies, app tenant setting, DB role without bypass, and negative SQL tests. |
| SEC-FILE-01 | File download is path-based, not tenant object-authorized | `Controllers/FileController.cs` | Download by attachment/evidence id, verify tenant ownership, reject direct paths/symlinks/traversal. |
| SEC-SMTP-01 | SMTP test path can connect to caller-supplied host and bypasses cert validation | `Controllers/SmtpController.cs` | Restrict host allowlist, remove cert bypass, block internal/link-local targets, add permission tests. |
| QA-01 | Postgres queue claim/lease/dead-letter paths lack SIT | `ERP_RFQ_Automation.Tests/TESTING.md`; `ExtractionQueue.cs` | Add Testcontainers/Postgres integration tests. |
| QA-02 | Frontend has no automated regression/E2E suite | `Frontend/package.json`; `.github/workflows/ci.yml` | Add Playwright smoke and API contract tests. |
| OBS-01 | Extraction metrics defined but not emitted | `Platform/Hardening/NexoraMetrics.cs` | Inject metrics into ingestion, queue, worker, chunking, OCR, and LLM calls. |
| GOV-01 | No controlled AI gateway or uncertainty capsules | `Services/Interfaces/ILLMService.cs`; `OllamaLlmService.cs` | Route all provider calls through gateway with minimization, schema validation, audit, budget, and kill switch. |
| PROD-01 | Review UI is not yet source-grounded | `Frontend/src/pages/ExtractionReview/*` | Add evidence viewer, highlights, confidence reasons, validation issues, and reviewer audit trail. |

## P1 Product Gates

| Gate | Required Evidence |
|---|---|
| Inquiry segmentation | 1,000+ inquiry corpus produces independent inquiry records and exception queues. |
| Template engine | Versioned customer/supplier templates with approval, rollback, regression tests. |
| Catalogue matching | Candidate generation and explainable match/reject decisions against products/history. |
| Confidence/risk policies | Field-specific thresholds by tenant/customer/document type/risk. |
| Continuous learning | Human corrections become versioned rules/templates only after evaluation. |
| Observability | Correlation ID from upload through final RFQ, queue metrics, extraction metrics, alerts. |
| Backup/restore | Restore test and documented RTO/RPO for DB and object/evidence storage. |
| Dependency hygiene | Vulnerability warnings resolved or risk-accepted with owner/date. |

## Smallest Complete Vertical Slice

Build this first, end to end, before expanding formats and scale:

1. Upload one CSV/XLSX RFQ with two inquiries and duplicate/conflicting line-item cases.
2. Store immutable original with content hash and chain-of-custody audit.
3. Create document, page/sheet, inquiry, line-item, field, and evidence records.
4. Extract via deterministic parser with cell references as evidence.
5. Run deterministic validation and duplicate detection.
6. Produce high-confidence canonical RFQ records for valid lines.
7. Route invalid/conflicting fields to review.
8. Approve/correct in the review workbench.
9. Persist final RFQ and audit reviewer decisions.
10. Re-upload unchanged file and prove idempotent reuse.

This slice avoids premature OCR complexity while proving the evidence ledger, validation, review, tenant safety, idempotency, and audit foundations.

## First Schema Slice

The first migration should establish these tenant-owned records and constraints:

| Table | Core Purpose and Constraints |
|---|---|
| `document_corpora` | Batch/corpus lifecycle; index tenant plus creation time and unique batch identifier. |
| `source_documents` | Immutable source identity, detected type, content hash, object bucket/key/version, byte/page counts, and security state; unique tenant plus content hash. |
| `document_pages` | Ordered page/sheet ledger with dimensions, rotation, text hash, OCR state, and confidence; unique document plus page number. |
| `document_regions` | Typed bounding regions with source text and confidence, linked to a page. |
| `canonical_inquiries` | Independent customer inquiry records linked to corpus and eventual lead/RFQ output. |
| `canonical_line_items` | Normalized commercial fields plus retained raw payload, indexed for inquiry and manufacturer-part lookup. |
| `field_evidence` | Field-level raw/normalized values, extractor/run identity, confidence, and source-region link. |

Every table must carry `business_unit_id`; application requests must set the PostgreSQL tenant session value, and RLS policies must fail closed when it is absent. Platform and background processing require separate, audited database roles rather than a nullable tenant bypass.

High-growth tables should include creation/run timestamps from the first migration so monthly range partitioning can be introduced without redesign. Tenant-list partitioning is deferred unless production measurements show a small number of exceptionally large tenants.

## First Sprint Backlog

1. Add `DocumentIntelligence` persistence entities and migration for documents, sheets/pages, inquiries, line items, fields, evidence anchors, extraction runs, and validation findings.
2. Wire the deterministic CSV/XLSX path to persist evidence records before writing final `Lead`/`LeadItem` output.
3. Add API endpoints for evidence inspection and review workbench data.
4. Add Testcontainers/Postgres SIT for queue claim, lease renewal, retry, dead-letter, and duplicate upload behavior.
5. Add Playwright smoke for platform login, tenant list, tenant app login, upload/review shell, and quote path.
6. Inject `NexoraMetrics` into ingestion and worker paths for enqueued/succeeded/failed/duration metrics.
7. Add a secure ingestion facade with document magic-type detection and quarantine states.
8. Resolve or formally risk-accept dependency warnings for `MailKit`, `MimeKit`, `OpenXmlPowerTools`, and `System.Management.Automation.dll`.
9. Replace local-path attachment identity with versioned object metadata and add a database-plus-object-store restore test.
10. Prototype batched field-evidence and line-item ingestion, then benchmark it against the EF graph path at 100,000 rows.

## Implementation Increments

### Increment 1: Evidence Ledger Foundation

- Add canonical document-intelligence entities and migrations.
- Wire deterministic XLSX/CSV normalization to persist field evidence.
- Add API endpoints to inspect document/page/inquiry/field evidence.
- Add tests for evidence completeness, duplicate upload, and tenant scoping.

### Increment 2: Native Parser Hardening

- Replace baseline text reader for production paths.
- Preserve PDF text positions, spreadsheet cell references, Word table coordinates where available.
- Reject unsupported/encrypted/password-protected files explicitly.
- Add parser contract tests with representative fixtures.

### Increment 3: Secure Intake

- Add file signature validation and limits.
- Add malware-scanning interface and quarantine state.
- Add path traversal, archive recursion, decompression, and formula-injection tests.

### Increment 4: Segmentation and Canonicalization

- Persist inquiry maps.
- Add continuation and multi-inquiry boundary tests.
- Separate accepted canonical records from review-required uncertainty cases.

### Increment 5: Validation, Consensus, and Risk

- Add deterministic validators for critical commercial fields.
- Introduce contradiction policies across parser/OCR/template/master-data signals.
- Store confidence dimensions instead of one generic score.

### Increment 6: Controlled External AI

- Add a single external AI gateway interface.
- Generate privacy-minimized uncertainty capsules.
- Enforce provider allowlists, budgets, redaction/tokenization, schema validation, audit logs, and kill switch.

### Increment 7: Review Workbench and Learning

- Source page/region/cell evidence beside extracted fields.
- Keyboard-driven exception review and bulk approval only under policy.
- Convert approved corrections into proposed rules/templates with tests and rollback.

### Increment 8: Production Certification

- Load, stress, endurance, failure-injection, security, RLS, backup, restore, accessibility, and E2E tests.
- Release verdict based on measured gates, not feature claims.

## Regression and SIT Matrix

| Layer | Test Type | Required Before Release |
|---|---|---|
| Backend unit | services, validators, normalizers, queue, auth | every PR |
| Backend integration | DB migrations, APIs, tenant filters, queues | every release candidate |
| Parser contracts | CSV/XLSX/PDF/DOCX/email/image fixtures | every parser change |
| Security | auth, RBAC, tenant isolation, upload attacks, prompt injection | every release candidate |
| Frontend | build, route smoke, review workbench workflows | every release candidate |
| E2E/SIT | upload → extraction → review → RFQ → quote | every release candidate |
| Performance | 10k-page, 1k-inquiry, 100k-line synthetic and real fixtures | production certification |
| Recovery | worker crash, lease expiry, retry, dead-letter, resume | production certification |
| DR | backup restore and evidence-store recovery | production certification |

## Independent Review Protocol

Every increment must include:

1. Team implementation notes.
2. QA test evidence.
3. Security/privacy review.
4. Product/RFQ SME acceptance.
5. Independent readiness verdict.
6. Updated P0/P1/P2 ledger.

No module is marked complete because a screen exists. Completion requires working code, persisted data, tests, and evidence.
