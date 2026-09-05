# Security posture re-audit — 2026-09-04 (delta since 2026-09-02)

**Scope.** `a1f4d8f5..4b76d9c` (PRs #140–#150, deployed; `/build-identity` reports
`4b76d9cfa31c…`). This is the DELTA plus an adversarial pass on everything those PRs added —
auth (SecurityStamp revocation), users (tenant-side invitations), email (per-tenant outbound
sender) and the four new endpoints — not a repeat of the 09-02 report. Method: read the code
paths named below with file:line, regenerate the authorization matrix at both commits and
diff it, count isolation constructs at both commits, run the dependency audits, grep the
eleven PRs' diffs for secrets in logs, and read production (read-only) for the fixture shapes.

**Verdict (one sentence).** An external client's staff can be placed on the shared instance
for a proof of concept once this PR's three fixes are deployed and `Auth__RequireSecurityStamp`
is set to `true` on Render, under the written conditions in §10 — no P0 was found, no
cross-tenant read or write was found, and the one P1 is fixed here with a revert-proofed test.

---

## 1. Findings, ranked

| # | Sev | Finding | Status |
|---|-----|---------|--------|
| F1 | **P1** | `POST /api/User/{id}/resend-invitation` reactivates ANY inactive account, not only never-activated invitees. | **Fixed** (this PR) |
| F2 | P2 | `UserRepository.DeleteAsync` did not evict the session cache: a deleted account's tokens stayed valid for up to 30 s. | **Fixed** (this PR) |
| F3 | P2 | `send-test` and `resend-invitation` send mail per call with no rate limit; the `smtp` policy exists and was only on password-reset. | **Fixed** (this PR) |
| F4 | P2 | `Auth:RequireSecurityStamp` is `false` in the Blueprint; a stamp-less token is honoured with NO live-account check. Safe to flip now. | Config — see §2 |
| F5 | P2 | A tenant whose only SMTP mailbox is paused/deleted silently falls back to the platform sender. Visible in the mailbox status screen and the send log, not blocking. | Design note, §4 |
| F6 | P2 | The send-test recipient allow-list includes "the mailbox's own address", which the tenant sets — so a tenant can aim one test at any address through its own SMTP account. Bounded by F3's limiter. | Accepted with F3 |
| F7 | P2 | Subject lines are now logged at Information on every tenant send (`RuntimeConfiguredEmailSender.cs:58`, `TenantSmtpEmailSender.cs:59`). Not a secret; a data-minimisation point for a client tenant. | Report only |
| F8 | P2 | `ForwardedHeaders` KnownProxies/KnownNetworks still empty in production (pre-existing, documented in `render.yaml:333`). Every IP-keyed lockout trusts `X-Forwarded-For` from anyone, mitigated by `ForwardLimit = 1` and Render appending the client address. | Pre-existing, config |
| F9 | P2 | `SSH.NET 2025.1.0` High advisory (GHSA-q939-rpr3-3284), TRANSITIVE in the **test** project via `Testcontainers.PostgreSql 4.13.0`. Not in the shipped assembly. | Report only |

No P0. No new IDOR, no new injection, no cross-tenant path reachable with a tenant token.

### F1 — resend-invitation is a reactivation path (P1, fixed)

*Exploit.* A user holding `Users:Create` (but not `Users:Edit` or `Roles & Permissions:Edit`)
calls `POST /api/User/{id}/resend-invitation` against a colleague an administrator has
deactivated; the only guard was `existing.IsActive == true → 409`
(`UserController.cs:676`), so `ReissueAsync` mints a link and `RedeemAsync` sets
`IsActive = true` plus a new password (`TenantAdminInvitationService.cs:344-353`). Offboarding
is undone by a permission that does not include Edit, the seat-limit check on reactivation
(`UserController.cs:480-482`) is bypassed, and with `Users:Edit` added the caller can first
re-point the target's email and then own the account (at the target's rank, which
`CanManageRoleAsync` caps at the caller's own — no escalation, but impersonation of a peer).

*Fix.* `UserController.ResendInvitation` now reads the account's invitation history inside the
same platform-plane block and refuses (409) when the account was never invited or has already
redeemed a link. Tests: `TenantUserInvitationTests.Resending_cannot_reactivate_an_account_that_was_created_with_a_password`
and `…_an_invitee_who_already_redeemed_a_link` — both fail against the old code (verified by
stash/revert), while the pre-existing `Resending_supersedes_the_earlier_link…` passes either
way as the control.

