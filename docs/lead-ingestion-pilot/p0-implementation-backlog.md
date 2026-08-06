# P0 Implementation Backlog — Checkpoint

**Written:** 2026-08-06. **Scope discipline:** this backlog is bounded to the single pilot
journey below. Email intake, folder intake, Arabic OCR, advanced routing rules, Quote,
Supplier, Inventory, Orders, Shipment, Delivery and ZATCA are **out of scope** here.

> **The journey.** Manual RFQ Upload → Durable Ingestion Occurrence → Source Stored →
> Parse/OCR → Extract Header and Lines → Human Review → Accept as Lead/RFQ →
> **Assign to Sales Queue** → Ready for Quotation.

---

## 0. Blocker zero — restore the tree before anything else

The working tree is **red**: 1 failing test, 6 test cases silently dropped at discovery.
Both are consequences of the uncommitted `.msg`/`.eml`/`.html` work. Details in
`test-evidence-and-readiness.md` §2–3. Sizing: under an hour. **Do this first**; no P0 below
should be started against a red tree.

---

## 1. The five P0 blockers on this journey

Ordered by where the journey breaks, walking it forward.

### P0-1 — *Assign to Sales Queue* has no reachable outcome (FR-RFQ-07.1)

**The journey's terminal blocker.** 44 of 44 auto-decisions are `NO_MATCH_EVIDENCE` /
`Unassigned`. `sales_rep_profiles` = 0, `customer_ownerships` = 0, `Leads.CustomerID` = 0/51.
Re-verified at this checkpoint: **no controller exposes `UpsertProfileAsync`**, so the gate has
no write path — the table cannot be populated through the product at all. The routing engine,
its 8-factor workload model and its append-only bitemporal audit are all well built and all
unreachable.

The review-queue safety net (FR-RFQ-07.2) genuinely works — 41 Open items, `ReasonCode`
populated, 4 h SLA clocks. Nothing is lost. But a lead that only ever reaches a queue is not
*assigned*, and *Ready for Quotation* is unreachable behind it.

**Done when:** a manually uploaded RFQ routes to a **named** owner with decision code
`PRIMARY_OWNER_ASSIGNED`, shown in a browser against a real backend.

### P0-2 — an amendment still becomes an unrelated new RFQ (FR-RFQ-05, D-05.1 / R-ID-02)

`LeadIdentityApplicationService.cs:165-166` raises a possible match **only when customer
identity is unresolved**. In the healthy case — incoming document and candidate lead both
resolve a customer scope, `LogicalGroupKey` absent (0/47, never populated) — a match scoring up
to **1.00** on byte-identical line items fails the condition, falls through to `:184-197`, and
mints a new `Lead` + `LeadRevision` #1 + `CommercialCaseReference`. No link, no operator
signal. Fires whenever the reference string changes (`RFQ-123` → `RFQ-123 Rev B`) or is absent —
**12 of 47 production leads carry no `RFQNo` at all**.

Compounding, unfixed: matching is capped at the 250 newest leads (~8 days at 900 inquiries/month,
D-05.2), and all 23 Legacy-backfilled leads have `CustomerScopeKey` NULL so the unbounded path
cannot see them (D-05.3). **Untouched in the working tree.**

**Done when:** re-uploading a real corpus document with an altered reference produces a
**revision of the existing lead**, not a new one — regression-tested at both scope states.

### P0-3 — Parse/OCR is unproven in the deployed container (FR-RFQ-02)

No `TesseractEngine` has ever been constructed in production. If the managed Tesseract 5.2.0
wrapper does not bind Debian's `libtesseract` in the `aspnet:8.0` image, **every image and
scanned PDF returns empty text with `OcrStatus='Failed'`** and looks like a bad document rather
than a broken container. `OcrEngineStartupProbe` exists in the working tree, untested and
undeployed; one deploy settles it.

Second, narrower defect: `NearEmptyThreshold = 20` (`ProductionDocumentReader.cs:51`) is an
**absolute** character count, not per-page. A 40-page scanned tender carrying a 25-character
digital-signature stamp clears it and returns "native" with 25 characters of text, asserting
`OcrStatus='NotRequired'` — silent content loss.

**Done when:** probe output from the deployed container names the Tesseract version and the
`eng` load, and a scanned specimen takes `processing_path='LocalOcr'`.

