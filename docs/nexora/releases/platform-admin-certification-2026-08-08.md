# Platform Admin certification ledger — 2026-08-08

## Current verdict

**NOT READY.** This ledger is the authoritative integration record. A status of VERIFIED means the
implemented scope has executable evidence; it does not promote adjacent PARTIAL or MISSING scope.
No production deployment or remote push is authorized by this record.

| Readiness decision | Verdict | Blocking reason |
| --- | --- | --- |
| Product pilot readiness | NOT READY | One activation authority and all typed feature boundaries now fail closed, but the real tenant cannot truthfully clear external tenant-MFA, integration and recovery evidence in the local certification environment. |
| Paid-pilot billing readiness | NOT READY | Governed tax rules, revenue actions and a durable accounting dispatcher now exist, but production jurisdiction evidence, an ERP receipt contract and billing-safe page/OCR/storage signals are not configured/certified. |
| Security readiness | NOT READY | Revocable MFA sessions, second-Owner recovery, multi-tab propagation and CIDR enforcement are implemented; approved production CIDRs/trusted proxy configuration and enterprise SSO/SCIM remain external gates. |
| Compliance-audit readiness | NOT READY | Legal holds, deletion and historical-audit minimization policy are strong, but export completeness and real provider backup/destruction/non-resurrection evidence remain open. |
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
| 4. Event metering | PARTIAL | Immutable tenant-qualified events, minute aggregates, idempotency, adjustments and rate-card integrity are PostgreSQL-enforced. Every production ingestion door is forced through the unified queue; document/page/OCR event IDs are deterministic and only measured PDF/image/TIFF counts emit page events. A backend-only closed-hour storage calculator uses verified immutable bytes and prorates purge, but lacks a durable scheduling cursor. Page/OCR/storage remain charging-blocked until orchestration and provider/version evidence are certified. |
| 4. AI usage/cost metering | PARTIAL | Token/cost occurrences exist, but overview silently excludes unpriced/non-USD activity; expose coverage and currency breakdown. |
| 5. Statements and proration | VERIFIED | Draft/final statement engine, settle lag, idempotency, proration and immutable finalization are tested on PostgreSQL. |
| 5. Invoices, tax, credits and AR | PARTIAL | Versioned jurisdiction rules, frozen tax evidence, stable maker/checker IDs, credits/payments, void/refund/reversal/write-off/dunning and reconciled append-only rollups are PostgreSQL-enforced. Production jurisdiction/nexus rules, invoice delivery and provider receipts remain external. |
| 5. Reconciliation and margin | PARTIAL | Finalized invoices and revenue actions transactionally create immutable accounting messages; the hosted dispatcher has leases, poison/re-drive, idempotent HTTPS delivery and bounded reconciliation receipts. A real ERP receipt contract and broader non-AI cost attribution are not configured. |
| 6. Platform authentication boundary | VERIFIED | Dedicated scheme/audience/signing boundary; cross-scope tokens fail closed. |
| 6. Revocable sessions | VERIFIED | Persisted JTI/generation ledger, request-time live validation, mutation revoke-all fence and server-side current-session logout. Owner inventory/revoke-one UX remains P1. |
| 6. Last-Owner invariant | VERIFIED | Advisory transaction lock and authoritative post-lock check; deterministic PostgreSQL concurrent demotion/deactivation tests. |
| 6. MFA | VERIFIED | Server-authoritative TOTP enrollment, recovery codes, replay/attempt fencing and MFA-bound sessions are covered, including concurrent races. A distinct active Owner can now revoke another operator's MFA credentials and sessions for governed recovery; self-reset is denied. |
| 6. SSO, SCIM and network policy | PARTIAL | Platform CIDR enforcement is implemented for IPv4/IPv6 after trusted-forwarder normalization and fails closed on invalid production configuration. Production CIDRs/proxy ranges and enterprise OIDC/SAML/SCIM contracts remain external. |
| 6. Multi-tab platform sessions | VERIFIED | Same-origin nonce handshakes share only the live session token through BroadcastChannel, never localStorage; logout propagates across tabs and captured-token replay is rejected by the server session ledger. |
| 7. Audit disclosure and integrity | VERIFIED | Legacy and explorer reads apply action-specific disclosure; unknown actions fail closed; writer-policy drift tests cover high-risk mutations. |
| 7. Legal holds | VERIFIED | Owner placement/release, immutable evidence, offboarding purge/erasure gates and evidence-retention deletion share a PostgreSQL fence; hold-vs-delete race and visible denial/release pass. |
| 7. Export | PARTIAL | Typed confirmation, tenant isolation, RLS fail-closed behavior, JSON download and SHA-256 receipt are verified. Current export coverage omits several finance/procurement records and large exports remain synchronous/in-memory. |
| 7. Erasure and retention | PARTIAL | Purge fail-closes without persisted erasure while commercial/legal/audit evidence survives. Historical immutable security occurrences follow RETAIN_RESTRICT_MINIMIZE_V1: raw operator email/IP is Owner-only rather than rewritten. Durable large-job orchestration and broader export coverage remain P1. |
| 8. Security posture and incidents | PARTIAL | Login throttling, audit, queue visibility and selected hardening exist. Incident command controls/evidence packs and network enforcement remain incomplete. |
| 8. Compliance evidence | PARTIAL | PostgreSQL authorization/immutability tests and retained browser artifacts exist; control ownership, periodic evidence and backup/restore proof are incomplete. |
| 9. Support content | VERIFIED | Ticket content is TenantAdmin-only; lower roles receive safe counts without prose/PII, and UI gates restricted routes. |
| 9. Impersonation | PARTIAL | Short-lived read-only tenant tokens, persisted JTI and revocation exist; session content now requires Impersonate authority. Visible expiry/revoke/multi-tab journey remains P1. |
| 9. Customer-success operations | PARTIAL | Tenant overview, lifecycle, support and audit timelines exist; governed orphan recovery and broader health playbooks remain P1. |
| 10. Integrations registry | MISSING-P1 | Email settings exist, but no provider-neutral tenant integration inventory, secret reference, health, disable/revoke and audit surface. |
| 10. Queue/DLQ operations | PARTIAL | Owner+MFA can governably re-drive extraction, supplier-RFQ and quote-delivery dead letters with resolved tenant/BU, reason, idempotency and audit; Platform Pipeline exposes the action with string-safe IDs. A no-mock live recovery mutation was not executed because no supported boundary manufactures a recoverable poison record. |
| 10. Monitoring | PARTIAL | Health and extraction queue signals exist; raw diagnostics are restricted. Production now refuses to register Prometheus without a scrape key, while OTLP or explicit disable remain valid. A real collector/alert route, unified fleet queues and incident controls remain external/incomplete. |
| 10. Backup and recovery | PARTIAL | Deletion certification now requires post-purge destruction evidence for every registered boundary, residency evidence, and a primary-PostgreSQL restore drill linked to a previously observed backup/recovery point, in-window tombstone reapplication, zero restored customer rows and achieved RPO/RTO. No real storage/provider connector, provider destruction receipt or real provider restore drill was available for certification. |
| 10. Production evidence storage and malware scanning | EXTERNALLY-BLOCKED | Client-supplied infrastructure: the client will provide versioned S3-compatible storage and a private ClamAV endpoint, or host equivalent resources in its AWS/Azure environment. This is accepted as outside the current Nexora code gate; deployment certification must still prove versioning, write/read/delete, clean/EICAR scanning, least privilege and recovery behavior against the supplied resources. |
| 11. Packaging and rollout | PARTIAL | Plans/features are manageable, but typed module enforcement, immutable versions and rollout governance are incomplete. |
| 11. Deployment governance | EXTERNALLY-BLOCKED | No production deployment was authorized. Regional topology and release evidence must be verified in the approved Vercel/Render/Neon deployment. |
| 12. AI provider and data use | VERIFIED | Platform Owner is the sole policy/provider allow-list mutation authority; tenant trust-center routes are read-only and enforcement remains fail closed. |
| 12. AI token/cost governance | PARTIAL | Provider/model/token limits persist; separated privacy/FinOps approval and complete currency/unpriced reporting remain P1. |
| Visible real-user certification | PARTIAL | Installed visible Chrome now passes 10/10 executable serial journeys, adding real multi-tab token handoff/logout/replay denial to MFA login, tabs, drafts, provisioning, plan change, two-Owner billing, export, entitlements, activation/recovery decisions, legal hold and AI governance. A complete Active consume-to-purge journey remains externally blocked; DLQ mutation was excluded rather than fabricated. |

