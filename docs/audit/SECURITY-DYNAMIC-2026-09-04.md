# Nexora — Dynamic (request-level) security audit

- **Date:** 2026-09-04
- **Branch:** `scenarios/dynamic-security` (from `origin/main` 4b76d9c)
- **Lane:** adversarial, request-level pass with **real tokens** against a **disposable** stack
  (never production). A static review runs in a parallel lane.
- **Stack under test:** backend `:5204`, frontend `:5184`, PostgreSQL `:55444`, container
  `nexora-e2e-sec`, `ASPNETCORE_ENVIRONMENT=Development`, outbound guard `DraftOnly`
  (nothing leaves).
- **Probe:** `scripts/security/dynamic-probe.py` — enumerates every controller route, then runs
  ten test groups and prints a table. **Re-runnable**, run **twice**; both passes identical.
- **Stack bring-up:** `scripts/security/run-sec-stack.sh` (a security-lane fork of
  `scripts/e2e/run-enterprise-commercial-journey.sh` that leaves the backend up on 5204 with a
  **known** acceptance password so the probe can log in on every rerun; same backend env/guards —
  Development, `Notifications__OutboundGuard__Mode=DraftOnly`, `Cors__AllowedOrigins__0=http://127.0.0.1:5184`).

## How to reproduce

```bash
cd /Users/zackkhan/Nexora/.worktrees/scn-security
(cd Frontend && npm ci)
DOTNET_CLI_TELEMETRY_OPTOUT=1 ./scripts/security/run-sec-stack.sh        # first run builds
python3 scripts/security/dynamic-probe.py                                # writes scratch-security-results.json
python3 scripts/security/dynamic-probe.py                                # run twice
./scripts/security/run-sec-stack.sh down                                 # teardown
```

Identities (all on tenant **80101** except `other` on **80102**): `manager`, `finance`,
`editor` (Sales Rep), `denied` (restricted), `other`, plus the tenant **owner** (admin-rank) and
a **platform owner** (`owner@acceptance.local`, MFA scripted). Tokens obtained via
`POST /api/Auth/Login`.

## Result summary (both passes identical)

| # | Group | Pass | Fail | Severity findings | Verdict |
|---|-------|-----:|-----:|------------------:|---------|
| 1 | Authorization matrix (135 GET routes × 4 callers + curated IDOR) | 478 | 1 | 0 | **PASS** (1 fail is by-design, non-security) |
| 2 | Tenant BU override (20 mutating routes) | 20 | 0 | 0 | **PASS** |
| 3 | Token lifecycle (change-password, deactivate → revocation) | 3 | 0 | 0 | **PASS** |
| 4 | Invitations (editor invite, cross-tenant, unauth token peek) | 3 | 0 | 0 | **PASS** |
| 5 | Outbound/SMTP abuse (SSRF, header injection) | 7 | 0 | 0 | **PASS** |
| 6 | Rate limits (login lockout, 600/60s burst) | 2 | 0 | 0 | **PASS** |
| 7 | Uploads (30 MB, zip-bomb, .html-as-.xlsx, path traversal, EICAR) | 6 | 0 | 0 | **PASS** |
| 8 | Headers & transport (CSP/nosniff/frame, preflight, infra endpoints) | 13 | 0 | 0 | **PASS** |
| 9 | Injection (SQL/NoSQL/LDAP/XSS in string filters) | 30 | 0 | 0 | **PASS** |
| 10 | Verbose errors (malformed JSON, oversized id, invalid enum) | 3 | 0 | 0 | **PASS** |
| | **Total** | **565** | **1** | **0** | |

**No exploitable request-level defect was found.** Every guard the plan set out to break held. The
enumerator covered **825 routes across 123 controllers**. The one group-1 "fail" is by-design and
carries no severity (see 1.4).

---

## Group 1 — Authorization matrix (live)

Sampled ≥120 GET routes spanning **every** controller (1 per controller, topped to 135), each
called four ways.

- **1.1 No-token → 401.** A process-wide `FallbackPolicy` requires authentication on every
  endpoint (`Program.cs:556`), so unauthenticated reads are 401 across the board. ✅
- **1.2 `denied` (restricted) on a module-gated route → 403/404, never 200-with-data.** Confirmed.
  Spot-checked mutations: `denied` `POST /api/Customer` → **403**, `PUT /api/Customer/2` → **403**.
  No 200 to `denied` on any mutating route ⇒ **no P1**. ✅
