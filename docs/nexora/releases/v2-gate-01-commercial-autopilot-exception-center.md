# V2 Gate 01 - Commercial Autopilot and Exception Center

Status: **GREEN for V2 Gate 01 development acceptance**. Production deployment remains blocked by the boundaries below.

## Scope

- Deterministic `UnassignedLead` and `OverdueFollowUp` exception detection.
- Immutable Commercial Case and Nexora Serial lineage.
- Human-governed acknowledge, resolve and dismiss decisions.
- Role-scoped normal-shell Exception Center and Dashboard entry.
- PostgreSQL RLS, append-only decisions, atomic outbox and tenant-safe owner evidence.
- Deterministic routing priority activation.

Excluded from this gate: autonomous commercial mutations, predictive pricing, supplier awards, purchase-order issue, external communication, inventory commitment, portal identity, finance execution and unsupported exception signals.

## Click Path

`Sign in -> Sales Management -> Exception Center`

Manager flow: `Refresh -> select exception -> inspect evidence -> acknowledge/resolve/dismiss`.

Individual flow: only owned follow-up exceptions are visible; tenant-wide reconcile is unavailable.

## Evidence Contract

Rule version: `commercial-exceptions-v1`.

Each row reconciles to one authoritative source record and shows Nexora Serial, source type/id/version, owner, severity, SLA, reason, evidence, recommendation and decision version. `sourceId` links remain record-specific after a follow-up or routing item becomes terminal. Source reachability is measured independently; complete, partial and unavailable coverage are distinct API/UI states.

## Verification

Executed on 2026-07-29 with authorized synthetic data only:

- Portable backend: `dotnet test ... --filter 'Category!=PostgreSQL'` -> 875 passed, 0 failed, 0 skipped.
- PostgreSQL 16: `dotnet test ... --filter 'Category=PostgreSQL'` -> 155 passed, 0 failed, 0 skipped. Gate-specific coverage includes two-context parallel refresh/transition replay, direct RLS and audit-negative SQL, and baseline-with-data upgrade.
- Focused application/platform/HTTP checks: 31 passed before consultant review; post-review application/platform checks 28 passed; final controller and Gate 1 PostgreSQL checks 11 passed.
- Backend build: succeeded with 0 errors. Existing `NU1701` compatibility warnings remain outside this gate.
- EF drift: `dotnet ef migrations has-pending-model-changes ...` -> no model changes since the latest migration.
- Frontend: `npm run lint` passed with zero warnings; `npm run build` passed. Initial JavaScript 1,270,328 bytes against the 1,446,856-byte budget.
- Authenticated Playwright: 2 passed, 0 failed, 0 skipped against normal React UI, actual HTTP APIs and PostgreSQL. Covered dashboard and sidebar entry, manager and owner scopes, evidence, exact source rows, deterministic filters, empty/error/retry states, stable retry identity, real `403`, real stale-version `409` recovery, persistence and 390x844 layout.
- Migration rehearsal: baseline `20260728202215_AllowNonEmailLeadIntake` with a persisted Lead/Nexora Serial -> Gate 1 upgrade -> backup restore -> re-upgrade. Both databases ended at `20260729020217_V2Gate01CommercialExceptionCenter`, preserved `NXR-2026-000001`, and had 4 tables, 4 forced-RLS tables and 4 policies. Backup size: 1,660,707 bytes.
- NuGet vulnerability scan: no vulnerable packages. `npm audit --omit=dev` reports the inherited high React Router RSC advisory. Nexora is a Vite `BrowserRouter` SPA with no RSC/server actions, so the vulnerable execution path is unreachable; forced downgrade is not accepted without regression validation.
- `git diff --check`: passed.

Independent consultant review found no P0. All accepted Gate 1 P1 findings were fixed and retested, including audit action/state integrity, material-change audit coupling, retry tracking, concurrency, exact source navigation, role scope, KPI definitions and browser repeatability.

## Migration And Rollback

The forward migration is additive and historical migration files are unchanged. Application rollback retains the forward-compatible schema and append-only exception/event/operation/outbox history. Destructive schema downgrade is not the operational rollback path.

## Production Boundary

Production readiness remains NO-GO while previously exposed secret rotation is uncertified and deployed S3-compatible evidence storage, malware scanning and `/ready = 200` evidence are absent. This gate does not weaken fail-closed readiness.

Before deploying this increment, configure `Jwt__PlatformKey` in Render as a distinct, cryptographically random secret of at least 32 bytes. Validate startup and platform-token authentication in a non-production environment before promoting. If validation fails, roll the application back to the prior image while retaining the forward-compatible schema; do not downgrade or delete append-only commercial exception history.
