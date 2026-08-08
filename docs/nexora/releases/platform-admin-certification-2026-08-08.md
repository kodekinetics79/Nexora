# Platform Admin certification ledger — 2026-08-08

## Current verdict

**NOT READY.** This ledger is the authoritative integration record. A status of VERIFIED means the
implemented scope has executable evidence; it does not promote adjacent PARTIAL or MISSING scope.
No production deployment or remote push is authorized by this record.

| Readiness decision | Verdict | Blocking reason |
| --- | --- | --- |
| Product pilot readiness | NOT READY | Privileged MFA and several full-stack journeys are repaired, but activation policy, typed feature enforcement and verified backup/restore remain P0. |
| Paid-pilot billing readiness | NOT READY | Operational invoices, credits and collections now exist, but event-level metering, billable signal coverage, legal tax rules and an ERP/accounting outbox remain P0. |
| Security readiness | NOT READY | Revocable MFA-bound sessions and last-Owner safety are implemented; SSO/SCIM, network policy and privileged-access recovery remain open. |
| Compliance-audit readiness | NOT READY | Legal holds and governed deletion are strong, but historical audit PII remediation, export completeness and backup non-resurrection evidence remain open. |
| Production readiness | NOT READY | Pilot gates plus regional deployment, backup/restore drills, observability/recovery and deployment governance are incomplete or externally blocked. |

## Complete capability matrix

