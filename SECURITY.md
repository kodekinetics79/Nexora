# Nexora — Security & Secrets

## ⚠️ ACTION REQUIRED: rotate three exposed credentials

These credentials were found in **plaintext** in tracked config/docs during the
Phase 0 baseline. They have been **removed from source** (replaced with
`__PLACEHOLDER__` tokens). Because they were live and stored in cleartext, each
must be **rotated** — removal from source does not undo prior exposure.

| # | Secret | Where it was | Blast radius if leaked | Priority |
|---|--------|--------------|------------------------|----------|
| 1 | **JWT signing key** (`Jwt:Key`) | `appsettings.json`, `appsettings.Development.json` | **Auth bypass.** Anyone with this key can forge a valid JWT for any user/role/business-unit → full impersonation across tenants. | **P0** |
| 2 | **SQL Server `sa` password** | `appsettings*.json`, inner `Readme.md` | Full control of the database (`sa` = sysadmin) at `__DB_SERVER__`. Read/write/drop all tenant data. | **P0** |
| 3 | **Ollama Cloud API key** (`Ollama:ApiKey`) | `appsettings*.json` | Billable API usage on your account; potential data-egress of customer RFQ content to a third party. | **P1** |

### Rotation steps (owner: you)
1. **JWT key** — generate a new 256-bit random key, set it via the config
   mechanism below, and **restart the API**. All existing tokens are invalidated
   (users re-login). Do this first.
2. **SQL `sa`** — change the `sa` password on the SQL Server host. Better:
   create a **least-privilege application login** scoped to the
   `ERP_RFQ_Automation` database instead of using `sa` at all. Coordinate —
   the DB may be shared.
3. **Ollama key** — revoke the exposed key in the Ollama account and issue a new
   one. (Note: an ADR is in progress to move the AI layer to Claude — see
   `docs/adr/`.)

## Stored customer mailbox credentials (encrypted at rest)

`Email_Configurations.Password` holds a **customer mailbox credential** — the corporate
Exchange/O365 password Nexora replays to the tenant's IMAP/SMTP server on every poll and
send. It cannot be hashed, because it must be presented verbatim; it used to be stored in
**cleartext**, readable by every database role that bypasses RLS (`nexora_identity_app`,
`nexora_pipeline_app`) and by anyone holding a backup.

It is now protected with **AES-256-GCM** at the EF Core persistence boundary
(`Security/ProtectedSecretConverter.cs`), so every read and write is transparently
encrypted/decrypted and no service can forget to do it. The stored form is versioned and
self-describing — `v1:<base64 nonce>:<base64 ciphertext||tag>` — with a fresh random nonce
per value, so two tenants sharing a password do not share a ciphertext. Tampering fails the
GCM tag check and **throws** rather than returning a corrupted credential.

**`Security:SecretProtectionKey` (env: `Security__SecretProtectionKey`) is required.**
Base64 of exactly 32 random bytes:

```
openssl rand -base64 32
```

- Outside Development a missing, placeholder, short, non-base64 or all-zero key makes the
  API **fail at startup**, exactly like `Jwt:Key` — a misconfigured deploy cannot silently
  fall back to writing cleartext.
- In Development only, an ephemeral random key is generated and logged as **INSECURE**.
  It dies with the process, so anything encrypted under it is unreadable after a restart.
- Existing cleartext rows are converted by an idempotent startup backfill
  (`Security/MailboxCredentialProtectionBackfill.cs`). It skips values already carrying the
  `v1:` prefix and logs-and-continues per row, so one bad row cannot leave the rest in
  cleartext. The data conversion is deliberately NOT a SQL migration: the key lives in
  application configuration and must never be reachable from inside the database.
- **Treat this key like `Jwt:Key`.** Losing or rotating it makes every already-encrypted
  mailbox password undecryptable and email polling stops until credentials are re-entered.

## Platform operator MFA enforcement (`Platform:Mfa:*`)

Whether platform operators must present a second factor is a **persisted, versioned, audited
policy row** (`platform."PlatformMfaPolicies"`, singleton), not a configuration flag — so a change
has an author, a reason and an expiry, and so it cannot be made by editing a file. What
configuration decides is the **ceiling**: what this deployment is allowed to do at all. That
decision is taken in `Platform/Auth/PlatformMfaPolicyOptions.cs` from `IHostEnvironment` plus the
keys below, and it is enforced three times — at startup, on write, and again on every read — so a
database restored from a staging snapshot is ignored rather than obeyed.

