# ADR-0004 — Deployment topology + accelerate PostgreSQL (Neon) now

- Status: **Accepted** (supersedes the "defer Postgres" stance of ADR-0002 §DB)
- Date: 2026-07-15
- Deciders: CTO/CIO (owner deferred final call to CTO), Principal System Architect,
  Principal Database Architect
- Related: ADR-0002 (stack — CONFIRMED by independent review), ADR-0003 (scale)

## Context

ADR-0002 chose "retain SQL Server now, defer Postgres." Two things changed the calculus:

1. **The product owner wants to deploy on Vercel and use Neon (serverless
   Postgres) now**, to avoid a second migration later.
2. **Ground truth measured:** the entire dataset is **948 KB** and there is **no
   production yet**. The deferral rationale in ADR-0002 assumed a costly migration
   of a *live database with data + downtime* — that cost is essentially absent
   today. Only the *schema port* remains.

Both premises were stress-tested by an independent Principal System Architect and
Principal Database Architect (see "Independent review" below).

## Decision

### 1. Deployment topology
- **Frontend (React/Vite SPA) → Vercel.** Ideal fit (static SPA; `vercel.json`
  present).
- **Backend (ASP.NET Core .NET 8 + background workers + the ADR-0003 extraction
  worker pool) → a container host** (Fly.io / Render / Railway / Azure Container
  Apps). **Vercel cannot host it:** Vercel is serverless/stateless/time-limited;
  this backend is a persistent Kestrel server with long-running workers — the
  opposite of serverless. Putting it "on Vercel" would require a Node/Next.js
  rewrite, which is rejected (throws away the .NET money engine + evidence model).
- **Database → Neon (managed serverless PostgreSQL).**

### 2. Accelerate Postgres to NOW, executed with discipline
- Move to **PostgreSQL now** (do it once, while data is trivial), **not** deferred.
- **The pilot is never bet on an unverified cutover:** `main` stays the
  pilot-ready SQL Server line; the port is done on branch `postgres-migration`,
  verified end-to-end against a **local Postgres** first (so Neon = a
  connection-string change), and merged only once proven equivalent (builds,
  connects, key flows pass).
- **MySQL was never a candidate** — for a money-bearing, deeply-relational,
  multi-tenant system of record it is the weakest option and offers none of the
  advantages below.

### 3. Why Postgres (the strategic drivers, not just licensing)
- **`jsonb`** (GIN-indexed) is the natural home for per-field AI extraction
  **evidence/provenance** (the canonical RFQ document) — a real, present advantage.
- **`pgvector`** for RAG/semantic search over RFQs/parts/suppliers — co-located
  with the data.
- Mature **row-level security** for tenant isolation (layer 3, beneath app-level
  query filters); **zero licensing cost** (SQL Server Std ≈ $3.6k/core, Ent ≈
  $13.7k/core at SaaS scale); better Linux/cloud portability.

### 4. Migration traps to handle (from the DB architect; being executed on the branch)
- `getdate()`/`sysdatetime()` defaults → `now()` (this is exactly what broke a
  naive pgloader run).
- The one **computed column** `Order.BalanceAmount` → Postgres STORED generated
  column (or compute in code).
- **Collation/case-sensitivity:** SQL Server is case-*insensitive*; email unique
  indexes and `.ToLower()` queries must become `lower(col)`/`citext` in Postgres
  to preserve uniqueness semantics.
- The keyless **view** `View_SupplierPriceList` → recreate via raw-SQL migration.
- **Map `Taxis`** (unmapped today) while porting — unblocks server-side tax (FIN-01).
- Resolve the orphan `Inventory*` model duplicates.
- Method: EF-native (Npgsql model adaptation → `dotnet ef migrations` → apply) +
  **data-only** copy (pgloader `WITH data only`, since pgloader's schema
  translation of function defaults fails).

## Independent architecture review (both confirm ADR-0002's stack)

- **System Architect: CONFIRM** retain .NET 8 + React 19 (no rewrite), modular
  monolith, React SPA (reject Next.js). Refinements (roadmap, not tech): treat the
  canonical evidence model as *the* LLM contract and redesign the `ILlmProvider`
  seam around Claude tool-use before swapping; reclassify idempotency/outbox/
  dead-letter from "later" to "next correctness" (delivered by ADR-0003's DB queue,
  no broker); reorganize leaky horizontal layering (transactions in controllers,
  services bypassing repos) into vertical slices with a real Unit of Work.
- **Database Architect: CONFIRM** SQL Server→Postgres direction; **REFINE** to a
  dated commitment (this ADR). Key corrections: (a) add **EF global query filters
  now** — tenant scoping is currently opt-in hand-written `.Where`, no
  `HasQueryFilter` exists; (b) **uniqueness is inverted** — doc numbers aren't
  unique (duplication risk) while master-data emails are *globally* unique
  (blocks legit multi-tenant + enumeration oracle); fix both; (c) the EF baseline
  has **model/DB drift** (unmapped `Taxis`/`Inventory*`, the view) — reconcile
  before it's canonical.

## Consequences
- One-time port now; no second migration; lands on the strategic target stack.
- Enables jsonb-based evidence storage (ADR-0003 provenance) and future pgvector RAG.
- Requires: Neon account provisioning (owner action), a container host for the
  backend, and CI/deploy wiring. The codebase is being made provider-configurable
  so the same build runs on local Postgres and Neon by connection string.
