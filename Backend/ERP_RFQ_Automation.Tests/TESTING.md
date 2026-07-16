# Nexora backend automated tests

xUnit test project for `ERP_RFQ_Automation`. Run from `Backend/`:

```bash
dotnet test ERP_RFQ_Automation.sln
```

39 tests, all green. The suite targets the platform's highest-risk logic rather than
trivial getters/setters.

## Test infrastructure

- **Relational SQLite in-memory** (`Support/TestDb.cs`) built against the **real**
  scaffolded `ErpRfqAutomationContext` model (all entities, the `platform` schema, the
  computed column, the async-extraction tables). SQLite-in-memory is chosen over EF Core
  `InMemory` because it enforces foreign keys, unique indexes, and — critically — the
  **relational translation of the global query filters**, which is exactly the invariant
  under test. The connection stays open for the fixture lifetime so multiple contexts
  share one database (seed with one context, assert with another).
- `StubTenant` implements the production `ITenantContext`; a null `BusinessUnitId` models
  the no-tenant path (login / anonymous / background worker).
- `Support/Seed.cs` builds FK-satisfying object graphs (BU → EmailConfig → EmailIngest →
  Lead, plus Setup_Master status rows and Customers).
- `Support/ExtractionFakes.cs` — a scripted `ILLMService` stub (one response per chunk,
  records prompts) and builders for the verbose positional records, plus a no-op logger.

## What is covered

### Tenant isolation — the crown jewel (`TenantIsolationTests`, 7)
- A BU1-scoped context returns only BU1 leads; BU2 rows are invisible.
- A BU1 context cannot read a BU2 row even by its exact primary key (returns null).
- A null tenant (background worker) sees **all** business units.
- `IgnoreQueryFilters()` is the deliberate cross-tenant opt-out (sees both).
- Scoping is applied server-side in `COUNT` aggregates (no post-hoc leak); two sibling
  scoped contexts each see only their own rows.
- Master data (nullable `Buid`): a scoped context sees own-tenant **and** shared
  (null-`Buid`) rows but not the other tenant's; a null tenant sees every row.

### Review workbench upsert (`LeadReviewUpsertTests`, 11)
Exercises the real `LeadRepository.SubmitLeadReviewAsync` against SQLite (no EF mocking):
existing item updated in place; new item (null id) inserted; omitted item deleted;
`NoOfLineItems` recomputed across insert+delete; `EmailIngests.ParseStatus` flips
`NeedsReview` → `Success`; `approve` sets `LeadStatusId = 24` while `save` leaves it null;
header fields applied only when non-null; the `[NEEDS REVIEW]` marker is stripped from
`HeaderRemarks` when no remark is supplied; a foreign/stale item id is ignored while
unreferenced real items are removed ("opt-in survival"); a cross-tenant lead is not found
and returns null without mutating it.

### Chunked extraction invariants (`ChunkedExtractionServiceTests`, 12)
Drives `ChunkedExtractionService` with a scripted LLM: item-count conservation (Σ chunk
items == parsed rows) yields `Ok`; a count mismatch or low overall confidence routes to
`NeedsReview`; a partial chunk failure surfaces the failed chunk and routes to
`NeedsReview`; all chunks failing yields `Failed`; chunking splits by the 200-item cap and
by the 24k-char budget (asserted via call count + diagnostics); the no-detected-rows
single-pass branch (Ok / NeedsReview / Failed); and `ExtractAsync` routing — a structured
spreadsheet bypasses the LLM entirely (deterministic normalizer), unstructured goes
through it.

### Content-hash idempotency of the extraction queue (`ExtractionQueueIdempotencyTests`, 6)
Exercises the real EF `EnqueueAsync` SaveChanges path: first enqueue creates a Pending job
and lower-cases the hash; re-enqueuing identical bytes for the same tenant short-circuits
to `Duplicate` (same job id, no second row); the same bytes for a different tenant is a
distinct job (composite `(BusinessUnitId, ContentHash)` key); computed vs. precomputed
hashes dedup consistently; missing content/hash and blank storage path throw.

### Canonical normalizer (`DocumentIntelligence/CanonicalRfqNormalizerTests`, 3)
Pre-existing; left untouched.

## Deliberately skipped seams (would require production changes or a real Postgres)

- **`ExtractionQueue.ClaimAsync` / `RenewLease` / `Complete` / `Fail`** — the atomic
  weighted-fair claim is PostgreSQL-only raw SQL (`FOR UPDATE ... SKIP LOCKED`,
  `POWER(...)`, `INTERVAL`) with `PostgresException` handling. It cannot run on SQLite and
  is not meaningfully unit-testable without a real Postgres. It belongs in an integration
  test with Testcontainers/Postgres. The idempotent `EnqueueAsync` path *is* covered here.
  The `IsUniqueViolation` catch is also Postgres-specific; the tests reach idempotency via
  the pre-check path (the DB unique index remains the production backstop), so they never
  depend on provider-specific exception translation.
- **`ExtractionWorker` end-to-end** (claim → extract → persist → complete/backoff) — an
  `IHostedService` that composes the queue, the reader and the LLM; an integration-test
  target, not a unit target.
- **`ChunkedExtractionService` private helpers** (`BuildChunks`, `ComputeOverallConfidence`)
  are private; they are covered indirectly through the public API (chunk counts asserted
  via `CallCount` + diagnostics, thresholds via outcome status) rather than via reflection.

No production source file, `Program.cs`, or the main `.csproj` was modified to make code
testable. Only the test project and the solution's existing test-project reference were
used.
