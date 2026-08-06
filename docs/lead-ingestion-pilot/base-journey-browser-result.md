# Base Journey — Browser Result

**Date:** 2026-08-06 · Branch `wip/phase1-base-journey-20260806`

## BASE LEAD → RFQ → CUSTOMER QUOTE DRAFT: **GO**

Proven by an authenticated browser run against a real frontend, backend, worker and PostgreSQL,
reproducible from a clean state with one command.

```
./scripts/e2e/run-phase1-base-journey.sh
  ✓ Phase 1 — Lead converts to exactly one RFQ and one Customer Quote Draft (11.2s)
  ✓ Phase 1 — cross-tenant and unauthorized access is denied (2.4s)
  2 passed (14.3s)
```

| Lane | Result |
|---|---|
| Backend non-PostgreSQL | **Failed 0 · Passed 2092 · Skipped 0** |
| Backend PostgreSQL | **Failed 0 · Passed 330 · Skipped 0** |
| Frontend typecheck / build / vitest | exit 0 · exit 0 · **216 passed** |
| Login preflight | **HTTP 200 + JWT** |
| Playwright | discovered 2 · **passed 2 · failed 0 · skipped 0 · retried 0** |

## Identity-seeding root cause

The seeder constructed a `Lead` directly. Real ingestion creates the canonical Lead, revision 1,
the occurrence lineage and the commercial identity **together** inside the identity pipeline, so
the hand-built row had no `CurrentRevisionId`. Conversion legitimately refuses a Lead with no
immutable current revision — the seeder was manufacturing a state the product never produces, and
the failure surfaced four steps downstream pointing nowhere near its cause.

**Path reused:** `ILeadIdentityApplicationService.ReconcileAsync(Lead candidate, LeadIntakeDescriptor)`
— the same call `ExtractionWorker.cs:944` makes. No identity logic is duplicated in the seeder and
no second pipeline exists. Extracted document facts are supplied deterministically because this
scenario certifies Lead review and conversion, not OCR accuracy; everything downstream of the
facts — identity, revision, fingerprint, occurrence, serial — is the product's own work.

The lead is then walked through its **real** lifecycle
(RECEIVED → PENDING_IDENTIFICATION → ASSIGNED → UNDER_REVIEW → QUALIFIED) via the governed
lifecycle command. Setting `LeadStatusId` directly is refused by the domain, and jumping straight
to QUALIFIED is refused by the transition policy — both correctly.

## Seeder invariants (runner stops before Playwright if any fail)

Exactly one canonical Lead · exactly one revision, number 1 · lead points at that revision ·
revision linked to its source occurrence · six revision lines and six lead lines · customer
confirmed · CommercialCaseId and NexoraSerial present · **no RFQ, no Quote, no participation
decision** before the browser runs. Replay is idempotent by `IdempotencyKey` — same Lead, no
second revision.

## Product defects found by the browser run and fixed

1. **`LinkRfqAsync` violated its own immutability guard.** `nexora_guard_commercial_line_resolution_update`
   permits exactly one mutation — `RfqId` **and** `RfqItemId` moving NULL→NOT NULL together.
   `RfqId` was set before searching for a matching RFQ line, so a resolution with no match — the
   normal outcome for an **excluded** line — was left half-written and raised `P0001`, failing the
   whole conversion with a 500. **Every partial conversion hit this.**
2. **Commercial readiness ignored participation.** Readiness was judged over every RFQ line, so
   lines explicitly declined (No-Quote) or still Pending counted as blockers —
   *"Resolve 5 blocked lines before quoting"* on an RFQ where only one line was ever to be quoted.
   Now judged over the lines actually being quoted, with the pre-participation behaviour preserved
   as the fallback.
3. **Quote Draft required everything the draft exists to defer.** The gate demanded `VIABLE_READY`
   — zero unfulfilled demand — before a draft could be created, while the draft it then builds
   carries `UnitPrice 0`, no currency, no validity and the remark *"Commercial Review Required:
   pricing, inventory, lead time, tax, freight and validity remain pending."* Supply coverage is a
   condition of quote **release**, not of starting one. A draft is now blocked only by
   `NO_QUOTE_REVIEW` (no lines, or an overdue deadline); identity integrity is still enforced
   separately.

## Regression tests added (real PostgreSQL)

`GoldenJourneyIdentitySeedPostgreSqlTests` — revision 1 created and current, linked to its
occurrence, six projected lines; replay returns the same Lead and adds no second revision; a Lead
built outside the pipeline still has no current revision, so **the conversion gate is not
weakened**.

## Artifacts

`Frontend/playwright-report/index.html` · traces, video and screenshots under
`Frontend/test-results/` · service logs and the mode-600 credentials file under `.e2e-run/`
(both gitignored).

## Journey proven

Login · named owner visible · hard warning cannot be waived · hard quantity corrected · soft
warning acknowledged with a reason · line excluded and still preserved on the Lead · **exactly
one RFQ** · Quote / No-Quote-with-reason / Pending · **exactly one Customer Quote Draft**
containing only the Quote-selected line, carrying manufacturer and part number · replay of both
convert and prepare-draft creates no duplicate · unauthenticated 401 · cross-tenant denied.