### F2 — delete does not evict (P2, fixed)

`UserRepository.DeleteAsync` removed the row but not the cached verdict
(`TenantSessionValidator.cs:151-165`), so a deleted account's token was honoured for up to
`CacheTtl` (30 s). Now evicts like the deactivate and role-change paths. Test:
`SecurityStampRotationTests.Deleting_through_the_repository_evicts_the_cached_session` (fails
against old code).

### F3 — mail-sending actions unbounded (P2, fixed)

`[EnableRateLimiting(SmtpPolicy)]` (10 per 60 s, partitioned per tenant for authenticated
traffic — `RateLimitingExtensions.cs:120-127`) added to `MailboxController.SendTest` and
`UserController.ResendInvitation`. Pinned by `OutboundMailRateLimitPinTests` (fails when either
attribute is removed).

---

## 2. Token revocation (`Security/TenantSessionValidator.cs`, `Users.SecurityStamp`)

**Every authority-changing path rotates the stamp and evicts** (all verified at file:line):

| Path | Rotation | Evict |
|---|---|---|
| Tenant deactivate / role change (`UserController.Update`) | `UserRepository.cs:270-275` (`authorityChanged` → `RevokeIssuedTokens`) | yes |
| Password change (self) | `UserRepository.cs:337` | yes |
| Password reset (anonymous flow) | `PasswordResetService.cs:461-464` | yes |
| Activation (invite redeemed) | `TenantAdminInvitationService.cs:349` | n/a (no live token) |
| Erasure | `TenantPersonalDataEraser.cs:146` (+ `IsActive=false`) | via tenant-access evict |
| Platform-side deactivate | `TenantUsersController.cs:400` | yes (`:82-86`) |
| Platform-side role change | `TenantUsersController.cs:513` | yes |
| Tenant delete | **was missing** → F2, now `UserRepository.DeleteAsync` | yes |
| Profile edit (name/avatar/timezone) | deliberately none (`UserRepository.cs:268-273`, test `A_profile_edit_does_not_rotate_the_stamp`) | — |

**The 30 s cache cannot be abused to extend a revoked session.** The cache stores the DB
snapshot `{IsActive, RoleId, SecurityStamp}` keyed by `userId` only
(`TenantSessionValidator.cs:158-165`); a token never writes to it, so presenting a token
cannot refresh or prolong an entry. Every in-process revocation evicts, so the victim's next
request re-reads. Cross-instance staleness is bounded by the TTL and is moot today: the
service runs one instance. The belt-and-braces `roleId` compare (`:141-144`) catches a writer
that forgets to rotate on role change.

**Legacy tokens and `Auth:RequireSecurityStamp=false`.** With the flag false a token that
carries no `sst` claim returns `true` at `TenantSessionValidator.cs:120-126` — i.e. it is
accepted **without** the `IsActive`/role check. What an attacker holding a stolen pre-#142
token could do: everything that user could, until the token's `exp` (60 min + 30 s skew).
#142 merged 2026-09-02 22:42 −04:00, so every such token expired by ~2026-09-03 00:15 −04:00.
Today the only way to obtain a stamp-less token is to forge one, which requires `Jwt:Key` —
already total compromise. **A login-time stamp is issued on every token**
(`AuthRepository.cs:263`), production has 10 users and 0 rows with a null/empty stamp, and the
`true` branch is covered by `TenantSessionRevocationHttpTests`. **Flipping the flag is safe
now; do it before external staff log in.** The Blueprint carries `"false"`
(`render.yaml:207`); the live dashboard value could not be read in this session (API read
refused by the sandbox), so treat it as unverified until someone looks.

---

## 3. Tenant-side invitations (`UserController`, `TenantAdminInvitationService`)

- **Who may invite.** `Create` requires `Users:Create` AND `Roles & Permissions:Edit`
  (`UserController.cs:225-232`); `resend-invitation` requires `Users:Create` and the RoleGate
  check on the target's role (`:673-674`).
