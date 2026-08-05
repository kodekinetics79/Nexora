# V2 Gate 04 - Supplier Bid Quality and Negotiation Intelligence

Status: passed at M3 for development; M4 and production deployment remain blocked.

## Delivered Contract

- Normal path: `Procurement -> Supplier Quote Inbox -> Supplier Quote -> Bid quality and negotiation guidance`.
- Deterministic bid checks cover validity, stock, commercial terms, lead time, price outliers, currency, revision volatility, post-selection price increases, alternates, Supplier authorization, stale evidence and completeness.
- Seven shadow recommendations cover best-and-final price, quantity breaks, faster delivery, freight-inclusive offers, payment terms, partial immediate availability and approved alternates.
- Comparable cohorts are tenant-, RFQ-line- and currency-qualified; use one current eligible offer per other Supplier; and exclude stale, unverified, non-ready, blocked and high-risk Suppliers.
- Decisions are append-only, optimistic, idempotent and evidence-snapshotted. They cannot contact a Supplier, change an offer, select an award or mutate Customer pricing.
- Shared charges are allocated by line commercial value across mixed units. Intake rejects quotes above 1,000 lines or 5,000 evidence facts. API and decision snapshots retain authoritative sample sizes while capping displayed evidence at 100 facts and prior decisions at 100 rows; the API and UI disclose the full decision count and whether older rows were omitted.

## Authorization And Lineage

- `Supplier History:View` reads guidance; `Supplier Negotiation:Edit` records a decision. A history-only role receives `200` for GET and `403` for POST.
- Tenant identity comes only from the authenticated context. EF filters, composite tenant foreign keys and PostgreSQL RLS protect `supplier_negotiation_decisions`.
- Current Supplier Quote, revision and line lineage is mandatory. Alternate authorization uses the latest accepted or corrected review value; revocation takes effect immediately.
- Blocking bid findings prevent `PREPARED`; zero/non-positive prices are an explicit critical blocker.

## Verification

- Focused negotiation and Supplier Quote intake tests: 25 passed, 0 failed, 0 skipped.
- Focused commercial-learning/procurement/negotiation tests: 76 passed, 0 failed, 0 skipped.
- Focused PostgreSQL and authenticated HTTP: 4 passed, 0 failed, 0 skipped.
- Portable backend: 933 passed, 0 failed, 0 skipped.
- PostgreSQL 16/Testcontainers: 180 passed, 0 failed, 0 skipped after fixing the acceptance fixture to preserve Lead -> RFQ -> Demand Line serial identity.
- Backend solution build: passed with 0 errors; existing legacy-package warnings remain.
- EF model drift: no pending model changes.
- Frontend lint: passed with zero warnings.
- Frontend production build: passed; initial JavaScript 1,279,582 bytes against the 1,446,856-byte budget.
- Mocked browser acceptance: 15 passed, 0 failed, 0 skipped across desktop and mobile on a clean loopback port, including authoritative decision-history truncation disclosure.
- Live browser acceptance: 1 passed, 0 failed, 0 skipped in 5.8 seconds using normal login, real Vite/API HTTP, a least-privilege tenant-role connection, PostgreSQL/RLS, GET guidance and POST decision. An initial rehearsal run failed because the temporary frontend origin was absent from the API's explicit CORS list; direct authentication succeeded, the local origin was configured, and the authoritative rerun passed without bypassing authentication.
- NuGet vulnerability scan: no known vulnerable direct or transitive packages.
- npm audit: two package nodes for the high React Router RSC action advisory. Nexora is a Vite `BrowserRouter` SPA with no RSC server or action endpoint, so the affected path is unreachable; the breaking forced downgrade is not accepted.
- `git diff --check`: passed before certification and is rerun at commit time.

## Migration And Recovery

- Migration `20260729120629_V2Gate04SupplierNegotiationIntelligence` creates the append-only decision table, tenant RLS/grants, composite foreign keys, checks, indexes and permission module.
- Representative rollback removed the V2.4 table, owned module and a permission assigned after migration without losing the Supplier Quote; history returned to 76 records with `20260729054001_V2Gate02ValidateOpportunityCommercialComponents` current.
- Re-upgrade restored 77 history records, the table/module and the representative Supplier Quote.
- A binary backup restored independently with all 77 migration records, one Supplier Quote and one negotiation decision.
- The frozen historical migration `20260722043733_CreateCustomFieldIdempotencyIndex` cannot run through the current EF/Npgsql fresh-chain lock because `CREATE INDEX CONCURRENTLY` is rejected inside that lock transaction. Historical migration files were not rewritten. Production migration must start from the certified current baseline or use an approved operator runbook; a brand-new empty-database chain remains NO-GO until separately remediated.

## Consultant Closure

- Corrected header and line values, alternate revocation, canonical-current-revision checks and current Supplier eligibility now agree across commercial learning, procurement and negotiation paths without mutating captured source values.
- Competitor cohorts exclude the current Supplier, inactive Suppliers and duplicate offers from one Supplier; the latest captured current offer wins independently of revision ID ordering.
- Quote capture requires demand-line Nexora Serial continuity, and acceptance-fixture reuse validates the complete RFQ, sourcing-case, solicitation, Supplier Quote, revision and line graph.
- Quote input, displayed evidence and prior-decision history are bounded. Capped decision history carries its authoritative total and a visible truncation disclosure.
- Quote graph loading avoids sibling-collection Cartesian expansion.
- Serializable write conflicts map to the domain conflict response, and the concurrency regression requires that exact exception.
- Rollback tolerates ordinary post-migration permission administration.

## Remaining Gates

- Benchmark unique-constraint lock duration against a production-scale `supplier_quote_revisions` copy before deployment.
- Remediate or formally operationalize the frozen fresh-database migration-chain limitation.
- M4 requires calibrated Supplier response/outcome cohorts, immutable recommendation effectiveness measurements and policy-approved autonomous communication boundaries.
- Production remains NO-GO until the existing Render evidence-storage and malware-scanner prerequisites are verified.
