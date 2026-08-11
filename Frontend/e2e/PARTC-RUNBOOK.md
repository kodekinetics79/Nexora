# PART C runbook — governed stale-lease recovery under a relaxed platform MFA policy

Test-only. Independent SDET harness. It certifies or rejects; it never repairs.

| Asset | Path |
| --- | --- |
| Spec (18 steps) | `Frontend/e2e/partc-governed-recovery.spec.ts` |
| Reusable helpers | `Frontend/e2e/support/partc-control-plane.ts` |
| Step-ledger reporter | `Frontend/e2e/support/partc-step-ledger.ts` |
| Ambient types | `Frontend/e2e/support/partc-ambient.d.ts` |
| Config | `Frontend/playwright.partc-governed-recovery.config.ts` |
| Synthetic-tenant seed | `Frontend/e2e/partc-synthetic-tenant.seed.ts` |
| Seed config | `Frontend/playwright.partc-seed.config.ts` |
| Scoped type-check | `Frontend/tsconfig.partc.json` |

---

## 1. Stand the stack up

The harness **does not start anything**. It attaches to the stack that
`scripts/local/run-platform-console.sh` owns. Run that first, in its own terminal, and leave it
running — Ctrl-C tears the database container down and destroys the data.

```bash
cd /Users/zackkhan/Nexora/Nexora-main
export NEXORA_OWNER_PASSWORD="$(python3 -c 'import secrets;print("Local!"+secrets.token_urlsafe(18))')"
export NEXORA_CHECKER_PASSWORD="$(python3 -c 'import secrets;print("Local!"+secrets.token_urlsafe(18))')"
export NEXORA_OUTBOUND_GUARD=DraftOnly     # default; nothing leaves the box
./scripts/local/run-platform-console.sh
```

Wait for the banner. It prints the console URL, the owner email (`owner@nexora.local` unless
`NEXORA_OWNER_EMAIL` overrides it) and the path to the MFA seed. Ports: frontend `5173`,
API `5192`, PostgreSQL `55433`.

> **Sandbox note.** `docker start` on a pre-existing container is blocked in this environment.
> The run script does `docker run` on a fresh container, which works. Do not try to revive an
> old `nexora-local-pg`; the script removes and recreates it anyway.

## 2. Seed the tenant under examination

**The journey will not create it.** PART C is parameterised on a tenant that already has a
**failed provisioning execution**. A fresh database from the run script has none, so step 10
fails loudly and correctly rather than inventing one.

If you are pointing the run at a database that already holds such a tenant, set
`PARTC_TENANT_NAME` to its exact name and skip to §3.

Otherwise, run the one-shot seed:

```bash
npx playwright test --config playwright.partc-seed.config.ts
```

> ### The seeded tenant is a SYNTHETIC REPRODUCTION
>
> It is **not** the user's original "Noor and Sons", which is absent from every reachable
> database and whose failure nobody here observed. It carries the same *name* so the journey
> needs no override — it does **not** carry the same history, data, or cause of failure.
> Never describe evidence downstream of the seed as reproducing what happened to the real tenant.

The seed drives the **real four-step wizard in a real browser** — no INSERT, no direct API call.

### Why the failure is injected at the database

The obvious candidates cannot produce the state PART C needs, and the backend says so:

- a founding-administrator address that already exists is **rejected at submit with 409**
  (`ProvisioningRequestValidator`) — there is no execution row at all, so nothing to recover;
- a taken or reserved slug is likewise a 400/409 at submit;
- a missing or deactivated plan fails at ordinal 0 (`tenant`) with `FailureIsTerminal = true`
  and **zero committed steps**.

PART C needs a failure that is mid-execution (so earlier steps have genuinely committed) **and**
non-terminal (so the cause can be fixed and the same execution resumed).
`TenantProvisioningRunner.Describe` treats only SQLSTATE `23505` / `23503` / `42501` as terminal;
everything else is retryable. So the injected fault is `42P01 undefined_table` on the first
relation the baseline seeder reads — the same shape as the `42501` missing-GRANT failure the
runner's own comments record as having happened in production.

```bash
# 1. inject (before the seed)
docker exec nexora-local-pg psql -U postgres -d nexora_local \
  -c 'ALTER TABLE public."QuoteConfiguration" RENAME TO "QuoteConfiguration__partc_fault";'

# 2. seed, then WAIT for the execution to exhaust its 3 automatic attempts and park in Failed
#    (Platform:Provisioning:MaxAutomaticAttempts = 3, RetryBackoff 10s)

# 3. repair the root cause — this is the operator action the resume in step 14 depends on
docker exec nexora-local-pg psql -U postgres -d nexora_local \
  -c 'ALTER TABLE public."QuoteConfiguration__partc_fault" RENAME TO "QuoteConfiguration";'
```