- **Role above the inviter — no.** `RoleGate.CanManageRoleAsync` (`Authorization/RoleGate.cs:64-115`):
  owner tier unrestricted; otherwise target rank ≤ caller rank AND the target role's
  module grants ⊆ the caller's. Applied on create (`:268`), on update for both old and new
  role (`:474-477`, self-role-change refused), and on resend.
- **Another tenant's business unit — no.** `request.Buid` is overwritten by the claim
  (`:265-267`); `tenantId` is resolved from `Tenants.PrimaryBusinessUnitId == claimBU`
  (`:297-300`); `Team/UserGroup/Manager/Role` are validated against `Buid` in
  `UserRepository.AddAsync` (`:159-190`).
- **Token.** 32 bytes from `RandomNumberGenerator` (256 bits), base64url
  (`TenantAdminInvitationService.cs:616-626`); only SHA-256 stored, unique index,
  fixed-time compare (`:554-569`); default lifetime 72 h (`TenantOnboardingOptions.cs:21`);
  single-use by a conditional `UPDATE … WHERE RedeemedAtUtc IS NULL AND RevokedAtUtc IS NULL
  AND ExpiresAtUtc > now` (`:323-338`); redemption revokes every other live link for the
  account (`:365-373`); reissue revokes first, then mints, in one transaction (`:459-475`).
- **What the anonymous redeem endpoint leaks** (`TenantActivationController.cs:64-86`): to a
  holder of a VALID token only — masked email (`MaskActivationEmail` default `true`),
  company name, first name, expiry, minimum password length. Invalid/expired/used/revoked
  tokens return a status word and nothing about any account; unrecognised tokens feed a per-IP
  lockout (`:146-173`). Neither inspect nor redeem is reachable by email address.
- **`resend-invitation` under `PlatformPlaneExecution` (BYPASSRLS).** Before the block the
  controller runs `_repository.GetByIdAsync(id, claimBUId)` under the tenant role and RLS
  (`:673`, `UserRepository.cs:138-149`) — a foreign id is a 404 before any privileged
  statement runs. Inside, `ReissueAsync` independently refuses unless
  `user.Buid == tenant.PrimaryBusinessUnitId` (`TenantAdminInvitationService.cs:441-446`).
  Two independent checks; no cross-tenant user row is readable with a tenant token. The
  `Create` block is additionally fail-closed on dirty tenant entities (`:399-404`).
- Production: 12 invitation rows, all tenant 3 / users 12–13, none live.

---

## 4. Per-tenant outbound sender

- **Header injection via From display name (business-unit NAME) or Reply-To — not
  exploitable.** `MimeMessageComposer.Address` builds `MailboxAddress(name, address)`
  separately (`MimeMessageComposer.cs:48-49`); MimeKit RFC-2047-encodes CR/LF in phrases and
  strips them from `Subject`. Proven empirically and pinned by
  `OutboundMailHeaderInjectionTests` (a CRLF `Bcc:` in From/To/Reply-To/Subject yields 0 Bcc
  and no injected header after re-parse).
- **SSRF on the tenant's SMTP host/port.** `MailEndpointPolicy.IsAllowedEndpoint` at
  create/update (`MailboxController.cs:~305`) and at `send-test` (`:206`), then
  resolve-then-connect with an all-addresses-public rule at dial time
  (`MailEndpointPolicy.cs:118-137`, used by `MailboxConnectionProbe.cs:184-200` and by the
  send transport at `Security/OutboundSmtpTransport.cs:35-42`). Loopback allowance is structurally Development-only (`:69-73`).
  Production rows: two active SMTP mailboxes (BU 1 → `mail.spacemail.com:465`, BU 7 →
  `smtpout.secureserver.net:465`), both public.
- **Paused/deleted mailbox → platform sender, silently (F5).** `TenantOutboundSenderSource`
  returns `null` when no ACTIVE SMTP row exists and `OutboundSenderResolver.ResolveAsync`
  falls back to the platform transport (`OutboundSenderResolver.cs:156`). The mailbox
  status endpoint says so in words and the send log names the origin; `send-readiness` blocks
  only when the platform sender itself cannot transmit (`QuoteService.cs:1721-1726`), so with a
  transmitting platform provider nothing refuses.
  For a client tenant that means their quotes could leave from the platform address. Design
  recommendation: a tenant setting "own mailbox required" that turns the fallback into a
  send-readiness blocker.
