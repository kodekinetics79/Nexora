# Platform-Owner Control Plane — Wiring Guide (ADR-0005, Phase 1)

All code lives under `Platform/` and was written to **not touch any existing
file**. To activate it, the orchestrator applies the central edits below to
`Program.cs`, `Models/ErpRfqAutomationContext.cs`, adds a migration, and seeds one
Owner. Nothing here changes tenant-plane behavior.

The new files reference the DbContext via `context.Set<T>()`, so they compile
today (verified: `dotnet build` → **0 errors**). They will only *run* correctly
once the DbSets + model configs below are added and a migration is applied.

---

## 1. DbSets — add to `Models/ErpRfqAutomationContext.cs`

Add these properties alongside the existing `DbSet` declarations:

```csharp
public virtual DbSet<ERP_RFQ_Automation.Platform.Models.PlatformUser> PlatformUsers { get; set; }
public virtual DbSet<ERP_RFQ_Automation.Platform.Models.Tenant> Tenants { get; set; }
public virtual DbSet<ERP_RFQ_Automation.Platform.Models.Plan> Plans { get; set; }
public virtual DbSet<ERP_RFQ_Automation.Platform.Models.PlatformAuditLog> PlatformAuditLogs { get; set; }
```

## 2. Model configuration — add to `OnModelCreatingPartial`

The tenancy partial `Models/ErpRfqAutomationContext.Tenancy.cs` already defines
`partial void OnModelCreatingPartial(ModelBuilder modelBuilder)`. **Do not add a
second partial with the same signature** — instead append these lines inside the
existing method body (or create a new partial method is not allowed, so append):

```csharp
// ---- Platform-Owner control plane (ADR-0005 §3/§4) --------------------------
// Placed in the (non-RLS'd) "platform" schema. These tables are NOT ITenantScoped
// and carry NO query filter — the platform plane reads across tenants by design.

modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.PlatformUser>(e =>
{
    e.ToTable("PlatformUsers", "platform");
    e.HasKey(x => x.Id);
    e.HasIndex(x => x.Email).IsUnique();
    e.Property(x => x.Email).IsRequired().HasMaxLength(256);
    e.Property(x => x.PasswordHash).IsRequired();
    e.Property(x => x.PlatformRole).HasConversion<string>().HasMaxLength(32); // store enum as text
    e.Property(x => x.DisplayName).HasMaxLength(200);
});

modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.Plan>(e =>
{
    e.ToTable("Plans", "platform");
    e.HasKey(x => x.Id);
    e.HasIndex(x => x.Code).IsUnique();
    e.Property(x => x.Code).IsRequired().HasMaxLength(64);
    e.Property(x => x.Name).IsRequired().HasMaxLength(128);
    e.Property(x => x.Features).HasColumnType("jsonb").HasDefaultValue("{}");
});

modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.Tenant>(e =>
{
    e.ToTable("Tenants", "platform");
    e.HasKey(x => x.Id);
    e.HasIndex(x => x.Slug).IsUnique();
    e.Property(x => x.Name).IsRequired().HasMaxLength(256);
    e.Property(x => x.Slug).IsRequired().HasMaxLength(64);
    e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
    e.Property(x => x.StatusReason).HasMaxLength(1000);
    e.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.SetNull);
    // PrimaryBusinessUnitId intentionally NOT an EF relationship — it bridges to the
    // existing BusinessUnit table without coupling the platform schema to tenant FKs.
});

modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.PlatformAuditLog>(e =>
{
    e.ToTable("PlatformAuditLogs", "platform");
    e.HasKey(x => x.Id);
    e.Property(x => x.Action).IsRequired().HasMaxLength(128);
    e.Property(x => x.TargetType).HasMaxLength(128);
    e.Property(x => x.TargetId).HasMaxLength(128);
    e.Property(x => x.Metadata).HasColumnType("jsonb");
    e.Property(x => x.Ip).HasMaxLength(64);
    e.HasIndex(x => new { x.ActorPlatformUserId, x.CreatedOn });
    e.HasIndex(x => new { x.ActAsTenantId, x.CreatedOn });
    // Append-only: application code never updates/deletes. Enforce at the DB layer
    // with a trigger or a role that lacks UPDATE/DELETE on this table (see §6).
});
```