| Domain / capability | Status | Evidence and next gate |
| --- | --- | --- |
| 1. Tenant registry and hierarchy | PARTIAL | Durable tenant/workspace records and lifecycle states exist. Parent/child enterprise-account hierarchy and governed orphan-provisioning recovery remain P1. |
| 1. Provisioning and activation | PARTIAL | Idempotent durable execution, partial draft persistence and 8/8 worker steps are visibly proven. Provisioning still activates without one authoritative cross-domain activation gate; invitation redemption and tenant-admin sign-in remain unexecuted in Chrome. |
| 1. Suspend/archive/restore | VERIFIED | State graph is persisted and audited; restore atomically cancels unclaimed or stale deletion and current purge claims block reanimation. |
| 1. Purge execution fencing | VERIFIED | Attempt token plus offboarding-row lock fences late executors; outcome commits atomically with deletion. Real PostgreSQL covers purge-wins and stale-executor-after-restore. |
| 2. Plans and rate cards | PARTIAL | Active/effective rate cards and tenant pins are server checked at assignment and calculation. Immutable effective-dated plan versions remain P1. |
| 2. Billing authority split | VERIFIED | BillingAdmin performs ordinary calculation; Owner is required for explicit price override/finalization, and persisted maker/checker identity prevents the calculating Owner from finalizing the same Draft. |
| 2. Commercial approvals | MISSING-P1 | Nonstandard/internal terms lack independent second-actor approval, immutable approved snapshot/hash and expiry. |
| 3. Typed entitlements | PARTIAL | A closed 16-key typed catalogue and operator UI replace arbitrary JSON. Seats, documents, concurrent jobs and WFQ weight are enforced, but 0/16 typed feature keys are yet called at production API/domain/worker boundaries. |
| 3. Usage limits | VERIFIED | Existing supported hard limits are server authoritative and have negative tests; coverage does not imply every product module is typed. |
| 4. Event metering | MISSING-P0 | Statements aggregate operational tables on demand; there is no canonical append-only usage event ledger with near-real-time aggregation and source reconciliation. Page/OCR/storage signals are not billing-safe. |
| 4. AI usage/cost metering | PARTIAL | Token/cost occurrences exist, but overview silently excludes unpriced/non-USD activity; expose coverage and currency breakdown. |
| 5. Statements and proration | VERIFIED | Draft/final statement engine, settle lag, idempotency, proration and immutable finalization are tested on PostgreSQL. |
| 5. Invoices, tax, credits and AR | PARTIAL | Draft/final invoices, frozen evidence SHA-256, tax calculation, maker/checker, idempotent credits/payments and outstanding balance are implemented. Legal jurisdiction rules, delivery, reversals/refunds/write-off/dunning and stable actor-id evidence remain incomplete. |
| 5. Reconciliation and margin | PARTIAL | Cost and statement primitives exist; billable-meter coverage, revenue reconciliation and ERP/accounting export boundary are not pilot-certified. |
| 6. Platform authentication boundary | VERIFIED | Dedicated scheme/audience/signing boundary; cross-scope tokens fail closed. |
| 6. Revocable sessions | VERIFIED | Persisted JTI/generation ledger, request-time live validation, mutation revoke-all fence and server-side current-session logout. Owner inventory/revoke-one UX remains P1. |
| 6. Last-Owner invariant | VERIFIED | Advisory transaction lock and authoritative post-lock check; deterministic PostgreSQL concurrent demotion/deactivation tests. |
| 6. MFA | VERIFIED | Server-authoritative TOTP enrollment, password-to-MFA challenge, one-time recovery codes, replay fencing, five-attempt lockout and MFA-bound revocable sessions. Concurrent challenge/guess PostgreSQL races are covered. Governed operator reset/re-enrollment remains P1. |
| 6. SSO, SCIM and network policy | MISSING-P1 | Enterprise federation/provisioning and IP/network allowlisting are absent; production contracts must not claim them. |
| 7. Audit disclosure and integrity | VERIFIED | Legacy and explorer reads apply action-specific disclosure; unknown actions fail closed; writer-policy drift tests cover high-risk mutations. |
| 7. Legal holds | VERIFIED | Owner placement/release, immutable evidence, offboarding purge/erasure gates and evidence-retention deletion share a PostgreSQL fence; hold-vs-delete race and visible denial/release pass. |
| 7. Export | PARTIAL | Typed confirmation, tenant isolation, RLS fail-closed behavior, JSON download and SHA-256 receipt are verified. Current export coverage omits several finance/procurement records and large exports remain synchronous/in-memory. |
| 7. Erasure and retention | PARTIAL | Governed tenant erasure/purge and evidence-retention hold fencing exist; durable large-job orchestration and broader retention evidence remain P1. |
| 8. Security posture and incidents | PARTIAL | Login throttling, audit, queue visibility and selected hardening exist. Incident command controls/evidence packs and network enforcement remain incomplete. |
| 8. Compliance evidence | PARTIAL | PostgreSQL authorization/immutability tests and retained browser artifacts exist; control ownership, periodic evidence and backup/restore proof are incomplete. |
| 9. Support content | VERIFIED | Ticket content is TenantAdmin-only; lower roles receive safe counts without prose/PII, and UI gates restricted routes. |
| 9. Impersonation | PARTIAL | Short-lived read-only tenant tokens, persisted JTI and revocation exist; session content now requires Impersonate authority. Visible expiry/revoke/multi-tab journey remains P1. |
| 9. Customer-success operations | PARTIAL | Tenant overview, lifecycle, support and audit timelines exist; governed orphan recovery and broader health playbooks remain P1. |
| 10. Integrations registry | MISSING-P1 | Email settings exist, but no provider-neutral tenant integration inventory, secret reference, health, disable/revoke and audit surface. |
| 10. Queue/DLQ operations | BACKEND-ONLY | Queue/job reads exist; governed audited idempotent dead-letter re-drive is missing P1. |
| 10. Monitoring | PARTIAL | Health and extraction queue signals exist; raw document/error diagnostics are now restricted. Unified fleet queues, truthful dependency failure classification and full alert/incident controls remain incomplete. |
| 10. Backup and recovery | MISSING-P0 | No verified restorable tenant backup, RPO/RTO or restore-drill evidence in the control plane. |
| 11. Packaging and rollout | PARTIAL | Plans/features are manageable, but typed module enforcement, immutable versions and rollout governance are incomplete. |
| 11. Deployment governance | EXTERNALLY-BLOCKED | No production deployment was authorized. Regional topology and release evidence must be verified in the approved Vercel/Render/Neon deployment. |
| 12. AI provider and data use | VERIFIED | Platform Owner is the sole policy/provider allow-list mutation authority; tenant trust-center routes are read-only and enforcement remains fail closed. |
| 12. AI token/cost governance | PARTIAL | Provider/model/token limits persist; separated privacy/FinOps approval and complete currency/unpriced reporting remain P1. |
| Visible real-user certification | PARTIAL | Installed visible Chrome proves MFA login, ten tabs, first-field draft save/resume/edit/reload, durable billable provisioning, plan change, statement compute, suspend/resume, lifecycle/export, legal-hold denial/release and AI governance. Tenant-admin consume, independent invoice finalization/collection, support mutation and eligible purge remain incomplete; no mocks count as final evidence. |

