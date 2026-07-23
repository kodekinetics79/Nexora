# Nexora backend automated tests

xUnit test project for `ERP_RFQ_Automation`. Run from `Backend/`:

```bash
dotnet test ERP_RFQ_Automation.sln
```

The suite targets the platform's highest-risk logic rather than trivial getters/setters.

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
- **Disposable PostgreSQL 16** (`Support/PostgreSqlTestDatabase.cs`) applies the real EF
  migration chain through Testcontainers. These tests require a running Docker engine and
  carry the `Category=PostgreSQL` trait for an independently selectable CI lane.

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

### Review workbench upsert (`LeadReviewUpsertTests`, 15)
Exercises the real `LeadRepository.SubmitLeadReviewAsync` against SQLite (no EF mocking):
existing item updated in place; new item (null id) inserted; omitted item deleted;
`NoOfLineItems` recomputed across insert+delete; save preserves `NeedsReview` while
approval flips it to `Success`; header fields apply only when non-null; the review marker
survives save and is stripped on approval; a foreign/stale item id fails closed without
deleting real items; and a cross-tenant lead is not found or mutated. Governance cases
prove each write increments the expected version and creates one immutable before/after
audit attributed to the reviewer, stale versions leave data untouched, and approval
rejects invalid commercial values before marking facts verified. Omitted versions fail
closed, and after-images contain the assigned, distinct IDs for newly inserted lines.

### Commercial-review promotion gates (`LeadConversionGovernanceTests`, lifecycle suite)
- Direct intelligence conversion cannot create an RFQ from unverified AI commercial facts.
- The governed lifecycle rejects `UNDER_REVIEW -> QUALIFIED` until those facts are approved.

### AI provider privacy boundary (`AiProviderPrivacySurfaceTests`, 5)
- Provider implementations cannot log raw model output or provider response bodies.
- Health diagnostics cannot disclose key prefixes, provider bodies, or model replies.
- Captured Ollama requests prove trusted policy/task/schema content stays in the system
  role while hostile document text stays inside random matched boundaries in the user role.
- Captured logs and health-controller results prove successful and failed provider bodies,
  endpoint/model metadata, key material, and exception details are not disclosed.
- Provider usage counts, request IDs, duration, and the numeric output-token ceiling are
  verified against captured Ollama HTTP requests and responses.

### AI policy, budget, and accounting (`AiGovernanceServiceTests`, `AiGovernanceLedgerTests`)
- Missing policy, disabled external processing, and exhausted hard budgets deny before a
  provider call; idempotency-key collisions cannot reserve twice.
- Successful calls release reserved capacity, settle exact usage, and persist only hashes
  and character counts rather than source or model content.
- Tenant query filters hide another tenant's policy, request, attempt, and budget rows;
  composite foreign keys reject attributing an attempt to another tenant's request.
- `AnthropicGovernanceTests` proves agent turns use the same reservation/attempt contract
  and retain exact provider token usage and request identifiers.

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

### PostgreSQL production dialect (`PostgreSqlProductionDialectTests`)
- Applies every migration to an empty PostgreSQL database and verifies the latest model
  metadata marker is recorded.
- Runs concurrent `FOR UPDATE SKIP LOCKED` claims and proves workers receive distinct jobs
  while the per-tenant concurrency cap leaves excess work pending.
- Exercises the complete queue state machine with lease ownership fencing: wrong and
  expired workers and stale claim generations cannot renew, advance, fail, or complete
  work; expired jobs are reclaimed, transitions cannot regress, retries honor backoff,
  and crashed final attempts or poison documents reach `DeadLetter` at `MaxAttempts`.
- Proves lead persistence and queue completion roll back together when the fenced
  completion is rejected, preventing a durable lead from being left behind for retry.
- Uses a deliberately hung renewal provider to prove active extraction is canceled by
  the last known lease deadline instead of continuing after ownership can be reclaimed.
- Creates leads concurrently through raw PostgreSQL and proves permanent NXR references
  are server-generated, unique, and immutable.
- Derives expected RLS coverage from EF tenant query filters and verifies every mapped
  tenant table has an enabled read/write policy assigned to `nexora_tenant_app`. It also
  checks every public tenant-column table plus parent-derived commercial, attachment,
  contact, email, and governed-custom-field children.
- Proves the role has no privilege on any unprotected public table or unrelated sequence;
  newly created table/sequence canaries also receive no access through default privileges.
- `IgnoreQueryFilters()` and raw cross-tenant parent/child writes remain blocked by
  PostgreSQL with SQLSTATE `42501`; explicit service transactions work, missing tenant
  state fails closed, and transaction-local role/GUC state does not leak through a reused
  pooled connection.
- The extraction-review audit rejects update/delete with SQLSTATE `55000`, and its
  composite tenant/lead foreign key rejects cross-tenant attribution with SQLSTATE `23503`.
- The AI ledger runs with forced RLS under `nexora_tenant_app`; cross-tenant requests are
  invisible, attempts and request identity fields reject mutation with SQLSTATE `55000`,
  and every newly inserted business unit receives an external-processing-disabled policy.
  A distinct `NOINHERIT`, non-superuser runtime login has no direct ledger access and sees
  one tenant only after transaction-local role/GUC setup.

### Tenant claim boundary (`TenantClaimGuardMiddlewareTests`)
- Authenticated tenant API requests with missing, zero, or malformed `businessUnitId`
  claims fail with 403 before controllers can trust route/query/body tenant identifiers.
- Valid tenant claims, anonymous login, and the separately authorized platform control
  plane continue through their intended paths.

## Deliberately skipped seams

- **`ExtractionWorker` end-to-end** (claim → extract → persist → complete/backoff) — an
  `IHostedService` that composes the queue, the reader and the LLM; an integration-test
  target, not a unit target.
- **`ChunkedExtractionService` private helpers** (`BuildChunks`, `ComputeOverallConfidence`)
  are private; they are covered indirectly through the public API (chunk counts asserted
  via `CallCount` + diagnostics, thresholds via outcome status) rather than via reflection.