> These entities are plain (non-`ITenantScoped`) types, so they get **no global
> query filter** — but the new code still calls `.IgnoreQueryFilters()` on reads
> defensively and to read across tenants (`Lead`/`Rfq`/`Quote`/`Order`).

## 3. Second JWT scheme + policies — `Program.cs`

The default (tenant) scheme is unchanged. Chain the platform scheme onto the
existing `AddAuthentication().AddJwtBearer(...)` call, and add the platform
policies to the existing `AddAuthorization(...)` call.

**a) Authentication** — after the existing `.AddJwtBearer(options => { ... })`
(around line 182), append:

```csharp
    .AddPlatformJwtBearer(builder.Configuration);   // ERP_RFQ_Automation.Platform.Auth
```

Add the using at the top of `Program.cs`:

```csharp
using ERP_RFQ_Automation.Platform.Auth;
```

**b) Authorization** — inside the existing
`builder.Services.AddAuthorization(options => { ... })` block (Program.cs ~106),
add one line:

```csharp
    options.AddPlatformPolicies();   // PlatformScope (default-deny) + role sub-policies
```

This registers: `PlatformScope` (default-deny gate on every `/api/platform/*`
endpoint), `Platform.Owner`, `Platform.TenantAdmin` (Owner|SupportAdmin),
`Platform.Billing` (Owner|BillingAdmin), `Platform.Impersonate` (Owner|SupportAdmin).
Every policy pins the `"Platform"` scheme and requires `scope=platform`.

### Why the boundary holds (five gates, ADR-0005 §3)
- **Gate 1 (audience/scheme):** the platform scheme validates
  `ValidAudience = "nexora-platform"`. A **tenant token** (audience `"RFQ"`) fails
  validation on the platform scheme; a **platform token** fails on the default
  tenant scheme. Confirmed by construction in `PlatformAuthService`.
- **Gate 2 (default-deny policy):** `[Authorize(Policy="PlatformScope")]` on every
  platform controller; the policy pins the `Platform` scheme + requires
  `scope=platform`, so a tenant principal is never even authenticated against it.
- The platform token carries **no `businessUnitId`/`roleId` claim**, so it can
  never satisfy `HttpTenantContext` or the tenant `PermissionHandler`.

## 4. DI registrations — `Program.cs`

Add near the other `AddScoped` registrations (after line ~104):

```csharp
// Platform-Owner control plane (ADR-0005)
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Auth.IPlatformAuthService,
                           ERP_RFQ_Automation.Platform.Auth.PlatformAuthService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Services.IPlatformAuditService,
                           ERP_RFQ_Automation.Platform.Services.PlatformAuditService>();
```

Controllers are discovered automatically (they are `[ApiController]` in the same
assembly) — no manual registration.

## 5. Configuration keys — deployment secrets and environment

The platform scheme reads these (all optional; safe fallbacks shown):

```jsonc
"Jwt": {
  // existing tenant keys: Key, Issuer ("KodeKinetics"), Audience ("RFQ"), ExpiryMinutes
  "PlatformKey": "<distinct 32+ byte signing key>", // REQUIRED outside Development/Testing
  "PlatformIssuer": "KodeKinetics",                  // falls back to Jwt:Issuer
  "PlatformAudience": "nexora-platform",             // default if omitted
  "PlatformExpiryMinutes": 30,                       // default 30
  "ImpersonationExpiryMinutes": 15                   // default 15 (tenant token)
}
```

Outside Development/Testing, `Jwt:PlatformKey` is required and must differ from
`Jwt:Key`; startup fails otherwise.

Production must also make an explicit network-access choice. Paid-pilot deployments
should use `AllowList` and supply each approved office/VPN/operator network:

```jsonc
"PlatformAccess": {
  "NetworkMode": "AllowList",
  "AllowedCidrs": [ "203.0.113.0/24", "2001:db8:42::/48" ]
}
```

`NetworkMode=Any` is an explicit exception for environments without a private/VPN
operator boundary. Missing mode in Production, an empty allow-list, or any malformed
CIDR fails closed. The gate reads only `Connection.RemoteIpAddress` after
`UseForwardedHeaders`; configure `ForwardedHeaders:KnownProxies` or `KnownNetworks`
for the actual Render proxy boundary. It never trusts raw `X-Forwarded-For`.

