# NEXORA AUTONOMOUS EXECUTION STATE

> Continuously updated ledger. Resume from here — do not restart the program.

## Repository
- Repo: kodekinetics79/Nexora — local path `/Users/zackkhan/Nexora/Nexora-main`
- Working branch: `fix-vercel-api-base` (tracks and equals `origin/main`; push with `git push origin HEAD:main`). Local `main` is a stale worktree at `/Users/zackkhan/Nexora/RFQ-Automation-Vite` — do not use.
- Baseline commit: `92cae6e` — "Govern commercial lifecycle transitions"

## Mission
Bring Nexora to pilot-ready per docs/SOVEREIGN-RFQ-DELIVERY-PROGRAM.md. Three client pilots at full capacity.

## Stack (verified against code, correcting the program doc)
- Backend: .NET **8.0** (net8.0 LTS — NOT .NET 10 as the program doc claims; SDK 10.0.300 installed). EF Core 9 (Npgsql; a dead SqlServer 9.0.9 provider is still referenced). xUnit tests.
- Frontend: React + Vite + TypeScript, MUI, React Query, code-split routes. Vercel.
- DB: PostgreSQL (Neon). 13 EF migrations incl. DB-trigger-enforced NXR references (20260722033825).
- Deploy reality: DEPLOYMENT.md says **Render** (nexora-fyjw.onrender.com) is live; `Backend/fly.toml` defines a Fly app; `vercel.json` bakes the Render URL into the build. **Split-brain — must pick one host.**

## Test baseline
- Pre-change: 209/209 pass (6s). Post-change: **212/212 pass** (209 + 3 new DemoUserSeeder security tests).

## Current phase
Pilot-readiness evaluation complete (9-discipline SME board, run wf_3d130c34-a6f, adversarially verified where the Fable budget allowed). Highest-severity, self-contained P0/P1 items fixed and tested. Infrastructure-decision items surfaced to CTO, not silently changed.

## Findings ledger (from SME board; corroboration noted)

### P0 — pilot-blocking
1. **Ephemeral evidence storage** — all uploads/attachments/raw emails written to container-local `Uploads/` (DocumentIngestionService.cs:56, EmailService.cs:77-81). No Fly volume / object store. Lost on every deploy; breaks with >1 machine. **Corroborated by SRE + DocInt + Architecture.** REQUIRES CTO INFRA DECISION (object storage provider). *Open.*
2. **DemoUserSeeder fail-open** — previously ran by default in prod and reset privileged passwords on every restart. Corroborated by SRE + Architecture. **FIXED** — default off, prod requires explicit passwords (no hardcoded fallback), never overwrites existing hashes, and deployment docs no longer publish pilot credentials. Tests: DemoUserSeederTests.cs (3).
3. **Cross-tenant SMTP fallback** — SendEmailAsync fell back to "any active SMTP" when a tenant's config was missing, sending one tenant's quote through another's mail server (EmailService.cs:1319). **FIXED** — fallback scoped to the same BusinessUnit; throws a clear error otherwise. *Manual verification recommended (heavy ctor; no unit harness).* 
4. **Postgres-only paths untested** — atomic queue claim/lease/dead-letter and NXR sequence allocation are certified only on a SQLite fallback that never runs in prod (TESTING.md:70-81). QA P0. Needs Testcontainers-Postgres harness. *Open.*