## Integrated repairs in this increment

- Added approved, non-overlapping subscription-tax rules with stable maker/checker identities,
  frozen invoice jurisdiction evidence and PostgreSQL actor, interval, hash and immutability guards.
- Added append-only void, refund, payment-reversal, write-off and automated-dunning actions with
  correct collected-cash bounds, idempotency, independent approval and deferred invoice/child
  rollup reconciliation; every completed action creates a tenant-qualified accounting message.
- Added the hosted accounting dispatcher and explicit external connector contract with lease,
  retry, poison, re-drive, idempotency and acknowledged receipt reconciliation.
- Added centralized Owner+MFA DLQ recovery for extraction, supplier-RFQ and quote delivery plus
  the Platform Pipeline recovery UI; tenant/BU resolution is server-owned and payloads remain untouched.
- Added CIDR platform-route enforcement behind an explicit trusted-forwarder boundary, governed
  second-Owner MFA recovery and same-origin multi-tab session/logout propagation without localStorage.
- Made the tenant-bearing Prometheus scrape fail closed in Production unless an authentication key
  is configured; anonymous scrape remains a Development-only convenience.
- Forced every Production ingestion door through the governed unified queue, replaced random usage
  event IDs with deterministic identities, separated authoritative from assumed page counts, and
  added fail-closed closed-hour storage accrual from provider-verified immutable bytes and purge proof.

