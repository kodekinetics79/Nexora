# Tenant-side user invitations

Stream 3, item B. Design first; implementation follows this note.

## Problem

A tenant administrator adding a colleague must TYPE that colleague's password
(`Frontend/src/pages/Security/Users/UsersPage.tsx:216-257`, `DTOs/UserDTO/UserCreateRequestDTO.cs:21`
`[Required] Password`). The administrator then holds a working credential for someone
else's account, there is no must-change flag, and the password's real home is a chat
message. The platform console already solved this for the founding administrator and for
console-created users (`Platform/Controllers/TenantUsersController.cs:169-345`,
`activation=invite` default) — the tenant screen never got it.

Live evidence: `platform.TenantAdminInvitations` has 15 rows, all issued from the platform
plane; `Users` rows 13/14 were created by the console with `IsActive=t` and a typed
password, one of which has never logged in.

## Current mechanism

| step | file:line |
|---|---|
| Tenant create | `Controllers/UserController.cs:215-338`: `Users:Create` + `Roles & Permissions:Edit`; BCrypt hash of `request.Password`; user + audit in `ExecuteAtomicAsync` |
| Platform invite | `TenantUsersController.cs:214-327`: unusable hash (random 64 hex, discarded), `IsActive=false`, `DeactivatedAtUtc=now`, `IssueAsync` INSIDE the transaction, email AFTER commit |
| Token | `Platform/Onboarding/TenantAdminInvitationService.cs:63-113`: 256-bit CSPRNG, SHA-256 hex at rest, 72 h (`TenantOnboardingOptions.InvitationLifetimeHours`) |
| Redeem | `TenantAdminInvitationService.cs:274-385`: atomic claim, sets hash + `IsActive`, revokes siblings |
| Page | `/activate/:token` → `GET/POST /api/tenant-activation/{token}` (anonymous, identity role) |

### The constraint that shapes the design

The tenant request path executes as `nexora_tenant_app` (`MultiTenancy/TenantRlsCommandInterceptor.cs`).
Verified live with `has_table_privilege`:

| table | nexora_tenant_app |
|---|---|
| `platform."TenantAdminInvitations"` | no SELECT/INSERT/UPDATE |
| `platform."Tenants"` | column-scoped SELECT: `Id, PrimaryBusinessUnitId, Status, PlanId` only — NOT `Name` |
| `public."Users"`, `"BusinessUnits"` | full |

A GRANT is a migration, and this stream has no migration budget. The codebase already has
the seam for exactly this: `MultiTenancy/PlatformPlaneExecution.cs` — an ambient block
under which the RLS interceptor issues `SET LOCAL ROLE nexora_pipeline_app` for each
command (`TenantRlsCommandInterceptor.cs:185-198`), used by usage metering inside the
extraction persist transaction. The rule attached to it: wrap the SMALLEST region, never a
region that touches tenant rows, and fail closed if the change tracker holds unsaved
tenant-plane changes.

## Proposed mechanism

`POST /api/User` gains `Activation` = `invite` (default when no password is supplied) |
`password`.

```
UserController.Create
  ├─ activation = request.Activation ?? (Password present ? "password" : "invite")
  ├─ invite: tenantId = Tenants.Where(PrimaryBusinessUnitId == claimBU).Select(Id)   (granted columns only)
  │          → 409 if the business unit is not a tenant's primary unit
  │          tenantName = BusinessUnit.BusinessUnitName                                (tenant-plane, granted)
  │          hash = BCrypt(random 64 hex)   IsActive=false   DeactivatedAtUtc=now
  ├─ password: unchanged (the pre-existing path; no new floor — see decisions)
  ├─ ExecuteAtomicAsync
  │     repository.AddAsync(user)  → SaveChanges (tenant role, RLS)
  │     audit UserCreated           (tenant role)
  │     if invite:
  │        assert !ChangeTracker.HasChanges()          (fail closed — see ExtractionWorker:2340)
  │        using PlatformPlaneExecution.Enter():        (pipeline role for these statements only)
  │            issued = invitations.IssueAsync(context, {TenantId, UserId, Email, RecipientName,
  │                                                     TenantName, IssuedBy, SenderBusinessUnitId = claimBU})
  └─ after commit: invitations.SendInvitationEmailAsync(issued)
        → EmailMessage.OwningBusinessUnitId = SenderBusinessUnitId → item A resolves the tenant sender
        → the SendCount/LastSentAtUtc bump inside the service runs under PlatformPlaneExecution.Enter()
```

