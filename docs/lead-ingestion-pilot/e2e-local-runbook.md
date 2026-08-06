# Local E2E Runbook — Lead → RFQ → Customer Quote Draft

**Verified working 2026-08-06 on macOS (darwin), Docker Desktop, .NET 8, Node/Vite.**
Everything below was executed in this session; nothing is aspirational.

> **No secrets in this file.** Every credential shown is a throwaway local value you set
> yourself. Nothing here is a real credential and none of it is committed.

---

## 1. Prerequisites

| Tool | Check |
|---|---|
| Docker (running) | `docker info` |
| .NET 8 SDK | `dotnet --version` |
| Node + npm | `node -v` |
| Playwright browsers | `cd Frontend && npx playwright install chromium` |

---

## 2. Start PostgreSQL

```bash
docker run -d --name nexora-e2e-pg \
  -e POSTGRES_PASSWORD=<local-only-password> \
  -e POSTGRES_DB=nexora_e2e \
  -p 55432:5432 postgres:16-alpine
docker exec nexora-e2e-pg pg_isready -U postgres     # -> accepting connections
```

Port **55432** deliberately avoids colliding with a local 5432.

---

## 3. Start the backend (migrations + demo identity)

`dotnet ef database update` **does not work** against a fresh database: the app's runtime
DbContext carries `TenantRlsCommandInterceptor`, which issues `SET ROLE nexora_pipeline_app`
before the migration that *creates* that role has run — a chicken-and-egg failure
(`SqlState 22023: role "nexora_pipeline_app" does not exist`).

Migrate **through the application**, which uses a separate options builder without that
interceptor (`Program.cs:632-644`). It is off by default outside Production, so opt in:

```bash
cd Backend/ERP_RFQ_Automation
S="<32+ byte local-only placeholder>"
ConnectionStrings__DefaultConnection="Host=localhost;Port=55432;Database=nexora_e2e;Username=postgres;Password=<local-only-password>" \
Database__ApplyMigrationsOnStartup=true \
Jwt__Key="$S" \
CommercialFinance__ContactVerificationSecret="$S" \
CommercialFinance__DunningProviderWebhookSecret="$S" \
CommercialFinance__AuditActorSecret="$S" \
DemoUser__Enabled=true DemoUser__Password="<local-only-password>" \
PlatformOwner__Password="<local-only-password>" \
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:5192" \
dotnet run --no-build --no-launch-profile
```

**Startup validation is strict and fails fast** — `Jwt:Key` and all three
`CommercialFinance:*` secrets must each be ≥ 32 bytes or the process exits.

Verify: `curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:5192/health` → **200**,
and `select count(*) from information_schema.tables where table_schema='public'` → **207**.

### The identity mechanism

`Infrastructure/DemoUserSeeder.cs` is the approved local path. It is **fail-closed**
(`DemoUser:Enabled` defaults false), **refuses to run in Production**, and **seeds no password
unless one is supplied** — there is no repo-published default credential. It provisions
`robert@example.com` as Super Admin in business unit `Customer POC`.

Confirm login returns a real JWT:

```bash
curl -s -X POST http://127.0.0.1:5192/api/Auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"robert@example.com","password":"<the password you set>"}'
```

---

## 4. Playwright discovery

```bash
cd Frontend
npx playwright test --list                                            # 373 tests / 30 files
npx playwright test --config playwright.core-commercial.config.ts --list   # 40 tests / 3 files
npx playwright test --config playwright.commercial-journey-v2.config.ts --list  # 39 tests / 1 file
```

**Discovery no longer requires any environment variable.** See §7 below for why it used to.

---

## 5. Environment variables

| Variable | Purpose |
|---|---|
| `ConnectionStrings__DefaultConnection` | Local PostgreSQL |
| `Database__ApplyMigrationsOnStartup` | **Required** — defaults to true only in Production |
| `Jwt__Key`, `CommercialFinance__*` | Startup validation, ≥ 32 bytes each |
| `DemoUser__Enabled`, `DemoUser__Password`, `PlatformOwner__Password` | Local identity seed |
| `ASPNETCORE_ENVIRONMENT=Development` | Demo seeder refuses under Production |
| `E2E_BASE_URL`, `E2E_API_URL` | Playwright targets |
| `E2E_MANAGER_EMAIL` / `_PASSWORD` / `_BUSINESS_UNIT_ID` | Browser login |
| `E2E_CORE_LEAD_ID`, `E2E_CORE_RFQ_ID`, … | Golden-scenario ids — **see the gap in §8** |

Write these to an ignored file (e.g. `Frontend/.env.e2e.local`, `chmod 600`). **Never commit it.**

---

## 6. Artifacts

`playwright-report/` (HTML), `test-results/` (traces, screenshots, video), and journey
screenshots under `docs/nexora/evidence/core-sales-force-inventory/`. The first two are
generated output and are not committed.

---

## 7. Why discovery used to report `0 tests in 0 files`

Four specs validated environment variables at **module scope** — `if (!password) throw …` beside
the imports. Playwright *evaluates every spec file* to collect its tests, so one missing variable
threw during collection and aborted discovery for the **entire suite**. The whole acceptance
suite was invisible, and the discovered-count gate in `zero-skips-reporter.ts` could not fire
either, because there was nothing to count.

Fixed by `requireEnv()` in `e2e/support/environment.ts`, called **inside** the test body — the
pattern `auth.setup.ts:14` already used. The failure is exactly as loud and names the same
variables; it simply happens at run time. **No test was skipped, disabled or deleted.**

Affected: `p0-duplicate-live`, `v2-gate04-supplier-negotiation-live`,
`v2-gate05-coaching-recovery-live`, `wave1-platform-parity`.

### The count gate is per-config, not global

`zero-skips-reporter.ts` is shared by **ten** configs but `EXPECTED_TESTS` applies only under
`E2E_FULL_ACCEPTANCE=true` — the `e2e:acceptance` script, i.e.
`playwright.commercial-journey-v2.config.ts`, matching `commercial-journey-v2.spec.ts`, which
holds exactly **39** tests. It was briefly raised to 40 for a test added to
`core-commercial-journey.spec.ts` — a different file under a different config — which would have
failed the acceptance run. Reverted to 39, with the scoping documented in the file.

---

## 8. Cleanup

```bash
pkill -f ERP_RFQ_Automation
docker rm -f nexora-e2e-pg
```

Set `KEEP_E2E_STACK=1` to keep it running between iterations.

---

## 9. Common failures

| Symptom | Cause / fix |
|---|---|
| `role "nexora_pipeline_app" does not exist` | Using `dotnet ef database update`. Migrate through the app instead (§3). |
| 0 tables created, app exits at `SyncFinanceProviderSecretsAsync` | `Database__ApplyMigrationsOnStartup` not set. |
| Process exits immediately | A required secret is under 32 bytes. |
| Login 401 | `DemoUser__Enabled`/`DemoUser__Password` unset, or environment is Production. |
| `0 tests in 0 files` | Pre-dates the §7 fix — rebase. |
