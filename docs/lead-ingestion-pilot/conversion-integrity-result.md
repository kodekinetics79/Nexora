# Lead → RFQ → Quote Draft — Conversion Integrity Result

**Date:** 2026-08-06. **Scope:** repair increment #1 of the commercial-integrity slice.

## 1. Decision

> **LEAD → RFQ → QUOTE DRAFT INTEGRITY: NO-GO** — with four defects repaired and proven, and
> five named blockers remaining. This is an honest interim, not a failure: the increment
> delivered is complete, tested and reversible. The journey is not yet demonstrable end to end
> in a browser, so it cannot be called GO.

The single fact that governs this verdict: **the Lead → RFQ → Quote Draft path already existed
and worked.** The task was never to build it. Independent review confirmed the conversion is
genuine engineering — serializable transaction, tenant scoping, immutable Nexora Serial,
duplicate and extraction-review gates — and that both idempotency checks are backstopped by
unique partial indexes (`UX_RFQ_BusinessUnitID_LeadID`, `UX_Quotes_BusinessUnitID_RFQID`,
`Migrations/20260722051308_OperationalizeCommercialLifecycle.cs:304-305`). Building a second
conversion command would have been the worst available outcome.

## 2. Baseline gate — MET

| Lane | Result | Backing |
|---|---|---|
| Non-PostgreSQL (SQLite) | **Failed 0 · Passed 2064 · Skipped 0** | in-memory / SQLite |
| PostgreSQL | **Failed 0 · Passed 312 · Skipped 0** | **real PostgreSQL**, Testcontainers on a live Docker daemon, migrations applied |

Zero duplicate-ID discovery drops. No retries, no skip attributes, nothing mocked into passing.

Progression this session: `1 failed / 2040 passed / 6 silently dropped` → `0 failed / 2043` →
`0 failed / 2064` after the repairs. **+23 tests, all new coverage.**

## 3. Defects repaired

| # | Defect | Repair | Proof |
|---|---|---|---|
| **D-1** | **Test truth eroded.** 1 failing test, and 6 theory cases dropped by xUnit at *discovery* — invisible in `Skipped: 0` while the intake drift-guard silently shrank. | `.pptx` now carries the "nothing disappears silently" assertion (`.msg` is admitted since the container readers landed); a new test pins the new `.msg` contract; `ProbeExtensions()` de-duplicates and a new test **asserts** no duplicates, so this erosion class fails loudly. | `DocumentIntakeAllowListTests`, `EmailIngestEnqueuerTests` |
| **D-2** | **No line-level bid participation.** A partial bid — quote 12 of 84 lines — had no representation anywhere. The only bid field was `Rfq.BiddingDecision`: header-level, untyped nullable `string`, no reason, no actor. `ConvertRequestItem.Include` is transient and never persisted. | `Rfqitem.ParticipationDecision` (`Pending`/`Quote`/`NoQuote`) + `NoQuoteReason` + actor + timestamp, with the reason rule enforced in the **domain** so every caller obeys it, and mirrored as DB check constraints. Defaults to `Pending` — never `Quote`. | `RfqLineParticipationTests` (11 cases) |
| **D-3** | **Quote Draft quoted the whole bid list, and hid the part's identity.** Every RFQ line became a quote line. The engineer pricing it saw quantity, UoM and a description — no manufacturer, no part number, no requested delivery date. | `PrepareDraftFromRfqAsync` now refuses unless ≥1 line is explicitly marked `Quote`, and projects **only** those lines. Completeness is checked only on quoted lines — a declined line is often incomplete, which is *why* it was declined. The buyer's manufacturer/part number/material code/requested date/currency are surfaced **read-through `QuoteItem.RfqitemId`**, not copied. | `QuoteDraftParticipationAndCarryForwardTests` (8 cases) |
| **D-4** | **Quote numbers could repeat, and leaked across tenants.** Three independent generators write `Quotes.QuoteNo`; `IX_Quotes_QuoteNo` is **not** unique; `QuoteService.GenerateNextQuoteNumber` filtered on prefix **alone**, so tenant B advanced tenant A's counter. | Unique index `UX_Quotes_BusinessUnitID_QuoteNo` — a collision from **any** path, including ones not yet written, is now impossible. Generation is tenant-scoped. | migration + model |
| **D-5** | **`QuoteConfigurationController` unguarded on state change.** `[Authorize]` only. `POST migrate` iterated **every** BusinessUnitId with no tenant check; `POST` fell back to the request body's `BusinessUnitId` when the claim was absent or `0`. | `migrate` → `Business Units:Edit` (the cross-tenant maintenance convention). `CreateOrUpdate` → `Quote Configuration:Edit`, and the tenant claim is now the **only** accepted source — no claim, `403`. | controller |

**Retracted finding:** an earlier report said `QuotationUploaderController` lacked permission
attributes. It does not — it carries `Quotations:View` and `Quotations:Create`. Corrected.

## 4. Reused, not rebuilt

`LeadConversionIntelligence.ConvertAsync` · `QuoteService.PrepareDraftFromRfqAsync` · `Rfq` /
`Rfqitem` / `Quote` / `QuoteItem` · `CommercialCase` immutable identity · `LifecyclePolicy`
canonicalisation · `SetupMaster` quote statuses (`DRAFT` reused; **`AwaitingInputs` deliberately
not created**) · `RequireModulePermission` + existing module names · EF global query filters +
Postgres RLS · the existing `QuoteItem.RfqitemId` link, which made the carry-forward a
projection rather than a schema change.

