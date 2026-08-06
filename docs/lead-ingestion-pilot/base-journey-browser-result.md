# Base Journey — Browser Result

**Date:** 2026-08-06 · **Repo:** `Nexora-main`, `release/nexora-v2-v3-accelerated`

## 1. Final decision

> # BASE LEAD → RFQ → CUSTOMER QUOTE DRAFT: **NO-GO**

Not blocked by authentication, and no longer blocked by the environment. Blocked by **one named
piece of remaining work**: a deterministic seeder for the four-line golden Lead (§8 of the
assignment). The full authenticated browser journey has **not** been executed, so GO cannot be
issued. Compilation and discovery are explicitly not GO.

**Material change since the last report:** the previous blocker — "no live environment, no
credentials" — is **gone**. A local stack now runs, migrates, seeds an identity and authenticates.
That was the thing believed to need something external. It did not.

## 2. Why Playwright previously found zero tests

Four specs validated environment variables at **module scope** (`if (!password) throw …` beside
the imports). Playwright evaluates every spec file to collect its tests, so one missing variable
threw during collection and **aborted discovery for the entire suite**. The whole acceptance suite
was invisible — and the discovered-count gate in `zero-skips-reporter.ts` could not fire either,
because there was nothing to count. A missing secret silently disabled 100% of browser coverage.

Fixed with `requireEnv()` in `e2e/support/environment.ts`, called **inside** the test body — the
pattern `auth.setup.ts:14` already used. Failure stays exactly as loud and names the same
variables; it happens at run time instead of collection time.

| Discovery command | Before | After |
|---|---|---|
| `npx playwright test --list` (no env) | **0 tests in 0 files** | **373 tests in 30 files** |
| `--config playwright.core-commercial.config.ts --list` | n/a | **40 tests in 3 files** |
| `--config playwright.commercial-journey-v2.config.ts --list` | n/a | **39 tests in 1 file** |

**No test was skipped, disabled, deleted, or gated behind a new condition.**

### A defect I introduced, and corrected

`zero-skips-reporter.ts` is shared by ten configs, but `EXPECTED_TESTS` applies only under
`E2E_FULL_ACCEPTANCE=true` — the `e2e:acceptance` script running
`playwright.commercial-journey-v2.config.ts`, which matches `commercial-journey-v2.spec.ts` and
holds exactly **39** tests. I had raised it to 40 for a test added to
`core-commercial-journey.spec.ts` — a **different file under a different config**. That would have
failed the acceptance run by expecting a test that suite never contained. Reverted to 39, with
the per-config scoping documented in the file so the mistake is not repeated.

## 3. Environment — built and proven

| Step | Result |
|---|---|
| PostgreSQL 16 (Docker, port 55432) | started, `pg_isready` → accepting connections |
| Migrations | **207 tables** created in `public` |
| Backend (`http://127.0.0.1:5192`) | `/health` → **200** |
| Identity seed | `robert@example.com`, Super Admin, BU 1 `Customer POC` |
| **Authentication** | `POST /api/Auth/login` → **200 with a real JWT** |

Two environment defects found and documented in the runbook:

1. **`dotnet ef database update` cannot migrate a fresh database.** The runtime DbContext carries
   `TenantRlsCommandInterceptor`, which issues `SET ROLE nexora_pipeline_app` *before* the
   migration that creates that role — `SqlState 22023`. Migrating through the application works,
   because `Program.cs:632-644` uses a separate options builder without that interceptor.
2. **`Database:ApplyMigrationsOnStartup` defaults to true only in Production**, so a local run
   silently starts against an empty schema and dies later at `SyncFinanceProviderSecretsAsync`
   with a confusing "relation does not exist".

The identity path is the repository's own `Infrastructure/DemoUserSeeder.cs`: fail-closed,
refuses to run in Production, and seeds **no password unless one is supplied** — there is no
repo-published default credential. No authentication was bypassed or disabled.

## 4. Ownership continuity — proven against the real API