## Integrated repairs in this increment

- Added privileged platform MFA with TOTP enrollment, recovery codes, replay/attempt fencing,
  concurrent challenge serialization and MFA-bound revocable sessions; added the complete Owner
  enrollment/bootstrap path to the real-stack runner without printing the seed.
- Replaced arbitrary plan-feature editing with a closed 16-key typed catalogue and added truthful
  tenant entitlement/data-boundary projections that fail closed on unresolved server evidence.
- Added operational subscription invoices with frozen source evidence/hash, line/header
  reconciliation, tax snapshots, maker/checker finalization, idempotent credits/payments,
  per-invoice concurrency fencing, PostgreSQL immutability triggers and AR balances.
- Added the owner-governed tenant data-asset registry and lifecycle-aware activation decision;
  this is evidence inventory, not a claim of backup/restore certification.
- Repaired first-field provisioning drafts so MVC final-submit validation no longer prevents a
  partial save; Chrome now proves save, resume, edit and reload.
- Made tenant exports fail closed when the required PostgreSQL RLS role is absent, removed new
  support/provisioning PII from immutable audit metadata, and made impersonation revocation plus
  its audit atomic.
- Restricted pipeline document names and raw exception messages to the narrower support/Owner
  authority and stopped observability from misreporting arbitrary database faults as a missing table.
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
- Full backend portable lane: **2,762/2,762 passed**, 0 skipped.
- Full PostgreSQL lane: **460/460 passed**, 0 skipped.
- Wave 5 MFA/invoice/data-asset PostgreSQL focus: **7/7 passed**, including concurrent
  finalization, credit replay, payment replay, MFA challenge/guess races and migration application.
- Wave 5 frontend focused Vitest: **14/14 passed** across six files.
- Billing authority lane: 123/123 portable and 1/1 PostgreSQL passed independently.
- Last-Owner PostgreSQL concurrency: 2/2 passed independently; combined test cleanup was repaired.
- Lifecycle reanimation/fencing PostgreSQL focus: 4/4 passed, including a late stale executor fenced after restore.
- Evidence-retention legal-hold focus: 1/1 portable and 24/24 PostgreSQL passed, including hold-vs-in-flight-delete fencing.
- Data-bearing maker/checker migration rehearsal: 1/1 PostgreSQL passed; populated legacy Draft backfilled to `system:legacy` and remained governably finalizable.
- EF pending-model-change check: passed; no drift after migration
  `20260808163605_Wave5PlatformOperatingControls`.
- Frontend lint and production build: passed, including bundle budget; existing large-chunk warning remains.
- Real-stack platform simulation: **63/63 probes passed**, 0 defects.
- Installed visible Google Chrome against the rebuilt/migrated real stack: **8/8 passed in 3.8m**.
  It used real password+TOTP login, no request interception or fixture API, and covered ten tabs,
  first-field draft persistence, durable provisioning, lifecycle/export, entitlements, data gate,
  legal-hold denial/release and Owner AI governance.
- Additional watched Chrome journey on the same real stack: draft resume/edit -> 8/8 provisioning
  -> Growth-to-Enterprise plan change -> August statement compute (`$6,192.77`) -> maker/checker
  denial -> suspend/resume -> governed export. PostgreSQL recorded a 112-row, 54,556-byte export
  receipt with a 64-character SHA-256; the browser download-event handoff itself timed out.
- Real session-revocation probe: authenticated GET 200, logout 204, replayed token GET 401.
- Full frontend Vitest remains blocked by the known open-handle hang; focused Wave 5 tests pass. Broad fixture Playwright remains
  blocked by its missing fixture-only `/api/User/me/permissions` route. These are not reclassified as production passes.

## Certification boundary

Final certification uses real PostgreSQL, backend, workers and installed visible Google Chrome. Fixture APIs,
route interception, hard-coded business results and direct database mutation do not count as final journey
evidence. The seven-day retention floor is not bypassed: destructive-purge certification requires a pre-aged,
isolated synthetic tenant or an authorized controllable clock. A passing browser shell test is not evidence for
billing, authorization, retention, worker recovery or destructive concurrency unless that journey is exercised.
