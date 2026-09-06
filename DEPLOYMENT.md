# Nexora deployment

Topology: **Frontend on Vercel** + **Backend on a container host** + **Neon (Postgres)**.
(Vercel cannot host the .NET backend — it's a persistent server with background
workers, not serverless.)

Current: frontend is deployed at **https://nexora1-ai.vercel.app**; it needs
the backend URL below to function.

---

## 1. Backend → Render

The current backend is deployed at **https://nexora-fyjw.onrender.com**.

The existing Render service (`srv-d9csjhe1a83c739phue0`, dashboard name `Nexora`) was
created manually and is not linked to the checked-in Blueprint. `render.yaml` is the
reviewed desired-state contract and matches the last verified dashboard layout, but a
green repository check is not evidence that Render consumed it. Before each release,
compare the live service with that contract through the Render API and prove the deployed
revision through `GET /build-identity`. Do not create a second service while attempting
to link the Blueprint.

Set configuration as **Render environment variables** (nothing sensitive is baked into the image —
`appsettings.json` ships only placeholders and the app FAILS FAST without these):

```text
ConnectionStrings__DefaultConnection=Host=<neon-DIRECT-endpoint>;Database=neondb;Username=neondb_owner;Password=<neon-pw>;SSL Mode=Require;Trust Server Certificate=True
ConnectionStrings__MigrationConnection=<the same, on the owner role — see below>
Jwt__Key=<a NEW 32+ byte random key>
Jwt__PlatformKey=<a SECOND, DIFFERENT 32+ byte random key>
Security__SecretProtectionKey=<base64 of 32 random bytes: openssl rand -base64 32>
CommercialFinance__ContactVerificationSecret=<32+ byte random secret>
CommercialFinance__DunningProviderWebhookSecret=<a different 32+ byte random secret>
CommercialFinance__AuditActorSecret=<a third 32+ byte random secret>
Platform__BootstrapOwnerEmail=<platform-operator-email>
Platform__BootstrapOwnerPassword=<one-time password, 12+ chars>
Ollama__BaseUrl=https://ollama.com/
Ollama__ApiKey=<ollama key>
Cors__AllowedOrigins__0=https://nexora1-ai.vercel.app
Storage__RootPath=/var/data/nexora/uploads
Storage__RequiredMountPath=/var/data
Storage__EnforcePersistentMount=true
```

**Every one of the first nine fails the boot, not a request.** `Program.cs` and
`AddPlatformJwtBearer` validate them before the host is built, so a missing or
placeholder value produces a container that exits during startup with the reason
on stdout and nothing in the application log. Three that are easy to miss because
they were previously undocumented:

- `Jwt__PlatformKey` must be present **and different from `Jwt__Key`** — otherwise a
  tenant token and a platform control-plane token could be forged from each other.
- `ConnectionStrings__MigrationConnection` is required because `render.yaml` sets
  `Database__AllowManagedOwnerRoleMigrationCompatibility=true`; migrations run on the
  owner role while the application serves traffic on the least-privilege runtime role,
  and the app refuses to conflate the two.
- `Platform__BootstrapOwnerEmail` / `Platform__BootstrapOwnerPassword` do not fail the
  boot — they fail *silently*, which is worse: see §3.

The repository contract includes a persistent disk mounted at `/var/data`. Apply the
equivalent settings to the existing service (or deliberately link that service to the
Blueprint after reviewing the plan) before accepting customer documents. A service
without the disk remains stateless and must not be used for RFQ evidence ingestion.

In Production, the filesystem provider always requires an explicit storage root and
verifies that it is writable. The disk-backed profile additionally enables strict mount
verification with `Storage__EnforcePersistentMount=true`. For an emergency deployment
without a disk, omit that flag and `Storage__RequiredMountPath`; this restores service
but the files remain ephemeral and the deployment is not pilot-certified. Before moving
an existing service to the mounted root, copy any
recoverable legacy files and rewrite absolute `Attachments.FilePath` values to portable
`Uploads/...` paths; absolute paths outside the configured root are deliberately rejected.

- Liveness and Render deploy health check: `GET /health` → `Healthy`.
- Operational readiness: `GET /ready` reports evidence storage, storage capacity,
  scanner, OCR and worker health. Inspect it after deployment; do not point Render's
  restart/liveness gate at it while the declared pilot storage posture is intentionally
  reported as not durable.
- Deployment identity: `GET /build-identity` must report the exact merge SHA from the
  Render deployment metadata.
- The app URL is `https://nexora-fyjw.onrender.com`.

### Data-boundary manifest (optional, but it is what stops the retyping)

`Platform:DataBoundaries:*` describes **this deployment's own estate**, per tenant
data-boundary type. Provisioning reads it and registers each declared boundary against
every new tenant, and verifies the one boundary the platform can genuinely observe — its
own PostgreSQL tenant scope — from a recorded probe. Without it, an operator has to hand-type
the platform's provider reference, region and backup policy into a form for every tenant,
and hand-hash an evidence document about a database the platform runs itself.

