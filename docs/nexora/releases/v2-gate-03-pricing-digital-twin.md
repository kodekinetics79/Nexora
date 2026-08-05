# V2 Gate 03 - Pricing Intelligence and Opportunity Digital Twin

Status: passed at M3 for development; M4 and production deployment remain blocked.

## Delivered Contract

- Normal path: `RFQ Management -> RFQ -> Opportunity Digital Twin -> Smart Pricing`.
- Nine deterministic scenarios: stock, Supplier-only, split, fastest, lowest landed cost, best verified margin, lowest verified risk, approved alternate and partial immediate delivery.
- Every scenario exposes quantity allocation, cost sources, delivery, validity, margin status, risk status, assumptions, confidence, evidence and approval requirements.
- Verified Customer target bridges are derived only from immutable `CustomerQuoteSourcingDecision` rows using gross-margin mathematics.
- Shadow price/win cohorts use tenant-scoped exact Product/currency history, prefer Customer and quantity comparability, exclude open/undecided Quotes, define wins through distinct actual Customer Orders, and report last won price, range, sample size and chronological walk-forward MAPE.

## Financial Authority

`PricingEngine` is advisory. Unknown/mixed currencies and incomplete line coverage do not produce a combined total. Legacy price-list, Supplier purchase history, raw Supplier projection and unstamped Product master inputs are excluded from the shadow blend. Purchase history remains disabled until its schema, relationships, backfill and PostgreSQL RLS are strictly tenant-qualified. Historical costs never become binding floors. Direct RFQ price application fails closed; the governed Supplier award to Customer Quote bridge remains authoritative.

## Current Evidence

- Focused backend: `Release02CommercialLearningTests` - 29 passed, 0 failed, 0 skipped.
- Portable backend: 909 passed, 0 failed, 0 skipped.
- PostgreSQL 16/Testcontainers: 176 passed, 0 failed, 0 skipped.
- Backend solution build: passed with 0 errors; existing nullable, obsolete-API and legacy-package warnings remain.
- EF model drift: no pending model changes. This gate adds no migration.
- Frontend lint: passed with zero warnings.
- Frontend production build: passed; initial JavaScript 1,279,352 bytes against the 1,446,856-byte budget.
- Authenticated browser SIT: scenario 35 passed, 0 failed, 0 skipped against the normal RFQ route, actual HTTP API and PostgreSQL. It verifies all nine scenarios, evidence disclosure, predictive/target summaries, Smart Pricing, and real HTTP `409` for prohibited direct apply.
- NuGet vulnerability scan: no vulnerable direct or transitive packages.
- npm audit: two high findings from the React Router RSC action advisory. Nexora is a `BrowserRouter` SPA with no React Server Components or server actions, so the vulnerable execution path is unreachable; upgrade remains tracked without a major-version downgrade.
- `git diff --check`: passed.

## Consultant Closure

- Quote-send floor checks now require the authenticated business unit and no longer bypass EF tenant filters.
- Supplier purchase history is disabled as a pricing signal until its schema, relationships, backfill and RLS are strictly tenant-qualified.
- Open Quotes are excluded, outcomes are distinct per Quote, and four-win backtesting uses one chronological holdout without future leakage.
- Split scenarios with unknown inventory cost withhold total landed cost; stock-covered cases do not fabricate eligible Supplier routes.
- Target bridges reject stale, non-canonical, currency-mismatched or infeasible evidence.
- Mixed, unknown or incomplete pricing coverage withholds combined totals, margins and authoritative actions.

## Production Gate

Production remains NO-GO. Render readiness still requires reachable S3-compatible immutable evidence storage and a reachable production malware scanner. This local checkpoint does not deploy or change production configuration.

## M4 Blockers

- Immutable tenant-qualified Digital Twin run, override and outcome event persistence.
- Production calibration and drift cohorts.
- Authoritative internal inventory cost currency.
- Explicit Customer or engineering alternate approval evidence.
- Approved FX snapshots for cross-currency comparison.
