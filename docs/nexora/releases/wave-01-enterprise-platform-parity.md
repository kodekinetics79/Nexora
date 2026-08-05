# Wave 1 Enterprise Platform Parity

## Scope and Result

Wave 1 adds governed enterprise-platform capabilities around the frozen RFQ-to-Revenue backbone. It does not replace the commercial domain model, add desktop RPA, deploy infrastructure, or enable external AI by default.

| Capability | Before | After | Certified behavior |
| --- | --- | --- | --- |
| Commercial Taxonomy and Document Skill Studio | M0/M1 | M3 | Tenant-scoped versioned artifacts with test, publish, archive, restore, and rollback |
| Human Action and Exception Center | M1/M2 | M3 | Persisted assignments, evidence, atomic decisions, bulk transitions, workflow-resume audit |
| AI Trust and Governance Center | M2 | M3 | Fail-closed tenant policy, egress/redaction/residency controls, cost ledger, audit, rollback |
| Model, Rule and Dataset Lifecycle Studio | M1 | M3 | Governed draft-to-production lifecycle with immutable version history |
| Integration Hub and Connector SDK | M1/M2 | M3 | Versioned connector contracts, secret references, lifecycle controls, server-owned SDK contract |
| Test, Simulation and Release Center | M1 | M3 | Persisted deterministic simulations and release publication gates |
| Commercial Document Archive and Search | M1 | M3 | Evidence-ledger search, access audit, retention policy, legal hold, export/deletion requests |
| Quality Analytics Center | M1/M2 | M3 | Cohort-based measured quality, evidence status, definitions, thresholds, and drill-downs |

## Normal Application Evidence

The authenticated normal-shell browser acceptance used `http://127.0.0.1:5173` with the real ASP.NET Core API and PostgreSQL 16. No route interception, mocked business response, test-only authentication, or skipped scenario was used.

| Capability | Click path | Route | Screenshot |
| --- | --- | --- | --- |
| Taxonomy and skills | Platform Governance -> Taxonomy & Skills | `/admin/platform/taxonomy` | [01-taxonomy-studio.png](../evidence/wave-01-platform-parity/01-taxonomy-studio.png) |
| Human actions | Sales Management -> Human Actions | `/sales/actions` | [02-human-action-center.png](../evidence/wave-01-platform-parity/02-human-action-center.png) |
| AI trust | Platform Governance -> AI Trust | `/admin/platform/ai-trust` | [03-ai-trust-center.png](../evidence/wave-01-platform-parity/03-ai-trust-center.png) |
| Lifecycle studio | Platform Governance -> Lifecycle Studio | `/admin/platform/lifecycle` | [04-lifecycle-studio.png](../evidence/wave-01-platform-parity/04-lifecycle-studio.png) |
| Integration hub | Platform Governance -> Integrations | `/admin/platform/integrations` | [05-integration-hub.png](../evidence/wave-01-platform-parity/05-integration-hub.png) |
| Test and release | Platform Governance -> Test & Release | `/admin/platform/releases` | [06-test-release-center.png](../evidence/wave-01-platform-parity/06-test-release-center.png) |
| Document archive | Platform Governance -> Document Archive | `/admin/platform/archive` | [07-document-archive.png](../evidence/wave-01-platform-parity/07-document-archive.png) |
| Quality analytics | Platform Governance -> Quality Analytics | `/admin/platform/quality` | [08-quality-analytics.png](../evidence/wave-01-platform-parity/08-quality-analytics.png) |

## Data, API, and Security

- The authenticated API is rooted at `/api/platform-governance`; tenant identity comes from the authenticated context, never request-supplied tenant IDs.
- Tenant tables use EF query filters, tenant-qualified relationships, PostgreSQL RLS with forced policies, least-privilege grants, append-only event protection, optimistic versions, and idempotency records.
- Commercial artifact and AI-policy mutations produce tenant-scoped audit/version records and support governed rollback.
- New tenants receive a fail-closed AI policy. External processing is disabled by default and the database constrains its dependency ceiling to 10 percent.
- Connector definitions persist secret references only. Archive search returns metadata and commercial lineage, not evidence bytes or document text.
- Explicit transactions execute through the configured Npgsql retry strategy, use serializable isolation, and are covered by a PostgreSQL regression test.

