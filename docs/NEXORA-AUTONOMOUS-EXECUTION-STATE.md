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
2. **DemoUserSeeder fail-open** — ran by default in prod, reset Super Admin + Platform Owner password to repo-published `Nexora#Pilot-a9bc9e` on every restart (DemoUserSeeder.cs, DEPLOYMENT.md:46-66). Corroborated by SRE + Architecture. **FIXED** — default off, prod requires explicit passwords (no hardcoded fallback), never overwrites existing hashes. Tests: DemoUserSeederTests.cs (3).
3. **Cross-tenant SMTP fallback** — SendEmailAsync fell back to "any active SMTP" when a tenant's config was missing, sending one tenant's quote through another's mail server (EmailService.cs:1319). **FIXED** — fallback scoped to the same BusinessUnit; throws a clear error otherwise. *Manual verification recommended (heavy ctor; no unit harness).* 
4. **Postgres-only paths untested** — atomic queue claim/lease/dead-letter and NXR sequence allocation are certified only on a SQLite fallback that never runs in prod (TESTING.md:70-81). QA P0. Needs Testcontainers-Postgres harness. *Open.*

### P1 — fix in pilot week 1
- **tessdata not in publish output** — OCR silently broken in the deployed container (csproj had no Content item). **FIXED** — added `<Content Include="tessdata/**">`.
- **No AI gateway** — OllamaLlmService + AnthropicAgentLlm call providers directly; default extraction provider is Ollama **Cloud** receiving up to 30k raw chars/call (contradicts local-first). *Open — CTO decision: self-host Ollama vs build IAiGateway seam.*
- **Zero token/usage/billing metering** — no UsageEvent/LlmCallLog entity; provider `usage` blocks discarded at call sites; Plan quotas (MaxDocsPerMonth/MaxSeats/Weight) are dead schema. Corroborated by Billing + DocInt + QA + Architecture. *Open — CTO scope decision for pilots.*
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
3. Metering scope for pilots: descope (flat-fee, reconstruct doc volume from ExtractionJobs later) vs. minimal UsageEvent + LlmCallLog now.
4. AI privacy posture: self-host Ollama vs. build IAiGateway seam (raw doc text currently leaves to Ollama Cloud by default).
5. Migration execution mechanism (release_command vs. guarded startup Migrate).

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
Implement `IFileStorage` abstraction over object storage behind DocumentIngestionService (P0 #1) once provider is chosen; interim: attach a Fly volume via `[mounts]` (single-machine only).

## Next command
`cd /Users/zackkhan/Nexora/Nexora-main/Backend && dotnet test ERP_RFQ_Automation.sln --nologo -v minimal`