### P1 — fix in pilot week 1
- **tessdata not in publish output** — OCR silently broken in the deployed container (csproj had no Content item). **FIXED** — added `<Content Include="tessdata/**">`.
- **No AI gateway** — **FIXED** for every current Ollama and Anthropic completion path: fail-closed tenant policy, purpose/provider/model controls, independent budget reservation/settlement, immutable per-attempt ledger, exact/estimated usage, idempotency, and output ceilings. External processing remains disabled until explicitly enabled by a platform tenant administrator.
- **Zero token/usage/billing metering** — **FIXED for AI token metering and hard monthly budgets**. Commercial invoicing/plan monetization remains separate finance work.
- **LLM path has no determinism guard** — qty/price/UOM/MPN persisted verbatim from model output; no source-evidence check; evidence-ledger tables (SourceDocument/FieldEvidence...) migrated but never written. *Open.*
- **Scanned PDFs >10 pages silently truncated** and can save as clean "Ok" leads (ProductionDocumentReader.cs:214). *Open — hours-scale: flag truncation → NeedsReview.*
- **No DB migration execution path** in any deploy flow (no Migrate(), no release_command). Schema drift one forgotten command away. *Open.*
- **Customer 1 / Customer 2 prototype live in nav** — hardcoded SEC/Aramco tabs (FolderUploadLeadsPage). DoD #1. **FIXED** — page deleted, sidebar entry removed, legacy routes redirect to manual upload. FE typecheck clean.
- **Dead RFQ actions / fabricated audit entry** — Create RFQ 404s; Upload/Export/Edit-Draft buttons no-op; ViewRFQPage synthesizes an "Approved & Sent" audit row attributed to the viewer (ViewRFQPage.tsx:288). *Open.*
- **NXR reference / case search / routing queue / custom fields have zero FE surface** (grep of Frontend finds no NXR/referenceCode/customField). *Open.*
- **No email-account setup UI/API** — intake+outbound depend on manual SQL (sales P1). *Open.*
- **Quote UI hardcoded to "$"/USD** — currency model exists but reps can't set it. *Open.*
- **Draft RFQ deletes on one unconfirmed click** (DraftRFQsPage.tsx:60). *Open — 1-2h.*
- **Split-brain deploy topology** (Render vs Fly) → duplicate singleton workers against one DB. *Open — pick one host.*
- **Tenant isolation certified only at EF-filter/SQLite level; no DB RLS, no HTTP negative tests.** *Open.*
- **Platform owner control plane + impersonation have zero tests.** *Open.*
- **Frontend has zero tests / no runner.** *Open — add Playwright smoke suite.*
- **No telemetry export/alerting; backup/restore untested.** *Open.*

### P2 — schedulable (owners assigned in report)
Job-completion non-idempotency (duplicate lead on crash between persist+complete); OCR process-wide serialization throughput cliff; no revision/amendment detection; lifecycle outbox written but never drained; OverviewController returns fabricated zero-cost/"healthy" data; RFQ status magic IDs 34-37; list pages swallow API errors as empty grids; invoice is a print-view with no invoice identity; no shareable quote link/portal/ERP hooks; dead SqlServer provider; TESTING.md understates suite 5x.

## Strengths verified (genuinely well-built)
- Durable Postgres job queue: `FOR UPDATE SKIP LOCKED` atomic claims, leases, retry/backoff, dead-letter, per-tenant weighted-fair scheduling (ExtractionQueue.cs). Crash-safe across deploys.
- NXR permanent references enforced in DB via sequence + trigger + immutability protection (migration 20260722033825).
- EF global tenant query filters are opt-out across all tenant entities; entity graph durably FK-connected (Lead 1:1 CommercialCase required Restrict, etc.).
- Local-first parse order real (native parsers → selective OCR); deterministic spreadsheet path validates qty/price/dates/currency per field; item-count conservation + never-guess-split.
- End-to-end money path exists: IMAP intake → OCR/LLM extract → review queue → lead → RFQ → quote (QuestPDF, branded) → revisions with won/lost/expired → order → shipment → invoice print → payment fields. Strong sales reporting; Excel import/export.
- Test infra avoids in-memory-EF trap (relational SQLite on the real model); 212 green in 6s.

