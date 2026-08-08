# Platform Admin certification ledger — 2026-08-08

## Current verdict

**NOT READY.** This ledger is the authoritative integration record. A status of VERIFIED means the
implemented scope has executable evidence; it does not promote adjacent PARTIAL or MISSING scope.
No production deployment or remote push is authorized by this record.

| Readiness decision | Verdict | Blocking reason |
| --- | --- | --- |
| Product pilot readiness | NOT READY | One activation authority and all typed feature boundaries now fail closed, but the real tenant cannot truthfully clear external tenant-MFA, integration and recovery evidence in the local certification environment. |
| Paid-pilot billing readiness | NOT READY | Statements now prefer canonical usage events and freeze their manifest lineage, but legacy fallback, certified page/OCR/storage rating and the external ERP transport remain open. |
| Security readiness | NOT READY | Revocable MFA-bound sessions and last-Owner safety are implemented; SSO/SCIM, network policy and privileged-access recovery remain open. |
| Compliance-audit readiness | NOT READY | Legal holds and governed deletion are strong, but historical audit PII remediation, export completeness and real provider backup/non-resurrection evidence remain open. |
| Production readiness | NOT READY | Pilot gates plus regional deployment, backup/restore drills, observability/recovery and deployment governance are incomplete or externally blocked. |

## Complete capability matrix

| Domain / capability | Status | Evidence and next gate |
| --- | --- | --- |
| 1. Tenant registry and hierarchy | PARTIAL | Durable tenant/workspace records and lifecycle states exist. Parent/child enterprise-account hierarchy and governed orphan-provisioning recovery remain P1. |
| 1. Provisioning and activation | PARTIAL | Idempotent durable execution, partial draft persistence and 8/8 worker steps are visibly proven. Provisioning now remains Provisioning and only the versioned Owner+MFA activation authority may transition Active. The real Chrome tenant correctly remains blocked because external tenant-security/integration/data evidence was not fabricated. |
| 1. Suspend/archive/restore | VERIFIED | State graph is persisted and audited; restore atomically cancels unclaimed or stale deletion and current purge claims block reanimation. |
| 1. Purge execution fencing | VERIFIED | Attempt token plus offboarding-row lock fences late executors; outcome commits atomically with deletion. Real PostgreSQL covers purge-wins and stale-executor-after-restore. |
| 2. Plans and rate cards | PARTIAL | Active/effective rate cards and tenant pins are server checked at assignment and calculation. Immutable effective-dated plan versions remain P1. |
| 2. Billing authority split | VERIFIED | BillingAdmin performs ordinary calculation; Owner is required for explicit price override/finalization, and persisted maker/checker identity prevents the calculating Owner from finalizing the same Draft. |
| 2. Commercial approvals | MISSING-P1 | Nonstandard/internal terms lack independent second-actor approval, immutable approved snapshot/hash and expiry. |
| 3. Typed entitlements | PARTIAL | All 16 closed keys have an authoritative server boundary: 11 implemented capabilities are enforced and five unimplemented capabilities are runtime-unavailable/default-denied even if plan JSON says true. Effective-dated entitlement versions, quantity thresholds and tenant overrides remain incomplete. |
| 3. Usage limits | VERIFIED | Existing supported hard limits are server authoritative and have negative tests; coverage does not imply every product module is typed. |
| 4. Event metering | PARTIAL | Immutable tenant-qualified usage events, minute aggregates, idempotency, late arrival, adjustment lineage and rate-card integrity are PostgreSQL-enforced. Successful extraction emits one document event transactionally. Statements prefer canonical events by meter, include adjustments, and freeze a deterministic event-manifest SHA-256 in source lineage; legacy fallback and page/OCR/storage certification remain open. |
| 4. AI usage/cost metering | PARTIAL | Token/cost occurrences exist, but overview silently excludes unpriced/non-USD activity; expose coverage and currency breakdown. |
| 5. Statements and proration | VERIFIED | Draft/final statement engine, settle lag, idempotency, proration and immutable finalization are tested on PostgreSQL. |
| 5. Invoices, tax, credits and AR | PARTIAL | Draft/final invoices, frozen evidence SHA-256, tax calculation, maker/checker, idempotent credits/payments and outstanding balance are implemented. Legal jurisdiction rules, delivery, reversals/refunds/write-off/dunning and stable actor-id evidence remain incomplete. |
| 5. Reconciliation and margin | PARTIAL | Invoice finalization transactionally creates an immutable accounting outbox record with lease/poison/re-drive/ack/reconciliation states. No hosted external ERP connector transport exists, and canonical usage-to-statement reconciliation plus broader cost attribution remain incomplete. |
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
| 10. Backup and recovery | PARTIAL | Deletion certification now requires post-purge destruction evidence for every registered boundary, residency evidence, and a primary-PostgreSQL restore drill linked to a previously observed backup/recovery point, in-window tombstone reapplication, zero restored customer rows and achieved RPO/RTO. No real storage/provider connector, provider destruction receipt or real provider restore drill was available for certification. |
| 11. Packaging and rollout | PARTIAL | Plans/features are manageable, but typed module enforcement, immutable versions and rollout governance are incomplete. |
| 11. Deployment governance | EXTERNALLY-BLOCKED | No production deployment was authorized. Regional topology and release evidence must be verified in the approved Vercel/Render/Neon deployment. |
| 12. AI provider and data use | VERIFIED | Platform Owner is the sole policy/provider allow-list mutation authority; tenant trust-center routes are read-only and enforcement remains fail closed. |
| 12. AI token/cost governance | PARTIAL | Provider/model/token limits persist; separated privacy/FinOps approval and complete currency/unpriced reporting remain P1. |
| Visible real-user certification | PARTIAL | Installed visible Chrome proves MFA login, ten tabs, first-field draft save/resume/edit/reload, durable provisioning that remains blocked, export, entitlement projection, activation/recovery/deletion decisions, legal-hold denial/release and AI governance. A previous Wave 5 stack proved active-tenant lifecycle, but the current Wave 6 tenant is correctly not activated without external evidence. Tenant consume, billing/outbox delivery and eligible purge remain incomplete. |