* Reuses `TenantAdminInvitationService` and the existing `/activate/:token` page unchanged;
  `RedeemAsync` runs as the identity role, which already holds SELECT/UPDATE on the table.
* `POST /api/User/{id}/resend-invitation` (`Users:Create`) → `ReissueAsync` under the
  platform-plane block, then send. Without it a failed first send strands the colleague.
* `DELETE /api/User/{id}` is a hard delete (`UserRepository.DeleteAsync` removes the row);
  `RedeemAsync` already answers `Invalid` for an invitation whose user no longer exists, so
  no revocation step is needed on that path.
* Seat entitlement is checked at invite time (the account is dormant but redemption has no
  gate), mirroring the console.
* Response: `UserResponseDTO` + `activationMethod`, `invitationEmailDispatched`,
  `invitationExpiresAtUtc`.
* Frontend `UsersPage`: "Send invitation" is the default; "Set a password instead" is a
  secondary text button that reveals the password field.

## What could go wrong

| risk | mitigation |
|---|---|
| Tenant-plane rows written under the bypass role | the block wraps only `IssueAsync`; `HasChanges()` assertion throws before entering |
| Business unit without a tenant row (legacy BU 1/2/5/6) | 409 with "set a password instead"; nothing half-written |
| SQLite tests cannot see role/grant failures | the PostgreSQL lane runs the full journey (`Category=PostgreSQL`) |
| Invite email fails | logged, `emailDispatched:false` in the response, resend endpoint; the account stays dormant and harmless |
| Two working links | `RedeemAsync` revokes siblings; `ReissueAsync` revokes before issuing |
| Old clients posting a password with no `Activation` | treated as `password` — wire-compatible |

## Tests that prove it

`TenantUserInvitationTests` (SQLite `TestDb`, controller-level, mirrors
`PlatformTenantUserManagementTests` harness):

1. invitation created — one `TenantAdminInvitation` row for the new user, `TokenHash` is
   64 hex, the captured email carries the activation URL and `OwningBusinessUnitId`;
2. password hash unusable until redeemed — `IsActive=false`, `DeactivatedAtUtc` set,
   `BCrypt.Verify` fails for the empty string and for the token itself;
3. redeem activates — `RedeemAsync(token, strong password)` → `IsActive=true`, the new
   password verifies, the invitation is `RedeemedAtUtc`;
4. expired token refused — `FakeClock` advanced 73 h → `Expired`, hash and `IsActive`
   untouched.

Each is revert-proofed: with `Activation` ignored (old code path) test 1 fails at the
missing invitation row.

## Rollout / rollback

No schema, no grant. Feature is additive; `password` path unchanged apart from the floor.
Rollback = revert.

## Product-owner decisions to confirm

1. The password path is left as it was (no minimum-length floor on `POST /api/User`); the
   activation flow's 12-character floor applies only to the link. Aligning the two is a
   separate, deliberate change because existing harnesses create users with 8-character
   passwords.
2. Tenant-side invites use the tenant's own verified sender when one exists (item A), so
   the activation email arrives from the company address, not from Nexora.
3. Business units that are not a tenant's primary unit cannot invite (409) — they keep
   the password path.
4. `POST /api/User/{id}/resend-invitation` runs `ReissueAsync` under the platform-plane
   block, which also reads the target user row under the bypass role (filtered by the
   tenant's own primary unit, which the caller has already proved it manages).