| Environment class | Derived from `ASPNETCORE_ENVIRONMENT` | Permitted modes |
| --- | --- | --- |
| `Production` | `Production`, **and anything unrecognised** | `REQUIRED` only |
| `StagingOrUat` | `Staging`, `UAT`, `PreProduction`, `PreProd`, `QA` | `REQUIRED`, `OPTIONAL` (+ `DISABLED_TEST_ONLY` only with the isolation key below) |
| `LocalOrTest` | `Development`, `Testing`, `Test`, `Local`, `IntegrationTest` | all three |

An unrecognised environment name classifies as **Production**. That is deliberate: the opposite
default fails open on exactly the name nobody remembered to add. There is deliberately **no key
that names the environment class directly** — such a key would be a production bypass with extra
steps.

| Key (env form) | Default | Range | What it does |
| --- | --- | --- | --- |
| `Platform:Mfa:IsolatedTestInfrastructure` (`Platform__Mfa__IsolatedTestInfrastructure`) | `false` | bool | Declares a Staging/UAT deployment to be isolated test infrastructure with no customer data. It is the **only** thing that lets Staging reach `DISABLED_TEST_ONLY`. Setting it in a Production-classified environment makes the API **fail at startup**, exactly like `Jwt:Key` — a staging appsettings copied onto a production host must not boot. |
| `Platform:Mfa:MaxBypassHours` (`Platform__Mfa__MaxBypassHours`) | `24` | 1–24 | Ceiling on how long any non-`REQUIRED` policy may run. An expiry is **mandatory** for every mode other than `REQUIRED`; the effective mode reverts to `REQUIRED` on the first read after it passes, with no job and no human involved. |
| `Platform:Mfa:BrowserTrustHours` (`Platform__Mfa__BrowserTrustHours`) | `12` | 8–720 | **Seed only.** The "remember this browser" window until a policy row exists; from then on `platform."PlatformMfaPolicies"."BrowserTrustHours"` and `."BrowserTrustEnabled"` decide and this key is ignored (see below). The server stores only SHA-256 of the token the browser holds (`platform."PlatformBrowserTrusts"`); revoking is a column both the login redemption and `PlatformSessionValidator` read, so it takes effect on the next request and ends the session the trust minted. |
| `Platform:Mfa:PasswordReauthWindowMinutes` (`Platform__Mfa__PasswordReauthWindowMinutes`) | `5` | 1–15 | How long a password re-authentication (`POST /api/platform/auth/reauthenticate`) satisfies a high-risk operation. |

A value that parses but sits outside its range **throws at startup** rather than being clamped: an
operator who wrote `MaxBypassHours: 240` believes they have ten days, and a silent clamp to 24
leaves the belief and the system disagreeing with nobody told.

**"Remember this browser" is a policy row, not a deployment setting.** `BrowserTrustEnabled` and
`BrowserTrustHours` are columns on the singleton `platform."PlatformMfaPolicies"` row, changed only
through `PUT /api/platform/auth/policy` — platform **Owner**, password re-authentication, typed
confirmation, a reason, an `expectedVersion` fence and an audit row carrying the before and after
values. `Platform:Mfa:BrowserTrustHours` seeds a deployment that has never had a policy row and is
ignored once one exists, on the same appsettings-versus-row precedence the outbound email settings
use.

The permitted window is **8 hours to 720 hours (30 days)**, bounded by a database check constraint as
well as by the service, so a hand-written `UPDATE` cannot install "remember forever". The ceiling was
12 hours until an explicit product decision raised it; a month-long browser trust is a real trade —
fewer challenges, a longer window in which a stolen laptop signs in without one — and it rests on
three things staying true:

- **`BrowserTrustEnabled` is enforced at redemption, not only at issuance.** Switching it off refuses
  trusts that were *already* granted, at the next sign-in, rather than only stopping new ones.
- **Revocation reaches backwards.** `RevokedAtUtc` is a predicate inside the redemption query and
  inside `PlatformSessionValidator`, so revoking a browser both refuses the next sign-in and
  invalidates the live session that trust already minted. An operator can revoke one browser or all
  of them (`POST /api/platform/auth/browser-trusts/revoke-all`, own account only) from Platform Admin
  → Security → Platform Authentication.
