# Gate 1 — Progress Log

**Scope discipline:** Gate 1 only (RFQ ingestion, FR-RFQ-01..08). No later gate was started.
Two items below sit just outside FR-RFQ but were pulled in deliberately because they are
ingestion-correctness defects on the same code path; each is marked and justified.

---

## Session 1 — 2026-08-08 → 09

### What changed

| # | Requirement | Change | Tests |
|---|---|---|---|
| 1 | **FR-RFQ-06** | The duplicate rule now runs **before** a record is created | 2 new |
| 2 | **FR-RFQ-04** | One canonical closing-date parser across every ingestion door | 41 new |
| 3 | **FR-RFQ-02/04** | Spreadsheets: unit-of-measure column is read; header row is found rather than assumed | 19 new |
| 4 | **FR-RFQ-08** | Attachment enqueue failures now reach the durable skip ledger | covered by existing |
| 5 | *(ingestion correctness)* | Four filters that silently discarded lines with no stated quantity removed | covered by existing |
| 6 | **FR-RFQ-04** | A genuine Excel date cell is no longer reported as an unsupported format | 5 new (end-to-end) |

**Test position:** 2,897 portable + 483 PostgreSQL, all passing, both lanes run against the final
state of the change set. 67 tests added this session. Solution builds with 0 errors and the same
8 pre-existing package-compatibility warnings as the baseline — none new.
`ExtractionWorkerLeaseTests.HungHeartbeatCancelsWorkByKnownLeaseDeadline` flaked once under
parallel load (10s) and passes in isolation (1s); a clean re-run confirmed it as a timing flake,
not a regression — no change this session touches leases.

---

### 1 · FR-RFQ-06 — duplicates are held before the record exists

**What the requirement says:** flag possible duplicates on same buyer, same item and overlapping
dates *for human review before a new record is created*.

**What was actually true** — and this corrects the audit, which was too harsh. There were not two
live engines corrupting data. `LeadDuplicateDetector` (post-persistence, stamps a flag on the
newer lead) is guarded by `_leadIdentity is null`, and `ILeadIdentityApplicationService` **is**
registered, so that path is inert in production. The real gap was narrower and still real: the
authoritative pre-persistence engine keyed on content similarity and **never implemented the
buyer + item + overlapping-dates rule at all**.

**Change.** `Deduplication/DuplicateRules.cs` holds the rule as pure predicates over two leads —
no database, no I/O, nothing stamped — so it can be evaluated while the incoming lead is still an
unsaved object. `LeadIdentityApplicationService` consults it after the revision arms and before
the "new inquiry" fall-through; a hit raises the existing review occurrence and match candidate,
so no row is written. The rule text is carried over from the old detector unchanged, so detection
behaviour is preserved; only the moment it runs and the queue it feeds have moved.

Two deliberate constraints: the revision arms stay gated on content similarity alone, so a
low-similarity duplicate can never auto-link as a revision; and a candidate whose own RFQ
reference contradicts the incoming one is excluded, because the buyer has told us they are
different inquiries.

The guarded legacy fallback was left in place rather than ripped out — it is inert, and removing
it would have disturbed the tests that exercise the no-identity path for no benefit.

### 2 · FR-RFQ-04 — one closing-date parser

**Three real defects, in the one field where being wrong loses a bid.**

*Divergence.* Five ingestion doors each carried a private date parser and they disagreed. The
watched folder accepted `15 Mar 2026`; email and manual upload returned null for it. Manual
upload rejected `yyyy/MM/dd`, which the others accepted. Email and the folder applied no
sentinel-year guard at all, so an extracted `0001-01-01` reached the database as a real closing
date. **The same tender produced a different deadline, or none, depending on which door it came
through.**

*Times were lost entirely.* No accepted format carried a time, and `TryParseExact` returns null
rather than degrading — so a tender closing `2026-09-01 14:00` yielded **no deadline at all**,
not a truncated one.

*Ordering.* The watched-folder parser listed `M/d/yyyy` ahead of `d/M/yyyy` and therefore read
`5/3/2026` as 3 May while every other door read it as 5 March.

*A sixth parser, found later.* `CanonicalRfqNormalizer.DateValue` carried its own private format
list — on the spreadsheet path, which is the only path that ingests without the AI gateway. It
omitted ISO 8601 with a `T`, and the legacy `.xls` reader renders a genuine Excel date cell in
exactly that round-trip form. **A real date cell in a real workbook was therefore reported as an
unsupported format and came out `Invalid`.** It now delegates to the shared parser. The `.Date`
truncation there is deliberately retained: capturing a stated closing time also requires deciding
which time zone it is in, which is an open product decision.

