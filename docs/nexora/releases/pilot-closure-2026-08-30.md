# Frozen pilot closure — 2026-08-30

## Release being evaluated

- Main baseline: `e6aa2ddc948928f2e5887b9af3db79464e8e2a01` (PR #136).
- Closure branch: `fix/pilot-release-certification`.
- Scope is acceptance and operational safety, not additional modules or a UI redesign.
- No external emails, real commercial postings, destructive production operations, or paid infrastructure provisioning in this increment.

## Corrections to the earlier readiness assessment

Live evidence supersedes the old `render.yaml` commentary and historical audit documents.

1. **Real antivirus is already active.** At `2026-08-30T20:27:40Z`, production `/ready` reported
   `ClamAV malware scanner passed clean and detection controls.` The code executes both a clean
   scan and the EICAR detection control. A new scanner purchase is not an established blocker.
2. **Disk snapshots already exist.** Render's live Disk page showed seven daily snapshots, dated
   August 23–29, on the 5 GB disk at `/var/data`. No restore button was pressed. A snapshot listing
   does not prove a usable restore or consistency with the separate PostgreSQL database/evidence store.
3. **Both service and dependency health are green.** The runtime probe matched the full baseline
   SHA and found all eleven required checks healthy, including database, evidence, scanner,
   mailbox polling, workers, OCR, SMTP and capacity. SMTP was only health-checked; no mail was sent.
4. **The current test tenant is not a role-acceptance tenant.** Its user list contains two active
   Super Administrators, not independently configured Sales Manager and Sales Rep accounts.

Do not repeat the superseded claims that no antivirus or no backup exists.

## Acceptance harness defect and repair

The old PR workflow conditionally called `npm run e2e:acceptance` using Release-01B settings,
but that command selects the commercial-v2 fixture suite. It requires different fixture IDs,
platform MFA and integration credentials, and performs commercial/platform mutations. It also
defaulted the API target to localhost when absent. Merely setting `E2E_ACCEPTANCE_ENABLED=true`
would not certify the deployed application and must not be used as the closure action.

The repair:

- Commercial-v2 and core-commercial configurations reject non-loopback targets before execution.
- Existing disposable PostgreSQL/browser journeys stay in ordinary CI; they are not replaced by smoke tests.
- The stale conditional deployed block is removed from PR CI.
- `pilot-deployed.yml` is a separate, explicitly dispatched, main-only **post-deployment** check.
- It fails on missing configuration, incorrect backend SHA, unhealthy/missing dependencies, a
  structural-only scanner, shared persona accounts, wrong tenant or role, skipped or missing tests.
- Browser traffic permits reads and the tenant login POST only. Business writes are blocked and
  fail the run. Authenticated credentials are restricted to the two reviewed Nexora origins.
- Browser traces, video, screenshots and persisted authentication state are disabled in this lane.
- Its four tests cover runtime plus three role-access boundaries. They do **not** certify a complete
  state-changing journey, cross-tenant isolation, platform MFA, finance authority or disaster recovery.

## Configure the deployed lane

Create/approve the isolated pilot tenant through the normal Platform Console. Use three distinct
test identities; do not elevate them to administrator to make the tests pass. Do not send invitations
to customers/suppliers for this setup.

In the GitHub environment `pilot-certification`, configure:

| Setting | Kind | Requirement |
| --- | --- | --- |
| `PILOT_MANAGER_EMAIL`, `PILOT_MANAGER_PASSWORD` | Secret | Sales Manager: manager rank, Leads and Quotations view; no Users view or admin authority |
| `PILOT_EDITOR_EMAIL`, `PILOT_EDITOR_PASSWORD` | Secret | Sales Rep: member rank, Leads and Quotations view; no Users or manager authority |
| `PILOT_DENIED_EMAIL`, `PILOT_DENIED_PASSWORD` | Secret | Restricted member without Leads/Users access |
| `PILOT_BUSINESS_UNIT_ID` | Variable | All three identities belong to this isolated tenant |
| `PILOT_MANAGER_ROLE_NAME`, `PILOT_EDITOR_ROLE_NAME`, `PILOT_DENIED_ROLE_NAME` | Variable | Exact expected server role names, independently chosen at provisioning |

Role provisioning must also grant the relevant tenant capabilities and normal login/Inbox
entitlements. Capture those in the pilot onboarding record. Do not reuse the development fixture
passwords, reset existing operator passwords or copy an interactive browser token to CI.

After both hosts deploy the chosen full main SHA, dispatch **Deployed pilot verification (read-only)**
on `main` with that SHA. The checkout and backend identity must agree. Separately record the
Vercel deployment metadata proving that the production alias points to the same SHA; downloading
an HTML shell or a JavaScript bundle is not frontend commit proof.

Public runtime-only check (no credentials, no business writes):

```sh
PILOT_EXPECTED_SHA=<full-main-sha> \
PILOT_FRONTEND_URL=https://nexora1-ai.vercel.app \
PILOT_API_URL=https://nexora-fyjw.onrender.com \
node scripts/deploy/verify-pilot-runtime.mjs
```

The sanitised result is `Frontend/test-results/deployed-pilot/runtime.json` (ignored by Git).
Full role preflight adds `--preflight --roles`; it performs no network requests.

## Remaining closure gates — no silent waivers

| Gate | Current evidence | Remaining action |
| --- | --- | --- |
| Exact release/runtime | Backend SHA and 11 checks verified; Render deployment is Live | Recheck matching Vercel metadata for each candidate |
| Attachment malware controls | ClamAV clean and detection controls pass | Repeat in final candidate; keep fail-closed outage coverage |
| Recoverability | Seven disk snapshots exist | Verify DB recovery window and evidence-store backup; isolated restore drill with hashes, row counts, RPO/RTO and outbound workers disabled |
| Deployed roles | Current browser is a tenant Super Administrator | Separate Platform Console sign-in, clean tenant, named roles, negative API/browser checks |
| Complete client journey | Disposable CI lane exists | Deployed isolated-tenant evidence from email occurrence through cash; no customer/supplier sends; proof of approved-line lineage and replay safety |
| Operator controls | Platform Console correctly requires its separate sign-in | Authenticated provisioning, suspension, offboarding, storage controls and recovery acceptance without deleting real tenants |
| Client handoff | Frozen scope described here | Named pilot participants, master data, exclusions, support contact, recovery/rollback procedure and client acceptance |

The existing tenant Operations screen retains historical terminal source-integrity and
source-unavailable records. They are not proof of a fresh ingestion defect and must not be
blindly retried, erased or labelled resolved. A clean isolated tenant avoids mixing those
historical incidents into client onboarding.

## Verification of this increment

- Live runtime-only probe passed at `2026-08-30T20:27:40.171Z`: exact backend SHA, eleven
  health checks, login HTML, entry-module content type and required frontend security headers.
- Acceptance safety and zero-skip/count regression tests: **23 passed**.
- Existing authentication and manager-boundary regressions: **18 passed**.
- Frontend production build and bundle budget: passed.
- Full frontend lint, deployment-contract checks, workflow YAML parsing and `git diff --check`: passed.
- Dedicated deployed-test TypeScript check: passed (Node type declarations added as a dev dependency).
- Discovery: commercial-v2 **41**, core-commercial **40**, new deployed lane **4** tests.
  Discovery is not execution; neither live role checks nor the full commercial suites were rerun
  during this increment. The real API suites remain required CI lanes for the PR.
- Production dependency audit: **0 known advisories** (`npm audit --omit=dev`). Development-only
  advisories are not included in that claim; no automatic dependency remediation was performed.
- Neon project listing found `Nexora-neon-cyclamen-house` (`fancy-term-30744129`) in AWS US East 1
  with `history_retention_seconds=21600` (six hours). Matching that project to the active runtime
  endpoint and rehearsing recovery remain unverified. The project-detail connector has a
  parameter-schema mismatch; no database data or settings were changed.

## Release verdict

**Independent-pilot approval remains pending the unverified gates above.** Runtime checks alone,
the new read-only lane, a build, or an old consultant report cannot grant full approval.