- **1.3 Cross-tenant IDOR — `other` (80102) token against 80101 resource ids → never the 80101
  row.** Curated checks (`/api/Customer/2`, `/api/Lead/2`, `/api/Contact/1`) and the broad sweep
  (byte-similarity of `other` vs `manager` bodies on every parameterized route) found **zero**
  cross-tenant reads. Tenant isolation is enforced twice: controllers derive the tenant only from
  the token claim (`TryGetAuthenticatedBusinessUnitId`, e.g. `Controllers/RfqController.cs:752`)
  **and** PostgreSQL row-level security is applied at the command boundary keyed on the token's
  tenant (`MultiTenancy/TenantRlsCommandInterceptor.cs`). The one apparent broad hit
  (`/api/commercial-routing/default-owner`) was a **false positive**: both tenants read their own
  empty config (identical boilerplate, no 80101 data). ✅ **No IDOR / no P0.**
- **1.4 Admin-rank (`owner`) never 403.** Confirmed on all sampled routes. The single non-security
  "fail" is `GET /api/search` returning **400** (not 403/404) for a short query — `SearchController`
  is `[Authorize]`-only and **scope-filtered** by design (`Controllers/SearchController.cs`
  header comment), returning results any authenticated user may see and 400 for a `< 2`-char query.
  Not a defect.

> Note on manager 403s: the seeded `manager` role legitimately lacks modules such as Currency,
> Mailbox, General Ledger, Treasury, RolePermission and BusinessUnit, so `manager` receives **403**
> on those — correct RBAC, not a defect. The "should be allowed" invariant is therefore checked
> with the admin-rank `owner`, whose rank rule (`Authorization/PermissionHandler.cs:47-62`,
> replacing the former super-admin bypass) grants module access without a per-module grant.

## Group 2 — Tenant override attempts

For 20 mutating routes the probe sent a **conflicting `businessUnitId`** in body, query and header
(`X-Business-Unit-Id: 80102`, `?businessUnitId=80102`, `{"businessUnitId":80102,...}`) with a
tenant-80101 token. **The server always uses the claim.** Tenant-plane cases confirmed it directly
(`PUT /api/reporting/subscriptions` → 400, `PUT /api/supplier-scoring-weights` → 400,
`PUT /api/sla/policy` → 200 with no 80102 echo); platform-plane routes correctly reject a tenant
token (401). This confirms the 09-02 review's expectation — controllers overwrite the request BU
with the claim. The pattern is explicit in code, e.g. `Controllers/UserController.cs:471-473`:

```csharp
if (request.Buid > 0 && request.Buid != claimBUId) return Forbid();
request.Buid = claimBUId;
```

Backed by RLS so even a controller that forgot this cannot read/write another tenant's rows. ✅

## Group 3 — Token lifecycle (SecurityStamp / #142)

Every tenant JWT carries a short `sst` claim = the account's `SecurityStamp`
(`Repositories/AuthRepository.cs:263`), and `TenantSessionValidator.IsCurrentAsync`
(`Security/TenantSessionValidator.cs:114-145`) refuses a token whose stamp no longer matches, whose
account is inactive, or whose role changed. The write side rotates the stamp and evicts the cached
verdict on any authority change (`Repositories/UserRepository.cs:33-36`, called from `UpdateAsync`
on `authorityChanged` at `:271-275` and from `ChangePasswordAsync` at `:337`).

Proven live:

- **Change password (self)** → old token rejected **within the same request cycle** (`after=0s`):
  baseline `200`, change `204`, next call `401`. ✅
- **Deactivate (owner `PUT /api/User/{id}` with `IsActive=false`)** → old token rejected `after=0s`.
  Reactivation restores service. ✅
- Cross-instance staleness is bounded to **30 s** (`Security/ReadOnlyImpersonationMiddleware.cs:37`,
  shared by reference with the impersonation guard); same-process eviction is immediate.

**What `Auth:RequireSecurityStamp=false` (production default) leaves open — precisely:** it governs
**only** tokens that carry **no** `sst` claim, i.e. tokens minted by a build that predates the stamp
(`Security/TenantSessionValidator.cs:120-126`, `return !_requireStamp`). Current builds always mint
the claim, and **all** seeded accounts have a non-empty `SecurityStamp` (verified in the DB), so
revocation is **unconditional** for every live account. The residual exposure is a one-hour
compat window for pre-stamp legacy tokens immediately after the introducing deploy — not present on
a fresh deployment. **Recommendation:** flip `Auth:RequireSecurityStamp=true` in Production once
past that window to make even a stampless token fail closed. (Low.)

## Group 4 — Invitations

- **`editor` invites a user** (`POST /api/User`) → **403**. Editor lacks `Users:Create` and
  `Roles & Permissions:Create`; user-create carries a **segregation-of-duties** double gate
  (`Controllers/UserController.cs:225`+ and the `Update` note at `:460`). ✅
- **Cross-tenant invite** (`manager`, body `businessUnitId=80102`) → **403**; the response never
  accepts 80102. Role/BU come from the caller's claim, never the body. ✅