## Completed this session
- Hardened DemoUserSeeder (fail-closed). Files: Infrastructure/DemoUserSeeder.cs, Program.cs:342. Tests: DemoUserSeederTests.cs.
- Removed cross-tenant SMTP fallback. File: Services/EmailService.cs.
- tessdata shipped in publish. File: ERP_RFQ_Automation.csproj.
- Removed Customer 1/Customer 2 prototype. Files: Frontend App.tsx, Sidebar.tsx; deleted pages/Leads/FolderUploadLeadsPage.tsx.

## Blockers / CTO decisions required
1. Object storage provider for evidence (Tigris/S3/R2) — unblocks P0 #1.
2. Single deploy host (Render vs Fly) — unblocks split-brain.
3. AI hosting posture: external providers now require explicit per-tenant consent and are metered; self-hosted inference remains an optional sovereignty enhancement.
5. Migration execution mechanism (release_command vs. guarded startup Migrate).

## Money-path implementation (priority per user, 2026-07-22)
Priority spine: Lead -> RFQ -> Quote -> PO/Order -> Delivery(Shipment) -> Inventory.
- Lead/Rfq/Quote/Order/Shipment: entities exist, FK-connected (nullable links).
- **KEY FINDING**: the `Inventory` POCO was **never mapped to the DB** (no DbSet, only reference was an absent query filter) — so there was **no persisted stock** at all. Confirms inventory was the biggest real hole.
- **SHIPPED — Inventory reservation engine** (commit pending): mapped the `Inventory` table (scalar cols, navs ignored to avoid cascading into other unmapped aggregates), added append-only `StockReservation` ledger, `IInventoryAvailabilityService` (OnHand/Reserved/Available; Reserve idempotent + over-reserve rejected; ReleaseForOrder; Consume decrements on-hand once). Fixed Inventory tenant query-filter gap. Migration `20260722235620_AddInventoryStockReservations`. Tests: `InventoryReservationTests` (6) — availability math, two-orders-cant-double-promise, idempotent reserve, release restores, consume-decrements-once, tenant isolation. **218/218 green.**
- NEXT (increment 2): wire Order confirmation -> ReserveAsync per line; delivery/shipment -> ConsumeAsync. Requires resolving RFQ/Order line -> InventoryId (product/part match).
- Slice A storage SME board (wf_a8c1da57-0f1) finished: 3/4 SME specs were placeholder stubs; the reconciliation consultant was strong and flagged Slice-B invoice intel (Postgres nextval is NOT gap-free for legal invoice numbers -> use a counter table; `Taxis` already mapped; BusinessUnit lacks TRN/functional-currency). Bank for the invoice slice.

## Commercial Case connectivity map (verified in code, 2026-07-22)
- **CommercialCase ↔ Lead**: durable 1:1, required, immutable reference (`Lead.CommercialCase.cs`: `CommercialCaseId`/`CommercialCaseReference` private-set). Reference DB-trigger enforced (migration 20260722033825).
- **Chain (all FK, but nullable)**: Lead ←`LeadId?`← Rfq ←`Rfqid?`← Quote ←`QuoteId?`← Order ←`OrderId`← Shipment. QuoteItem→Rfqitem via `RfqitemId?`.
- **Read spine EXISTS**: `CommercialCaseQueryService` already traverses Case→Lead→Rfqs→Quotes→Orders→Shipments for search + detail by permanent reference (`Controllers/CommercialCasesController.cs`: GET /api/commercial-cases/search, /{id}). **Do not rebuild.**
- **Real gaps vs. the end-to-end prompt**:
  - No direct denormalized `CommercialCaseId` on Rfq/Quote/Order/Shipment → search needs full-chain join; links are nullable so downstream docs can orphan from a case (no enforcement).
  - **No first-class Invoice / Payment / AR / Credit-Debit-Note entities** — invoice is an Order-derived print view; payment is status fields on Order (MODULE 14/15 largely unbuilt).
  - Supplier sourcing / cost-comparison depth unverified (MODULE 6/7) — needs inspection.
  - Commercial Case reference has thin/no frontend surface (per FE review).

