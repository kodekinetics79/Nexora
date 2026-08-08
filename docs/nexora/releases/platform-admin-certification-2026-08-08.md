# Platform Admin certification ledger — 2026-08-08

## Verdict

**NOT READY.** The repaired slice is verified, but pilot release remains blocked by platform IAM/session controls, legal hold, destructive-purge fencing, billing correction/evidence/segregation-of-duties, and incomplete visible coverage of the critical workflows.

## Capability and finding matrix

| Capability | Status | Evidence / remaining gate |
| --- | --- | --- |
| Platform/tenant authentication boundary | Verified | Separate platform scheme, audience, key requirements, and role policies. Preserve cross-scope negative tests. |
| Durable tenant provisioning and first-admin activation | Verified | Idempotent durable execution and transactional single-use invitation claim/revocation. |
| Audit explorer | Verified | Server paging/filtering, diff view, and policy-specific payload disclosure. |
| Platform IAM | Missing — P0 | No MFA/enterprise SSO/IP allowlist; normal 30-minute operator JWTs have no revocable session/JTI/version check. |
| Typed module entitlements | Partial — P1 | Seats, documents, concurrency and WFQ are enforced; arbitrary feature keys remain display/configuration data. |
| Restore/resume versus active purge | Repaired | Reanimation cancels only unclaimed pending deletion in the same transaction; an active claim blocks; purge rechecks Archived status during claim. |
| Stale/live purge fencing | Broken — P0 | A timestamp lease may be reclaimed while an earlier owner-connection sweep is still alive. Add attempt token, heartbeat, and fencing at destructive work and completion. |
| Legal hold | Missing — P0 | No immutable hold/release workflow or fail-closed purge/erasure gate. |
| Support content disclosure | Repaired | Ticket content is TenantAdmin-only; unauthorized summaries retain counts but omit rows; tenant timeline omits ticket entries; UI hides restricted support routes and labels withheld detail. |
| Billing/revenue completion | Partial — P0 | Statements/proration/finalization exist; corrections/credits, invoice identity/tax/AR, frozen meter evidence, and price-setter/finalizer separation remain open. |
| Nonstandard commercial approval | Missing — P1 | Authority check exists, but no independent second-actor approval, approved snapshot/hash, or expiry. |
| Queue recovery | Backend-only — P1 | Queue/jobs are observable, but governed audited DLQ re-drive is absent. |
| Visible browser certification | Partial | Real Chrome Owner login and ten top-level tabs passed; the requested critical mutation, denial, recovery, download, impersonation, and multi-tab journeys are not visibly certified. |

## Delivered repairs

- Serialized reanimation with purge claim and added a real PostgreSQL blocking-race test.
- Restricted direct and indirect customer support content to TenantAdmin authority while preserving safe aggregates.
- Corrected tenant Support navigation and restricted-detail messaging for lower roles.
- Added a dedicated non-fixture, headful Google Chrome Playwright project and real-backend smoke specification.
- Corrected lifecycle operator copy and an existing frontend non-printable-character lint violation.

## Verification record

- Backend portable: **2722 passed, 0 failed, 0 skipped**.
- Changed authorization/lifecycle focus: **44 passed, 0 failed, 0 skipped**.
- PostgreSQL full lane: **441 passed, 0 failed, 0 skipped**.
- PostgreSQL lifecycle/race focus: **10 passed, 0 failed, 0 skipped**.
- Frontend Vitest: **345 passed across 26 files**.
- Frontend lint and production build: **passed**; initial JavaScript 1,384,085 bytes within the 1,446,856-byte budget (existing large-chunk warning remains).
- EF pending-model-change check: **passed; no model drift**. No migration was added.
- Real-stack platform simulation: **63 probes passed, 0 defects**.
- Visible Google Chrome real-backend smoke: **1 passed in 8.8 seconds**.
- Broad fixture Playwright regression: **failed in auth setup** for manager/editor/denied; **374 tests did not run**. Retained screenshots, video, traces, and error contexts under `Frontend/test-results/playwright/`.
- `git diff --check`: **passed**.

## Visible-smoke boundary

The passing Chrome smoke proves real backend health, Owner login, ten console routes/headings, and absence of observed API network failures, HTTP 5xx responses, browser console errors, or page errors during that scope. It does not certify provisioning/invitation mutation, role/tenant denials, billing finalization/proration, purge races, exports, impersonation lifecycle, uploads, retry/DLQ, refresh/back/multi-tab, session expiry, PostgreSQL reconciliation, or worker recovery.

No production deployment, remote push, or schema migration was performed.