- **The sign-in screen states the real window** ("Don't ask again on this browser for 30 days") and
  does not render the checkbox at all when the switch is off — both read from the server on the
  challenge response, because at that point the operator holds no token and cannot read the policy
  endpoint.

**Compensating controls while enforcement is relaxed.** Tenant purge, tenant export, personal-data
erasure, legal-hold release and subscription-invoice finalisation carry
`[PlatformHighRiskOperation]`. On an MFA-bound session it changes nothing. On a password-only
session — reachable only on non-production infrastructure — it demands a password
re-authentication inside the window above, on top of the existing typed confirmations and role
gates. Changing the MFA policy itself always requires Owner, current-password re-authentication, a
reason of at least 20 characters, a mandatory bounded expiry and a typed confirmation phrase.

## External AI egress consent (`Ai:ExternalProvider:AutoAuthorizeInternalDeployment`)

Whether a tenant's document text may leave their infrastructure is a **persisted, versioned,
audited decision** — a row in `"AiProcessingPolicies"` plus a per-destination grant in
`"AiExternalProviderAuthorizations"` naming the exact origin, model, purposes and expiry, each
written by a named platform Owner with a justification. Configuration decides one thing only:
whether this deployment may write those rows **on the tenant's behalf, with no human**.

| Key (env form) | Default | What it does |
| --- | --- | --- |
| `Ai:ExternalProvider:AutoAuthorizeInternalDeployment` (`Ai__ExternalProvider__AutoAuthorizeInternalDeployment`) | `false` | On the first governed call for a tenant, opens `IsEnabled`, `ExternalProcessingAllowed`, `EgressPolicy = FullDocument` and the extraction purposes, and writes a grant for the deployment's own inference endpoint authored by `system:auto-internal`. |

**It ships off, and the default is the control.** The gate that triggers this path is reached only
for an `External` provider class — both callers, the token ledger's `AuthorizeExternalAsync` and
the conversational extraction gate, test that first — so every row it can write is a decision to
send one customer's documents to a host they do not control. It is legitimate for a **single-tenant
installation whose deployer is also the customer**: there is no self-service tenant admin, and the
person who chose the endpoint is the person whose data goes to it. It is not legitimate for a
multi-tenant deployment, where it would take that decision for every tenant at once, silently, on
the first document each of them submitted.

Turning it on has one further consequence worth stating plainly: the extraction readiness
pre-flight (`GET /api/platform/tenants/{id}/ai-readiness`) performs **no writes** and therefore
cannot model it, so the operator console will go on reporting those controls closed while
documents egress successfully. Report and enforcement only agree while this key is off.

With it off, a tenant that should use an external provider is set up through the platform console
— the AI governance tab's policy and provider-authorization dialogs — which produce the same rows
with a real actor, a real justification and an expiry.

## Configuration pattern (how secrets are supplied now)

`appsettings.json` is a **committed template containing only placeholders**
(`__DB_PASSWORD__`, `__JWT_SIGNING_KEY__`, `__OLLAMA_API_KEY__`, …). Real values
are supplied per-environment and are **never committed**:

- **Local dev / demo:** real values live in `appsettings.Development.json`, which
  is **git-ignored** (see `.gitignore`). The app loads it automatically when
  `ASPNETCORE_ENVIRONMENT=Development`, so the demo runs unchanged.
- **Preferred (more secure):** use .NET user-secrets, e.g.
  ```
  dotnet user-secrets init
  dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=...;"
  dotnet user-secrets set "Jwt:Key" "<new-random-key>"
  dotnet user-secrets set "Ollama:ApiKey" "<new-key>"
  ```
- **Production:** supply via environment variables / a secrets manager. Because
  `appsettings.json` holds only placeholders, a misconfigured prod deploy
  **fails closed** (cannot connect / cannot sign tokens) rather than silently
  using a leaked credential.

## Notes
- `appsettings.Development.json` still holds the **old** live values on this
  machine so the demo keeps working. Replace them with the **rotated** values
  when you rotate.
- User-uploaded images under `wwwroot/UserImages`, `wwwroot/InventoryImages`,
  etc. are git-ignored (may contain personal data). They remain on disk for the
  demo but are not committed.