## Next smallest executable task
Complete the first-class invoice, payment, accounts-receivable, and credit/debit-note domain, then expose the commercial finance workflow in the frontend.

## Next command
`cd /Users/zackkhan/Nexora/Nexora-main/Backend && dotnet test ERP_RFQ_Automation.sln --nologo -v minimal`

## Evidence storage and authenticated download wave (2026-07-22)
- Added `IFileStorage` plus a traversal-safe, immutable filesystem provider. Unified extraction, email, manual-upload, and watched-folder evidence roots now resolve through `Storage:RootPath`.
- Added `render.yaml` with a 10 GB persistent disk at `/var/data` and configured evidence root `/var/data/nexora/uploads`. Production startup requires an explicit writable root; the disk-backed profile enables strict mount verification with `Storage:EnforcePersistentMount=true`. Disk attachment remains an operator deployment step for the existing live Render service.
- Retired path-addressed downloads. `GET /api/File/attachment/{id}` now resolves the attachment, verifies its Lead belongs to the JWT business unit, constrains paths to the configured storage root, and returns a range-enabled stream.
- Fixed lead detail and extraction review to fetch attachments with the authenticated Axios client. The review path opens its window synchronously so popup blockers do not race the authenticated request.
- Added `LocalFileStorageTests` and `AttachmentDownloadSecurityTests`: immutable first-write behavior, legacy path resolution, traversal/absolute-path/symlink rejection, fail-closed production configuration, retired path route, own-tenant download, cross-tenant 404, and missing-tenant rejection.
- Verification: **233/233 backend tests pass**; frontend production build passes; live Render `/health` returned HTTP 200 `Healthy` after cold start. Browser SIT was not executable because the in-app browser runtime failed to initialize; browser E2E remains open.
- Consultant conditions retained as P1: legacy email/manual/folder paths now land on the configured volume but still use raw file writes rather than the immutable writer; FolderService attachment fan-out needs transaction/idempotency hardening; legacy absolute attachment paths require an explicit copy/backfill during deployment.
- Render deployment follow-up: the first deployment of `743962b` rolled back before the new attachment route became live. Mount enforcement was revised to use Render's documented mount-path contract instead of undocumented `/proc` representation, and is now explicitly controlled by `Storage:EnforcePersistentMount`; the Blueprint service name was aligned to `nexora-fyjw`.
- Independent board verdict remains **FAIL for enterprise certification**. This wave closes the direct attachment leak and creates a durable Render profile, but object storage/horizontal scaling, DB RLS/Postgres negative tests, page-complete evidence persistence, AI gateway/metering, finance entities, browser/load/restore testing, and alternate deployment certification remain P0/P1 conditions.

## PostgreSQL certification and tenant claim boundary (2026-07-23)
- Added a Testcontainers PostgreSQL 16 fixture that applies the complete production EF
  migration chain to an empty database. The suite now executes production-only queue SQL
  and database triggers rather than inferring their behavior from SQLite.
- Certified concurrent `FOR UPDATE SKIP LOCKED` claims: workers receive distinct jobs and
  the per-tenant cap leaves excess work pending. Certified 12 concurrent lead inserts:
  NXR references are server-generated, unique, and immutable.
- Added the zero-SQL `SynchronizeProductionModelMetadata` migration. The preceding
  hand-authored inventory migration lacked generated target-model metadata, causing EF 9
  to reject clean-database `MigrateAsync`; the new marker repairs migration validation
  without changing schema or data.
- Added `TenantClaimGuardMiddleware`: authenticated non-platform API traffic now receives
  403 when `businessUnitId` is missing, malformed, zero, or negative, closing controller
  fallbacks that previously trusted request-supplied tenant IDs. Platform routes retain
  their independent audience/policy boundary and anonymous login remains available.
- Verification: **243/243 backend tests pass** in one run, including the disposable
  PostgreSQL lane; an idempotent SQL migration script generates through the new marker.
