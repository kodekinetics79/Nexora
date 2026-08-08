# Platform Admin certification ledger — 2026-08-08

## Current verdict

**NOT READY.** This ledger is the authoritative integration record. A status of VERIFIED means the
implemented scope has executable evidence; it does not promote adjacent PARTIAL or MISSING scope.
No production deployment or remote push is authorized by this record.

| Readiness decision | Verdict | Blocking reason |
| --- | --- | --- |
| Product pilot readiness | NOT READY | MFA and backup/restore evidence remain P0; several pilot P1 journeys are incomplete. |
| Paid-pilot billing readiness | NOT READY | Rate-card and finalization authority are repaired, but corrections/credits, invoice/tax identity, frozen source evidence, collections and reconciliation certification remain incomplete. |
| Security readiness | NOT READY | Revocable sessions and last-Owner safety are implemented; MFA, SSO/SCIM, network policy and privileged-access certification remain open. |
| Compliance-audit readiness | NOT READY | Immutable legal holds exist, but all deletion engines and retention/export evidence have not yet passed independent end-to-end certification. |
| Production readiness | NOT READY | Pilot gates plus regional deployment, backup/restore drills, observability/recovery and deployment governance are incomplete or externally blocked. |

## Complete capability matrix

| Domain / capability | Status | Evidence and next gate |
| --- | --- | --- |
| 1. Tenant registry and hierarchy | PARTIAL | Durable tenant/workspace records and lifecycle states exist. Parent/child enterprise-account hierarchy and governed orphan-provisioning recovery remain P1. |
| 1. Provisioning and activation | PARTIAL | Idempotent durable execution, worker steps, password activation and transactional single-use invitations. Real stack proved 8/8 worker steps, but visible Chrome did not redeem an invitation and sign in as the tenant administrator; invitation-link handover is not surfaced in the async UI. |
| 1. Suspend/archive/restore | VERIFIED | State graph is persisted and audited; restore atomically cancels unclaimed or stale deletion and current purge claims block reanimation. |
| 1. Purge execution fencing | VERIFIED | Attempt token plus offboarding-row lock fences late executors; outcome commits atomically with deletion. Real PostgreSQL covers purge-wins and stale-executor-after-restore. |
| 2. Plans and rate cards | PARTIAL | Active/effective rate cards and tenant pins are server checked at assignment and calculation. Immutable effective-dated plan versions remain P1. |
| 2. Billing authority split | VERIFIED | BillingAdmin performs ordinary calculation; Owner is required for explicit price override/finalization, and persisted maker/checker identity prevents the calculating Owner from finalizing the same Draft. |
| 2. Commercial approvals | MISSING-P1 | Nonstandard/internal terms lack independent second-actor approval, immutable approved snapshot/hash and expiry. |
| 3. Typed entitlements | PARTIAL | Seats, documents, concurrent jobs and WFQ weight are enforced. Arbitrary feature JSON is not a typed authorization catalog. |
| 3. Usage limits | VERIFIED | Existing supported hard limits are server authoritative and have negative tests; coverage does not imply every product module is typed. |
| 4. Event metering | PARTIAL | Event usage and near-real-time worker aggregation exist for supported meters. Page/storage coverage and reconciliation coverage remain incomplete. |
| 4. AI usage/cost metering | PARTIAL | Token/cost occurrences exist, but overview silently excludes unpriced/non-USD activity; expose coverage and currency breakdown. |
| 5. Statements and proration | VERIFIED | Draft/final statement engine, settle lag, idempotency, proration and immutable finalization are tested on PostgreSQL. |
| 5. Invoices, tax, credits and AR | MISSING-P0 | No complete operational invoice identity/tax/credit-correction/collections workflow or frozen billable-source evidence. |
| 5. Reconciliation and margin | PARTIAL | Cost and statement primitives exist; billable-meter coverage, revenue reconciliation and ERP/accounting export boundary are not pilot-certified. |
| 6. Platform authentication boundary | VERIFIED | Dedicated scheme/audience/signing boundary; cross-scope tokens fail closed. |
| 6. Revocable sessions | VERIFIED | Persisted JTI/generation ledger, request-time live validation, mutation revoke-all fence and server-side current-session logout. Owner inventory/revoke-one UX remains P1. |
| 6. Last-Owner invariant | VERIFIED | Advisory transaction lock and authoritative post-lock check; deterministic PostgreSQL concurrent demotion/deactivation tests. |
| 6. MFA | MISSING-P0 | No enrolled and enforced second factor for privileged platform operators. |
| 6. SSO, SCIM and network policy | MISSING-P1 | Enterprise federation/provisioning and IP/network allowlisting are absent; production contracts must not claim them. |
| 7. Audit disclosure and integrity | VERIFIED | Legacy and explorer reads apply action-specific disclosure; unknown actions fail closed; writer-policy drift tests cover high-risk mutations. |
| 7. Legal holds | VERIFIED | Owner placement/release, immutable evidence, offboarding purge/erasure gates and evidence-retention deletion share a PostgreSQL fence; hold-vs-delete race and visible denial/release pass. |
| 7. Export | PARTIAL | Typed confirmation, JSON download and SHA-256 receipt are visibly verified. Large exports remain synchronous/in-memory instead of durable encrypted jobs. |
| 7. Erasure and retention | PARTIAL | Governed tenant erasure/purge and evidence-retention hold fencing exist; durable large-job orchestration and broader retention evidence remain P1. |
| 8. Security posture and incidents | PARTIAL | Login throttling, audit, queue visibility and selected hardening exist. Incident command controls/evidence packs and network enforcement remain incomplete. |
| 8. Compliance evidence | PARTIAL | PostgreSQL authorization/immutability tests and retained browser artifacts exist; control ownership, periodic evidence and backup/restore proof are incomplete. |
| 9. Support content | VERIFIED | Ticket content is TenantAdmin-only; lower roles receive safe counts without prose/PII, and UI gates restricted routes. |
| 9. Impersonation | PARTIAL | Short-lived read-only tenant tokens, persisted JTI and revocation exist; session content now requires Impersonate authority. Visible expiry/revoke/multi-tab journey remains P1. |
| 9. Customer-success operations | PARTIAL | Tenant overview, lifecycle, support and audit timelines exist; governed orphan recovery and broader health playbooks remain P1. |
| 10. Integrations registry | MISSING-P1 | Email settings exist, but no provider-neutral tenant integration inventory, secret reference, health, disable/revoke and audit surface. |
| 10. Queue/DLQ operations | BACKEND-ONLY | Queue/job reads exist; governed audited idempotent dead-letter re-drive is missing P1. |
| 10. Monitoring | PARTIAL | Health and queue signals exist; total-cost coverage and full alert/incident controls are incomplete. |
| 10. Backup and recovery | MISSING-P0 | No verified restorable tenant backup, RPO/RTO or restore-drill evidence in the control plane. |
| 11. Packaging and rollout | PARTIAL | Plans/features are manageable, but typed module enforcement, immutable versions and rollout governance are incomplete. |
| 11. Deployment governance | EXTERNALLY-BLOCKED | No production deployment was authorized. Regional topology and release evidence must be verified in the approved Vercel/Render/Neon deployment. |
| 12. AI provider and data use | VERIFIED | Platform Owner is the sole policy/provider allow-list mutation authority; tenant trust-center routes are read-only and enforcement remains fail closed. |
| 12. AI token/cost governance | PARTIAL | Provider/model/token limits persist; separated privacy/FinOps approval and complete currency/unpriced reporting remain P1. |
| Visible real-user certification | PARTIAL | Installed visible Chrome passed 5/5: login/ten tabs, provisioning/eight worker steps, lifecycle/export, legal-hold denial/release, and Owner AI policy/provider authorize/revoke. Tenant-admin activation/sign-in, consume-to-invoice-to-collection, support, plan change and an eligible purge remain unexecuted; no mocks count as final evidence. |