> The impersonation token is deliberately a **tenant** token (signed with `Jwt:Key`,
> audience `Jwt:Audience`) so it is accepted by the default scheme and scoped by the
> tenant query filters; it carries `impersonated=true`, `act_sub`, and no `roleId`
> (so RBAC-gated writes fail → read-only).

## 6. Migration

A migration is required to create the four `platform.*` tables + indexes.

```bash
cd Backend/ERP_RFQ_Automation
dotnet ef migrations add PlatformControlPlane
dotnet ef database update
```

Notes:
- The `platform` schema is created by the migration (from `ToTable(..., "platform")`).
- **Append-only enforcement for `PlatformAuditLogs`:** after the migration, add a
  DB guard (a `BEFORE UPDATE OR DELETE` trigger that raises, or `REVOKE UPDATE,
  DELETE ON platform."PlatformAuditLogs"` from the app role). The app never issues
  those statements, but the DB should make it impossible.
- Per ADR-0005 §3, the platform pipeline is the only one that should hold
  `BYPASSRLS`; the `platform` schema is intentionally **not** RLS-filtered.

## 7. Seed one Owner PlatformUser

There is no self-service platform signup by design. Seed the first Owner out-of-band.

**Option A — SQL (compute a BCrypt hash first):**

```sql
INSERT INTO platform."PlatformUsers"
  ("Email","PasswordHash","PlatformRole","IsActive","DisplayName","CreatedOn","CreatedBy")
VALUES
  ('owner@nexora.app', '<bcrypt-hash>', 'Owner', true, 'Platform Owner', now(), 'seed');
```

Generate `<bcrypt-hash>` with the same library the app uses (`BCrypt.Net-Next`),
e.g. a throwaway console line: `BCrypt.Net.BCrypt.HashPassword("<password>")`.
`PlatformRole` is stored as **text** (`Owner` / `SupportAdmin` / `BillingAdmin` /
`ReadOnlyOps`).

**Option B — migration seed / idempotent startup task:** insert the same row via
`migrationBuilder.InsertData(...)` (only if you want it version-controlled; keep the
hash out of source — read the password from configuration/secret at seed time).

Then log in: `POST /api/platform/auth/login { "email": "...", "password": "..." }`
→ returns a `nexora-platform` token to use as `Authorization: Bearer` against
`/api/platform/*`.

---

## New files delivered

```
Platform/
├─ WIRING.md                                  (this file)
├─ Models/
│  ├─ PlatformEnums.cs      PlatformRole, TenantStatus
│  ├─ PlatformUser.cs       control-plane operator (no tenantId)
│  ├─ Tenant.cs             customer org (Status, PlanId, PrimaryBusinessUnitId)
│  ├─ Plan.cs               tier (Weight, MaxConcurrentExtractionJobs, quotas, Features)
│  ├─ PlatformAuditLog.cs   append-only audit
│  └─ PlatformDtos.cs       login / tenant / impersonation DTOs
├─ Auth/
│  ├─ PlatformAuthConstants.cs   scheme/audience/claim/policy names
│  ├─ PlatformAuthExtensions.cs  AddPlatformJwtBearer + AddPlatformPolicies
│  └─ PlatformAuthService.cs     BCrypt login, platform-token + impersonation-token issue
├─ Services/
│  └─ PlatformAuditService.cs    append-only audit writer
└─ Controllers/                  route /api/platform/*  [Authorize(PlatformScope)]
   ├─ PlatformAuthController.cs   POST /auth/login  (anonymous)
   ├─ TenantsController.cs        list/get/provision(txn)/suspend/resume
   ├─ ObservabilityController.cs  cross-tenant stats (degrades if tables absent)
   └─ ImpersonationController.cs  POST /tenants/{id}/impersonate (read-only, audited)
```

## Endpoint summary