```ini
Platform__DataBoundaries__PostgreSqlTenantScope__OpaqueProviderReference=neon-project-nexora-prod
Platform__DataBoundaries__PostgreSqlTenantScope__Region=us-east-1
Platform__DataBoundaries__PostgreSqlTenantScope__BackupPolicyReference=neon-pitr-7d
Platform__DataBoundaries__PostgreSqlTenantScope__BackupPolicyVersion=3
```

Repeat per type: `PostgreSqlTenantScope`, `ObjectStorage`, `SearchIndex`, `EmbeddingStore`,
`Cache`, `QueuePayload`, `GeneratedExport`, `AiOcrProvider`, `Subprocessor`. Optional
per-type overrides: `LogicalKey`, `Classification`, `Disposition`.

- **`Region` must equal every tenant's contractual `DataRegion`.** They disagree, the probe
  fails, the provisioning step fails, nothing is registered and `data.residency-isolation`
  stays blocking. That is the intended behaviour, not a bug to configure around.
- **`OpaqueProviderReference` is an opaque identifier** — never a URL, connection string or
  credential. The registry refuses anything containing `://`, `@`, `=` or `?`.
- **A type declared with a missing field is refused**, logged, and left on the manual path.
  Nothing is defaulted; a guessed provider reference would be a residency claim nobody made.
- **Set nothing and nothing changes.** An absent section is the pre-existing manual behaviour,
  exactly.
- Only `PostgreSqlTenantScope` is *verified*. The rest are *registered*, which is what
  deletion certification needs from them, and is not a claim that anything about a
  subprocessor has been checked.

**Tenants provisioned before the manifest was set.** Provisioning was the only moment the
automation ever ran, so a tenant created before these keys existed stayed on the manual path
forever. It no longer does: on the tenant's **Activation** tab, `data.residency-isolation`
opens a dialog with no fields and one button, which calls
`POST /api/platform/tenants/{id}/data-assets/apply-platform-manifest` (Owner). That registers
the declared boundaries and verifies the PostgreSQL scope from the same live probe
provisioning uses, under the same registry rules — a probe that disagrees refuses the whole
action. `GET /api/platform/data-boundaries` is what the console reads to decide whether it can
offer the button at all; declare nothing and the operator gets the manual form, now carrying
the four key names that would end it.

A tenant with **no contractual `DataRegion`** can never pass this control — the probe has
nothing to agree with. Two things now prevent that: provisioning records the declared region
when the wizard's Data region box is left blank (a submitted region is never overridden), and
the button above fills an empty column from the declaration and audits it as having come from
there. A region that is already recorded and disagrees is refused, never rewritten.

### Malware scanning posture

The current Render contract deliberately selects the built-in structural inspector for
the pilot. It enforces file/type/archive safety and the EICAR test string, but it is **not
an antivirus engine** and must not be represented as one. Real customer-document
production requires the private ClamAV service and host wiring described in
**[`docs/RUNBOOK-CLAMAV-RENDER.md`](docs/RUNBOOK-CLAMAV-RENDER.md)**. The single-box
deployment already includes ClamAV; this paragraph concerns the current Render topology.

### Retired or orphaned mailbox remediation

Mailbox polling is stricter than other legacy background work: a live external mailbox
credential must belong to a serviceable platform Tenant. An active IMAP row whose business
unit has no platform Tenant, or whose Tenant is Provisioning, PastDue, Suspended or Archived,
is not polled and is not included in `/ready`. Its durable failure counters remain in the
database as operational evidence; the application does not silently rewrite or delete them.

After deploying this boundary, no data migration is required to clear readiness. On the next
poller start/cycle, only eligible mailboxes seed the in-process health ledger. Operators should
still resolve the stale row deliberately:

1. If the customer is genuinely active, repair the Tenant-to-BusinessUnit linkage first and
   validate the mailbox credentials from Setup → Email Inboxes.
2. If the customer was retired, deactivate the mailbox through that governed screen. For an
   orphan that no tenant administrator can reach, use a reviewed owner-role maintenance change
   scoped to the exact `Email_Configurations.ID`, retain the row and its poll history, and record
   the operator/reason in the platform change ticket. Do not delete the mailbox row or its
   evidence merely to turn `/ready` green.
3. Verify the retired mailbox's failure count no longer advances, the active mailbox still
   advances `LastSuccessfulPollOn`, and `/ready` no longer names the retired address.
- **Neon endpoint:** use the **direct** endpoint (no `-pooler`) for now. The
  pooled endpoint needs `Max Auto Prepare=0` + RLS-via-`SET LOCAL` (ADR-0005 Ph2).

> Any container host works (Render, Fly.io, Railway, Azure Container Apps) — they all build
> the same `Dockerfile`.

## 2. Frontend → Vercel (already deployed)

The SPA reads the API base URL from `VITE_API_BASE_URL` **at build time**. In the
Vercel project settings:

1. **Environment Variables** → add `VITE_API_BASE_URL = https://nexora-fyjw.onrender.com`
   (your backend URL).
2. **Redeploy** the frontend so the value is baked in.

`vercel.json` already has the SPA rewrite for client-side routing.

## 3. Pilot login provisioning

Tenant app:

- URL: https://nexora1-ai.vercel.app
- Credentials: provision through Render secrets and distribute out of band.

Platform console:

- URL: https://nexora1-ai.vercel.app/platform
- Credentials: `Platform__BootstrapOwnerEmail` + `Platform__BootstrapOwnerPassword`, set as
  Render secrets and distributed out of band.

**This is the only bootstrap that works in Production, and without it nobody can sign into
the operator console at all — which means no tenant can be provisioned and the customer
journey has no entry point.** `PlatformOwnerSeeder` runs in Production deliberately and is
fail-closed in every direction: with both unset it silently creates nothing; with one set it
warns and creates nothing; a password under 12 characters is refused; and once **any**
platform user row exists (active or not) it is skipped forever. It can only ever create the
very first account, so leaving the two variables in place after the first boot is inert.

The first thing to do after signing in is enrol MFA — every privileged platform policy
(`PlatformScope`, `Owner`, `TenantAdmin`, `Billing`, `Impersonate`) requires a second factor,
so a bootstrap owner who has not enrolled can reach only the enrolment endpoints. Then rotate
the password through `/api/platform/users`.

> **Do not use `DemoUser__Enabled` + `PlatformOwner__Email` / `PlatformOwner__Password` for
> this.** Those belong to `DemoUserSeeder`, which **refuses to run** under
> `ASPNETCORE_ENVIRONMENT=Production` (the Dockerfile and `render.yaml` both set it): it
> provisions a tenant Super Admin from outside any HttpContext, with EF query filters and RLS
> role selection both inert, and Production is not a place that is acceptable. On Render it
> logs an error and creates nothing, leaving the console unreachable with no other symptom.

Tenant demo login (non-Production environments only — a staging or local host):

```text
DemoUser__Enabled=true
DemoUser__Email=<tenant-admin-email>
DemoUser__Password=<unique-one-time-password>
PlatformOwner__Email=<platform-owner-email>
PlatformOwner__Password=<different-unique-one-time-password>
```

After the first successful seed, set `DemoUser__Enabled=false`, remove both password
secrets, and rotate the credentials through the application before customer use.

## Required backend env vars (reference)

| Key (env form) | Purpose |
|---|---|
| `ConnectionStrings__DefaultConnection` | Neon Postgres connection (least-privilege runtime role) |
| `ConnectionStrings__MigrationConnection` | Owner-role connection used **only** to apply migrations. **Required** while `Database__AllowManagedOwnerRoleMigrationCompatibility=true` (which `render.yaml` sets) — the app **refuses to start** without it rather than migrate on the runtime role. |
| `Jwt__Key` | JWT signing key (**use a new strong key**; ≥32 bytes) |
| `Jwt__PlatformKey` | Signing key for the **platform control plane** token. **Required outside Development/Testing and must differ from `Jwt__Key`** — the app **refuses to start** on either violation, because a shared key would let a tenant token and an operator token be forged from one another. |
| `Platform__BootstrapOwnerEmail` / `Platform__BootstrapOwnerPassword` | The **first platform operator**. Without both, no platform user exists and `/platform` cannot be signed into by anyone — no tenant can be provisioned. Password ≥12 characters. Creates the first account only; skipped forever once any platform user exists. See §3. |
| `Security__SecretProtectionKey` | **AES-256 key encrypting stored customer mailbox credentials at rest** (`Email_Configurations.Password`). Base64 of exactly 32 random bytes — `openssl rand -base64 32`. The API **refuses to start** without it outside Development. Losing or rotating it makes every already-encrypted mailbox password undecryptable and email polling stops until credentials are re-entered. |
| `CommercialFinance__DunningProviderWebhookSecret` | HMAC secret for authenticated dunning delivery events (**use a distinct secret**; ≥32 bytes) |
| `CommercialFinance__ContactVerificationSecret` | HMAC secret for trusted finance-contact verification assertions (**use a distinct secret**; ≥32 bytes) |
| `CommercialFinance__AuditActorSecret` | HMAC secret binding authenticated actors to governed database mutations (**use a distinct secret**; ≥32 bytes) |
| `Ollama__BaseUrl` / `Ollama__ApiKey` | AI extraction provider (until the Claude migration, ADR-0001) |
| `Cors__AllowedOrigins__0..n` | allowed frontend origins (the Vercel URL) |
| `ASPNETCORE_ENVIRONMENT` | `Production` (set by the Dockerfile) |

## Security reminders before a real pilot
- **Rotate** the 3 original secrets (old SQL `sa` / JWT / Ollama) — `SECURITY.md`.
- Consider rotating the Neon credentials (they passed through chat).
- Keep pilot credentials out of source control and distribute them through an approved secret channel.
