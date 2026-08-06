# Nexora deployment

Topology: **Frontend on Vercel** + **Backend on a container host** + **Neon (Postgres)**.
(Vercel cannot host the .NET backend — it's a persistent server with background
workers, not serverless.)

Current: frontend is deployed at **https://nexora1-ai.vercel.app**; it needs
the backend URL below to function.

---

## 1. Backend → Render

The current backend is deployed at **https://nexora-fyjw.onrender.com**.

Set configuration as **Render environment variables** (nothing sensitive is baked into the image —
`appsettings.json` ships only placeholders and the app FAILS FAST without these):

```text
ConnectionStrings__DefaultConnection=Host=<neon-DIRECT-endpoint>;Database=neondb;Username=neondb_owner;Password=<neon-pw>;SSL Mode=Require;Trust Server Certificate=True
Jwt__Key=<a NEW 32+ byte random key>
Security__SecretProtectionKey=<base64 of 32 random bytes: openssl rand -base64 32>
Ollama__BaseUrl=https://ollama.com/
Ollama__ApiKey=<ollama key>
Cors__AllowedOrigins__0=https://nexora1-ai.vercel.app
Storage__RootPath=/var/data/nexora/uploads
Storage__RequiredMountPath=/var/data
Storage__EnforcePersistentMount=true
```

The repository includes `render.yaml` with a persistent disk mounted at `/var/data`.
Use that Blueprint or attach an equivalent Render disk before accepting customer
documents. A service without the disk remains stateless and must not be used for RFQ
evidence ingestion.

In Production, the filesystem provider always requires an explicit storage root and
verifies that it is writable. The disk-backed profile additionally enables strict mount
verification with `Storage__EnforcePersistentMount=true`. For an emergency deployment
without a disk, omit that flag and `Storage__RequiredMountPath`; this restores service
but the files remain ephemeral and the deployment is not pilot-certified. Before moving
an existing service to the mounted root, copy any
recoverable legacy files and rewrite absolute `Attachments.FilePath` values to portable
`Uploads/...` paths; absolute paths outside the configured root are deliberately rejected.

- Health check: `GET /health` → `Healthy`. Render's deploy health check uses
  `GET /ready`, which additionally requires the database, evidence storage **and a
  reachable ClamAV daemon**.
- The app URL is `https://nexora-fyjw.onrender.com`.

### Malware scanning (required)

`render.yaml` declares a second Render service, `nexora-clamav` — a **private
service** running `docker.io/clamav/clamav:1.5` and reachable only over Render's
private network on TCP 3310. The backend streams every upload to it, and both
uploads and `/ready` **fail closed** without it.

`DocumentInspection__ClamAV__Host` / `__Port` are wired automatically by the
Blueprint; do not set them by hand. Before the first sync, confirm both services
are in the **same Render region** — private networking depends on it and a
service's region cannot be changed afterwards.

Setup, verification and outage handling: **[`docs/RUNBOOK-CLAMAV-RENDER.md`](docs/RUNBOOK-CLAMAV-RENDER.md)**.
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
- Credentials: provision through Render secrets and distribute out of band.

The seeder is disabled by default and never overwrites an existing password. For an
explicit first-run pilot seed, temporarily provide all five settings:

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
| `ConnectionStrings__DefaultConnection` | Neon Postgres connection |
| `Jwt__Key` | JWT signing key (**use a new strong key**; ≥32 bytes) |
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