- This checkpoint originally left RLS certification open. The later RLS and AI-governance
  increments below close it with an explicit tenant role/GUC and tenant-iterating workers.

## PostgreSQL row-level tenant isolation (2026-07-23)
- Added migration `AddTenantRowLevelSecurity`: creates a restricted NOLOGIN,
  non-BYPASSRLS `nexora_tenant_app` role and fail-closed read/write policies on the core
  commercial workspace, lifecycle ledger, and document-evidence tables.
- Added `TenantRlsCommandInterceptor`. Tenant EF commands use `SET LOCAL ROLE` plus a
  transaction-local `nexora.business_unit_id`; standalone commands receive a short
  transaction and existing service-owned transactions remain intact. Platform and
  background contexts retain their explicit owner path.
- Production now applies migrations before serving traffic. An explicit
  `ConnectionStrings:MigrationConnection` is supported; otherwise Neon `-pooler` hosts
  are converted to their direct endpoint for EF migrations.
- PostgreSQL SIT proves role attributes and all 16 policies, cross-tenant read/write
  denial even with `IgnoreQueryFilters`, compatibility with explicit transactions,
  fail-closed missing tenant state, and no state leakage with a one-connection pool.
- Verification: **244/244 backend tests pass** in one run, including the PostgreSQL RLS
  and production-dialect lane.

## Schema-wide RLS coverage expansion (2026-07-23)
- Independent review rejected the first RLS slice as a complete boundary because the
  restricted role had broad public-table grants while only 16 tables had policies.
- Added `CompleteTenantRlsCoverage`: all public tables with a recognized tenant column
  receive role-specific read/write RLS. Nullable master-data tenant columns remain
  readable as shared rows only for the explicit Customer/Supplier/Product/Inventory
  allow-list, but tenant sessions cannot create or convert rows to global.
- Added parent-derived policies for lead/RFQ/quote/order/shipment children, email ingests,
  lead attachments, contacts, product attachments, shipment history, supplier purchase
  history, and governed custom-field descendants. Migration-history access is revoked.
- PostgreSQL certification now derives expected coverage from EF metadata, validates
  policy role/read/write expressions, checks all tenant-column tables, tests dependent
  child-row visibility, and asserts SQLSTATE `42501` for cross-tenant writes. Broad table,
  sequence, and default grants are revoked; future-object canaries prove fail-closed
  permissions for later migrations.

## Extraction queue lease fencing (2026-07-23)
- Queue progress, failure, and completion transitions now require the active worker id,
  claim-generation token, unexpired lease, and expected monotonic state. Stale tasks
  cannot mutate a reclaimed job even if a worker id is reused.
- The extraction worker runs a lease heartbeat in independent dependency-injection scopes,
  avoiding concurrent use of its processing `DbContext`. Heartbeat failures cancel work
  by the last known lease deadline. Lead persistence and `Succeeded` completion commit in
  one transaction while holding the fenced queue-row lock.
- PostgreSQL SIT covers wrong-worker denial, expiry/reclaim, stale completion/failure,
  claim-generation ABA denial, monotonic transitions, retry backoff, and crash-expired
  dead-letter exhaustion. A fault-injection test proves persistence rolls back when queue
  completion is rejected, and a hung-renewal test proves work stops by the known lease
  deadline. Verification after the new tests: **247/247 backend tests pass**.

## Governed extraction review (2026-07-23)
- AI-extracted leads are now explicitly marked as requiring commercial review and cannot
  qualify or convert to an RFQ through either conversion API until a human approves their
  commercial facts. Unified queue, email, manual-document, and watched-folder AI paths
  all enter review; the migration backfills unconverted legacy AI leads.
- Review saves preserve `NeedsReview`; approval requires a reason, validates commercial
  values and product identity, records the authenticated reviewer and timestamp, and
  advances an optimistic concurrency version. Stale pages fail with HTTP 409.