- **Send-test recipient restriction.** Only the caller's `email` claim or the mailbox's own
  address (`MailboxController.cs:210-217`); DTO is `[EmailAddress][StringLength(320)]`.
  The mailbox address is tenant-set (F6) — one message to anywhere through the tenant's OWN
  SMTP account, now 10/min per tenant.
- **One tenant's SMTP credentials for another's message — no.** The resolver holds no cache
  at all: each send opens a fresh DI scope and resolves by the message's
  `OwningBusinessUnitId` (`OutboundSenderResolver.cs:148-156`); the source throws on a scope
  mismatch (`TenantOutboundSenderSource.cs:27-29`); the sender throws if the message's owner
  differs from the mailbox's BU (`TenantSmtpEmailSender.cs:43-47`). The platform guard mode
  (`Live` in production) applies to tenant sends too (`OutboundSenderResolver.cs:164-169`).

---

## 5. New endpoints since 09-02

Diff of `[Http*]` attributes `a1f4d8f5..4b76d9c`: exactly four added, none removed.

| Endpoint | Permission | Tenant predicate | Input bounds | Rate limit |
|---|---|---|---|---|
| `POST /api/Mailbox/{id}/send-test` | `Mailboxes:Edit` | `x.Id == id && x.BusinessUnitId == tenant` | `[EmailAddress][StringLength(320)]` | **added** (smtp) |
| `POST /api/User/{id}/resend-invitation` | `Users:Create` + RoleGate | `GetByIdAsync(id, claimBU)` + `Buid == PrimaryBusinessUnitId` | id only | **added** (smtp) |
| `GET /api/Quote/{id}/send-readiness` | `Quotations:View` | `CanAccessQuoteAsync` → 404 | id only | n/a (read) |
| `POST /api/commercial-intelligence/follow-ups` | `Quotations:Edit` | `q.BusinessUnitId == tenant && q.Id == QuoteId` | reason ≤ 80, `Idempotency-Key` required | n/a (no mail/file) |

Pre-existing endpoints that gained a UI in this range (re-checked, unchanged gates):
`GET /api/email-triage?state=stopped` (`Leads:View` + class entitlement, tenant claim);
`POST /api/commercial-lifecycle/leads/{id}/reopen` (`Leads:Edit` + `[RequireManagerRole]`);
`GET /api/intake-records/{id}` and `/by-lead/{id}` (`[Authorize]` + EmailIntake entitlement +
`Leads:View`, tenant claim → service predicate); `PUT /api/commercial-routing/default-owner`
(`[RequireManagerRole]` + `Leads:Edit`; owner must be an active user of the same BU —
`CommercialRoutingApplicationService.cs:773-776`). The intake-record dialog and the other
deployed-lane changes are frontend-only; no `dangerouslySetInnerHTML`/`innerHTML` was added.

---

## 6. Authorization matrix (regenerated at both commits)

Script: enumerate every `.cs` whose class derives from `ControllerBase`/`Controller`,
collect class-level and action-level attributes around each `[Http*]`, classify each action
as `anon` (`[AllowAnonymous]`), `perm` (a `RequireModulePermission` / `RequirePlatformRole` /
`RequireManagerRole` / `Authorize(Policy=…)` on the action or class) or `bare` (nothing
beyond a bare `[Authorize]` or the fallback policy).

| | 09-02 (`a1f4d8f5`) | 09-04 (`4b76d9c`) | Δ |
|---|---|---|---|
| Controllers | 123 | 123 | 0 |
| Actions (`[Http*]`) | 821 | 825 | +4 (§5) |
| `perm` | 752 | 757 | +5 |
| `bare` | 61 (13 mutating) | 60 (12 mutating) | −1: `QuoteBackfillController POST` bare→perm (the 09-02 P1) |
| `anon` | 8 | 8 | same eight: tenant login, platform login + MFA challenge, password-reset ×3, activation ×2 |

The 09-02 report's "47 bare" counted explicit per-action `[Authorize]` only; this method also
counts actions inheriting a bare class-level `[Authorize]`, hence 61/60. Both agree on the
delta. **No new bare `[Authorize]` mutating action** — the 12 remaining were all present on
09-02 (Contact CRUD, own ChangePassword, list-view preferences, custom-field values, agent
chat, three PlatformGovernance actions).