`POST /api/commercial-intelligence/reps/{userId}/routing-profile`, exercised against the running
backend and real PostgreSQL:

| Case | Result |
|---|---|
| Create profile | **200**, `version: 1`, `sales_rep_profiles` **0 → 1 row** |
| Retry, same idempotency key and body | **200**, *same* profile id, still **1 row** |
| No token | **401** |
| User id outside the tenant | **404** "User 999 was not found in this business unit" |

`sales_rep_profiles` holding zero rows was the root cause of 44/44 production leads routing to
`NO_MATCH_EVIDENCE`. It is now writable through a real, permissioned, tenant-scoped API.

### Defect found by real-API testing, and fixed

The endpoint defaulted `EffectiveFromUtc` to `DateTime.UtcNow`. A genuine retry therefore sent
the same idempotency key with a *different* timestamp, the service saw different content, and
answered **409** — no caller could safely retry. Floored to `DateTime.UtcNow.Date`: an
effective-dated ownership record starts on a **day**, not at `14:12:55.441094`. Retry now returns
the same profile (verified above). This was invisible to unit tests and only appeared when the
endpoint was driven for real.

## 5. Test lanes — all green after the fix

| Lane | Result | Real vs mocked |
|---|---|---|
| Backend non-PostgreSQL | **Failed 0 · Passed 2092 · Skipped 0** (1 m 50 s) | SQLite/in-memory |
| Backend PostgreSQL | **Failed 0 · Passed 324 · Skipped 0** (2 m 47 s) | **real PostgreSQL**, Testcontainers |
| Frontend typecheck | exit **0** | real compiler |
| Playwright discovery | **373 / 40 / 39** as above | real collection |
| **Authenticated browser journey** | **NOT EXECUTED** | — |

Retries: 0. Skips: 0. Nothing mocked in place of an API.

## 6. Remaining blocker — one item

**The four-line golden Lead has no deterministic seeder.** §8 requires a Lead carrying: a hard
warning that must be corrected, an acknowledgeable soft warning, a line to exclude with a reason,
and at least two valid participating lines.

It cannot be produced by the paths available:
- **Manual upload** runs the real extraction pipeline, which requires an LLM provider — the line
  warnings would be non-deterministic even if one were configured.
- **Direct SQL insertion is explicitly forbidden** by the assignment, and rightly so.

The correct route is §6 option C — an **environment-gated E2E seeder** that refuses outside
Development, uses deterministic idempotency keys, is tenant-scoped, and is itself tested. That is
the next unit of work, and it is the *only* thing standing between here and an executable journey.

## 7. Targeted completeness checks

| Check | Status |
|---|---|
| **5.1 Commercial document classification** | **NOT IMPLEMENTED.** No authoritative field exists: `InquiryType` is `product\|service\|mixed`, `Rfqtype` is `Agreement\|Direct`, `LeadOccurrenceClassification` is duplicate identity. Building it was deliberately deferred — it needs a product decision on where the classification originates (manual uploads have no triage outcome at all), and a field nothing populates would either block every conversion or default permissive. |
| **5.2 Ownership continuity** | **Write path proven** (§4). Read-side resolution through `RFQ.LeadId → effective LeadAssignment` is **not yet surfaced** on RFQ/Quote. No `Rfq.OwnerId` or `Quote.OwnerId` was added. |
| **5.3 A-9 included/excluded lines** | **Confirmed still open.** `FindConversionBlockers` evaluates every Lead line, not only included ones. Pinned by `ConversionWarningGovernancePostgreSqlTests.Excluding_a_zero_quantity_line_does_NOT_bypass_the_lead_level_blocker` so the behaviour is documented rather than silently relaxed. |

## 8. Artifacts

Backend log: scratchpad `backend.log`. Safety patch (binary-capable, 297,466 bytes):
`…/scratchpad/nexora-phase1-20260806-100257.patch`. No Playwright HTML report or trace exists —
no browser run occurred, and fabricating artifacts would be worse than having none.
