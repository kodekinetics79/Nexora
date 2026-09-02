# Token revocation for the tenant plane (SecurityStamp)

Stream 4, item A. Status: designed 2026-09-02, implemented in the same PR.

## Problem

A tenant JWT lives 60 minutes (`appsettings.json` `Jwt:ExpiryMinutes`, minted in
`Repositories/AuthRepository.cs:253-271`) and nothing re-checks the account after the
signature check. Deactivating a user, demoting them, or changing their password leaves every
token they already hold fully usable until it expires. `Program.cs:633-639` records this as
"backlog item 2" and shrinks the clock skew to 30 s, which trims the window from 65 to 60
minutes and nothing more.

The platform plane already has the mechanism: `Platform/Auth/PlatformAuthExtensions.cs:79-101`
wires `OnTokenValidated` to `IPlatformSessionValidator`, which fails closed against a session
row and a `SessionGeneration` counter. The tenant scheme has no `Events` at all.

## Current mechanism (file:line)

| Concern | Where | What happens today |
|---|---|---|
| Token issue | `Repositories/AuthRepository.cs:253-271` | claims: sub, email, name, roleId, businessUnitId, jti. No revocation handle. |
| Token validation | `Program.cs:621-640` | signature, issuer, audience, lifetime only. |
| Deactivate (tenant admin) | `Controllers/UserController.cs:341-470` (`Update`, `IsActive=false`) | row updated, tokens untouched. |
| Role change (tenant admin) | `Controllers/UserController.cs:424` | same. |
| Password change (self) | `Controllers/UserController.cs:482-530` → `Repositories/UserRepository.cs:289-310` | same. |
| Deactivate (platform console) | `Platform/Controllers/TenantUsersController.cs:366-405` | same; invitations are withdrawn, tokens are not. |
| Role change (platform console) | `Platform/Controllers/TenantUsersController.cs:470-510` | same. |
| Password reset (self-service) | `Security/PasswordReset/PasswordResetService.cs:449-456` (`ExecuteUpdateAsync`, runs as `nexora_identity_app`) | same. |
| Activation (invitation) | `Platform/Onboarding/TenantAdminInvitationService.cs:330-336` (`ExecuteUpdateAsync`) | sets password + IsActive. |
| Personal-data erasure | `Platform/Lifecycle/TenantPersonalDataEraser.cs:139-150` (`ExecuteUpdateAsync`) | credential made unusable, tokens untouched. |
| Impersonation tokens | `Platform/Auth/PlatformAuthService.cs:598-610`, `Security/ReadOnlyImpersonationMiddleware.cs` | sub = `impersonation:{tenantId}`, revocable through `ImpersonationSession` with a 30 s cache. |

No existing column can serve as a stamp: `ModifiedOn` moves on every profile edit (name,
timezone, image) and would log people out for editing their own avatar; `DeactivatedAtUtc`
only covers one of the three events; `LastLogin` is written on login, not on revocation.

## Proposal

1. **`Users.SecurityStamp`** — `character varying(64) NOT NULL`, an opaque random value that
   changes whenever the account's authority changes. CLR default `SecurityStamps.NewStamp()`
   on the entity, so every existing `new User { ... }` (controllers, provisioning, seeders,
   tests) gets a stamp without being edited. Migration `20260902120000_UserSecurityStamp`
   (the ONE migration for this stream): `ADD COLUMN ... DEFAULT encode(gen_random_bytes(16),
   'hex')` (volatile default ⇒ PostgreSQL evaluates it per row, so every existing user gets a
   distinct stamp), the default **kept** so a raw-SQL insert that omits the column (ops
   scripts, fixtures, three existing guard tests) succeeds with its own stamp — the first cut
   dropped it and those inserts failed with 23502 — then `GRANT UPDATE("SecurityStamp") ON
   "Users" TO nexora_identity_app` guarded by role existence like every other grant migration.
   The model declares the same `HasDefaultValueSql`, so the snapshot agrees; the SQLite lane
   never evaluates it because the entity initialiser always supplies the value.
   *Why the grant:* `nexora_identity_app` holds table-level SELECT and column-level UPDATE on
   `Password_Hash` and `IsActive` only (verified on production: `has_column_privilege`).
   Password reset and invitation activation rotate the stamp on that role, so without the
   column grant both would fail with 42501 the first time a customer reset a password.
   `nexora_tenant_app` and `nexora_pipeline_app` hold table-level UPDATE, so they are covered.
   RLS: `Users` already carries `nexora_tenant_isolation`
   (`MigrationsBaseline/Sql/08_row_level_security.sql:1066-1069, 3925-3936`); a column adds no
   policy work. The over-grant guard (`PostgreSqlProductionDialectTests.cs:1018-1056`) lists
   append-only ledgers, not `Users`; the RLS-without-grant guard (`:918-940`) needs SELECT,
   which `Users` has. Both stay green; a new test asserts the column grant directly.