This is fault injection into a disposable local database. It is **not** a change to product code
and **not** a pretend failure: the runner, the diagnostics projection and the resume path all
execute for real. Resulting state: steps 0-5 `Succeeded`, `baseline-seed` `Failed`
(`failureIsTerminal: false`), `invitation` `Pending`.

The seed refuses to run twice under the same tenant name: a second tenant of that name would
invalidate the duplicate counts in step 15. Steps 9-16 are **one-shot** — they consume the failed
execution — so re-running them needs a fresh seed under a new `PARTC_TENANT_NAME` /
`PARTC_TENANT_SLUG` / `PARTC_ADMIN_EMAIL`.

## 3. Export the harness environment

```bash
cd /Users/zackkhan/Nexora/Nexora-main/Frontend

export E2E_FIXTURE_MODE=false                       # step 1 refuses to run without this
export E2E_BASE_URL=http://127.0.0.1:5173
export E2E_API_URL=http://127.0.0.1:5192

export E2E_PLATFORM_ADMIN_EMAIL="${NEXORA_OWNER_EMAIL:-owner@nexora.local}"
export E2E_PLATFORM_ADMIN_PASSWORD="$NEXORA_OWNER_PASSWORD"
# The seed file is base32 and mode 0600. It is read HERE, by the operator, and passed as an
# environment value; the spec never touches the filesystem.
export E2E_PLATFORM_ADMIN_TOTP_SECRET="$(cat ../.local-run/platform-owner-mfa-secret)"

export PARTC_TENANT_NAME="Noor and Sons"
```

Never paste the seed, the password or a generated TOTP into a report, a screenshot caption or a
commit message.

## 4. Type-check and confirm discovery before burning a watched run

```bash
npx tsc -p tsconfig.partc.json --noEmit          # must print nothing
npx playwright test --config playwright.partc-governed-recovery.config.ts --list
```

`--list` must report **18 tests in 1 file**. Anything less means the spec failed to load and the
run would certify nothing.

## 5. Run the visible journey

```bash
npx playwright test --config playwright.partc-governed-recovery.config.ts
```

Real Google Chrome (`channel: 'chrome'`), headed, one worker, `slowMo: 150`, no retries, no
request interception, no mocked APIs. Expect **8–12 minutes**: steps 2, 7 and 17 may each wait
out a real 30-second TOTP window rather than weaken the server's replay fence.

## 6. Read the verdict

The run ends with the **PART C step ledger**:

- `EXECUTED` — the step asserted something real and it held.
- `PARTIAL` — the step asserted what it could; a named sub-check had no surface to measure.
- `BLOCKED` — the backing feature is absent. **The step asserted nothing.** Its reason names every
  candidate route tried and the status each returned; that list is the request to the implementing
  agent, verbatim.
- `FAILED` — a real defect. The message is the finding.

**A blocked step is never a pass.** A run of 18 steps with 9 blocked certifies 9 steps.

Artifacts:

| What | Where |
| --- | --- |
| Ordered screenshots, `01-…png` … `18-…png` | `.local-run/partc/evidence/` |
| Probe payloads, execution JSON, audit entries | attachments in the HTML report |
| HTML report | `.local-run/partc/report/` |
| Traces + video (on, every attempt) | `.local-run/partc/test-results/` |

**Artifacts live in `.local-run/`, outside the Vite root, and that is load-bearing.** With the
report inside `Frontend/`, Vite's watcher broadcast a full page reload for every trace file the run
wrote, which restarted the `React.lazy` import the console was mid-way through resolving. A
code-split route then sat on the Suspense spinner forever and the harness reported a shipped screen
as MISSING. Override the root with `PARTC_ARTIFACT_ROOT`, or the screenshots alone with
`PARTC_EVIDENCE_DIR` — but keep both out of `Frontend/`.

## 7. Pointing the harness at the real routes as A/B/C land

Nobody edits the spec to make a certification pass. Each capability is one environment variable
holding a comma-separated candidate list:

All three features have now landed, so the **first candidate in each list is the confirmed route**
and no override is needed for an ordinary run. The lists remain because a route change must show
up as a BLOCKED step naming what it tried, not as a silent green.

