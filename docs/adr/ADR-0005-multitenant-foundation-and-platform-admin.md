# ADR-0005 — Multi-tenant foundation + Platform-Owner control plane

- Status: **Accepted** (design); implementation phased (Phase 0 rides the Postgres port)
- Date: 2026-07-15
- Deciders: CTO/CIO, Principal SaaS Platform Architect, Principal DB Architect
- Related: ADR-0002 (stack), ADR-0003 (scale pipeline), ADR-0004 (Postgres/deploy)

## Context

Nexora must serve **many client organizations** with **growing** document volume,
and needs a **Platform Owner** control plane above tenants. Today tenancy = a flat
`BusinessUnit`; there is **no platform layer** (any authenticated user can CRUD
every BusinessUnit — `BusinessUnitController.cs:23-153`), **no EF global query
filters** (scoping is hand-written `.Where` with a `param ?? claim` fallback), and
the permission check ignores tenant (`RolePermissionRepository.cs:130-147`).

## Decisions

### 1. Tenancy model — introduce a first-class `Tenant` above `BusinessUnit`
`BusinessUnit` conflates *customer* (billing/plan/isolation/lifecycle) with
*internal division* (RBAC/config scope). Split them:
```
Tenant (Organization)   ← isolation + billing + lifecycle + plan/quota
   └── BusinessUnit(s)   ← intra-tenant division (keeps RBAC / SetupMaster roles / QuoteConfiguration)
          └── Users, Leads, Rfqs, Quotes, Orders, Customers, Suppliers, Products …
```
- Add `TenantId NOT NULL` to `BusinessUnit` and **every tenant root**, and
  **denormalize `TenantId` onto line-item children** (`LeadItem`, `Rfqitem`,
  `OrderItem`, `QuoteItem`) so RLS + partitioning act without a join.
- JWT gains a `tenantId` claim + `scope=tenant` + audience `nexora-tenant`.
- Migration seeds **one Tenant per existing BusinessUnit (1:1)**, backfills, flips
  `NOT NULL` — done inside the ADR-0004 Postgres port (948 KB, zero downtime cost).

### 2. Isolation — defense in depth
- **(a) EF global query filters NOW:** an `ITenantScoped` marker + a scoped
  `ITenantContext` (from the claim) → `HasQueryFilter` on every tenant entity.
  Replaces hand-written `.Where`; **remove the `param ?? claim` fallback**
  (`UserController.cs:46,83,133`) — tenant derivation becomes claim-only. Explicit
  `IgnoreQueryFilters()` for platform-plane and worker sweeps.
- **(b) Postgres RLS as the DB backstop:** app connects as a role **without
  BYPASSRLS**; per request `SET LOCAL app.tenant_id=<claim>` inside the
  unit-of-work transaction; policies `USING (tenant_id = current_setting(...))`.
  A forgotten filter then **cannot** leak. **Neon caveat (budgeted):** the pooled
  endpoint is PgBouncer *transaction* mode → use `SET LOCAL` within an explicit
  txn (a `DbConnectionInterceptor`) and `Max Auto Prepare=0`.
- **(c) Escalation model:** shared-schema + RLS is the default for most tenants;
  **reject schema-per-tenant**; reserve **DB-per-tenant** (dedicated Neon
  project) for a whale or a data-residency/hard-isolation contract — supported by
  a `tenant→datasource` catalog resolved in `ITenantContext` (design the seam now,
  keep the default shared).

### 3. Identity & the hard Platform/Tenant boundary
- **`PlatformUser` is a separate table and a separate token** — never a super-row
  in `Users`. It has no tenant claim. `POST /api/platform/auth/login` issues a
  token with audience **`nexora-platform`**, `scope=platform`, `platformRole`
  (Owner/SupportAdmin/BillingAdmin/ReadOnlyOps); MFA + IP allowlist in prod.
- **Five independent gates:** (1) token audience/scheme — a tenant token *fails
  validation* on a platform endpoint and vice-versa; (2) `PlatformScope`
  default-deny policy on `/api/platform/*`; (3) route/ingress separation
  (`admin.nexora.app`, network-restrictable); (4) DB role separation (only the
  platform pipeline gets BYPASSRLS); (5) tenant writes derive `tenantId` from the
  claim and validate `target.TenantId == caller.TenantId`.
- **Support impersonation:** `POST /platform/tenants/{id}/impersonate` (reason
  required) mints a **short-lived, read-only-by-default** tenant token stamped
  `act_sub`, `impersonated=true`; every action audited with both actors; tenant
  UI shows a banner; sessions revocable.