2. **Claim `sst`** on tenant tokens (`AuthRepository.GenerateJwtToken`).
3. **`ITenantSessionValidator`** (`Security/TenantSessionValidator.cs`), invoked from the tenant
   scheme's `OnTokenValidated`:
   - impersonation tokens (`impersonated=true`) are exempt — they have no user row and their
     own revocation ledger;
   - a token with **no** `sst` claim is a token this build did not issue (minted before
     deploy). It is accepted unchanged until it expires, unless `Auth:RequireSecurityStamp`
     is `true`. Default false for the first deploy, so nobody is logged out by the release;
     flip it afterwards (see rollout). A missing claim is not a forgery — signatures still
     bind it — so this trades at most one hour of the old behaviour for a clean deploy;
   - otherwise: push the tenant scope from the `businessUnitId` claim
     (`ITenantScopeAccessor.Push`) and do the read in a **dedicated DI scope**.
     `HttpContext.User` is not yet assigned during `OnTokenValidated`; `HttpTenantContext` and
     `TenantRlsCommandInterceptor` are request-scoped and capture the tenant at construction,
     so resolving the request's DbContext here would freeze the whole request at "no tenant"
     (found the hard way: 17 authenticated HTTP tests went 403). The validator is a singleton
     holding `IServiceScopeFactory`; the request's scope is never touched. Then load
     `{IsActive, RoleId, SecurityStamp}` for `sub` under that scope, and reject unless
     `IsActive == true`, the stamp matches, and `roleId` matches (the role compare is
     belt-and-braces; rotation already covers it);
   - cache the DB answer in `IMemoryCache` for `TenantSessionValidator.CacheTtl` which IS
     `ReadOnlyImpersonationMiddleware.CacheTtl` (30 s) — one constant, not two that drift;
   - fail closed on any exception (mirrors the platform validator). The cache means a
     DB blip costs at most one uncached request per user per 30 s.
4. **Rotation** = `user.SecurityStamp = SecurityStamps.NewStamp()` plus
   `ITenantSessionCache.Evict(userId)` (same-process eviction so an admin's deactivate takes
   effect on the next request, not in ≤30 s). Sites: every row in the table above except the
   impersonation one. `UserController.Update` rotates only when `RoleId` changed or `IsActive`
   flipped to false — a name edit must not log the user out. `ExecuteUpdateAsync` sites add
   `.SetProperty(u => u.SecurityStamp, SecurityStamps.NewStamp())`.

## Failure modes considered

- **Two sources of truth.** The token's `roleId` claim vs `Users.RoleID`. Resolution: the DB
  wins; the claim is compared and a mismatch rejects.
- **Guard on a convenience method.** The check is in `OnTokenValidated`, i.e. on the scheme,
  so no controller can reach a request without it. Rotation, however, is per-site — the
  `RoleId`/`IsActive`/`PasswordHash` writers were enumerated by grep, not by memory, and the
  list is in the table above. A future writer that forgets to rotate is the residual risk; the
  `roleId` compare in the validator catches the role case regardless.
- **One-way trap.** Rotation on reactivation is not required and is not done; a reactivated
  user simply logs in again.
- **Scale.** One indexed primary-key read per user per 30 s. Seven users in production today.
- **Poison / silence.** A DB error yields 401 with a logged warning, not a silent pass.
- **Fixture shape.** `Release01BHttpApplication.Token()` mints tokens without `sst`; the
  compat rule keeps every existing HTTP test green and new tests mint tokens WITH the claim.
  The seeded users in that fixture (`Users.AddRange` at `:387`) get a stamp from the CLR
  default exactly as production rows get one from the migration.

## Tests

- `TenantSessionRevocationHttpTests` (PostgreSQL, real `Program.cs`): stale stamp → 401;
  current stamp → 200; token minted before deactivation → 401 after the cache entry is
  evicted (and documented ≤30 s staleness without eviction); legacy token without `sst` → 200
  by default; impersonation-shaped token is not subjected to the check.
- Rotation on each path: `UserController.Update` (deactivate, role change, and the control:
  a name-only edit does NOT rotate), `UserRepository.ChangePasswordAsync`,
  `TenantUsersController.DeactivateUser` / `ChangeRole`, `PasswordResetService.CompleteAsync`,
  `TenantAdminInvitationService` activation, `TenantPersonalDataEraser`.
- Migration/grants (PostgreSQL): column exists NOT NULL, every pre-existing row got a distinct
  value, `nexora_identity_app` can UPDATE it, and the two existing guard tests stay green.
- Each regression test is verified by reverting the fix (recorded in the PR).

## Rollout / rollback

1. Deploy. `Auth:RequireSecurityStamp` absent ⇒ tokens without the claim keep working for ≤60
   min; tokens issued after deploy are stamp-checked immediately.
2. After ≥1 h, set `Auth__RequireSecurityStamp=true` on Render (dashboard; render.yaml carries
   the key with the default) so an unstamped token can never be accepted again.
3. Rollback: the column is inert for the previous build (it never reads it); `Down()` revokes
   the grant and drops the column. No data to preserve.