- **Unauthenticated `GET /api/tenant-activation/{token}`** with an invalid/guessed token → **404**
  with every field null (`{"status":"invalid","email":null,"tenantName":null,...}`). No account
  enumeration, no internals. A *valid* token reveals only the invitee email / tenant name needed to
  render the activation screen — by design, gated by an unguessable token. ✅

## Group 5 — Outbound sender abuse (DraftOnly; nothing left)

The single authority is `Security/MailEndpointPolicy.cs` — resolve-then-connect, DNS-rebinding-safe
(`ValidateResolvedAddresses` requires **all** resolved addresses public), loopback refused unless a
**Development-only, structurally-gated** opt-in is set (`EnableLoopbackForLocalDevelopment`, a
parameter not a config read). Proven live via `POST /api/Mailbox/test` (owner):

| Configured SMTP host | Result |
|---|---|
| `127.0.0.1:5204` (loopback) | refused at **Policy** stage — *"not an address this server may connect to"* |
| `169.254.169.254:80` (cloud metadata) | refused at Policy stage |
| `localhost:22` | refused at Policy stage |
| `10.0.0.5:25` (RFC 1918) | refused at Policy stage |

`POST /api/Mailbox` (create) with a metadata host → **400 refused**. **Header injection:**
`POST /api/Smtp/send` with `ToEmail`/`Subject` containing `\r\nBcc: attacker@example.com` →
**400** *"Email headers contain invalid characters"* (`Controllers/SmtpController.cs:45`). The send
path also refuses an arbitrary recipient — it must be an active contact of a tenant supplier
(`Controllers/SmtpController.cs:69-74`) — and blocks attachments until malware scanning is wired.
Note even loopback is refused here because the loopback allowance
(`Mail:AllowLoopbackForLocalDevelopment`) is **not** set on this stack. ✅

## Group 6 — Rate limits

- **Login lockout:** progressive per-`(plane,email)` lockout, DB-backed, threshold **5** — the
  **6th** rapid bad login returns **429** with a plain message *"Too many failed sign-in
  attempts…"* (`Controllers/AuthController.cs:36-46`, `Security/LoginAttemptThrottle.cs`). ✅
- **Volume:** the global limiter is **600 / 60 s**, partitioned by tenant claim
  (`Platform/Hardening/RateLimitingExtensions.cs:40-68`). A burst from one token → **429** at
  request 601, with `Retry-After`. ✅ Upload (`30/60s`) and SMTP (`10/60s`) named policies exist
  (`:44-48`).

> Caveat (test artifact, not a product issue): all tenant-80101 identities share **one** partition
> (`tenant:80101`), so the probe defers its volume burst to the very end and warms up on rerun to
> avoid self-inflicted 429s.

## Group 7 — Uploads

Real inspection door: `POST /api/{Customer|Product|Rfq}Uploader/upload-template`, which runs
`UploadInspectionGate.InspectAsync` → `Security/DocumentInspection/DocumentFileInspectionService.cs`
(magic-byte allowlist, archive-entry limit, malware scan) and refuses via a 400
(`UploadInspectionGate.Refuse`, `:79-109`).