**Change.** `Extraction/RfqDateParser.cs` is now the only place an RFQ date is parsed; all five
doors delegate to it. Every format any door previously accepted still parses and day-first
ordering is preserved, so nothing that worked has changed. Added: times of day (24-hour, 12-hour
and ISO 8601), a degrade-to-date path so an unreadable time never costs the whole deadline,
spelled month names for every door, dates embedded in prose, Arabic-Indic and Eastern
Arabic-Indic digits, and a uniform plausible-year guard. Format ordering is now explicitly
year-first, then every day-first form, then month-first as a fallback that can only win when a
day-first reading is impossible.

The parser also reports `HasExplicitTime` and `IsDayMonthAmbiguous`. **Nothing consumes those
yet** — see open items.

### 3 · FR-RFQ-02/04 — the spreadsheet path

Spreadsheets are the only ingestion path that works without the AI gateway, which makes anything
this parser drops a live customer-facing loss today.

*The unit column did not exist.* `RfqSpreadsheetRow` had no unit member, the alias map had no
unit entry, and the canonical mapper hardcoded `UnitOfMeasure: null`. **Every spreadsheet RFQ lost
its unit column outright** — a line reading "500 M" of cable was ingested as a bare 500 and would
be quoted as 500 each. The field is now read end to end and is never defaulted.

*The header was assumed to be row 1.* `ParseXlsx` took the first row of the used range and
`ParseXls` hardcoded row 1. Any workbook opening with a logo, title block or covering note mapped
zero columns, which made every subsequent row immaterial and **discarded the entire RFQ with no
diagnostic**. `LocateHeader` now scans a bounded 25-row window and takes the row recognising the
most fields, requiring at least two so a stray title cell cannot win. When nothing looks like a
header the first row is returned, which is the previous behaviour, so unrecognisable sheets still
fall through to the unstructured path exactly as before. The `.xls` reader is forward-only, so
only the scan window is buffered; the rest of the sheet still streams.

*Header spellings.* Matching now normalises punctuation and spacing, so `Qty.`, `U/M`, `Part No.`
and `Unit of Measure` land correctly. Aliases were widened to the spellings this industry
actually uses (`Material Code`, `Material Description`, `Req Qty`, `Make`, `Brand`). Matching
stays exact after normalisation — substring matching would let a `Total Price` column capture
`Price`, and there is a test pinning that.

**Five existing tests had to change, and this is worth reading.** The fixture named
`unrecognized-layout-rfq.xls` is a title row above `S.No | Material Code | Material Description |
UOM | Req Qty | Delivery Location`. That is an ordinary, readable RFQ. It was classed
unrecognisable only because of the two defects above, and was therefore handed to an LLM that is
blocked in the deployed configuration — so it dead-lettered. It now parses deterministically, and
the tests assert that. Fallback coverage was **not** weakened: the fail-closed, zero-egress and
never-dead-lettered assertions are all retained, repointed at a synthetic workbook whose headers
carry no commercial meaning so the fallback path is genuinely exercised.

### 4 · FR-RFQ-08 — the unrecorded skip

`EmailIngestEnqueuer` recorded every attachment skip reason to the durable ledger except one: the
catch around enqueue failure logged and moved on. A transient storage failure on the single
attachment carrying the BoQ left a queued ingest, a body-only lead, and a log line nobody reads.
It now records to the same ledger as every other skip. The irony is that the comment immediately
below that catch documents an identical loss found earlier.

### 5 · Ingestion correctness — the armed filter

*Outside FR-RFQ, included deliberately: same code path, same failure mode.*

Four sites filtered extracted items on `Quantity > 0`. The extractor is explicitly instructed to
return null when a document states no quantity, and `null > 0` is false, so those lines vanished —
and the line count was taken from the filtered list, making the loss self-consistent and
invisible. Currently unreachable because the unified queue is on, but one configuration flip
re-arms it. Removed. A line a reviewer can see and correct is always better than a line that
never existed.

---

---

## Session 2 — 2026-08-09

### The blocker, resolved without needing the AI decision

The 120-document sample set is Word files, and Word files went to the language model — which is
refused in this deployment, so **all 120 would have dead-lettered**. Every one of them states its
lines in a table: `Item | Description | Qty | Notes`, under a header block carrying RFQ number,
customer and date.

A table is structured data. `DocxTableParser` now reads it directly, reusing the same column
aliases and header-location rule as the spreadsheet path, so one set of buyer spellings serves
both formats. **All 120 documents and 641 line items now read deterministically — no model, no
egress, no 60-second risk.** The `.docx` path falls back to the previous text behaviour byte for
byte when no table maps, so a prose RFQ is unaffected.

The header block is read separately, because in a workbook those values are columns and in Word
they are paragraphs — and these documents concatenate them with no separator at all
(`RFQ Number: RFQ-260011Customer: Omega OilRFQ Date: 2026-05-26`), so each value has to run to
the next recognised label.

### Three defects this work exposed, two of them mine

