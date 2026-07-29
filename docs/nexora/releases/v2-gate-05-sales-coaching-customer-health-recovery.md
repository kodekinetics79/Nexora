# V2 Gate 05 - Sales Coaching, Customer Health and Revenue Recovery

Status: passed at M3 for development; M4 and production deployment remain blocked.

## Delivered Contract

- Normal paths: `Sales Management -> Sales Today -> Coaching and recovery` and `Customers -> Customer -> Customer 360`.
- Sales Today calculates local deterministic coaching findings and recovery opportunities from persisted Lead, RFQ, Quote, Order, sourcing, follow-up, revision and commercial-event evidence.
- Customer Health reports period-over-period RFQ trend, Quote coverage and decisions, conversion, accepted prices, margin evidence status, separate field and line revision burden, follow-up effectiveness, latest commercial activity, explainable opportunities and one next action.
- Recovery requires a viable, open RFQ with positive demand or an open nonterminal Quote without governed follow-up. Won, lost, cancelled, expired and other terminal Quotes are excluded; unknown execution outcomes are insufficient evidence.
- Every source is bounded and returns explicit `complete` or `partial` coverage. No bounded read silently claims a complete cohort.
- Manager acknowledgement is append-only, evidence-versioned, optimistic and idempotent. It records a coaching decision only and cannot mutate authoritative commercial workflow.

## Authorization And Isolation

- The workspace requires a valid authenticated tenant plus view permission for at least one relevant commercial module. Manager acknowledgement additionally requires manager status and edit authority.
- Managers see tenant-wide findings; individual users see their own assigned cohort. Manager-or-self rules also protect Sales Rep commercial-memory reads.
- Customer Health requires Customer plus commercial-workspace view authority. Route, query and body values cannot override authenticated tenant context.
- `sales_coaching_acknowledgements` has tenant-qualified keys and foreign keys, an EF query filter, least-privilege runtime grants and PostgreSQL RLS. Cross-tenant HTTP and direct RLS probes return no foreign data.

## Verification

- Focused backend, authorization and authenticated HTTP: 27 passed, 0 failed, 0 skipped.
- Focused PostgreSQL: 1 passed, 0 failed, 0 skipped.
- Portable backend: 962 passed, 0 failed, 0 skipped.
- PostgreSQL 16/Testcontainers: 182 passed, 0 failed, 0 skipped after correcting an older fixture-cardinality assertion for the newly persisted overdue coaching follow-up.
- Backend solution build: passed with 0 errors; existing legacy .NET Framework package-compatibility warnings remain.
- EF model drift: no pending model changes.
- Frontend lint: passed with zero warnings.
- Frontend production build: passed; initial JavaScript 1,279,582 bytes against the 1,446,856-byte optimized budget, preserving the 23.97% Gate 5 reduction.
- Mocked deterministic browser coverage: 11 passed, 0 failed, 0 skipped across desktop and mobile, including manager and individual roles, evidence, acknowledgement, retry, empty state, Customer Health and responsive layout.
- Authenticated live browser: 2 passed, 0 failed, 0 skipped in 8.8 seconds through normal UI login, Vite, actual HTTP APIs, a tenant runtime-role connection and PostgreSQL 16/RLS. The run reconciled 11 findings and one recovery opportunity, persisted a manager acknowledgement, rendered the Customer 360 health response and asserted zero browser console errors. Rehearsal runs exposed text-locator and repeat-run strictness mismatches, not application defects; the authoritative rerun passed. Duplicate React keys found in legitimate repeated Customer 360 rows were fixed before certification.
- NuGet vulnerability scan: no known vulnerable direct or transitive packages.
- npm audit: two package nodes for the high React Router RSC action advisory. Nexora uses a Vite `BrowserRouter` SPA with no RSC server, server action route, data-router action or affected execution endpoint. The path is unreachable; a forced breaking downgrade is not accepted. Upgrade remains tracked as a dependency-maintenance prerequisite.

## Migration And Recovery

- Migration `20260729135045_V2Gate05SalesCoachingGrowthIntelligence` creates append-only acknowledgements, checks, indexes, three tenant-qualified foreign keys, RLS and least-privilege runtime grants. Authorization reuses the established commercial modules and does not add a competing permission module.
- A representative data-bearing V2.4 baseline contained 77 migrations, an existing Supplier negotiation decision and nullable legacy User rows.
- Upgrade produced 78 migration-history rows, preserved the nullable User, created the acknowledgement table and retained all three foreign keys.
- Rollback returned history to 77, removed only the V2.5 table and preserved the legacy User and Supplier decision. Re-upgrade restored 78 history rows, the table and all three foreign keys.
- A PostgreSQL 16 binary dump restored into an independent database with 77 migration rows, the representative User and Supplier decision; upgrading that restored database produced 78 rows and the complete V2.5 table. The host PostgreSQL 18 restore client was rejected after emitting an unsupported setting, so the empty target was discarded and the rehearsal was repeated successfully with PostgreSQL 16 client binaries.
- Historical migrations were not edited or deleted. No shared or production database was accessed.

## Consultant Closure

- Expanded workspace authorization to the established Lead, Customer, RFQ, Quotation, Order and Supplier History modules without creating a parallel permission model.
- Closed the idempotency race by treating the tenant/key unique constraint as replay, rereading the persisted acknowledgement and rejecting request-hash mismatch. Concurrent authenticated HTTP coverage passes.
- Excluded terminal Quotes, expired RFQs, non-positive demand and unknown execution outcomes from recovery claims.
- Prevented unrelated Customer follow-ups from suppressing RFQ-specific coaching.
- Separated field and line revision denominators and exposed source-limit incompleteness in API and UI.
- Retained `QUOTE_SENT` as an open commercial state and kept all recommendations non-authoritative.

## Remaining Gates

- M4 requires sufficient observed coaching, follow-up and commercial outcomes for calibration, drift monitoring and causal-effect limits.
- Autonomous communication, reassignment, lifecycle mutation and pricing action remain prohibited without a separately approved policy gate.
- Production remains NO-GO until reachable S3-compatible immutable evidence storage and a production malware scanner are verified, Render/Vercel configuration is rehearsed, and the React Router dependency can be upgraded without a breaking regression.