| Method & route | Policy | Notes |
|---|---|---|
| `POST /api/platform/auth/login` | anonymous | issues `nexora-platform` token |
| `GET  /api/platform/tenants` | PlatformScope | `?status=` filter |
| `GET  /api/platform/tenants/{id}` | PlatformScope | |
| `POST /api/platform/tenants` | TenantAdmin | provision Tenant + primary BU (txn) |
| `POST /api/platform/tenants/{id}/suspend` | TenantAdmin | reason required |
| `POST /api/platform/tenants/{id}/resume` | TenantAdmin | reason required |
| `GET  /api/platform/observability/stats` | PlatformScope | per-BU counts; extraction stats degrade gracefully |
| `POST /api/platform/tenants/{id}/impersonate` | Impersonate | short-lived read-only tenant token, audited |
```

---

## Platform Admin 360 — WS-B increment (control-plane surface)

### Program.cs line required (Integration Owner)

```csharp
// After the existing DemoUserSeeder.EnsureAsync(...) call (~line 658), before app.Run():
await app.SeedPlatformOwnerAsync();   // using ERP_RFQ_Automation.Platform.Services;
```

No new service registrations are needed: the new `PlatformUsersController` uses the
already-registered `ErpRfqAutomationContext` + `IPlatformAuditService`, and
`ReadOnlyImpersonationMiddleware` resolves the DbContext per request from
`HttpContext.RequestServices`.

### Schema changes (single migration to be generated by the Integration Owner)

| Table (schema `platform`) | Change |
|---|---|
| `PlatformAuditLogs` | new column `Result` — string, required, max 16, DB default `'success'` |
| `Plans` | new column `MonthlyPriceUsd` — `decimal(10,2)`, nullable |
| `ImpersonationSessions` | NEW TABLE: `Id` PK, `Jti` (required, max 64, unique index), `TenantId`, `ActorPlatformUserId`, `Reason` (required, max 1000), `IssuedAtUtc`, `ExpiresAtUtc`, `RevokedAtUtc?`, `RevokedBy?` (max 256); indexes `(TenantId, IssuedAtUtc)` and `(ExpiresAtUtc)` |

`ImpersonationSessions` is platform-schema (non-tenant): no query filter, exempt from
tenant invariants; add the usual REVOKE for `nexora_tenant_app` in the merged migration.
NOTE: the tenant app role's revocation lookup runs in `ReadOnlyImpersonationMiddleware`
under the REQUEST's database role — grant SELECT on `platform."ImpersonationSessions"`
to whichever role serves impersonated (tenant-token) requests.

### New / changed endpoints

| Method & route | Policy | Notes |
|---|---|---|
| `POST /api/platform/tenants/{id}/archive` | TenantAdmin | Suspended → Archived, reason required, audited `tenant.archive` |
| `POST /api/platform/tenants/{id}/restore` | TenantAdmin | Archived → Suspended, reason required, audited `tenant.restore` |
| `PUT  /api/platform/tenants/{id}/plan` | TenantAdmin | plan must exist + be active, audited `tenant.plan.change` |
| `GET  /api/platform/users` | Owner | list platform operators |
| `POST /api/platform/users` | Owner | create (unique email, BCrypt), audited `platform-user.create` |
| `PUT  /api/platform/users/{id}/role` | Owner | last-active-Owner demotion blocked, audited |
| `POST /api/platform/users/{id}/deactivate` | Owner | self + last-active-Owner blocked, audited |
| `POST /api/platform/users/{id}/reactivate` | Owner | audited |
| `POST /api/platform/users/{id}/password` | Owner | admin reset, secret never audited |
| `POST /api/platform/plans` | Owner | code unique, audited `plan.create` |
| `PUT  /api/platform/plans/{id}` | Owner | code unique, audited `plan.update` |
| `GET  /api/platform/impersonation/sessions` | PlatformScope | active + last-7-days sessions |
| `POST /api/platform/impersonation/{jti}/revoke` | Impersonate | audited `impersonate.revoke`; enforced by middleware ≤30s |

### Behavioral notes

- `platform.login` / `platform.login.failed` are now audited; failed logins are the ONLY
  records allowed to use the reserved system actor id 0 (`PlatformAuditService.SystemActorId`).
- `/api/platform/audit` returns the real `Result` column, `?result=failure` filters for
  real, and `?search=` is applied in SQL BEFORE the 500-row cap (Metadata is no longer
  searched — jsonb has no portable text-search translation).
- Impersonation token TTL is clamped to [5, 60] minutes; every issued token writes an
  `ImpersonationSessions` row keyed by `jti`, and tokens whose row is missing, revoked
  or expired are rejected (401) by `ReadOnlyImpersonationMiddleware` (30s cache bound).
- Overview: `seatsInUse` renamed to `activeUsersFleetWide` (honest label); plan buckets
  are real plan codes with `none` for tenants without a plan (frontend workstream must
  adapt `/platform` Overview + Plans pages).
- `Platform:BootstrapOwnerEmail` + `Platform:BootstrapOwnerPassword` seed ONE Owner at
  startup, only when the PlatformUsers table is completely empty (fail-closed, never
  overwrites, no hardcoded credentials, secrets never logged).

---

## Per-tenant module entitlements (20260818013530)

Module and capability access moved off the **plan** and onto the **tenant**.

Before this, `EntitlementService.CheckFeatureAsync` resolved every typed key from
`Plan.Features`, so what a customer could reach was a property of its price tier. Granting
one tenant Procurement, or revoking Inventory from one tenant, could only be expressed by
moving them to a different plan — which also moved their seat cap, their monthly document
quota and their price. Operators therefore cloned a plan per customer and the plan catalogue
stopped describing the commercial offer. The tenant Entitlements tab reflected this honestly:
it was 74 lines of read-only chips.

### Where authority now sits

| Question | Answered by |
|---|---|
| Which modules/capabilities may this customer open? | `Tenants."Entitlements"` (jsonb, NOT NULL, default `{}`) |
| How many seats / documents / concurrent extractions? | `Plans` |
| What is this customer charged? | `Plans` + rate card |

`Plan.Features` still exists and is still editable. It is now a **provisioning template**:
copied into `Tenant.Entitlements` once, when the tenant row is created, and never read again
at runtime. Editing a plan therefore cannot re-open a module an operator deliberately revoked
from a live customer.

### What the migration does

1. `AddColumn Tenants."Entitlements"` — jsonb, NOT NULL, store default `{}` (an unknown write
   path grants nothing rather than everything).
2. Backfills every tenant from its current plan's features, so **nobody's access changed on
   the day it shipped**. Tenants with no plan land on `{}` — which is what they effectively
   had, since `CheckFeatureAsync` denied every key for a plan-less tenant.
3. `GRANT SELECT ("Entitlements") ON platform."Tenants" TO nexora_tenant_app,
   nexora_identity_app`, guarded on the role existing. The column is projected by
   `TenantAccessService.CoreQuery`, so it is also declared in
   `TenantAccessGrantContract.RequiredColumns` — a deployment missing the grant refuses to
   boot rather than answering 42501 on every tenant request. `nexora_pipeline_app` needs
   nothing; it holds table-level SELECT.

### Endpoints

| Method & route | Policy | Notes |
|---|---|---|
| `GET /api/platform/tenants/{id}/modules` | PlatformScope | catalogue order, with `available` and `fromPlanTemplate` per key |
| `PUT /api/platform/tenants/{id}/modules` | Billing | wholesale replace; reason ≥ 15 chars; audited `tenant.modules.update`; evicts the tenant-access cache |

`Platform.Billing` (Owner | BillingAdmin), the same authority that assigns a plan: deciding
what a customer may open is a decision about scope of supply, and giving it to SupportAdmin
would hollow out the separation that keeps support out of pricing.

The write is **wholesale, not a patch**. A partial write cannot distinguish "off" from
"undecided", and the `entitlements.typed-hard-limits` activation control requires every key
to be present — so a patch API would let an operator leave a customer permanently
unactivatable through a screen that looked like it had saved. The server runs the request
through `TypedEntitlementCatalog.Complete` on the way in.

### Seeding paths that must set it

Three places create a `Tenant` row, and all three copy the plan's features:
`ProvisioningStepExecutor.CreateTenantAsync` (the governed path),
`TenantsController.Provision` (the synchronous one) and `GovernedPlatformTenantSeeder`
(local/CI, which also fills an existing tenant whose grant is still empty).

### Still unbuilt

Five catalogue keys — `capability.api`, `capability.automation`, `capability.sso`,
`capability.scim`, `capability.dedicated-resources` — are `RuntimeUnavailableBoundary`:
runtime authorization denies them however the grant reads. The console marks them
"Not built yet" on both the tenant Modules tab and the plan editor rather than offering a
switch that grants nothing.