### 4. Platform-owner control plane
- **New `platform` schema (not RLS'd):** `Tenant`, `PlatformUser`, `Plan`
  (weight, MaxConcurrentExtractionJobs, quotas, LlmMonthlyBudget, Features jsonb),
  `TenantEntitlement`/`FeatureFlag`, `LlmCallLog` (per-call cost), `UsageMeter`
  (rollups), `PlatformAuditLog` (append-only), `BillingAccount`.
- **API `/api/platform/*`:** tenant CRUD + provisioning + suspend/resume/archive/
  offboard + impersonate + export; cross-tenant user/seat mgmt; plans/entitlements/
  feature-flags; **observability** (global + per-tenant queue depth, in-flight,
  DLQ rate, extraction success/error, LLM cost per tenant/model); usage/billing;
  global config; audit; system health.
- **Console = a separate SPA** (own Vercel project `admin.nexora.app`, `scope=
  platform` guard) so owner code never ships in the tenant bundle. (A guarded
  `/platform/*` route tree in the current SPA is the lighter pilot fallback.)

### 5. Job-queue fairness (extends ADR-0003)
Global FIFO lets one tenant's 1,000-doc batch starve others. Adopt **Weighted
Fair Queuing + hard per-tenant concurrency cap + admission control:**
- `ExtractionJobs` gains `TenantId` + `SchedulerTag` (virtual-time double);
  `TenantQueueState(TenantId, InFlight, LastVTime, Weight)`.
- Enqueue tag `vtime = max(now_virtual, tenant.LastVTime) + cost/weight` (cost ≈
  line-item count, weight = plan tier) → batches get monotonically increasing tags
  that interleave fairly; higher plan → larger share; inherent anti-starvation.
- Claim skips tenants at their `MaxConcurrentExtractionJobs` cap; same txn
  increments `InFlight`; `ORDER BY Priority, SchedulerTag … FOR UPDATE SKIP
  LOCKED`. Coarse `Priority` lets an interactive single upload jump a tenant's own
  bulk batch. Token-bucket admission on enqueue → `Throttled` state on overflow.
- Plan map: Free (w1/cap2), Pro (w4/cap8), Enterprise (w16/cap32, optional
  dedicated pool/DB). Rides on ADR-0003's global LLM semaphore + circuit breaker +
  lease/retry/DLQ + content-hash idempotency.

### 6. Foundation-scaling checklist
Neon **pooled** datasource (singleton `NpgsqlDataSource`, txn-mode-safe, direct
connection for migrations/long worker txns); **tenant-aware caching** (keys
include tenantId; cache the per-request permission lookup `PermissionHandler.cs:32`);
**per-tenant rate limiting** (ASP.NET limiter partitioned by tenant claim);
**OpenTelemetry with a tenant dimension** (the genuinely-open observability item);
**workers as a separate horizontally-scaled deployment**; **partitioning** —
lead PKs with `TenantId` now (also fixes inverted uniqueness DATA-09: unique
`(TenantId, Rfqno)`, master email `(TenantId, lower(email))`), range-partition
append tables by month, hash-by-tenant on `LeadItems`/`Rfqitems` when size
justifies (one migration, not a redesign).

## Phased implementation
- **Phase 0 (rides the Postgres port, now):** add `Tenant` + `TenantId`
  everywhere (+ denormalize to children); EF global query filters + claim-only
  tenant derivation; `tenantId`/`scope`/audience in the token; RLS + `SET LOCAL`
  interceptor; fix inverted uniqueness; close the BU-CRUD hole + tenant-correct
  permission checks.
- **Phase 1 — MVP platform admin (unblocks multi-tenant pilot):** `PlatformUser`
  + platform login/scheme/policy + `/api/platform/*` default-deny; tenant CRUD +
  provisioning + suspend/resume; read-only impersonation + audit; basic
  cross-tenant observability; 2–3 plan tiers enforcing the per-tenant concurrency
  cap; separate `/platform` console area.
- **Phase 2 — fairness + metering + hardening:** WFQ scheduler; `LlmCallLog`/
  `UsageMeter` + cost console + Stripe webhooks; OTel tenant dimension; per-tenant
  rate limiting; Redis tenant-aware cache; feature flags; full audit UI; Neon
  pooled datasource; worker as its own deployment.
- **Phase 3 — scale-out:** measured partitioning; DB-per-tenant + dedicated worker
  pool for whales/regulated tenants; data-residency regions; automated
  offboarding/purge; pgvector RAG per tenant.

## Consequences
This is the "extremely powerful foundation" requirement made concrete: isolation
that fails closed, a control plane with a hard privilege boundary, fair scheduling
so growth doesn't cause starvation, and a partitioning/scale path that's a
migration rather than a rewrite. Phase 0 is done *with* the Postgres move so the
tenant model is native from day one rather than retrofitted.
