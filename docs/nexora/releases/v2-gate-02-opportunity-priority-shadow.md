# V2 Gate 02: Opportunity Priority Shadow

Status: **GREEN for the local development checkpoint.** Production deployment is outside this gate and remains blocked by Track 0 storage and malware-scanner readiness.

## Frozen Scope

- Local deterministic opportunity priority and next-best-action advice in `Shadow` mode.
- Immutable, tenant-qualified Lead, Commercial Case, and Nexora Serial lineage.
- Persisted evidence snapshots, recommendations, later outcomes, reviewer feedback, audit events, operations, and atomic outbox records.
- Manager tenant queue and assigned-owner queue in the normal Sales Today and Commercial Case workspace.
- No workflow mutation, predictive win probability, revenue claim, external AI call, price mutation, inventory commitment, supplier award, or customer communication.

## Accepted Review Fixes

1. Terminal Quote/Order cases are excluded from active ranking and never receive a new recommendation; a pre-terminal recommendation may still collect its later authoritative outcome.
2. Quote outcomes use exact canonical status codes. Display text and substrings cannot convert `Not accepted` into a win.
3. PostgreSQL rejects recommendation lineage or supersession that changes tenant, Lead, Commercial Case, or Nexora Serial.
4. Reconciliation is idempotent and cursor-bounded to 100 cases per transaction; the client advances the durable cursor with a new command identity per batch.
5. The reconcile action requires manager role and Lead edit permission. Assigned owners can read and assess only their own recommendations; denied users cannot read or mutate them.

## Verification

| Gate | Result |
|---|---|
| Focused portable | 19 passed, 0 failed, 0 skipped |
| Focused PostgreSQL/HTTP/RLS | 18 passed, 0 failed, 0 skipped |
| Full portable | 894 passed, 0 failed, 0 skipped; 58 seconds |
| Full PostgreSQL 16 | 173 passed, 0 failed, 0 skipped; 1 minute 44 seconds |
| Authenticated browser | 6 passed, 0 failed, 0 skipped; desktop and 390x844 mobile; 32.4 seconds |
| Backend build | Succeeded, 0 errors; 4 pre-existing package compatibility warnings |
| Frontend lint | Passed with zero warnings |
| Frontend production build | Passed; 1,270,435 initial JS bytes against 1,446,856-byte budget |
| EF model drift | No pending model changes |
| NuGet vulnerability scan | No known vulnerable packages |
| Git whitespace | `git diff --check` passed |

Browser paths: `Login -> Sales -> Today -> Reconcile -> Open opportunity -> Record feedback`. Roles covered: manager, assigned sales owner, and denied user. Calls used normal UI authentication, HTTP APIs, PostgreSQL, and RLS with representative synthetic fixture data.

## Migration And Restore

- Historical migrations were not rewritten. New migration: `20260729031740_V2Gate02OpportunityPriorityShadow`.
- Populated local PostgreSQL was downgraded to Gate 1 and re-upgraded to Gate 2 successfully.
- A PostgreSQL 16 custom dump (1.7 MB) restored into a fresh database with 74 migrations, 16 recommendations, 6 feedback records, and the recommendation-lineage trigger intact.
- PostgreSQL tests verify forced RLS, least-privilege grants, append-only mutation rejection, event/outbox coupling, tenant negatives, outcome chronology, feedback lineage, recommendation lineage, concurrency, and authenticated HTTP role boundaries.

## Security Disposition

- `npm audit --omit=dev` reports the known high-severity React Router RSC action advisory through `react-router-dom`. Nexora uses client-side `BrowserRouter` and defines no RSC server route actions, so the affected request path is unreachable. The proposed automated fix is a breaking downgrade; retain the current version, prohibit RSC actions, and upgrade when a compatible patched release is available.
- External AI dependency for this gate is 0%. All evidence and scoring are local deterministic processing.

## Truthful Limits

The cohort reports accuracy as `null` with an explicit not-measured status. Calibration, expected commercial value, win probability, and autonomous action remain blocked until adequate authoritative later outcomes, currency-safe value, cost authority, and drift evidence exist.
