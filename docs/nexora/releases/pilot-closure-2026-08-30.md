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

### Authenticated operator continuation — 2026-08-30, 21:23–21:47 UTC

- PR **#137** merged after all **24** reported checks passed, with unchanged head
  `228553385a180870a43a6242e126cce48a6d398d`. Merge commit:
  **`3cdf249e6a606843b76bcedb40d070317cce9549`**.
- Vercel production deployment `dpl_6BD7v61ZewFvwVUt7nTL4xj6Lft8` is **READY**, identifies
  that merge SHA and owns `nexora1-ai.vercel.app`.
- Render is configured **After CI Checks Pass**. Main run `33336279319` completed
  **successfully** at this checkpoint, including backend, commercial operations, all five
  frontend shards and the governed Lead-to-RFQ gate. The last Render identity probe reports
  `e6aa2ddc948928f2e5887b9af3db79464e8e2a01`. Matching-host release certification is pending,
  not a demonstrated failed deployment. No manual deployment bypass was used.
- The existing Platform Owner session was successfully claimed and used. Through the normal
  production wizard, provisioned an **internal, non-billable QA tenant**:
  tenant **4**, business unit **8**, slug **`pilot-certification-20260830`**, name
  **Nexora Pilot Certification 20260830**. Provisioning correlation
  `3c2e4018cc9545ea9a422db8e78e676f`: eight steps succeeded and the invitation step was
  deliberately skipped. The new administrator uses a password, not an email invitation.
  No real customer/supplier contacts or financial postings were created.
- Enabled the nine scoped capabilities through the Modules screen: RFQs, Quotes, Orders,
  Procurement, Inventory, OCR, Email intake, Supplier search and Exports. Existing tenants
  were not modified; no mailbox was connected and no outbound email was sent.
- The new tenant remains **Provisioning**, under the production activation profile. Blocking
  controls include legal identity, plan, billing recipient, rate card, approved terms, typed
  hard limits, data residency/isolation with external evidence, and privileged MFA policy
  certification. No fabricated attestation or DEMO/LOCAL_TEST downgrade was applied.
- At **21:32:52 UTC** and **21:34:45 UTC**, Render logs confirmed the new administrator's
  login was denied because business unit **8** is **Provisioning**. The page instead said
  **Invalid credentials**. This is a confirmed response-to-UI defect, not proof of a bad
  password or a working tenant journey.

### Repairs from that live continuation

- Login now maps canonical problem types and HTTP statuses to actionable, non-sensitive
  messages: inactive workspace, restricted access, wrong credentials, throttling, unresolved
  tenant status and transport/service failures. Failed login still establishes no session.
- Provisioning completion reads the server's activation method, not whether a generated
  password happens to be present in the current dialog. Supplied-password and reopened
  executions no longer invent an invitation. Unknown methods give a safe next step. Step
  count is server-driven rather than hard-coded to eight.
- Verification: **75 tests passed across five files**, focused ESLint passed, production build
  and bundle budget passed. Four provisioning regressions failed before the repair and passed
  after it. The original authentication/navigation contracts remain covered.
- The actual changed React components were viewed in an isolated loopback-only synthetic
  preview. Desktop sign-in displayed the corrected activation message and an enabled retry
  button; desktop provisioning displayed the password-specific instruction and nine-step count.
  A 390px iframe viewport check showed readable provisioning copy and the sign-in layout.
  This was a component visual check, not authenticated production acceptance or a full mobile
  sign-in scenario. Temporary preview files were removed; no preview code ships.
- Impeccable's clarification checks kept the change to truthful state/recovery messaging;
  both changed UI files passed its detector. React review found no new effects, auth persistence,
  layout changes, or broadened access. Existing design-sidecar freshness warnings were not
  repaired in this scope.

**Next dependency:** complete and evidence the new tenant's production activation prerequisites,
then provision three distinct pilot personas without external invitations and run their deployed
access checks. The authenticated fresh-tenant commercial journey and isolated restore rehearsal
remain unexecuted. Local message fixes do not close those gates.

**Independent-pilot approval remains pending the unverified gates above.** Runtime checks alone,
the new read-only lane, a build, or an old consultant report cannot grant full approval.