- Every save and approval writes exact before/after JSON to an immutable, tenant-scoped
  audit ledger in the same transaction as the lead changes. PostgreSQL enforces a
  composite tenant/lead relationship, forced RLS, and application `SELECT/INSERT`-only
  privileges on the ledger.
- The review UI submits the expected version, distinguishes save from approval, requires
  an approval reason, and surfaces API conflict/validation errors. Verification:
  **254/254 backend tests pass**, the PostgreSQL production lane passes 6/6, the EF model
  has no migration drift, and the production frontend TypeScript/Vite build passes.
- Frontend lint remains a certification tooling gap: the repository has a lint script but
  currently declares neither ESLint nor a configuration.

## AI provider privacy boundary (2026-07-23)
- Ollama extraction and BOQ calls now keep trusted task rules and JSON schemas in the
  system role while placing only source-document text inside random matched boundaries
  in the user role. Document instructions cannot redefine policy or output schema.
- Raw model output, provider error bodies, and console response dumps were removed from
  Ollama and Anthropic logging. The manager health endpoint reports only configuration
  presence and status codes; it no longer exposes key prefixes, provider bodies, model
  replies, or exception details.
- Behavioral request/logger/controller tests prevent those disclosures and verify the
  serialized trust boundary using hostile document content. Verification:
  **258/258 backend tests pass**.

## Governed AI policy, metering, and provider accounting (2026-07-23)
- Added tenant-scoped AI processing policies, logical requests, immutable provider-call
  attempts, and monthly budget periods. The PostgreSQL migration applies forced RLS,
  restricted grants, tenant-composite foreign keys, status/hash/time/usage constraints,
  immutable attempt and request-identity triggers, and fail-closed policy creation for
  both existing and newly provisioned business units.
- External processing is disabled by default. Platform tenant administrators can inspect
  and update a tenant's policy through versioned, reason-required, audited control-plane
  endpoints, including purpose/provider/model restrictions and soft/hard token limits.
- Ollama extraction, Ollama BOQ drafting, and Anthropic agent turns now require explicit
  business-unit, purpose, prompt-version, and stable idempotency context. Each logical
  call reserves its maximum retry budget in an independent serializable transaction;
  every real HTTP attempt records exact provider usage when available or a labeled
  conservative estimate, then settles the reservation independently of business data.
- Output token ceilings are sent to both providers. Permanent Ollama 4xx responses are
  not retried; transient timeouts/cancellation are recorded as unknown rather than free.
  The health endpoint no longer performs an unmetered completion.
- Accounting stores content length and SHA-256 hashes only, never raw prompts or model
  output. PostgreSQL tests execute under `nexora_tenant_app`, prove cross-tenant AI rows
  are hidden, forced RLS is present, request identity and attempts cannot be rewritten,
  and new-business-unit policy provisioning remains fail closed.
- The runtime login is required to be `NOINHERIT` and enters `nexora_tenant_app` only with
  transaction-local `SET ROLE`. Production startup validates this contract and refuses to
  serve if an implicit AI maintenance bypass exists. Platform policy operations and stale
  reservation reconciliation use ordinary one-tenant scopes.
- AI-policy audit inserts remain atomic with policy updates through a narrowly constrained
  platform-audit RLS policy; the action, target, and tenant-to-business-unit mapping are
  database-validated, and those audit rows reject updates and deletes.
- Verification: **274/274 backend tests pass**, including **8/8 PostgreSQL** production
  tests; EF reports no pending model changes; the frontend TypeScript/Vite production
  build passes. Existing dependency advisories and missing ESLint configuration remain
  release-hardening work.
- Production rollback for `AddAiGovernanceLedger` is application-only or forward-fix.
  Its EF `Down()` is intentionally destructive and drops policy, request, attempt, and
  budget history; it must not be executed in production without an approved ledger export
  and retention procedure.