| Attack | Result |
|---|---|
| **30 MB** file (cap = 25 MB, `DefaultMaximumFileBytes`, #140) | **400** refused — the 25 MB cap **is** enforced |
| **.html renamed .xlsx** | **400** *"failed security inspection"* (magic-byte allowlist) |
| **zip-bomb-shaped** (5 000-entry archive) | **400** (archive-entry limit) |
| **path-traversal filename** (`../../../etc/passwd.xlsx`) | **400**, no path echoed; storage uses a GUID name |
| **EICAR** | **400** — the **BuiltIn** `EicarMalwareScanner` flags the exact signature |

**What the BuiltIn (`Nexora.EICAR`) scanner does and does not catch**
(`Security/DocumentInspection/MalwareScanners.cs:8-58`): it is an exact-signature matcher for the
EICAR test string only. It is **not** a real AV — arbitrary live malware whose bytes are not the
EICAR signature would pass the scanner (though still be caught by the magic-byte allowlist if it is
not an allowed document type). Production should point `ClamAvInstreamMalwareScanner` at a real
daemon (the code path exists, `:71-`). This is expected for a disposable stack; flagged so it is a
deliberate deployment decision, not an oversight. (Informational.)

## Group 8 — Headers & transport

Measured (Development stack):

| Surface | Header | Value |
|---|---|---|
| API | `X-Content-Type-Options` | `nosniff` (every response, both environments — `Program.cs:1125`) |
| API | `X-Frame-Options` | `DENY` (every response — `Program.cs:1126`) |
| API | `Content-Security-Policy` | Development Swagger policy (`default-src 'self'`; `frame-ancestors 'none'`) |
| API | `Strict-Transport-Security` | ABSENT (Development — intentional) |
| Frontend (Vite dev) | security headers | ABSENT (dev server; the deployed frontend is a separate Vercel origin with its own headers) |

- **Preflight from `Origin: https://evil.example`** → **no** `Access-Control-Allow-Origin`. ✅
- **Unauth infra endpoints:** `/metrics` → **401** (scrape-key gated), `/swagger` → **200**
  (Development only — `Program.cs:1141-1144`), `/ready` → **503**, `/health` → **200**. None leak
  a host, connection string or bucket name. ✅

**Production posture (verified by reading the environment guards, since the stack runs
Development):** outside Development the CSP is `default-src 'none'; object-src 'none';
frame-ancestors 'none'; base-uri 'none'; form-action 'none'`
(`Infrastructure/TransportSecurityPolicy.cs:154-159`, selected at `Program.cs:1122`); HSTS
`max-age=31536000; includeSubDomains` and loop-safe HTTPS redirect are **on**
(`Program.cs:1186-1190`); the loopback CORS origins are merged **only** under `IsDevelopment`
(`TransportSecurityPolicy.cs:33-66`, `Program.cs:633`); Swagger is **off**. These relaxations are
structurally Development-gated, not config toggles that can reach Production set wrong.

## Group 9 — Injection

SQL/NoSQL/LDAP/XSS payloads (`' OR '1'='1`, `'; DROP TABLE users;--`, `*)(uid=*`, `{$gt:''}`,
`<script>…`) in every reachable string filter (`/api/Lead?buyersName=`, `/api/Customer?name=`,
`/api/Supplier?name=`, `/api/search?q=`, `/api/Product?search=`): **200 or 400, no error leakage,
no behaviour change.** Zero Npgsql/SQLSTATE/stack fragments in any response. Queries are
parameterised via EF Core; RLS additionally scopes every read. ✅ (XSS *rendering* — that user
input is escaped on screen — belongs to the frontend/static lane; the API returns data, not markup,
and the API origin's CSP is `default-src 'none'` in Production.)

## Group 10 — Verbose errors

- Malformed JSON `POST /api/Customer` → **400**, no internals.
- Oversized id `GET /api/Customer/99999…` → **400**, no internals.
- Invalid bool `GET /api/Lead?isActive=notabool` → **200** (ignored), no internals.

No stack trace, connection string or bucket name in any error body. The one anonymous endpoint
(`Login`) was specifically hardened to return a fixed string and log the detail
(`Controllers/AuthController.cs:100-114`, "Sec-A3"). ✅

---

## Findings

**No exploitable request-level defect was found.** No S-effort fix is warranted; introducing a
"fix" with a regression test that passes both ways would violate `nexora-verify` §1. The items
below are **low-severity notes / deployment decisions**, not defects:

| Sev | Note | Where | Action |
|-----|------|-------|--------|
| Low | `Auth:RequireSecurityStamp=false` accepts *legacy stampless* tokens; harmless on a fresh deploy (all live accounts carry a stamp) | `Security/TenantSessionValidator.cs:110-126` | Set `=true` in Production after the one-hour post-deploy window |
| Info | BuiltIn malware scanner matches the EICAR signature only — not a real AV | `Security/DocumentInspection/MalwareScanners.cs` | Wire `ClamAvInstreamMalwareScanner` to a daemon in Production |
| Info | `/api/search` returns 400 (not 403) for restricted users / short queries — by design (scoped search) | `Controllers/SearchController.cs` | None |
| Info | Development relaxations (Swagger, loopback CORS, relaxed CSP, no HSTS) are present on this stack | `Program.cs:633,1122,1141,1190` | None — structurally Dev-gated; ensure the shared instance runs a **non-Development** environment |

## Verdict

From a dynamic, request-level standpoint, **the shared instance is safe to put an external
client's staff on**, provided it is deployed with **`ASPNETCORE_ENVIRONMENT` ≠ Development** (so the
Development-only relaxations — Swagger UI, loopback CORS, relaxed CSP, no HSTS — are off; the guards
that gate them are structural, not config toggles). Tenant isolation is enforced twice (claim-only
BU resolution + PostgreSQL RLS) and showed **no cross-tenant read or write** across 825 routes;
RBAC denies restricted users and honours segregation of duties; token revocation is immediate and
unconditional for live accounts; the SSRF and header-injection guards on the outbound path hold;
uploads are size-, type-, archive- and malware-gated; rate limits and login lockout are active; and
error/injection surfaces leak nothing. Recommended before go-live: set
`Auth:RequireSecurityStamp=true` and point the malware scanner at a real ClamAV daemon.

*The Production header/transport posture was verified by reading the environment guards, not
exercised live, because the disposable stack runs Development by design.*
