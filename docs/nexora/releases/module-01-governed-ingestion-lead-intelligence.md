# Module 01: Governed Ingestion and Lead Intelligence

Date: 2026-07-30
Branch: `release/nexora-v2-v3-accelerated`
Baseline: `07a8dab`

## Decisions

- Module development: GO at M3 for the certified features below.
- Production pilot: NO-GO until durable S3-compatible evidence storage and a separate reachable ClamAV service are configured, `/ready` returns 200, and existing production dead letters are reconciled under authorization.
- Scope boundary: automatic grouping of multiple related documents into one inquiry remains Partial and is not represented as complete.

## Feature Results

| Feature | Result | Evidence |
| --- | --- | --- |
| Governed upload entry | Complete | Upload routes use claims-derived tenancy, bounded inspection, immutable quarantine, malware scanning, evidence occurrence creation, and idempotent queueing. UI commands require Leads create permission. |
| Security outage recovery | Complete | Scanner outage remains `AwaitingSecurityScan`; transient storage reads remain retryable; explicit missing objects become `SOURCE_OBJECT_UNAVAILABLE`; integrity failures and malware are terminal and auditable. No re-upload is required for recoverable sources. |
| Strict scanner readiness | Complete | `/ready` requires both a clean control and an EICAR infected control. Scanner liveness alone cannot satisfy readiness. |
| Durable S3 controls | Complete in application | Non-local endpoints require HTTPS at construction, object reads are bucket-qualified, and readiness requires bucket versioning. Production service configuration remains external. |
| Document formats | Complete for certified allowlist | PDF, DOC, DOCX, XLS, XLSX, XLSM, CSV, TXT, PNG, JPG/JPEG, GIF, BMP, TIF/TIFF, and WEBP pass extension/signature governance. Unsupported or mismatched content fails closed. |
| Native and local extraction | Complete | DOCX tables and paragraphs preserve structure; malformed DOCX is a typed permanent failure; all TIFF pages are processed with restricted temporary-file permissions; structured formats retain deterministic local parsing. |
| Local-first AI privacy | Complete | Raw unstructured regions cannot be sent to an external provider. Local structured and local unstructured paths remain available; external unstructured extraction fails closed. |
| Async lifecycle and shared duplicates | Complete | Pending and dead-letter shared jobs no longer report false resolved/reused outcomes. All occurrences linked to a job synchronize on retry, failure, and success. |
| Exact duplicate accounting | Complete | Historical exact duplicates are backfilled from authoritative job state; succeeded processing is reused without duplicate Lead, RFQ, workload, cost, or KPI work. |
| Evidence provenance | Complete | Source provenance and occurrence metadata are database-immutable; cleared object coordinates cannot be replaced; security rejection updates all linked occurrences. |
| Human extraction review | Complete | Save and approve are permission-aware. Approval is server-blocked without cleared, resolved, integrity-valid source evidence; saving corrections remains available. Pending confidence is displayed as `Not yet scored`. |
| Tenant and role separation | Complete | Existing HTTP authorization, EF filters, PostgreSQL RLS, cross-tenant negatives, and permission-aware UI controls pass in the complete PostgreSQL and browser lanes. |
| Ingestion dates and sorting | Complete | Batch/occurrence timestamps remain visible and deterministic ordering is preserved on ingestion and duplicate views. |
| Multi-document lineage | Complete | Many-to-many source-document lineage is persisted and retained through reconciliation. |
| Automatic multi-document grouping | Partial | Deterministic grouping contracts and possible-match review exist, but automatic certification for arbitrary related email attachments remains backlog. |

## Normal Application

| Workflow | Click path | Route |
| --- | --- | --- |
| Upload documents | Lead Management -> Bulk Uploads | `/procurement/leads/manual-upload` |
| Review a batch | Bulk Uploads -> Queue for reconciliation | `/procurement/leads/ingestion/:batchId` |
| Review extracted facts | Lead Management -> Needs Review -> Open | `/procurement/extraction/review/:id` |
| Inspect duplicates | Lead Management -> Duplicates | `/procurement/leads/duplicates` |
| Recover dead letters | Tenant Administration -> Operations -> Lead extraction exceptions | `/admin/operations` |

## Migration

`20260730193414_SynchronizeSharedExtractionOccurrences`:

- adds structured terminal outcomes for source-unavailable and evidence-integrity failures;
- corrects historical duplicate occurrence status and reuse flags from the linked extraction job;
- synchronizes every occurrence sharing an extraction job;
- protects source-document provenance and occurrence source metadata with PostgreSQL triggers;
- maps new terminal outcomes safely on downgrade;
- leaves all historical migrations unchanged.

The populated isolated PostgreSQL rehearsal passed upgrade, provenance enforcement, downgrade, migration-history verification, and re-upgrade.

## Verification

| Gate | Exact result |
| --- | --- |
| Focused module backend | 68 passed, 0 failed, 0 skipped |
| Scanner transient-recovery PostgreSQL | 1 passed, 0 failed, 0 skipped |
| Populated migration rehearsal | 1 passed, 0 failed, 0 skipped |
| Portable backend suite | 1017 passed, 0 failed, 0 skipped; 1 minute 33 seconds |
| PostgreSQL 16 suite | 199 passed, 0 failed, 0 skipped; 3 minutes 3 seconds |
| Backend solution build | Passed, 0 errors; 216 pre-existing compatibility/nullability/obsolete-API warnings |
| EF model drift | No pending model changes |
| Frontend lint | Passed with 0 warnings |
| Frontend production build | Passed; 14,740 modules; 1,287,639 initial JS bytes within 1,446,856-byte budget |
| Browser UX and permission acceptance | 13 passed, 0 failed, 0 skipped across desktop and mobile fixture-backed UI |
| Authenticated HTTP and RLS | Passed inside the complete PostgreSQL lane using real ASP.NET Core HTTP, PostgreSQL, authentication, permissions, and RLS |
| NuGet vulnerability scan | No known vulnerable packages |
| Git whitespace validation | Passed |

The 13 new browser scenarios validate responsive permission surfaces and interaction states. They are UI fixture scenarios, not a replacement for the existing real-backend authenticated pilot browser lane.

## Independent Review

Accepted P1 findings were fixed for server-authoritative evidence approval, transient-versus-terminal storage recovery, migration rollback compatibility, historical duplicate backfill, S3 TLS validation, all-linked-occurrence security disposition, database-immutable evidence identity, and TIFF temporary-file confidentiality. No accepted P0 remains.

The installed React Router advisory affects RSC action handling. Nexora is a client-only Vite `BrowserRouter` application with no RSC, SSR, data-action, or server-action path, so the advisory is not reachable in the current architecture. It remains a dependency watch item and must be upgraded before any affected runtime is introduced.

## Remaining Gates

1. Configure durable, versioned S3-compatible storage in Render.
2. Configure a separate reachable ClamAV service and retain strict `/ready` behavior.
3. Apply the new migration through the Neon owner/direct migration role; retain a least-privilege runtime role.
4. Reconcile the known production dead letters only with explicit live-data authorization.
5. Run the clean-file, EICAR, scanner-outage, recovery, exact-duplicate, legacy-DOC, sorting, and real-backend authenticated browser scenarios in staging.
6. Promote only when `/health` and `/ready` both return 200 and no unresolved P0/P1 ingestion exception remains.

No push, merge, deployment, production infrastructure change, or live-data access was performed.