### P0-4 — source evidence is not governed, not audited, and 45 of 86 documents have been unreachable (FR-RFQ-08)

Four defects on one requirement, all on this journey's *Source Stored* step:

- **Governed storage MISSING** — `object_bucket='local'` on 86/86; production runs on a single
  5 GB Render disk with no versioning, object-lock, SSE or lifecycle policy. `/ready` is
  `Unhealthy` and the `evidence-storage` check fails **by name**. The `S3EvidenceObjectStorage`
  implementation and its `Program.cs:121-127` branch already exist — this is configuration plus
  a backfill of 86 objects.
- **Access-audit history MISSING** — nothing records who retrieved a source document, when, or
  from which tenant. `FileController.cs:60-152` writes nothing on the success path. The audit
  infrastructure already exists and is used for evidence *mutation*; reads are simply not
  covered. **This single change moves FR-RFQ-08 closest to complete.**
- **Scan status DEFECTIVE** — `DocumentInspection__Scanner__Provider = BuiltIn` selects the
  `Nexora.EICAR` stub. **30 real customer documents are shown green-labelled "Cleared" by a
  matcher that only detects a 68-byte test string**; 54 remain Quarantined. This is a
  truthfulness defect before it is a security one.
- **45 of 86 documents unreachable at least once**, while the ledger still reports
  `purge_state='Present'`; 20 job paths sit on wiped ephemeral storage.

### P0-5 — the four BRD fields the extractor captures nowhere (FR-RFQ-04)

Present in the real client documents, absent end to end:

| Field | Prompt | DTO | Column | Production rows |
|---|---|---|---|---|
| Delivery location | ✗ | ✗ | ✗ | 0 — `Ship To` is in 14/14 real documents and lands in `ExtraFields` on 6 rows before being discarded |
| Saudi region / city | ✗ | ✗ | ✗ | 0 |
| Required delivery date | ✗ | partial (`LeadTime`) | wrong type | **0 of 3,121** |
| Closing **time of day** | ✗ (format-blocked) | ✗ | column exists | **0 of 46** — every close is midnight |

Closing time is the sharp one: a bid at 14:05 against a 14:00 close is rejected, and the
product currently implies "any time that day". Rule 4 of the prompt pins every date to
`YYYY-MM-DD`; the column is already `timestamp` and can hold a time.

Also P0 on the same requirement: `HeaderRemarks` is a *"very brief (1-2 sentences) summary"* of
contractually binding terms (real blocks exceed 2,000 characters with ten numbered clauses), and
it doubles as the `[NEEDS REVIEW]` diagnostics channel — pipeline messages concatenated into the
buyer's own words.

---

## 2. The next smallest vertical slice — one only

> ### Give `sales_rep_profiles` a write path and route one real lead to a named owner.

**Why this one.** It is the only P0 that unblocks the journey's terminal step. P0-2 through P0-5
degrade quality *along* a journey that already runs; P0-1 is where the journey **stops**. It is
also the cheapest of the five: the routing engine, the scoring model, the audit trail and the
queue UI are all built and correct — the single missing piece is that nothing can create the
row the gate reads.

**The slice, exactly:**

1. Expose the existing `UpsertProfileAsync` on a controller, behind the module permission the
   sibling admin routes already use. No new engine, no rules table, no new entity.
2. Seed the pilot tenant: its sales reps, `customer_identifiers` for the pilot customers, and
   `customer_ownerships`.
3. Re-drive routing for existing leads through the already-registered
   `RoutingReconciliationWorker`.

**Explicitly not in this slice:** routing rules configurability (FR-RFQ-07.3, P2 — do not build
a rules engine), territory / manufacturer / Saudi-region dimensions (P2–P3), surfacing the
`Explanation` blob in the UI (P1, a separate slice), and the reason-less override bypass at
`UnAssignedLeadController` (P1, separate).

**Acceptance:** one manually uploaded RFQ, walked in a browser against a real backend, arriving
at a **named** sales engineer with decision code `PRIMARY_OWNER_ASSIGNED` and a
`lead_assignments` row — plus a PostgreSQL-lane test asserting the assigned outcome, which no
test asserts today.

**Prerequisite:** blocker zero above. The tree must be `Failed: 0` first.