**Nothing new was created except** `Rfqitem` participation state — justified in
`docs/lead-rfq-quote/decision-and-field-contract.md §4`.

## 5. Files changed

**Product:** `Models/Rfqitem.Participation.cs` (new) · `Models/ErpRfqAutomationContext.cs` ·
`Services/QuoteService.cs` · `DTOs/QuoteDTOs/QuoteResponseDTO.cs` ·
`Controllers/QuoteConfigurationController.cs` · `Migrations/ErpRfqAutomationContextModelSnapshot.cs`

**Tests:** `RfqLineParticipationTests.cs` (new) · `QuoteDraftParticipationAndCarryForwardTests.cs`
(new) · `QuoteDraftHandoffTests.cs` · `Module04ProductInventoryMigrationPostgreSqlTests.cs` ·
`DocumentIntakeAllowListTests.cs` · `EmailIngestEnqueuerTests.cs`

**Migration:** `20260806044841_RfqLineParticipationAndQuoteNumberUniqueness`

## 6. Migration safety

- **Additive and reversible.** `Down()` restores the dropped index and removes every column,
  constraint and index added.
- **Backfill is truthful.** Existing RFQ lines default to `Pending`, not `Quote` — backfilling
  `Quote` would retroactively assert a commercial decision no human ever made.
- **Pre-flight data audit** runs *before* the unique index: a `DO $$` block that names the
  offending `(BusinessUnitID, QuoteNo)` duplicates and aborts with an actionable message rather
  than letting Postgres raise an opaque violation. **This has not yet been run against
  production** — see remaining item R-4.
- **Index consolidation:** `IX_RFQItems_RFQID` is dropped because
  `IX_RFQItems_Rfqid_Participation` leads with the same column and serves the same lookups.
- **Cross-provider:** the check constraint uses `trim()`, not `btrim()`. `btrim` is
  Postgres-only and broke the entire SQLite lane — caught and fixed before it could be reported
  as passing.

## 7. Test-infrastructure defect found and fixed

`Module04ProductInventoryMigrationPostgreSqlTests` pins to a historical migration and then seeded
`RFQItems` **through the current EF model** — silently assuming every column the current model
knows about already exists at that point. Any new `Rfqitem` column breaks it with `42703`. Now
inserted with explicit raw SQL naming only the era's columns, exactly as the sibling
`lead_line_commercial_resolutions` insert already did. This was a latent trap for every future
schema change, not just this one.

## 8. Remaining blockers

**Product defects**

| ID | Blocker | Severity |
|---|---|---|
| R-1 | **Ownership dies at conversion.** `Rfq` has **no owner column**; `ConvertCoreAsync` never reads `Lead.AssignTo`. With 44/44 production leads `Unassigned`, "route to a named Sales Engineer" has nothing to write to. | S1 |
| R-2 | **Validation warnings are decorative.** `ResolveLinesAsync` computes `NeedsAttention`/`AttentionReason` (incl. "Quantity missing", UoM `"25 Pack"`); `ConvertCoreAsync` never reads them and the UI leaves **Create RFQ enabled**. | S1 |
| R-3 | **No per-line lineage.** `Rfqitem` has no `LeadItemId`; the mapping is re-guessed afterwards, falling back to *"if the counts match, take the first free row"*. **Blocked by a prerequisite:** `ApplyCurrentProjection` deletes and recreates `LeadItems` on each revision, so an FK would be destroyed. Source-line identity must be made stable first. | S1 |
| R-4 | **No RFQ revision axis.** `Rfq` has no business revision number; `LifecycleVersion` is an optimistic-concurrency counter that `Quote` misreads as one. `RFQ_REVISION_REQUIRED` impacts are written and never consumed. | S2 |
| R-5 | **Quote-number generators not consolidated.** Three remain. The unique index makes collision impossible, but the read-max-plus-one allocator should be replaced by the row-locked `LegalDocumentCounters` pattern finance already uses. | S2 |
| R-6 | Classification never checked — nothing reads `Lead.InquiryType`; a non-RFQ that reaches QUALIFIED converts. | S3 |
| R-7 | Conversion cannot express an unknown closing date — `FindConversionBlockers` hard-requires `BidClosingDate`. | S3 |

**Production prerequisites:** run the duplicate-quote-number audit against production before
applying the migration. **Client data:** the golden corpus remains unmet (A7) — 8 of 9 formats
have no real specimen. **Pilot limitations:** Arabic/Hijri descoped (A1); routing rules not
user-configurable.

## 9. Not yet evidenced

No frontend change, no typecheck/build run, and **no browser journey** in this increment. The
UI work is gated on R-1 and R-2 — putting a "Mark for Quote" control on screen before ownership
survives conversion and warnings actually block would produce a demo that looks complete and
is not. Concurrency tests for double-click and the participation API surface are also
outstanding.

## 10. Git

`git status --porcelain` → 54 entries (42 inherited from the prior session, 12 from this work).
`git diff --stat` on this session's tracked files → **9 files changed, 423 insertions(+),
28 deletions(-)**, plus 5 new untracked files and 1 migration. No commit created — the
inherited working tree was preserved untouched, as instructed.
