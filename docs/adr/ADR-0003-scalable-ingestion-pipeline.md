# ADR-0003 — Scalable ingestion & extraction pipeline (1,000+ docs × 10,000+ line items)

- Status: **Accepted** (design); implementation phased (core = next)
- Date: 2026-07-15
- Deciders: CTO/CIO, Principal Performance & Scalability Engineer, Document Intelligence Architect
- Related: ADR-0001 (Claude provider), ADR-0002 (stack), ADR-0004 (Postgres/deploy)

## Context — a hard, load-defining requirement

Customers will submit **1,000+ documents at once**, each containing **10,000+
bid/RFQ line items**, and the platform must ingest, extract, normalize, and
persist them **strongly, efficiently, and accurately**.

An independent performance review of the current pipeline found it does not merely
run slowly at this load — **it produces incorrect data**:

- **Document merging (CRITICAL):** the manual-upload path concatenates *every*
  uploaded file into one text blob and produces **one** Lead from **one** LLM
  call (`ManualUploadService.cs:76,138,289,212`). 1,000 files → **1 lead**; the
  other 999 RFQs' identities are destroyed.
- **Silent truncation (CRITICAL, = ING-07):** stacked caps (`OllamaLlmService.cs:17`
  ~30k chars wins; `EmailService.cs:44`; `ManualUploadService.cs:37`) mean only
  ~220–270 of 10,000 line items reach the model — **~97–99% silently dropped** —
  yet the lead is saved as "complete."
- **Single synchronous LLM call** cannot emit 10k items in one JSON response
  within the 180s timeout (`OllamaLlmService.cs:110,128,19`).
- **No queue / no worker pool:** extraction runs inline in the mail-poll loop,
  fully sequential (`EmailBackgroundService.cs:29`, `EmailService.cs:86,120`) →
  ~16.7 h per mailbox cycle at 1,000 docs, days with retries.
- **One-transaction bulk import** of 1,000 docs with per-doc `SaveChanges` and
  per-doc dedup scans (`RfqUploaderService.cs:113,145,118`) → log blowup, lock
  escalation, whole-batch rollback on one poison doc.
- **No idempotency / race safety:** read-then-insert dedup (TOCTOU), non-unique
  `IX_Leads_RFQNo`, **no index on `RFQ.Rfqno` at all**, `max()+1` numbering.
- **Everything in memory:** all 1,000 files buffered (up to ~25 GB), O(n²) string
  concatenation and O(n²) Excel shared-string lookups.

## Decision — target architecture

**Principle:** *fast immutable ingest → durable DB-backed job queue →
bounded-concurrency workers → chunked map/reduce extraction → idempotent bulk
persist.* **One document is the unit of work everywhere.** No new infrastructure
beyond the existing database (no Kafka/Temporal) — this delivers outbox, retry,
dead-letter, idempotency, and human-in-the-loop review state with the DB alone.

1. **Decouple ingest from extraction.** The upload endpoint / email poller does
   only cheap reliable work: persist each file immutably (content-hashed),
   enqueue **one `ExtractionJob` per document**, return `202 Accepted` + batch id.
   No parsing/LLM on the request or poll thread.

2. **Durable job table + atomic lease.** `ExtractionJobs(Id, BatchId,
   BusinessUnitId, SourceType, ContentHash, StoragePath, Status, Priority,
   Attempts, MaxAttempts, NextAttemptAt, LeasedBy, LeaseExpiresAt, LastError,
   ResultLeadId, …)`. Unique `(BusinessUnitId, ContentHash)` = idempotency.
   State machine `Pending→Leased→Extracting→Persisting→Succeeded`; failure →
   backoff reschedule; `Attempts≥MaxAttempts` → `DeadLetter`; duplicate hash →
   `Duplicate`. Workers claim with a `READPAST, UPDLOCK` (or `FOR UPDATE SKIP
   LOCKED` on Postgres) `UPDATE … OUTPUT` so N workers take disjoint jobs; an
   expired lease is reclaimable (crash-safe, exactly-once via the hash).