- Made extraction claims poison-pill safe: invalid intake candidates are classified and
  quarantined exactly once while later valid work continues under PostgreSQL `SKIP LOCKED`;
  canonical occurrence transition and queue creation remain atomic and the database guard stays active.
- Added the single versioned tenant-activation decision and transition. Provisioning no longer
  self-activates; resume re-evaluates policy and real legal-hold/offboarding/commercial/access/data
  state is used instead of optimistic constants.
- Closed a certification-found pre-activation access bypass. Provisioning tenants are now denied at
  tenant login, authenticated API middleware, background work gates and extraction claims; the real
  pre-fix API issued a token and the repaired Chrome/API path returns a typed 403 with no token.
- Replaced first-canonical-event billing switching with maker/checker source policies, immutable exact
  coverage segments, event-time rating results, server-authoritative allowance allocation and frozen
  readiness manifests. Statement finalization and invoice creation independently fail closed.
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
- Full backend portable lane after the final metering and observability repairs: **2,834/2,834 passed**, 0 failed/skipped.
- Full PostgreSQL lane after the final revenue, metering and lifecycle integration repairs:
  **483/483 passed**, 0 failed/skipped. TRX: `Backend/ERP_RFQ_Automation.Tests/TestResults/platform-admin-final-postgresql-certified.trx`.
- Final revenue-control PostgreSQL focus: **4/4 passed** on fresh and data-bearing PG16,
  covering RLS/grants, actor FKs, tax overlap/evidence, action transitions, refund/reversal
  bounds, rollup reconciliation and tenant-qualified outbox lineage.
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
- Billing cutover closure PostgreSQL suite: **8/8 passed**, including distinct-maker/checker successful
  canonical approval, exact persisted segments, UTC boundary reload, rating lineage, RLS and concurrency.
- Lifecycle erasure/readiness PostgreSQL suite: **14/14 passed**; portable lifecycle/readiness: **123/123 passed**.
- EF pending-model-change check: passed; no drift through migration
  `20260808234734_FinalPlatformRevenueControls`.
- Frontend lint and production build: passed, including bundle budget; existing large-chunk warning remains.
- Full frontend Vitest: **376/376 passed** across 37 files, 0 failed/skipped.
- Real-stack platform simulation: **63/63 probes passed**, 0 defects.
- Installed visible Google Chrome final closure run: **10/10 executable journeys passed in 6.6 minutes**, one worker,
  `channel: chrome`, `headless: false`, slow motion 150 ms, with durable HTML evidence at
  `Frontend/playwright-report/index.html`. It used real password+TOTP for two Owners, fresh migrated
  PostgreSQL 16, rebuilt API/workers/Vite, persisted synthetic tenants, no request interception and no
  fixture API. It proves multi-tab login/logout/replay denial and the named commercial/lifecycle journeys;
  the tenant correctly remained Provisioning because external activation evidence was not fabricated.
- The Platform DLQ recovery mutation was excluded rather than skipped or mocked: the fresh tenant is
  correctly Provisioning and no supported public boundary manufactures a recoverable poison record.
  Backend and UI recovery suites pass, but a live mutation still requires an authorized active isolated
  tenant and a genuine terminal recoverable ingestion failure.
- Additional watched Chrome journey on the same real stack: draft resume/edit -> 8/8 provisioning
  -> Growth-to-Enterprise plan change -> August statement compute (`$6,192.77`) -> maker/checker
  denial -> suspend/resume -> governed export. PostgreSQL recorded a 112-row, 54,556-byte export
  receipt with a 64-character SHA-256; the browser download-event handoff itself timed out.
- Real session-revocation probe: authenticated GET 200, logout 204, replayed token GET 401.
- Broad fixture Playwright remains blocked by its missing fixture-only `/api/User/me/permissions`
  route. This fixture-only failure is not reclassified as a production pass.

## Certification boundary

Final certification uses real PostgreSQL, backend, workers and installed visible Google Chrome. Fixture APIs,
route interception, hard-coded business results and direct database mutation do not count as final journey
evidence. The seven-day retention floor is not bypassed: destructive-purge certification requires a pre-aged,
isolated synthetic tenant or an authorized controllable clock. A passing browser shell test is not evidence for
billing, authorization, retention, worker recovery or destructive concurrency unless that journey is exercised.