**A silently false lead time, introduced by me and caught in review.** Mapping "Requested
Delivery: 9 weeks" to a lead-time field meant an integer parse failure, which invalidated every
line of every document — and, because the failed parse still left the value at its default,
wrote `LeadTime = 0` onto all 641 items. Zero reads as *deliver immediately*. Fixed three ways:
the label is now recognised only as a boundary and stored nowhere; an **optional** field that
cannot be parsed yields NeedsReview rather than Invalid, because no best-effort field should be
able to condemn a document; and numeric values are emitted only when actually parsed, never from
a struct default.

**"Item" is ambiguous.** Beside a description column it is the buyer's item code; alone it is the
description. It was in the ProductName aliases, so every SKU was read as a product name. The rule
is now explicit and decided from the columns actually present.

**The buyer's note was read by no format at all.** "OEM only", "Equivalent accepted" — these
change what may legitimately be quoted. Now captured to `ItemText` across every format.

Also fixed: line numbers started at 2, because the header occupies a real row and numbering used
the physical row rather than the line ordinal.

### Deployment

`deploy/single-box/` holds a compose file, `.env.example` and an install runbook. It is the demo
machine and the client machine — one procedure, because demoing one topology while shipping
another proves nothing.

The one trap worth repeating: Nexora treats a provider as Local **only** at a loopback address.
A normal Compose service name (`http://ollama:11434`) is not loopback, so the obvious setup
silently reproduces the exact refusal this deployment exists to escape. The backend therefore
uses host networking and Ollama runs natively on the host — which is also far easier for GPU
access. This is documented at the top of the compose file so nobody "tidies" it back into a
broken state.

**Test position after session 2:** 2,906 portable tests passing, including a sweep over all 120
real documents.

---

## Open items — Gate 1 not yet closed

Ranked by customer impact.

1. **External AI is blocked in the deployed configuration.** `Ollama:BaseUrl` resolves to an
   external provider, and every business unit is seeded `ExternalProcessingAllowed = FALSE` with a
   trigger applying the same default to new tenants. **Consequence: every PDF, scan, DOCX and
   email-body RFQ dead-letters.** After this session's fixes the working set is spreadsheets —
   now including title-block layouts, and now carrying units — but that is the whole of it. The
   failure is at least honest (dead-letter, not a thin lead). This needs a decision from you:
   authorise a provider, or point at a loopback model. Either way `/ready` should fail when no
   provider is authorised, so this can never again be true silently.
2. **Quantity persists as `0` when the document stated none.** `LeadItem.Quantity` is a
   non-nullable `int` and the mapper does `?? 0`. A reviewer cannot distinguish "document said
   nothing" from "document said zero". `QuantityParser` — which was written precisely to refuse
   guessing, and handles `1,000`, `1 000`, thousands-vs-decimal ambiguity and non-positive
   refusal — exists and is wired into exactly one uploader, not the live pipeline. Fixing
   properly needs a migration to `int?`, so it is queued rather than rushed.
3. **The ambiguity and time flags are computed and ignored.** `RfqDateParser` reports
   `IsDayMonthAmbiguous` and `HasExplicitTime`; nothing reads either. `03/04/2026` is stored as
   3 April with no marker. Per the standing rule that uncertain values must be flagged rather
   than guessed, these should drive a review flag — but *what* a reviewer is shown is a product
   decision, so it is listed rather than invented.
4. **The unit refusal is discarded.** `UomCanonicalizer` returns a `NeedsReview` verdict and the
   mapper calls the storage-only overload, so "Pallet" or "Length" persists looking like an
   ordinary unit with no flag.
5. **Four FR-RFQ-04 fields still have no column:** required delivery date, Saudi region/city
   delivery location, closing time, Hijri closing date. Note the fixture above literally contains
   a `Delivery Location` column we now read into the row and then drop.
6. **Scanned PDFs stop at 10 pages.** Flagged, not silent — but items 40 to 200 of a 30-page
   tender do not exist.
7. **Expected-versus-extracted item count is not persisted.** A failed chunk drops its items and
   the lead records the count that survived, so a reviewer cannot tell that 6 of 174 arrived.
8. Remaining FR-RFQ gaps from the audit: identifier format (E1), no agreement/contract reference
   (E2), routing rules have no master-data screen, evidence hashing on `Attachment`.

## Decisions I made rather than block on

- **Hijri date parsing stays in Gate 1 scope** even though Arabic and RTL are deferred. A Hijri
  closing datetime is data correctness, not language; Etimad publishes them, and getting one
  wrong loses a bid regardless of what language the screen is in. Not yet built.
- **The legacy post-persistence duplicate detector stays**, guarded and inert, rather than being
  removed. Removing it buys nothing and disturbs tests that exercise the no-identity path.
- **Widened header recognition was kept even though it broke five tests**, because those tests
  encoded a behaviour that is actively harmful in the current deployment: sending readable
  spreadsheets to a disabled LLM. The tests were updated to the better contract, and every
  safety assertion in them was preserved.