## Migrations

- `20260730044854_Wave1PlatformParity` creates the governed artifact, version, event, idempotency, human-action, action-event, simulation, and archive-control schema with RLS and tenant constraints.
- `20260730050411_Wave1AiTrustPolicy` creates and safely backfills tenant AI policy, policy versions, and AI usage records while enforcing the external-dependency ceiling.
- EF Core `migrations has-pending-model-changes` reports no drift.
- PostgreSQL 16 migration and RLS coverage passed in the complete PostgreSQL lane. No migration was applied to production.

## Verification

| Gate | Exact result |
| --- | --- |
| Focused Wave 1 tests | 19 passed, 0 failed, 0 skipped |
| Tenant AI-policy provisioning tests | 20 passed, 0 failed, 0 skipped |
| Production retry-strategy PostgreSQL regression | 1 passed, 0 failed, 0 skipped |
| Portable backend suite | 990 passed, 0 failed, 0 skipped; 57 seconds |
| PostgreSQL 16 suite | 193 passed, 0 failed, 0 skipped; 1 minute 51 seconds |
| Backend solution build | Passed with 0 errors; 216 pre-existing compatibility/nullability/obsolete-API warnings |
| Frontend lint | Passed with 0 warnings |
| Frontend production build | Passed; 14,740 modules; 1,287,617 initial JS bytes within the 1,446,856-byte optimized budget |
| Authenticated Playwright | 8 passed, 0 failed, 0 skipped; 38.9 seconds |
| EF model drift | No pending model changes |
| NuGet vulnerability scan | No known vulnerable packages |
| Git whitespace validation | `git diff --check` passed |

Browser acceptance covered authenticated creation and publication, human decision and workflow resume, AI-policy audit, lifecycle publication, connector SDK visibility, persisted simulation, retention policy, and truthful insufficient-evidence quality states.

## Independent Review Findings

- **P1 fixed:** explicit transactions failed under production Npgsql retry configuration. All affected governance services now execute transactions through `CreateExecutionStrategy`; PostgreSQL regression and all eight browser scenarios pass.
- **P1 fixed:** newly provisioned tenants could lack an explicit AI policy. Provisioning now creates one fail-closed policy and remains idempotent.
- **Accepted P2:** true critical-field and document-classification accuracy requires an independently labeled evaluation corpus. The UI reports insufficient evidence instead of inventing accuracy.
- **Accepted P2:** archive search intentionally indexes metadata and commercial lineage only until a governed secure customer-text index is approved.
- **Accepted P2:** the connector SDK and lifecycle are delivered; individual third-party runtime adapters require provider-specific certification.
- **Compensating control:** `react-router` 7.18.1 is affected by `GHSA-qwww-vcr4-c8h2` only through unstable RSC APIs. This Vite SPA contains no RSC/server-action runtime. Upgrade remains required before introducing SSR/RSC or when a compatible patched 7.x release exists.

## Readiness and Category Impact

Wave 1 is development-complete at M3. It converts platform administration from disconnected setup pages into governed, tenant-safe operating centers with evidence, release discipline, human accountability, AI cost control, and truthful quality measurement. This improves enterprise auditability and operational trust without weakening the established commercial workflow.

Production deployment remains outside this checkpoint. Strict readiness requires a reachable separate `clamd`, durable S3-compatible evidence storage, distinct Neon migration/runtime roles, reviewed secrets, migrations through the guarded owner path, and `/ready` returning 200. The local certification environment returns `/health` 200 and intentionally returns `/ready` 503 because production scanner and durable storage services were not provisioned.

No push, merge, deployment, production infrastructure change, or live-data access was performed.