## Integrated repairs in this increment

- Added revocable platform-session persistence and request-time validation; role, password and
  account-state changes revoke all sessions and rotate a generation fence.
- Serialized last-active-Owner mutations and made AI policy mutation Owner-only on the platform surface.
- Enforced action-specific disclosure on the legacy audit endpoint and corrected high-risk action mappings.
- Enforced rate-card assignment/pin/effectivity and separated ordinary calculation, Owner price override and distinct-actor Owner finalization.
- Added immutable tenant legal holds, purge/erasure gates, attempt-token fencing and durable purge outcome evidence.
- Added typed-confirmation export UI and real download/receipt browser assertions.
- Restricted invitation and impersonation-session content to the matching privileged policies.
- Added server-side current-session logout and a frontend logout call before local session removal.
- Made Platform Owner the sole full AI-policy/provider authorization authority; provider grant/revoke, tenant governance evidence and Platform Audit now share one transaction, while tenant trust-center surfaces are read-only.
- Granted the identity execution role only the `Users.LastLogin` column required after successful tenant authentication; the real-stack simulator no longer emits PostgreSQL 42501 activity-stamp failures.

## Current verification record

- Backend build: passed, 0 errors (pre-existing package/nullability/analyzer warnings remain).
- Full backend portable lane: **2,745/2,745 passed**, 0 skipped.
- Full PostgreSQL lane: **453/453 passed**, 0 skipped.
- Billing authority lane: 123/123 portable and 1/1 PostgreSQL passed independently.
- Last-Owner PostgreSQL concurrency: 2/2 passed independently; combined test cleanup was repaired.
- Lifecycle reanimation/fencing PostgreSQL focus: 4/4 passed, including a late stale executor fenced after restore.
- Evidence-retention legal-hold focus: 1/1 portable and 24/24 PostgreSQL passed, including hold-vs-in-flight-delete fencing.
- Data-bearing maker/checker migration rehearsal: 1/1 PostgreSQL passed; populated legacy Draft backfilled to `system:legacy` and remained governably finalizable.
- EF pending-model-change check: passed; no drift after migration `20260808134402`.
- Frontend lint and production build: passed, including bundle budget; existing large-chunk warning remains.
- Real-stack platform simulation: **63/63 probes passed**, 0 defects.
- Installed visible Google Chrome against the rebuilt/migrated real stack: **5/5 passed in 1.1m**.
- Real session-revocation probe: authenticated GET 200, logout 204, replayed token GET 401.
- Full frontend Vitest remains blocked by the known open-handle hang (no results); broad fixture Playwright remains
  blocked by its missing fixture-only `/api/User/me/permissions` route. These are not reclassified as production passes.

## Certification boundary

Final certification uses real PostgreSQL, backend, workers and installed visible Google Chrome. Fixture APIs,
route interception, hard-coded business results and direct database mutation do not count as final journey
evidence. The seven-day retention floor is not bypassed: destructive-purge certification requires a pre-aged,
isolated synthetic tenant or an authorized controllable clock. A passing browser shell test is not evidence for
billing, authorization, retention, worker recovery or destructive concurrency unless that journey is exercised.
