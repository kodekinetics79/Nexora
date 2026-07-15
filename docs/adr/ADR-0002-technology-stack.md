# ADR-0002 — Nexora technology stack decision

- Status: **Accepted** (CTO/CIO decision)
- Date: 2026-07-14
- Deciders: CTO/CIO (orchestrator), Enterprise System Architect, CISO, Chief AI Officer
- Related: ADR-0001 (AI provider → Claude)

## Context — what kind of system this is

Nexora is simultaneously three things, and the stack must serve all three:

1. **A money-bearing commercial system of record** (RFQs → quotes → orders →
   agreements → shipments). Demands strong typing, ACID transactions,
   deterministic decimal math, and auditability.
2. **A document/AI intelligence platform** (email, PDF/scanned/OCR, spreadsheets,
   layout/tables, LLM extraction with per-field evidence).
3. **A data-dense, multi-tenant internal enterprise application** (review
   workbenches, large grids, role-based workflows) — **not** a public content
   site.

Current reality (verified in Phase 0): a **working** ASP.NET Core **8** Web API
(EF Core database-first on SQL Server; 42 controllers, 45 entities) plus a
**working** React 19 + Vite + MUI 9 + TanStack Query SPA. Both build green.
Constraints: a **client demo deadline**, thin test coverage (~1 test), a remote
DB dependency, and the prompt's own rule — **"no blind rewrite of working
technology."**

## Decision — headline

**Retain the .NET 8 + React 19 core. Do not rewrite.** Make targeted upgrades and
incremental refactors, swap the LLM provider to Claude, and move schema and AI
into governed seams. Every larger migration (Postgres, Python doc-service,
durable workflow, .NET 10) is **evidence-gated and deferred** — none has a demo
benefit and each carries real risk.

## Decision matrix

| Component | Current | Decision | Rationale |
|---|---|---|---|
| **Backend framework** | ASP.NET Core 8 (LTS) | **Retain** now, **Upgrade** to **.NET 10 LTS** post-demo | net8 builds green and is LTS; net10 is the newer LTS (SDK already installed, obj shows a prior net10 attempt). Excellent fit for a transactional commercial core. No demo reason to move today. |
| **Backend architecture** | Layered monolith (controllers/services/repos) | **Refactor** incrementally → **modular monolith** with domain boundaries + vertical slices | A monolith is the *correct* deployment shape at this scale. Improve internal boundaries over time; do **not** split into microservices now. |
| **ORM / schema** | EF Core, **database-first (scaffolded)** | **Refactor** → **EF migrations as source of truth** (baseline from current schema) | Database-first blocks versioned schema + rollback, which the product principles require. Keep EF Core; adopt migrations. |
| **Database engine** | SQL Server (remote) | **Retain**; **Defer** PostgreSQL evaluation | SQL Server works and holds the data. Postgres+RLS is attractive for tenant isolation and licensing later, but a migration now is high-risk, zero demo value. Add a **local dev/seed DB path** so we're not hostage to a remote server. |
| **Tenant isolation** | App-level BusinessUnit scoping | **Harden** now; **Defer** DB-level RLS | Enforce tenant scoping on every query first (see security findings). RLS is a later defense-in-depth layer (natural with Postgres). |
| **AI / LLM provider** | Ollama cloud (deepseek) | **Replace** → **Claude**, behind an `ILlmProvider` seam (see ADR-0001) | Data-egress/DPA, deterministic schema output, governance. Abstraction keeps it reversible and keeps a local/offline fallback. |
| **Document parsing** | Tesseract / PdfPig / Docnet / OpenXml (.NET, in-proc) | **Retain** now; **Defer** a dedicated **Python doc-intelligence service** | .NET parsing is workable for the demo. Extract a FastAPI Python worker **only if** evidence shows .NET parsing/layout/tables are insufficient — not preemptively. |
| **Frontend framework** | React 19 + Vite 8 + TS + MUI 9 + @mui/x-data-grid | **Retain** + **Upgrade** (hardening) | Ideal for data-dense enterprise UI. Harden: TS strict, route-level code-splitting (fix the 2.6 MB single chunk), consistent TanStack Query, design tokens, real loading/empty/error states. |
| **Next.js / SSR** | none | **Reject** for the app; **Defer** for any future public portal | Internal, authenticated SPA has no SSR/SEO need. Revisit only if a public buyer/supplier portal is built. |
| **Server state** | TanStack Query (partial) | **Retain** + standardize | Already a dependency; make it the single server-state model, remove ad-hoc axios/useEffect fetching. |
| **Auth / identity** | Custom JWT + permission handler | **Retain** + **Harden**; **Defer** SSO/OIDC/SAML | Rotate the key, enforce authZ on every endpoint. Enterprise SSO is a later sales requirement. |
| **Background processing** | Hosted `BackgroundService` (email poll) | **Retain** + **Refactor** (retry/dead-letter/idempotency); **Defer** durable-workflow engine + broker | A hosted worker is fine for demo volume. Add reliability primitives. Temporal-style orchestration only when volume/complexity justifies. |
| **File / object storage** | Local `wwwroot`/`Uploads` | **Retain** now; **Defer** object storage (S3/Azure Blob) | Fine for a single-node demo; externalize when we scale or need immutable source retention guarantees. |
| **Search / vector / RAG** | none | **Defer** | RAG/vector retrieval is a NEXT/LATER intelligence feature, not demo-critical. |
| **Observability** | health/metrics/tracing (claimed in prior commit) | **Verify + Upgrade** | Confirm real health checks + structured logging exist; fill gaps. |
| **Testing** | ~1 test | **Upgrade** (priority gap) | Add unit tests for financial math + ingestion normalization, API contract tests, and a few critical E2E journeys. |

## Target shape we are converging toward

```
React 19 SPA (Vite, MUI, TanStack Query)   ── internal, data-dense, code-split
        │  typed API contracts
        ▼
ASP.NET Core (.NET 10 LTS) modular monolith  ── authoritative commercial core
   • domain modules + vertical slices          (deterministic decimal money math)
   • EF Core + migrations (SQL Server → Postgres later)
   • tenant scoping now, RLS later
   • hosted workers + retry/DLQ/idempotency
        │  ILlmProvider (versioned, schema-constrained)
        ▼
Claude (Haiku/Sonnet/Opus tiered)            ── extraction/classification/drafting
   • non-authoritative for money
   • PII-minimized, prompt-injection-contained
   (optional later: Python FastAPI doc-intelligence worker for OCR/layout/tables)
```

## What we are explicitly NOT doing (and why)

- **Not** rewriting the backend or frontend — both work and there's a demo to hit.
- **Not** adopting microservices — a modular monolith is right for this scale.
- **Not** migrating SQL Server → Postgres now — high risk, no demo value.
- **Not** adding Next.js — no SSR/public-content need for an internal SPA.
- **Not** standing up a Python service, message broker, or durable-workflow
  engine preemptively — each is deferred behind evidence.

## Consequences / sequencing

- **Demo track (now):** stays on the current retained stack; only hardening +
  the Claude extraction path + a reliable way to stand the app up.
- **Post-demo NEXT:** EF migrations baseline; .NET 10 upgrade; frontend
  code-splitting + TS strict; test suite for money + ingestion; tenant-scope
  enforcement pass.
- **LATER:** Postgres+RLS evaluation; Python doc-intelligence service (if
  evidenced); object storage; durable workflow; SSO/OIDC; RAG/vector search.
- Each deferred migration will get its own ADR with the required proof of
  limitation, measured benefit, migration/rollback/cost plan before it proceeds.