## Integrated repairs in this increment

- Made extraction claims poison-pill safe: invalid intake candidates are classified and
  quarantined exactly once while later valid work continues under PostgreSQL `SKIP LOCKED`;
  canonical occurrence transition and queue creation remain atomic and the database guard stays active.
- Added the single versioned tenant-activation decision and transition. Provisioning no longer
  self-activates; resume re-evaluates policy and real legal-hold/offboarding/commercial/access/data
  state is used instead of optimistic constants.
- Enforced all 16 typed entitlement keys at real server boundaries or an explicit runtime-
  unavailable default-deny boundary; extraction checks OCR before native providers are invoked.
- Added tenant-qualified immutable usage events and minute aggregates with idempotency, late-arrival
  and adjustment lineage, rated-field/effective-card PostgreSQL guards, and one durable document event
  emitted inside successful extraction persistence/completion.
- Added the invoice-finalization accounting outbox with frozen payload/hash, concurrent claims,
  leases, poison state, governed re-drive and acknowledgement/reconciliation evidence.
- Added recovery evidence and deletion-certificate ledgers with composite tenant/asset foreign keys,
  forced RLS, least-privilege fleet policy and append-only triggers; the purge map now explicitly
  preserves all five Wave 6 operator-evidence tables.
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
- Full backend portable lane after final recovery/billing repairs: **2,788/2,788 passed**, 0 failed/skipped.
- Full PostgreSQL lane: **466/466 passed**, 0 failed/skipped.
- Wave 6 integrated PostgreSQL defect-fix focus: **108/108 passed** across purge/lifecycle,
  red-team control-plane, entitlement claim and authenticated procurement HTTP.
- Wave 6 queue/DLQ PostgreSQL and observability lanes: **51/51 passed**.
- Wave 6 usage/outbox focused lanes: **8/8 passed**, including real PostgreSQL duplicate-event
  concurrency, valid rating/adjustment and accounting claims.
- Wave 5 MFA/invoice/data-asset PostgreSQL focus: **7/7 passed**, including concurrent
  finalization, credit replay, payment replay, MFA challenge/guess races and migration application.
- Wave 5 frontend focused Vitest: **14/14 passed** across six files.
- Billing authority lane: 123/123 portable and 1/1 PostgreSQL passed independently.
- Last-Owner PostgreSQL concurrency: 2/2 passed independently; combined test cleanup was repaired.
- Lifecycle reanimation/fencing PostgreSQL focus: 4/4 passed, including a late stale executor fenced after restore.
- Evidence-retention legal-hold focus: 1/1 portable and 24/24 PostgreSQL passed, including hold-vs-in-flight-delete fencing.
- Data-bearing maker/checker migration rehearsal: 1/1 PostgreSQL passed; populated legacy Draft backfilled to `system:legacy` and remained governably finalizable.
- EF pending-model-change check: passed; no drift after migrations
  `20260808190127_Wave6PlatformFoundations` and `20260808191301_Wave6UsageRatingIntegrity`.
- Frontend lint and production build: passed, including bundle budget; existing large-chunk warning remains.
- Real-stack platform simulation: **63/63 probes passed**, 0 defects.
- Installed visible Google Chrome current Wave 6 run: **8/8 passed in 4.1 minutes** after the
  ambiguous deletion-certificate locator and pre-activation legal-hold expectation were corrected.
  The run used real password+TOTP, the rebuilt API/workers/PostgreSQL, persisted synthetic tenant
  data, no request interception and no fixture API. It proves only the eight named journeys; the
  tenant correctly remained Provisioning because external activation evidence was not fabricated.
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
