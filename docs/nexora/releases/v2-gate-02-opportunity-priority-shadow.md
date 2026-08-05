# V2 Gate 02: Opportunity Priority Shadow

Status: **GREEN M3 acceptance; M4 shadow activation blocked.** The governed recommendation workflow is accepted, but real-path ECV cannot reach M4 until Product or offer cost has authoritative currency. Production deployment is outside this gate and remains blocked by Track 0 storage and malware-scanner readiness.

## Frozen Scope

- Local deterministic opportunity priority and next-best-action advice in `Shadow` mode.
- Immutable, tenant-qualified Lead, Commercial Case, and Nexora Serial lineage.
- Persisted evidence snapshots, recommendations, later outcomes, reviewer feedback, audit events, operations, and atomic outbox records.
- Manager tenant queue and assigned-owner queue in the normal Sales Today and Commercial Case workspace.
- Seven separately visible commercial evidence components and conditional Expected Commercial Value, explicitly uncalibrated and advisory.
- No workflow mutation, calibrated win-probability claim, revenue claim, external AI call, price mutation, inventory commitment, supplier award, or customer communication.

## Accepted Review Fixes

1. Terminal Quote/Order cases are excluded from active ranking and never receive a new recommendation; a pre-terminal recommendation may still collect its later authoritative outcome.
2. Quote outcomes use exact canonical status codes. Display text and substrings cannot convert `Not accepted` into a win.
3. PostgreSQL rejects recommendation lineage or supersession that changes tenant, Lead, Commercial Case, or Nexora Serial.
4. Reconciliation is idempotent and cursor-bounded to 100 cases per transaction; the client advances the durable cursor with a new command identity per batch.
5. The reconcile action requires manager role and Lead edit permission. Assigned owners can read and assess only their own recommendations; denied users cannot read or mutate them.
6. Fulfilment confidence requires persisted resolution evidence for every Lead line; partial, unresolved, absent, or stale evidence blocks Expected Commercial Value.
7. Currency-denominated Expected Commercial Value is not used to order unlike currencies. The queue keeps the currency-neutral deterministic priority score.
8. Component evidence timestamps are anchored to source Lead or inventory evidence, preventing unchanged reconciliations from creating replacement recommendations.
9. Expected Commercial Value requires high-confidence opportunity value, one uppercase three-letter currency, currency-qualified cost coverage for every Lead line, and complete current-revision fulfilment evidence. The current Product master has no cost-currency authority, so the real resolver fails closed with no margin or ECV; the formula is contract-tested with complete synthetic service inputs only.
10. Issued incoming supply is not treated as available-to-promise unless a future deadline-qualified fulfilment contract exists; zero availability now produces a sourcing action and blocker.
11. Replacement feedback uses a server-owned action catalog, the Sales Today queue is paginated, and responsive rationale controls have unique IDs.
12. PostgreSQL validates component shape, non-negative values, ECV/currency pairing, JSON/relational coherence, RLS expressions, and append-only migration behavior.
13. Exact Product identifiers must resolve to one active tenant Product. Duplicate model identifiers fail closed, and an invalid canonical Customer never contributes customer-quality score.
14. Historical win evidence uses a coherent 24-month cohort: recent sent Quotes are the denominator and only recent Orders linked to those same Quotes are the numerator.
15. Recommendation policy v3 makes Customer identity an explicit action, exposes low/missing-margin escalation and qualified Lead opening, and supersedes legacy component backfills exactly once even when source evidence is unchanged.

## Verification

| Gate | Result |
|---|---|
| Focused portable | 28 passed, 0 failed, 0 skipped |
| Focused PostgreSQL/HTTP/RLS | 21 passed, 0 failed, 0 skipped |
| Full portable | 903 passed, 0 failed, 0 skipped; 49 seconds |
| Full PostgreSQL 16 | 176 passed, 0 failed, 0 skipped; 1 minute 28 seconds |
| Authenticated browser | 6 passed, 0 failed, 0 skipped; 3 role scenarios across desktop and 390x844 mobile; 34.6 seconds |
| Backend build | Succeeded, 0 errors; 4 pre-existing package compatibility warnings |
| Frontend lint | Passed with zero warnings |
| Frontend production build | Passed; 1,279,304 initial JS bytes against 1,446,856-byte budget |
| EF model drift | No pending model changes |
| NuGet vulnerability scan | No known vulnerable packages |
| Git whitespace | `git diff --check` passed |

Browser paths: `Login -> Sales -> Today -> Reconcile -> Open opportunity -> Record feedback`. Roles covered: manager, assigned sales owner, and denied user. Calls used normal UI authentication, HTTP APIs, PostgreSQL, and RLS with representative synthetic fixture data.

## Migration And Restore

- Historical migrations were not rewritten. Gate migrations: `20260729031740_V2Gate02OpportunityPriorityShadow`, `20260729043226_V2Gate02OpportunityCommercialComponents`, and `20260729054001_V2Gate02ValidateOpportunityCommercialComponents`.
- The final commercial-component migrations were rehearsed on populated PostgreSQL: 64 recommendation rows survived, migration history moved 76 -> 74 -> 76, all component columns were absent after downgrade, all 64 legacy rows received `legacy_reconcile_required` after re-upgrade, all three new constraints were validated, the temporary metadata default was removed, and the append-only trigger returned enabled. Constraint validation is isolated in its own migration transaction so the schema-change transaction releases its earlier exclusive DDL lock before table scans. An isolated PostgreSQL test automates populated upgrade, downgrade, and re-upgrade.
- A PostgreSQL 16 custom dump (1.7 MB) restored the 74-migration foundation into a fresh database with 16 recommendations, 6 feedback records, and the recommendation-lineage trigger intact before the component migration was applied and re-rehearsed.
- PostgreSQL tests verify forced RLS, least-privilege grants, append-only mutation rejection, event/outbox coupling, tenant negatives, outcome chronology, feedback lineage, recommendation lineage, concurrency, and authenticated HTTP role boundaries.

## Security Disposition

- `npm audit --omit=dev` reports the known high-severity React Router RSC action advisory through `react-router-dom`. Nexora uses client-side `BrowserRouter` and defines no RSC server route actions, so the affected request path is unreachable. The proposed automated fix is a breaking downgrade; retain the current version, prohibit RSC actions, and upgrade when a compatible patched release is available.
- External AI dependency for this gate is 0%. All evidence and scoring are local deterministic processing.

## Truthful Limits

The cohort reports accuracy as `null` with an explicit not-measured status. Expected Commercial Value is calculated only when all seven component inputs are available in one ISO-format currency, is labelled `shadow_unvalidated`, and is not a calibrated revenue forecast. The production Lead decision resolver currently emits no margin or ECV because Product cost lacks authoritative currency; it does not infer one. Win likelihood and customer quality are transparent historical proxies. The three-letter currency format is not yet currency-master validation. Autonomous action remains blocked until adequate authoritative later outcomes, currency-qualified landed-cost completeness, calibration, and drift evidence exist.
