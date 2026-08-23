# Deduplication + lead-ownership wiring (WP-A1 / WP-A3)

## Current state (updated — the post-persistence detector is GONE)

The original WP-A3 deliverable, `LeadDuplicateDetector` (a post-persistence
detector that committed the lead and then stamped `DuplicateStatus = "suspected"`
on it), has been **deleted**. It was dead in production: both of its call sites —
`LeadPersister` in `Extraction/ExtractionWorker.cs` and `LeadRepository` (where
the injected field was never even read) — were gated behind
`if (_leadIdentity is null)`, and `ILeadIdentityApplicationService` is always
registered in `Program.cs`, so the gate never opened. Its DI registration, the
constructor injections and the dead call sites were removed with it.

What is LIVE:

- **`DuplicateRules`** (this folder) — the duplicate rule as pure predicates,
  evaluated **pre-persistence** by
  `LeadIdentity/LeadIdentityApplicationService.cs` against already-loaded tenant
  candidates while the incoming lead is still an unsaved in-memory object, per
  FR-RFQ-06 ("flag for human review before a new record is created"). The rule
  text is preserved verbatim from the deleted detector; only the moment it runs
  and the queue it feeds moved.
- The `Lead` duplicate-review columns (`DuplicateStatus`, `DuplicateOfLeadId`,
  `DuplicateResolvedBy` — `Models/Lead.Duplicate.cs`) and the resolution
  endpoint `POST /api/Lead/{id}/duplicate-resolution` remain in use by the
  review flow and the list/detail projections.
- The duplicate/revision verdict for every arrival is durably recorded on
  `LeadIngestionOccurrence.Classification` (LeadIdentity/) and surfaced on the
  canonical intake record (`GET api/intake-records/...`, section 9).

## The rule (unchanged wording, now in `DuplicateRules`)

A pair is a possible duplicate when:

- (a) normalized `Rfqno` matches exactly (casefold, strip non-alphanumerics,
  only when both are non-null), OR
- (b) same customer key (`lower(Clientemail)`, falling back to normalized
  `BuyersName` when the email is null **or an internal pipeline placeholder**
  like `extraction@pipeline.local`) AND |BidClosingDate delta| ≤ 2 days (or both
  null) AND item overlap ≥ 0.6, where overlap = |intersection| / min(|A|,|B|)
  over normalized `ManufacturerPartNumber` ∪ `ItemMaterialCode` ∪
  `CustomerRfqno`.

Rejected leads (`LeadStatusId == 25`) are never duplicate targets.

## Assignment endpoints (WP-A1, for reference)

- `POST /api/UnAssignedLead/assign` returns **403** unless the caller's role
  resolves to a manager/admin rank.
- `GET /api/UnAssignedLead` / `/assigned` rows include `unassignedHours`,
  `isUnassignedOverdue` (threshold via `ISlaPolicyReader`), `duplicateStatus`,
  `duplicateOfLeadId`.