3. **Per-document processing.**
   - **Route by structure:** structured spreadsheets/CSV parse **deterministically**
     via the existing `CanonicalRfqNormalizer` (per-field confidence + evidence +
     NeedsReview already built) — **skip the LLM entirely** (10k rows in ms). This
     is the single biggest cost/throughput lever.
   - **Unstructured docs → chunked map/reduce:** header extracted once; line items
     extracted in bounded chunks (~150–250 items ≈ 20–26k chars); union in order.
     **Nothing is ever truncated** — chunk count scales with the document. Assert
     `Σ chunk items == parsed row count`; mismatch → `NeedsReview`.
   - **Bulk persist, per document, one transaction each:** `SqlBulkCopy`/TVP (or
     `AddRange` + `AutoDetectChangesEnabled=false` + explicit `MaxBatchSize`,
     tracker cleared) → 10k rows sub-second. Guard header insert with the unique
     `(Rfqno, BU)` index and catch duplicate-key (race-proof, not read-then-insert).

4. **Concurrency & backpressure.** A worker `BackgroundService` runs
   `WorkerCount` (start 4–8) claim loops. A process-wide `SemaphoreSlim`
   (start 8) bounds total in-flight LLM calls regardless of worker count. A Polly
   **circuit breaker** trips the pool to backoff when the provider is down (no
   3×180s storms). A poison doc is caught, recorded, rescheduled, and dead-lettered
   — **the batch never fails as a unit.**

5. **Accuracy at scale.** No silent loss (chunking + count-conservation asserts);
   per-field confidence preserved end-to-end; dedup by content hash + unique
   `(Rfqno,BU)` + within-doc line-key; `NoOfLineItems` must equal persisted count.

## Throughput / cost envelope (1,000 × 10k)

| Scenario | LLM calls | Wall-clock | Fidelity |
|---|---|---|---|
| **Current** | 1,000 (truncated, sequential) | ~16.7 h best case, days w/ timeouts | **~2–3% items retained (unusable)** |
| **Redesign, all via LLM** (50 chunks/doc, conc 8→32) | ~50,000 | ~52 h → **~13 h** | 100% |
| **Redesign, structured bypass LLM** (realistic mix) | only unstructured | **minutes–low hours** | 100% |

Levers (highest first): deterministic parse for structured docs → eliminate most
LLM calls; LLM concurrency 8→32; larger chunks / faster model for simple layouts;
bulk-copy persistence; content-hash skip of unchanged docs.

## Implementation sequence

**Core foundation (NOW/next):**
1. Stop merging documents — one file → one job → one lead/RFQ.
2. `ExtractionJobs` table + claim/lease/retry/dead-letter + bounded-concurrency
   worker service; move extraction off the poll loop and request thread.
3. Chunked map/reduce extraction; delete truncation caps; count-conservation asserts.
4. Idempotency + race safety: content-hash unique key; unique `(Rfqno,BU)` on
   Leads & RFQ; catch duplicate-key; replace `max()+1` numbering with a sequence.
5. Bulk, per-document persistence (bulk-copy or `AddRange` no-tracking, per-doc txn).

**Production hardening (next):** LLM provider gate + circuit breaker + Polly;
route structured docs to the deterministic path; streaming/memory fixes (kill
O(n²) Excel `ElementAt` and `+=`); dedup indexes + `AsNoTracking`; review-queue UX
for `DeadLetter`/`NeedsReview`; observability (queue depth, per-job timings, LLM
latency/error-rate, tokens/doc, dead-letter rate).

## Load-test plan (acceptance)

Synthesize a corpus with a **known ground-truth manifest** in three families
(structured xlsx/csv @ 10k rows; semi-structured docx/pdf table; scanned/image
PDF), scaled 1×→10×→100×→**1,000×10k**, plus a 1,000-file single upload, poison
docs, and exact duplicates. **Pass criteria:** 100% line-item fidelity (persisted
count == manifest), **0 duplicate leads/RFQs**, bounded memory (nowhere near the
~25 GB current ceiling), queue fully drained, and a poison/duplicate doc isolated
to dead-letter without rolling back the batch — none of which the current pipeline
achieves.