---

## 7. Isolation regressions

| Construct | 09-02 | 09-04 | Notes |
|---|---|---|---|
| `IgnoreQueryFilters()` (prod code) | 240 | 242 | 10 added, each with an explicit BU/id predicate: `UserController` ×2 (`Tenants` by `PrimaryBusinessUnitId`, projects `Id` only), `TenantSessionValidator` (`Users` by id+`Buid`, under pushed tenant scope), `LegacyEvidenceMigrationJob` ×3 (worker), `TenantBaselineSeeder` ×2 and `TenantReferenceListReconciler` ×1 (provisioning, platform plane), `FolderService` ×1 (`Leads` by id). 8 removed elsewhere. |
| Raw SQL | — | +1 | `TenantBaselineSeeder.cs:538-539` — `ExecuteSqlInterpolatedAsync("SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))")`, a parameterised per-tenant advisory lock during provisioning; no data path. No `FromSqlRaw`/`ExecuteSqlRaw` added. |
| `PlatformPlaneExecution.Enter` | 1 (ExtractionWorker) | 4 | 3 new, all in REQUEST paths: `UserController.Create` (`:406`, wraps `IssueAsync` only, fail-closed on `ChangeTracker.HasChanges()`), `UserController.ResendInvitation` (`:690`, after the tenant-scoped id check), `TenantAdminInvitationService.SendInvitationEmailAsync` (`:164`, one `ExecuteUpdate` on the invitation row by id). Each wraps only platform-table statements. |
| `Attachments` RLS policy | Lead-only | Lead-only | Unchanged (`20260723120000_CompleteTenantRlsCoverage.cs:113-121`). No new `ParentType` written in this range (diff adds are tests and `"Lead"`); production holds 313 rows, all `Lead`. The pre-existing non-Lead parent types (`DeliveryProofEvidence`, `CustomerPoDocument`, `CustomerPurchaseOrder`, `MaterialLotCertificate`) remain invisible to `nexora_tenant_app` under this policy — a functional gap on those features, not a leak. |

---

## 8. Dependency audit

- Frontend `npm audit --omit=dev`: **0** (173 prod deps).
- Backend `dotnet list package --vulnerable --include-transitive`: shipped project **0**;
  test project: `SSH.NET 2025.1.0` High via `Testcontainers.PostgreSql 4.13.0` (F9).

## 9. Secrets hygiene of the eleven PRs

14 `Log(Information|Warning|Error)` lines added in the range; none interpolates a token,
password, SMTP credential, authorization header or message body. Invitation logging carries
ids, tenant, expiry and client IP only (`TenantAdminInvitationService.cs:99-100, 381-383`);
the activation controller logs the client IP and explicitly not the token (`:195-199`).
`TenantOutboundSender.ToString()` redacts the password. Only the subject line is new in logs (F7).

---

## 10. Written conditions for placing an external client's staff on the shared instance

1. This PR's fixes (F1–F3) deployed; `Auth__RequireSecurityStamp=true` set on Render and
   confirmed in the dashboard (F4) **before** the first external login.
2. Client users are created by **invitation** (default), never by an operator-typed password;
   client staff hold roles at or below Manager rank; only the client's own tenant owner holds
   `Users:Create` and `Roles & Permissions:Edit`.
3. The client tenant configures and verifies its **own SMTP mailbox** before any quote is
   sent, and the pilot agreement states in writing that a paused/deleted mailbox falls back
   to the platform sender (F5) until the "own mailbox required" setting exists.
4. Platform operators do not create, edit or password-set client users except through the
   audited platform console paths (`TenantUsersController`), and never share a business unit
   between tenants.
5. `ForwardedHeaders:KnownNetworks` populated for Render's edge, or the documented reliance on
   `ForwardLimit = 1` accepted in writing (F8).
6. The client is told that email subject lines appear in server logs (F7) and that log
   retention is the platform's.

Under those conditions the isolation model (RLS + tenant claim predicates + BYPASSRLS
confined to four reviewed blocks), the revocation model (stamp on every token, rotated on every
authority change, cached 30 s with in-process eviction) and the invitation model (256-bit
single-use tokens, rank-capped roles, tenant-bound business units) are adequate for a proof of
concept on the shared instance.