| Variable | Feature | First candidate (confirmed) |
| --- | --- | --- |
| `PARTC_MFA_POLICY_API` | A — Owner policy read/write | `GET\|PUT /api/platform/auth/policy` |
| `PARTC_MFA_EFFECTIVE_API` | A — effective policy (password-only reachable) | `GET /api/platform/auth/policy/effective` |
| `PARTC_MFA_POLICY_ROUTE` | A — console surface | `/platform/security/authentication` |
| `PARTC_DEPLOYMENT_PROFILE_API` | B — deployment profile | `GET /api/platform/provisioning/tenants/{id}/diagnostics` |
| `PARTC_PROVISIONING_OPENER` | B — tab that opens a failed attempt (regex on accessible name) | `^Provisioning$` |
| `PARTC_LEASE_RECOVERY_API` | C — lease state | `GET /api/platform/provisioning/executions/{id}/lease` |
| `PARTC_ENTITLEMENT_SNAPSHOT_API` | duplicate check | none — probed, still absent |

### The control contract step 5 and step 17 drive

Read off `Frontend/src/platform/pages/PlatformAuthenticationPage.tsx`:

| Control | How it is driven |
| --- | --- |
| `MFA enforcement mode` | native `<select>` → `selectOption('DISABLED_TEST_ONLY' \| 'REQUIRED')` |
| `Expires at` | `datetime-local`, **wall clock not UTC** — the harness fills local time |
| `Reason` | typed |
| `Confirmation phrase` | typed, read from the API's `confirmationPhrases`, **never hard-coded** |
| `Current password` | typed — password re-authentication |
| `Apply MFA policy` | button |

Banner: testid `platform-mfa-disabled-banner`, `role="alert"`, exact text
`MFA enforcement is disabled in this test environment.`, asserted on the policy screen **and after
navigating to Tenants**, because `PlatformLayout` renders it above `<Outlet />`.

Other testids asserted: `platform-mfa-policy-status`, `platform-mfa-not-relaxable` (a
production-class deployment refusing to relax — a correct refusal, reported as BLOCKED rather than
failed), `platform-mfa-apply-blocked` (surfaces *why* Apply is disabled instead of failing on a
dead button).

### Environment classification matters

`DISABLED_TEST_ONLY` is only permitted when the deployment classifies as `LocalOrTest`.
`PlatformMfaPolicyOptions.ClassifyEnvironment` maps `Development`/`Testing`/`Test`/`Local`/
`IntegrationTest` there, and **anything unrecognised classifies as Production** — fail-closed.
`run-platform-console.sh` sets `ASPNETCORE_ENVIRONMENT=Development`, so the local rig qualifies.
Against any other environment, step 5 will correctly block.

A probe treats **404 and 405 as absent**. Any other status means the route exists — `401`/`403`
is reported as `found: true, authorized: false`, i.e. *a defect to raise*, never *a feature to
wait for*.

Two routes are **asserted, not probed**, because they are confirmed on `TenantProvisioningController`
and a `404` from either is a regression rather than a pending feature:

- `GET /api/platform/provisioning/tenants/{tenantId}/diagnostics` → `TenantProvisioningDiagnostics`
- `GET /api/platform/provisioning/executions/{id}/diagnostics`

Steps 12 and 16 read `DeploymentProfile`, `FailedStep`, `Classification`, `ProductionBlockers` and
`LocalTestBlockers` from them.

## 8. Safety

Step 5 relaxes a platform-wide authentication control. `afterAll` restores `REQUIRED` through the
API whatever happened in between, and prints a `*** PART C HARNESS ALARM ***` block if it could
not. **If you see that alarm, restore the policy by hand before anyone else touches the
environment.** A certification run must not be able to leave the control plane weaker than it
found it.

## 9. Why this suite has its own config

- `playwright.config.ts` **throws at load** when `E2E_FIXTURE_MODE=false` unless ~20 tenant-plane
  fixture ids are exported. PART C is a control-plane journey with no lead, RFQ or quote to name.
- That config also starts a web server and, in fixture mode, a canned API — which would fight the
  run script for port 5173 and silently replace PostgreSQL with stubs in a *watched* run.
- It wires `zero-skips-reporter`, which fails any run containing a skip. PART C skips blocked
  steps on purpose. Wiring it there would make "feature A has not shipped" indistinguishable from
  "feature A is broken".

`retries: 0` is deliberate: a retry would re-drive a privileged policy change and a provisioning
resume — exactly the duplicate-side-effect class step 15 exists to detect.
